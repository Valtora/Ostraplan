namespace Ostraplan.Core;

/// <summary>One placed part. X/Y = top-left tile of the ROTATED footprint; Rot in {0,90,180,270}.</summary>
public sealed class Placement
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string DefName { get; init; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Rot { get; set; }
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

    public PartDef? Part(Placement p) => Catalog.ByDefName.GetValueOrDefault(p.DefName);

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
    /// Placements sorted bottom-to-top for painting: render layer (floor → wall →
    /// fixture → conduit), then any explicit item nLayer, then top-left Y, then
    /// insertion order. Floors sort under whatever is placed on them.
    /// </summary>
    public IEnumerable<Placement> DrawOrder() =>
        _placements.OrderBy(p => Catalog.RenderLayer(Part(p)))
                   .ThenBy(p => Part(p)?.Item.NLayer ?? 0)
                   .ThenBy(p => p.Y)
                   .ThenBy(p => _order.GetValueOrDefault(p.Id));

    /// <summary>Topmost placement covering the tile, or null.</summary>
    public Placement? HitTest(int x, int y) =>
        DrawOrder().LastOrDefault(p => Covers(p, x, y));

    /// <summary>Every placement covering the tile, topmost first (reverse draw order) — for layer picking.</summary>
    public IReadOnlyList<Placement> HitTestStack(int x, int y)
    {
        var stack = DrawOrder().Where(p => Covers(p, x, y)).ToList();
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

    // ---- mutations (command implementations only) ----

    internal void Add(Placement p)
    {
        _placements.Add(p);
        _order[p.Id] = _seq++;
        if (Part(p) is { } part) Conds.Apply(p, part.Item, +1);
        RaiseChanged();
    }

    internal void Remove(Placement p)
    {
        if (!_placements.Remove(p)) return;
        _order.Remove(p.Id);
        if (Part(p) is { } part) Conds.Apply(p, part.Item, -1);
        RaiseChanged();
    }

    internal void MoveTo(Placement p, int x, int y)
    {
        var part = Part(p);
        if (part is not null) Conds.Apply(p, part.Item, -1);
        p.X = x;
        p.Y = y;
        if (part is not null) Conds.Apply(p, part.Item, +1);
        RaiseChanged();
    }

    internal void SetPose(Placement p, int x, int y, int rot)
    {
        var part = Part(p);
        if (part is not null) Conds.Apply(p, part.Item, -1);
        p.X = x;
        p.Y = y;
        p.Rot = GridMath.Norm(rot);
        if (part is not null) Conds.Apply(p, part.Item, +1);
        RaiseChanged();
    }

    internal void Clear()
    {
        _placements.Clear();
        _order.Clear();
        Conds.Clear();
        _seq = 0;
        RaiseChanged();
    }
}
