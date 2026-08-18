namespace Ostraplan.Core;

/// <summary>One item laid out on an inventory grid: the <see cref="CargoItem"/> it represents, its top-left
/// cell, its footprint in tiles, and how many are stacked in it.</summary>
public sealed record PackedItem(CargoItem Item, int X, int Y, int W, int H, int Count);

/// <summary>The result of packing a container's loose cargo onto its grid: the final grid size (which may have
/// grown past the declared size to fit everything) and where each item block landed.</summary>
public sealed record GridLayoutResult(int Width, int Height, IReadOnlyList<PackedItem> Items);

/// <summary>A free cell for an item, and the rotation it has to be in to sit there (0 for the item's declared
/// footprint, 90 for the transpose).</summary>
public sealed record FreeCell(int X, int Y, int Rot);

/// <summary>
/// Lays a container's loose cargo onto its inventory grid the way the game does when the inventory window opens
/// (<c>GUIInventoryItem.AddToWindow</c>): for each item in order, honour its stored cell when free, otherwise
/// take the unoccupied cell nearest it, otherwise the first free cell — a faithful port of
/// <c>GridLayout.FindNearestUnoccupiedTile</c>/<c>FindFirstUnoccupiedTile</c>.
///
/// <para>Real saves leave most contained items at (0,0) (a container never opened in-game never materialised its
/// layout), so this packing is what makes the viewer look like the game rather than a pile at the origin.
/// Identical items sharing a cell collapse into one stacked block (mirroring the game's stack merge, without its
/// full stack-limit logic).</para>
///
/// <para><b>Rotation is part of the footprint.</b> The game's own search never turns an item to make it fit:
/// <c>FindNearestUnoccupiedTile</c> reads <c>itemWidthOnGrid</c>/<c>itemHeightOnGrid</c> as they stand, because in
/// game a rotation is something a player does by hand to an item already picked up
/// (<c>GUIInventory.RotateCWSelected</c> swaps the pair with no fit check and leaves validity to the drop).
/// Ostraplan's add is an authoring operation the game has no counterpart for, so it may try the transpose
/// (<see cref="FirstFreeCellRotated"/>). What that produces is a state the game reproduces exactly, because a
/// rotated item's swapped footprint survives a save round trip: the reload sets <c>Item.fLastRotation</c>, whose
/// setter runs <c>Item.RotateCW</c>, which swaps <c>nWidthInTiles</c>/<c>nHeightInTiles</c> — the very fields
/// <c>GetWidthHeightForCO</c> then reads back. (Verified against 1.0.0.11. The one hole is a def declaring
/// <c>inventoryWidth</c>/<c>inventoryHeight</c>, which overrides the item geometry and is <b>not</b> swapped on
/// reload; no core def declares either, and <see cref="PartDef.InvSize"/> mirrors the same precedence.)</para>
/// </summary>
public static class InventoryGrid
{
    /// <summary>The grid size a container falls back to when it declares none — the game's <c>Container</c>
    /// default.</summary>
    private const int DefaultGrid = 6;

    /// <summary>Pack the loose (non-slotted) children of a container onto a <paramref name="gridW"/>×
    /// <paramref name="gridH"/> grid (each ≤0 falls back to 6, the game's default). The grid grows if the items
    /// genuinely don't fit, so a viewer never silently hides cargo.</summary>
    public static GridLayoutResult Pack(int gridW, int gridH, IReadOnlyList<CargoItem> loose)
    {
        var width = gridW > 0 ? gridW : DefaultGrid;
        var height = gridH > 0 ? gridH : DefaultGrid;

        // Collapse same-def items sharing a stored cell into one stacked block, preserving first-seen order.
        // Rotation is part of the key: two same-def items parked at one cell in different orientations are two
        // separately placed items rather than a stack, and merging them would draw one under the other's footprint.
        var blocks = new List<Block>();
        var byKey = new Dictionary<(string, int, int, int), Block>();
        foreach (var it in loose)
        {
            var key = (it.DefName, it.GridX, it.GridY, GridMath.Norm(it.GridRot));
            if (byKey.TryGetValue(key, out var existing)) { existing.Count += Math.Max(1, it.Stack); continue; }
            var b = new Block(it, it.GridX, it.GridY, Math.Max(1, it.EffW), Math.Max(1, it.EffH), Math.Max(1, it.Stack));
            byKey[key] = b;
            blocks.Add(b);
        }

        // An item bigger than the declared grid is a data defect, not something to hide: grow to fit it rather
        // than clamp its footprint, which would draw it inside a space it cannot occupy while the capacity rule
        // reported it as fitting.
        foreach (var b in blocks)
        {
            width = Math.Max(width, b.W);
            height = Math.Max(height, b.H);
        }

        var occupied = new HashSet<(int, int)>();
        var placed = new List<PackedItem>(blocks.Count);
        foreach (var b in blocks)
        {
            var (x, y) = Nearest(occupied, width, ref height, b.W, b.H, b.DesiredX, b.DesiredY);
            Occupy(occupied, x, y, b.W, b.H);
            placed.Add(new PackedItem(b.Item, x, y, b.W, b.H, b.Count));
        }

        return new GridLayoutResult(width, height, placed);
    }

    /// <summary>
    /// The first free cell (row-major) that fits a <paramref name="w"/>×<paramref name="h"/> item in a
    /// <paramref name="gridW"/>×<paramref name="gridH"/> container already holding <paramref name="loose"/>, or
    /// <c>null</c> when it will not fit within the declared grid — the capacity rule ("the Law" for cargo). The
    /// existing cargo is packed the way the game lays it out on open, so the free cell matches what the player
    /// would see. Stacks and multi-tile items are honoured. Unlike <see cref="Pack"/> this never grows the grid,
    /// and it never turns the item: see <see cref="FirstFreeCellRotated"/> for that.
    /// </summary>
    public static (int X, int Y)? FirstFreeCell(int gridW, int gridH, IReadOnlyList<CargoItem> loose, int w, int h) =>
        FirstFreeCellRotated(gridW, gridH, loose, w, h, canRotate: false) is { } cell ? (cell.X, cell.Y) : null;

    /// <summary>
    /// The first free cell for a <paramref name="w"/>×<paramref name="h"/> item, trying its declared footprint
    /// first and its transpose second when <paramref name="canRotate"/> — so a 1×3 missile takes an upright slot
    /// while any remain and lies flat once none do. <see cref="FreeCell.Rot"/> is the rotation the item has to
    /// carry to sit there. <c>null</c> when neither orientation fits the declared grid.
    ///
    /// <para>Upright-first, one item at a time, is deliberate. It fills a container the way a player would, and it
    /// keeps the result stable as the quantity grows (adding one more never re-orients the ones already placed),
    /// which is what keeps <see cref="CargoEdit.MaxAddable"/>'s binary search monotonic.</para>
    /// </summary>
    public static FreeCell? FirstFreeCellRotated(
        int gridW, int gridH, IReadOnlyList<CargoItem> loose, int w, int h, bool canRotate)
    {
        var width = gridW > 0 ? gridW : DefaultGrid;
        var height = gridH > 0 ? gridH : DefaultGrid;
        var iw = Math.Max(1, w);
        var ih = Math.Max(1, h);

        // Pack against the DECLARED grid so the free cells are the ones the player would see. Pack may report a
        // grown grid when the existing contents overflow it; the search below stays inside the declared bounds,
        // which is what makes this a capacity rule rather than a layout.
        var layout = Pack(width, height, loose);
        var occ = new HashSet<(int, int)>();
        foreach (var it in layout.Items)
            Occupy(occ, it.X, it.Y, it.W, it.H);

        if (Scan(occ, width, height, iw, ih) is { } upright) return new FreeCell(upright.X, upright.Y, 0);
        if (!canRotate || iw == ih) return null;
        return Scan(occ, width, height, ih, iw) is { } turned ? new FreeCell(turned.X, turned.Y, 90) : null;
    }

    /// <summary>The first free w×h cell row-major within the declared grid, or null. An item too big for the grid
    /// simply finds nothing, so it is never reported as fitting.</summary>
    private static (int X, int Y)? Scan(HashSet<(int, int)> occ, int width, int height, int w, int h)
    {
        for (var y = 0; y + h <= height; y++)
            for (var x = 0; x + w <= width; x++)
                if (Free(occ, x, y, w, h)) return (x, y);
        return null;
    }

    /// <summary>The unoccupied w×h cell nearest (<paramref name="nearX"/>,<paramref name="nearY"/>) by squared
    /// distance, scanning row-major so ties resolve to the top-left-most — the game's
    /// <c>FindNearestUnoccupiedTile</c>. Grows the grid height as a last resort so an over-full container still
    /// shows every item.</summary>
    private static (int X, int Y) Nearest(HashSet<(int, int)> occ, int width, ref int height, int w, int h, int nearX, int nearY)
    {
        while (true)
        {
            var bestX = -1;
            var bestY = -1;
            var best = long.MaxValue;
            for (var y = 0; y + h <= height; y++)
                for (var x = 0; x + w <= width; x++)
                {
                    if (!Free(occ, x, y, w, h)) continue;
                    long dx = x - nearX, dy = y - nearY;
                    var d = dx * dx + dy * dy;
                    if (d < best) { best = d; bestX = x; bestY = y; }
                }
            if (bestX >= 0) return (bestX, bestY);
            height += h;   // no room at the current height — add rows and rescan (safety net; real grids fit)
        }
    }

    private static void Occupy(HashSet<(int, int)> occ, int x, int y, int w, int h)
    {
        for (var r = y; r < y + h; r++)
            for (var c = x; c < x + w; c++)
                occ.Add((c, r));
    }

    private static bool Free(HashSet<(int, int)> occ, int x, int y, int w, int h)
    {
        for (var r = y; r < y + h; r++)
            for (var c = x; c < x + w; c++)
                if (occ.Contains((c, r))) return false;
        return true;
    }

    private sealed class Block(CargoItem item, int desiredX, int desiredY, int w, int h, int count)
    {
        public CargoItem Item { get; } = item;
        public int DesiredX { get; } = desiredX;
        public int DesiredY { get; } = desiredY;
        public int W { get; } = w;
        public int H { get; } = h;
        public int Count { get; set; } = count;
    }
}
