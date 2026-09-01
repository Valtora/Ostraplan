using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ostraplan.Core;

/// <summary>
/// The <c>.oplanmod</c> document: a mod made of several designs, each with its own settings, saved so that
/// exporting the same pack again is one click rather than an afternoon.
///
/// <para><b>It holds paths to designs, not designs.</b> A ship stays a <c>.oplan</c> and stays the authority on
/// what it is made of and what it is called, so editing one and re-exporting the pack picks the change up. This
/// file holds only what a design cannot say for itself: which mod it is part of, and how that mod hands it out.
/// The one exception is <see cref="BundleEntry.NameOverride"/>, which exists to break a name collision between two
/// designs without having to rename either.</para>
///
/// <para><b>No machine state.</b> Where the mod is written, and whether Ostrasort is run afterwards, stay in the
/// app's settings, exactly as they do for a single design: this file is shareable, and usage.md's rule is that a
/// document you share carries no folder paths of yours. Versioned like the <c>.oplan</c>, with unknown fields
/// preserved, so a file written by a later build survives an older one.</para>
/// </summary>
public sealed class BundleFile
{
    public const int CurrentFormatVersion = 1;

    /// <summary>The file extension, without the dot.</summary>
    public const string Extension = "oplanmod";

    [JsonPropertyName("formatVersion")] public int FormatVersion { get; set; } = CurrentFormatVersion;
    [JsonPropertyName("game")] public OplanGame Game { get; set; } = new();
    [JsonPropertyName("mod")] public BundleModMeta Mod { get; set; } = new();
    [JsonPropertyName("ships")] public List<BundleEntry> Ships { get; set; } = [];

    /// <summary>
    /// The ship names the last export of this pack wrote. It is what lets a re-export take a dropped ship's kiosk
    /// entries and preview art back out: once the mod is registered, the pools it clones already carry its own
    /// last write, and a ship no longer in the pack would otherwise stay in them naming a template that is gone.
    /// </summary>
    [JsonPropertyName("lastWritten")] public List<string> LastWritten { get; set; } = [];

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Options));

    public static BundleFile Load(string path)
    {
        var file = JsonSerializer.Deserialize<BundleFile>(File.ReadAllText(path), Options)
                   ?? throw new InvalidDataException("File parsed to null.");
        if (file.FormatVersion > CurrentFormatVersion)
            throw new InvalidDataException(
                $"'{Path.GetFileName(path)}' is format v{file.FormatVersion}; this build reads up to v{CurrentFormatVersion}.");
        return file;
    }

    /// <summary>
    /// Where a member design actually is, given where the bundle file is. Stored relative wherever that resolves,
    /// so a folder holding a pack and its designs can be moved or shared whole; an absolute path is kept as one
    /// (a design on another drive has no relative form).
    /// </summary>
    public static string ResolveDesignPath(string bundlePath, string entryPath) =>
        Path.IsPathRooted(entryPath)
            ? Path.GetFullPath(entryPath)
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(bundlePath)) ?? "", entryPath));

    /// <inheritdoc cref="ResolveDesignPath"/>
    public static string StoreDesignPath(string bundlePath, string designPath)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(bundlePath));
        if (string.IsNullOrEmpty(root)) return Path.GetFullPath(designPath);
        var relative = Path.GetRelativePath(root, Path.GetFullPath(designPath));
        // GetRelativePath hands back an absolute path when there is no relative one (a different volume), which
        // is exactly what should be stored in that case.
        return relative;
    }
}

/// <summary>What the mod is, as against what the ships in it are. The same four fields the export wizard's Mod
/// details step asks for, plus the one question only a pack can be asked.</summary>
public sealed class BundleModMeta
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "1.0.0";
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";

    /// <summary>Pin the Shipbreaker start to this mod's ships, dropping the vanilla salvage pods. See
    /// <see cref="BundleOptions.ExclusiveStart"/> for why it is asked of the mod rather than of a ship.</summary>
    [JsonPropertyName("exclusiveStart")] public bool ExclusiveStart { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>One design in the pack: where it is, and everything about it that belongs to this mod rather than to
/// the design.</summary>
public sealed class BundleEntry
{
    /// <summary>The <c>.oplan</c>, relative to the bundle file where that resolves (see
    /// <see cref="BundleFile.ResolveDesignPath"/>).</summary>
    [JsonPropertyName("path")] public string Path { get; set; } = "";

    /// <summary>A name to use instead of the design's own, or null to take the design's. It exists for one reason:
    /// two designs in a pack may share a name, and a ship's name is what the game keys its data and its pictures
    /// on, so one of them has to give. Renaming it here beats having to edit a design to fit a pack.</summary>
    [JsonPropertyName("name")] public string? NameOverride { get; set; }

    /// <summary>The <c>strName</c> of an existing ship this design replaces, or null.</summary>
    [JsonPropertyName("replaces")] public string? Replaces { get; set; }

    [JsonPropertyName("wear")] public BundleWear Wear { get; set; } = new();

    [JsonPropertyName("delivery")] public DeliveryPlan Delivery { get; set; } = new();

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>The condition to bake into one ship. The seed is not stored: it is drawn afresh whenever the pack is
/// reviewed, exactly as it is for a single design.</summary>
public sealed class BundleWear
{
    [JsonPropertyName("on")] public bool On { get; set; }
    [JsonPropertyName("target")] public double Target { get; set; } = 1.0;

    public WearOptions ToOptions() => new(On, Target);

    public static BundleWear From(WearOptions wear) => new() { On = wear.Enabled, Target = wear.TargetCondition };
}
