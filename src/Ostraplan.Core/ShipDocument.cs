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

    public ShipDocument(Catalog catalog)
    {
        Catalog = catalog;
        Conds = new TileConds(catalog);
    }

    public IReadOnlyList<Placement> Placements => _placements;

    public PartDef? Part(Placement p) => Catalog.ByDefName.GetValueOrDefault(p.DefName);

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

    /// <summary>Placements sorted for painting: layer, then top-left Y, then insertion order.</summary>
    public IEnumerable<Placement> DrawOrder() =>
        _placements.OrderBy(p => Part(p)?.Item.NLayer ?? 0)
                   .ThenBy(p => p.Y)
                   .ThenBy(p => _order.GetValueOrDefault(p.Id));

    /// <summary>Topmost placement covering the tile, or null.</summary>
    public Placement? HitTest(int x, int y) =>
        DrawOrder().LastOrDefault(p => Covers(p, x, y));

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
        Changed?.Invoke();
    }

    internal void Remove(Placement p)
    {
        if (!_placements.Remove(p)) return;
        _order.Remove(p.Id);
        if (Part(p) is { } part) Conds.Apply(p, part.Item, -1);
        Changed?.Invoke();
    }

    internal void MoveTo(Placement p, int x, int y)
    {
        var part = Part(p);
        if (part is not null) Conds.Apply(p, part.Item, -1);
        p.X = x;
        p.Y = y;
        if (part is not null) Conds.Apply(p, part.Item, +1);
        Changed?.Invoke();
    }

    internal void SetPose(Placement p, int x, int y, int rot)
    {
        var part = Part(p);
        if (part is not null) Conds.Apply(p, part.Item, -1);
        p.X = x;
        p.Y = y;
        p.Rot = GridMath.Norm(rot);
        if (part is not null) Conds.Apply(p, part.Item, +1);
        Changed?.Invoke();
    }

    internal void Clear()
    {
        _placements.Clear();
        _order.Clear();
        Conds.Clear();
        _seq = 0;
        Changed?.Invoke();
    }
}
