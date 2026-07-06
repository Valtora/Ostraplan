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
    /// True for structure the user did not author — an imported ship's existing parts. The game applies its
    /// placement law (Item.CheckFit sockets, the airlock envelope) only to <b>new</b> construction and
    /// <b>never re-validates what's already there</b>; so given parts are exempt from the legality scan (a valid
    /// imported ship must flag nothing). It is <b>cleared the moment the part is moved or rotated</b> — relocating
    /// a part IS an authoring act, so the law must re-apply (a device dragged off its wall should flag "needs a
    /// wall alongside"). The legality scan is build-order-aware (see <see cref="ProblemScan"/>), so dragging a
    /// whole intact compartment stays clean without needing immunity; only genuinely exotic stacks moved out of
    /// their construction order may over-flag. Given-ness is set at import (and inherited by a same-class
    /// re-skin/replace, which builds a fresh placement); an unmoved imported part keeps its immunity.
    /// </summary>
    public bool IsGiven { get; set; }

    /// <summary>
    /// The <c>strID</c> of the save item this part came from, set when the document was imported from a save
    /// <b>for editing</b> (<see cref="SaveEditImport"/>); null for parts the user added. Unlike
    /// <see cref="IsGiven"/> (which clears on move) it is <b>preserved</b> across move / rotate / group-rotate —
    /// the part keeps its save identity so its live-state CO and cargo travel with it on write-back, and the diff
    /// classifies it as <i>moved</i> rather than deleted+new. It is dropped only by operations that create a genuinely new
    /// part: duplicate, paste, paint, box-fill, symmetry-mirror, a def-changing replace / re-skin, and layout-only
    /// template/save import (all of which build a fresh <see cref="Placement"/> without copying it). Drives the
    /// save-edit diff (<see cref="ShipDiff"/>). Init-only: identity is fixed at creation and never reassigned.
    /// </summary>
    public string? OriginStrID { get; init; }
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

    /// <summary>
    /// When this design was imported from a save <b>for editing</b>, the save + ship it came from — so an
    /// edit can be written back into a copy of that save. Persisted in the .oplan (see <see cref="OplanFile"/>)
    /// and restored on reopen; null for from-scratch, template, or layout-only save designs. The heavy
    /// per-item <see cref="SaveShipContext"/> is held alongside in-session and rebuilt from this on reopen.
    /// </summary>
    public SaveSourceRef? SourceSave { get; set; }

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
            copy.Add(new Placement { DefName = p.DefName, X = p.X, Y = p.Y, Rot = p.Rot, IsGiven = p.IsGiven, OriginStrID = p.OriginStrID });
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

    /// <summary>
    /// The bounding rect of a part's ABOVE-FLOOR body at its current pose — its footprint minus any
    /// under-floor-only reservation. The large fuel tanks project a 7×7 under-floor storage ring
    /// (<c>TILSubfloorAdds</c>, IsSubTile only) beneath a 3×3 visible body (<c>TIL2DeckAdds</c>, adds
    /// IsObstruction); the game treats only the body as "there" for selection and interaction, so
    /// that is what Ostraplan hit-tests, selects and outlines. Ordinary parts have no under-floor
    /// ring, so this equals the whole footprint. The placement law is unaffected — it keeps using the
    /// full socket grid (<see cref="CheckFit"/> reads the item's sockets directly).
    /// </summary>
    public (int X, int Y, int W, int H) BodyBounds(Placement p)
    {
        var (w, h) = FootprintOf(p);
        var under = UnderFloorCells(p);
        if (under.Count == 0) return (p.X, p.Y, w, h);

        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (var r = 0; r < h; r++)
            for (var c = 0; c < w; c++)
                if (!under.Contains((p.X + c, p.Y + r)))
                {
                    minX = Math.Min(minX, p.X + c); minY = Math.Min(minY, p.Y + r);
                    maxX = Math.Max(maxX, p.X + c); maxY = Math.Max(maxY, p.Y + r);
                }
        return maxX < minX ? (p.X, p.Y, w, h) : (minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>World tiles a part reserves as under-floor storage (IsSubTile, no solid body) at its pose.</summary>
    private HashSet<(int, int)> UnderFloorCells(Placement p)
    {
        var cells = new HashSet<(int, int)>();
        if (Part(p) is not { } part) return cells;
        var effRot = part.Item.HasSpriteSheet ? 0 : GridMath.Norm(p.Rot);
        var (rw, rh, adds) = GridMath.Rotate(part.Item.SocketAdds, part.Item.Width, part.Item.Height, effRot);
        if (adds.Length != rw * rh) return cells;
        for (var r = 0; r < rh; r++)
            for (var c = 0; c < rw; c++)
                if (Catalog.IsUnderFloorLoot(adds[r * rw + c]))
                    cells.Add((p.X + c, p.Y + r));
        return cells;
    }

    /// <summary>True if the tile falls inside the part's above-floor body (see <see cref="BodyBounds"/>).</summary>
    public bool Covers(Placement p, int x, int y)
    {
        var (bx, by, bw, bh) = BodyBounds(p);
        return x >= bx && x < bx + bw && y >= by && y < by + bh;
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

    // The index (and thus hit-testing/selection) uses the ABOVE-FLOOR body, not the socket footprint:
    // clicking a tank's under-floor ring hits the floor there, not the tank centred on it.
    private IEnumerable<(int, int)> Tiles(Placement p)
    {
        var (bx, by, bw, bh) = BodyBounds(p);
        for (var r = 0; r < bh; r++)
            for (var c = 0; c < bw; c++)
                yield return (bx + c, by + r);
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
        // Moving a part is an authoring act: clear its given-ness so the placement law re-applies (a device
        // dragged off its wall must flag). OriginStrID is KEPT — the part is still the same save item, so its
        // live-state CO and cargo travel with it and the diff sees a move, not a delete+new.
        p.IsGiven = false;
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
        // repositioning re-authors the part: clear given-ness so the law re-applies; keep OriginStrID (identity)
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
