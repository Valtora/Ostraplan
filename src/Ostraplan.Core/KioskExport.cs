using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ostraplan.Core;

/// <summary>One alternative inside a loot pool's weighted pick: <c>Name=WeightxCount</c>
/// (e.g. <c>"Babak=0.017x1"</c>). <see cref="Count"/> stays a string because the game allows a
/// range there (<c>"3-10"</c>) as well as a plain number.</summary>
public readonly record struct LootEntry(string Name, double Weight, string Count);

/// <summary>
/// Parses and edits the game's loot-pool <c>aCOs</c> weighted-list format. A ship-broker pool's
/// <c>aCOs</c> is a <b>single-element array</b> whose one string is a <c>|</c>-delimited weighted
/// set — <c>"A=0.02x1|B=0.03x1|…"</c> — from which the game picks exactly one option per roll. To
/// add a ship to a broker you therefore append another <c>|Name=Wx1</c> alternative to that same
/// string; adding a new array element instead would make the game roll a <i>second</i> ship (see
/// <see cref="KioskExport"/>). Appends preserve the existing string verbatim to minimise churn.
/// </summary>
public static class LootList
{
    public static IReadOnlyList<LootEntry> Parse(string piped)
    {
        var result = new List<LootEntry>();
        if (string.IsNullOrWhiteSpace(piped)) return result;
        foreach (var raw in piped.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = raw.IndexOf('=');
            if (eq < 0) continue;
            var name = raw[..eq];
            var mag = raw[(eq + 1)..];
            var xi = mag.IndexOf('x');
            if (xi < 0) continue;
            if (!double.TryParse(mag[..xi], NumberStyles.Float, CultureInfo.InvariantCulture, out var w)) continue;
            result.Add(new LootEntry(name, w, mag[(xi + 1)..]));
        }
        return result;
    }

    /// <summary>The mean weight of a pool's existing alternatives — the sensible default weight for a
    /// newly added ship so it appears about as often as a stock one. Falls back to 0.05 for an empty pool.</summary>
    public static double AverageWeight(string piped)
    {
        var entries = Parse(piped);
        return entries.Count == 0 ? 0.05 : entries.Average(e => e.Weight);
    }

    public static bool Contains(string piped, string name) =>
        Parse(piped).Any(e => string.Equals(e.Name, name, StringComparison.Ordinal));

    /// <summary>Serialize one alternative as the game expects: <c>Name=WeightxCount</c>, weight in
    /// invariant culture (the game parses '.' decimals) with trailing zeros trimmed.</summary>
    public static string FormatEntry(string name, double weight, string count = "1") =>
        $"{name}={weight.ToString("0.######", CultureInfo.InvariantCulture)}x{count}";

    /// <summary>Append an alternative to an existing <c>|</c>-delimited string (verbatim + the new tail),
    /// or return just the new entry when the string is empty. No-op if the name is already present.</summary>
    public static string Append(string piped, string name, double weight, string count = "1")
    {
        if (Contains(piped, name)) return piped;
        var entry = FormatEntry(name, weight, count);
        return string.IsNullOrWhiteSpace(piped) ? entry : piped + "|" + entry;
    }

    /// <summary>
    /// Drop an alternative from a <c>|</c>-delimited string. A name that is not there returns the string
    /// verbatim, so the no-op case costs nothing and churns nothing; a removal necessarily re-serializes the
    /// survivors, which normalises their weight formatting (<c>0.0170</c> becomes <c>0.017</c>) and nothing else.
    ///
    /// <para>This is the other half of <see cref="Append"/>, and it exists because an export has to be able to
    /// take its own ship back out of a pool it previously put it into. See
    /// <see cref="KioskExport.StripShipsFromPool"/> for why that matters.</para>
    /// </summary>
    public static string Remove(string piped, string name)
    {
        if (!Contains(piped, name)) return piped;
        return string.Join('|', Parse(piped)
            .Where(e => !string.Equals(e.Name, name, StringComparison.Ordinal))
            .Select(e => FormatEntry(e.Name, e.Weight, e.Count)));
    }
}

/// <summary>
/// Builds <c>data/loot</c> ship-broker pool overrides that make an exported ship purchasable in game.
/// Every builder returns a <b>complete</b> pool object (a whole-object override, the only merge the game
/// does for loot), cloned from the current <b>effective</b> pool via <see cref="DataIndex"/> so any ships
/// other loaded mods already added are preserved — a same-pool clash with another ship mod is then the
/// per-item-union case Ostrasort's <c>--patch</c> resolves (the export dialog says so).
/// </summary>
public static class KioskExport
{
    /// <summary>The loot <c>strName</c> prefix every station's regular ship-broker stock pool carries.</summary>
    public const string BrokerPoolPrefix = "RandomShipBroker";

    /// <summary>The prefix of the "Special Offer" pools, which are <see cref="BrokerPoolPrefix"/> plus a
    /// marker — so a discovery pass over the broker prefix has to exclude these explicitly.</summary>
    public const string SpecialOfferPoolPrefix = "RandomShipBrokerSpecialOffer";

    /// <summary>
    /// The station ship-broker pools the loaded data actually has, newest game and every loaded mod included.
    ///
    /// <para><b>Discovered, not listed.</b> This used to be a hardcoded five (OKLG, BCER, BCRS, Venus, VORB),
    /// which was the whole set in 0.15.1.6. Game 1.0 opened the rest of the system and there are now thirteen, so
    /// a hardcoded list quietly hid two thirds of the kiosks in the game from the export dialog. Reading them out
    /// of the effective loot table means a station added by a later patch, or by another ship mod, shows up on its
    /// own.</para>
    ///
    /// <para>OKLG sorts first because it is where a new game starts; the rest are alphabetical.</para>
    /// </summary>
    public static IReadOnlyList<(string Pool, string Label)> BrokerPoolsIn(DataIndex index) =>
        [.. ShipPools(index)
            .Where(n => n.StartsWith(BrokerPoolPrefix, StringComparison.Ordinal)
                     && !n.StartsWith(SpecialOfferPoolPrefix, StringComparison.Ordinal))
            .OrderBy(n => n == "RandomShipBrokerOKLG" ? 0 : 1)
            .ThenBy(n => n, StringComparer.Ordinal)
            .Select(n => (n, StationLabel(n[BrokerPoolPrefix.Length..])))];

    /// <summary>The "Special Offer" pools present in the loaded data (shown in game only when the player owns no
    /// ship or property anywhere). Each is a single pinned ship, so adding one overwrites the whole pick.
    /// Discovered for the same reason as <see cref="BrokerPoolsIn"/>.</summary>
    public static IReadOnlyList<(string Pool, string Label)> SpecialOfferPoolsIn(DataIndex index) =>
        [.. ShipPools(index)
            .Where(n => n.StartsWith(SpecialOfferPoolPrefix, StringComparison.Ordinal))
            .OrderBy(n => n.Length)   // the bare default pool first, then the station variants
            .ThenBy(n => n, StringComparer.Ordinal)
            .Select(n => (n, n.Length == SpecialOfferPoolPrefix.Length
                ? "OKLG / default"
                : StationLabel(n[SpecialOfferPoolPrefix.Length..])))];

    /// <summary>Every <c>strType: "ship"</c> loot pool in the effective (mod-resolved) data.</summary>
    private static IEnumerable<string> ShipPools(DataIndex index) =>
        index.Type("loot")
            .Where(kv => Json.Str(kv.Value.El, "strType") == "ship")
            .Select(kv => kv.Key);

    /// <summary>
    /// What to call a station in the dialog. The game shows these as bare four-letter ATC codes almost everywhere,
    /// so the code is the label, with a gloss appended for the few the world data actually names. An unknown code
    /// (a later patch, or a mod's own station) falls through to the bare code rather than being dropped.
    /// </summary>
    public static string StationLabel(string code) =>
        StationNames.TryGetValue(code, out var name) ? $"{code} ({name})" : code;

    private static readonly IReadOnlyDictionary<string, string> StationNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OKLG"] = "K-Legrange",
            ["EJDR"] = "Old China",
            ["HQCH"] = "Prokofiev Penal Colony",
            ["MHNG"] = "Hangzhou",
            ["MSUZ"] = "Suzhou",
        };

    /// <summary>
    /// The derelict-ring pools: the wrecks scattered through the salvage fields at world generation.
    ///
    /// <para><c>star_systems/star_system.json</c>'s <c>aSpawnDerelictRings</c> names a <c>strType: "ship"</c> loot
    /// pool per ring, so these are the same kind of weighted pick as a broker kiosk and take the same override.
    /// The spawner is what marks the ship derelict and damages it; every one of the 220 core ship templates
    /// carries <c>DMGStatus = 0</c>, so nothing about being a wreck belongs in the ship file.</para>
    ///
    /// <para><b>Venus is not a leaf.</b> <c>RandomDerelictVenus</c> has an empty <c>aCOs</c> and delegates through
    /// <c>aLoots</c> to <c>RandomScavShipVNCA</c> (0.85) and <c>RandomScavShip</c> (0.15), the way
    /// <c>RandomDerelict</c> delegates to the three size bands. The VNCA pool is therefore the honest target for
    /// "put my ship in the Venus fields": adding to the composer would be writing at the wrong level.</para>
    /// </summary>
    public static readonly IReadOnlyList<(string Pool, string Label)> DerelictPools =
    [
        ("RandomDerelictSmall", "Small"),
        ("RandomDerelictMedium", "Medium"),
        ("RandomDerelictBig", "Big"),
        ("RandomScavShipVNCA", "Venus"),
    ];

    /// <summary>
    /// The part counts of each size band's own members, measured against game 1.0.0.7, so the UI can say what a
    /// band actually holds instead of asserting a size.
    ///
    /// <para><b>The bands overlap heavily</b> — Small reaches 800 parts while Medium starts at 319 and Big at 520 —
    /// so no threshold separates them and any claim that a hull "is" a given size would be invented. The median is
    /// offered as a nearest-fit suggestion and the range is shown beside it, which is as far as the data honestly
    /// goes.</para>
    /// </summary>
    public static readonly IReadOnlyList<(string Pool, int Min, int Median, int Max)> DerelictBands =
    [
        ("RandomDerelictSmall", 107, 252, 800),
        ("RandomDerelictMedium", 319, 348, 2509),
        ("RandomDerelictBig", 520, 2323, 5852),
    ];

    /// <summary>The size band whose members a design of <paramref name="partCount"/> parts sits closest to, by
    /// distance from the band median. Never Venus, which is a flavour pool rather than a size.</summary>
    public static string SuggestDerelictBand(int partCount) =>
        DerelictBands.OrderBy(b => Math.Abs(partCount - b.Median)).ThenBy(b => b.Median).First().Pool;

    /// <summary>Add <paramref name="shipName"/> to a regular broker pool as one more weighted alternative,
    /// preserving every ship already in the effective pool. Returns the full override object to write.
    /// <paramref name="owned"/> is stripped from the pool first (see <see cref="StripShipsFromPool"/>).</summary>
    public static JsonObject BrokerPoolOverride(
        DataIndex index, string poolName, string shipName, double weight, IEnumerable<string>? owned = null) =>
        AppendShipToPool(StripShipsFromPool(ClonePoolOrDefault(index, poolName), owned), shipName, weight);

    /// <summary>Add <paramref name="shipName"/> to a derelict ring's pool. Mechanically identical to
    /// <see cref="BrokerPoolOverride"/> — both are weighted <c>aCOs</c> picks — and named separately because the
    /// two mean entirely different things to a player.</summary>
    public static JsonObject DerelictPoolOverride(
        DataIndex index, string poolName, string shipName, double weight, IEnumerable<string>? owned = null) =>
        BrokerPoolOverride(index, poolName, shipName, weight, owned);

    /// <summary>Point a Special Offer pool at <paramref name="shipName"/> — a straight overwrite, since a
    /// Special Offer pool is always exactly one pinned ship at weight 1.</summary>
    public static JsonObject SpecialOfferOverride(DataIndex index, string poolName, string shipName) =>
        PinShipToPool(ClonePoolOrDefault(index, poolName), shipName);

    /// <summary>Append a weighted ship alternative to a pool's first (and only) <c>aCOs</c> pick, in place. A ship
    /// already present is left as-is (no duplicate). Returns the same object for chaining. Pure — the argument is
    /// mutated, so callers pass a clone (<see cref="ClonePoolOrDefault"/>).</summary>
    public static JsonObject AppendShipToPool(JsonObject pool, string shipName, double weight)
    {
        var aCOs = EnsureACOs(pool);
        var first = aCOs.Count > 0 ? aCOs[0]?.GetValue<string>() ?? "" : "";
        var updated = LootList.Append(first, shipName, weight);
        if (aCOs.Count > 0) aCOs[0] = updated; else aCOs.Add(updated);
        return pool;
    }

    /// <summary>
    /// Take a mod's <b>own</b> ships back out of a pool's pick, in place, before it appends the ships it is
    /// writing now. Returns the same object for chaining. Pure — the argument is mutated, so callers pass a
    /// clone (<see cref="ClonePoolOrDefault"/>).
    ///
    /// <para><b>Why an export has to do this at all.</b> The pool it clones is the <i>effective</i> data, and
    /// once a mod is registered that includes the mod's own last write. So a re-export clones a pool that
    /// already lists the ship under whatever name it had last time, and <see cref="LootList.Append"/> leaves an
    /// entry it finds. Rename the ship, or take it out of a bundle, and the kiosk keeps selling a ship the mod
    /// no longer contains: the pool names a template that is not there any more. Stripping first and appending
    /// after makes the write say exactly what the mod holds now.</para>
    ///
    /// <para><b>Only the mod's own names.</b> Another ship mod's entry in the same pool is preserved, exactly as
    /// <see cref="AppendShipToPool"/> preserves it — that clash is Ostrasort's <c>--patch</c> case and not this
    /// one. A <b>replacement</b> export owns no name here either: its <c>strName</c> is the core ship's, which
    /// core's own pools legitimately list, and stripping that would take a vanilla ship out of the game.</para>
    /// </summary>
    public static JsonObject StripShipsFromPool(JsonObject pool, IEnumerable<string>? names)
    {
        if (names is null) return pool;
        var aCOs = EnsureACOs(pool);
        if (aCOs.Count == 0) return pool;

        var first = aCOs[0]?.GetValue<string>() ?? "";
        var stripped = names.Aggregate(first, LootList.Remove);
        if (stripped != first) aCOs[0] = stripped;
        return pool;
    }

    /// <summary>Overwrite a pool's pick to a single pinned ship at weight 1, in place (a Special Offer is always
    /// one ship). Returns the same object.</summary>
    public static JsonObject PinShipToPool(JsonObject pool, string shipName)
    {
        pool["aCOs"] = new JsonArray(LootList.FormatEntry(shipName, 1.0));
        return pool;
    }

    /// <summary>The default weight to pre-fill for a broker pool: the mean of its existing alternatives, so a
    /// new ship shows up about as often as a stock one. 0.05 when the pool is empty/absent.</summary>
    public static double DefaultBrokerWeight(DataIndex index, string poolName)
    {
        if (!index.Type("loot").TryGetValue(poolName, out var hit)) return 0.05;
        if (!hit.El.TryGetProperty("aCOs", out var aCOs) || aCOs.ValueKind != JsonValueKind.Array || aCOs.GetArrayLength() == 0)
            return 0.05;
        return LootList.AverageWeight(aCOs[0].GetString() ?? "");
    }

    /// <summary>Clone the current effective pool object as a mutable node, or synthesize a minimal ship pool
    /// (<c>strName</c>/<c>aCOs</c>/<c>aLoots</c>/<c>strType</c>) if the game has no such pool.</summary>
    public static JsonObject ClonePoolOrDefault(DataIndex index, string poolName)
    {
        if (index.Type("loot").TryGetValue(poolName, out var hit)
            && JsonNode.Parse(hit.El.GetRawText()) is JsonObject cloned)
            return cloned;

        return new JsonObject
        {
            ["strName"] = poolName,
            ["aCOs"] = new JsonArray(),
            ["aLoots"] = new JsonArray(),
            ["strType"] = "ship",
        };
    }

    private static JsonArray EnsureACOs(JsonObject pool)
    {
        if (pool["aCOs"] is JsonArray a) return a;
        var fresh = new JsonArray();
        pool["aCOs"] = fresh;
        return fresh;
    }
}
