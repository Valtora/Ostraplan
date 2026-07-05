using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ostraplan.Core;

/// <summary>
/// The .oplan document: versioned JSON, unknown fields preserved on round-trip
/// so newer files survive older builds. Parts reference game defs by strName;
/// resolution against the catalog happens at open time (missing defs are
/// reported, not fatal).
/// </summary>
public sealed class OplanFile
{
    public const int CurrentFormatVersion = 1;

    [JsonPropertyName("formatVersion")] public int FormatVersion { get; set; } = CurrentFormatVersion;
    [JsonPropertyName("game")] public OplanGame Game { get; set; } = new();
    [JsonPropertyName("mods")] public List<OplanMod> Mods { get; set; } = [];
    [JsonPropertyName("meta")] public OplanMeta Meta { get; set; } = new();
    [JsonPropertyName("parts")] public List<OplanPart> Parts { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static OplanFile FromDocument(ShipDocument doc, DataIndex index, OplanMeta meta)
    {
        var file = new OplanFile
        {
            Game = new OplanGame
            {
                VersionAtSave = index.Env.InstalledVersion,
                VersionVerified = GameEnv.VerifiedGameVersion,
            },
            Mods = index.Sources.Where(s => !s.IsCore)
                                .Select(s => new OplanMod { Name = s.Label, Entry = s.Raw })
                                .ToList(),
            Meta = meta,
            Parts = doc.Placements
                       .Select(p => new OplanPart { Def = p.DefName, X = p.X, Y = p.Y, Rot = p.Rot, Given = p.IsGiven })
                       .ToList(),
        };
        file.Meta.Modified = DateTime.UtcNow;
        return file;
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Options));

    public static OplanFile Load(string path)
    {
        var file = JsonSerializer.Deserialize<OplanFile>(File.ReadAllText(path), Options)
                   ?? throw new InvalidDataException("File parsed to null.");
        if (file.FormatVersion > CurrentFormatVersion)
            throw new InvalidDataException(
                $"'{Path.GetFileName(path)}' is format v{file.FormatVersion}; this build reads up to v{CurrentFormatVersion}.");
        return file;
    }

    /// <summary>Rebuild a document; parts whose def is not in the catalog are returned, not placed.</summary>
    public (ShipDocument Doc, List<OplanPart> Missing) ToDocument(Catalog catalog)
    {
        var doc = new ShipDocument(catalog);
        var missing = new List<OplanPart>();
        foreach (var part in Parts)
        {
            if (part.Def.Length == 0 || catalog.Lookup(part.Def) is null)
            {
                missing.Add(part);
                continue;
            }
            doc.Add(new Placement { DefName = part.Def, X = part.X, Y = part.Y, Rot = GridMath.Norm(part.Rot), IsGiven = part.Given });
        }
        return (doc, missing);
    }
}

public sealed class OplanGame
{
    [JsonPropertyName("versionAtSave")] public string? VersionAtSave { get; set; }
    [JsonPropertyName("versionVerified")] public string? VersionVerified { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class OplanMod
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("entry")] public string Entry { get; set; } = "";   // the loading_order form
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class OplanMeta
{
    [JsonPropertyName("name")] public string Name { get; set; } = "Untitled ship";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    [JsonPropertyName("created")] public DateTime Created { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("modified")] public DateTime Modified { get; set; } = DateTime.UtcNow;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class OplanPart
{
    [JsonPropertyName("def")] public string Def { get; set; } = "";
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("rot")] public int Rot { get; set; }
    [JsonPropertyName("given")] public bool Given { get; set; }   // imported (pre-existing) structure — exempt from the placement-law scan
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
