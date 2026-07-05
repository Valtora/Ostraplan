using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>The tile→parts spatial index behind PlacementsAt/HitTest stays consistent through
/// add/move/remove and covers a multi-tile part's whole footprint. No-ops without the game.</summary>
public class DocumentIndexTests
{
    private const string Wall = "ItmWall1x1";

    [Fact]
    public void PlacementsAt_tracks_add_move_and_remove()
    {
        if (TestData.Game is not { } g || !g.Catalog.ByDefName.ContainsKey(Wall)) return;
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

    [Fact]
    public void HitTest_returns_the_topmost_covering_part()
    {
        if (TestData.Game is not { } g || !g.Catalog.ByDefName.ContainsKey(Wall)) return;
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

    [Fact]
    public void A_multitile_part_indexes_every_footprint_tile()
    {
        if (TestData.Game is not { } g) return;
        var multi = g.Catalog.Parts.FirstOrDefault(p => GridMath.Size(p.Item.Width, p.Item.Height, 0) is var (w, h) && (w > 1 || h > 1));
        if (multi is null) return;

        var doc = new ShipDocument(g.Catalog);
        var p = new Placement { DefName = multi.DefName, X = 0, Y = 0 };
        new PlaceCommand(p).Do(doc);
        var (fw, fh) = doc.FootprintOf(p);

        for (var r = 0; r < fh; r++)
            for (var c = 0; c < fw; c++)
                Assert.Contains(p, doc.PlacementsAt(c, r));
        Assert.Empty(doc.PlacementsAt(fw, fh));   // just outside the footprint
    }
}
