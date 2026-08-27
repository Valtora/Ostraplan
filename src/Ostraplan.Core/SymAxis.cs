namespace Ostraplan.Core;

/// <summary>
/// Where the symmetry axes sit, in <b>half-tiles of the centre frame</b>: <see cref="HX"/> is twice the vertical
/// axis's X coordinate and <see cref="HY"/> twice the horizontal axis's Y.
///
/// <para><b>The doubling is the whole point.</b> It lets an axis land <i>between</i> two columns rather than only
/// down the middle of one. An even value is a column's centre line (<c>HX = 2x</c> is the middle of column
/// <c>x</c>); an odd value is the seam between columns <c>(HX−1)/2</c> and <c>(HX+1)/2</c>. A design of odd width
/// mirrors about a column and one of even width about a seam, and until this type the axis was a bare tile index,
/// so only the first could be said at all: a 20-wide hull had no expressible centre line and was mirrored half a
/// tile off its own middle, with no way for the designer to correct it (issue #46).</para>
///
/// <para><b>Centre frame</b>, per CONVENTIONS.md, because that is the frame <see cref="Symmetry"/>'s reflection
/// has always been written in: <c>2·centre − span</c> reflects about a tile's <i>middle</i>. It is not the
/// canvas's corner frame, and the crossing goes through <see cref="TileFrame"/> like every other one.</para>
///
/// <para><b>A struct rather than two loose ints</b> because the unit is invisible at a call site. Every one of
/// these parameters used to take a tile index, and a halved int handed to something expecting tiles compiles
/// perfectly and mirrors the ship to the wrong place. The type is what makes that a build error.</para>
/// </summary>
public readonly record struct SymAxis(int HX, int HY)
{
    /// <summary>Axes down the middle of column <paramref name="x"/> and row <paramref name="y"/>.</summary>
    public static SymAxis OnTile(int x, int y) => new(2 * x, 2 * y);

    /// <summary>
    /// The axes that mirror the inclusive tile span <c>[min, max]</c> onto itself, which is the true centre line
    /// of a design's bounding box: a column when the span is odd, a seam when it is even. Falls out of the
    /// reflection itself, since a span maps to itself exactly when <c>2·centre = min + max</c>.
    /// </summary>
    public static SymAxis Centring(int minX, int maxX, int minY, int maxY) => new(minX + maxX, minY + maxY);

    /// <summary>The axes nearest a <b>corner-frame</b> point, snapped to the half-tile grid so a drag can pick a
    /// seam as readily as a column. The inverse of <see cref="Corner"/>.</summary>
    public static SymAxis NearestTo((double X, double Y) corner)
    {
        var (cx, cy) = TileFrame.CornerToCentre(corner);
        return new SymAxis((int)Math.Round(cx * 2), (int)Math.Round(cy * 2));
    }

    /// <summary>True when the vertical axis runs down a column's middle rather than along a seam between two.
    /// Only an odd-width arrangement can be symmetric about a column, and only an even-width one about a seam.</summary>
    public bool OnColumn => (HX & 1) == 0;

    /// <summary>True when the horizontal axis runs along a row's middle rather than a seam. See <see cref="OnColumn"/>.</summary>
    public bool OnRow => (HY & 1) == 0;

    /// <summary>Where to draw: the axis crossing as a <b>corner-frame</b> point, where an integer is a tile's
    /// top-left. A seam axis lands on a whole number here, which is exactly the grid line the canvas already
    /// draws, and a column axis lands on its <c>+0.5</c> middle.</summary>
    public (double X, double Y) Corner => TileFrame.CentreToCorner((HX / 2.0, HY / 2.0));

    /// <summary>The tile the crossing sits on or beside, for anything that needs a whole cell (framing the view,
    /// reporting a position). A seam resolves to the tile on its higher side.</summary>
    public (int X, int Y) Cell => TileFrame.CellOf(Corner);
}
