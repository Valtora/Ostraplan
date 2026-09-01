namespace Ostraplan.Core;

/// <summary>
/// Where the incoming ship's grid sits on the receiver's for one mating pair — the game's
/// <c>GridUtils.DockOffset</c>, plus the incoming grid's dimensions, which its rotation term needs.
/// </summary>
public sealed record DockPoseTransform(int Rotation, int OffsetRow, int OffsetCol, int IncomingHeight, int IncomingWidth);

/// <summary>
/// The mating pose as something that can be <b>drawn</b>: the other ship's parts expressed in your design's own
/// tile coordinates, so the canvas can ghost the two hulls docked together.
///
/// <para>The overlay in <see cref="DockMating"/> answers a yes or no from grid cells, which is all legality
/// needs. A picture needs more: a grid cell is not a part (one cell per item whatever its size, plus Blank halo
/// cells that are not items at all), so the parts are carried separately on <see cref="DockShip.Parts"/> and
/// transformed here.</para>
///
/// <para><b>The transform is fitted rather than derived.</b> Composing the two document-to-grid mappings with the
/// game's own rotation chain gives a rigid transform (a multiple of 90 degrees plus a translation — the two y
/// flips cancel, so there is no reflection), and writing that out by hand is four chances to invert a sign.
/// Instead the round trip is evaluated at three tiles and the affine read off them, which cannot disagree with
/// the overlay because it <i>is</i> the overlay.</para>
/// </summary>
public static class DockPose
{
    /// <summary>How a clockwise quarter-turn moves a direction in Ostraplan's y-down tile frame — the linear part
    /// of <see cref="GridMath.Rotate"/>, which takes (x,y) to (−y,x) per turn.</summary>
    private static readonly (int Rot, (int X, int Y) U, (int X, int Y) V)[] Rotations =
    [
        (0, (1, 0), (0, 1)),
        (90, (0, 1), (-1, 0)),
        (180, (-1, 0), (0, -1)),
        (270, (0, -1), (1, 0)),
    ];

    /// <summary>
    /// The receiver's parts in the incoming ship's document frame, ready to draw. Empty when the pair has no
    /// pose (a rotation that is not a multiple of 90, which no shipped ship has) or when the fitted transform
    /// is not a rigid one, which would mean the algebra above stopped holding and is worth drawing nothing over
    /// rather than drawing a ship in the wrong place.
    /// </summary>
    public static IReadOnlyList<DockPart> ReceiverParts(DockShip receiver, DockShip incoming, DockPoseTransform pose)
    {
        if (!TryFit(receiver, incoming, pose, out var origin, out var u, out var v, out var turn)) return [];

        var parts = new List<DockPart>(receiver.Parts.Count);
        foreach (var part in receiver.Parts)
        {
            // Work in the CENTRE frame (an integer is a tile's middle), because that is the frame the fitted
            // affine is in: it was sampled on tile indices. See CONVENTIONS on naming which frame you are in.
            var (wr, hr) = GridMath.Size(part.W, part.H, part.Rot);
            var cx = part.X + (wr - 1) / 2.0;
            var cy = part.Y + (hr - 1) / 2.0;

            var tx = origin.X + cx * u.X + cy * v.X;
            var ty = origin.Y + cx * u.Y + cy * v.Y;

            var rot = GridMath.Norm(part.Rot + turn);
            var (wr2, hr2) = GridMath.Size(part.W, part.H, rot);
            parts.Add(new DockPart(part.DefName,
                (int)Math.Round(tx - (wr2 - 1) / 2.0, MidpointRounding.AwayFromZero),
                (int)Math.Round(ty - (hr2 - 1) / 2.0, MidpointRounding.AwayFromZero),
                rot, part.W, part.H));
        }
        return parts;
    }

    /// <summary>
    /// Fit the receiver-document to incoming-document affine by sending three of the receiver's tiles all the way
    /// round: receiver document → receiver grid → (the overlay, inverted) → incoming grid → incoming document.
    /// </summary>
    private static bool TryFit(
        DockShip receiver, DockShip incoming, DockPoseTransform pose,
        out (double X, double Y) origin, out (int X, int Y) u, out (int X, int Y) v, out int turn)
    {
        origin = default; u = default; v = default; turn = 0;

        // The overlay maps an incoming grid cell onto a receiver one. Sample it at three incoming cells to get
        // its linear part, then invert; the matrix is a rotation, so its determinant is ±1 and the inverse is
        // integral.
        var f0 = Forward(0, 0, pose);
        var fu = Sub(Forward(1, 0, pose), f0);
        var fv = Sub(Forward(0, 1, pose), f0);
        var det = fu.X * fv.Y - fu.Y * fv.X;
        if (det is not (1 or -1)) return false;

        // Receiver document tile → incoming document tile, at the three samples that define the affine.
        (double X, double Y) Round(int rx, int ry)
        {
            var (gx, gy) = receiver.GridCellOf(rx, ry);
            var dx = gx - f0.X;
            var dy = gy - f0.Y;
            var bx = (dx * fv.Y - dy * fv.X) / det;
            var by = (-dx * fu.Y + dy * fu.X) / det;
            var (ox, oy) = incoming.DocTileOf(bx, by);
            return (ox, oy);
        }

        var p0 = Round(0, 0);
        var pu = Round(1, 0);
        var pv = Round(0, 1);
        origin = p0;
        u = ((int)(pu.X - p0.X), (int)(pu.Y - p0.Y));
        v = ((int)(pv.X - p0.X), (int)(pv.Y - p0.Y));

        // A rigid transform and nothing else. Anything the table does not name is a reflection or a skew, which
        // the composition cannot produce, so it means an assumption above has stopped being true.
        foreach (var (rot, ru, rv) in Rotations)
            if (u == ru && v == rv) { turn = rot; return true; }
        return false;
    }

    /// <summary>The overlay's own cell mapping, incoming grid → receiver grid.</summary>
    private static (int X, int Y) Forward(int bx, int by, DockPoseTransform pose)
    {
        var (row, col) = DockMating.Turn(by, bx, pose.IncomingHeight, pose.IncomingWidth, pose.Rotation);
        return (col + pose.OffsetCol, row + pose.OffsetRow);
    }

    private static (int X, int Y) Sub((int X, int Y) a, (int X, int Y) b) => (a.X - b.X, a.Y - b.Y);
}
