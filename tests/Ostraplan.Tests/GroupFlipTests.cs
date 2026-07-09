using System.Collections.Generic;
using System.Linq;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>Selection-mirror geometry — pure, no game data needed.</summary>
public class GroupFlipTests
{
    private static GroupRotate.Item Tile(int x, int y, bool sheet = false) => new(x, y, 1, 1, 0, sheet);

    private static (int, int)[] Cells((int X, int Y, int Rot)[] poses) =>
        poses.Select(p => (p.X, p.Y)).OrderBy(c => c.Item1).ThenBy(c => c.Item2).ToArray();

    // an L of unit tiles:  X .
    //                      X X
    private static List<GroupRotate.Item> L(bool sheet = false) => [Tile(0, 0, sheet), Tile(0, 1, sheet), Tile(1, 1, sheet)];

    [Fact]
    public void Horizontal_flip_mirrors_left_to_right()
    {
        var poses = GroupFlip.Flip(L(), horizontal: true);
        // X .   ->   . X
        // X X        X X
        Assert.Equal([(0, 1), (1, 0), (1, 1)], Cells(poses));
    }

    [Fact]
    public void Vertical_flip_mirrors_top_to_bottom()
    {
        var poses = GroupFlip.Flip(L(), horizontal: false);
        // X .   ->   X X
        // X X        X .
        Assert.Equal([(0, 0), (0, 1), (1, 0)], Cells(poses));
    }

    [Fact]
    public void Horizontal_flip_remaps_each_rotation_to_its_east_west_mirror()
    {
        foreach (var (rot, expected) in new[] { (0, 0), (90, 270), (180, 180), (270, 90) })
        {
            var poses = GroupFlip.Flip([new GroupRotate.Item(5, 5, 1, 1, rot, false)], horizontal: true);
            Assert.Equal((5, 5, expected), poses[0]);   // a lone part reflects IN PLACE, rotation remapped
        }
    }

    [Fact]
    public void Vertical_flip_remaps_each_rotation_to_its_north_south_mirror()
    {
        foreach (var (rot, expected) in new[] { (0, 180), (90, 90), (180, 0), (270, 270) })
        {
            var poses = GroupFlip.Flip([new GroupRotate.Item(5, 5, 1, 1, rot, false)], horizontal: false);
            Assert.Equal((5, 5, expected), poses[0]);
        }
    }

    [Fact]
    public void Flipping_twice_returns_to_the_start()
    {
        foreach (var horizontal in new[] { true, false })
        {
            var once = GroupFlip.Flip(L(), horizontal);
            var back = GroupFlip.Flip([.. once.Select(p => new GroupRotate.Item(p.X, p.Y, 1, 1, p.Rot, false))], horizontal);
            Assert.Equal(L().Select(i => (i.X, i.Y, i.Rot)).OrderBy(c => c).ToArray(),
                back.Select(p => (p.X, p.Y, p.Rot)).OrderBy(c => c).ToArray());
        }
    }

    [Fact]
    public void Sheet_items_keep_rotation_zero_but_still_move()
    {
        var poses = GroupFlip.Flip(L(sheet: true), horizontal: true);
        Assert.All(poses, p => Assert.Equal(0, p.Rot));           // walls/floors auto-tile, never turn
        Assert.Equal([(0, 1), (1, 0), (1, 1)], Cells(poses));     // but the arrangement still mirrors
    }

    [Fact]
    public void A_horizontal_part_flipped_horizontally_stays_put()
    {
        // a 3×1 part mirrored across a vertical axis maps onto itself (footprint is mirror-invariant)
        var poses = GroupFlip.Flip([new GroupRotate.Item(0, 0, 3, 1, 0, false)], horizontal: true);
        Assert.Equal((0, 0, 0), poses[0]);
    }

    [Fact]
    public void Every_emitted_pose_is_a_real_rotation()
    {
        // the Law-safe guarantee: a flip can only ever produce buildable 0/90/180/270 poses
        var mixed = new List<GroupRotate.Item>
        {
            new(0, 0, 2, 3, 90, false), new(4, 1, 1, 1, 270, false),
            new(2, 5, 3, 1, 180, false), new(6, 6, 1, 1, 0, true),
        };
        foreach (var horizontal in new[] { true, false })
            Assert.All(GroupFlip.Flip(mixed, horizontal), p => Assert.Contains(p.Rot, new[] { 0, 90, 180, 270 }));
    }
}
