namespace Ostraplan.Core;

/// <summary>One container on the way to an item: what it is called, and which one it is. The name is what a
/// person reads and the id is what tells two identically named crates apart, and a location view needs both.</summary>
public sealed record ManifestStep(string Id, string Name);

/// <summary>
/// One item on the manifest: a single deck object, or one node of one container's cargo tree.
///
/// <para>It carries where it is as well as what it is, because the whole point of the manifest is that an item's
/// location is what you cannot see: a pouch three containers deep inside a locker on the far side of the ship
/// renders as nothing at all on the plan.</para>
/// </summary>
/// <param name="DefName">The internal def name, which is what groups identical items onto one line.</param>
/// <param name="Name">What to call it: the name the user gave it, else the def's, else the raw def name.</param>
/// <param name="CustomName">The name the user gave it, or null. Null and <see cref="Name"/> is the stock one.</param>
/// <param name="Count">Units this entry stands for: a deck stack's quantity, a cargo stack's count, else 1.</param>
/// <param name="UnitValue">The def's base value in credits, or 0 when it declares none.</param>
/// <param name="Intrinsic">True for a container the host spawns as part of itself (see <see cref="CargoItem.Intrinsic"/>).</param>
/// <param name="OnDeck">True when the entry <i>is</i> a deck object rather than something inside one.</param>
/// <param name="Where">Where it sits, ready to show: "on the deck", or "in Storage Locker ▸ Small Pouch".</param>
/// <param name="Host">The deck object or placed part it belongs to — what the grid can be pointed at.</param>
/// <param name="ItemId">The <see cref="CargoItem.StrID"/> this stands for, or null when the entry is the deck
/// object itself. It is the only stable handle across a cargo edit, since the tree is immutable and every edit
/// replaces nodes.</param>
/// <param name="Zone">The zone the entry's host stands in, or null when it stands in none. A <i>label</i> rather
/// than a filter: scoping the whole report to one zone was the only thing zones did here, which cannot express
/// "show me everything, arranged by where it is".</param>
/// <param name="HostLabel">The deck object or placed part the entry belongs to, named as the design names it. The
/// root of the entry's location, and for a deck object's own entry, itself.</param>
/// <param name="Path">The containers between the host and this entry, outermost first, excluding the host. Empty
/// for something sitting directly in the host. <see cref="Where"/> is this same chain written out for reading; this
/// is the form something can be arranged by, and it carries each container's id as well as its name because two
/// crates called the same thing in the same locker are still two crates.</param>
public sealed record ManifestEntry(
    string DefName,
    string Name,
    string? CustomName,
    int Count,
    double UnitValue,
    bool Intrinsic,
    bool OnDeck,
    string Where,
    RenderItem Host,
    string? ItemId,
    string? Zone = null,
    string HostLabel = "",
    IReadOnlyList<ManifestStep>? Path = null)
{
    /// <summary>The containers between the host and this entry, never null.</summary>
    public IReadOnlyList<ManifestStep> Nesting => Path ?? [];

    /// <summary>What this entry is worth at the game's base price, across its whole stack.</summary>
    public double Value => UnitValue * Count;

    /// <summary>True when the entry is cargo inside something, rather than the deck object itself.</summary>
    public bool IsCargo => ItemId is not null;
}

/// <summary>One line of the manifest: every item of one def, wherever on the ship they are.</summary>
/// <param name="DefName">The def every entry on this line shares.</param>
/// <param name="Friendly">The def's own name, which is the line's label — an entry that carries a name of its
/// own shows it on its own row, so a renamed crate is still found under "Storage Crate".</param>
/// <param name="Count">Total units across every entry, stacks counted out.</param>
/// <param name="Value">What the line is worth at base price.</param>
/// <param name="Entries">The individual items, in the order the ship was walked.</param>
public sealed record ManifestLine(
    string DefName,
    string Friendly,
    int Count,
    double Value,
    IReadOnlyList<ManifestEntry> Entries)
{
    /// <summary>Units of this def lying on the decks.</summary>
    public int OnDeckCount => Entries.Where(e => e.OnDeck).Sum(e => e.Count);

    /// <summary>Units of this def inside something.</summary>
    public int ContainedCount => Count - OnDeckCount;

    /// <summary>True when every entry on the line is a container its host spawned as part of itself.</summary>
    public bool AllIntrinsic => Entries.All(e => e.Intrinsic);
}

/// <summary>
/// Every item on a design, grouped by def. Not a bill of materials: see <see cref="ItemManifest"/> for why the
/// two are deliberately separate reports.
/// </summary>
/// <param name="Lines">One line per def, sorted by name.</param>
/// <param name="TotalCount">Every unit counted, stacks counted out.</param>
/// <param name="OnDeckCount">Units lying on the decks.</param>
/// <param name="ContainedCount">Units inside something.</param>
/// <param name="IntrinsicCount">Units that are a host's own pockets, pouches or data stores.</param>
/// <param name="TotalValue">What the lot is worth at base price.</param>
public sealed record Manifest(
    IReadOnlyList<ManifestLine> Lines,
    int TotalCount,
    int OnDeckCount,
    int ContainedCount,
    int IntrinsicCount,
    double TotalValue)
{
    /// <summary>How many kinds of item the design carries.</summary>
    public int DistinctDefs => Lines.Count;

    /// <summary>Nothing at all in scope.</summary>
    public bool IsEmpty => Lines.Count == 0;

    /// <summary>An empty manifest, for a scope that holds nothing.</summary>
    public static Manifest Empty { get; } = new([], 0, 0, 0, 0, 0);
}

/// <summary>What a node in the location tree stands for, so a UI can draw the levels differently without
/// re-deriving which is which from its depth.</summary>
public enum ManifestNodeKind
{
    /// <summary>A painted zone, or the bucket for everything standing in none.</summary>
    Zone,

    /// <summary>The deck object or installed part an item ultimately sits in.</summary>
    Host,

    /// <summary>A container inside the host, at any depth.</summary>
    Container,

    /// <summary>An item that holds nothing.</summary>
    Item,
}

/// <summary>
/// One level of the manifest arranged <b>by where things are</b> rather than by what they are.
///
/// <para>The by-type grouping answers "what does this ship carry, and how much of it": a stock list, and the right
/// shape for that question. It cannot answer the other one. A ship's organisation <i>is</i> its nesting — a rack in
/// an engineering bay holding a backpack holding conduits is three deliberate decisions, and a flat list of
/// conduits with a location string against each has thrown all three away. This keeps them.</para>
/// </summary>
/// <param name="Label">What to call this level.</param>
/// <param name="Kind">Which level it is.</param>
/// <param name="Entry">The item this node stands for, or null for a zone (which is not a thing you can hold).
/// An interior node usually has one: a container is an item as well as a place.</param>
/// <param name="Children">What is inside, zones first by document order, then by name.</param>
/// <param name="Count">Everything at or under this node, so a zone's figure is its whole contents.</param>
/// <param name="Value">The same, in credits.</param>
public sealed record ManifestNode(
    string Label,
    ManifestNodeKind Kind,
    ManifestEntry? Entry,
    IReadOnlyList<ManifestNode> Children,
    int Count,
    double Value)
{
    /// <summary>What this node is in its own right, not counting what is inside it. A rack holding 40 conduits is
    /// one rack: without the two figures apart, a container's row either hides its contents or double-counts
    /// itself.</summary>
    public int OwnCount => Entry?.Count ?? 0;

    public double OwnValue => Entry?.Value ?? 0;

    /// <summary>Items under this node, excluding the node itself.</summary>
    public int ContainedCount => Count - OwnCount;
}

/// <summary>
/// Walks a design for every item it carries, wherever it is: lying on a deck, inside an installed container, or
/// nested any depth inside either (#36).
///
/// <para><b>Items, not parts.</b> An installed locker is structure — it is built from an install kit and it is the
/// <see cref="BillOfMaterials"/>'s business. What is <i>in</i> it is cargo, which no report answered before this
/// one, and which the plan cannot show you because a container renders as a closed box. So a placed part never
/// gets a line of its own here; it appears only as the place an item was found. A crate <i>lying on the deck</i>
/// does get a line, because a deck object is an item and not structure.</para>
///
/// <para><b>A stack is one entry.</b> A cargo stack persists as a lead item plus copies of itself as children
/// (see <see cref="CargoItem.IsStack"/>), so the walk does not descend into one: twenty rounds in a locker are one
/// entry counting twenty, not twenty entries and not a container of itself. A deck stack works the same way
/// through <see cref="LooseObject.Quantity"/>.</para>
///
/// <para>Pure and window-free, so the walk and the zone scoping can be regression tested without a UI.</para>
/// </summary>
public static class ItemManifest
{
    /// <summary>Separates the containers on an item's path, the way the app writes a menu trail.</summary>
    private const string PathSep = " ▸ ";

    /// <summary>
    /// Every item in scope, grouped by def.
    /// </summary>
    /// <param name="doc">The design to walk.</param>
    /// <param name="zoneTiles">Restrict the walk to items sitting on these tiles (a <see cref="ShipZone.Tiles"/>
    /// set), or null for the whole ship. A container's whole cargo tree is in scope when the container is, however
    /// deep it nests, because contents sit where their host does — which is also what a shop window means by
    /// scoping to a zone.</param>
    public static Manifest Build(ShipDocument doc, IReadOnlySet<(int X, int Y)>? zoneTiles = null)
    {
        ArgumentNullException.ThrowIfNull(doc);

        var catalog = doc.Catalog;
        var entries = new List<ManifestEntry>();

        void Walk(IReadOnlyList<CargoItem> items, RenderItem host, List<string> path, List<ManifestStep> steps,
                  string? zone)
        {
            var where = "in " + string.Join(PathSep, path);
            // path[0] is the host; steps holds only what is between it and the item, which is what the location
            // view arranges by. Kept beside the display path rather than derived from it, because a name is not an
            // identity: two crates called "Electrical" in one locker are two places, and only the ids say so.
            var nesting = steps.ToArray();
            foreach (var item in items)
            {
                var name = item.CustomName ?? item.Friendly ?? item.DefName;
                entries.Add(new ManifestEntry(
                    item.DefName, name, item.CustomName,
                    Count: item.IsStack ? item.Stack : 1,
                    UnitValue: catalog.Lookup(item.DefName)?.BasePrice ?? 0,
                    Intrinsic: item.Intrinsic,
                    OnDeck: false,
                    Where: where,
                    Host: host,
                    ItemId: item.StrID,
                    Zone: zone,
                    HostLabel: path[0],
                    Path: nesting));

                if (item.IsStack || item.Children.Count == 0) continue;   // a stack holds copies of itself, not cargo
                path.Add(name);
                steps.Add(new ManifestStep(item.StrID, name));
                Walk(item.Children, host, path, steps, zone);
                path.RemoveAt(path.Count - 1);
                steps.RemoveAt(steps.Count - 1);
            }
        }

        // Which zone a host stands in, for labelling. First match wins where zones overlap: a thing is listed
        // somewhere rather than twice, and the document's own order is the tie-break a user can see and change.
        string? ZoneOf(RenderItem host)
        {
            var tiles = TilesOf(doc, host);
            foreach (var z in doc.Zones)
                if (tiles.Any(z.Tiles.Contains))
                    return string.IsNullOrWhiteSpace(z.Name) ? "unnamed zone" : z.Name;
            return null;
        }

        // Deck items first, and in a fixed reading order of the report's own. LooseObjects enumerates in the order
        // things were dropped, which is not an order anybody reading a manifest can see — without this the same
        // ship could list its strays differently depending on how it was built, which is exactly the report you
        // cannot trust to compare against itself.
        foreach (var lo in doc.LooseObjects.OrderBy(o => o.Y).ThenBy(o => o.X))
        {
            var host = new RenderItem(null, lo);
            // Scoped by the whole footprint, the same as a placement above: an item is in the zone when any of it
            // is, which is also what ZoneOf labels it by.
            if (zoneTiles is not null && !TilesOf(doc, host).Any(zoneTiles.Contains)) continue;
            var def = catalog.Lookup(lo.DefName);
            var name = Rename.Display(lo, def);
            var zone = ZoneOf(host);
            entries.Add(new ManifestEntry(
                lo.DefName, name, lo.CustomName,
                Count: Math.Max(1, lo.Quantity),
                UnitValue: def?.BasePrice ?? 0,
                Intrinsic: false,
                OnDeck: true,
                Where: "on the deck",
                Host: host,
                ItemId: null,
                Zone: zone,
                // A deck object is its own host: it sits on the deck rather than inside anything, so the location
                // view hangs it directly off its zone instead of under a container that does not exist.
                HostLabel: name,
                Path: []));
            if (lo.Cargo.Count > 0) Walk(lo.Cargo, host, [name], [], zone);
        }

        foreach (var p in doc.Placements)
        {
            if (p.Cargo.Count == 0) continue;
            if (zoneTiles is not null && !TilesOf(doc, p).Any(zoneTiles.Contains)) continue;
            var host = new RenderItem(p, null);
            Walk(p.Cargo, host, [Rename.Display(p, doc.Part(p))], [], ZoneOf(host));
        }

        var lines = entries
            .GroupBy(e => e.DefName, StringComparer.Ordinal)
            .Select(g => new ManifestLine(
                g.Key,
                catalog.Lookup(g.Key)?.Friendly ?? g.Key,
                g.Sum(e => e.Count),
                g.Sum(e => e.Value),
                g.ToList()))
            .OrderBy(l => l.Friendly, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.DefName, StringComparer.Ordinal)   // two defs sharing a friendly name still sort stably
            .ToList();

        return new Manifest(
            lines,
            TotalCount: entries.Sum(e => e.Count),
            OnDeckCount: entries.Where(e => e.OnDeck).Sum(e => e.Count),
            ContainedCount: entries.Where(e => !e.OnDeck).Sum(e => e.Count),
            IntrinsicCount: entries.Where(e => e.Intrinsic).Sum(e => e.Count),
            TotalValue: entries.Sum(e => e.Value));
    }

    /// <summary>The bucket for everything standing in no zone at all. Named rather than left blank, because a
    /// list that ends in an unlabelled heap reads as a rendering fault.</summary>
    public const string NoZone = "Not in a zone";

    /// <summary>
    /// Rearrange a set of manifest lines into the location tree: zone, then the thing it is in, then whatever that
    /// nests inside, then the items.
    ///
    /// <para>Takes lines rather than a <see cref="Manifest"/> so the caller can hand in a filtered set and get a
    /// tree of exactly what is on screen. Both views then answer for the same items, which is what stops a filter
    /// meaning two different things depending on how the list happens to be grouped.</para>
    /// </summary>
    public static IReadOnlyList<ManifestNode> ByLocation(IEnumerable<ManifestLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        // Built mutably and frozen on the way out: a tree assembled by inserting paths cannot be built from the
        // leaves up, and every alternative is a lot of re-allocation for a shape nothing looks at until it is done.
        var roots = new List<Builder>();

        foreach (var entry in lines.SelectMany(l => l.Entries))
        {
            // Zones are keyed by name because a name is all a zone is. Everything below is keyed by id, so two
            // crates called the same thing in one locker stay two crates.
            var zone = Find(roots, entry.Zone ?? NoZone, ManifestNodeKind.Zone, entry.Zone ?? NoZone);
            var node = Find(zone.Children, entry.HostLabel, ManifestNodeKind.Host, entry.Host.Id.ToString());

            // A deck object's own entry IS its host node, so it lands on the node rather than under it. Anything
            // else walks the containers between the host and itself before taking its own place.
            if (entry.OnDeck && entry.ItemId is null) { node.Entry = entry; continue; }

            foreach (var step in entry.Nesting)
                node = Find(node.Children, step.Name, ManifestNodeKind.Container, step.Id);

            // The same key the walk above uses, which is what makes a container and the path through it one node.
            // Keying the two differently put every non-empty crate on the tree twice: once as the place its cargo
            // was reached through, and once as the item it also is.
            var leaf = Find(node.Children, entry.Name, ManifestNodeKind.Item, entry.ItemId!);
            leaf.Entry = entry;
            // A container met as an item first and walked through later (or the other way round) is one node, and
            // it is a place as soon as anything is found inside it.
            if (leaf.Children.Count > 0) leaf.Kind = ManifestNodeKind.Container;
        }

        var tree = Freeze(roots);
        // A design with no zones at all would otherwise open on a single "Not in a zone" heading holding the entire
        // ship: a level that separates nothing, indents everything, and has to be opened before the view says
        // anything. Drop it and start at what things are in. The bucket stays wherever it is one of several,
        // because there it does separate something.
        return tree is [{ Kind: ManifestNodeKind.Zone, Label: NoZone } only] ? only.Children : tree;

        static Builder Find(List<Builder> among, string label, ManifestNodeKind kind, string id)
        {
            var found = among.FirstOrDefault(b => b.Id == id);
            if (found is not null)
            {
                // Reached as a place before it was reached as an item: keep the label the item gave it, since that
                // is the one carrying a custom name.
                if (kind == ManifestNodeKind.Container) found.Kind = kind;
                return found;
            }
            var made = new Builder { Label = label, Kind = kind, Id = id };
            among.Add(made);
            return made;
        }

        static IReadOnlyList<ManifestNode> Freeze(List<Builder> nodes)
        {
            var made = nodes.Select(n =>
            {
                var kids = Freeze(n.Children);
                var own = n.Entry?.Count ?? 0;
                var ownValue = n.Entry?.Value ?? 0;
                // A container is one item AND a place, so its figure is itself plus everything under it. Counting
                // only the contents loses the crate; counting only the crate loses the point.
                return new ManifestNode(n.Label, n.Kind, n.Entry, kids,
                    own + kids.Sum(k => k.Count), ownValue + kids.Sum(k => k.Value));
            }).ToList();

            // Fullest first, then by name. The rollup is the whole point of arranging by location, so the level
            // holding most of the ship should be the one the eye lands on: sorting by name alone opened a
            // 1377-item hold on forty coffee machines, each holding two drink pouches, and buried every store that
            // mattered below the fold. Name is the tie-break, so equal levels stay in a stable, findable order.
            // Sorted after building rather than before, because the figure being sorted on is the subtree's.
            made.Sort((a, b) =>
            {
                var byBulk = b.Count.CompareTo(a.Count);
                return byBulk != 0 ? byBulk : string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
            });
            return made;
        }
    }

    private sealed class Builder
    {
        public string Label = "";
        public ManifestNodeKind Kind;
        public string Id = "";
        public ManifestEntry? Entry;
        public List<Builder> Children { get; } = [];
    }

    /// <summary>
    /// The tiles a manifest entry's host occupies — what to point the grid at, and what a zone test asks about.
    /// A placed part answers with its above-floor body (<see cref="ShipDocument.BodyBounds"/>) rather than its
    /// whole footprint, so a fuel tank's under-floor ring does not drag the view out or put the tank in a zone it
    /// only reaches beneath the deck. A deck item answers with its rotated footprint, which is the area the canvas
    /// draws it over.
    ///
    /// <para>It used to answer with its anchor tile alone, and that is the fault behind "a loose item in two zones
    /// takes the top left corner, whichever way it is rotated": the anchor <i>is</i> the top-left of the rotated
    /// footprint, so rotating an item could never change which zone it was filed under. Most loose items are
    /// bigger than one tile, so this is the common case rather than an edge one.</para>
    /// </summary>
    public static IReadOnlyList<(int X, int Y)> TilesOf(ShipDocument doc, RenderItem host)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return host.Placement is { } p ? TilesOf(doc, p) : [.. doc.LooseTiles(host.Loose!)];
    }

    private static List<(int X, int Y)> TilesOf(ShipDocument doc, Placement p)
    {
        var (bx, by, bw, bh) = doc.BodyBounds(p);
        var tiles = new List<(int X, int Y)>(Math.Max(1, bw * bh));
        for (var y = by; y < by + bh; y++)
            for (var x = bx; x < bx + bw; x++)
                tiles.Add((x, y));
        return tiles;
    }

    /// <summary>
    /// Find an entry's item again in its host's live cargo tree. The tree is immutable and every edit replaces
    /// nodes, so an entry's <see cref="CargoItem"/> reference would go stale the moment anything is renamed or
    /// removed; the id is what survives, exactly as <c>CargoInfoWindow</c> re-reads by id rather than holding one.
    /// Null when the item is gone (an edit or an undo took it).
    /// </summary>
    public static CargoItem? Resolve(RenderItem host, string itemId)
    {
        var root = host.Placement is { } p ? p.Cargo : host.Loose?.Cargo ?? [];
        return Find(root, itemId);
    }

    private static CargoItem? Find(IReadOnlyList<CargoItem> items, string id)
    {
        foreach (var item in items)
        {
            if (item.StrID == id) return item;
            if (Find(item.Children, id) is { } hit) return hit;
        }
        return null;
    }
}
