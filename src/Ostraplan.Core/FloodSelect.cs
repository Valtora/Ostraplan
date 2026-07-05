namespace Ostraplan.Core;

/// <summary>
/// Magic-wand selection: from a seed part, the set of same-def parts reachable from it by
/// 4-connectivity ("directly touching") across the tile grid — the editor's double-click
/// flood-select, for bulk-deleting or replacing a run of identical tiles. Matching is by
/// exact <see cref="Placement.DefName"/> and limited to 1×1 parts (floors, walls, conduits,
/// single-tile fixtures); a multi-tile seed has nothing to flood and yields just itself.
/// Pure and side-effect-free so it unit-tests without the UI.
/// </summary>
public static class FloodSelect
{
    private static readonly (int Dx, int Dy)[] Neighbours = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    /// <summary>
    /// Every 1×1 placement of <paramref name="seed"/>'s def 4-connected to it through same-def
    /// tiles, including the seed. Returns just the seed when it is not a 1×1 part.
    /// </summary>
    public static IReadOnlyList<Placement> Collect(ShipDocument doc, Placement seed)
    {
        var (sw, sh) = doc.FootprintOf(seed);
        if (sw != 1 || sh != 1) return [seed];

        // tile -> the same-def 1×1 placement on it; the seed wins its own tile if several stack
        var byTile = new Dictionary<(int, int), Placement>();
        foreach (var p in doc.Placements)
        {
            if (p.DefName != seed.DefName) continue;
            var (w, h) = doc.FootprintOf(p);
            if (w == 1 && h == 1) byTile.TryAdd((p.X, p.Y), p);
        }
        byTile[(seed.X, seed.Y)] = seed;

        var result = new List<Placement>();
        var seen = new HashSet<(int, int)> { (seed.X, seed.Y) };
        var queue = new Queue<(int X, int Y)>();
        queue.Enqueue((seed.X, seed.Y));
        while (queue.Count > 0)
        {
            var (x, y) = queue.Dequeue();
            if (!byTile.TryGetValue((x, y), out var p)) continue;   // gap tile — don't spread through it
            result.Add(p);
            foreach (var (dx, dy) in Neighbours)
                if (seen.Add((x + dx, y + dy)))
                    queue.Enqueue((x + dx, y + dy));
        }
        return result;
    }
}
