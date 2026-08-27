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
        var poses = Symmetry.Poses(3, 4, 90, 1, 1, SymAxis.OnTile(10, 10), vertical: false, horizontal: false).ToList();
        Assert.Equal([(3, 4, 90)], poses);
    }

    [Fact]
    public void Vertical_reflects_x_and_flips_east_west_rotation()
    {
        // axis col 10; a 1×1 at col 7 mirrors to 2·10−7 = 13; rot 90 (facing E) flips to 270 (W)
        var poses = Symmetry.Poses(7, 4, 90, 1, 1, SymAxis.OnTile(10, 10), vertical: true, horizontal: false).ToList();
        Assert.Equal(2, poses.Count);
        Assert.Equal((7, 4, 90), poses[0]);
        Assert.Equal((13, 4, 270), poses[1]);
    }

    [Fact]
    public void Horizontal_reflects_y_and_flips_north_south_rotation()
    {
        // axis row 10; a 1×1 at row 6 mirrors to 14; rot 0 (N) flips to 180 (S), rot 90 (E) is unchanged
        var poses = Symmetry.Poses(5, 6, 0, 1, 1, SymAxis.OnTile(10, 10), vertical: false, horizontal: true).ToList();
        Assert.Equal((5, 6, 0), poses[0]);
        Assert.Equal((5, 14, 180), poses[1]);
    }

    [Fact]
    public void Both_yields_the_cursor_pose_and_three_mirrors()
    {
        var poses = Symmetry.Poses(7, 6, 90, 1, 1, SymAxis.OnTile(10, 12), vertical: true, horizontal: true).ToList();
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
        var poses = Symmetry.Poses(10, 5, 0, 2, 1, SymAxis.OnTile(20, 5), vertical: true, horizontal: false).ToList();
        Assert.Equal((29, 5, 0), poses[1]);
    }

    [Fact]
    public void Rotated_footprint_keeps_its_dimensions_under_every_mirror()
    {
        // rot 90 → rotated footprint 1×2 at (10,5); axis (20,15)
        var poses = Symmetry.Poses(10, 5, 90, 1, 2, SymAxis.OnTile(20, 15), vertical: true, horizontal: true).ToList();
        Assert.Equal((30, 5, 270), poses[1]);   // vertical:  mx = 40−10 = 30
        Assert.Equal((10, 24, 90), poses[2]);   // horizontal: my = 30−6 = 24
        Assert.Equal((30, 24, 270), poses[3]);  // both
    }

    [Fact]
    public void A_part_on_the_axis_mirrors_onto_itself()
    {
        // a 1×1 exactly on the axis column: the mirror coincides with the original (the caller dedups)
        var poses = Symmetry.Poses(10, 4, 0, 1, 1, SymAxis.OnTile(10, 10), vertical: true, horizontal: false).ToList();
        Assert.Equal(poses[0], poses[1]);
    }

    [Fact]
    public void Reflection_is_an_involution()
    {
        const int cx = 25;
        var mirror = Symmetry.Poses(3, 0, 0, 1, 1, SymAxis.OnTile(cx, 0), vertical: true, horizontal: false).ElementAt(1);
        var back = Symmetry.Poses(mirror.X, mirror.Y, mirror.Rot, 1, 1, SymAxis.OnTile(cx, 0), vertical: true, horizontal: false).ElementAt(1);
        Assert.Equal((3, 0, 0), back);   // reflecting the mirror returns the original, pose and rotation
    }

    [Fact]
    public void Station_scale_coordinates_reflect_without_drift()
    {
        // large tile indices (a station-sized grid) stay exact — integer math, no rounding
        var poses = Symmetry.Poses(2500, 10, 0, 1, 1, SymAxis.OnTile(3000, 10), vertical: true, horizontal: false).ToList();
        Assert.Equal((3500, 10, 0), poses[1]);
    }

    // ---- IsSymmetricSet: the strict gate that decides whether symmetry-preserving edits apply ----

    [Fact]
    public void A_mirrored_pair_is_a_symmetric_set()
    {
        const int cx = 10;
        // a part at col 5 and its vertical mirror at 2·10−5 = 15, same def
        var set = new[]
        {
            new Symmetry.SetItem("W", 5, 3, 1, 1),
            new Symmetry.SetItem("W", 15, 3, 1, 1),
        };
        Assert.True(Symmetry.IsSymmetricSet(set, SymAxis.OnTile(cx, 0), vertical: true, horizontal: false));
    }

    [Fact]
    public void A_one_sided_paste_is_not_a_symmetric_set()
    {
        const int cx = 10;
        // two parts both on the same side of the axis (a straight copy, no mirror partner present)
        var set = new[]
        {
            new Symmetry.SetItem("W", 15, 3, 1, 1),
            new Symmetry.SetItem("W", 16, 3, 1, 1),
        };
        Assert.False(Symmetry.IsSymmetricSet(set, SymAxis.OnTile(cx, 0), vertical: true, horizontal: false));
    }

    [Fact]
    public void A_partner_of_the_wrong_def_does_not_count()
    {
        const int cx = 10;
        // the mirror tile is occupied, but by a different part type — not a genuine partner
        var set = new[]
        {
            new Symmetry.SetItem("W", 5, 3, 1, 1),
            new Symmetry.SetItem("D", 15, 3, 1, 1),
        };
        Assert.False(Symmetry.IsSymmetricSet(set, SymAxis.OnTile(cx, 0), vertical: true, horizontal: false));
    }

    [Fact]
    public void An_on_axis_part_mirrors_onto_itself_and_counts()
    {
        const int cx = 10;
        var set = new[] { new Symmetry.SetItem("W", 10, 3, 1, 1) };   // exactly on the vertical axis tile
        Assert.True(Symmetry.IsSymmetricSet(set, SymAxis.OnTile(cx, 0), vertical: true, horizontal: false));
    }

    [Fact]
    public void Both_axes_need_the_full_quad()
    {
        const int cx = 10;
        const int cy = 10;
        // a top-left part with only its vertical and horizontal partners, missing the diagonal → not a full quad
        var trio = new[]
        {
            new Symmetry.SetItem("W", 5, 5, 1, 1),
            new Symmetry.SetItem("W", 15, 5, 1, 1),   // vertical mirror
            new Symmetry.SetItem("W", 5, 15, 1, 1),   // horizontal mirror
        };
        Assert.False(Symmetry.IsSymmetricSet(trio, SymAxis.OnTile(cx, cy), vertical: true, horizontal: true));

        var quad = trio.Append(new Symmetry.SetItem("W", 15, 15, 1, 1)).ToArray();   // + diagonal
        Assert.True(Symmetry.IsSymmetricSet(quad, SymAxis.OnTile(cx, cy), vertical: true, horizontal: true));
    }

    [Fact]
    public void Symmetry_off_is_never_a_symmetric_set()
    {
        var set = new[] { new Symmetry.SetItem("W", 5, 3, 1, 1) };
        Assert.False(Symmetry.IsSymmetricSet(set, SymAxis.OnTile(10, 0), vertical: false, horizontal: false));
    }

    // ---- seam axes: an even-width arrangement mirrors about a line between two columns (issue #46) ----

    [Fact]
    public void An_even_width_pair_mirrors_about_the_seam_between_them()
    {
        // cols 9 and 10 are each other's mirror about the 9|10 seam, and about nothing else
        var axis = SymAxis.Centring(9, 10, 0, 0);
        Assert.Equal((10, 3, 0), Symmetry.Poses(9, 3, 0, 1, 1, axis, vertical: true, horizontal: false).ElementAt(1));
        Assert.Equal((9, 3, 0), Symmetry.Poses(10, 3, 0, 1, 1, axis, vertical: true, horizontal: false).ElementAt(1));
    }

    [Fact]
    public void An_even_width_part_straddling_a_seam_mirrors_onto_itself()
    {
        // a 2-wide part covering cols [9,10] is centred on that seam, so it is its own mirror — the even-width
        // equivalent of a 1-wide part standing on a column axis
        var poses = Symmetry.Poses(9, 3, 0, 2, 1, new SymAxis(19, 0), vertical: true, horizontal: false).ToList();
        Assert.Equal(poses[0], poses[1]);
    }

    [Fact]
    public void Reflection_about_a_seam_is_an_involution()
    {
        var axis = new SymAxis(19, 0);
        var mirror = Symmetry.Poses(3, 0, 90, 1, 1, axis, vertical: true, horizontal: false).ElementAt(1);
        var back = Symmetry.Poses(mirror.X, mirror.Y, mirror.Rot, 1, 1, axis, vertical: true, horizontal: false).ElementAt(1);
        Assert.Equal((3, 0, 90), back);
    }

    [Fact]
    public void An_even_width_hull_is_symmetric_about_its_seam_and_about_neither_neighbouring_column()
    {
        // The bug this type exists for, stated as a test. A 20-wide hull's outermost pair is cols 0 and 19; the
        // axis that mirrors them onto each other is the 9|10 seam. Snapped to either adjacent column instead —
        // all a whole-tile axis could ever offer — the same arrangement reads as asymmetric, and a symmetry-mode
        // build lays its mirror half a tile off the hull.
        var set = new[]
        {
            new Symmetry.SetItem("W", 0, 3, 1, 1),
            new Symmetry.SetItem("W", 19, 3, 1, 1),
        };
        Assert.True(Symmetry.IsSymmetricSet(set, SymAxis.Centring(0, 19, 0, 0), vertical: true, horizontal: false));
        Assert.False(Symmetry.IsSymmetricSet(set, SymAxis.OnTile(9, 0), vertical: true, horizontal: false));
        Assert.False(Symmetry.IsSymmetricSet(set, SymAxis.OnTile(10, 0), vertical: true, horizontal: false));
    }

    [Fact]
    public void A_seam_axis_mirrors_a_multi_tile_span_as_a_whole()
    {
        // a 3-wide part covering [4,6] about the 9|10 seam mirrors onto [13,15], top-left 13
        var poses = Symmetry.Poses(4, 3, 0, 3, 1, new SymAxis(19, 0), vertical: true, horizontal: false).ToList();
        Assert.Equal((13, 3, 0), poses[1]);
    }
}
