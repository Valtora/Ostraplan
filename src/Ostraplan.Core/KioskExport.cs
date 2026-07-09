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
    /// <summary>The five station ship-broker pools, in the order the dialog lists them (OKLG first — the
    /// starting station). Key = loot <c>strName</c>; Label = the short station tag shown to the user.</summary>
    public static readonly IReadOnlyList<(string Pool, string Label)> BrokerPools =
    [
        ("RandomShipBrokerOKLG", "OKLG (K-Legrange)"),
        ("RandomShipBrokerBCER", "BCER"),
        ("RandomShipBrokerBCRS", "BCRS"),
        ("RandomShipBrokerVenus", "Venus"),
        ("RandomShipBrokerVORB", "VORB"),
    ];

    /// <summary>The four "Special Offer" pools (shown only when the player owns no ship/property anywhere).
    /// Each is a single pinned ship, so adding one is a straight overwrite of the whole pick.</summary>
    public static readonly IReadOnlyList<(string Pool, string Label)> SpecialOfferPools =
    [
        ("RandomShipBrokerSpecialOffer", "OKLG / default"),
        ("RandomShipBrokerSpecialOfferVENC", "VENC"),
        ("RandomShipBrokerSpecialOfferVNCA", "VNCA"),
        ("RandomShipBrokerSpecialOfferVORB", "VORB"),
    ];

    /// <summary>Add <paramref name="shipName"/> to a regular broker pool as one more weighted alternative,
    /// preserving every ship already in the effective pool. Returns the full override object to write.</summary>
    public static JsonObject BrokerPoolOverride(DataIndex index, string poolName, string shipName, double weight) =>
        AppendShipToPool(ClonePoolOrDefault(index, poolName), shipName, weight);

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
