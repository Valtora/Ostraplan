using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ostraplan.Core;

/// <summary>What the user chose in the export dialog. <see cref="DestinationParent"/> is
/// the folder the mod folder is created <b>inside</b> (a user-picked directory or the game's
/// <c>Ostranauts_Data/Mods</c>); the mod folder itself is named after the ship.
/// <para><see cref="PublicName"/> is the ship's in-game display name (shown at the XPDR
/// transponder, comms, broker listings, MFD dock info, and the rating screen) — distinct from
/// <see cref="ShipName"/>, which only names the mod/file. Defaults to <see cref="ShipName"/>
/// when left blank by the dialog, but must never be blank/"$TEMPLATE" itself: the game only
/// keeps a custom <c>publicName</c> when the on-disk value isn't one of those two things
/// (verified against decompiled <c>Ship.InitShip</c>) — otherwise it re-rolls a random name on
/// every spawn, which is exactly the "brittle" behavior this fixes.</para>
/// <para><see cref="Make"/>/<see cref="Model"/>/<see cref="Year"/>/<see cref="Designation"/>/
/// <see cref="Description"/> map straight onto the game's own <c>JsonShip</c> fields (present on
/// core ships and used by mods like Ithalan's Additional Ships) — flavor text only, no game logic
/// reads them beyond display.</para>
/// <para><see cref="ReplaceTarget"/>, when set, is the <c>strName</c> of an existing (core or mod)
/// ship this design should <b>replace</b>: the exported ship is keyed to that name so — loaded after
/// core — the game's whole-object override swaps the design in for the original everywhere it spawns.
/// A replacement with no explicit <see cref="PublicName"/> keeps the vanilla varied-naming behaviour
/// (<c>publicName = "$TEMPLATE"</c>), not the design name.</para>
/// <para><see cref="ModName"/> names the mod itself (its <c>mod_info.json strName</c> + folder), separate
/// from the ship. Blank resolves (<see cref="ShipExport.ResolveModName"/>) to <c>"{ReplaceTarget} - Replaced
/// via Ostraplan"</c> for a replacement (so the mod is distinct from the ship it overrides), else to
/// <see cref="ShipName"/>.</para></summary>
public sealed record ExportOptions(
    string ShipName, string Author, string Notes, string ModVersion, string GameVersion,
    string DestinationParent, string PublicName, string Make = "", string Model = "",
    string Year = "", string Designation = "", string Description = "",
    ShipDelivery? Delivery = null, string? ReplaceTarget = null, string ModName = "");

/// <summary>How the exported ship becomes obtainable in game — the loot/chargen data an export
/// injects on top of the ship file. All of it is full-object overrides / additive entries the game
/// merges by <c>strName</c>; a same-pool clash with another ship mod is Ostrasort's <c>--patch</c> case.
/// <see cref="TouchesLoot"/> is true when anything here writes <c>data/loot</c> (drives the Ostrasort
/// patch follow-up). Default <see cref="None"/> exports the ship file only, as before.</summary>
public sealed record ShipDelivery(
    IReadOnlyList<string> BrokerPools, double BrokerWeight,
    IReadOnlyList<string> SpecialOfferPools,
    bool StartingShip, double StartingShipWeight, string StartingShipStation,
    double StartingShipMortgage, string StartingShipTitle, string StartingShipDesc)
{
    public bool TouchesLoot => BrokerPools.Count > 0 || SpecialOfferPools.Count > 0 || StartingShip;
    public static ShipDelivery None => new([], 0, [], false, 0, "OKLG", 0, "", "");
}

/// <summary>The outcome of a successful export: where it landed and what it contains.
/// <see cref="TouchedLootPools"/> is true when the export wrote broker/Special-Offer/starting-ship loot
/// (so the caller knows an Ostrasort <c>--patch</c> pass may be warranted).</summary>
public sealed record ExportResult(
    string ModDir, string ShipJsonPath, string ModInfoPath,
    int PartCount, int RoomCount, ShipRating Rating, IReadOnlyList<string> Warnings,
    bool TouchedLootPools = false);

/// <summary>Flavor/identity fields for <see cref="ShipExport.Build"/>, split out from
/// <see cref="ExportOptions"/> so callers that don't care about ship metadata (most tests) can omit
/// it entirely. See <see cref="ExportOptions.PublicName"/> for why <see cref="PublicName"/> matters
/// more than it looks — it's the one field the game actually keeps sticky across spawns.</summary>
public sealed record ExportMetadata(
    string PublicName = "", string Make = "", string Model = "", string Year = "",
    string Designation = "", string Description = "");

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
        ShipDocument doc, Catalog catalog, IReadOnlyList<RoomSpecDef> specs, string shipName,
        List<string>? warnings = null, ExportMetadata? meta = null)
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

        // Installed docking ports, collected as we emit items so they can be baked into aDockingPorts below.
        var docksysPorts = new List<(string Id, bool TypeB, bool PrimaryDef)>();

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

            if (IsDocksysPart(part.Part, catalog))
                docksysPorts.Add((strID, part.Part.Has("IsTypeB"), part.Part.DefName == Catalog.PrimaryDocksysDef));

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

        // Loose items dropped on the floor (the Items palette): the stack head is a free-standing, parentless
        // top-level item at its tile — exactly how a core template lists floor cargo (a salvage pod's scrap, a
        // bunk's effects). The loader keeps a top-level item unconditionally (unlike a parented one, which needs the
        // bForceLoad/marker gate), so the single-item case needs no CO record. A quantity > 1 is a STACK: the extra
        // copies are members parented to the head (with the pristine marker + bForceLoad so they survive and keep
        // their strIDs), and the head gets a CO whose aStack lists them, the same shape EmitContained bakes for a
        // container's stacked cargo (see CondOwner.PostGameLoad).
        foreach (var lo in doc.LooseObjects)
        {
            if (catalog.Lookup(lo.DefName) is not { } part) { warnings?.Add($"Loose item '{lo.DefName}' has no def; skipped."); continue; }
            var (w, h) = GridMath.Size(part.Item.Width, part.Item.Height, lo.Rot);
            var fx = lo.X + (w / 2.0 - 0.5);
            var fy = -(lo.Y + (h / 2.0 - 0.5));
            var rot = GridMath.Norm(-lo.Rot);
            var headId = Guid.NewGuid().ToString();
            var qty = Math.Clamp(lo.Quantity, 1, Math.Max(1, part.StackLimit));

            items.Add(new ExportedItem { StrName = lo.DefName, FX = fx, FY = fy, FRotation = rot, StrID = headId });

            if (qty > 1)
            {
                var memberIds = new List<string>(qty - 1);
                for (var i = 1; i < qty; i++)
                {
                    var mid = Guid.NewGuid().ToString();
                    items.Add(new ExportedItem
                    {
                        StrName = lo.DefName, FX = fx, FY = fy, FRotation = rot, StrID = mid,
                        StrParentID = headId, ACondOverrides = PristineMarker, BForceLoad = true,
                    });
                    memberIds.Add(mid);
                }
                cos.Add(new ExportedCondOwnerSave
                {
                    StrID = headId, StrCODef = lo.DefName, StrCondID = lo.DefName + headId, AStack = memberIds.ToArray(),
                });
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

        // publicName is written verbatim: the caller (Write) has already resolved the display-name policy
        // (custom name / vanilla "$TEMPLATE" for a replacement / the ship name), via ResolvePublicName. Build is a
        // mechanical writer — it only falls back to the ship name if handed nothing at all.
        var publicName = meta?.PublicName is { Length: > 0 } pn ? pn : shipName;

        // Bake the installed docking ports + primary. The game rebuilds these from items only on a Full/Edit load
        // (Ship load clears aDockingPorts then re-registers via AddCO); a SHALLOW-loaded spawn reads them straight
        // from the file and never rebuilds. Vendor stock (Trader.AddNewShips), the Special Offer
        // (GUIShipBroker.AddSpecialOfferShip), and the shallow-station dock branch of OnPurchaseConfirm all spawn/
        // dock the ship Shallow — so omitting these left a purchased ship exposing zero open ports, and the game
        // could not mate it to the station and stranded it at its objSS instead of docking.
        string[]? aDockingPorts = null;
        string? primaryPortId = null;
        if (docksysPorts.Count > 0)
        {
            // Mirror the game's registration order: non-TypeB (primary) ports first, TypeB ports last; the primary
            // is the Primary Airlock (ItmDockSys02Closed) when present, else the first non-TypeB port.
            var ordered = docksysPorts.Where(p => !p.TypeB).Concat(docksysPorts.Where(p => p.TypeB)).ToList();
            primaryPortId = docksysPorts.FirstOrDefault(p => p.PrimaryDef).Id ?? ordered[0].Id;
            aDockingPorts = ordered.Select(p => p.Id).OrderBy(id => id == primaryPortId ? 0 : 1).ToArray();
        }

        var ship = new ExportedShip
        {
            StrName = shipName,
            StrRegID = regId,
            PublicName = publicName,
            Make = meta?.Make ?? "",
            Model = meta?.Model ?? "",
            Year = meta?.Year ?? "",
            Designation = meta?.Designation ?? "",
            Description = meta?.Description ?? "",
            NCols = grid.NCols,
            NRows = grid.NRows,
            VShipPos = new ExportedVec2(),   // (0,0): the anchor the coordinate inverse assumes
            AItems = items.ToArray(),
            ADockingPorts = aDockingPorts,
            StrPrimaryDockingPortID = primaryPortId,
            ACOs = cos.Count > 0 ? cos.ToArray() : null,   // save-style CO data for authored cargo; omitted when none
            ARooms = rooms,
            AZones = zones,
            // objSS at exact (0,0) around "Sol" is Sol's own coordinate origin, not a neutral placeholder: the
            // loot-spawn path (kiosk/Special-Offer/starting-ship) does NOT reposition it like template import does,
            // so a literal (0,0) spawns the ship inside the star. Every core template instead carries small nonzero
            // leftover save-state coordinates (e.g. SalvageCustom2.json: -0.2178, -0.3177) and never exhibits this.
            ObjSS = new ExportedSitu { VPosx = -0.25, VPosy = -0.35 },
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
    /// under <see cref="ExportOptions.DestinationParent"/>. Overwrites an existing same-named mod folder's data
    /// files (never deletes anything else). When <see cref="ExportOptions.Delivery"/> asks for kiosk/Special-Offer/
    /// starting-ship availability, also writes the loot/lifeevent/interaction files — which needs
    /// <paramref name="index"/> to clone the current effective loot pools. Returns where it landed; throws on I/O
    /// failure for the caller to report.
    /// </summary>
    public static ExportResult Write(
        ShipDocument doc, Catalog catalog, IReadOnlyList<RoomSpecDef> specs, ExportOptions opts, DataIndex? index = null)
    {
        var warnings = new List<string>();

        // The ship's strName is the override key. For a replacement it's the target ship's name (so the game swaps
        // this design in for it); otherwise it's the design name. Everything that references the ship by strName —
        // the ship object itself and the delivery loot pools — must use this, NOT the display publicName.
        var isReplace = !string.IsNullOrWhiteSpace(opts.ReplaceTarget);
        var strName = isReplace ? opts.ReplaceTarget!.Trim() : opts.ShipName;
        var publicName = ResolvePublicName(opts.PublicName, opts.ShipName, isReplace);

        var meta = new ExportMetadata(publicName, opts.Make, opts.Model, opts.Year, opts.Designation, opts.Description);
        var (ship, rating, roomCount) = Build(doc, catalog, specs, strName, warnings, meta);

        // The mod name (mod_info strName + folder) is separate from the ship: a replacement defaults to
        // "{target} - Replaced via Ostraplan" so the mod reads distinctly from the ship it overrides.
        var modName = ResolveModName(opts.ModName, opts.ShipName, opts.ReplaceTarget);
        var folderName = SanitizeName(modName);
        var modDir = Path.Combine(opts.DestinationParent, folderName);
        var shipsDir = Path.Combine(modDir, "data", "ships");
        Directory.CreateDirectory(shipsDir);

        var shipPath = Path.Combine(shipsDir, folderName + ".json");
        File.WriteAllText(shipPath, Serialize(ship));

        var modInfoPath = Path.Combine(modDir, "mod_info.json");
        var modInfo = new ModInfo
        {
            StrName = modName,
            StrAuthor = opts.Author,
            StrGameVersion = opts.GameVersion,
            StrModVersion = string.IsNullOrWhiteSpace(opts.ModVersion) ? "1.0.0" : opts.ModVersion,
            StrNotes = string.IsNullOrWhiteSpace(opts.Notes)
                ? isReplace
                    ? $"Replaces \"{strName}\" in-game with a design exported from Ostraplan."
                    : $"\"{opts.ShipName}\", a ship design exported from Ostraplan."
                : opts.Notes,
        };
        File.WriteAllText(modInfoPath, SerializeModInfo(modInfo));

        var touchedLoot = false;
        if (opts.Delivery is { TouchesLoot: true } delivery)
        {
            if (index is null)
                warnings.Add("Delivery options were set but no game data was available to resolve loot pools; skipped.");
            else
                touchedLoot = WriteDeliveryFiles(modDir, strName, delivery, index, warnings);
        }

        return new ExportResult(modDir, shipPath, modInfoPath, ship.AItems.Length, roomCount, rating, warnings, touchedLoot);
    }

    /// <summary>
    /// Resolve the ship's in-game <c>publicName</c> from the user's input. A real typed name (not blank, not the
    /// literal <c>"$TEMPLATE"</c> sentinel) is always honoured. Otherwise: a <b>replacement</b> keeps the vanilla
    /// varied-naming behaviour (<c>"$TEMPLATE"</c>, so each spawned copy still gets its own generated name, matching
    /// the original template), while a <b>new</b> ship takes the design name (a stable identity for your own ship).
    /// </summary>
    public static string ResolvePublicName(string? custom, string fallbackName, bool isReplace) =>
        custom is { Length: > 0 } c && c.Trim() is { Length: > 0 } t && t != "$TEMPLATE" ? t
        : isReplace ? "$TEMPLATE"
        : fallbackName;

    /// <summary>
    /// Resolve the mod's name (its <c>mod_info.json strName</c> + folder), which is separate from the ship. A name
    /// the user typed is honoured; otherwise a <b>replacement</b> defaults to <c>"{replaceTarget} - Replaced via
    /// Ostraplan"</c> — so the mod reads distinctly from the ship it overrides, rather than colliding with the
    /// replaced ship's own name — while a <b>new</b> ship's mod takes the ship name.
    /// </summary>
    public static string ResolveModName(string? modName, string shipName, string? replaceTarget) =>
        modName is { Length: > 0 } m && m.Trim() is { Length: > 0 } t ? t
        : replaceTarget is { Length: > 0 } r && r.Trim() is { Length: > 0 } target ? $"{target} - Replaced via Ostraplan"
        : shipName;

    /// <summary>
    /// Write the loot/lifeevent/interaction files that make the ship obtainable. <paramref name="shipRef"/> is the
    /// ship's spawn name — the design's <c>strName</c> (the loot <c>aCOs</c> reference the ship template by that
    /// name). Broker + Special-Offer overrides and the starting-ship reward pool all share one
    /// <c>data/loot/loot.json</c>; the starting-ship chain adds <c>data/lifeevents</c> + <c>data/interactions</c>.
    /// Returns whether any loot was written.
    /// </summary>
    private static bool WriteDeliveryFiles(
        string modDir, string shipStrName, ShipDelivery delivery, DataIndex index, List<string> warnings)
    {
        // The loot aCOs reference a ship by its data/ships strName (the ship object's strName — the design name, or
        // the replace target when replacing), NOT the display publicName. The caller passes exactly that.

        var loot = new List<JsonObject>();
        foreach (var pool in delivery.BrokerPools)
            loot.Add(KioskExport.BrokerPoolOverride(index, pool, shipStrName, delivery.BrokerWeight));
        foreach (var pool in delivery.SpecialOfferPools)
            loot.Add(KioskExport.SpecialOfferOverride(index, pool, shipStrName));

        List<JsonObject>? lifeevents = null, interactions = null;
        if (delivery.StartingShip)
        {
            var eventsPool = KioskExport.ClonePoolOrDefault(index, StartingShipExport.ShipEventsPool);
            var frags = StartingShipExport.Build(
                eventsPool, shipStrName, delivery.StartingShipWeight, delivery.StartingShipStation,
                delivery.StartingShipMortgage,
                string.IsNullOrWhiteSpace(delivery.StartingShipTitle) ? shipStrName + "." : delivery.StartingShipTitle,
                string.IsNullOrWhiteSpace(delivery.StartingShipDesc)
                    ? $"You come across a listing for the {shipStrName}. It could be your ticket out of the day-labour berth."
                    : delivery.StartingShipDesc);
            loot.AddRange(frags.LootObjects);
            lifeevents = frags.Lifeevents.ToList();
            interactions = frags.Interactions.ToList();
        }

        if (loot.Count > 0) WriteJsonArray(Path.Combine(modDir, "data", "loot", "loot.json"), loot);
        if (lifeevents is { Count: > 0 }) WriteJsonArray(Path.Combine(modDir, "data", "lifeevents", "lifeevents.json"), lifeevents);
        if (interactions is { Count: > 0 }) WriteJsonArray(Path.Combine(modDir, "data", "interactions", "interactions.json"), interactions);

        return loot.Count > 0;
    }

    /// <summary>Write a list of objects as the game expects a data file: a top-level JSON array, indented.</summary>
    private static void WriteJsonArray(string path, IReadOnlyList<JsonObject> objects)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var arr = new JsonArray();
        foreach (var o in objects) arr.Add(o.DeepClone());   // DeepClone: a node can't be re-parented into two arrays
        File.WriteAllText(path, arr.ToJsonString(Json));
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

    /// <summary>True when a placed part is an installed docking port the game registers into
    /// <c>Ship.aDockingPorts</c> — it triggers <c>TIsDockSysInstalled</c>, the same predicate
    /// <see cref="ProblemScan.IsDocksys"/> uses for the "no docking port" design check.</summary>
    private static bool IsDocksysPart(ResolvedPart part, Catalog catalog) =>
        catalog.Triggers.TryGetValue(ProblemScan.DocksysTrigger, out var ct)
        && ct.Reqs.Length > 0
        && ct.Reqs.All(part.Has);

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

    /// <summary>Installed docking-port item strIDs (primary/non-TypeB first, TypeB last). The game rebuilds this
    /// from items on a Full/Edit load, but a <b>Shallow</b>-loaded spawn (vendor stock, Special Offer, and the
    /// shallow-station dock branch in <c>GUIShipBroker.OnPurchaseConfirm</c>) reads it verbatim from the file —
    /// without it a purchased ship exposes no open ports (<c>Ship.GetOpenDockingPorts</c>) and the game strands it
    /// at its <c>objSS</c> instead of docking. Null — and omitted — when the design has no docking port (every
    /// valid export carries the Primary Airlock).</summary>
    [JsonPropertyName("aDockingPorts")] public string[]? ADockingPorts { get; set; }

    /// <summary>The primary docking port's item strID (the Primary Airlock). The game derives this from
    /// <see cref="ADockingPorts"/> when empty, but baking it keeps a Shallow spawn unambiguous. Null — and
    /// omitted — when the design has no docking port.</summary>
    [JsonPropertyName("strPrimaryDockingPortID")] public string? StrPrimaryDockingPortID { get; set; }
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

/// <summary>The star-system situation (position/velocity). Template <b>import</b> repositions this on load, but the
/// loot-spawn path (kiosk/Special-Offer/starting-ship) does not — a literal (0,0) spawns the ship inside "Sol" (see
/// <see cref="ShipExport.Build"/>), so this must never default to exact zero.</summary>
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
