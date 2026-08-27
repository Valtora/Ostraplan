namespace Ostraplan.Core;

/// <summary>
/// Symmetry-preserving group edits (the manipulation counterpart to <see cref="Symmetry"/>'s placement mirroring).
/// A selection built with symmetry on holds mirror partners across the axes; these keep the pair/quad symmetric
/// under a group rotation by rotating one canonical side and reflecting the result onto its partners — rather than
/// rotating the combined bounds, which would swing a left/right pair into a top/bottom one and break the mirror.
/// Pure geometry so the reflection/rotation math is unit-testable without WPF.
///
/// <para>Sides are decided in <see cref="SymAxis"/>'s half-tile units, which is why the comparisons here double a
/// tile index before testing it: <c>2·x + (w − 1)</c> is a span's own centre in the same units as
/// <see cref="SymAxis.HX"/>. A part is on the axis when the two are equal, which an even-width part straddling a
/// seam now satisfies exactly.</para>
/// </summary>
public static class SymmetryOps
{
    /// <summary>A selected part: a <paramref name="Key"/> partners match on (the def name), its pose, its rotated
    /// footprint <paramref name="W"/>×<paramref name="H"/>, and whether it auto-tiles (a sheet wall/floor).</summary>
    public readonly record struct Item(string Key, int X, int Y, int W, int H, int Rot, bool Sheet);

    /// <summary>
    /// The move offset for one part of a symmetric drag: the raw delta (<paramref name="rawDx"/>, <paramref
    /// name="rawDy"/>), mirrored on the far side of each active axis so a symmetric selection stays symmetric
    /// (drag the left cluster right and the right cluster tracks left). The grabbed tile (<paramref name="refX"/>,
    /// <paramref name="refY"/>) is the reference side that follows the cursor; a part straddling an axis can't move
    /// along it without breaking its own symmetry, so that component is pinned to zero.
    /// </summary>
    public static (int X, int Y) MoveDelta(
        int x, int y, int w, int h, int rawDx, int rawDy, SymAxis axis, int refX, int refY, bool vertical, bool horizontal)
    {
        var rx = Math.Sign(2 * refX - axis.HX); if (rx == 0) rx = 1;
        var ry = Math.Sign(2 * refY - axis.HY); if (ry == 0) ry = 1;
        var sideX = Math.Sign(2 * x + (w - 1) - axis.HX);
        var sideY = Math.Sign(2 * y + (h - 1) - axis.HY);
        var ox = vertical ? sideX == 0 ? 0 : sideX == rx ? rawDx : -rawDx : rawDx;
        var oy = horizontal ? sideY == 0 ? 0 : sideY == ry ? rawDy : -rawDy : rawDy;
        return (ox, oy);
    }

    /// <summary>
    /// Rotate a symmetric selection by <paramref name="delta"/> (a multiple of 90°) keeping it symmetric about
    /// <paramref name="axis"/>. The canonical primary side (left / top / top-left quadrant, plus on-axis parts)
    /// rotates as a group about its own centre, and every mirror partner is set to the reflection of its primary's
    /// new pose. Partners are matched to primaries by <see cref="Item.Key"/> + exact mirrored top-left — how a
    /// symmetry-mode build lays them down. Parts with no partner (asymmetric strays, or an on-axis part whose
    /// mirror is itself) simply rotate in place. Returns one new pose per input item, in order.
    /// </summary>
    public static (int X, int Y, int Rot)[] RotateGroup(
        IReadOnlyList<Item> items, int delta, SymAxis axis, bool vertical, bool horizontal)
    {
        var result = new (int X, int Y, int Rot)[items.Count];
        var assigned = new bool[items.Count];

        bool Primary(Item it)
        {
            var sx = Math.Sign(2 * it.X + (it.W - 1) - axis.HX);
            var sy = Math.Sign(2 * it.Y + (it.H - 1) - axis.HY);
            if (vertical && horizontal) return sx <= 0 && sy <= 0;
            if (vertical) return sx <= 0;
            if (horizontal) return sy <= 0;
            return true;
        }

        var primary = Enumerable.Range(0, items.Count).Where(i => Primary(items[i])).ToList();
        var rotated = GroupRotate.Rotate(
            primary.Select(i => new GroupRotate.Item(items[i].X, items[i].Y, items[i].W, items[i].H, items[i].Rot, items[i].Sheet)).ToList(),
            delta);

        for (var k = 0; k < primary.Count; k++)
        {
            var i = primary[k];
            var it = items[i];
            var np = rotated[k];
            result[i] = np;
            assigned[i] = true;

            // the new pose's rotated footprint: a 90°/270° turn swaps the dimensions, 0°/180° keeps them
            var (nw, nh) = delta % 180 == 0 ? (it.W, it.H) : (it.H, it.W);
            var origMirrors = Symmetry.Poses(it.X, it.Y, it.Rot, it.W, it.H, axis, vertical, horizontal).Skip(1).ToList();
            var newMirrors = Symmetry.Poses(np.X, np.Y, np.Rot, nw, nh, axis, vertical, horizontal).Skip(1).ToList();
            for (var m = 0; m < origMirrors.Count; m++)
            {
                var (omx, omy, _) = origMirrors[m];
                var j = -1;
                for (var t = 0; t < items.Count; t++)
                    if (!assigned[t] && t != i && items[t].Key == it.Key && items[t].X == omx && items[t].Y == omy) { j = t; break; }
                if (j >= 0) { result[j] = newMirrors[m]; assigned[j] = true; }
            }
        }

        // leftovers (no partner matched): rotate each in place so the operation still does something sensible
        for (var i = 0; i < items.Count; i++)
            if (!assigned[i])
            {
                var it = items[i];
                result[i] = GroupRotate.Rotate([new GroupRotate.Item(it.X, it.Y, it.W, it.H, it.Rot, it.Sheet)], delta)[0];
            }

        return result;
    }
}
