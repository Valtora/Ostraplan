using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// <see cref="SymAxis"/>: the half-tile unit that lets a symmetry axis sit on a column's middle or on the seam
/// between two. The distinction is the whole reason the type exists, so each constructor is held to which of the
/// two it produces, and the frame crossing is held to landing where the canvas actually draws.
/// </summary>
public class SymAxisTests
{
    [Fact]
    public void OnTile_is_a_column_axis()
    {
        var axis = SymAxis.OnTile(10, 4);
        Assert.Equal(20, axis.HX);
        Assert.True(axis.OnColumn);
        Assert.True(axis.OnRow);
    }

    [Fact]
    public void Centring_an_odd_span_lands_on_its_middle_column()
    {
        // cols [0,20] is 21 wide, so the middle column 10 exists and is the axis
        var axis = SymAxis.Centring(0, 20, 0, 20);
        Assert.True(axis.OnColumn);
        Assert.Equal(SymAxis.OnTile(10, 10), axis);
    }

    [Fact]
    public void Centring_an_even_span_lands_on_the_seam()
    {
        // cols [0,19] is 20 wide: the true centre line is between 9 and 10, which no whole tile can name
        var axis = SymAxis.Centring(0, 19, 0, 19);
        Assert.False(axis.OnColumn);
        Assert.False(axis.OnRow);
        Assert.Equal(19, axis.HX);
    }

    [Fact]
    public void A_column_axis_draws_down_the_middle_of_its_tile()
    {
        // corner frame: tile 10 covers [10,11), so its middle is 10.5
        Assert.Equal(10.5, SymAxis.OnTile(10, 3).Corner.X, 9);
    }

    [Fact]
    public void A_seam_axis_draws_on_the_grid_line()
    {
        // the seam between tiles 9 and 10 is the whole number 10 in the corner frame — a line the grid already has
        Assert.Equal(10.0, new SymAxis(19, 0).Corner.X, 9);
    }

    [Fact]
    public void NearestTo_snaps_a_tile_middle_to_that_column()
    {
        Assert.Equal(SymAxis.OnTile(10, 4), SymAxis.NearestTo((10.5, 4.5)));
    }

    [Fact]
    public void NearestTo_snaps_a_grid_line_to_the_seam()
    {
        Assert.Equal(new SymAxis(19, 9), SymAxis.NearestTo((10.0, 5.0)));
    }

    [Theory]
    [InlineData(20, 8)]    // column / column
    [InlineData(19, 9)]    // seam / seam
    [InlineData(19, 8)]    // seam / column, the mixed case a drag passes through
    public void Corner_round_trips_through_NearestTo(int hx, int hy)
    {
        var axis = new SymAxis(hx, hy);
        Assert.Equal(axis, SymAxis.NearestTo(axis.Corner));
    }

    [Fact]
    public void A_seam_resolves_to_the_tile_on_its_higher_side()
    {
        Assert.Equal((10, 5), new SymAxis(19, 9).Cell);
        Assert.Equal((10, 4), SymAxis.OnTile(10, 4).Cell);
    }
}
