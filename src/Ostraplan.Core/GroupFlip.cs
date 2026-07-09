namespace Ostraplan.Core;

/// <summary>
/// Mirroring a selection: the whole arrangement reflects across an axis through its bounding-box CENTRE —
/// a <b>horizontal</b> flip mirrors left↔right (reflect X, a vertical axis), a <b>vertical</b> flip mirrors
/// up↔down (reflect Y, a horizontal axis). Every part's POSITION reflects and its own rotation remaps to the
/// reflected rotation, exactly the reflection the live symmetry mode uses (<see cref="Symmetry"/>): a vertical
/// axis flips East↔West (<c>rot' = 360 − rot</c>), a horizontal axis flips North↔South (<c>rot' = 180 − rot</c>),
/// all normalised to {0,90,180,270}.
///
/// <para>This is the only <b>Law-safe</b> "flip": the game's ship format has no mirror/flip field (an item
/// carries only a rotation) and build mode cannot mirror a chiral part, so a true visual mirror of a single
/// asymmetric part is not buildable. Reflecting the ARRANGEMENT while snapping each part to a real rotation
/// stays inside what the game can build — every emitted pose is a genuine 90° rotation. Unlike a group ROTATE,
/// a mirror leaves each footprint W×H unchanged (a 90°/270° pose keeps its H×W either way), so the bounding
/// box keeps its size and the parts simply reflect within it. Sheet items (walls/floors) auto-tile rather than
/// turn, so they keep rotation 0 but still move to their mirrored tile. A single part reflects in place
/// (position unchanged, rotation remapped); a part that is symmetric under the mirror is a visual no-op.
/// Pure and tile-based, so it is unit-tested.</para>
/// </summary>
public static class GroupFlip
{
    /// <summary>
    /// New (X,Y,Rot) for each input under a mirror of the whole selection about its bounding-box centre.
    /// <paramref name="horizontal"/> = a left↔right flip (reflect X); otherwise an up↔down flip (reflect Y).
    /// Reuses <see cref="GroupRotate.Item"/> (same footprint-at-a-pose shape); <c>Sheet</c> parts keep rot 0.
    /// </summary>
    public static (int X, int Y, int Rot)[] Flip(IReadOnlyList<GroupRotate.Item> items, bool horizontal)
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

        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            // Reflect the part's whole span within the (unchanged) bounding box. Footprint W×H is
            // mirror-invariant, so the reflected top-left is (min + max) − (topLeft + size − 1).
            var nx = horizontal ? minX + maxX - (it.X + it.W - 1) : it.X;
            var ny = horizontal ? it.Y : minY + maxY - (it.Y + it.H - 1);
            var rot = it.Sheet ? 0 : GridMath.Norm(horizontal ? 360 - it.Rot : 180 - it.Rot);
            result[i] = (nx, ny, rot);
        }
        return result;
    }
}
