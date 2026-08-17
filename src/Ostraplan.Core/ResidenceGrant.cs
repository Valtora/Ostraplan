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
public sealed record ResidenceStation(string RegId, string DisplayName, GrantAnchor Anchor, bool HasTransitRoute)
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
    /// The stations in <paramref name="zipPath"/>'s save a residence could be attached to, best first: a station
    /// with a transit route ahead of one without, and the player's own location ahead of everything.
    ///
    /// <para>A station is any ship whose <c>objSS.bIsBO</c> is set, which is the game's own test
    /// (<c>Ship.IsStation</c>). Sub-modules are excluded — their RegIDs already carry a pipe, and hanging a
    /// residence off one would mint <c>BCRS|RES_1|RES_1</c>, which truncates to the same transit node but reads
    /// as nonsense. That exclusion is also what the game's 0.15.0.x migration did by hand, rewriting
    /// <c>BCRS_RES|RES…</c> to <c>BCRS|RES…</c>.</para>
    /// </summary>
    public static IReadOnlyList<ResidenceStation> ListStations(string zipPath, DataIndex index)
    {
        var transitNodes = TransitNodes(index);
        var stations = new List<ResidenceStation>();
        string? playerStation = null;

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
            if (ship["objSS"] is not JsonObject situ || situ["bIsBO"]?.GetValue<bool>() != true) continue;

            var name = Str(ship, "publicName") is { Length: > 0 } p && p != "$TEMPLATE" ? p : regId;
            stations.Add(new ResidenceStation(
                regId, name, GrantAnchor.FromShipRecord(ship), transitNodes.Contains(regId + "|")));
            if (regId == playerShipReg) playerStation = regId;
        }

        return [.. stations
            .OrderByDescending(s => s.RegId == playerStation)
            .ThenByDescending(s => s.HasTransitRoute)
            .ThenBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Every transit node name in the effective (mod-resolved) data. Read from the index rather than
    /// hard-coded so a mod that adds a station's residence route is seen.</summary>
    private static HashSet<string> TransitNodes(DataIndex index) =>
        [.. index.Type("transit").Keys];

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
