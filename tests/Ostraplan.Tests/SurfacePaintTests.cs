using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// Surface painting: which brushes paint, which placed part a stroke re-skins on a tile, and how a
/// pattern picks between the two brushes. Game-free (synthetic catalog) — none of it needs real defs.
/// </summary>
public class SurfacePaintTests
{
    private static Catalog Cat() => new Fixtures()
        .Floor("Floor")
        .Floor("FloorChecker")
        .Wall("Wall")
        .Wall("WallArmoured")
        .Door("Door")                                                        // 1×1 door: wall layer, wall class
        .Part("BigDoor", 5, 1, tileConds: ["IsWall", "IsPortal"])            // 5×1 door: wall layer, NOT the wall class
        .Fixture("Bed")
        .Conduit("Conduit")
        .Container("Locker")
        .Build();

    [Fact]
    public void Only_one_by_one_walls_and_floors_are_brushes()
    {
        var cat = Cat();
        Assert.True(SurfacePaint.IsSurfaceBrush(cat, cat.Lookup("Floor")));
        Assert.True(SurfacePaint.IsSurfaceBrush(cat, cat.Lookup("Wall")));
        Assert.True(SurfacePaint.IsSurfaceBrush(cat, cat.Lookup("Door")));     // a 1×1 door IS the wall class

        Assert.False(SurfacePaint.IsSurfaceBrush(cat, cat.Lookup("BigDoor")));  // wrong footprint
        Assert.False(SurfacePaint.IsSurfaceBrush(cat, cat.Lookup("Bed")));      // wrong layer
        Assert.False(SurfacePaint.IsSurfaceBrush(cat, cat.Lookup("Conduit")));  // wrong layer
        Assert.False(SurfacePaint.IsSurfaceBrush(cat, cat.Lookup("Locker")));   // a container never swaps
        Assert.False(SurfacePaint.IsSurfaceBrush(cat, null));
    }

    [Fact]
    public void Surface_layer_is_wider_than_the_brush_rule()
    {
        // What stays bright in the Surfaces view is a layer question, so the 5×1 door counts even
        // though no brush can paint over it.
        var cat = Cat();
        Assert.True(SurfacePaint.IsSurfaceLayer(cat, cat.Lookup("BigDoor")));
        Assert.True(SurfacePaint.IsSurfaceLayer(cat, cat.Lookup("Floor")));
        Assert.False(SurfacePaint.IsSurfaceLayer(cat, cat.Lookup("Bed")));
        Assert.False(SurfacePaint.IsSurfaceLayer(cat, cat.Lookup("Conduit")));
    }

    [Fact]
    public void Swap_target_is_the_same_class_part_on_the_tile()
    {
        var cat = Cat();
        var doc = new ShipDocument(cat);
        var floor = Fixtures.Place(doc, "Floor", 3, 4);
        Fixtures.Place(doc, "Bed", 3, 4);          // a fixture standing on that floor
        Fixtures.Place(doc, "Wall", 9, 9);

        var floorBrush = cat.Lookup("FloorChecker")!;
        var wallBrush = cat.Lookup("WallArmoured")!;

        // the floor is found under the fixture — the whole point of painting the deck
        Assert.Same(floor, SurfacePaint.SwapTargetAt(doc, floorBrush, 3, 4));
        // a wall brush finds nothing there: right tile, wrong class
        Assert.Null(SurfacePaint.SwapTargetAt(doc, wallBrush, 3, 4));
        // and nothing at all off the ship
        Assert.Null(SurfacePaint.SwapTargetAt(doc, floorBrush, 50, 50));
    }

    [Fact]
    public void A_wall_stroke_passes_over_a_multi_tile_door()
    {
        // The class rule is what keeps doors safe from a wall stroke dragged along a run of hull.
        var cat = Cat();
        var doc = new ShipDocument(cat);
        Fixtures.Place(doc, "BigDoor", 2, 0);
        var wallBrush = cat.Lookup("WallArmoured")!;

        for (var x = 2; x < 7; x++) Assert.Null(SurfacePaint.SwapTargetAt(doc, wallBrush, x, 0));
    }

    [Fact]
    public void Containers_are_never_a_swap_target()
    {
        var cat = Cat();
        var doc = new ShipDocument(cat);
        Fixtures.Place(doc, "Locker", 0, 0);
        // a locker is a fixture, so no surface brush would match it anyway; assert it explicitly
        // because a container losing its cargo to a def-change is the failure that matters.
        Assert.Null(SurfacePaint.SwapTargetAt(doc, cat.Lookup("Floor")!, 0, 0));
    }

    [Fact]
    public void Painting_over_a_placed_surface_re_skins_it_in_place()
    {
        // The composition the canvas relies on: find the target, then hand it to the ordinary swap.
        var cat = Cat();
        var doc = new ShipDocument(cat);
        var given = new Placement { DefName = "Floor", X = 1, Y = 1, IsGiven = true };
        new PlaceCommand(given).Do(doc);

        var target = SurfacePaint.SwapTargetAt(doc, cat.Lookup("FloorChecker")!, 1, 1);
        Assert.Same(given, target);

        var swap = ReplaceOps.BuildSwap(doc, [target!], "FloorChecker");
        Assert.NotNull(swap);
        swap!.Value.Cmd.Do(doc);

        var now = Assert.Single(doc.Placements);
        Assert.Equal("FloorChecker", now.DefName);
        Assert.Equal((1, 1), (now.X, now.Y));
        Assert.True(now.IsGiven);   // a same-class re-skin keeps the tile's structural role
    }

    [Fact]
    public void Painting_the_skin_that_is_already_there_changes_nothing()
    {
        var cat = Cat();
        var doc = new ShipDocument(cat);
        var floor = Fixtures.Place(doc, "Floor", 0, 0);

        var target = SurfacePaint.SwapTargetAt(doc, cat.Lookup("Floor")!, 0, 0);
        Assert.Same(floor, target);
        Assert.Null(ReplaceOps.BuildSwap(doc, [target!], "Floor"));   // no-op, so a re-entering stroke is free
    }

    [Fact]
    public void A_floor_brush_finds_the_floor_under_a_wall()
    {
        // The under-wall case: the shipped ships floor most of their wall tiles, and those floors are what the
        // Floors focus exists to reach. Class matching sees straight past the wall standing on top.
        var cat = Cat();
        var doc = new ShipDocument(cat);
        var floor = Fixtures.Place(doc, "Floor", 2, 2);
        Fixtures.Place(doc, "Wall", 2, 2);

        Assert.Same(floor, SurfacePaint.SwapTargetAt(doc, cat.Lookup("FloorChecker")!, 2, 2));
        // and the wall on that same tile is still the wall brush's target, not the floor
        Assert.Equal("Wall", SurfacePaint.SwapTargetAt(doc, cat.Lookup("WallArmoured")!, 2, 2)?.DefName);
    }

    [SkippableFact]
    public void The_game_itself_allows_a_floor_and_a_wall_on_one_tile()
    {
        // Painting a floor UNDER a bare wall is not an exception Ostraplan grants itself: neither part's socket
        // mask forbids the other, so the game's own law permits the pair in either order. If a patch ever changes
        // that mask, this fails and the Fill mode's under-wall behaviour has to be revisited rather than discovered
        // by a user whose exported ship won't build.
        var g = TestData.RequireGame();
        var wall = g.Catalog.Lookup("ItmWall1x1");
        var floor = g.Catalog.Lookup("ItmFloorCAYL01");
        if (wall is null || floor is null) return;

        var onWall = new ShipDocument(g.Catalog);
        new PlaceCommand(new Placement { DefName = wall.DefName, X = 0, Y = 0 }).Do(onWall);
        Assert.True(CheckFit.Check(onWall, floor, 0, 0, 0, includeEnvelope: false).Ok, "floor onto a wall tile");

        var onFloor = new ShipDocument(g.Catalog);
        new PlaceCommand(new Placement { DefName = floor.DefName, X = 0, Y = 0 }).Do(onFloor);
        Assert.True(CheckFit.Check(onFloor, wall, 0, 0, 0, includeEnvelope: false).Ok, "wall onto a floor tile");
    }

    [Fact]
    public void The_focus_decides_what_is_bright_and_clickable()
    {
        var cat = Cat();
        var wall = cat.Lookup("Wall")!;
        var floor = cat.Lookup("Floor")!;
        var bed = cat.Lookup("Bed")!;

        Assert.True(SurfacePaint.IsFocusLayer(cat, wall, SurfaceFocus.Both));
        Assert.True(SurfacePaint.IsFocusLayer(cat, floor, SurfaceFocus.Both));

        // Floors focus drops the wall layer too — the only way to see or click a floor under a wall
        Assert.True(SurfacePaint.IsFocusLayer(cat, floor, SurfaceFocus.Floors));
        Assert.False(SurfacePaint.IsFocusLayer(cat, wall, SurfaceFocus.Floors));

        Assert.True(SurfacePaint.IsFocusLayer(cat, wall, SurfaceFocus.Walls));
        Assert.False(SurfacePaint.IsFocusLayer(cat, floor, SurfaceFocus.Walls));

        // a fixture is never the subject, whatever the focus
        foreach (var f in Enum.GetValues<SurfaceFocus>())
            Assert.False(SurfacePaint.IsFocusLayer(cat, bed, f));
    }

    [Fact]
    public void Solid_and_an_unset_second_brush_always_paint_the_primary()
    {
        foreach (var (x, y) in new[] { (0, 0), (1, 0), (0, 1), (7, 3) })
        {
            Assert.Equal("A", SurfacePaint.DefAt(SurfacePattern.Solid, "A", "B", x, y));
            Assert.Equal("A", SurfacePaint.DefAt(SurfacePattern.Checker, "A", null, x, y));
            Assert.Equal("A", SurfacePaint.DefAt(SurfacePattern.Checker, "A", "", x, y));
        }
    }

    [Fact]
    public void Checker_alternates_on_both_axes()
    {
        static string At(int x, int y) => SurfacePaint.DefAt(SurfacePattern.Checker, "A", "B", x, y);
        Assert.Equal("A", At(0, 0));
        Assert.Equal("B", At(1, 0));
        Assert.Equal("B", At(0, 1));
        Assert.Equal("A", At(1, 1));
        Assert.Equal("A", At(2, 4));
        Assert.Equal("B", At(3, 4));
    }

    [Fact]
    public void Checker_stays_continuous_across_negative_coordinates()
    {
        // A ship built left of the origin must not see the pattern mirror about x = 0.
        static string At(int x, int y) => SurfacePaint.DefAt(SurfacePattern.Checker, "A", "B", x, y);
        Assert.Equal("B", At(-1, 0));
        Assert.Equal("A", At(-2, 0));
        Assert.Equal("A", At(-1, -1));
        Assert.Equal("B", At(-1, -2));
    }

    [Fact]
    public void Checker_parity_survives_a_symmetry_mirror()
    {
        // A mirror is x' = 2c - x, and 2c is even, so parity is preserved: painting under active
        // symmetry produces one continuous checkerboard rather than a seam down the axis.
        static string At(int x, int y) => SurfacePaint.DefAt(SurfacePattern.Checker, "A", "B", x, y);
        foreach (var c in new[] { 0, 3, -4 })
            for (var x = -3; x <= 3; x++)
                Assert.Equal(At(x, 1), At(2 * c - x, 1));
    }

    [Fact]
    public void Stripes_run_along_one_axis_each()
    {
        static string H(int x, int y) => SurfacePaint.DefAt(SurfacePattern.StripesH, "A", "B", x, y);
        static string V(int x, int y) => SurfacePaint.DefAt(SurfacePattern.StripesV, "A", "B", x, y);

        // horizontal bands: constant along x, alternating down y
        Assert.Equal("A", H(0, 0));
        Assert.Equal("A", H(9, 0));
        Assert.Equal("B", H(0, 1));
        Assert.Equal("B", H(9, 1));

        // vertical bands: the transpose
        Assert.Equal("A", V(0, 0));
        Assert.Equal("A", V(0, 9));
        Assert.Equal("B", V(1, 0));
        Assert.Equal("B", V(1, 9));
    }
}
