namespace Ostraplan.Core;

/// <summary>
/// What a <c>SysLootSpawner</c> spawns, and therefore <b>which array of the ship file it belongs in</b>. The two
/// are the same fact in the game's own data, measured over all 3,631 spawners its ships carry: every one of the
/// 2,954 in <c>aItems</c> is <see cref="Loot"/>, and every one of the 677 in <c>aShallowPSpecs</c> is
/// <see cref="Pspec"/> or <see cref="PspecLoot"/>. Neither array ever holds the other kind.
/// </summary>
public enum SpawnerType
{
    /// <summary>Spawns objects, from a <c>data/loot</c> table. Lives in <c>aItems</c>.</summary>
    Loot = 0,

    /// <summary>Spawns a person, from a <c>data/personspecs</c> entry. Lives in <c>aShallowPSpecs</c>.</summary>
    Pspec = 1,

    /// <summary>Spawns a person chosen through a <c>data/loot</c> table of <c>strType "pspec"</c>. Lives in
    /// <c>aShallowPSpecs</c>.</summary>
    PspecLoot = 2,
}

/// <summary>
/// A loot spawner's control panel (<c>GUILootSpawn</c>): what it spawns, how far it scatters, how many, and which
/// conditions of ship it fires on.
///
/// <para>The three condition flags are not decoration. A spawner is authored on a ship template that the game may
/// instantiate new, damaged or derelict, and each flag says whether this spawner runs in that case, which is how
/// one template yields a clean ship with supplies and a wreck strewn with scrap. The game's own ships set all
/// three true the overwhelming majority of the time (3,344 / 3,586 / 3,440 of those that carry the key), so that
/// is the default here.</para>
/// </summary>
public sealed record SpawnerSettings
{
    public SpawnerType Type { get; init; } = SpawnerType.Loot;

    /// <summary>The <c>strLoot</c> value: a loot table's name, or a person spec's, depending on
    /// <see cref="Type"/>. <see cref="DefaultTarget"/> is the game's own template default and spawns nothing.</summary>
    public string Target { get; init; } = DefaultTarget;

    /// <summary>How far from its own tile the spawner scatters what it makes, in tiles. 0 (the template default,
    /// and 2,849 of the authored spawners) puts everything on the spawner's tile.</summary>
    public int Range { get; init; }

    /// <summary>How many to spawn. The template omits it; where it is authored, 1 is the overwhelming majority.
    /// 0 and -1 both appear in the game's data and are passed through rather than corrected, because a spawner
    /// that makes nothing is a legitimate thing to author on a template that only fills when damaged.</summary>
    public int Count { get; init; } = 1;

    /// <summary>Fire when the ship is instantiated new.</summary>
    public bool WhenNew { get; init; } = true;

    /// <summary>Fire when the ship is instantiated damaged.</summary>
    public bool WhenDamaged { get; init; } = true;

    /// <summary>Fire when the ship is instantiated derelict.</summary>
    public bool WhenDerelict { get; init; } = true;

    /// <summary>The game's template default for <c>strLoot</c>: a real loot entry that yields nothing, which is
    /// what an unconfigured spawner points at.</summary>
    public const string DefaultTarget = "Blank";

    /// <summary>The two roles <see cref="ShipExport"/> synthesises when a design does not author them: where a
    /// person arriving at the ship appears, and where one already assigned to it does.</summary>
    public const string BoardingRole = "Boarding";
    public const string NotBoardingRole = "NotBoarding";

    public const int MaxRange = 64;
    public const int MinCount = -1;
    public const int MaxCount = 999;

    public static readonly SpawnerSettings Default = new();

    public bool IsDefault => Equals(Default);

    /// <summary>Whether this spawner is written into <c>aShallowPSpecs</c> rather than <c>aItems</c>.</summary>
    public bool IsPersonSpawn => Type is SpawnerType.Pspec or SpawnerType.PspecLoot;

    /// <summary>True when this is one of the two roles the export would otherwise synthesise, so an authored one
    /// can take that role's place (see <see cref="ShipExport"/>).</summary>
    public bool IsBoardingRole =>
        Type == SpawnerType.Pspec && Target is BoardingRole or NotBoardingRole;

    /// <summary>Settings forced into range. A design can arrive from an <c>.oplan</c> or an imported ship, so
    /// nothing downstream should have to defend itself against a range of two million tiles.</summary>
    public SpawnerSettings Clamped() => this with
    {
        Target = string.IsNullOrWhiteSpace(Target) ? DefaultTarget : Target.Trim(),
        Range = Math.Clamp(Range, 0, MaxRange),
        Count = Math.Clamp(Count, MinCount, MaxCount),
    };

    /// <summary>The <c>strType</c> string the game writes for <paramref name="type"/>. "Pspec Loot" carries a
    /// space, which is why this is a lookup and not <c>ToString</c>.</summary>
    public static string Wire(SpawnerType type) => type switch
    {
        SpawnerType.Pspec => "Pspec",
        SpawnerType.PspecLoot => "Pspec Loot",
        _ => "Loot",
    };

    /// <summary>Read a <c>strType</c> back. Anything unrecognised is <see cref="SpawnerType.Loot"/>, matching the
    /// template default, so a mod's typo imports as an object spawner rather than losing the spawner.</summary>
    public static SpawnerType ParseType(string? wire) => wire?.Trim() switch
    {
        "Pspec" => SpawnerType.Pspec,
        "Pspec Loot" => SpawnerType.PspecLoot,
        _ => SpawnerType.Loot,
    };

    /// <summary>The panel's flat key/value array, in the game's own key order. Every key is written, including the
    /// three condition flags the template omits: the game's ships write them explicitly, and a spawner that
    /// inherits an unknown default is a spawner whose behaviour Ostraplan cannot predict.</summary>
    public IReadOnlyList<object?> ToPanelKeys() =>
    [
        "strGUIPrefab", "GUILootSpawn",
        "strFriendlyName", "Loot Spawner",
        "strGUIPrefabRight", null,
        "strGUIPrefabLeft", null,
        "strGUIPrefabUp", null,
        "strGUIPrefabDown", null,
        "strType", Wire(Type),
        "strLoot", Target,
        "strRange", Range.ToString(),
        "strCount", Count.ToString(),
        "strNew", Bool(WhenNew),
        "strDamaged", Bool(WhenDamaged),
        "strDerelict", Bool(WhenDerelict),
    ];

    /// <summary>The game writes these as C# <c>bool.ToString()</c> output, so capitalised. Read back
    /// case-insensitively all the same (see <see cref="FromPanel"/>).</summary>
    private static string Bool(bool value) => value ? "True" : "False";

    /// <summary>
    /// Read a spawner's settings off its resolved <c>GUILootSpawn</c> panel keys, or null when the panel is not
    /// one. A missing flag reads as true, which is both the majority case and the safer one: a spawner that
    /// declines to fire is indistinguishable from one that was dropped.
    /// </summary>
    public static SpawnerSettings? FromPanel(IReadOnlyDictionary<string, string?> keys)
    {
        if (keys.GetValueOrDefault("strGUIPrefab") != "GUILootSpawn") return null;
        return new SpawnerSettings
        {
            Type = ParseType(keys.GetValueOrDefault("strType")),
            Target = keys.GetValueOrDefault("strLoot") is { Length: > 0 } loot ? loot : DefaultTarget,
            Range = Int(keys.GetValueOrDefault("strRange"), 0),
            Count = Int(keys.GetValueOrDefault("strCount"), 1),
            WhenNew = Flag(keys, "strNew"),
            WhenDamaged = Flag(keys, "strDamaged"),
            WhenDerelict = Flag(keys, "strDerelict"),
        }.Clamped();

        static int Int(string? raw, int fallback) =>
            int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

        static bool Flag(IReadOnlyDictionary<string, string?> keys, string key) =>
            keys.GetValueOrDefault(key) is not { Length: > 0 } raw
            || !bool.TryParse(raw, out var v) || v;
    }
}

/// <summary>One thing a spawner may be pointed at: the name the file carries, and something readable beside it.</summary>
public sealed record SpawnerTarget(string Name, string? Friendly)
{
    /// <summary>What the picker lists. The name is always shown, because it is what the file carries and what a
    /// bug report will quote.</summary>
    public string Display => Friendly is { Length: > 0 } f && f != Name ? $"{Name}  ({f})" : Name;

    public bool Matches(string search) =>
        search.Length == 0
        || Name.Contains(search, StringComparison.OrdinalIgnoreCase)
        || (Friendly?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);
}

/// <summary>
/// What a spawner of each type may be pointed at, read from the loaded data.
///
/// <para>The candidate set is per type and is not a matter of taste: measured over the game's own ships, every
/// <see cref="SpawnerType.Loot"/> target but one resolves in <c>data/loot</c>, every
/// <see cref="SpawnerType.Pspec"/> target but three resolves in <c>data/personspecs</c>, and every
/// <see cref="SpawnerType.PspecLoot"/> target is a <c>data/loot</c> entry of <c>strType "pspec"</c>. Offering the
/// whole of <c>data/loot</c> for all three would be 9,238 entries, most of which do nothing in a spawner.</para>
/// </summary>
public static class SpawnerCatalog
{
    /// <summary>The <c>strType</c> of a loot entry that spawns objects.</summary>
    private const string ItemLoot = "item";

    /// <summary>The <c>strType</c> of a loot entry that spawns a person.</summary>
    private const string PspecLoot = "pspec";

    /// <summary>Everything a spawner of <paramref name="type"/> may name, sorted by name. Empty for a synthetic
    /// catalog with no index, which is what makes the picker show nothing rather than throw.</summary>
    public static IReadOnlyList<SpawnerTarget> For(Catalog catalog, SpawnerType type)
    {
        if (catalog.Index is not { } index) return [];
        var targets = type switch
        {
            // A person spec has no friendly name of its own, so the career it spawns into stands in where there
            // is one. It is the field that distinguishes EJDRLEO from EJDRMaintenanceTech at a glance, and about
            // two thirds of the specs carry it.
            SpawnerType.Pspec => index.Type("personspecs")
                .Select(kv => new SpawnerTarget(kv.Key, Json.Str(kv.Value.El, "strCareerNow"))),
            SpawnerType.PspecLoot => LootOfType(index, PspecLoot),
            _ => LootOfType(index, ItemLoot),
        };
        return targets.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<SpawnerTarget> LootOfType(DataIndex index, string strType) =>
        index.Type("loot")
            .Where(kv => string.Equals(Json.Str(kv.Value.El, "strType"), strType, StringComparison.OrdinalIgnoreCase))
            .Select(kv => new SpawnerTarget(kv.Key, null));

    /// <summary>
    /// Whether <paramref name="target"/> is something this install can actually spawn. Used to warn rather than to
    /// block: the game's own data names three person specs and one loot that nothing declares, so a design that
    /// reproduces a stock ship would be refused by a rule that insisted.
    /// </summary>
    public static bool Resolves(Catalog catalog, SpawnerType type, string target) =>
        For(catalog, type).Any(t => string.Equals(t.Name, target, StringComparison.Ordinal));
}
