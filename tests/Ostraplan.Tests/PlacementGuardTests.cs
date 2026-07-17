using Ostraplan.App;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// Issue #6. The paint-stroke dedup guard (<see cref="ShipCanvas.SameDefAtPose"/>) must skip only an EXACT
/// duplicate — the same part already at the same tile AND rotation — never a legal same-def overlap. Cargo
/// pods stack by sharing one row, so an overlap-based guard (the old bug) made a cargo train unbuildable
/// even though CheckFit approved it. CheckFit stays the sole judge of whether an overlap is legal.
/// </summary>
public class PlacementGuardTests
{
    private static Placement P(string def, int x, int y, int rot = 0) => new() { DefName = def, X = x, Y = y, Rot = rot };

    [Fact]
    public void An_exact_duplicate_is_skipped()
    {
        var atOrigin = new[] { P("ItmCargoPod01", 10, 15) };
        Assert.True(ShipCanvas.SameDefAtPose(atOrigin, 10, 15, 0, "ItmCargoPod01"));
    }

    [Fact]
    public void A_same_def_overlap_at_a_different_pose_is_allowed()
    {
        // The overlapping pod covers this new pose's origin tile (10,15) but sits at its own pose (10,10) —
        // that is a legal stack, not a duplicate, so the guard must not veto it.
        var atOrigin = new[] { P("ItmCargoPod01", 10, 10) };
        Assert.False(ShipCanvas.SameDefAtPose(atOrigin, 10, 15, 0, "ItmCargoPod01"));
    }

    [Fact]
    public void A_different_rotation_at_the_same_tile_is_allowed()
    {
        var atOrigin = new[] { P("ItmCargoPod01", 10, 15, 0) };
        Assert.False(ShipCanvas.SameDefAtPose(atOrigin, 10, 15, 90, "ItmCargoPod01"));
    }

    [Fact]
    public void A_different_def_at_the_same_pose_is_allowed()
    {
        var atOrigin = new[] { P("ItmWall1x1", 10, 15) };
        Assert.False(ShipCanvas.SameDefAtPose(atOrigin, 10, 15, 0, "ItmCargoPod01"));
    }
}
