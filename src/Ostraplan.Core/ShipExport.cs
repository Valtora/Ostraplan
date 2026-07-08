using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ostraplan.Core;

/// <summary>What the user chose in the export dialog. <see cref="DestinationParent"/> is
/// the folder the mod folder is created <b>inside</b> (a user-picked directory or the game's
/// <c>Ostranauts_Data/Mods</c>); the mod folder itself is named after the ship.</summary>
public sealed record ExportOptions(
    string ShipName, string Author, string Notes, string ModVersion, string GameVersion,
    string DestinationParent);

/// <summary>The outcome of a successful export: where it landed and what it contains.</summary>
public sealed record ExportResult(
    string ModDir, string ShipJsonPath, string ModInfoPath,
    int PartCount, int RoomCount, ShipRating Rating, IReadOnlyList<string> Warnings);

/// <summary>
/// Exports the current design as a spawnable local data mod: a <c>data/ships/&lt;Name&gt;.json</c>
/// in the game's own JsonShip shape plus a <c>mod_info.json</c>. The hard part is the reverse of
/// the loader's coordinate/rotation mapping (<see cref="ShipGrid"/>): a document part's 0-based
/// grid top-left + Ostraplan rotation become the game's centre <c>(fX,fY)</c> + CCW
/// <c>fRotation</c>, with the export grid anchored at <c>vShipPos = (0,0)</c> so the two extra
/// offset terms vanish. <c>aRooms</c>/<c>aRating</c> are precomputed with the same P2 engine the
/// game recomputes on full load, so the broker/registry rating shown on shallow load already
/// matches — verified by the round-trip test.
/// <para><b>Never writes <c>loading_order.json</c></b>: registration stays single-owner with
/// ModTools/Ostrasort (the caller's dialog says so). Staging into the game Mods folder is the only
/// write into the install and only on the user's explicit choice.</para>
/// </summary>
public static class ShipExport
{
    /// <summary>Metres per tile (16&#160;px); the game's dimensions string uses this (10×12 → "3.20m x 3.84m").</summary>
    private const double MetresPerTile = 0.32;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The per-instance override every contained item carries so the game keeps it on a template spawn
    /// (see <c>EmitCargo</c>). A single StatDamage=0 (Amount 0 = undamaged) — a benign "pristine" instruction that is
    /// also a non-null array, which is the exact condition <c>Ship.SpawnItems</c> tests to retain a parented item.
    /// Immutable and shared: the exporter only ever reads it.</summary>
    private static readonly ExportedCondOverride[] PristineMarker =
        [new() { CondName = "StatDamage", Chance = 1.0, Amount = 0.0, NegativeValue = false }];

    /// <summary>
    /// Run the P2 engine and assemble the JsonShip-shaped export object for the design.
    /// Pure and testable — no file I/O. <paramref name="warnings"/> collects anything the
    /// export dropped (currently: nothing — every placed part resolves — but reserved for
    /// unresolved defs so the caller can always surface a report).
    /// </summary>
    public static (ExportedShip Ship, ShipRating Rating, int RoomCount) Build(
        ShipDocument doc, Catalog catalog, IReadOnlyList<RoomSpecDef> specs, string shipName, List<string>? warnings = null)
    {
        var grid = ShipGrid.FromDocument(doc, catalog);
        var partition = RoomBuilder.Build(grid);
        RoomCertifier.CertifyAll(partition, specs, catalog);
        var rating = Rating.Calculate(grid, partition, catalog);

        // map a grid part back to its source placement (PlacedPart.StrID == Placement.Id) so its contained cargo
        // travels into the export
        var byPlacementId = doc.Placements.ToDictionary(p => p.Id.ToString());

        var items = new List<ExportedItem>(grid.Parts.Count);
        var cos = new List<ExportedCondOwnerSave>();

        // Emit a container's contents the way a SAVE stores them, because a data/ships file spawns as a TEMPLATE
        // (bTemplateOnly) and a template can't otherwise keep authored cargo. Ship.SpawnItems (decompiled):
        //   * drops any parented item unless it has aCondOverrides (→ the item's root container is flagged, which
        //     also clears bLoot so the container isn't refilled from its DEFAULT loot) OR bForceLoad (→ the item
        //     keeps its strID instead of getting a fresh one);
        //   * reconstructs a STACK only from the stack-head CO's aStack (a list of member strIDs) in
        //     CondOwner.PostGameLoad — which needs the head's baked CO and the members to keep their strIDs.
        // So each contained item carries BOTH bForceLoad (keep strID) AND the aCondOverrides "pristine" marker
        // (survive + suppress the container's default loot), plus a baked aCOs entry; a stack head's CO lists its
        // members in aStack so the game rebuilds the ×N stack at the right count (a bare lead+members chain alone
        // orphaned the members and collapsed the stack). The marker is a StatDamage=0 override (Amount 0 =
        // undamaged): real, benign, and a non-null array (the exact gate SpawnItems tests). Recurses so nested
        // containers and stacks come through; loose cargo parents by strParentID, equipped gear by strSlotParentID.
        // Returns the emitted item's fresh strID so a parent stack head can collect its members.
        string EmitContained(CargoItem c, string parentStrID, double fx, double fy)
        {
            var cid = Guid.NewGuid().ToString();
            var item = new ExportedItem
            {
                StrName = c.DefName, FX = fx, FY = fy, FRotation = c.GridRot, StrID = cid,
                ACondOverrides = PristineMarker, BForceLoad = true,
            };
            if (c.Slotted) item.StrSlotParentID = parentStrID; else item.StrParentID = parentStrID;
            items.Add(item);

            var childIds = c.Children.Select(child => EmitContained(child, cid, fx, fy)).ToList();

            cos.Add(new ExportedCondOwnerSave
            {
                StrID = cid,
                StrCODef = c.DefName,
                StrCondID = c.DefName + cid,
                InventoryX = c.GridX,
                InventoryY = c.GridY,
                // A stack head lists its members; a real (drillable) container does not — its children are separate
                // items positioned by their own inventory cells, not stack members of the container.
                AStack = c.IsStack && childIds.Count > 0 ? childIds.ToArray() : null,
            });
            return cid;
        }

        void EmitCargo(IReadOnlyList<CargoItem> nodes, string parentStrID, double fx, double fy)
        {
            foreach (var c in nodes) EmitContained(c, parentStrID, fx, fy);
        }

        foreach (var part in grid.Parts)
        {
            var (w, h) = GridMath.Size(part.Part.Item.Width, part.Part.Item.Height, part.Rot);
            // inverse of ShipGrid.FromTemplate with vShipPos=(0,0): centre = top-left + (size/2 − 0.5),
            // y flips (grid is y-down, the game is y-up), and fRotation is CCW = Norm(−Rot).
            var fx = part.TopLeftCol + (w / 2.0 - 0.5);
            var fy = -(part.TopLeftRow + (h / 2.0 - 0.5));
            var strID = Guid.NewGuid().ToString();
            items.Add(new ExportedItem
            {
                StrName = part.Part.DefName,
                FX = fx,
                FY = fy,
                FRotation = GridMath.Norm(-part.Rot),
                StrID = strID,
            });

            var placement = part.StrID is { } pid ? byPlacementId.GetValueOrDefault(pid) : null;
            if (placement is { Cargo.Count: > 0 })
            {
                EmitCargo(placement.Cargo, strID, fx, fy);   // the design's contents (original + authored), pristine
            }
            else if (NavConsole.IsConsole(part.Part))
            {
                // An EMPTY nav console is a bare frame: its interface is assembled from hot-swappable module items
                // contained inside it. Ostraplan places only the console, so install the standard module set here or
                // it spawns blank. Each module is baked the same way as EmitContained's cargo (bForceLoad + marker +
                // a CO): a nav console has no default module loot, so without that the modules would be dropped on a
                // template spawn and the console would come back empty (see EmitContained, NavConsole, Babak.json).
                // A console that already carries modules (a save-imported one) keeps them via EmitCargo above.
                foreach (var modDef in NavConsole.StandardModules)
                {
                    var modId = Guid.NewGuid().ToString();
                    items.Add(new ExportedItem
                    {
                        StrName = modDef, FX = fx, FY = fy, FRotation = 0, StrID = modId,
                        StrParentID = strID, ACondOverrides = PristineMarker, BForceLoad = true,
                    });
                    cos.Add(new ExportedCondOwnerSave { StrID = modId, StrCODef = modDef, StrCondID = modDef + modId });
                }
            }
        }

        // The game's roomValue is the room's PARTS value (Room.CalculateRoomValue = Σ GetBasePrice × modifier),
        // which GetShipValue sums on a shallow load. Bake that, not the physical volume — a volume figure (~0.256
        // per tile) made a spawned design read as near-worthless at a broker until the game recomputed on full load.
        var valueModifiers = specs.ToDictionary(s => s.Name, s => s.ValueModifier, StringComparer.Ordinal);
        var rooms = partition.Rooms.Select(r => new ExportedRoom
        {
            StrID = Guid.NewGuid().ToString(),
            BVoid = r.Void,
            ATiles = r.Tiles.ToArray(),
            RoomSpec = r.RoomSpec,
            RoomValue = ShipValue.RoomValueOf(r, valueModifiers),
        }).ToArray();

        var roomCount = partition.Rooms.Count(r => r.RoomSpec is not ("" or "Blank"));

        var regId = GenerateRegID();
        var zones = BuildZones(doc, grid, regId);

        var ship = new ExportedShip
        {
            StrName = shipName,
            StrRegID = regId,
            NCols = grid.NCols,
            NRows = grid.NRows,
            VShipPos = new ExportedVec2(),   // (0,0): the anchor the coordinate inverse assumes
            AItems = items.ToArray(),
            ACOs = cos.Count > 0 ? cos.ToArray() : null,   // save-style CO data for authored cargo; omitted when none
            ARooms = rooms,
            AZones = zones,
            ARating = [rating.Epoch.Length == 0 ? "0" : rating.Epoch,
                rating.Condition, rating.RoomCount, rating.Maneuver, rating.Size, rating.Slot5],
            Dimensions = $"{grid.NCols * MetresPerTile:0.00}m x {grid.NRows * MetresPerTile:0.00}m",
            ShipCO = ExportedShipCO.Pristine(),
        };

        return (ship, rating, roomCount);
    }

    /// <summary>Serialize the ship as the game expects a <c>data/ships</c> file: a one-element top-level array.</summary>
    public static string Serialize(ExportedShip ship) => JsonSerializer.Serialize(new[] { ship }, Json);

    /// <summary>Serialize the mod metadata as the game expects <c>mod_info.json</c>: a one-element top-level
    /// array, the same shape as every core data file. A bare object parses to an empty collection, so the
    /// loader (<c>DataHandler.JsonToData</c>) falls back to a default name and logs a spurious
    /// "Missing mod_info.json" warning plus an "Error loading file" for the mod.</summary>
    public static string SerializeModInfo(ModInfo modInfo) => JsonSerializer.Serialize(new[] { modInfo }, Json);

    /// <summary>
    /// Build the design and write a complete mod folder (<c>mod_info.json</c> + <c>data/ships/&lt;Name&gt;.json</c>)
    /// under <see cref="ExportOptions.DestinationParent"/>. Overwrites an existing same-named mod folder's two
    /// files (never deletes anything else). Returns where it landed; throws on I/O failure for the caller to report.
    /// </summary>
    public static ExportResult Write(ShipDocument doc, Catalog catalog, IReadOnlyList<RoomSpecDef> specs, ExportOptions opts)
    {
        var warnings = new List<string>();
        var (ship, rating, roomCount) = Build(doc, catalog, specs, opts.ShipName, warnings);

        var folderName = SanitizeName(opts.ShipName);
        var modDir = Path.Combine(opts.DestinationParent, folderName);
        var shipsDir = Path.Combine(modDir, "data", "ships");
        Directory.CreateDirectory(shipsDir);

        var shipPath = Path.Combine(shipsDir, folderName + ".json");
        File.WriteAllText(shipPath, Serialize(ship));

        var modInfoPath = Path.Combine(modDir, "mod_info.json");
        var modInfo = new ModInfo
        {
            StrName = opts.ShipName,
            StrAuthor = opts.Author,
            StrGameVersion = opts.GameVersion,
            StrModVersion = string.IsNullOrWhiteSpace(opts.ModVersion) ? "1.0.0" : opts.ModVersion,
            StrNotes = string.IsNullOrWhiteSpace(opts.Notes)
                ? $"\"{opts.ShipName}\", a ship design exported from Ostraplan."
                : opts.Notes,
        };
        File.WriteAllText(modInfoPath, SerializeModInfo(modInfo));

        return new ExportResult(modDir, shipPath, modInfoPath, ship.AItems.Length, roomCount, rating, warnings);
    }

    /// <summary>
    /// Project the document's zones into the export grid frame (which the parts used: <c>vShipPos=(0,0)</c>,
    /// origin = the grid's document-coord origin). Only in-range flat indices are emitted — one out-of-range
    /// index would make the game drop that zone and every zone after it — so a zone whose tiles all fall outside
    /// the exported hull is skipped (it would be inert). Names are made unique (<c>mapZones</c> is name-keyed).
    /// </summary>
    private static ExportedZone[] BuildZones(ShipDocument doc, ShipGrid grid, string regId)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ExportedZone>(doc.Zones.Count);
        foreach (var z in doc.Zones)
        {
            var tiles = new List<int>(z.Tiles.Count);
            foreach (var (dx, dy) in z.Tiles)
            {
                var idx = ZoneGeometry.DocToIndex(dx, dy, (int)grid.VShipPosX, (int)grid.VShipPosY, grid.NCols, grid.NRows);
                if (idx >= 0) tiles.Add(idx);
            }
            if (tiles.Count == 0) continue;
            tiles.Sort();
            result.Add(new ExportedZone
            {
                StrName = UniqueName(z.Name, used),
                StrRegID = regId,
                BTriggerOnOwner = z.TriggerOnOwner,
                ATiles = tiles.ToArray(),
                ATileConds = z.TileConds.ToArray(),
                CategoryConds = z.CategoryConds.Count > 0 ? z.CategoryConds.ToArray() : null,
                StrPersonSpec = z.PersonSpec,
                StrTargetPSpec = z.TargetPSpec,
                ZoneColor = new ExportedColor { R = z.Color.R, G = z.Color.G, B = z.Color.B, A = z.Color.A },
            });
        }
        return result.ToArray();
    }

    /// <summary>A per-ship-unique zone name: the given name, else "zone", suffixed " 2", " 3"… on a clash.</summary>
    private static string UniqueName(string name, HashSet<string> used)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "zone" : name.Trim();
        var candidate = baseName;
        for (var n = 2; !used.Add(candidate); n++) candidate = $"{baseName} {n}";
        return candidate;
    }

    /// <summary>A plausible RegID (letter-prefixed, non-empty — the game indexes <c>strRegID[0]</c> and
    /// regenerates it on spawn anyway). Uppercase, GUID-derived so distinct per export.</summary>
    private static string GenerateRegID() => "H-" + Guid.NewGuid().ToString("N")[..3].ToUpperInvariant();

    /// <summary>A file/folder-safe form of the ship name (invalid path chars → '_'; never empty).</summary>
    public static string SanitizeName(string name)
    {
        var cleaned = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        return cleaned.Length == 0 ? "OstraplanShip" : cleaned;
    }
}

// ---- the JsonShip-shaped export DTOs (a well-formed subset: the fields present on every core
// template, plus aRating). Newtonsoft (the game's serializer) defaults anything omitted and
// ignores anything extra, so this loads cleanly; pristine values sit where the game recomputes. ----

/// <summary>A <c>data/ships</c> ship object. Field names/casing match the game's JsonShip exactly.</summary>
public sealed class ExportedShip
{
    [JsonPropertyName("strName")] public string StrName { get; set; } = "";
    [JsonPropertyName("strRegID")] public string StrRegID { get; set; } = "";
    [JsonPropertyName("nCurrentWaypoint")] public int NCurrentWaypoint { get; set; } = -1;
    [JsonPropertyName("fTimeEngaged")] public double FTimeEngaged { get; set; }
    [JsonPropertyName("fWearManeuver")] public double FWearManeuver { get; set; }
    [JsonPropertyName("fWearAccrued")] public double FWearAccrued { get; set; }
    [JsonPropertyName("shipCO")] public ExportedShipCO ShipCO { get; set; } = new();
    [JsonPropertyName("aItems")] public ExportedItem[] AItems { get; set; } = [];

    /// <summary>Save-style CO records for authored contained cargo (see <c>EmitContained</c>); a template needs
    /// these so the game keeps the cargo and rebuilds stacks (<c>aStack</c>). Null — and omitted — when the design
    /// has no cargo, matching a core template.</summary>
    [JsonPropertyName("aCOs")] public ExportedCondOwnerSave[]? ACOs { get; set; }
    [JsonPropertyName("vShipPos")] public ExportedVec2 VShipPos { get; set; } = new();
    [JsonPropertyName("objSS")] public ExportedSitu ObjSS { get; set; } = new();
    [JsonPropertyName("aRooms")] public ExportedRoom[] ARooms { get; set; } = [];
    [JsonPropertyName("aZones")] public ExportedZone[] AZones { get; set; } = [];
    [JsonPropertyName("aRating")] public string[] ARating { get; set; } = [];
    [JsonPropertyName("DMGStatus")] public int DMGStatus { get; set; }
    [JsonPropertyName("fLastVisit")] public double FLastVisit { get; set; }
    [JsonPropertyName("fFirstVisit")] public double FFirstVisit { get; set; }
    [JsonPropertyName("fAIDockingExpire")] public double FAIDockingExpire { get; set; }
    [JsonPropertyName("fAIPauseTimer")] public double FAIPauseTimer { get; set; }
    [JsonPropertyName("bPrefill")] public bool BPrefill { get; set; }
    [JsonPropertyName("bBreakInUsed")] public bool BBreakInUsed { get; set; }
    [JsonPropertyName("bNoCollisions")] public bool BNoCollisions { get; set; }
    [JsonPropertyName("dLastScanTime")] public double DLastScanTime { get; set; }
    [JsonPropertyName("bLocalAuthority")] public bool BLocalAuthority { get; set; }
    [JsonPropertyName("bAIShip")] public bool BAIShip { get; set; }
    [JsonPropertyName("make")] public string Make { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("year")] public string Year { get; set; } = "";
    [JsonPropertyName("origin")] public string Origin { get; set; } = "$TEMPLATE";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("designation")] public string Designation { get; set; } = "";
    [JsonPropertyName("publicName")] public string PublicName { get; set; } = "$TEMPLATE";
    [JsonPropertyName("dimensions")] public string Dimensions { get; set; } = "";
    [JsonPropertyName("fShallowMass")] public double FShallowMass { get; set; }
    [JsonPropertyName("fShallowRCSRemass")] public double FShallowRCSRemass { get; set; }
    [JsonPropertyName("fShallowRCSRemassMax")] public double FShallowRCSRemassMax { get; set; }
    [JsonPropertyName("fShallowFusionRemain")] public double FShallowFusionRemain { get; set; }
    [JsonPropertyName("fFusionThrustMax")] public double FFusionThrustMax { get; set; }
    [JsonPropertyName("fFusionPelletMax")] public double FFusionPelletMax { get; set; }
    [JsonPropertyName("fLastQuotedPrice")] public double FLastQuotedPrice { get; set; }
    [JsonPropertyName("fEpochNextGrav")] public double FEpochNextGrav { get; set; }
    [JsonPropertyName("fBreakInMultiplier")] public double FBreakInMultiplier { get; set; }
    [JsonPropertyName("nRCSCount")] public double NRCSCount { get; set; }
    [JsonPropertyName("fShallowRotorStrength")] public double FShallowRotorStrength { get; set; }
    [JsonPropertyName("nRCSDistroCount")] public int NRCSDistroCount { get; set; }
    [JsonPropertyName("fAeroCoefficient")] public double FAeroCoefficient { get; set; }
    [JsonPropertyName("bFusionTorch")] public bool BFusionTorch { get; set; }
    [JsonPropertyName("bXPDRAntenna")] public bool BXPDRAntenna { get; set; }
    [JsonPropertyName("bShipHidden")] public bool BShipHidden { get; set; }
    [JsonPropertyName("bIsUnderConstruction")] public bool BIsUnderConstruction { get; set; }
    [JsonPropertyName("nO2PumpCount")] public int NO2PumpCount { get; set; }
    [JsonPropertyName("commData")] public ExportedComm CommData { get; set; } = new();
    [JsonPropertyName("ShipType")] public int ShipType { get; set; }
    [JsonPropertyName("nConstructionProgress")] public int NConstructionProgress { get; set; } = 100;
    [JsonPropertyName("nInitConstructionProgress")] public int NInitConstructionProgress { get; set; }
    [JsonPropertyName("nRows")] public int NRows { get; set; }
    [JsonPropertyName("nCols")] public int NCols { get; set; }
    [JsonPropertyName("nGridRotation")] public int NGridRotation { get; set; }
}

/// <summary>One item in the exported ship: a top-level placed part, or a contained sub-object — a nav-console
/// module, or a container's cargo — when <see cref="StrParentID"/>/<see cref="StrSlotParentID"/> is set.</summary>
public sealed class ExportedItem
{
    [JsonPropertyName("strName")] public string StrName { get; set; } = "";
    [JsonPropertyName("fX")] public double FX { get; set; }
    [JsonPropertyName("fY")] public double FY { get; set; }
    [JsonPropertyName("fRotation")] public double FRotation { get; set; }
    [JsonPropertyName("strID")] public string StrID { get; set; } = "";

    /// <summary>Set only on loose contained cargo (and nav-console modules): the <c>strID</c> of the container that
    /// holds it. Null — and omitted from the JSON — for a top-level part.</summary>
    [JsonPropertyName("strParentID")] public string? StrParentID { get; set; }

    /// <summary>Set only on equipped contained gear: the <c>strID</c> of the host it is slotted into. Null — and
    /// omitted from the JSON — otherwise.</summary>
    [JsonPropertyName("strSlotParentID")] public string? StrSlotParentID { get; set; }

    /// <summary>Per-instance condition overrides. Set on contained/slotted items so a template spawn retains them
    /// (<c>Ship.SpawnItems</c> keeps a parented item only when this is non-null) and suppresses the container's
    /// default loot; null — and omitted — on a top-level part, which the loader keeps unconditionally.</summary>
    [JsonPropertyName("aCondOverrides")] public ExportedCondOverride[]? ACondOverrides { get; set; }

    /// <summary>Set on contained/slotted items so the template spawn keeps their <c>strID</c> (instead of assigning
    /// a fresh one), which is what links each item to its baked CO and lets a stack head find its members. Null —
    /// and omitted — on a top-level part.</summary>
    [JsonPropertyName("bForceLoad")] public bool? BForceLoad { get; set; }
}

/// <summary>One entry in an item's <c>aCondOverrides</c>: a condition set to a fixed value on the spawned instance.
/// Matches the game's <c>JsonCondOverride</c> shape (see any core <c>data/ships</c> file).</summary>
public sealed class ExportedCondOverride
{
    [JsonPropertyName("CondName")] public string CondName { get; set; } = "";
    [JsonPropertyName("Chance")] public double Chance { get; set; } = 1.0;
    [JsonPropertyName("Amount")] public double Amount { get; set; }
    [JsonPropertyName("NegativeValue")] public bool NegativeValue { get; set; }
}

/// <summary>A minimal per-instance CO save record for a piece of authored cargo (the game's <c>JsonCondOwnerSave</c>
/// shape). A <c>data/ships</c> file spawns as a template, which keeps contained items only if they carry save-style
/// CO data; <c>aConds = ["DEFAULT"]</c> tells <c>CondOwner.SetData</c> to repopulate the def's starting conds (a
/// pristine item), and a stack head's <c>aStack</c> (member <c>strID</c>s) is what the game reads to rebuild the
/// ×N stack at the authored count. Mirrors <see cref="SaveEdit"/>'s synthesized COs.</summary>
public sealed class ExportedCondOwnerSave
{
    [JsonPropertyName("strID")] public string StrID { get; set; } = "";
    [JsonPropertyName("strCODef")] public string StrCODef { get; set; } = "";
    [JsonPropertyName("bAlive")] public bool BAlive { get; set; } = true;
    [JsonPropertyName("aConds")] public string[] AConds { get; set; } = ["DEFAULT"];
    [JsonPropertyName("strCondID")] public string StrCondID { get; set; } = "";
    [JsonPropertyName("strIdleAnim")] public string StrIdleAnim { get; set; } = "Idle";
    [JsonPropertyName("inventoryX")] public int InventoryX { get; set; }
    [JsonPropertyName("inventoryY")] public int InventoryY { get; set; }

    /// <summary>On a stack head only: the member <c>strID</c>s the game re-collects into the stack. Null — and
    /// omitted — for a single item or a real container.</summary>
    [JsonPropertyName("aStack")] public string[]? AStack { get; set; }
}

/// <summary>A baked room: tile indices (row-major into nCols×nRows), certified spec, void flag.</summary>
public sealed class ExportedRoom
{
    [JsonPropertyName("strID")] public string StrID { get; set; } = "";
    [JsonPropertyName("bVoid")] public bool BVoid { get; set; }
    [JsonPropertyName("aTiles")] public int[] ATiles { get; set; } = [];
    [JsonPropertyName("roomSpec")] public string RoomSpec { get; set; } = "Blank";
    [JsonPropertyName("roomValue")] public double RoomValue { get; set; }
}

/// <summary>A painted zone as the game expects it in <c>aZones</c> (field names/casing match JsonZone).
/// Tiles are flat row-major indices into nCols×nRows; the transient <c>aOldTiles</c> and legacy <c>ranks</c>
/// are intentionally never emitted.</summary>
public sealed class ExportedZone
{
    [JsonPropertyName("strName")] public string StrName { get; set; } = "";
    [JsonPropertyName("strRegID")] public string StrRegID { get; set; } = "";
    [JsonPropertyName("bTriggerOnOwner")] public bool BTriggerOnOwner { get; set; }
    [JsonPropertyName("aTiles")] public int[] ATiles { get; set; } = [];
    [JsonPropertyName("aTileConds")] public string[] ATileConds { get; set; } = [];
    [JsonPropertyName("categoryConds")] public string[]? CategoryConds { get; set; }
    [JsonPropertyName("strPersonSpec")] public string? StrPersonSpec { get; set; }
    [JsonPropertyName("strTargetPSpec")] public string? StrTargetPSpec { get; set; }
    [JsonPropertyName("zoneColor")] public ExportedColor ZoneColor { get; set; } = new();
}

/// <summary>A zoneColor {r,g,b,a} (components 0..1).</summary>
public sealed class ExportedColor
{
    [JsonPropertyName("r")] public double R { get; set; }
    [JsonPropertyName("g")] public double G { get; set; }
    [JsonPropertyName("b")] public double B { get; set; }
    [JsonPropertyName("a")] public double A { get; set; } = 1;
}

/// <summary>vShipPos / a Vector2 as the game serializes it.</summary>
public sealed class ExportedVec2
{
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
}

/// <summary>The ship's own condition owner: a pristine ShipCO with the standard progress-cap conds.</summary>
public sealed class ExportedShipCO
{
    [JsonPropertyName("strID")] public string StrID { get; set; } = "";
    [JsonPropertyName("strCODef")] public string StrCODef { get; set; } = "ShipCO";
    [JsonPropertyName("bAlive")] public bool BAlive { get; set; } = true;
    [JsonPropertyName("aConds")] public string[] AConds { get; set; } = [];
    [JsonPropertyName("strCondID")] public string StrCondID { get; set; } = "";
    [JsonPropertyName("strIdleAnim")] public string StrIdleAnim { get; set; } = "Idle";
    [JsonPropertyName("strFriendlyName")] public string StrFriendlyName { get; set; } = "ShipCO";

    public static ExportedShipCO Pristine()
    {
        var id = Guid.NewGuid().ToString();
        return new ExportedShipCO
        {
            StrID = "CO-" + id,
            StrCondID = id,
            AConds =
            [
                "StatInstallProgressMax=1.0x1000",
                "StatUninstallProgressMax=1.0x1000",
                "StatRepairProgressMax=1.0x1000",
                "DEFAULT",
            ],
        };
    }
}

/// <summary>The star-system situation (position/velocity). Neutral — the game repositions a spawned template.</summary>
public sealed class ExportedSitu
{
    [JsonPropertyName("boPORShip")] public string BoPORShip { get; set; } = "Sol";
    [JsonPropertyName("vPosx")] public double VPosx { get; set; }
    [JsonPropertyName("vPosy")] public double VPosy { get; set; }
    [JsonPropertyName("vVelX")] public double VVelX { get; set; }
    [JsonPropertyName("vVelY")] public double VVelY { get; set; }
    [JsonPropertyName("bIsNoFees")] public bool BIsNoFees { get; set; } = true;
    [JsonPropertyName("size")] public int Size { get; set; }
}

/// <summary>Comm/clearance state — empty for a fresh design.</summary>
public sealed class ExportedComm
{
    [JsonPropertyName("strClearanceType")] public string StrClearanceType { get; set; } = "";
}

/// <summary>The mod's <c>mod_info.json</c> (matches the sample/CLAUDE.md fields).</summary>
public sealed class ModInfo
{
    [JsonPropertyName("strName")] public string StrName { get; set; } = "";
    [JsonPropertyName("strAuthor")] public string StrAuthor { get; set; } = "";
    [JsonPropertyName("strModURL")] public string StrModURL { get; set; } = "";
    [JsonPropertyName("strGameVersion")] public string StrGameVersion { get; set; } = "";
    [JsonPropertyName("strModVersion")] public string StrModVersion { get; set; } = "1.0.0";
    [JsonPropertyName("strNotes")] public string StrNotes { get; set; } = "";
}
