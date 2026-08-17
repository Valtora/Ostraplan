using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;

namespace Ostraplan.Core;

/// <summary>
/// Where a granted ship is put: the world pose of the ship the player is currently on (their own ship when
/// flying, the station's own coordinates when docked, since a docked ship shares its host's position exactly).
/// The granted ship is placed a few kilometres off this point, inheriting the reference body and velocity so it
/// drifts with everything else in the neighbourhood.
/// <para><see cref="SizeMetres"/> is the anchor's <c>objSS.size</c>, its collision radius in metres
/// (<c>ShipSitu.GetRadiusAU</c> = <c>size × 6.684587E-12</c>). The game recomputes this for every ship on load
/// (<c>Ship.InitShip</c> → <c>SilhouetteUtility.GetSilhouetteLength</c>), so a granted ship writes 0 and lets the
/// game fill it in; it is read here only to keep the spawn clear of the anchor.</para>
/// </summary>
public sealed record GrantAnchor(
    double PosX, double PosY, double VelX, double VelY, string? BoPorShip, bool BoLocked, int SizeMetres)
{
    /// <summary>Read the anchor out of a save's ship record (<c>ships/&lt;RegID&gt;.json</c>'s <c>objSS</c>).</summary>
    public static GrantAnchor FromShipRecord(JsonNode shipRecord)
    {
        var ss = (shipRecord as JsonObject)?["objSS"] as JsonObject;
        return new GrantAnchor(
            Dbl(ss, "vPosx"), Dbl(ss, "vPosy"), Dbl(ss, "vVelX"), Dbl(ss, "vVelY"),
            Str(ss, "boPORShip"), Bool(ss, "bBOLocked"), (int)Math.Round(Dbl(ss, "size")));
    }

    private static double Dbl(JsonNode? n, string p) =>
        (n as JsonObject)?[p] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0.0;

    private static string? Str(JsonNode? n, string p) =>
        (n as JsonObject)?[p] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool Bool(JsonNode? n, string p) =>
        (n as JsonObject)?[p] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}

/// <summary>What the user chose for a grant. <see cref="DesignName"/> is only a fallback for the in-game display
/// name; a granted ship's <c>strName</c> is its RegID, the way the game writes a save's own ships.
/// <see cref="PlacementSeed"/> pins the spawn draw for a reproducible result (tests); null draws freely.
///
/// <para><see cref="SourceCos"/> turns the grant into a <b>transfer</b>: the condition owners of the ship this
/// design was imported from, keyed by save item <c>strID</c>. Supplied, each part arrives with the damage it
/// really had rather than a fresh roll, and <see cref="Wear"/> is not applied — a ship moved between saves should
/// be the ship, not a re-rolled copy of it. Null for an ordinary grant of a design that came from nowhere.</para></summary>
public sealed record GrantOptions(
    string DesignName, ExportMetadata? Meta = null, WearOptions? Wear = null, int? PlacementSeed = null,
    IReadOnlyDictionary<string, JsonNode>? SourceCos = null);

/// <summary>What a grant produced: the minted identity, the size of the ship, its baked rating, and how far off
/// the anchor it landed (in km, which is the figure worth showing the user — it is the walk home).</summary>
public sealed record GrantReport(
    string RegId, string PublicName, int ItemCount, int RoomCount, ShipRating Rating,
    double DistanceKm, IReadOnlyList<string> Warnings,
    double? Charged = null, double? ResultingBalance = null);

/// <summary>
/// The target save, read once so a grant can be costed and described before anything is written: which ship the
/// player is on (the spawn anchor and the record holding their character CO), their CO id and balance, the game
/// epoch, and every registration already in use.
///
/// <para><see cref="PlayerShipRecord"/> is the live, mutable record the grant edits — the player CO's
/// <c>aMyShips</c> gains the new ship, and its <c>StatUSD</c> takes the price. That is the same record the
/// <see cref="Anchor"/> is read from, so a grant rewrites exactly one existing ship file.</para>
/// </summary>
public sealed class GrantContext
{
    /// <summary>The save's folder name, for messages and for naming the copy.</summary>
    public required string SaveName { get; init; }

    /// <summary>The save's data zip. Opened read-only here; a grant writes to a copy of the whole folder.</summary>
    public required string ZipPath { get; init; }

    /// <summary>The ship the player is currently standing on: the spawn anchor, and the record carrying the
    /// player CO. When they are docked this is the station's own position, since a docked ship shares its host's
    /// coordinates exactly.</summary>
    public required string PlayerShipRegId { get; init; }

    /// <summary>The player character CO's <c>strID</c> — the owner a granted ship is registered to.</summary>
    public required string PlayerCoId { get; init; }

    /// <summary>The save's game epoch, stamped onto the device tickers baked into the granted ship.</summary>
    public required double Epoch { get; init; }

    /// <summary>Where the granted ship is parked, read from <see cref="PlayerShipRecord"/>.</summary>
    public required GrantAnchor Anchor { get; init; }

    /// <summary>Every registration the save already uses (one per <c>ships/*.json</c>), so a mint cannot collide.</summary>
    public required IReadOnlyCollection<string> ExistingRegIds { get; init; }

    /// <summary>The player's ship record as a mutable node.</summary>
    public required JsonNode PlayerShipRecord { get; init; }

    /// <summary>The entry name of the session (character) record, which is where ownership has to be written.</summary>
    public required string SessionEntryName { get; init; }

    /// <summary>The player's current credit balance (their CO's summed <c>StatUSD</c>).</summary>
    public required double Balance { get; init; }
}

/// <summary>
/// Builds a <b>new</b> ship record to drop into a save, as opposed to <see cref="SaveEdit"/>, which rewrites one
/// that is already there. The design becomes a ship the player owns, parked a few kilometres off wherever they
/// currently are, undocked and reachable by the P.A.S.S. ferry.
///
/// <para>The record is <see cref="ShipExport"/>'s output converted from <b>template</b> shape to <b>save</b>
/// shape. The two differ in three ways that matter, all verified against the decompile:</para>
/// <list type="bullet">
/// <item>A template defaults a missing condition owner; a save does not. <c>DataHandler.SpawnItems</c> skips any
/// item whose <c>strID</c> is absent from <c>dictCOSaves</c> ("Trying to load a CO … with missing save data …
/// Skipping"), so <b>every</b> item needs one. <see cref="ShipExport"/> emits COs for contained cargo only, so
/// the top-level parts are synthesized here.</item>
/// <item>A newly-built device needs its GUI-prop-maps baked or it loads installed-but-unwired, because the game
/// restores those from the save rather than rebuilding them from the def. Wired devices already carry an
/// <c>Electrical</c> panel written by <see cref="ShipExport"/>, so the def's panels are <b>merged under</b> what
/// is already there rather than replacing it, or the signal connections would be lost.</item>
/// <item>Regenerated <c>aRooms</c> orphan the per-room gas containers, so the ship comes up in vacuum without
/// <c>bPrefill</c>. Prefill also fires the break-in damage pass on a Used/Damaged/Derelict hull, so the record is
/// marked <c>DMGStatus</c> New first and any wear is baked per part instead (the same reasoning, and the same
/// pairing, as <see cref="SaveEdit"/>).</item>
/// </list>
///
/// <para><b>Ownership is not written here.</b> A <c>JsonShip</c> has no owner field: ownership lives entirely in
/// the character record's <c>objSystem.dictShipOwners</c> (RegID → owner CO strID; a miss returns the literal
/// "UNREGISTERED") plus the owning CO's <c>aMyShips</c>. Both are the writer's job, not this builder's.</para>
/// </summary>
public static class SaveGrant
{
    // ---- placement, ported from the game's own "bought a ship with nowhere to dock" path ----

    /// <summary>Metres per AU, as the game reckons it: the reciprocal of <c>ShipSitu.GetRadiusAU</c>'s
    /// <c>6.684587E-12</c> AU-per-metre.</summary>
    public const double MetresPerAu = 1.0 / RadiusAuPerMetre;

    /// <summary>AU per metre (<c>ShipSitu.GetRadiusAU</c>), for turning a ship's <c>objSS.size</c> into a radius.</summary>
    private const double RadiusAuPerMetre = 6.684587E-12;

    /// <summary>The inner spawn radius, exactly 3.000 km. This and <see cref="MaxRadiusAu"/> are the literal pair
    /// <c>GUIShipBroker.OnPurchaseConfirm</c> hands <c>StarSystem.SetSituToRandomSafeCoords</c> when a ship is
    /// bought and the station has no free port, which is the same situation a grant creates.</summary>
    public const double MinRadiusAu = 2.005376131819503E-08;

    /// <summary>The outer spawn radius, exactly 5.000 km. See <see cref="MinRadiusAu"/>.</summary>
    public const double MaxRadiusAu = 3.342293553032505E-08;

    /// <summary>How many times the game re-draws a spawn point before giving up
    /// (<c>SetSituToRandomSafeCoords</c>).</summary>
    private const int PlacementAttempts = 25;

    /// <summary>The ferry's range limit (<c>GUIPDAFerry.ShowRequest</c>): a destination further than this from the
    /// caller, and not inside the local ATC region, is dropped from the list. Exactly 5,000 km, so the whole spawn
    /// band above sits at a fifth of it or less. Exposed so the UI can say so.</summary>
    public const double FerryRangeAu = 3.342293712194078E-05;

    /// <summary>The game's <c>MathUtils.RandType.Low</c> draw: uniform squared, so the result is biased toward
    /// <paramref name="min"/>. Ported so a granted ship clusters at the same end of the band a bought one does.</summary>
    private static double RandLow(Random rng, double min, double max)
    {
        var u = rng.NextDouble();
        return min + u * u * (max - min);
    }

    /// <summary>
    /// Draw a spawn point <paramref name="anchor"/>-relative, the way <c>SetSituToRandomSafeCoords</c> does:
    /// a <see cref="RandLow"/> radius in [<see cref="MinRadiusAu"/>, <see cref="MaxRadiusAu"/>] at a flat random
    /// bearing, with <c>x = anchor.x + sin(θ)·r</c> and <c>y = anchor.y + cos(θ)·r</c>.
    ///
    /// <para>The game re-draws while the point lands inside a body or overlaps any ship. Only the second test is
    /// reproduced, and only against the anchor: reading every ship's position would mean decompressing the whole
    /// save (377&#160;MB on a mature one) to guard a case that cannot arise, because <b>every ship docked at the
    /// anchor shares the anchor's coordinates exactly</b>, so one check against the anchor covers the entire
    /// docking group. Undocked third parties are elsewhere in the system and not a factor at 5&#160;km. The body
    /// test needs the orbit table, which lives in the character record we deliberately do not parse.</para>
    /// </summary>
    public static (double X, double Y, double DistanceKm) DrawSpawnPoint(GrantAnchor anchor, Random rng)
    {
        // Keep clear of the anchor: its own radius, plus the same figure again for the granted ship, whose size
        // the game will not compute until it loads the record. Like-for-like is the honest assumption, and the
        // draw floor of 3 km clears the largest hull in core data (a station reads size 1500, i.e. 1.5 km) anyway.
        var clearanceAu = 2.0 * anchor.SizeMetres * RadiusAuPerMetre;

        double x = 0, y = 0, r = 0;
        for (var attempt = 0; attempt < PlacementAttempts; attempt++)
        {
            r = RandLow(rng, MinRadiusAu, MaxRadiusAu);
            var theta = rng.NextDouble() * 2.0 * Math.PI;
            x = anchor.PosX + Math.Sin(theta) * r;
            y = anchor.PosY + Math.Cos(theta) * r;
            if (r > clearanceAu) break;
        }
        // Every draw was inside the clearance (an implausibly large anchor). Fall back to the outer radius rather
        // than returning a point known to overlap.
        if (r <= clearanceAu)
        {
            r = MaxRadiusAu;
            x = anchor.PosX;
            y = anchor.PosY + r;
        }
        return (x, y, r * MetresPerAu / 1000.0);
    }

    // ---- identity ----

    /// <summary>
    /// Mint a RegID no ship in the target save is using. The shape follows the game's own
    /// (<c>&lt;letter&gt;-&lt;alphanumerics&gt;</c>), and the leading letter is inherited from
    /// <paramref name="likeRegId"/> when there is one: the game re-derives a ship's <c>origin</c> from the
    /// <c>TXTShipOrigin&lt;first letter&gt;</c> loot, so matching the neighbourhood's letter gives the granted
    /// ship a plausible origin instead of an off-region one.
    /// </summary>
    public static string MintRegId(IReadOnlyCollection<string> taken, string? likeRegId = null)
    {
        var prefix = likeRegId is { Length: > 0 } && char.IsLetter(likeRegId[0])
            ? char.ToUpperInvariant(likeRegId[0])
            : 'H';
        for (var attempt = 0; ; attempt++)
        {
            var body = Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
            var candidate = $"{prefix}-{body}";
            if (!taken.Contains(candidate)) return candidate;
            if (attempt > 1000)   // unreachable with 16^4 bodies against ~150 ships, but never spin forever
                throw new InvalidDataException("Could not mint a free ship registration for this save.");
        }
    }

    // ---- build ----

    /// <summary>
    /// Build the ship record for a grant. Pure: no file I/O, nothing read from or written to the save. The
    /// caller supplies the minted <paramref name="regId"/>, the <paramref name="anchor"/> read from the save, and
    /// the save's current game <paramref name="epoch"/> (stamped onto the device tickers so they fire on load).
    /// </summary>
    public static (JsonObject Ship, GrantReport Report) BuildShip(
        ShipDocument doc, Catalog catalog, IReadOnlyList<RoomSpecDef> specs,
        string regId, GrantAnchor anchor, GrantOptions opts, double epoch)
    {
        var warnings = new List<string>();

        // Wear is deliberately NOT handed to ShipExport. Its wear rides on each item's aCondOverrides, which is
        // the template-spawn mechanism; a save load takes a part's condition from its CO's aConds instead. Baking
        // it during CO synthesis below keeps one wear path for save-shaped output rather than two that could
        // disagree about what the ship's condition actually is.
        // Resolve the display name BEFORE building, through the same policy the mod export uses. ShipExport.Build
        // falls back to the strName it is handed when the metadata carries no public name, and a granted ship's
        // strName is its registration — so leaving the fallback to Build would name the ship "H-1234" in game.
        var publicName = ShipExport.ResolvePublicName(opts.Meta?.PublicName, opts.DesignName, isReplace: false);
        var meta = (opts.Meta ?? new ExportMetadata()) with { PublicName = publicName };

        // Only a transfer needs the id map back; an ordinary grant has nothing to trace a part to.
        var itemIdByPlacementId = opts.SourceCos is null ? null : new Dictionary<string, string>(StringComparer.Ordinal);
        var (exported, rating, roomCount) = ShipExport.Build(
            doc, catalog, specs, regId, warnings, meta, wear: null, itemIdByPlacementId);

        var ship = ShipExport.ToJsonObject(exported);

        // A save's own ships name themselves by their registration (verified against a real save: strName,
        // strRegID and strXPDR all read "B-A1R"), unlike a data/ships template, whose strName is the override key.
        ship["strName"] = regId;
        ship["strRegID"] = regId;
        ship["strXPDR"] = regId;

        // origin is left at ShipExport's "$TEMPLATE": Ship.InitShip re-rolls it from the TXTShipOrigin<letter>
        // loot on ANY load, template or save (the check sits outside the bTemplate branch), so the granted ship
        // picks up a real origin string rather than showing the sentinel.

        ship["publicName"] = publicName;

        var (x, y, distanceKm) = DrawSpawnPoint(anchor, opts.PlacementSeed is { } seed ? new Random(seed) : new Random());
        ship["objSS"] = BuildSitu(anchor, x, y, epoch);

        // Every item needs a CO on a save load. ShipExport already emitted them for contained cargo, so this
        // covers the top-level parts, and does it by "which ids are missing" rather than "which items look
        // top-level" so a future ShipExport change cannot leave a hole.
        var items = ship["aItems"] as JsonArray ?? [];
        var cos = ship["aCOs"] as JsonArray;
        if (cos is null) ship["aCOs"] = cos = new JsonArray();

        var haveCo = new HashSet<string>(StringComparer.Ordinal);
        foreach (var co in cos)
            if (Str(co, "strID") is { } id) haveCo.Add(id);

        var synthesized = new List<(JsonObject Co, PartDef Part)>();
        foreach (var item in items)
        {
            if (item is not JsonObject obj || Str(obj, "strID") is not { Length: > 0 } id || haveCo.Contains(id)) continue;
            if (Str(obj, "strName") is not { } defName) continue;
            var co = SaveEdit.SynthesizeCo(defName, id, catalog, regId, epoch);
            cos.Add(co);
            haveCo.Add(id);
            if (catalog.Lookup(defName) is { } part) synthesized.Add((co, part));
            MergeGpm(obj, catalog, defName);
        }

        var wornGrade = opts.SourceCos is { } sourceCos
            ? CarryCondition(doc, sourceCos, itemIdByPlacementId!, synthesized)
            : ApplyWear(opts.Wear, synthesized);
        if (ship["aRating"] is JsonArray ratingArr && ratingArr.Count > 1)
        {
            if (wornGrade is not null) ratingArr[1] = wornGrade;
            // Slot 0 is when the ship was last rated. A template export has no clock and writes "0"; a grant
            // does know the save's epoch, so stamp it and the ship reads as rated now rather than at time zero.
            if (epoch > 0) ratingArr[0] = epoch.ToString("R", CultureInfo.InvariantCulture);
        }

        // Mark the hull pristine and arm the atmosphere refill. bPrefill makes the game run its own PreFillRooms
        // once (the rooms this record carries are freshly generated, so their gas containers do not exist yet);
        // DMGStatus New stops that same pass also running the break-in damage roll over the wear baked above.
        ship["DMGStatus"] = 0;
        ship["bPrefill"] = true;
        ship["bBreakInUsed"] = false;

        Validate(ship);

        var report = new GrantReport(
            regId, publicName, items.Count, roomCount,
            wornGrade is null ? rating : rating with { Condition = wornGrade },
            distanceKm, warnings);
        return (ship, report);
    }

    /// <summary>
    /// The granted ship's <c>objSS</c>: parked at the drawn point, inheriting the anchor's reference body and
    /// velocity so it holds station with the neighbourhood instead of drifting away from it.
    ///
    /// <para><c>bIsBO</c> stays false (that flag is what makes a ship a station, and it is also what
    /// <c>CondOwner.ClaimShip</c> refuses to claim for a player CO). <c>bGrounded</c> stays false because the ship
    /// is not docked to anything, and the dock offsets are cleared for the same reason. <c>size</c> is left at 0:
    /// <c>Ship.InitShip</c> recomputes it from the floor plan for every ship on load.</para>
    ///
    /// <para><b>The path history is not optional.</b> Every other collection on a ship is created in
    /// <c>Ship</c>'s constructor and merely <i>replaced</i> when the save carries one, so omitting it is safe.
    /// <c>ShipSitu.aPathRecent</c> is the exception: the <c>ShipSitu(JsonShipSitu)</c> constructor does not chain
    /// to the one that calls <c>InitPath()</c>, so the list is built <b>only</b> when <c>aPathRecentX</c> is
    /// present. <c>StarSystem.UpdateShip</c> then ends with an unguarded <c>objSS.aPathRecent.Count</c>, which
    /// throws every frame for a ship without one — and because that exception escapes <c>StarSystem.Update</c>,
    /// it takes the entire simulation down with it, not just the offending ship. So seed one entry: where the
    /// ship is, at the save's own clock, exactly what <c>ShipSitu.LogPath</c> would have written.</para>
    /// </summary>
    private static JsonObject BuildSitu(GrantAnchor anchor, double x, double y, double epoch) => new()
    {
        ["aPathRecentT"] = new JsonArray(epoch),
        ["aPathRecentX"] = new JsonArray(x),
        ["aPathRecentY"] = new JsonArray(y),
        ["boPORShip"] = anchor.BoPorShip,
        ["vPosx"] = x,
        ["vPosy"] = y,
        ["vBOOffsetx"] = 0.0,
        ["vBOOffsety"] = 0.0,
        ["vVelX"] = anchor.VelX,
        ["vVelY"] = anchor.VelY,
        ["fPathLastEpoch"] = 0.0,
        // The acceleration vectors are Unity structs, so the loader defaults them safely when absent. Written
        // anyway so the record matches the shape the game itself saves, field for field.
        ["vAccIn"] = Vec2Zero(),
        ["vAccRCS"] = Vec2Zero(),
        ["vAccEx"] = Vec2Zero(),
        ["vAccLift"] = Vec2Zero(),
        ["vAccDrag"] = Vec2Zero(),
        ["fRot"] = 0.0,
        ["fW"] = 0.0,
        ["fA"] = 0.0,
        ["bBOLocked"] = anchor.BoLocked,
        ["bOrbitLocked"] = false,
        ["bIsBO"] = false,
        ["bIsRegion"] = false,
        ["bIsNoFees"] = true,
        ["bGrounded"] = false,
        ["size"] = 0,
        ["bIgnoreGrav"] = false,
        ["fDockOffsetPosX"] = 0.0,
        ["fDockOffsetPosY"] = 0.0,
        ["fDockOffsetRot"] = 0.0,
    };

    private static JsonObject Vec2Zero() => new() { ["x"] = 0.0, ["y"] = 0.0 };

    /// <summary>
    /// Bake the def's GUI-prop-map panels onto a newly-synthesized item, <b>under</b> anything already there.
    /// A device wired in the design already carries an <c>Electrical</c> panel holding its signal connections
    /// (<see cref="ShipExport"/>'s device links); replacing it with the def's empty one would drop the wiring, so
    /// panels already present win by name and the def only fills in the rest.
    /// </summary>
    private static void MergeGpm(JsonObject item, Catalog catalog, string defName)
    {
        if (SaveEdit.GpmSettings(catalog, defName) is not { } fromDef) return;

        if (item["aGPMSettings"] is not JsonArray existing || existing.Count == 0)
        {
            item["aGPMSettings"] = fromDef;
            return;
        }

        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var panel in existing)
            if (Str(panel, "strName") is { } name) present.Add(name);

        foreach (var panel in fromDef.ToList())
        {
            if (Str(panel, "strName") is { } name && present.Contains(name)) continue;
            existing.Add(panel!.DeepClone());
        }
    }

    /// <summary>
    /// Re-roll <c>StatDamage</c> on every installed structural part, the way the game stores per-part wear, and
    /// return the resulting Ship-Rating Condition grade. Mirrors <see cref="SaveEdit"/>'s pass: <c>IsSystem</c>
    /// and undamageable parts stay pristine but still count in the mean, so the grade matches the game's
    /// all-installed-parts denominator. Null when wear is off, leaving the ship pristine.
    ///
    /// <para>A repair pass (<see cref="WearOptions.IsRepair"/>) grades A without touching anything: a granted ship's
    /// condition owners are minted fresh from their defs, and no def declares <c>StatDamage</c>, so there is nothing
    /// here to clear. It is still worth answering rather than falling through to the roll, so the baked rating says
    /// A for the same reason the update path's does.</para>
    /// </summary>
    private static string? ApplyWear(WearOptions? wear, IReadOnlyList<(JsonObject Co, PartDef Part)> parts)
    {
        if (wear is not { Enabled: true } w) return null;
        if (w.IsRepair) return Rating.ConditionGrade(1.0);
        var rng = WearModel.NewRng(w);
        var ceiling = WearModel.CeilingFor(w.TargetCondition);
        var rates = new List<double>();
        foreach (var (co, part) in parts)
        {
            if (!part.StartingConds.Contains("IsInstalled")) continue;   // cargo and loose kit are not "the ship's condition"
            if (part.StartingConds.Contains("IsSystem")) { rates.Add(0.0); continue; }
            var damageMax = part.StartingCondValues.GetValueOrDefault("StatDamageMax");
            if (damageMax <= 0) { rates.Add(0.0); continue; }
            var dmg = WearModel.DamageAmount(rng, ceiling, damageMax);
            SaveEdit.SetStatDamage(co, dmg);
            rates.Add(dmg / damageMax);
        }
        return WearModel.GradeFor(rates);
    }

    /// <summary>
    /// Carry each part's real <c>StatDamage</c> across from the ship this design was imported from, and return the
    /// resulting Ship-Rating Condition grade — the transfer counterpart of <see cref="ApplyWear"/>.
    ///
    /// <para>Every item in a granted ship is minted fresh, so the route back to a source part is
    /// design placement → its <see cref="Placement.OriginStrID"/>. A part the user added after the import has no
    /// origin and stays pristine, which is right: it was never on the source ship to be worn.</para>
    ///
    /// <para>The denominator matches the game's (<c>Ship.CalculateRating</c> means over <b>every</b> installed
    /// part), so an undamageable or system part still contributes a 0 rate rather than being left out and
    /// flattering the grade.</para>
    /// </summary>
    private static string? CarryCondition(
        ShipDocument doc, IReadOnlyDictionary<string, JsonNode> sourceCos,
        IReadOnlyDictionary<string, string> itemIdByPlacementId,
        IReadOnlyList<(JsonObject Co, PartDef Part)> parts)
    {
        // new item strID -> the save item it came from
        var originByItemId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in doc.Placements)
            if (p.OriginStrID is { } origin && itemIdByPlacementId.TryGetValue(p.Id.ToString(), out var itemId))
                originByItemId[itemId] = origin;

        var rates = new List<double>();
        foreach (var (co, part) in parts)
        {
            if (!part.StartingConds.Contains("IsInstalled")) continue;   // cargo and loose kit are not "the ship's condition"
            var damageMax = part.StartingCondValues.GetValueOrDefault("StatDamageMax");
            if (part.StartingConds.Contains("IsSystem") || damageMax <= 0) { rates.Add(0.0); continue; }

            var damage = Str(co, "strID") is { } id && originByItemId.TryGetValue(id, out var origin)
                         && sourceCos.TryGetValue(origin, out var src)
                ? Math.Clamp(StatDamage(src), 0, damageMax)
                : 0.0;   // added since the import, so it really is a new part
            SaveEdit.SetStatDamage(co, damage);
            rates.Add(damage / damageMax);
        }
        return WearModel.GradeFor(rates);
    }

    /// <summary>A condition owner's accumulated <c>StatDamage</c>, or 0 when it carries none (an undamaged part
    /// has no such cond at all rather than one reading zero).</summary>
    private static double StatDamage(JsonNode co)
    {
        foreach (var c in (co as JsonObject)?["aConds"] as JsonArray ?? [])
            if (c is JsonValue v && v.TryGetValue<string>(out var s)
                && s.StartsWith("StatDamage=", StringComparison.Ordinal))
                return LootDef.CondAmount(s);
        return 0.0;
    }

    /// <summary>The invariants a save load enforces, checked before anything reaches a file. Throws to abort the
    /// whole grant: a ship that trips one of these loads as a partial wreck rather than failing loudly.</summary>
    private static void Validate(JsonObject ship)
    {
        var itemIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in ship["aItems"] as JsonArray ?? [])
            if (Str(item, "strID") is { Length: > 0 } id && !itemIds.Add(id))
                throw new InvalidDataException($"Two items share strID '{id}' — grant aborted.");

        var coIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var co in ship["aCOs"] as JsonArray ?? [])
            if (Str(co, "strID") is { } id) coIds.Add(id);

        foreach (var item in ship["aItems"] as JsonArray ?? [])
        {
            var id = Str(item, "strID");
            if (id is { Length: > 0 } && !coIds.Contains(id))
                throw new InvalidDataException(
                    $"Item '{id}' ({Str(item, "strName")}) has no condition owner — the game would skip it on load. Grant aborted.");

            var parent = Str(item, "strParentID") ?? Str(item, "strSlotParentID");
            if (parent is { Length: > 0 } && !itemIds.Contains(parent))
                throw new InvalidDataException($"Item '{id}' is parented to missing '{parent}' — grant aborted.");
        }
    }

    private static string? Str(JsonNode? n, string prop) =>
        (n as JsonObject)?[prop] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // ---- reading the target save ----

    /// <summary>
    /// Read everything a grant needs from a save, without writing anything. Throws
    /// <see cref="InvalidDataException"/> with a message the UI can show when the save cannot take a grant at
    /// all: no session record, no player CO, or a player CO that is not on the ship they are standing on (which
    /// would leave the ownership half of a grant unwritable).
    /// </summary>
    public static GrantContext ReadContext(SaveEntry save)
    {
        using var zip = ZipFile.OpenRead(save.ZipPath);

        var session = SaveImport.ReadSession(zip)
            ?? throw new InvalidDataException("Couldn't find the player's character record in this save.");
        if (session.PlayerCoId is not { Length: > 0 } coId)
            throw new InvalidDataException("This save's character record names no player, so a ship can't be registered to them.");

        var shipEntry = zip.GetEntry(SaveZip.ShipEntry(session.ShipRegId))
            ?? throw new InvalidDataException($"The ship the player is on ('{session.ShipRegId}') is not among this save's ships.");
        var record = LargestShip(JsonNode.Parse(SaveImport.ReadText(shipEntry)))
            ?? throw new InvalidDataException($"The ship the player is on ('{session.ShipRegId}') has no readable record.");

        // The player CO has to be in this record: it is where aMyShips and the money balance live, and a grant
        // that cannot write those produces a ship the player does not own and their crew will not work on.
        var playerCo = FindCo(record, coId)
            ?? throw new InvalidDataException(
                "The player's character wasn't found on the ship they're standing on, so ownership can't be written. " +
                "Load the save in game, then save again from aboard your own ship.");

        // Decoded, because the file name is not the RegID: the game substitutes '|' and '*' on write
        // (SaveZip), so an apartment's BCRS|RES_1 is stored as BCRS%RES_1 and a raw read would let a mint
        // collide with a registration that is already taken.
        var regIds = zip.Entries
            .Where(e => e.FullName.StartsWith("ships/", StringComparison.Ordinal)
                && e.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Select(e => SaveZip.DecodeName(Path.GetFileNameWithoutExtension(e.FullName)))
            .ToHashSet(StringComparer.Ordinal);

        return new GrantContext
        {
            SaveName = save.Name,
            ZipPath = save.ZipPath,
            PlayerShipRegId = session.ShipRegId,
            PlayerCoId = coId,
            Epoch = session.Epoch,
            Anchor = GrantAnchor.FromShipRecord(record),
            ExistingRegIds = regIds,
            PlayerShipRecord = record,
            SessionEntryName = session.EntryName,
            Balance = SumStatUsd(playerCo),
        };
    }

    // ---- writing ----

    /// <summary>
    /// Build the design into <paramref name="ctx"/>'s save and write the result to a <b>copy</b> of it. The
    /// original save folder is never opened for writing. Returns where the copy landed.
    ///
    /// <para><paramref name="price"/> is deducted from the player's balance (0 = a gift). The caller is expected
    /// to have checked affordability; an unaffordable price is refused here rather than writing a negative
    /// balance.</para>
    ///
    /// <para>This is mint + <see cref="BuildShip"/> + <see cref="WriteGrant"/> in one call, for a caller that has
    /// nothing to show between the build and the write. A caller that reports the result <b>before</b> committing
    /// (the export wizard's Review step) must run the three itself, or the registration it displayed would not be
    /// the one written: <see cref="MintRegId"/> draws from <see cref="Guid"/> and cannot be seeded.</para>
    /// </summary>
    public static (string OutputDir, GrantReport Report) Grant(
        GrantContext ctx, ShipDocument doc, Catalog catalog, IReadOnlyList<RoomSpecDef> specs,
        GrantOptions opts, double price = 0, string? outputSaveDir = null, bool overwrite = false)
    {
        CheckPrice(ctx, price);   // refuse before the build, not after it
        var regId = MintRegId(ctx.ExistingRegIds, ctx.PlayerShipRegId);
        var (ship, report) = BuildShip(doc, catalog, specs, regId, ctx.Anchor, opts, ctx.Epoch);
        return WriteGrant(ctx, regId, ship, report, price, outputSaveDir, overwrite);
    }

    /// <summary>
    /// Write an already-built grant (<see cref="BuildShip"/>'s output) into a <b>copy</b> of
    /// <paramref name="ctx"/>'s save. The original save folder is never opened for writing. Returns where the copy
    /// landed, and the report restated with what was actually charged.
    ///
    /// <para><paramref name="price"/> is deducted from the player's balance (0 = a gift). An unaffordable price is
    /// refused here rather than writing a negative balance.</para>
    ///
    /// <para><b>One shot.</b> This mutates <paramref name="ctx"/>'s player ship record: the CO claims the ship and
    /// takes the deduction. Calling it twice against the same context would claim twice and charge twice, so read a
    /// fresh <see cref="GrantContext"/> for each write.</para>
    /// </summary>
    public static (string OutputDir, GrantReport Report) WriteGrant(
        GrantContext ctx, string regId, JsonObject ship, GrantReport report,
        double price = 0, string? outputSaveDir = null, bool overwrite = false)
    {
        CheckPrice(ctx, price);

        // The owning half of the grant. dictShipOwners (written into the session record below) is what the ferry
        // and the broker read; aMyShips is what CondOwner.OwnsShip reads, which gates crew pledges, bTargetOwned
        // interactions and fast-forward. A ship with only the first is reachable but your crew won't work on it.
        var playerCo = FindCo(ctx.PlayerShipRecord, ctx.PlayerCoId)
            ?? throw new InvalidDataException("The player's character owner disappeared from the record — grant aborted.");
        ClaimShip(playerCo, regId);

        double? newBalance = null;
        if (price > 0)
        {
            newBalance = ctx.Balance - price;
            SaveEdit.SetStatUsd(playerCo, newBalance.Value);
        }

        var srcDir = Path.GetDirectoryName(ctx.ZipPath)!;
        var outDir = outputSaveDir ?? SuggestCopyDir(srcDir);
        if (Directory.Exists(outDir))
        {
            if (!overwrite) throw new IOException($"'{Path.GetFileName(outDir)}' already exists.");
            Directory.Delete(outDir, recursive: true);
        }

        var targetZip = SaveEdit.MaterializeCopy(srcDir, Path.GetFileName(ctx.ZipPath), outDir, newBalance);
        WriteIntoCopy(targetZip, ctx, regId, ship);

        return (outDir, report with { Charged = price > 0 ? price : null, ResultingBalance = newBalance });
    }

    /// <summary>A grant is never allowed to overdraw the player. Checked at both entry points, so
    /// <see cref="Grant"/> refuses before it pays for a build it is going to throw away.</summary>
    private static void CheckPrice(GrantContext ctx, double price)
    {
        if (price < 0) throw new ArgumentOutOfRangeException(nameof(price), "A grant's price cannot be negative.");
        if (price > ctx.Balance)
            throw new InvalidDataException(
                $"That costs more than the player has ({price:0.##} against {ctx.Balance:0.##}). Lower the price or make it a gift.");
    }

    /// <summary>
    /// Apply the grant's three edits to the copied archive: add the new ship, replace the player's ship record
    /// (which now claims the ship and carries the deducted balance), and register the ownership in the session
    /// record. Ordered so the session record — the big one — is touched last.
    /// </summary>
    private static void WriteIntoCopy(string targetZip, GrantContext ctx, string regId, JsonObject ship)
    {
        using var za = ZipFile.Open(targetZip, ZipArchiveMode.Update);

        // The new ship. A save's ship files are top-level arrays, the same shape a data/ships file uses.
        var entry = za.CreateEntry(SaveZip.ShipEntry(regId));
        using (var w = new StreamWriter(entry.Open()))
            w.Write(new JsonArray(ship.DeepClone()).ToJsonString(Indented));

        ReplaceEntry(za, SaveZip.ShipEntry(ctx.PlayerShipRegId), (r, w) =>
        {
            var spliced = SpliceShip(JsonNode.Parse(r.ReadToEnd()), ctx.PlayerShipRecord);
            w.Write(spliced.ToJsonString(Indented));
        });

        ReplaceEntry(za, ctx.SessionEntryName, (r, w) =>
        {
            if (!InsertShipOwner(r, w, regId, ctx.PlayerCoId))
                throw new InvalidDataException(
                    "This save's character record has no ship-owner registry (dictShipOwners), so the ship can't be " +
                    "registered to the player. Grant aborted.");
        });
    }

    /// <summary>Rewrite one zip entry through a streaming transform. The reader and writer are line-oriented, so
    /// a transform never has to hold the whole entry: the session record alone runs to tens of MB.</summary>
    private static void ReplaceEntry(ZipArchive za, string entryName, Action<TextReader, TextWriter> transform)
    {
        var entry = za.GetEntry(entryName)
            ?? throw new InvalidDataException($"'{entryName}' is not in the save.");

        var buffer = new MemoryStream();
        using (var reader = new StreamReader(entry.Open()))
        using (var writer = new StreamWriter(buffer, leaveOpen: true))
            transform(reader, writer);

        entry.Delete();
        buffer.Position = 0;
        using var dest = za.CreateEntry(entryName).Open();
        buffer.CopyTo(dest);
    }

    /// <summary>
    /// Add the pair <c>(regId, ownerCoId)</c> to the session record's <c>objSystem.dictShipOwners</c>, streaming
    /// the record through line by line rather than parsing it.
    ///
    /// <para>That registry is the <b>only</b> place ship ownership exists: a <c>JsonShip</c> has no owner field,
    /// and <c>StarSystem.GetShipOwner</c> returns the literal "UNREGISTERED" for a RegID it does not hold. The
    /// game serialises the dictionary as a flat alternating <c>[key, value, key, value, …]</c> array
    /// (<c>DataHandler.ConvertStringArrayToDict</c> reads it back in pairs), so one pair is two entries.</para>
    ///
    /// <para>It is done textually on purpose. The record is the largest thing in a save, and round-tripping it
    /// through a parser would rewrite every number in the file to whatever the serialiser's formatting produces,
    /// which is a large blast radius for adding two strings. Returns false when the key is absent, having written
    /// the record through unchanged.</para>
    /// </summary>
    internal static bool InsertShipOwner(TextReader src, TextWriter dst, string regId, string ownerCoId)
    {
        const string key = "\"dictShipOwners\"";
        var inserted = false;
        var line = src.ReadLine();

        while (line is not null)
        {
            var next = src.ReadLine();

            if (!inserted && line.Contains(key, StringComparison.Ordinal) && line.IndexOf('[') is var open and >= 0)
            {
                var tail = line[(open + 1)..];
                if (tail.TrimStart().StartsWith(']'))
                {
                    // an empty registry written inline: "dictShipOwners" : [],
                    dst.WriteLine($"{line[..(open + 1)]} \"{regId}\", \"{ownerCoId}\" {tail.TrimStart()}");
                }
                else
                {
                    // the usual shape: the array opens here and its entries follow, one per line. A trailing comma
                    // is only valid when an entry follows ours, so the closing bracket on the next line means none.
                    var indent = Indent(next) ?? Indent(line) + "  ";
                    var closes = next?.TrimStart().StartsWith(']') ?? true;
                    dst.WriteLine(line);
                    dst.WriteLine($"{indent}\"{regId}\",");
                    dst.WriteLine($"{indent}\"{ownerCoId}\"{(closes ? "" : ",")}");
                }
                inserted = true;
            }
            else
            {
                dst.WriteLine(line);
            }

            line = next;
        }
        return inserted;
    }

    /// <summary>The leading whitespace of <paramref name="line"/>, or null when there is no line.</summary>
    private static string? Indent(string? line) =>
        line is null ? null : line[..(line.Length - line.TrimStart().Length)];

    // ---- save-record helpers ----

    /// <summary>Add a RegID to a CO's <c>aMyShips</c> (the game's <c>CondOwner.ClaimShip</c>), creating the array
    /// when the player has never owned a ship. Idempotent.</summary>
    private static void ClaimShip(JsonObject co, string regId)
    {
        if (co["aMyShips"] is not JsonArray mine) co["aMyShips"] = mine = new JsonArray();
        foreach (var n in mine)
            if (n is JsonValue v && v.TryGetValue<string>(out var s) && s == regId) return;
        mine.Add(regId);
    }

    /// <summary>The CO with <paramref name="coId"/> in a ship record's <c>aCOs</c>, or null.</summary>
    private static JsonObject? FindCo(JsonNode shipRecord, string coId)
    {
        foreach (var co in (shipRecord as JsonObject)?["aCOs"] as JsonArray ?? [])
            if (co is JsonObject o && Str(o, "strID") == coId) return o;
        return null;
    }

    /// <summary>Sum a CO's <c>StatUSD</c> conds — the game accumulates them, so the balance is the total.</summary>
    private static double SumStatUsd(JsonObject co)
    {
        double sum = 0;
        if (co["aConds"] is JsonArray conds)
            foreach (var n in conds)
                if (n is JsonValue v && v.TryGetValue<string>(out var s) && s.StartsWith("StatUSD=", StringComparison.Ordinal))
                    sum += LootDef.CondAmount(s);
        return sum;
    }

    /// <summary>The ship object with the most items — a save's ship file is one ship or an array of them.</summary>
    private static JsonNode? LargestShip(JsonNode? node) => node switch
    {
        JsonArray arr => arr.OfType<JsonObject>()
            .Where(o => o["nCols"] is not null && o["aItems"] is JsonArray)
            .OrderByDescending(o => (o["aItems"] as JsonArray)?.Count ?? 0)
            .FirstOrDefault(),
        JsonObject obj when obj["nCols"] is not null && obj["aItems"] is JsonArray => obj,
        _ => null,
    };

    /// <summary>Put <paramref name="ship"/> back into the file's original shape, so a file holding sibling ships
    /// keeps them (mirrors <see cref="SaveEdit"/>'s splice).</summary>
    private static JsonNode SpliceShip(JsonNode? top, JsonNode ship)
    {
        if (top is JsonArray arr)
        {
            int idx = -1, best = -1;
            for (var i = 0; i < arr.Count; i++)
                if (arr[i] is JsonObject o && o["aItems"] is JsonArray a && a.Count > best) { best = a.Count; idx = i; }
            var clone = ship.DeepClone();
            if (idx >= 0) arr[idx] = clone; else arr.Add(clone);
            return arr;
        }
        return ship.DeepClone();
    }

    /// <summary>A fresh, non-colliding sibling folder for the copy of <paramref name="ctx"/>'s save, matching the
    /// naming <see cref="SaveEdit.SuggestCopyDir"/> uses so both save-writing paths produce the same kind of save.
    /// Public so a caller that names the copy before writing it (the export wizard's Review step) names the one
    /// that will actually be used.</summary>
    public static string SuggestCopyDir(GrantContext ctx) => SuggestCopyDir(Path.GetDirectoryName(ctx.ZipPath)!);

    private static string SuggestCopyDir(string srcDir)
    {
        var parent = Path.GetDirectoryName(srcDir)!;
        var baseName = System.Text.RegularExpressions.Regex
            .Replace(new DirectoryInfo(srcDir).Name, @"\s*\(Ostraplan(?:\s+\d+)?\)\s*$", "")
            .TrimEnd();
        var candidate = Path.Combine(parent, $"{baseName} (Ostraplan)");
        for (var n = 2; Directory.Exists(candidate); n++)
            candidate = Path.Combine(parent, $"{baseName} (Ostraplan {n})");
        return candidate;
    }

    private static readonly System.Text.Json.JsonSerializerOptions Indented = new() { WriteIndented = true };
}
