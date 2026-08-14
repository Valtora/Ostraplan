using System.IO;
using System.Text.Json.Nodes;

namespace Ostraplan.Core;

/// <summary>One ship template on disk, for the browser: its display name, the source that
/// provides it (core or a mod label), and the file path.</summary>
public sealed record ShipFileEntry(string Name, string Origin, string Path);

/// <summary>A def an import couldn't resolve (its geometry isn't in the loaded data) and how
/// many tiles it dropped — surfaced so the user can enable the right mod and re-import.</summary>
public sealed record SkippedDef(string DefName, int Count);

/// <summary>
/// What an import should bring in besides the ship's structure.
///
/// <para>These exist because the three import paths used to disagree. Importing a ship "for editing" kept every
/// container's contents; importing the same ship layout-only, or importing a template, dropped them without
/// offering a choice — which is what people were reporting as cargo going missing depending on which menu item
/// they happened to use.</para>
/// </summary>
/// <param name="ContainerContents">Bring each container's contents in as viewable, editable cargo.</param>
/// <param name="LooseItems">Bring items lying loose on the deck in as loose objects.</param>
public sealed record ImportOptions(bool ContainerContents = true, bool LooseItems = true)
{
    /// <summary>Everything the ship carries. The default, and what the for-editing path always uses.</summary>
    public static readonly ImportOptions Everything = new(true, true);

    /// <summary>Structure only — what every path except the save-edit one used to do, with no way to say otherwise.</summary>
    public static readonly ImportOptions LayoutOnly = new(false, false);
}

/// <summary>
/// The outcome of an import: the new document, unresolved defs, the tallies of what came in and what did not, and
/// the ship's name.
/// </summary>
/// <param name="ContainedDropped">Contained sub-objects (cargo, tools, installed modules) <b>left behind</b>.</param>
/// <param name="SystemDropped">System objects (loot spawners) left behind. Always dropped: they populate a ship at
/// runtime and are not structure.</param>
public sealed record ImportResult(
    ShipDocument Doc, IReadOnlyList<SkippedDef> Skipped, int ContainedDropped, int SystemDropped,
    string ShipName, int PartCount)
{
    /// <summary>Contained sub-objects brought in as container contents.</summary>
    public int ContainedKept { get; init; }

    /// <summary>Of <see cref="ContainedDropped"/>, how many are carried by crew (their holder is not one of the
    /// ship's items). No import option can ever bring these in — crew are never imported — so the report must not
    /// point the user at the "Container contents" checkbox for them.</summary>
    public int CrewDropped { get; init; }

    /// <summary>Of <see cref="ContainedDropped"/>, how many sit inside a container lying on the deck. A deck item
    /// imports as a <see cref="LooseObject"/>, which holds no cargo, so these are left behind whatever the options
    /// say — a limitation, not a choice.</summary>
    public int DeckDropped { get; init; }

    /// <summary>Items lying loose on the deck that were brought in as loose objects, stack members included.</summary>
    public int LooseKept { get; init; }

    /// <summary>Items lying loose on the deck that were left behind, stack members included.</summary>
    public int LooseDropped { get; init; }

    /// <summary>Nav consoles that arrived with nothing inside and were stocked with the standard module set
    /// (see <see cref="NavConsole.StockEmptyConsoles"/>). Reported because it is an addition to the design, not
    /// something the source ship carried.</summary>
    public int NavConsolesStocked { get; init; }

    /// <summary>Modules installed by that stocking, across every console. Not counted in
    /// <see cref="ContainedKept"/> — nothing brought them in.</summary>
    public int NavModulesInstalled { get; init; }

    /// <summary>Of those, how many the console screen has no room for and so start in the console's edit-menu
    /// tray (see <see cref="NavConsole.Arrange"/>) — aboard and usable, but not on screen until placed.</summary>
    public int NavModulesTrayed { get; init; }
}

/// <summary>
/// Imports a game ship template (core or mod <c>data/ships/*.json</c>) into an editable document —
/// the forward of the export mapping. Each stored item's centre <c>(fX,fY)</c> + CCW rotation becomes
/// a top-left tile placement via the shared <see cref="ShipGrid.TemplateTile"/>. Items whose geometry
/// can't be resolved at all (a def whose mod isn't loaded) are skipped and reported; everything else —
/// including the many non-buildable defs a real ship uses (raw hull, systems, tiles) — resolves through
/// <see cref="Catalog.Lookup"/> and both renders and analyses. Crew, wear and damage are never read, so an
/// import is always pristine; container contents and loose deck items come in or not per <see cref="ImportOptions"/>.
/// </summary>
public static class TemplateImport
{
    /// <summary>Every ship file across core + loaded mods, later source winning a filename clash, name-sorted.</summary>
    public static IReadOnlyList<ShipFileEntry> ListShipFiles(DataIndex index)
    {
        var byName = new Dictionary<string, ShipFileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in index.Sources)
        {
            var dir = Path.Combine(source.DataDir, "ships");
            if (!Directory.Exists(dir)) continue;
            foreach (var path in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories))
                byName[Path.GetFileNameWithoutExtension(path)] = new ShipFileEntry(
                    Path.GetFileNameWithoutExtension(path), source.Label, path);
        }
        return byName.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The actual <c>strName</c> of a ship file's primary ship (the largest, matching <see cref="LoadFile"/>'s
    /// choice) — the authoritative override key for "replace this ship" export, which the filename only usually
    /// matches. Null if the file can't be parsed or holds no ship.</summary>
    public static string? ResolveShipStrName(string path)
    {
        try
        {
            return ShipTemplate.ParseFile(File.ReadAllText(path)).ToList()
                .OrderByDescending(s => s.Items.Count).FirstOrDefault()?.Name;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Parse a ship file and import its ship (the largest, for multi-ship batch files).</summary>
    public static ImportResult LoadFile(string path, Catalog catalog, ImportOptions? options = null)
    {
        var text = File.ReadAllText(path);
        var ships = ShipTemplate.ParseFileChecked(text, out var failure);
        var tmpl = ships.OrderByDescending(s => s.Items.Count).FirstOrDefault()
            ?? throw new InvalidDataException($"'{Path.GetFileName(path)}' contains no ship.\n\n{failure}");
        // the raw-JSON parse only feeds container contents — skip it when they aren't wanted (the retrofit
        // picker's layout-only read is a hot path, and a real save ship is megabytes of JSON)
        var wantCargo = (options ?? ImportOptions.Everything).ContainerContents;
        return Build(tmpl, catalog, retainOrigin: false, options, wantCargo ? ShipJson.Largest(text) : null);
    }

    /// <summary>Build an editable document from a parsed template. Placed parts carry no save identity — for the
    /// save-edit import that tags each part, see <see cref="SaveEditImport"/>. Container contents need the ship's
    /// raw JSON (they live in its <c>aItems</c>/<c>aCOs</c>), so this overload brings in structure and loose deck
    /// items only; <see cref="LoadFile"/> and <see cref="SaveImport"/> pass the JSON and get cargo too.</summary>
    public static ImportResult FromTemplate(ShipTemplate tmpl, Catalog catalog, ImportOptions? options = null) =>
        Build(tmpl, catalog, retainOrigin: false, options);

    /// <summary>
    /// Shared import core. With <paramref name="retainOrigin"/> set (the save-edit path), each placed part
    /// is tagged with its source item <c>strID</c> via <see cref="Placement.OriginStrID"/>; otherwise
    /// (template / layout-only save import) that stays null and the part is treated as new construction on
    /// any later write-back.
    ///
    /// <para><paramref name="shipNode"/> is the same ship as raw JSON, needed only to build container contents
    /// (see <see cref="Cargo.BuildForest"/>). The save-edit path passes null and attaches its own cargo from the
    /// full context it retains, so the two never do it twice.</para>
    /// </summary>
    internal static ImportResult Build(
        ShipTemplate tmpl, Catalog catalog, bool retainOrigin,
        ImportOptions? options = null, JsonNode? shipNode = null)
    {
        var opts = options ?? ImportOptions.Everything;
        var doc = new ShipDocument(catalog);
        var skipped = new Dictionary<string, int>(StringComparer.Ordinal);
        var systems = 0;
        int looseKept = 0, looseDropped = 0;

        // Structural parts by their source strID, so container contents can be hung on the right ones below.
        var placedByStrId = new Dictionary<string, Placement>(StringComparer.Ordinal);

        // The item graph, for the two walks below: a deck stack's members (absorbed into a quantity), and the
        // root-holder classification that tells crew-carried gear apart from a rack's contents.
        var byStrId = new Dictionary<string, TemplateItem>(StringComparer.Ordinal);
        foreach (var item in tmpl.Items)
            if (item.StrID is { Length: > 0 } id) byStrId.TryAdd(id, item);
        var childrenOf = tmpl.Items.Where(i => i.ParentId is not null).ToLookup(i => i.ParentId!, StringComparer.Ordinal);

        // Contained items consumed some other way than as container contents (today: deck-stack members folded
        // into their head's quantity) — excluded from every "left behind" tally.
        var absorbed = new HashSet<TemplateItem>();
        var taken = 0;   // contained items attached as container contents
        int navConsoles = 0, navModules = 0, navTrayed = 0;   // nav consoles stocked with the standard module set

        using (doc.SuspendChanged())
        {
            foreach (var item in tmpl.Items)
            {
                if (item.Contained) continue;   // handled as cargo below, absorbed into a stack, or left behind
                var part = catalog.Lookup(item.DefName);
                if (part is null)
                {
                    skipped[item.DefName] = skipped.GetValueOrDefault(item.DefName) + 1;
                    continue;
                }
                if (part.StartingConds.Contains("IsSystem"))   // loot spawners, fire, explosions — runtime, not structure
                {
                    systems++;
                    continue;
                }

                // Not everything sitting at a tile is structure. A tool, a shirt, a piece of scrap is lying on the
                // deck, and the game tells them apart exactly as the palette does: installed structure carries
                // IsInstalled and a loose item does not. These used to import as grid placements, which made a
                // shirt a buildable part — counted in the bill of materials and re-checked against the placement
                // law.
                //
                // NOT on the save-edit path, though, and the reason is worth stating. That import exists to be
                // written back, and its invariant is that a no-op write-back preserves every item by strID. Only a
                // Placement carries an OriginStrID; a LooseObject has no identity to carry. Reclassifying there
                // would leave the save's own item untouched (nothing marks it deleted) while the loose object
                // wrote a fresh copy beside it, so every loose item on the deck would double on each round trip.
                // Keeping them as placements is what keeps that write-back lossless.
                if (!retainOrigin && !part.StartingConds.Contains("IsInstalled"))
                {
                    // A stack on the deck persists as a head plus same-def members parented to it (the same shape
                    // Cargo.BuildForest collapses). Fold the members into a quantity, or a pile of 20 scrap would
                    // import as one piece with 19 reported left behind — and export 1 where the save had 20.
                    var members = item.StrID is { Length: > 0 } hid && !part.IsContainer
                        ? childrenOf[hid].Where(m => m.DefName == item.DefName
                            && (m.StrID is not { Length: > 0 } mid || !childrenOf[mid].Any())).ToList()
                        : [];
                    var quantity = 1 + members.Count;
                    if (!opts.LooseItems)
                    {
                        looseDropped += quantity;
                        absorbed.UnionWith(members);
                        continue;
                    }
                    var (lcol, lrow, lrot) = ShipGrid.TemplateTile(
                        item.FX, item.FY, item.FRotation, part.Item.Width, part.Item.Height, tmpl.VShipPosX, tmpl.VShipPosY);
                    doc.AddLoose(new LooseObject { DefName = item.DefName, X = lcol, Y = lrow, Rot = lrot, Quantity = quantity });
                    looseKept += quantity;
                    absorbed.UnionWith(members);
                    continue;
                }

                var (col, row, rot) = ShipGrid.TemplateTile(
                    item.FX, item.FY, item.FRotation, part.Item.Width, part.Item.Height, tmpl.VShipPosX, tmpl.VShipPosY);
                // imported structure is "given" — pre-existing, not user-authored, so the placement
                // law (which the game applies only to new construction) doesn't re-validate it
                var placement = new Placement
                {
                    DefName = item.DefName, X = col, Y = row, Rot = rot, IsGiven = true,
                    OriginStrID = retainOrigin ? item.StrID : null,
                    // A name the ship already carried, from its Rename panel. Core ships use it and so do players
                    // labelling their racks, and it used to be dropped on the way in (see Rename).
                    CustomName = item.CustomName,
                };
                new PlaceCommand(placement).Do(doc);
                if (item.StrID is { Length: > 0 } sid) placedByStrId[sid] = placement;
            }

            // Container contents. The save-edit path passes no shipNode: it attaches cargo itself from the full
            // context it keeps, and doing it twice would be wasted work. Everything else gets it here, which is
            // what makes the three import routes agree.
            if (opts.ContainerContents && shipNode is not null && placedByStrId.Count > 0)
                taken = AttachCargo(doc, catalog, shipNode, placedByStrId, retainOrigin);

            // A nav console that came in empty gets the standard module set, so the planner shows what the export
            // will actually spawn (see NavConsole). Not on the save-edit path: its cargo is attached afterwards
            // from the retained context, which would overwrite this — SaveEditImport calls the same fill once that
            // is done.
            if (opts.ContainerContents && !retainOrigin)
                (navConsoles, navModules, navTrayed) = NavConsole.StockEmptyConsoles(doc, catalog);

            // Convert stored zones to document coordinates. On import the document origin coincides with the
            // game grid origin, so a flat index maps straight to a doc tile; indices past the grid are dropped
            // (a corrupt/stale ship). Zones are pure overlays — no placement law, no tile conds.
            var tileCount = tmpl.NCols * tmpl.NRows;
            foreach (var sz in tmpl.Zones)
            {
                var zone = new ShipZone
                {
                    Name = sz.Name,
                    Color = sz.Color,
                    TileConds = [.. sz.TileConds],
                    CategoryConds = [.. sz.CategoryConds],
                    PersonSpec = sz.PersonSpec,
                    TargetPSpec = sz.TargetPSpec,
                    TriggerOnOwner = sz.TriggerOnOwner,
                };
                foreach (var idx in sz.Tiles)
                    if (idx >= 0 && idx < tileCount && tmpl.NCols > 0)
                        zone.Tiles.Add(ZoneGeometry.IndexToDoc(idx, tmpl.NCols));
                doc.AddZone(zone);
            }
        }

        // What is each left-behind contained item actually inside? Walking to the root holder splits the tally
        // into things a checkbox could have fetched (a placed container's contents), things no option ever can
        // (crew-carried gear — the holder is not one of the ship's items), and a deck container's contents (a
        // LooseObject holds no cargo). The report words each differently, so the split is computed here where the
        // graph is.
        int crewDropped = 0, deckDropped = 0, structureRooted = 0;
        foreach (var item in tmpl.Items)
        {
            if (!item.Contained || absorbed.Contains(item)) continue;
            var root = RootOf(item, byStrId);
            if (root is null) { crewDropped++; continue; }
            if (root.StrID is { Length: > 0 } rid && placedByStrId.ContainsKey(rid)) { structureRooted++; continue; }
            // the root never made the grid: a deck item, or a skipped/system def (whose own notes tell that story)
            if (catalog.Lookup(root.DefName) is { StartingConds.Length: > 0 } rootDef
                && !rootDef.StartingConds.Contains("IsInstalled") && !rootDef.StartingConds.Contains("IsSystem"))
                deckDropped++;
            else
                structureRooted++;
        }

        var skippedList = skipped
            .Select(kv => new SkippedDef(kv.Key, kv.Value))
            .OrderByDescending(s => s.Count).ThenBy(s => s.DefName, StringComparer.Ordinal)
            .ToList();
        // taken counts raw-JSON nodes and structureRooted counts parsed items; they agree on well-formed ships,
        // and the clamp keeps a malformed one (an item with no strName) from driving the tally negative.
        var structureDropped = Math.Max(0, structureRooted - taken);
        return new ImportResult(doc, skippedList, structureDropped + deckDropped + crewDropped, systems,
            ShipName(tmpl), doc.Placements.Count)
        {
            // the stocked nav modules are Ostraplan's own addition, not something the ship carried in
            ContainedKept = doc.Placements.Sum(p => CountCargo(p.Cargo)) - navModules,
            CrewDropped = crewDropped,
            DeckDropped = deckDropped,
            LooseKept = looseKept,
            LooseDropped = looseDropped,
            NavConsolesStocked = navConsoles,
            NavModulesInstalled = navModules,
            NavModulesTrayed = navTrayed,
        };
    }

    /// <summary>The top-level item ultimately holding <paramref name="item"/>, walking <see cref="TemplateItem.ParentId"/>
    /// — or null when the chain leaves the ship's items, which is what a crew member's carried gear does (crew are
    /// not items). Cycle-guarded.</summary>
    private static TemplateItem? RootOf(TemplateItem item, Dictionary<string, TemplateItem> byStrId)
    {
        var current = item;
        for (var hops = 0; hops < 100 && current.ParentId is { } parent; hops++)
        {
            if (!byStrId.TryGetValue(parent, out var holder)) return null;
            if (holder == current) return current;   // self-parented corruption
            current = holder;
        }
        return current;
    }

    /// <summary>
    /// Hang each container's contents on the placement that holds them, from the ship's raw JSON. Returns how many
    /// contained items were taken, so the caller can report the rest as left behind.
    ///
    /// <para>Outside the save-edit path there is no save to re-read the cargo from later, so the contents are
    /// marked <b>authored</b>: that is what makes them persist into the <c>.oplan</c> and survive a reopen. The
    /// save-edit path deliberately does not do this — its cargo is re-attached from the save it stays linked to.</para>
    /// </summary>
    private static int AttachCargo(
        ShipDocument doc, Catalog catalog, JsonNode shipNode,
        Dictionary<string, Placement> placedByStrId, bool retainOrigin)
    {
        var (itemsById, cosById, children) = ShipJson.Index(shipNode);
        var taken = 0;
        foreach (var (strId, placement) in placedByStrId)
        {
            if (!children.ContainsKey(strId)) continue;
            var forest = Cargo.BuildForest(strId, children, itemsById, cosById, catalog);
            if (forest.Count == 0) continue;
            placement.Cargo = retainOrigin ? forest : Cargo.AsAuthored(forest);
            if (!retainOrigin) doc.MarkCargoEdited(placement);
            taken += CountCargo(forest);
        }
        return taken;
    }

    /// <summary>Every node in a cargo forest, nested ones included.</summary>
    private static int CountCargo(IReadOnlyList<CargoItem> forest) =>
        forest.Sum(c => 1 + CountCargo(c.Children));

    /// <summary>The friendliest name for an imported ship: its player-given <c>publicName</c>
    /// (e.g. from a save) when it's a real one, else its <c>strName</c>.</summary>
    public static string ShipName(ShipTemplate tmpl) =>
        tmpl.PublicName is { Length: > 0 } pn && pn != "$TEMPLATE" ? pn
        : string.IsNullOrWhiteSpace(tmpl.Name) ? "Imported ship" : tmpl.Name;
}
