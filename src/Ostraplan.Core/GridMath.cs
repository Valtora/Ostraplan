namespace Ostraplan.Core;

/// <summary>90-degree grid rotation, mirroring TileUtils.RotateTilesCW semantics.</summary>
public static class GridMath
{
    /// <summary>Normalize any rotation to 0/90/180/270.</summary>
    public static int Norm(int rot) => ((rot % 360) + 360) % 360;

    public static (int W, int H) Size(int w, int h, int rot) =>
        Norm(rot) is 90 or 270 ? (h, w) : (w, h);

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
