namespace Ostraplan.Core;

/// <summary>
/// Rotating a multi-part selection as a group: the whole arrangement turns 90° about its
/// bounding-box centre — every part's POSITION rotates and its own rotation advances,
/// unlike per-part in-place rotation (which only re-orients one part). Sheet items
/// (walls/floors) keep rotation 0 — they auto-tile rather than turn — but still move to
/// their rotated tile. The tile mapping is exact; only re-centring the swapped W×H↔H×W
/// bounding box can cost up to half a tile of drift for odd-parity bounds (rounded
/// consistently), which the user can nudge. Pure and tile-based, so it is unit-tested.
/// </summary>
public static class GroupRotate
{
    /// <summary>A footprint at a pose. <paramref name="Sheet"/> = auto-tiling (keeps rot 0).</summary>
    public readonly record struct Item(int X, int Y, int W, int H, int Rot, bool Sheet);

    /// <summary>
    /// New (X,Y,Rot) for each input under a 90° group turn: delta +90 = clockwise,
    /// −90 (or 270) = counter-clockwise. Only ±90 is meaningful; other deltas are treated
    /// as their normalised 90/270 sense.
    /// </summary>
    public static (int X, int Y, int Rot)[] Rotate(IReadOnlyList<Item> items, int delta)
    {
        var result = new (int X, int Y, int Rot)[items.Count];
        if (items.Count == 0) return result;

        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        foreach (var it in items)
        {
            minX = Math.Min(minX, it.X);
            minY = Math.Min(minY, it.Y);
            maxX = Math.Max(maxX, it.X + it.W - 1);
            maxY = Math.Max(maxY, it.Y + it.H - 1);
        }
        int w = maxX - minX + 1, h = maxY - minY + 1;
        var cw = GridMath.Norm(delta) == 90;

        // the rotated box is h×w; anchor it so its centre sits on the old box centre
        // (round-half-up keeps the ≤½-tile drift for odd parity symmetric)
        var newMinX = (int)Math.Floor(minX + w / 2.0 - h / 2.0 + 0.5);
        var newMinY = (int)Math.Floor(minY + h / 2.0 - w / 2.0 + 0.5);

        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            int lx = it.X - minX, ly = it.Y - minY;
            var (nlx, nly) = cw
                ? (h - ly - it.H, lx)      // CW: a tile at local (c,r) -> (h-1-r, c)
                : (ly, w - lx - it.W);     // CCW: (c,r) -> (r, w-1-c)
            var rot = it.Sheet ? 0 : GridMath.Norm(it.Rot + delta);
            result[i] = (newMinX + nlx, newMinY + nly, rot);
        }
        return result;
    }
}
