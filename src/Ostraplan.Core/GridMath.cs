namespace Ostraplan.Core;

/// <summary>90-degree grid rotation, mirroring TileUtils.RotateTilesCW semantics.</summary>
public static class GridMath
{
    /// <summary>Normalize any rotation to 0/90/180/270.</summary>
    public static int Norm(int rot) => ((rot % 360) + 360) % 360;

    public static (int W, int H) Size(int w, int h, int rot) =>
        Norm(rot) is 90 or 270 ? (h, w) : (w, h);

    /// <summary>
    /// A condowner map point (pixels around the item centre, +y up, 16 px = 1 tile) →
    /// continuous tile coords in the <b>rotated</b> footprint (top-left origin, +y down);
    /// cell (0,0)'s centre sits at (0.5, 0.5). Mirrors <c>CondOwner.GetPos</c>' tf.right
    /// rotation math for the four grid rotations. Shared by the airlock face
    /// (<c>ProblemScan.TryGetFace</c>) and the map-point tile lookup
    /// (<c>ShipGrid.MapPointTile</c>).
    /// </summary>
    public static (double X, double Y) MapPoint((double X, double Y) px, int w, int h, int rot)
    {
        var pt = (X: w / 2.0 + px.X / 16.0, Y: h / 2.0 - px.Y / 16.0);
        return Norm(rot) switch
        {
            90 => (h - pt.Y, pt.X),
            180 => (w - pt.X, h - pt.Y),
            270 => (pt.Y, w - pt.X),
            _ => pt,
        };
    }

    /// <summary>
    /// Rotate a row-major W-by-H cell grid clockwise by rot degrees.
    /// CW90: new[r,c] = old[H-1-c, r] with swapped dimensions.
    /// </summary>
    public static (int W, int H, T[] Cells) Rotate<T>(T[] cells, int w, int h, int rot)
    {
        rot = Norm(rot);
        if (rot == 0 || cells.Length != w * h) return (w, h, cells);

        var quarters = rot / 90;
        var (cw, ch, cur) = (w, h, cells);
        for (var q = 0; q < quarters; q++)
        {
            var next = new T[cur.Length];
            var (nw, nh) = (ch, cw);
            for (var r = 0; r < nh; r++)
                for (var c = 0; c < nw; c++)
                    next[r * nw + c] = cur[(ch - 1 - c) * cw + r];
            (cw, ch, cur) = (nw, nh, next);
        }
        return (cw, ch, cur);
    }
}
