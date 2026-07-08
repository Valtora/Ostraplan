using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The pure symmetry geometry (<see cref="Symmetry.Poses"/>) — reflection positions, mirrored rotations,
/// multi-tile and rotated footprints, on-axis coincidence, involution and station-scale coordinates. This is the
/// math that has to be exactly right for symmetric placement to be trustworthy; it was previously buried in the
/// canvas and untested.
/// </summary>
public class SymmetryTests
{
    [Fact]
    public void Off_yields_only_the_cursor_pose()
    {
        var poses = Symmetry.Poses(3, 4, 90, 1, 1, 10, 10, vertical: false, horizontal: false).ToList();
        Assert.Equal([(3, 4, 90)], poses);
    }

    [Fact]
    public void Vertical_reflects_x_and_flips_east_west_rotation()
    {
        // axis col 10; a 1×1 at col 7 mirrors to 2·10−7 = 13; rot 90 (facing E) flips to 270 (W)
        var poses = Symmetry.Poses(7, 4, 90, 1, 1, 10, 10, vertical: true, horizontal: false).ToList();
        Assert.Equal(2, poses.Count);
        Assert.Equal((7, 4, 90), poses[0]);
        Assert.Equal((13, 4, 270), poses[1]);
    }

    [Fact]
    public void Horizontal_reflects_y_and_flips_north_south_rotation()
    {
        // axis row 10; a 1×1 at row 6 mirrors to 14; rot 0 (N) flips to 180 (S), rot 90 (E) is unchanged
        var poses = Symmetry.Poses(5, 6, 0, 1, 1, 10, 10, vertical: false, horizontal: true).ToList();
        Assert.Equal((5, 6, 0), poses[0]);
        Assert.Equal((5, 14, 180), poses[1]);
    }

    [Fact]
    public void Both_yields_the_cursor_pose_and_three_mirrors()
    {
        var poses = Symmetry.Poses(7, 6, 90, 1, 1, 10, 12, vertical: true, horizontal: true).ToList();
        Assert.Equal(4, poses.Count);
        Assert.Equal((7, 6, 90), poses[0]);     // cursor
        Assert.Equal((13, 6, 270), poses[1]);   // vertical:  x 7→13, rot 90→270
        Assert.Equal((7, 18, 90), poses[2]);    // horizontal: y 6→18, rot 90→90
        Assert.Equal((13, 18, 270), poses[3]);  // both (point reflection): rot 90→270
    }

    [Fact]
    public void Multi_tile_footprint_reflects_the_whole_span()
    {
        // a 2-wide part occupying cols [10,11] about axis col 20 → mirror occupies [29,30], top-left 29
        var poses = Symmetry.Poses(10, 5, 0, 2, 1, 20, 5, vertical: true, horizontal: false).ToList();
        Assert.Equal((29, 5, 0), poses[1]);
    }

    [Fact]
    public void Rotated_footprint_keeps_its_dimensions_under_every_mirror()
    {
        // rot 90 → rotated footprint 1×2 at (10,5); axis (20,15)
        var poses = Symmetry.Poses(10, 5, 90, 1, 2, 20, 15, vertical: true, horizontal: true).ToList();
        Assert.Equal((30, 5, 270), poses[1]);   // vertical:  mx = 40−10 = 30
        Assert.Equal((10, 24, 90), poses[2]);   // horizontal: my = 30−6 = 24
        Assert.Equal((30, 24, 270), poses[3]);  // both
    }

    [Fact]
    public void A_part_on_the_axis_mirrors_onto_itself()
    {
        // a 1×1 exactly on the axis column: the mirror coincides with the original (the caller dedups)
        var poses = Symmetry.Poses(10, 4, 0, 1, 1, 10, 10, vertical: true, horizontal: false).ToList();
        Assert.Equal(poses[0], poses[1]);
    }

    [Fact]
    public void Reflection_is_an_involution()
    {
        const int cx = 25;
        var mirror = Symmetry.Poses(3, 0, 0, 1, 1, cx, 0, vertical: true, horizontal: false).ElementAt(1);
        var back = Symmetry.Poses(mirror.X, mirror.Y, mirror.Rot, 1, 1, cx, 0, vertical: true, horizontal: false).ElementAt(1);
        Assert.Equal((3, 0, 0), back);   // reflecting the mirror returns the original, pose and rotation
    }

    [Fact]
    public void Station_scale_coordinates_reflect_without_drift()
    {
        // large tile indices (a station-sized grid) stay exact — integer math, no rounding
        var poses = Symmetry.Poses(2500, 10, 0, 1, 1, 3000, 10, vertical: true, horizontal: false).ToList();
        Assert.Equal((3500, 10, 0), poses[1]);
    }
}
