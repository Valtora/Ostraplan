using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ostraplan.Core;

/// <summary>
/// A station in a target save that a designed residence could be attached to: the registration the apartment's
/// own RegID is built from, a name to show, the situation to copy, and whether the game has a transit route to
/// residences there.
/// </summary>
/// <param name="RegId">The station's registration — the half of <c>&lt;STATION&gt;|RES_&lt;n&gt;</c> before the pipe.</param>
/// <param name="DisplayName">The station's <c>publicName</c>, or its RegID when it has none.</param>
/// <param name="Anchor">The station's own <c>objSS</c>. A residence does not park <i>near</i> its station the way
/// a granted ship parks near the player; it shares the station's coordinates exactly, the way a docked ship
/// does, and inherits its body-orbit lock.</param>
/// <param name="HasTransitRoute">Whether game data defines a <c>&lt;RegId&gt;|</c> transit node. Without one the
/// apartment exists and is owned but nothing can reach it — see <see cref="ResidenceGrant"/>.</param>
/// <param name="IsPlayerLocation">Whether this is the station the player is standing on. The list itself is
/// alphabetical, so this is what <see cref="ResidenceGrant.Preferred"/> reads to open on a sensible one.</param>
public sealed record ResidenceStation(
    string RegId, string DisplayName, GrantAnchor Anchor, bool HasTransitRoute, bool IsPlayerLocation = false)
{
    /// <summary>The transit node a residence at this station resolves to, which is the station's RegID with the
    /// pipe kept: <c>DataHandler.GetTransitConnections</c> truncates at and <b>including</b> it.</summary>
    public string TransitNodeName => RegId + "|";
}

/// <summary>
/// Putting a <b>designed residence</b> into a save, as opposed to <see cref="SaveGrant"/>'s vessel. The record
/// itself is built by the same builder; what differs is everything around it, and each difference is the game's,
/// not a preference:
///
/// <list type="bullet">
/// <item><b>The registration carries a pipe.</b> <c>&lt;STATION&gt;|RES_&lt;n&gt;</c>, <c>n</c> being the first
/// free index. <c>Ship.InitShip</c> keys sub-station behaviour off that pipe alone — it sets
/// <c>HideFromSystem</c> and <c>_subStation</c> for any RegID containing one — and
/// <c>DataHandler.GetTransitConnections</c> truncates at it to find the transit node. A residence without a pipe
/// is a ship parked inside a station.</item>
/// <item><b>It is placed on the station, not near it.</b> The broker copies the station's coordinates and calls
/// <c>LockToBO</c> on the station's body orbit, so there is no spawn draw and no separation to report.</item>
/// <item><b>Only one ownership registry is written.</b> <c>CondOwner.ClaimShip</c> early-returns for an
/// <c>IsPlayer</c> CO when the ship is a station, so the game's own purchase path leaves <c>aMyShips</c>
/// untouched and registers in <c>dictShipOwners</c> alone. Writing <c>aMyShips</c> anyway would produce a save
/// state the game never creates.</item>
/// <item><b>The buyer becomes a homeowner.</b> Purchase applies the broker's <c>strLootResidence</c>, granting
/// <c>IsHomeowner&lt;STATION&gt;</c>, and that cond is what the transit connection's <c>ctUserOptional</c> gate
/// reads. Without it the apartment is owned and unreachable.</item>
/// </list>
///
/// <para>See GAME-INTERNALS §19. <b>Reconstructed from <c>GUIShipBroker.OnPurchaseConfirm</c> and the save
/// writer rather than from an observed purchased apartment</b>, which is the part of this worth re-checking
/// first if an in-game test disagrees.</para>
/// </summary>
public static class ResidenceGrant
{
    /// <summary>The multiplier a Real Estate broker applies to the summed room values
    /// (<c>GUIShipBroker.SetupApartments</c>: <c>sum(aRooms[].roomValue) × discount × 10</c>).</summary>
    public const double PriceMultiplier = 10.0;

    /// <summary>The registration infix between the station and the index.</summary>
    private const string RegIdInfix = "|RES_";

    // ---- choosing a station ----

    /// <summary>
    /// The stations in <paramref name="zipPath"/>'s save a residence could be attached to, in alphabetical order —
    /// a list of twenty-odd is read by looking a name up in it, not by trusting its ranking. Which one to open on
    /// is a separate question, and <see cref="Preferred"/> answers it.
    ///
    /// <para>A host is what <c>GUIShipBroker.SetupApartments</c> would resolve to, which is
    /// <c>GetNearestStation(…, excludeOutposts: true)</c> and therefore <see cref="IsFullStation"/>: docking ports,
    /// <c>bIsBO</c>, a classification no higher than <c>GroundStationUnfinished</c>, and no pipe in the RegID. A
    /// residential module such as <c>OKLG_RES</c> or <c>BCRS_RES</c> fails the port test, which is precisely why
    /// the game never builds a registration from one. That is also what the 0.15.0.x save migration did by hand,
    /// rewriting <c>BCRS_RES|RES…</c> to <c>BCRS|RES…</c>.</para>
    ///
    /// <para>A ship the data already routes to — one with a <c>&lt;RegID&gt;|</c> transit node — is kept whatever
    /// the port test says. Vanilla never exercises that branch (all eight routed stations have ports), but a mod
    /// is free to hang a route off a portless module, and our filter should not be the thing that forbids it.</para>
    /// </summary>
    public static IReadOnlyList<ResidenceStation> ListStations(string zipPath, DataIndex index)
    {
        var transitNodes = TransitNodes(index);
        var stations = new List<ResidenceStation>();

        using var zip = ZipFile.OpenRead(zipPath);
        var playerShipReg = SaveImport.PlayerShipRegId(zip);

        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith("ships/", StringComparison.Ordinal)
                || !entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

            JsonNode? record;
            try { record = JsonNode.Parse(SaveImport.ReadText(entry)); }
            catch { continue; }   // an unreadable ship record is one fewer station, never a failed listing

            if (ShipJson.Largest(record) is not JsonObject ship) continue;
            var regId = Str(ship, "strRegID") ?? SaveZip.DecodeName(Path.GetFileNameWithoutExtension(entry.FullName));
            if (regId.Length == 0 || SaveZip.IsSubStation(regId)) continue;

            var routed = transitNodes.Contains(regId + "|");
            if (!IsFullStation(ship) && !routed) continue;

            var name = Str(ship, "publicName") is { Length: > 0 } p && p != "$TEMPLATE" ? p : regId;
            stations.Add(new ResidenceStation(
                regId, name, GrantAnchor.FromShipRecord(ship), routed, regId == playerShipReg));
        }

        return [.. stations.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Which of <paramref name="stations"/> to open a picker on: where the player is standing, else one the game
    /// can actually reach, else whatever is first. Null only when the list is empty.
    ///
    /// <para>This is separate from the list's order on purpose. Sorting the offer by usefulness makes a
    /// twenty-entry list unsearchable, but defaulting to whatever happens to sort first would put a station with
    /// no residence transit route under the cursor, and accepting that default is exactly how an apartment ends up
    /// owned and unreachable.</para>
    /// </summary>
    public static ResidenceStation? Preferred(IReadOnlyList<ResidenceStation> stations) =>
        stations.FirstOrDefault(s => s.IsPlayerLocation)
        ?? stations.FirstOrDefault(s => s.HasTransitRoute)
        ?? stations.FirstOrDefault();

    /// <summary>Every transit node name in the effective (mod-resolved) data. Read from the index rather than
    /// hard-coded so a mod that adds a station's residence route is seen.</summary>
    private static HashSet<string> TransitNodes(DataIndex index) =>
        [.. index.Type("transit").Keys];

    /// <summary>The highest <c>Ship.TypeClassification</c> that still counts as a whole station:
    /// <c>GroundStationUnfinished</c> (4). <c>Ship.IsNotAFullStation</c> is <c>Classification &gt; </c> this, which
    /// is what <c>GetNearestStation</c>'s <c>excludeOutposts</c> rejects — buoys (5), outposts (6), waypoints,
    /// projectiles and the rest.</summary>
    private const int LastFullStationType = 4;

    /// <summary>
    /// <c>Ship.IsStation() &amp;&amp; !IsNotAFullStation</c>, read off a saved record. This is the test
    /// <c>GetNearestStation(…, excludeOutposts: true)</c> applies, and so the test that decides which registration
    /// a bought apartment is built from.
    ///
    /// <para>The docking-port half is the one that matters in practice. <c>Ship.HasDockingPorts</c> is true when
    /// <c>aDockingPorts</c> holds any entry not prefixed <c>"MP|"</c> (a mooring point is not a dock), and every
    /// stock residential module — <c>OKLG_RES</c>, <c>BCRS_RES</c>, <c>BCER_ROOF</c>, <c>MSUZ_RB</c> — carries
    /// <c>bIsBO</c> with no ports at all. Judging on <c>bIsBO</c> alone is <c>IsStationHidden</c>, not
    /// <c>IsStation</c>, and it is what let a residence be minted as <c>OKLG_RES|RES_1</c>: a registration no
    /// transit node truncates to, so <c>GetConnectionsForKiosk</c> found no ship matching its <c>OKLG|</c>
    /// wildcard and fell back to a <c>TIsDead</c>-gated placeholder row.</para>
    /// </summary>
    private static bool IsFullStation(JsonObject ship)
    {
        if (ship["objSS"] is not JsonObject situ || situ["bIsBO"]?.GetValue<bool>() != true) return false;
        if (ship["ShipType"] is JsonValue t && t.TryGetValue<int>(out var type) && type > LastFullStationType)
            return false;

        foreach (var port in ship["aDockingPorts"] as JsonArray ?? [])
            if (port is JsonValue v && v.TryGetValue<string>(out var id)
                && !id.StartsWith("MP|", StringComparison.Ordinal)) return true;
        return false;
    }

    // ---- the registration ----

    /// <summary>
    /// The next free <c>&lt;STATION&gt;|RES_&lt;n&gt;</c> at <paramref name="stationRegId"/>, counting from 1 the
    /// way <c>GUIShipBroker.SetupApartments</c> does. Unlike a vessel's registration this is not drawn at random,
    /// so it is deterministic and can be shown before it is written.
    /// </summary>
    public static string MintRegId(IReadOnlyCollection<string> taken, string stationRegId)
    {
        if (stationRegId.Length == 0 || stationRegId.Contains('|'))
            throw new ArgumentException(
                $"'{stationRegId}' cannot host a residence: a station registration must be non-empty and carry no pipe.",
                nameof(stationRegId));

        for (var n = 1; n <= 10_000; n++)
        {
            var candidate = $"{stationRegId}{RegIdInfix}{n}";
            if (!taken.Contains(candidate)) return candidate;
        }
        throw new InvalidDataException($"Could not find a free residence registration at '{stationRegId}'.");
    }

    // ---- the price ----

    /// <summary>
    /// What a Real Estate broker would charge for this record: the summed <b>baked</b> room values ×10. Read off
    /// the built record's own <c>aRooms</c> rather than recomputed, because those are the exact numbers the game
    /// reads (<c>GUIShipBroker.SetupApartments</c> sums <c>jsonShip.aRooms[].roomValue</c> straight off the
    /// record, with no O2 multiplier and no re-derivation). Excludes the per-kiosk discount, which is a property
    /// of a broker and not of the design.
    /// </summary>
    public static double Price(JsonObject ship)
    {
        double sum = 0;
        foreach (var room in ship["aRooms"] as JsonArray ?? [])
            if (room?["roomValue"] is JsonValue v && v.TryGetValue<double>(out var d)) sum += d;
        return sum * PriceMultiplier;
    }

    // ---- the situation ----

    /// <summary>
    /// A residence's <c>objSS</c>: the station's own position, its body orbit, and the two flags that make the
    /// game treat the record as a station rather than a vessel.
    ///
    /// <para><c>bIsBO</c> is what <c>Ship.IsStation</c> reads, and <c>bBOLocked</c> plus <c>boPORShip</c> are
    /// what <c>ShipSitu.LockToBO</c> sets. The offsets are copied from the station rather than zeroed: they are
    /// the station's own displacement from its body, and a residence that shares the station's absolute position
    /// must share its offset too or the two would separate the moment the body moved.</para>
    ///
    /// <para><c>aPathRecent</c> is seeded for the same non-negotiable reason a granted ship's is: without
    /// <c>aPathRecentX</c> the list is never built and <c>StarSystem.UpdateShip</c> throws every frame, taking
    /// the whole simulation with it (GAME-INTERNALS §19).</para>
    /// </summary>
    internal static JsonObject BuildSitu(ResidenceStation station, double epoch)
    {
        var a = station.Anchor;
        return new JsonObject
        {
            ["aPathRecentT"] = new JsonArray(epoch),
            ["aPathRecentX"] = new JsonArray(a.PosX),
            ["aPathRecentY"] = new JsonArray(a.PosY),
            ["boPORShip"] = a.BoPorShip,
            ["vPosx"] = a.PosX,
            ["vPosy"] = a.PosY,
            ["vBOOffsetx"] = a.BoOffsetX,
            ["vBOOffsety"] = a.BoOffsetY,
            ["vVelX"] = a.VelX,
            ["vVelY"] = a.VelY,
            ["fPathLastEpoch"] = 0.0,
            ["vAccIn"] = Vec2Zero(),
            ["vAccRCS"] = Vec2Zero(),
            ["vAccEx"] = Vec2Zero(),
            ["vAccLift"] = Vec2Zero(),
            ["vAccDrag"] = Vec2Zero(),
            ["fRot"] = 0.0,
            ["fW"] = 0.0,
            ["fA"] = 0.0,
            ["bBOLocked"] = true,
            ["bOrbitLocked"] = false,
            ["bIsBO"] = true,
            ["bIsRegion"] = false,
            ["bIsNoFees"] = true,
            ["bGrounded"] = false,
            ["size"] = 0,
            ["bIgnoreGrav"] = false,
            ["fDockOffsetPosX"] = 0.0,
            ["fDockOffsetPosY"] = 0.0,
            ["fDockOffsetRot"] = 0.0,
        };
    }

    /// <summary>The name the game gives a bought residence: the station's public name, the separator it uses
    /// verbatim, then the design's designation (<c>GUIShipBroker.OnPurchaseConfirm</c>). Falls back to the
    /// design name when the design carries no designation, so the result is never a dangling separator.</summary>
    internal static string PublicName(ResidenceStation station, string? designation, string designName)
    {
        var suffix = designation is { Length: > 0 } d ? d : designName;
        return suffix.Length > 0 ? $"{station.DisplayName} | {suffix}" : station.DisplayName;
    }

    /// <summary>
    /// The homeowner condition a station's residents carry (<c>IsHomeowner&lt;STATION&gt;</c>), which the transit
    /// connection's <c>ctUserOptional</c> gate reads. Granted by the broker through the trader's
    /// <c>strLootResidence</c>; written directly here, since there is no broker in the loop.
    /// </summary>
    public static string HomeownerCond(string stationRegId) => "IsHomeowner" + stationRegId;

    /// <summary>
    /// Grant <paramref name="co"/> the homeowner cond for <paramref name="stationRegId"/> if it has not got it.
    /// Conds are stored as <c>"Name=MagnitudexAmount"</c> strings, so presence is tested on the name before the
    /// '=' rather than on the whole string. Returns true when the cond was added.
    /// </summary>
    internal static bool GrantHomeownerCond(JsonObject co, string stationRegId)
    {
        var wanted = HomeownerCond(stationRegId);
        if (co["aConds"] is not JsonArray conds) co["aConds"] = conds = new JsonArray();
        foreach (var n in conds)
            if (n is JsonValue v && v.TryGetValue<string>(out var s) && NameOf(s) == wanted) return false;
        conds.Add($"{wanted}=1.0x1");
        return true;

        static string NameOf(string cond)
        {
            var eq = cond.IndexOf('=');
            return eq < 0 ? cond : cond[..eq];
        }
    }

    private static JsonObject Vec2Zero() => new() { ["x"] = 0.0, ["y"] = 0.0 };

    private static string? Str(JsonNode? n, string p) =>
        (n as JsonObject)?[p] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
