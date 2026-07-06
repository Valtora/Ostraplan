namespace Ostraplan.Core;

/// <summary>One item laid out on an inventory grid: the <see cref="CargoItem"/> it represents, its top-left
/// cell, its footprint in tiles, and how many are stacked in it.</summary>
public sealed record PackedItem(CargoItem Item, int X, int Y, int W, int H, int Count);

/// <summary>The result of packing a container's loose cargo onto its grid: the final grid size (which may have
/// grown past the declared height to fit everything) and where each item block landed.</summary>
public sealed record GridLayoutResult(int Width, int Height, IReadOnlyList<PackedItem> Items);

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
/// </summary>
public static class InventoryGrid
{
    /// <summary>Pack the loose (non-slotted) children of a container onto a <paramref name="gridW"/>×
    /// <paramref name="gridH"/> grid (each ≤0 falls back to 6, the game's default). The grid grows in height if
    /// the items genuinely don't fit, so a viewer never silently hides cargo.</summary>
    public static GridLayoutResult Pack(int gridW, int gridH, IReadOnlyList<CargoItem> loose)
    {
        var width = gridW > 0 ? gridW : 6;
        var height = gridH > 0 ? gridH : 6;

        // Collapse same-def items sharing a stored cell into one stacked block, preserving first-seen order.
        var blocks = new List<Block>();
        var byKey = new Dictionary<(string, int, int), Block>();
        foreach (var it in loose)
        {
            var key = (it.DefName, it.GridX, it.GridY);
            if (byKey.TryGetValue(key, out var existing)) { existing.Count += Math.Max(1, it.Stack); continue; }
            var b = new Block(it, it.GridX, it.GridY, Math.Clamp(it.GridW, 1, width), Math.Max(1, it.GridH), Math.Max(1, it.Stack));
            byKey[key] = b;
            blocks.Add(b);
        }

        var occupied = new HashSet<(int, int)>();
        var placed = new List<PackedItem>(blocks.Count);
        foreach (var b in blocks)
        {
            var (x, y) = Nearest(occupied, width, ref height, b.W, b.H, b.DesiredX, b.DesiredY);
            for (var r = y; r < y + b.H; r++)
                for (var c = x; c < x + b.W; c++)
                    occupied.Add((c, r));
            placed.Add(new PackedItem(b.Item, x, y, b.W, b.H, b.Count));
        }

        return new GridLayoutResult(width, height, placed);
    }

    /// <summary>
    /// The first free cell (row-major) that fits a <paramref name="w"/>×<paramref name="h"/> item in a
    /// <paramref name="gridW"/>×<paramref name="gridH"/> container already holding <paramref name="loose"/>, or
    /// <c>null</c> when it will not fit within the declared grid — the capacity rule ("the Law" for cargo). The
    /// existing cargo is packed the way the game lays it out on open, so the free cell matches what the player
    /// would see. Stacks and multi-tile items are honoured. Unlike <see cref="Pack"/> this never grows the grid.
    /// </summary>
    public static (int X, int Y)? FirstFreeCell(int gridW, int gridH, IReadOnlyList<CargoItem> loose, int w, int h)
    {
        var width = gridW > 0 ? gridW : 6;
        var height = gridH > 0 ? gridH : 6;
        var iw = Math.Clamp(w, 1, width);
        var ih = Math.Max(1, h);

        var layout = Pack(width, height, loose);
        var occ = new HashSet<(int, int)>();
        foreach (var it in layout.Items)
            for (var r = it.Y; r < it.Y + it.H; r++)
                for (var c = it.X; c < it.X + it.W; c++)
                    occ.Add((c, r));

        for (var y = 0; y + ih <= height; y++)
            for (var x = 0; x + iw <= width; x++)
                if (Free(occ, x, y, iw, ih)) return (x, y);
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
