using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>ShipDocument.Snapshot backs the off-thread problem scan: it must be an independent copy
/// (edits to the original don't touch it) that preserves poses and given-ness. No-ops without the game.</summary>
public class DocumentSnapshotTests
{
    private const string Wall = "ItmWall1x1";

    [Fact]
    public void Snapshot_is_independent_and_preserves_given_flag()
    {
        if (TestData.Game is not { } g || !g.Catalog.ByDefName.ContainsKey(Wall)) return;

        var doc = new ShipDocument(g.Catalog);
        var given = new Placement { DefName = Wall, X = 0, Y = 0, IsGiven = true };
        new PlaceCommand(given).Do(doc);
        new PlaceCommand(new Placement { DefName = Wall, X = 1, Y = 0 }).Do(doc);

        var snap = doc.Snapshot();
        Assert.Equal(2, snap.Placements.Count);
        Assert.Contains(snap.Placements, p => p is { X: 0, Y: 0, IsGiven: true });   // given-ness carried

        // mutating the original leaves the snapshot untouched
        new PlaceCommand(new Placement { DefName = Wall, X = 5, Y = 5 }).Do(doc);
        Assert.Equal(3, doc.Placements.Count);
        Assert.Equal(2, snap.Placements.Count);
    }

    [Fact]
    public void Snapshot_scans_the_same_as_the_original()
    {
        if (TestData.Game is not { } g || !g.Catalog.ByDefName.ContainsKey(Wall)) return;

        var doc = new ShipDocument(g.Catalog);
        for (var x = 0; x < 6; x++)
            for (var y = 0; y < 6; y++)
                new PlaceCommand(new Placement { DefName = Wall, X = x, Y = y }).Do(doc);

        var live = ProblemScan.Scan(doc, g.Catalog).Count;
        var snap = ProblemScan.Scan(doc.Snapshot(), g.Catalog).Count;
        Assert.Equal(live, snap);
    }
}
