namespace Ostraplan.Core;

/// <summary>
/// A part's declared <b>use point</b>: the tile the game marks with a pair of footprints while you are placing
/// the item, meaning "this is the side you work it from".
///
/// <para>It is a property of the def and its rotation and nothing else, which is what makes it useful on a plan:
/// an arcade machine is symmetrical to look at and usable from one side only, and a 1×1 rack is very nearly
/// symmetrical, so a placed one gives no clue which way round it went down. The <see cref="WalkNetwork"/>'s access
/// analysis answers a different and larger question — where a crew member could actually stand, given the deck
/// that is built around the part — and needs the whole ship to answer it.</para>
///
/// <para><b>The rule is the game's own</b> (<c>CanvasManager</c>'s build cursor, the same block that puts the
/// power-input and power-output nubs on a part being placed): the condowner declares a <c>"use"</c> map point,
/// <b>and its raw value is not (0, 0)</b>. A point at the item's own centre is the default every condowner gets
/// and says nothing about which way the item faces, so the game does not draw it and neither does this.</para>
/// </summary>
public static class UsePoint
{
    /// <summary>The map point an interaction walks to, on all but a handful of the game's own interactions.</summary>
    public const string PointName = "use";

    /// <summary>
    /// The part's raw use point (condowner pixels around the item centre, +y up), or null when it declares none or
    /// declares one at its own centre. Null is the answer for most defs: on stock 1.0.0.13, of 1,034 item defs
    /// only the ones a crew member walks up to carry an offset one.
    /// </summary>
    public static (double X, double Y)? Raw(PartDef? part) =>
        part is not null
        && part.MapPoints.TryGetValue(PointName, out var px)
        && (px.X != 0 || px.Y != 0)
            ? px : null;

    /// <summary>Whether this part has a use point worth drawing (see <see cref="Raw"/>).</summary>
    public static bool Has(PartDef? part) => Raw(part) is not null;

    /// <summary>
    /// Where the part's use point lands, in continuous document tile coordinates, for a part whose rotated
    /// footprint has its top-left at <paramref name="gx"/>,<paramref name="gy"/>. Cell (c, r)'s centre is
    /// (c + 0.5, r + 0.5), so a drawn mark is centred on the value returned. Null when the part has no use point.
    /// </summary>
    public static (double X, double Y)? At(PartDef? part, int gx, int gy, int rot)
    {
        if (Raw(part) is not { } px) return null;
        var (tx, ty) = GridMath.MapPoint(px, part!.Item.Width, part.Item.Height, rot);
        return (gx + tx, gy + ty);
    }

    /// <summary>
    /// The document tile the use point falls on, rounded the way the game rounds it
    /// (<see cref="ShipGrid.MapPointTile"/>'s away-from-zero step). Null when the part has no use point.
    /// </summary>
    public static (int X, int Y)? Tile(PartDef? part, int gx, int gy, int rot) =>
        At(part, gx, gy, rot) is { } p
            ? ((int)Math.Round(p.X - 0.5, MidpointRounding.AwayFromZero),
               (int)Math.Round(p.Y - 0.5, MidpointRounding.AwayFromZero))
            : null;
}
