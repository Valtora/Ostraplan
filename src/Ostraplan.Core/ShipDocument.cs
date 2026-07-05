namespace Ostraplan.Core;

/// <summary>One placed part. X/Y = top-left tile of the ROTATED footprint; Rot in {0,90,180,270}.</summary>
public sealed class Placement
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string DefName { get; init; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Rot { get; set; }

    /// <summary>
    /// True for structure the user did not author — an imported ship's existing parts. The game
    /// applies its placement law (Item.CheckFit sockets, the airlock envelope) only to <b>new</b>
    /// construction, never re-validating what's already there; so given parts are exempt from the
    /// legality scan (a valid imported ship must flag nothing). Cleared the moment the user moves or
    /// rotates the part — an edit is new construction and is checked. Precursor to save-edit's origin id.
    /// </summary>
    public bool IsGiven { get; set; }
}

/// <summary>
/// The design being edited: an unbounded tile plane of placements plus the
/// accumulated tile conditions. All mutation goes through the command stack.
/// </summary>
public sealed class ShipDocument
{
    private readonly List<Placement> _placements = [];
    private long _seq;
    private readonly Dictionary<Guid, long> _order = [];   // insertion order for stable draw/hit priority
    private readonly Dictionary<(int, int), List<Placement>> _byTile = [];   // spatial index: tile -> parts covering it

    public Catalog Catalog { get; }
    public TileConds Conds { get; }
    public string? FilePath { get; set; }

    public event Action? Changed;

    private int _batchDepth;
    private bool _batchDirty;

    public ShipDocument(Catalog catalog)
    {
        Catalog = catalog;
        Conds = new TileConds(catalog);
    }

    /// <summary>
    /// Coalesce the <see cref="Changed"/> notifications of a burst of mutations into a
    /// single event fired when the scope disposes — so a group rotation / multi-part move /
    /// paste runs the (heavy) problem scan and repaint once, not once per part. Tile
    /// conditions still update per mutation, so mid-batch reads are correct.
    /// </summary>
    public IDisposable SuspendChanged()
    {
        _batchDepth++;
        return new BatchScope(this);
    }

    private void RaiseChanged()
    {
        if (_batchDepth > 0) { _batchDirty = true; return; }
        Changed?.Invoke();
    }

    private sealed class BatchScope(ShipDocument doc) : IDisposable
    {
        public void Dispose()
        {
            if (--doc._batchDepth != 0 || !doc._batchDirty) return;
            doc._batchDirty = false;
            doc.Changed?.Invoke();
        }
    }

    public IReadOnlyList<Placement> Placements => _placements;

    /// <summary>
    /// An independent copy for off-thread analysis: the same placements (poses + given-ness) with
    /// their own accumulated tile conditions, sharing the catalog. Safe to read on a background
    /// thread while the original keeps being edited on the UI thread. Cheap — rebuilding conditions
    /// for a few thousand parts is a millisecond or two.
    /// </summary>
    public ShipDocument Snapshot()
    {
        var copy = new ShipDocument(Catalog);
        foreach (var p in _placements)
            copy.Add(new Placement { DefName = p.DefName, X = p.X, Y = p.Y, Rot = p.Rot, IsGiven = p.IsGiven });
        return copy;
    }

    public PartDef? Part(Placement p) => Catalog.Lookup(p.DefName);

    /// <summary>The primary airlock is fixed to the ship: no move/rotate/delete/duplicate.</summary>
    public bool IsLocked(Placement p) => p.DefName == Catalog.PrimaryDocksysDef;

    public (int W, int H) FootprintOf(Placement p)
    {
        var part = Part(p);
        return part is null ? (1, 1) : GridMath.Size(part.Item.Width, part.Item.Height, p.Rot);
    }

    public bool Covers(Placement p, int x, int y)
    {
        var (w, h) = FootprintOf(p);
        return x >= p.X && x < p.X + w && y >= p.Y && y < p.Y + h;
    }

    /// <summary>
    /// Order a set of placements bottom-to-top for painting/hit-testing: render layer (floor → wall
    /// → fixture → conduit), then any explicit item nLayer, then top-left Y, then insertion order.
    /// Floors sort under whatever is placed on them.
    /// </summary>
    private IEnumerable<Placement> InDrawOrder(IEnumerable<Placement> parts) =>
        parts.OrderBy(p => Catalog.RenderLayer(Part(p)))
             .ThenBy(p => Part(p)?.Item.NLayer ?? 0)
             .ThenBy(p => p.Y)
             .ThenBy(p => _order.GetValueOrDefault(p.Id));

    public IEnumerable<Placement> DrawOrder() => InDrawOrder(_placements);

    /// <summary>Every placement covering the tile (spatial-index lookup, unordered). Empty off the ship.</summary>
    public IReadOnlyList<Placement> PlacementsAt(int x, int y) =>
        _byTile.TryGetValue((x, y), out var list) ? list : [];

    /// <summary>Topmost placement covering the tile, or null.</summary>
    public Placement? HitTest(int x, int y) => InDrawOrder(PlacementsAt(x, y)).LastOrDefault();

    /// <summary>Every placement covering the tile, topmost first (reverse draw order) — for layer picking.</summary>
    public IReadOnlyList<Placement> HitTestStack(int x, int y)
    {
        var stack = InDrawOrder(PlacementsAt(x, y)).ToList();
        stack.Reverse();
        return stack;
    }

    public (int MinX, int MinY, int MaxX, int MaxY)? Bounds()
    {
        if (_placements.Count == 0) return null;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        foreach (var p in _placements)
        {
            var (w, h) = FootprintOf(p);
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X + w - 1);
            maxY = Math.Max(maxY, p.Y + h - 1);
        }
        return (minX, minY, maxX, maxY);
    }

    // ---- spatial index ----

    private IEnumerable<(int, int)> Tiles(Placement p)
    {
        var (w, h) = FootprintOf(p);
        for (var r = 0; r < h; r++)
            for (var c = 0; c < w; c++)
                yield return (p.X + c, p.Y + r);
    }

    private void Index(Placement p)
    {
        foreach (var t in Tiles(p))
        {
            if (!_byTile.TryGetValue(t, out var list)) _byTile[t] = list = [];
            list.Add(p);
        }
    }

    private void Unindex(Placement p)
    {
        foreach (var t in Tiles(p))
            if (_byTile.TryGetValue(t, out var list) && list.Remove(p) && list.Count == 0)
                _byTile.Remove(t);
    }

    // ---- mutations (command implementations only) ----

    internal void Add(Placement p)
    {
        _placements.Add(p);
        _order[p.Id] = _seq++;
        Index(p);
        if (Part(p) is { } part) Conds.Apply(p, part.Item, +1);
        RaiseChanged();
    }

    internal void Remove(Placement p)
    {
        if (!_placements.Remove(p)) return;
        Unindex(p);
        _order.Remove(p.Id);
        if (Part(p) is { } part) Conds.Apply(p, part.Item, -1);
        RaiseChanged();
    }

    internal void MoveTo(Placement p, int x, int y)
    {
        var part = Part(p);
        Unindex(p);   // reindex under the new pose
        if (part is not null) Conds.Apply(p, part.Item, -1);
        p.X = x;
        p.Y = y;
        p.IsGiven = false;   // moved = new construction; it's now the user's and gets validated
        if (part is not null) Conds.Apply(p, part.Item, +1);
        Index(p);
        RaiseChanged();
    }

    internal void SetPose(Placement p, int x, int y, int rot)
    {
        var part = Part(p);
        Unindex(p);
        if (part is not null) Conds.Apply(p, part.Item, -1);
        p.X = x;
        p.Y = y;
        p.Rot = GridMath.Norm(rot);
        p.IsGiven = false;
        if (part is not null) Conds.Apply(p, part.Item, +1);
        Index(p);
        RaiseChanged();
    }

    internal void Clear()
    {
        _placements.Clear();
        _order.Clear();
        _byTile.Clear();
        Conds.Clear();
        _seq = 0;
        RaiseChanged();
    }
}
