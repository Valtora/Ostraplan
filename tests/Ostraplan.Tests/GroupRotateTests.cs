using System.Collections.Generic;
using System.Linq;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>Group rotation geometry — pure, no game data needed.</summary>
public class GroupRotateTests
{
    private static GroupRotate.Item Tile(int x, int y, bool sheet = false) => new(x, y, 1, 1, 0, sheet);

    /// <summary>Cell positions, sorted, for order-independent comparison.</summary>
    private static (int, int)[] Cells((int X, int Y, int Rot)[] poses) =>
        poses.Select(p => (p.X, p.Y)).OrderBy(c => c.Item1).ThenBy(c => c.Item2).ToArray();

    // an L of unit tiles:  X .
    //                      X X
    private static List<GroupRotate.Item> L() => [Tile(0, 0), Tile(0, 1), Tile(1, 1)];

    [Fact]
    public void Clockwise_turns_the_shape_and_advances_each_part()
    {
        var poses = GroupRotate.Rotate(L(), 90);
        // X .   ->   X X
        // X X        X .
        Assert.Equal([(0, 0), (0, 1), (1, 0)], Cells(poses));
        Assert.All(poses, p => Assert.Equal(90, p.Rot));   // non-sheet parts turn 0 -> 90
    }

    [Fact]
    public void Counter_clockwise_is_the_mirror_of_clockwise()
    {
        var poses = GroupRotate.Rotate(L(), -90);
        // X .   ->   . X
        // X X        X X
        Assert.Equal([(0, 1), (1, 0), (1, 1)], Cells(poses));
        Assert.All(poses, p => Assert.Equal(270, p.Rot));
    }

    [Fact]
    public void Four_clockwise_turns_return_to_the_start()
    {
        var cur = L();
        for (var i = 0; i < 4; i++)
            cur = [.. GroupRotate.Rotate(cur, 90).Select(p => new GroupRotate.Item(p.X, p.Y, 1, 1, p.Rot, false))];
        Assert.Equal([(0, 0), (0, 1), (1, 1)], cur.Select(i => (i.X, i.Y)).OrderBy(c => c.Item1).ThenBy(c => c.Item2).ToArray());
        Assert.All(cur, i => Assert.Equal(0, i.Rot));   // a square bbox drifts none over a full turn
    }

    [Fact]
    public void A_non_square_group_returns_exactly_after_four_clockwise_turns()
    {
        // a 2x1 domino (even-parity, non-square bbox) used to creep +1 tile per turn under round-half-up
        // re-centring — four turns landed it at (2,2),(3,2). Symmetric rounding makes the turn round-trip.
        var cur = new List<GroupRotate.Item> { Tile(0, 0), Tile(1, 0) };
        for (var i = 0; i < 4; i++)
            cur = [.. GroupRotate.Rotate(cur, 90).Select(p => new GroupRotate.Item(p.X, p.Y, 1, 1, p.Rot, false))];
        Assert.Equal([(0, 0), (1, 0)], Cells([.. cur.Select(i => (i.X, i.Y, 0))]));
    }

    [Fact]
    public void A_clockwise_turn_and_its_inverse_cancel_for_a_non_square_group()
    {
        // CW then CCW must be identity in position for any bbox (the drift bug broke this for non-square bounds)
        var start = new List<GroupRotate.Item> { Tile(0, 0), Tile(1, 0), Tile(2, 0) };   // a 3x1 row
        var cw = GroupRotate.Rotate(start, 90).Select(p => new GroupRotate.Item(p.X, p.Y, 1, 1, p.Rot, false)).ToList();
        var back = GroupRotate.Rotate(cw, -90);
        Assert.Equal(Cells([.. start.Select(i => (i.X, i.Y, 0))]), Cells(back));
    }

    [Fact]
    public void Sheet_items_keep_rotation_zero_but_still_move()
    {
        // two horizontal walls (sheet) rotate to a vertical pair — positions turn, rot stays 0
        var poses = GroupRotate.Rotate([Tile(0, 0, sheet: true), Tile(1, 0, sheet: true)], 90);
        Assert.All(poses, p => Assert.Equal(0, p.Rot));
        Assert.Single(poses.Select(p => p.X).Distinct());   // one column now
    }

    [Fact]
    public void A_wide_part_swaps_its_footprint_orientation()
    {
        // a 3×1 part turns to a 1×3 pose (its own rotation advances)
        var poses = GroupRotate.Rotate([new GroupRotate.Item(0, 0, 3, 1, 0, false)], 90);
        Assert.Equal(90, poses[0].Rot);
    }
}
