using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>Multi-part commands coalesce their Changed notifications so the (heavy) problem
/// scan and repaint run once per undo step, not once per part. No-ops without the game.</summary>
public class CommandBatchTests
{
    [SkippableFact]
    public void A_composite_fires_changed_once_not_per_part()
    {
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.ContainsKey("ItmWall1x1")) return;

        var doc = new ShipDocument(g.Catalog);
        var count = 0;
        doc.Changed += () => count++;

        Placement Wall(int x, int y) => new() { DefName = "ItmWall1x1", X = x, Y = y };

        // two bare placements: two notifications
        new PlaceCommand(Wall(0, 0)).Do(doc);
        new PlaceCommand(Wall(1, 0)).Do(doc);
        Assert.Equal(2, count);

        // a composite of three: exactly one notification for the whole step
        count = 0;
        var composite = new CompositeCommand(
        [
            new PlaceCommand(Wall(0, 1)),
            new PlaceCommand(Wall(1, 1)),
            new PlaceCommand(Wall(2, 1)),
        ]);
        composite.Do(doc);
        Assert.Equal(1, count);

        // and one on undo
        count = 0;
        composite.Undo(doc);
        Assert.Equal(1, count);
    }
}
