using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The symmetry-preserving group edits behind manipulating a mirrored selection: a move keeps a symmetric
/// selection symmetric, and a group rotate rotates one side and reflects it onto its partners. Pure geometry,
/// no game data needed.
/// </summary>
public class SymmetryOpsTests
{
    // ---- MoveDelta: the far side of the axis moves opposite so the pair stays a mirror ----

    [Fact]
    public void Vertical_move_mirrors_x_on_the_far_side()
    {
        // axis vertical at cx=10; grab a part on the LEFT (ref x=5), drag right by (+3,+2)
        // left part (x=5) follows the cursor; a right part (x=15) tracks left, y matches (vertical doesn't mirror y)
        var left = SymmetryOps.MoveDelta(5, 5, 1, 1, 3, 2, cx: 10, cy: 0, refX: 5, refY: 5, vertical: true, horizontal: false);
        var right = SymmetryOps.MoveDelta(15, 5, 1, 1, 3, 2, cx: 10, cy: 0, refX: 5, refY: 5, vertical: true, horizontal: false);
        Assert.Equal((3, 2), left);
        Assert.Equal((-3, 2), right);
    }

    [Fact]
    public void Grabbing_the_other_side_flips_which_follows_the_cursor()
    {
        // same axis, but grab the RIGHT part (ref x=15): now the right side follows the cursor and the left mirrors
        var left = SymmetryOps.MoveDelta(5, 5, 1, 1, 3, 2, cx: 10, cy: 0, refX: 15, refY: 5, vertical: true, horizontal: false);
        var right = SymmetryOps.MoveDelta(15, 5, 1, 1, 3, 2, cx: 10, cy: 0, refX: 15, refY: 5, vertical: true, horizontal: false);
        Assert.Equal((-3, 2), left);
        Assert.Equal((3, 2), right);
    }

    [Fact]
    public void A_part_on_the_axis_cannot_move_along_it()
    {
        // 1x1 centred on the vertical axis tile (x=10): its x-move is pinned to 0 (moving it off-axis breaks symmetry)
        var onAxis = SymmetryOps.MoveDelta(10, 5, 1, 1, 4, 2, cx: 10, cy: 0, refX: 5, refY: 5, vertical: true, horizontal: false);
        Assert.Equal((0, 2), onAxis);
    }

    [Fact]
    public void Both_axes_mirror_each_component_independently()
    {
        // grab top-left quadrant (ref 5,5); the bottom-right partner mirrors BOTH x and y
        var tl = SymmetryOps.MoveDelta(5, 5, 1, 1, 3, 4, 10, 10, 5, 5, vertical: true, horizontal: true);
        var br = SymmetryOps.MoveDelta(15, 15, 1, 1, 3, 4, 10, 10, 5, 5, vertical: true, horizontal: true);
        Assert.Equal((3, 4), tl);
        Assert.Equal((-3, -4), br);
    }

    // ---- RotateGroup: rotate one side, reflect onto partners, stay symmetric ----

    // the vertical mirror of a pose, using the pose's rotated footprint — the invariant a symmetric pair must keep
    private static (int X, int Y, int Rot) VMirror(int x, int y, int rot, int w, int h, int cx) =>
        (2 * cx - (x + w - 1), y, GridMath.Norm(360 - rot));

    [Fact]
    public void Rotating_a_vertical_pair_keeps_it_mirror_symmetric()
    {
        const int cx = 10;
        // a 1x3 part on the left and its vertical mirror on the right (as symmetry-mode placement would lay them)
        var a = new SymmetryOps.Item("L", 5, 5, 1, 3, 0, Sheet: false);
        var (bmx, bmy, brot) = VMirror(a.X, a.Y, a.Rot, a.W, a.H, cx);
        var b = new SymmetryOps.Item("L", bmx, bmy, 1, 3, brot, Sheet: false);

        var poses = SymmetryOps.RotateGroup([a, b], 90, cx, cy: 0, vertical: true, horizontal: false);

        // A actually rotated (its footprint is now 3x1), and B is exactly the vertical mirror of A's new pose
        Assert.NotEqual((a.X, a.Y, a.Rot), poses[0]);
        var (nw, nh) = (a.H, a.W);   // 90deg swap
        Assert.Equal(VMirror(poses[0].X, poses[0].Y, poses[0].Rot, nw, nh, cx), poses[1]);
    }

    [Fact]
    public void A_part_with_no_partner_rotates_in_place()
    {
        // a lone selected part (no mirror sibling in the set) still rotates rather than being left untouched
        var lone = new SymmetryOps.Item("X", 3, 3, 1, 3, 0, Sheet: false);
        var poses = SymmetryOps.RotateGroup([lone], 90, cx: 10, cy: 0, vertical: true, horizontal: false);
        Assert.Single(poses);
        Assert.NotEqual((lone.X, lone.Y, lone.Rot), poses[0]);
    }
}
