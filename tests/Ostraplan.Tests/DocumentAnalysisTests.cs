using System.Linq;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>Exercises the live-document analysis path (ShipGrid.FromDocument + ShipAnalysis)
/// and, in particular, the airtightness model — a compartment is sealed only when it is
/// enclosed by walls AND fully floored. No-ops without the install.</summary>
public class DocumentAnalysisTests
{
    private enum Mode { Sealed, FloorHole, WallGap, FloorOnly }

    // a 5×5 wall ring around a 3×3 floor interior, with a controllable defect
    private static ShipDocument Build((GameEnv, DataIndex, Catalog) g, Mode mode)
    {
        var (_, _, catalog) = g;
        var doc = new ShipDocument(catalog);
        void Place(string def, int x, int y) => new PlaceCommand(new Placement { DefName = def, X = x, Y = y }).Do(doc);

        for (var x = 0; x < 5; x++)
            for (var y = 0; y < 5; y++)
            {
                var perimeter = x is 0 or 4 || y is 0 or 4;
                if (perimeter)
                {
                    if (mode == Mode.FloorOnly) continue;               // no walls at all
                    if (mode == Mode.WallGap && (x, y) == (0, 2)) continue;  // one wall missing on the left
                    Place("ItmWall1x1", x, y);
                }
                else
                {
                    if (mode == Mode.FloorHole && (x, y) == (2, 2)) continue; // interior floor hole
                    Place("ItmFloorGrate01", x, y);
                }
            }
        return doc;
    }

    private static AnalysisReport Analyse((GameEnv Env, DataIndex Index, Catalog Catalog) g, Mode mode) =>
        ShipAnalysis.AnalyzeDocument(Build(g, mode), g.Catalog, RoomCertifier.LoadSpecs(g.Index));

    private static bool Ready((GameEnv, DataIndex, Catalog)? g) =>
        g is { } gg && gg.Item3.ByDefName.ContainsKey("ItmWall1x1") && gg.Item3.ByDefName.ContainsKey("ItmFloorGrate01");

    [Fact]
    public void Sealed_interior_is_a_nonvoid_compartment_with_no_breaches()
    {
        if (TestData.Game is not { } g || !Ready(g)) return;
        var report = Analyse(g, Mode.Sealed);

        Assert.Contains(report.Rooms, r => !r.Void && r.TileCount == 9);   // the enclosed 3×3
        Assert.Contains(report.Rooms, r => r.Outside);                     // the exterior
        Assert.Empty(report.Breaches);                                     // fully sealed
        Assert.Equal("Small", report.Rating.Size);
        Assert.Equal("A", report.Rating.Condition);
    }

    [Fact]
    public void A_floor_hole_in_an_enclosed_room_is_an_unsealed_breach()
    {
        if (TestData.Game is not { } g || !Ready(g)) return;
        var breach = Assert.Single(Analyse(g, Mode.FloorHole).Breaches);
        Assert.False(breach.OpenToSpace);                 // enclosed, just missing floor
        Assert.Contains((2, 2), breach.Tiles);            // the hole is surfaced for the canvas highlight
    }

    [Fact]
    public void A_wall_gap_makes_the_interior_open_to_space()
    {
        if (TestData.Game is not { } g || !Ready(g)) return;
        var breach = Assert.Single(Analyse(g, Mode.WallGap).Breaches);
        Assert.True(breach.OpenToSpace);                  // the interior escaped to the exterior through the gap
        Assert.Equal(9, breach.Tiles.Count);              // all nine interior floor tiles are exposed
    }

    [Fact]
    public void Floor_with_no_walls_is_entirely_open_to_space()
    {
        if (TestData.Game is not { } g || !Ready(g)) return;
        var report = Analyse(g, Mode.FloorOnly);
        var breach = Assert.Single(report.Breaches);
        Assert.True(breach.OpenToSpace);
        Assert.Equal(9, breach.Tiles.Count);              // every floor tile is exposed — nothing is enclosed
        Assert.DoesNotContain(report.Rooms, r => !r.Void);   // there is no sealed compartment at all
    }
}
