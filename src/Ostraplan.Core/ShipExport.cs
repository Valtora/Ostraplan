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

        var items = new List<ExportedItem>(grid.Parts.Count);
        foreach (var part in grid.Parts)
        {
            var (w, h) = GridMath.Size(part.Part.Item.Width, part.Part.Item.Height, part.Rot);
            // inverse of ShipGrid.FromTemplate with vShipPos=(0,0): centre = top-left + (size/2 − 0.5),
            // y flips (grid is y-down, the game is y-up), and fRotation is CCW = Norm(−Rot).
            items.Add(new ExportedItem
            {
                StrName = part.Part.DefName,
                FX = part.TopLeftCol + (w / 2.0 - 0.5),
                FY = -(part.TopLeftRow + (h / 2.0 - 0.5)),
                FRotation = GridMath.Norm(-part.Rot),
                StrID = Guid.NewGuid().ToString(),
            });
        }

        var rooms = partition.Rooms.Select(r => new ExportedRoom
        {
            StrID = Guid.NewGuid().ToString(),
            BVoid = r.Void,
            ATiles = r.Tiles.ToArray(),
            RoomSpec = r.RoomSpec,
            RoomValue = r.Volume,
        }).ToArray();

        var roomCount = partition.Rooms.Count(r => r.RoomSpec is not ("" or "Blank"));

        var ship = new ExportedShip
        {
            StrName = shipName,
            StrRegID = GenerateRegID(),
            NCols = grid.NCols,
            NRows = grid.NRows,
            VShipPos = new ExportedVec2(),   // (0,0): the anchor the coordinate inverse assumes
            AItems = items.ToArray(),
            ARooms = rooms,
            ARating = [rating.Epoch.Length == 0 ? "0" : rating.Epoch,
                rating.Condition, rating.RoomCount, rating.Maneuver, rating.Size, rating.Slot5],
            Dimensions = $"{grid.NCols * MetresPerTile:0.00}m x {grid.NRows * MetresPerTile:0.00}m",
            ShipCO = ExportedShipCO.Pristine(),
        };

        return (ship, rating, roomCount);
    }

    /// <summary>Serialize the ship as the game expects a <c>data/ships</c> file: a one-element top-level array.</summary>
    public static string Serialize(ExportedShip ship) => JsonSerializer.Serialize(new[] { ship }, Json);

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
        File.WriteAllText(modInfoPath, JsonSerializer.Serialize(new ModInfo
        {
            StrName = opts.ShipName,
            StrAuthor = opts.Author,
            StrGameVersion = opts.GameVersion,
            StrModVersion = string.IsNullOrWhiteSpace(opts.ModVersion) ? "1.0.0" : opts.ModVersion,
            StrNotes = string.IsNullOrWhiteSpace(opts.Notes)
                ? $"\"{opts.ShipName}\", a ship design exported from Ostraplan."
                : opts.Notes,
        }, Json));

        return new ExportResult(modDir, shipPath, modInfoPath, ship.AItems.Length, roomCount, rating, warnings);
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
    [JsonPropertyName("vShipPos")] public ExportedVec2 VShipPos { get; set; } = new();
    [JsonPropertyName("objSS")] public ExportedSitu ObjSS { get; set; } = new();
    [JsonPropertyName("aRooms")] public ExportedRoom[] ARooms { get; set; } = [];
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

/// <summary>One placed item, top-level (authored designs have no contained/slotted sub-objects).</summary>
public sealed class ExportedItem
{
    [JsonPropertyName("strName")] public string StrName { get; set; } = "";
    [JsonPropertyName("fX")] public double FX { get; set; }
    [JsonPropertyName("fY")] public double FY { get; set; }
    [JsonPropertyName("fRotation")] public double FRotation { get; set; }
    [JsonPropertyName("strID")] public string StrID { get; set; } = "";
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
