using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>The tile→parts spatial index behind PlacementsAt/HitTest stays consistent through
/// add/move/remove and covers a multi-tile part's whole footprint. No-ops without the game.</summary>
public class DocumentIndexTests
{
    private const string Wall = "ItmWall1x1";

    [SkippableFact]
    public void PlacementsAt_tracks_add_move_and_remove()
    {
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.ContainsKey(Wall)) return;
        var doc = new ShipDocument(g.Catalog);

        var p = new Placement { DefName = Wall, X = 3, Y = 4 };
        new PlaceCommand(p).Do(doc);
        Assert.Contains(p, doc.PlacementsAt(3, 4));

        new MoveCommand([p], 2, 0).Do(doc);
        Assert.Empty(doc.PlacementsAt(3, 4));          // vacated
        Assert.Contains(p, doc.PlacementsAt(5, 4));    // reindexed at the new pose

        new RemoveCommand([p]).Do(doc);
        Assert.Empty(doc.PlacementsAt(5, 4));
    }

    [SkippableFact]
    public void HitTest_returns_the_topmost_covering_part()
    {
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.ContainsKey(Wall)) return;
        var floor = g.Catalog.Parts.FirstOrDefault(p => g.Catalog.RenderLayer(p) == Catalog.LayerFloor && p.Item is { Width: 1, Height: 1 });
        if (floor is null) return;

        var doc = new ShipDocument(g.Catalog);
        var below = new Placement { DefName = floor.DefName, X = 0, Y = 0 };
        var above = new Placement { DefName = Wall, X = 0, Y = 0 };
        new PlaceCommand(below).Do(doc);
        new PlaceCommand(above).Do(doc);

        Assert.Equal(2, doc.PlacementsAt(0, 0).Count);
        Assert.Same(above, doc.HitTest(0, 0));          // wall sorts above floor
        Assert.Equal(above, doc.HitTestStack(0, 0)[0]); // topmost first
    }

    [SkippableFact]
    public void A_multitile_part_indexes_its_body_tiles()
    {
        var g = TestData.RequireGame();
        var multi = g.Catalog.Parts.FirstOrDefault(p => GridMath.Size(p.Item.Width, p.Item.Height, 0) is var (w, h) && (w > 1 || h > 1));
        if (multi is null) return;

        var doc = new ShipDocument(g.Catalog);
        var p = new Placement { DefName = multi.DefName, X = 0, Y = 0 };
        new PlaceCommand(p).Do(doc);
        var (bx, by, bw, bh) = doc.BodyBounds(p);   // the above-floor body (= whole footprint for ordinary parts)

        for (var r = 0; r < bh; r++)
            for (var c = 0; c < bw; c++)
                Assert.Contains(p, doc.PlacementsAt(bx + c, by + r));
        Assert.Empty(doc.PlacementsAt(bx + bw, by + bh));   // just outside the body
    }

    [SkippableFact]
    public void A_tank_is_selectable_on_its_above_floor_body_only()
    {
        var g = TestData.RequireGame();
        // a large fuel tank: 7×7 socket with an under-floor (IsSubTile-only) ring
        var tank = g.Catalog.Parts.FirstOrDefault(p =>
            p.Item is { Width: 7, Height: 7 } && p.Item.SocketAdds.Any(g.Catalog.IsUnderFloorLoot));
        if (tank is null) return;

        var doc = new ShipDocument(g.Catalog);
        var p = new Placement { DefName = tank.DefName, X = 0, Y = 0 };
        new PlaceCommand(p).Do(doc);

        Assert.Equal((2, 2, 3, 3), doc.BodyBounds(p));   // the visible body is the centred 3×3, not the 7×7 socket
        Assert.True(doc.Covers(p, 3, 3));                // centre tile is the tank
        Assert.False(doc.Covers(p, 0, 0));               // the under-floor ring corner is not "the tank"
        Assert.Same(p, doc.HitTest(3, 3));               // clicking the body selects it
        Assert.Null(doc.HitTest(0, 0));                  // clicking the ring hits nothing but abstracted sub-floor
    }
}
