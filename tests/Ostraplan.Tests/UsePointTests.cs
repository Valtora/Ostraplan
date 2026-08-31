using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The declared use point (#41): the tile the game marks with footprints under the build cursor, which is what
/// tells a symmetrical-looking fixture's facing apart on a plan.
/// </summary>
public class UsePointTests
{
    private static PartDef Part(int w, int h, params (string Name, double X, double Y)[] points)
    {
        var catalog = new Fixtures()
            .Part("ItmThing", w: w, h: h,
                  mapPoints: points.ToDictionary(p => p.Name, p => (p.X, p.Y), StringComparer.Ordinal))
            .Build();
        return catalog.ByDefName["ItmThing"];
    }

    [Fact]
    public void A_part_that_declares_no_use_point_has_none()
    {
        Assert.Null(UsePoint.Raw(Part(1, 1)));
        Assert.Null(UsePoint.Raw(Part(1, 1, ("PowerOutput", 0, 16))));
        Assert.False(UsePoint.Has(Part(2, 2)));
        Assert.Null(UsePoint.Raw(null));
    }

    [Fact]
    public void A_use_point_at_the_items_own_centre_is_not_drawn()
    {
        // The game's build cursor tests exactly this before it shows the footprints: a point at (0,0) is the
        // default every condowner gets and says nothing about which way the item faces.
        Assert.Null(UsePoint.Raw(Part(2, 2, ("use", 0, 0))));
        Assert.NotNull(UsePoint.Raw(Part(2, 2, ("use", 0, -16))));
        Assert.NotNull(UsePoint.Raw(Part(2, 2, ("use", 16, 0))));
    }

    [Fact]
    public void A_point_below_the_part_lands_on_the_tile_below_it()
    {
        // 16 px is one tile and +y is up in condowner space, so (0, -16) on a 1x1 is the tile underneath.
        var part = Part(1, 1, ("use", 0, -16));
        Assert.Equal((0, 1), UsePoint.Tile(part, 0, 0, 0));
        Assert.Equal((7, 4), UsePoint.Tile(part, 7, 3, 0));
    }

    [Fact]
    public void The_point_turns_with_the_part()
    {
        // A 1x1 worked from below at rest is worked from the left at 90 degrees, and so on round.
        var part = Part(1, 1, ("use", 0, -16));
        Assert.Equal((0, 1), UsePoint.Tile(part, 0, 0, 0));
        Assert.Equal((-1, 0), UsePoint.Tile(part, 0, 0, 90));
        Assert.Equal((0, -1), UsePoint.Tile(part, 0, 0, 180));
        Assert.Equal((1, 0), UsePoint.Tile(part, 0, 0, 270));
    }

    [Fact]
    public void The_drawn_position_is_the_tiles_centre()
    {
        // What the canvas draws around: cell (c, r)'s centre is (c + 0.5, r + 0.5).
        var at = UsePoint.At(Part(1, 1, ("use", 0, -16)), 0, 0, 0);
        Assert.NotNull(at);
        Assert.Equal(0.5, at.Value.X, 6);
        Assert.Equal(1.5, at.Value.Y, 6);
    }

    [SkippableFact]
    public void The_arcade_cabinet_is_worked_from_the_front_and_the_rack_from_the_side()
    {
        var g = TestData.RequireGame();
        var arcade = g.Catalog.Lookup("ItmArcadeGame01");
        Skip.If(arcade is null, "this install has no arcade cabinet");

        // 2x3, and the use point sits one row past the bottom of the footprint: the front of the machine, which
        // is the whole reason it needs a mark (it is symmetrical to look at and usable from one side).
        Assert.Equal((2, 3), (arcade!.Item.Width, arcade.Item.Height));
        Assert.Equal((1, 3), UsePoint.Tile(arcade, 0, 0, 0));
        Assert.Equal((1, 4), UsePoint.Tile(arcade, 0, 1, 0));   // moves with the part
        // Its point sits exactly on the line between the cabinet's two columns (x = 1.0 of a 2-wide part), so
        // which column it rounds into flips about the origin — the game's own MathUtils.RoundToInt rounds away
        // from zero and ShipGrid.MapPointTile reproduces it, so this is faithful rather than a bug to correct.
        Assert.Equal((-1, 3), UsePoint.Tile(arcade, -1, 0, 0));

        if (g.Catalog.Lookup("ItmRack1x101") is { } rack)
            Assert.Equal((1, 0), UsePoint.Tile(rack, 0, 0, 0));   // 1x1, worked from the tile to its right
    }

    [SkippableFact]
    public void Only_the_parts_a_crew_member_walks_up_to_carry_one()
    {
        var g = TestData.RequireGame();
        var with = g.Catalog.Parts.Count(UsePoint.Has);
        Skip.If(with == 0, "this install's data declares no use points");

        // 103 of 355 buildable parts on stock 1.0.0.13. Asserted as a band rather than a figure so the suite does
        // not break on a game patch, but tight enough to catch the rule inverting.
        Assert.InRange(with, 20, g.Catalog.Parts.Count - 20);
        // Walls and floors are not worked from anywhere.
        if (g.Catalog.Lookup("ItmWall1x1") is { } wall) Assert.False(UsePoint.Has(wall));
    }
}
