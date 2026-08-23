namespace Ostraplan.Core;

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
    string? ItemId)
{
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

        void Walk(IReadOnlyList<CargoItem> items, RenderItem host, List<string> path)
        {
            var where = "in " + string.Join(PathSep, path);
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
                    ItemId: item.StrID));

                if (item.IsStack || item.Children.Count == 0) continue;   // a stack holds copies of itself, not cargo
                path.Add(name);
                Walk(item.Children, host, path);
                path.RemoveAt(path.Count - 1);
            }
        }

        // Deck items first, and in a fixed reading order. LooseObjects comes off a tile-keyed dictionary, whose
        // enumeration order is an implementation detail — without this the same design could list its strays in a
        // different order twice, which is exactly the report you cannot trust to compare against itself.
        foreach (var lo in doc.LooseObjects.OrderBy(o => o.Y).ThenBy(o => o.X))
        {
            if (zoneTiles is not null && !zoneTiles.Contains((lo.X, lo.Y))) continue;
            var def = catalog.Lookup(lo.DefName);
            var name = Rename.Display(lo, def);
            var host = new RenderItem(null, lo);
            entries.Add(new ManifestEntry(
                lo.DefName, name, lo.CustomName,
                Count: Math.Max(1, lo.Quantity),
                UnitValue: def?.BasePrice ?? 0,
                Intrinsic: false,
                OnDeck: true,
                Where: "on the deck",
                Host: host,
                ItemId: null));
            if (lo.Cargo.Count > 0) Walk(lo.Cargo, host, [name]);
        }

        foreach (var p in doc.Placements)
        {
            if (p.Cargo.Count == 0) continue;
            if (zoneTiles is not null && !TilesOf(doc, p).Any(zoneTiles.Contains)) continue;
            Walk(p.Cargo, new RenderItem(p, null), [Rename.Display(p, doc.Part(p))]);
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

    /// <summary>
    /// The tiles a manifest entry's host occupies — what to point the grid at, and what a zone test asks about.
    /// A placed part answers with its above-floor body (<see cref="ShipDocument.BodyBounds"/>) rather than its
    /// whole footprint, so a fuel tank's under-floor ring does not drag the view out or put the tank in a zone it
    /// only reaches beneath the deck. A deck item is one tile, which is the design's own model for it.
    /// </summary>
    public static IReadOnlyList<(int X, int Y)> TilesOf(ShipDocument doc, RenderItem host)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return host.Placement is { } p ? TilesOf(doc, p) : [(host.X, host.Y)];
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
