namespace Ostraplan.Core;

/// <summary>
/// The two ways a <b>continuous</b> tile coordinate can be read, and the conversion between them. Integer tile
/// indices are unambiguous; a fractional position is not, and the two halves of the app had picked opposite
/// answers.
///
/// <para><b>Corner frame.</b> An integer is a tile's top-left corner, so tile <c>(x, y)</c> covers
/// <c>[x, x+1) × [y, y+1)</c> and its middle is <c>(x + 0.5, y + 0.5)</c>. This is what the canvas uses, because
/// it is the frame its own screen transform inverts to, and it is what <see cref="GridMath.MapPoint"/> already
/// documents for a footprint.</para>
///
/// <para><b>Centre frame.</b> An integer is a tile's <i>centre</i>, so tile <c>(x, y)</c> covers
/// <c>[x − 0.5, x + 0.5] × [y − 0.5, y + 0.5]</c>. This is what the damage solvers use, because it is the frame
/// the game's own item transforms are expressed in: an item's collider is centred on its position, which is why
/// <c>MicrometeoroidStrike</c> builds one as <c>p.X + w/2 − 0.5</c>.</para>
///
/// <para><b>Why this type exists.</b> Both frames are correct for what they describe and neither should move: the
/// canvas frame is the inverse of a screen transform, and the solver frame is what makes the port legible against
/// the decompile. What was missing was the conversion, so a path drawn on the canvas was resolved against a hull
/// sitting half a tile up and to the left of the one on screen. Every crossing between the two goes through here,
/// named, rather than through a bare <c>± 0.5</c> that reads like a rounding fudge.</para>
/// </summary>
public static class TileFrame
{
    /// <summary>Canvas → solver: a point the canvas reported, ready to fire.</summary>
    public static (double X, double Y) CornerToCentre((double X, double Y) p) => (p.X - 0.5, p.Y - 0.5);

    /// <summary>Solver → canvas: a point the solver produced, ready to draw.</summary>
    public static (double X, double Y) CentreToCorner((double X, double Y) p) => (p.X + 0.5, p.Y + 0.5);

    /// <summary>The tile a corner-frame point falls on. Floor, not round: the integer is already the tile's own
    /// corner, so everything from <c>x</c> up to <c>x+1</c> belongs to tile <c>x</c>.</summary>
    public static (int X, int Y) CellOf((double X, double Y) corner) =>
        ((int)Math.Floor(corner.X), (int)Math.Floor(corner.Y));
}
