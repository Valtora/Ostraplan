using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The load↔export coordinate/rotation mapping (<see cref="ShipGrid"/>). Export must be the exact inverse of the
/// template loader or a spawned ship comes out shifted or mis-rotated — a class of bug that only shows up in game.
/// Pure math; no install.
/// </summary>
public class CoordinateMapTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 270)]     // the game's CCW fRotation maps to Ostraplan's CW rot — the sign inverts
    [InlineData(180, 180)]
    [InlineData(270, 90)]
    [InlineData(-90, 90)]
    [InlineData(360, 0)]
    public void ToRot_maps_game_ccw_to_ostraplan_rotation(double fRotation, int expected) =>
        Assert.Equal(expected, ShipGrid.ToRot(fRotation));

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Export_rotation_is_the_inverse_of_the_loader(int rot)
    {
        // ShipExport writes fRotation = Norm(-rot); loading that back must recover the original rot.
        var exportedFRotation = GridMath.Norm(-rot);
        Assert.Equal(rot, ShipGrid.ToRot(exportedFRotation));
    }

    [Theory]
    [InlineData(1, 1, 0)]
    [InlineData(2, 3, 0)]
    [InlineData(2, 3, 90)]
    [InlineData(4, 2, 270)]
    [InlineData(3, 1, 180)]
    public void TemplateTile_round_trips_an_exported_part(int w, int h, int rot)
    {
        // A part at grid top-left (col,row), footprint w×h, rotation rot. Export it to a centre (fx,fy,fRotation)
        // exactly as ShipExport does (vShipPos = 0,0), then load it back — it must land on the same tile + rotation.
        const int col = 5, row = 7;
        var (wr, hr) = GridMath.Size(w, h, rot);
        var fx = col + (wr / 2.0 - 0.5);
        var fy = -(row + (hr / 2.0 - 0.5));
        var fRotation = GridMath.Norm(-rot);

        var recovered = ShipGrid.TemplateTile(fx, fy, fRotation, w, h, 0, 0);
        Assert.Equal((col, row, rot), recovered);
    }

    [Fact]
    public void Norm_wraps_into_the_four_quarters()
    {
        Assert.Equal(0, GridMath.Norm(360));
        Assert.Equal(90, GridMath.Norm(-270));
        Assert.Equal(270, GridMath.Norm(-90));
        Assert.Equal(180, GridMath.Norm(540));
    }

    [Fact]
    public void Size_swaps_dimensions_only_on_the_quarter_turns()
    {
        Assert.Equal((2, 3), GridMath.Size(2, 3, 0));
        Assert.Equal((3, 2), GridMath.Size(2, 3, 90));
        Assert.Equal((2, 3), GridMath.Size(2, 3, 180));
        Assert.Equal((3, 2), GridMath.Size(2, 3, 270));
    }
}
