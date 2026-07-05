using System.Text.Json;

namespace Ostraplan.Core;

/// <summary>
/// Typed views over the raw game JSON. Field semantics mirror the game's own
/// DTOs (JsonItemDef, JsonInstallable, JsonCondOwner); defaults match the
/// game's constructors (nCols=1, empty socket arrays).
/// </summary>
public sealed record ItemDef(
    string Name,
    string Img,
    bool HasSpriteSheet,
    string? CtSpriteSheet,
    int NLayer,
    int NCols,
    string[] SocketAdds,
    string[] SocketReqs,
    string[] SocketForbids)
{
    /// <summary>Footprint, exactly as Item.SetData derives it: W = nCols, H = adds/W.</summary>
    public int Width => NCols < 1 ? 1 : NCols;
    public int Height => SocketAdds.Length == 0 ? 1 : Math.Max(1, SocketAdds.Length / Width);

    public static ItemDef Parse(JsonElement e) => new(
        Json.Str(e, "strName") ?? "",
        Json.Str(e, "strImg") ?? "",
        Json.Bool(e, "bHasSpriteSheet"),
        Json.Str(e, "ctSpriteSheet"),
        Json.Int(e, "nLayer"),
        Json.Int(e, "nCols", 1),
        Json.StrArray(e, "aSocketAdds"),
        Json.StrArray(e, "aSocketReqs"),
        Json.StrArray(e, "aSocketForbids"));
}

/// <summary>
/// A cooverlay is a named skin over a base condowner: when the game can't find
/// a CO by name it falls back to dictCOOverlays and uses strCOBase with the
/// overlay's cosmetic overrides (DataHandler.LoadCO).
/// </summary>
public sealed record CoOverlayDef(string Name, string? NameFriendly, string? Img, string? COBase)
{
    public static CoOverlayDef Parse(JsonElement e) => new(
        Json.Str(e, "strName") ?? "",
        Json.Str(e, "strNameFriendly"),
        Json.Str(e, "strImg"),
        Json.Str(e, "strCOBase"));
}

public sealed record CondOwnerDef(
    string Name, string? NameFriendly, string? ItemDefName, string[] StartingCondNames,
    IReadOnlyDictionary<string, double> StartingCondValues,
    IReadOnlyDictionary<string, (double X, double Y)> MapPoints)
{
    public static CondOwnerDef Parse(JsonElement e)
    {
        var conds = Json.StrArray(e, "aStartingConds");
        return new(
            Json.Str(e, "strName") ?? "",
            Json.Str(e, "strNameFriendly"),
            Json.Str(e, "strItemDef"),
            conds.Select(LootDef.CondName).ToArray(),
            ParseCondValues(conds),
            ParseMapPoints(Json.StrArray(e, "mapPoints")));
    }

    /// <summary>
    /// The starting-cond magnitudes: name -&gt; the amount the game would apply. The condition
    /// string is "&lt;Cond&gt;=&lt;chance&gt;x&lt;amount&gt;" and the amount is the number AFTER the
    /// 'x' ("StatMass=1.0x45" -&gt; 45), matching GetCondAmount; the number before it is the apply
    /// chance (almost always 1.0), not the amount. Later entries win, matching the game's last-write
    /// accumulation. See <see cref="LootDef.CondAmount"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, double> ParseCondValues(string[] entries)
    {
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var name = LootDef.CondName(entry);
            if (name.Length == 0) continue;
            map[name] = LootDef.CondAmount(entry);
        }
        return map;
    }

    /// <summary>"DockA,0,8" entries: name, x, y in pixels around the item centre, +y up.</summary>
    public static IReadOnlyDictionary<string, (double X, double Y)> ParseMapPoints(string[] entries)
    {
        var map = new Dictionary<string, (double, double)>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var parts = entry.Split(',');
            if (parts.Length == 3
                && double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var x)
                && double.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var y))
                map[parts[0].Trim()] = (x, y);
        }
        return map;
    }
}

public sealed record InstallableDef(
    string Name, string BuildType, string JobType, string StartInstall,
    string[] Inputs, string[] Tools, bool NoJobMenu)
{
    public static InstallableDef Parse(JsonElement e) => new(
        Json.Str(e, "strName") ?? "",
        Json.Str(e, "strBuildType") ?? "",
        Json.Str(e, "strJobType") ?? "",
        Json.Str(e, "strStartInstall") ?? "",
        Json.StrArray(e, "aInputs"),
        Json.StrArray(e, "aToolCTsUse"),
        Json.Bool(e, "bNoJobMenu"));
}

/// <summary>
/// A loot's direct payload lives in aCOs ("IsWall=1.0x1" for condition-type
/// loots); aLoots nests other loots. Tile socket adds expand through both.
/// </summary>
public sealed record LootDef(string Name, string[] Conds, string[] Loots)
{
    public static LootDef Parse(JsonElement e) => new(
        Json.Str(e, "strName") ?? "",
        Json.StrArray(e, "aCOs").Select(CondName).Where(s => s.Length > 0).ToArray(),
        Json.StrArray(e, "aLoots").Where(s => s.Length > 0).ToArray());

    /// <summary>"IsWall=1.0x1" -> "IsWall".</summary>
    public static string CondName(string entry)
    {
        var i = entry.IndexOf('=');
        return (i < 0 ? entry : entry[..i]).Trim();
    }

    /// <summary>
    /// The magnitude the game applies for a condition entry, matching Loot.ParseCondEquation and
    /// CondOwner.GetCondAmount. The string is "&lt;Cond&gt;=&lt;chance&gt;x&lt;amount&gt;": the number
    /// AFTER the 'x' is the amount ("StatMass=1.0x45" -&gt; 45), the number before it is the apply
    /// chance (almost always 1.0), <b>not</b> the amount. A leading '-' negates the entry; ranges
    /// ("x4-6") take the low end (a deterministic static read, not the game's random roll). A bare
    /// name with no '=' reads as 1.0 (present); anything unparseable falls back to 1.0.
    /// </summary>
    public static double CondAmount(string entry)
    {
        var s = entry.Trim();
        var neg = s.StartsWith('-');
        if (neg) s = s[1..];
        var eq = s.IndexOf('=');
        if (eq < 0) return neg ? -1.0 : 1.0;
        var rest = s[(eq + 1)..];
        var x = rest.IndexOf('x');
        var amount = x < 0 ? rest : rest[(x + 1)..];   // the magnitude is AFTER the 'x'
        var dash = amount.IndexOf('-');                // "4-6" range -> low end
        if (dash > 0) amount = amount[..dash];
        return double.TryParse(amount.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? (neg ? -v : v) : 1.0;
    }
}

/// <summary>
/// A condtrigger. The presence-only path (every <see cref="Reqs"/> present, no
/// <see cref="Forbids"/> present) drives autotiling and CheckFit; the full reachable
/// evaluator (<see cref="CondEval"/>) additionally follows <see cref="Triggers"/>/
/// <see cref="TriggersForbid"/> and the <see cref="BAnd"/>=false OR path for room
/// certification. The <c>HasNested</c> positional flag is retained for existing
/// callers; the richer fields are populated by <see cref="Parse"/>.
/// </summary>
public sealed record CondTriggerDef(string Name, string[] Reqs, string[] Forbids, bool HasNested)
{
    public string[] Triggers { get; init; } = [];           // nested aTriggers (AND path)
    public string[] TriggersForbid { get; init; } = [];     // nested aTriggersForbid (OR path)
    public bool BAnd { get; init; } = true;                 // bAND: AND of reqs vs OR of reqs
    public double FChance { get; init; } = 1.0;             // <1 = randomised (unreachable from room specs)
    public string? HigherCond { get; init; }                // strHigherCond / aLowerConds ranking (unreachable)
    public string[] LowerConds { get; init; } = [];

    public static CondTriggerDef Parse(JsonElement e) => new(
        Json.Str(e, "strName") ?? "",
        Json.StrArray(e, "aReqs").Select(LootDef.CondName).ToArray(),
        Json.StrArray(e, "aForbids").Select(LootDef.CondName).ToArray(),
        Json.StrArray(e, "aTriggers").Length > 0)
    {
        Triggers = Json.StrArray(e, "aTriggers"),
        TriggersForbid = Json.StrArray(e, "aTriggersForbid"),
        BAnd = !(e.TryGetProperty("bAND", out var b) && b.ValueKind == JsonValueKind.False),   // default true
        FChance = Json.Dbl(e, "fChance", 1.0),
        HigherCond = Json.Str(e, "strHigherCond"),
        LowerConds = Json.StrArray(e, "aLowerConds").Select(LootDef.CondName).ToArray(),
    };
}

internal static class Json
{
    public static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True;

    public static int Int(JsonElement e, string name, int fallback = 0) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i : fallback;

    public static double Dbl(JsonElement e, string name, double fallback = 0) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)
            ? d : fallback;

    public static int[] IntArray(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out _))
                 .Select(x => x.GetInt32()).ToArray()
            : [];

    public static string[] StrArray(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
            : [];
}
