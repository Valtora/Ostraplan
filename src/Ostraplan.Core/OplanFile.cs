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
    /// <summary>Set when this design was imported from a save for editing — the save + ship to write back
    /// into. Null for from-scratch/template/layout-only designs. See <see cref="ShipDocument.SourceSave"/>.</summary>
    [JsonPropertyName("source")] public OplanSource? Source { get; set; }
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
            Source = doc.SourceSave is { } s ? new OplanSource { SaveName = s.SaveName, RegId = s.RegId } : null,
            Parts = doc.Placements
                       .Select(p => new OplanPart
                       {
                           Def = p.DefName, X = p.X, Y = p.Y, Rot = p.Rot, Given = p.IsGiven, Origin = p.OriginStrID,
                           // Persist a FULL snapshot of a container's contents once it has been edited, so authored
                           // cargo is authoritative on reopen rather than re-derived from the (possibly moved) save.
                           Cargo = doc.IsCargoEdited(p) && p.Cargo.Count > 0 ? p.Cargo.Select(ToOplanCargo).ToList() : null,
                       })
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
        var doc = new ShipDocument(catalog)
        {
            SourceSave = Source is { } s ? new SaveSourceRef(s.SaveName, s.RegId) : null,
        };
        var missing = new List<OplanPart>();
        foreach (var part in Parts)
        {
            if (part.Def.Length == 0 || catalog.Lookup(part.Def) is null)
            {
                missing.Add(part);
                continue;
            }
            var placement = new Placement { DefName = part.Def, X = part.X, Y = part.Y, Rot = GridMath.Norm(part.Rot), IsGiven = part.Given, OriginStrID = part.Origin };
            doc.Add(placement);
            // Restore an edited container's authored contents from the snapshot and re-mark it edited, so the
            // save re-attach on open leaves it alone (its snapshot is authoritative) and a re-save re-persists it.
            if (part.Cargo is { Count: > 0 } snap)
            {
                placement.Cargo = snap.Select(c => FromOplanCargo(c, catalog)).ToList();
                doc.MarkCargoEdited(placement);
            }
        }
        return (doc, missing);
    }

    /// <summary>Persist a cargo node's identity, authored-ness, grid position and stack — the display/footprint
    /// fields (friendly name, size) are re-resolved from the def on load, so only what can't be re-derived is stored.</summary>
    private static OplanCargo ToOplanCargo(CargoItem c) => new()
    {
        Def = c.DefName,
        StrID = c.StrID,
        Authored = c.Authored,
        Slotted = c.Slotted,
        SlotName = c.SlotName,
        X = c.GridX,
        Y = c.GridY,
        Stack = c.Stack,
        IsStack = c.IsStack,
        Children = c.Children.Count > 0 ? c.Children.Select(ToOplanCargo).ToList() : null,
    };

    /// <summary>Rebuild a cargo node from its snapshot, re-resolving footprint + friendly name from the catalog.</summary>
    private static CargoItem FromOplanCargo(OplanCargo o, Catalog catalog)
    {
        var def = catalog.Lookup(o.Def);
        var (gw, gh) = def?.InvSize ?? (1, 1);
        var children = (o.Children ?? []).Select(c => FromOplanCargo(c, catalog)).ToList();
        return new CargoItem(o.StrID, o.Def, def?.Friendly, o.Slotted, children)
        {
            GridX = o.X,
            GridY = o.Y,
            GridW = gw,
            GridH = gh,
            Stack = o.Stack <= 0 ? 1 : o.Stack,
            IsStack = o.IsStack,
            SlotName = o.SlotName,
            Authored = o.Authored,
        };
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
    [JsonPropertyName("origin")] public string? Origin { get; set; }   // save-edit: the source save item's strID (see Placement.OriginStrID)
    /// <summary>A full snapshot of this container's contents, present only when its cargo was edited in the
    /// inventory editor (see <see cref="ShipDocument.IsCargoEdited"/>). Null for un-edited parts, whose cargo is
    /// re-read from the linked save on open. See <see cref="OplanCargo"/>.</summary>
    [JsonPropertyName("cargo")] public List<OplanCargo>? Cargo { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>One contained item in a persisted cargo snapshot (see <see cref="OplanPart.Cargo"/>) — enough to
/// rebuild the <see cref="CargoItem"/> tree on reopen: its def + save/local strID, whether it was authored, its
/// grid position, and stack state. Friendly name and grid footprint are re-resolved from the def, so they are
/// not stored. Nested contents recurse through <see cref="Children"/>.</summary>
public sealed class OplanCargo
{
    [JsonPropertyName("def")] public string Def { get; set; } = "";
    [JsonPropertyName("strId")] public string StrID { get; set; } = "";
    [JsonPropertyName("authored")] public bool Authored { get; set; }
    [JsonPropertyName("slotted")] public bool Slotted { get; set; }
    [JsonPropertyName("slot")] public string? SlotName { get; set; }
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("stack")] public int Stack { get; set; } = 1;
    [JsonPropertyName("isStack")] public bool IsStack { get; set; }
    [JsonPropertyName("children")] public List<OplanCargo>? Children { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>The save a save-edit design was imported from — persisted so the design can be re-located and
/// injected after reopening. See <see cref="SaveSourceRef"/> for the in-memory form.</summary>
public sealed class OplanSource
{
    [JsonPropertyName("saveName")] public string SaveName { get; set; } = "";
    [JsonPropertyName("regId")] public string RegId { get; set; } = "";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
