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
    /// "StatMass=125.0x1" -&gt; StatMass:125.0. The game's condition string is
    /// "&lt;Cond&gt;=&lt;value&gt;x&lt;count&gt;"; the value is the amount GetCondAmount
    /// returns (mass, thrust strength, …). A bare "IsFoo" with no "=" reads as 1.0
    /// (present). Later entries win, matching the game's last-write accumulation.
    /// </summary>
    public static IReadOnlyDictionary<string, double> ParseCondValues(string[] entries)
    {
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var name = LootDef.CondName(entry);
            if (name.Length == 0) continue;
            map[name] = LootDef.CondValue(entry);
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
    /// "StatMass=125.0x1" -> 125.0 (the value between '=' and 'x'). A bare name with
    /// no '=' reads as 1.0 (present). Ranges ("x4-6") take the low end; unparseable
    /// values fall back to 1.0.
    /// </summary>
    public static double CondValue(string entry)
    {
        var eq = entry.IndexOf('=');
        if (eq < 0) return 1.0;
        var rest = entry[(eq + 1)..];
        var x = rest.IndexOf('x');
        var num = (x < 0 ? rest : rest[..x]).Trim();
        return double.TryParse(num, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 1.0;
    }
}

/// <summary>
/// The subset of a condtrigger Ostraplan evaluates today: presence of every
/// aReqs condition, absence of every aForbids condition. Nested aTriggers and
/// count semantics arrive with the full P1 engine port.
/// </summary>
public sealed record CondTriggerDef(string Name, string[] Reqs, string[] Forbids, bool HasNested)
{
    public static CondTriggerDef Parse(JsonElement e) => new(
        Json.Str(e, "strName") ?? "",
        Json.StrArray(e, "aReqs").Select(LootDef.CondName).ToArray(),
        Json.StrArray(e, "aForbids").Select(LootDef.CondName).ToArray(),
        Json.StrArray(e, "aTriggers").Length > 0);
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
