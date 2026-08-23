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
    /// <summary>The plan-view orientation (Q/E rotation, a 90° step) the design was last saved in, so it reopens
    /// in the same orientation. Additive since format v1 — an older build ignores it and round-trips it via
    /// <see cref="Extra"/>. Defaults to 0 (north-up).</summary>
    [JsonPropertyName("viewRot")] public int ViewRot { get; set; }
    [JsonPropertyName("game")] public OplanGame Game { get; set; } = new();
    [JsonPropertyName("mods")] public List<OplanMod> Mods { get; set; } = [];
    [JsonPropertyName("meta")] public OplanMeta Meta { get; set; } = new();
    /// <summary>
    /// <b>Legacy, read-only.</b> Builds up to 0.92.x stamped the save + ship a design was imported from into the
    /// file, and reopening it went looking for that save. A design is now save-agnostic: it carries its own
    /// container contents (<see cref="OplanPart.Cargo"/>) and names the ship it should be written over at the
    /// write, so nothing in the file depends on a save still being on this machine.
    ///
    /// <para>Still parsed, because a file written by an older build carries no container contents of its own and
    /// this is the only thing that says where they were. Opening such a file reads them back once and then owns
    /// them; the next save drops this field and the design is unlinked for good. Never written.</para>
    /// </summary>
    [JsonPropertyName("source")] public OplanSource? Source { get; set; }
    [JsonPropertyName("parts")] public List<OplanPart> Parts { get; set; } = [];
    /// <summary>The design's painted zones (see <see cref="ShipZone"/>). Additive since format v1 — older
    /// builds ignore it and preserve it via <see cref="Extra"/>.</summary>
    [JsonPropertyName("zones")] public List<OplanZone> Zones { get; set; } = [];
    /// <summary>The design's loose floor items (see <see cref="LooseObject"/>). Additive at format v1, exactly like
    /// <see cref="Zones"/>: an older build ignores it and round-trips it via <see cref="Extra"/>, so no version bump.</summary>
    [JsonPropertyName("looseObjects")] public List<OplanLoose> LooseObjects { get; set; } = [];
    /// <summary>The design's device signal connections (see <see cref="DeviceLink"/>). Each is a directed pair of
    /// <b>indices into <see cref="Parts"/></b> (source, target) — parts have no stable id in the file, but their
    /// array order is preserved, so an index pair round-trips a link. A pair referencing a part that was dropped on
    /// load (a missing-mod part) is skipped. Additive at format v1, like <see cref="Zones"/>.</summary>
    [JsonPropertyName("links")] public List<OplanLink> Links { get; set; } = [];
    /// <summary>Problem-warning keys the user dismissed (see <see cref="ShipDocument.DismissedAlerts"/>). Additive
    /// at format v1, like <see cref="Zones"/>.</summary>
    [JsonPropertyName("dismissedAlerts")] public List<string> DismissedAlerts { get; set; } = [];
    /// <summary>Friendly names for the factions this design's cargo belongs to (see
    /// <see cref="ShipDocument.FactionNames"/>), raw id → name. A document-level table rather than a name repeated
    /// on every item, because a hold's worth of cargo off one station shares three ids between hundreds of items.
    /// Null — and omitted — for a design whose cargo belongs to no faction, which is every design drawn from
    /// scratch. Additive at format v1, like <see cref="Zones"/>.</summary>
    [JsonPropertyName("factions")] public Dictionary<string, string>? Factions { get; set; }
    /// <summary>Extra mass (kg) the design is expected to haul, for the propulsion figures only (see
    /// <see cref="ShipDocument.ExtraMassKg"/>). Additive at format v1, like <see cref="Zones"/>: an older build
    /// ignores it and round-trips it via <see cref="Extra"/>, so no version bump. Omitted when zero.</summary>
    [JsonPropertyName("extraMassKg")] public double? ExtraMassKg { get; set; }
    /// <summary>What the design is — <c>"Ship"</c> or <c>"Residence"</c> (see <see cref="DocumentKind"/>). A
    /// document property like <see cref="ViewRot"/>, not part of the in-game identity in <see cref="Meta"/>.
    /// Additive at format v1, like <see cref="Zones"/>: an older build ignores it and round-trips it via
    /// <see cref="Extra"/>. Written as a name rather than a number so the file stays readable, and
    /// <b>omitted for a ship</b> so every existing design round-trips byte-identically. An unrecognised value
    /// reads back as a ship.</summary>
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    /// <summary>Set <b>only</b> by the auto-save snapshot writer (see <see cref="AutoSaveStore.Write"/>): the path of
    /// the design's own file at the moment the snapshot was taken, so recovering it puts the design back on that file
    /// rather than leaving an orphan. Null in a snapshot of a design that had never been saved, and null in every file
    /// an explicit Save writes — <see cref="FromDocument"/> never sets it. Additive at format v1, like
    /// <see cref="Zones"/>.</summary>
    [JsonPropertyName("autoSaveOf")] public string? AutoSaveOf { get; set; }
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
                       .Select(p => new OplanPart
                       {
                           Def = p.DefName, X = p.X, Y = p.Y, Rot = p.Rot, Given = p.IsGiven, Origin = p.OriginStrID,
                           SwappedFrom = p.SwappedFromStrID, SwappedFromDef = p.SwappedFromDef,
                           Name = p.CustomName, Z = p.ZBias == 0 ? null : p.ZBias,
                           // A FULL snapshot of whatever this container holds, authored or imported alike. Written
                           // for every container rather than only for edited ones, because the design has to be
                           // readable without the save it came from. CargoOwn is what keeps the two apart.
                           Cargo = p.Cargo.Count > 0 ? p.Cargo.Select(ToOplanCargo).ToList() : null,
                           CargoOwn = p.Cargo.Count > 0 ? doc.IsCargoEdited(p) : null,
                           NavLayout = p.NavLayout is { Count: > 0 } nav
                               ? new Dictionary<string, string>(nav, StringComparer.Ordinal) : null,
                           // An emptied tank is an EMPTY map, not a null one, so the two must stay distinct here:
                           // null means "the def's own amounts", {} means "this container holds nothing".
                           Fill = p.Fill is { } fill
                               ? new Dictionary<string, double>(fill, StringComparer.Ordinal) : null,
                           Cond = p.Condition,
                       })
                       .ToList(),
            Zones = doc.Zones.Select(ToOplanZone).ToList(),
            LooseObjects = doc.LooseObjects
                              .Select(lo => new OplanLoose
                              {
                                  Def = lo.DefName, X = lo.X, Y = lo.Y, Rot = lo.Rot, Qty = lo.Quantity,
                                  Name = lo.CustomName, Z = lo.ZBias == 0 ? null : lo.ZBias,
                                  Cargo = lo.Cargo.Count > 0 ? lo.Cargo.Select(ToOplanCargo).ToList() : null,
                                  Cond = lo.Condition,
                              })
                              .ToList(),
            ExtraMassKg = doc.ExtraMassKg > 0 ? doc.ExtraMassKg : null,
            Kind = doc.Kind == DocumentKind.Ship ? null : doc.Kind.ToString(),
        };
        // Device links as (source, target) index pairs into the parts array (= doc.Placements order); only links
        // whose both endpoints still exist are written (a dangling one, left by an un-undone delete, is pruned here).
        var indexById = new Dictionary<Guid, int>();
        for (var i = 0; i < doc.Placements.Count; i++) indexById[doc.Placements[i].Id] = i;
        foreach (var l in doc.Links)
            if (indexById.TryGetValue(l.Source, out var si) && indexById.TryGetValue(l.Target, out var ti))
                file.Links.Add(new OplanLink { Src = si, Tgt = ti });
        file.DismissedAlerts = doc.DismissedAlerts.OrderBy(k => k, StringComparer.Ordinal).ToList();
        // Only the factions this design's cargo actually references, sorted for a readable diff. A save carries a
        // few hundred, nearly all of them the per-person ones the game mints as it goes, and writing those into
        // every design would bloat the file with names nothing in it names.
        var referenced = doc.Placements.SelectMany(p => p.Cargo).SelectMany(AllFactions)
            .Concat(doc.LooseObjects.SelectMany(l => l.Cargo).SelectMany(AllFactions))
            .ToHashSet(StringComparer.Ordinal);
        var factionNames = doc.FactionNames
            .Where(kv => referenced.Contains(kv.Key))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        file.Factions = factionNames.Count > 0 ? factionNames : null;
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
            ExtraMassKg = ExtraMassKg ?? 0,
            Kind = Enum.TryParse<DocumentKind>(Kind, ignoreCase: true, out var kind) ? kind : DocumentKind.Ship,
        };
        var missing = new List<OplanPart>();
        var byIndex = new Placement?[Parts.Count];   // original part index → placement (null where dropped), for links
        for (var i = 0; i < Parts.Count; i++)
        {
            var part = Parts[i];
            if (part.Def.Length == 0 || catalog.Lookup(part.Def) is null)
            {
                missing.Add(part);
                continue;
            }
            var placement = new Placement
            {
                DefName = part.Def, X = part.X, Y = part.Y, Rot = GridMath.Norm(part.Rot), IsGiven = part.Given,
                OriginStrID = part.Origin, SwappedFromStrID = part.SwappedFrom, SwappedFromDef = part.SwappedFromDef,
                CustomName = Rename.OrNull(part.Name),   // verbatim: an imported name must survive a reopen unchanged
                ZBias = part.Z ?? 0,
                NavLayout = part.NavLayout is { Count: > 0 } nav
                    ? new Dictionary<string, string>(nav, StringComparer.Ordinal) : null,
                Fill = part.Fill is { } fill
                    ? new Dictionary<string, double>(fill, StringComparer.Ordinal) : null,
                // Clamped rather than trusted: the field is hand-editable and a value outside 0..1 would drive
                // the wear shader and the export's StatDamage past the pool the part actually has.
                Condition = Paint.Clamp(part.Cond),
            };
            doc.Add(placement);
            byIndex[i] = placement;
            // Restore the container's contents from the snapshot. Re-mark it edited only when the snapshot is
            // the design's OWN — contents the user authored, which nothing may overwrite. Contents that merely
            // came in with an import stay unmarked, so a write-back can refresh them from the ship it is about
            // to write over (see UpdateDriver) rather than reverting lockers the player has since rearranged.
            // A file from a build before CargoOwn existed only ever wrote a snapshot when it was edited, so a
            // missing flag means owned.
            if (part.Cargo is { Count: > 0 } snap)
            {
                placement.Cargo = FromOplanCargoList(snap, catalog.Lookup(placement.DefName), catalog);
                if (part.CargoOwn ?? true) doc.MarkCargoEdited(placement);
            }
        }
        // Device links: resolve each (source, target) index pair to its placements; skip a pair whose endpoint was
        // dropped (missing-mod part) so a stale index can never wire the wrong parts.
        foreach (var l in Links)
            if (l.Src >= 0 && l.Src < byIndex.Length && l.Tgt >= 0 && l.Tgt < byIndex.Length
                && byIndex[l.Src] is { } src && byIndex[l.Tgt] is { } tgt)
                doc.AddLink(new DeviceLink(src.Id, tgt.Id));
        doc.LoadDismissedAlerts(DismissedAlerts);
        if (Factions is { Count: > 0 }) doc.LoadFactionNames(Factions);
        foreach (var z in Zones) doc.AddZone(FromOplanZone(z));
        // Restore loose floor items whose def still resolves (a missing def is dropped, like a missing part). One
        // per tile: a later duplicate at the same tile simply overwrites, matching the in-editor invariant.
        foreach (var lo in LooseObjects)
            if (lo.Def.Length > 0 && catalog.Lookup(lo.Def) is not null)
                doc.AddLoose(new LooseObject
                {
                    DefName = lo.Def, X = lo.X, Y = lo.Y, Rot = GridMath.Norm(lo.Rot),
                    Quantity = lo.Qty < 1 ? 1 : lo.Qty, ZBias = lo.Z ?? 0,
                    CustomName = Rename.OrNull(lo.Name),   // verbatim, exactly as a part's name (see OplanPart.Name)
                    // AddLoose tops up the item's own pockets, so a file written before deck items held anything
                    // still opens with them (and a file that has them is left alone).
                    Cargo = FromOplanCargoList(lo.Cargo ?? [], catalog.Lookup(lo.Def), catalog),
                    Condition = Paint.Clamp(lo.Cond),
                });
        return (doc, missing);
    }

    /// <summary>Persist a zone: its editable fields plus its tiles as document <c>[x,y]</c> pairs (the doc plane
    /// is unbounded and can be negative, so tiles are stored as coordinates, not flat indices).</summary>
    private static OplanZone ToOplanZone(ShipZone z) => new()
    {
        Name = z.Name,
        Color = [z.Color.R, z.Color.G, z.Color.B, z.Color.A],
        TileConds = [.. z.TileConds],
        CategoryConds = z.CategoryConds.Count > 0 ? [.. z.CategoryConds] : null,
        PersonSpec = z.PersonSpec,
        TargetPSpec = z.TargetPSpec,
        TriggerOnOwner = z.TriggerOnOwner,
        Tiles = z.Tiles.Select(t => new[] { t.X, t.Y }).ToList(),
    };

    /// <summary>Rebuild a <see cref="ShipZone"/> from its snapshot.</summary>
    private static ShipZone FromOplanZone(OplanZone o)
    {
        var c = o.Color;
        var zone = new ShipZone
        {
            Name = o.Name,
            Color = c is { Length: >= 4 } ? new ZoneColor(c[0], c[1], c[2], c[3]) : ZoneColor.Default,
            TileConds = o.TileConds is { } tc ? [.. tc] : [],
            CategoryConds = o.CategoryConds is { } cc ? [.. cc] : [],
            PersonSpec = o.PersonSpec,
            TargetPSpec = o.TargetPSpec,
            TriggerOnOwner = o.TriggerOnOwner,
        };
        foreach (var t in o.Tiles ?? [])
            if (t is { Length: >= 2 }) zone.Tiles.Add((t[0], t[1]));
        return zone;
    }

    /// <summary>Persist a cargo node's identity, authored-ness, grid position and stack — the display/footprint
    /// fields (friendly name, size) are re-resolved from the def on load, so only what can't be re-derived is stored.</summary>
    /// <summary>Every faction id in a cargo subtree, this item's and its descendants'.</summary>
    private static IEnumerable<string> AllFactions(CargoItem c) =>
        c.Factions.Concat(c.Children.SelectMany(AllFactions));

    private static OplanCargo ToOplanCargo(CargoItem c) => new()
    {
        Def = c.DefName,
        StrID = c.StrID,
        Authored = c.Authored,
        Intrinsic = c.Intrinsic,
        Slotted = c.Slotted,
        SlotName = c.SlotName,
        X = c.GridX,
        Y = c.GridY,
        Rot = c.GridRot,
        Stack = c.Stack,
        IsStack = c.IsStack,
        Children = c.Children.Count > 0 ? c.Children.Select(ToOplanCargo).ToList() : null,
        Name = c.CustomName,
        Factions = c.Factions.Count > 0 ? [.. c.Factions] : null,
    };

    /// <summary>
    /// Rebuild one level of a cargo snapshot, healing pockets an older Ostraplan wrote as loose cargo. An
    /// intrinsic child (a garment's pocket, a backpack's pouch) belongs in one of its host's named slots, and a
    /// design saved before that was known holds them unslotted — which is what made an EVA suit reach the game
    /// with no compartments. Re-slot them here, by the same rule the game uses, so reopening a design is enough
    /// to correct it; anything the user genuinely put in a container is left where they put it.
    /// </summary>
    private static List<CargoItem> FromOplanCargoList(IReadOnlyList<OplanCargo> nodes, PartDef? host, Catalog catalog)
    {
        var taken = new HashSet<string>(
            nodes.Where(n => n.Slotted && n.SlotName is { Length: > 0 }).Select(n => n.SlotName!),
            StringComparer.Ordinal);
        return nodes.Select(n => FromOplanCargo(n, host, catalog, taken)).ToList();
    }

    /// <summary>Rebuild a cargo node from its snapshot, re-resolving footprint + friendly name from the catalog.</summary>
    private static CargoItem FromOplanCargo(OplanCargo o, PartDef? host, Catalog catalog, HashSet<string> taken)
    {
        var def = catalog.Lookup(o.Def);
        var (gw, gh) = def?.InvSize ?? (1, 1);
        var children = FromOplanCargoList(o.Children ?? [], def, catalog);
        var slotted = o.Slotted;
        var slotName = o.SlotName;
        if (o.Intrinsic && !slotted && Cargo.FreeSlotFor(host, def, taken) is { } healed)
        {
            taken.Add(healed);
            (slotted, slotName) = (true, healed);
        }
        return new CargoItem(o.StrID, o.Def, def?.Friendly, slotted, children)
        {
            GridX = slotted ? 0 : o.X,
            GridY = slotted ? 0 : o.Y,
            GridRot = GridMath.Norm(o.Rot),
            GridW = gw,
            GridH = gh,
            Stack = o.Stack <= 0 ? 1 : o.Stack,
            IsStack = o.IsStack,
            SlotName = slotName,
            Authored = o.Authored,
            Intrinsic = o.Intrinsic,
            // Verbatim, exactly as a part's name is: an imported name must survive a reopen unchanged, or a no-op
            // write-back would rewrite what the player typed in game.
            CustomName = Rename.OrNull(o.Name),
            Factions = o.Factions is { Count: > 0 } f ? [.. f] : [],
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

    // The ship's in-game identity — edited in the Ship Info dialog, persisted here, and used to pre-fill the
    // export dialog (see ExportMetadata / ShipExport). Additive since format v1: an older build ignores these
    // and round-trips them via Extra. All default to "" so a design that never set them exports exactly as before.
    /// <summary>The in-game display name (the ship's transponder/comms/broker name). Blank ⇒ the exporter falls
    /// back to the design name (or vanilla varied naming when replacing a ship). See <see cref="OplanMeta"/>.</summary>
    [JsonPropertyName("publicName")] public string PublicName { get; set; } = "";
    [JsonPropertyName("make")] public string Make { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("year")] public string Year { get; set; } = "";
    [JsonPropertyName("designation")] public string Designation { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";

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
    /// <summary>Save-edit: the save item this part was before an uninstall/install or door toggle re-stated it,
    /// and the def it carried then (see <see cref="Placement.SwappedFromStrID"/>). Additive; a design written
    /// before these existed simply has neither, and its re-stated parts price as new material as they used to.</summary>
    [JsonPropertyName("swappedFrom")] public string? SwappedFrom { get; set; }
    [JsonPropertyName("swappedFromDef")] public string? SwappedFromDef { get; set; }
    /// <summary>A name the user gave this part (see <see cref="Placement.CustomName"/>). Null — and omitted — for a
    /// part carrying its stock name. Additive at format v1: an older build ignores it and round-trips it through
    /// <see cref="Extra"/>, so a renamed design opened in one loses nothing.</summary>
    [JsonPropertyName("name")] public string? Name { get; set; }
    /// <summary>The manual draw-order bias a Move Back / Move Forward wrote onto this part (see
    /// <see cref="Placement.ZBias"/>). Null — and omitted — for a part left in the automatic order, which is
    /// almost all of them. Additive at format v1: an older build ignores it and round-trips it through
    /// <see cref="Extra"/>, so a re-stacked design opened in one draws in the old order but loses nothing.</summary>
    [JsonPropertyName("z")] public int? Z { get; set; }
    /// <summary>A full snapshot of this container's contents, so the design can be read, priced and exported
    /// without the save it came from. Null for a container holding nothing. See <see cref="OplanCargo"/>.</summary>
    [JsonPropertyName("cargo")] public List<OplanCargo>? Cargo { get; set; }
    /// <summary>Whether <see cref="Cargo"/> is the design's <b>own</b> — contents the user authored in the
    /// inventory editor (see <see cref="ShipDocument.IsCargoEdited"/>) — as against contents that arrived with an
    /// import and are still the ship's. Only the latter may be refreshed from the ship a write-back is about to
    /// write over. Null — and omitted — when the container holds nothing, and in a file written before this
    /// existed, where a snapshot was only ever written for an edited container and so reads back as owned.</summary>
    [JsonPropertyName("cargoOwn")] public bool? CargoOwn { get; set; }
    /// <summary>A nav console's screen arrangement, when the user laid one out in the arrange dialog (see
    /// <see cref="Placement.NavLayout"/>): module GUI-prefab key → anchor rect, <c>""</c> for a shelved module.
    /// Null — and omitted — for every other part and for a console left at the game's own arrangement, which is
    /// computed rather than stored. Additive at format v1, like the rest: an older build ignores it and
    /// round-trips it through <see cref="Extra"/>.</summary>
    [JsonPropertyName("navLayout")] public Dictionary<string, string>? NavLayout { get; set; }
    /// <summary>How much of what this container holds, when the user set it (see <see cref="Placement.Fill"/>):
    /// payload condition → amount. Null — and omitted — for every part left at the fill its def ships with, which
    /// is nearly all of them. An <b>empty object</b> is meaningful and different: it is a container deliberately
    /// emptied. Additive at format v1, like the rest: an older build ignores it and round-trips it through
    /// <see cref="Extra"/>, so a design opened in one is priced and flown on stock fills but loses nothing.</summary>
    [JsonPropertyName("fill")] public Dictionary<string, double>? Fill { get; set; }
    /// <summary>The condition the designer painted on this part, 1.0 pristine to 0.0 gone (see
    /// <see cref="Placement.Condition"/>). Null — and omitted — for a part nobody painted, which is almost all of
    /// them and which takes whatever the export's own wear setting decides. Additive at format v1, like the rest:
    /// an older build ignores it and round-trips it through <see cref="Extra"/>, so a design opened in one exports
    /// on the whole-ship wear setting alone but loses nothing.</summary>
    [JsonPropertyName("cond")] public double? Cond { get; set; }
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

    /// <summary>Part of the parent object rather than cargo put into it (a garment's pockets); see
    /// <see cref="CargoItem.Intrinsic"/>. Omitted when false.</summary>
    [JsonPropertyName("intrinsic")] public bool Intrinsic { get; set; }
    [JsonPropertyName("slotted")] public bool Slotted { get; set; }
    [JsonPropertyName("slot")] public string? SlotName { get; set; }
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("rot")] public int Rot { get; set; }
    [JsonPropertyName("stack")] public int Stack { get; set; } = 1;
    [JsonPropertyName("isStack")] public bool IsStack { get; set; }
    [JsonPropertyName("children")] public List<OplanCargo>? Children { get; set; }
    /// <summary>A name the user gave this item (see <see cref="CargoItem.CustomName"/>), on the same terms as a
    /// part's <see cref="OplanPart.Name"/>. Null — and omitted — for an item carrying its def's stock name.
    /// Additive at format v1: an older build ignores it and round-trips it through <see cref="Extra"/>.</summary>
    [JsonPropertyName("name")] public string? Name { get; set; }
    /// <summary>The factions this item belongs to (see <see cref="CargoItem.Factions"/>), raw ids. Null — and
    /// omitted — for the great majority that belong to none. Their friendly names live in the file's root
    /// <see cref="OplanFile.Factions"/> table, because a save mints factions at runtime and nothing in the
    /// install lists them.</summary>
    [JsonPropertyName("factions")] public List<string>? Factions { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>One persisted zone (see <see cref="OplanFile.Zones"/>): the editable fields plus the covered
/// tiles as document <c>[x,y]</c> pairs. Colour is <c>[r,g,b,a]</c> (0..1).</summary>
public sealed class OplanZone
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("color")] public double[]? Color { get; set; }
    [JsonPropertyName("tileConds")] public List<string>? TileConds { get; set; }
    [JsonPropertyName("categoryConds")] public List<string>? CategoryConds { get; set; }
    [JsonPropertyName("personSpec")] public string? PersonSpec { get; set; }
    [JsonPropertyName("targetPSpec")] public string? TargetPSpec { get; set; }
    [JsonPropertyName("triggerOnOwner")] public bool TriggerOnOwner { get; set; }
    [JsonPropertyName("tiles")] public List<int[]>? Tiles { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>One persisted loose floor item (see <see cref="OplanFile.LooseObjects"/>): its def and tile pose. The
/// sprite/footprint/friendly name are re-resolved from the def on load, so only the identity and position are stored.</summary>
public sealed class OplanLoose
{
    [JsonPropertyName("def")] public string Def { get; set; } = "";
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("rot")] public int Rot { get; set; }
    [JsonPropertyName("qty")] public int Qty { get; set; } = 1;   // stacked count (>=1); absent/0 in an older file → single
    /// <summary>A name the user gave this deck item (see <see cref="LooseObject.CustomName"/>), on the same terms
    /// as a part's <see cref="OplanPart.Name"/>. Null — and omitted — for an item carrying its stock name, which
    /// is nearly all of them. Additive: an older build ignores it and round-trips it through
    /// <see cref="Extra"/>, so a design opened in one loses no names.</summary>
    [JsonPropertyName("name")] public string? Name { get; set; }
    /// <summary>The manual draw-order bias (see <see cref="OplanPart.Z"/>); null for the automatic order.</summary>
    [JsonPropertyName("z")] public int? Z { get; set; }

    /// <summary>What the item holds (see <see cref="LooseObject.Cargo"/>): a backpack's pouches and whatever was
    /// put in them. Null — and omitted — for the great majority of deck items, which hold nothing.</summary>
    [JsonPropertyName("cargo")] public List<OplanCargo>? Cargo { get; set; }
    /// <summary>The condition the designer painted on this deck item, on the same terms as a part's
    /// <see cref="OplanPart.Cond"/>. Null — and omitted — for an item nobody painted. A stack carries one
    /// condition for the whole pile, which is where the game keeps it.</summary>
    [JsonPropertyName("cond")] public double? Cond { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>One persisted device signal connection (see <see cref="OplanFile.Links"/>): a directed pair of
/// indices into the <c>parts</c> array (<c>src</c> drives <c>tgt</c>).</summary>
public sealed class OplanLink
{
    [JsonPropertyName("src")] public int Src { get; set; }
    [JsonPropertyName("tgt")] public int Tgt { get; set; }
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
