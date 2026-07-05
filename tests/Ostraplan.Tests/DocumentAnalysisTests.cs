using System.Linq;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>Exercises the live-document analysis path (ShipGrid.FromDocument + ShipAnalysis)
/// that the template-based parity tests don't cover. No-ops without the install.</summary>
public class DocumentAnalysisTests
{
    // a 5×5 wall ring around a 3×3 sealed-floor interior
    private static ShipDocument SealedRoom((GameEnv, DataIndex, Catalog) g, bool sealFully = true)
    {
        var (_, _, catalog) = g;
        var doc = new ShipDocument(catalog);
        void Place(string def, int x, int y) => new PlaceCommand(new Placement { DefName = def, X = x, Y = y }).Do(doc);

        for (var x = 0; x < 5; x++)
            for (var y = 0; y < 5; y++)
            {
                if (x is 0 or 4 || y is 0 or 4) Place("ItmWall1x1", x, y);   // perimeter wall
                else if (sealFully || (x, y) != (2, 2)) Place("ItmFloorGrate01", x, y);   // interior floor (optionally leave (2,2) open)
            }
        return doc;
    }

    [Fact]
    public void Sealed_interior_is_a_nonvoid_compartment_with_a_rating()
    {
        if (TestData.Game is not { } g) return;
        if (!g.Catalog.ByDefName.ContainsKey("ItmWall1x1") || !g.Catalog.ByDefName.ContainsKey("ItmFloorGrate01")) return;

        var doc = SealedRoom(g);
        var specs = RoomCertifier.LoadSpecs(g.Index);
        var report = ShipAnalysis.AnalyzeDocument(doc, g.Catalog, specs);

        // the 3×3 interior is one sealed (non-void) compartment; the exterior is a void/Outside room
        Assert.Contains(report.Rooms, r => !r.Void && r.TileCount == 9);
        Assert.Contains(report.Rooms, r => r.Outside);
        Assert.Empty(report.Leaks);                       // nothing leaks — the interior is sealed
        Assert.Equal("Small", report.Rating.Size);        // tiny footprint
        Assert.Equal("A", report.Rating.Condition);       // pristine
    }

    [Fact]
    public void An_unsealed_interior_tile_is_reported_as_a_leak()
    {
        if (TestData.Game is not { } g) return;
        if (!g.Catalog.ByDefName.ContainsKey("ItmWall1x1") || !g.Catalog.ByDefName.ContainsKey("ItmFloorGrate01")) return;

        var doc = SealedRoom(g, sealFully: false);         // interior (2,2) left without a sealed floor
        var specs = RoomCertifier.LoadSpecs(g.Index);
        var report = ShipAnalysis.AnalyzeDocument(doc, g.Catalog, specs);

        // the interior compartment is now void (its centre tile has no sealed floor) -> a leak
        var leak = Assert.Single(report.Leaks);
        Assert.NotEmpty(leak.LeakTiles);                   // the unsealed tile(s) are surfaced for the canvas highlight
    }
}
