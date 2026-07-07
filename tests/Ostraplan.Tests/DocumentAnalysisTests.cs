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

    [SkippableFact]
    public void Sealed_interior_is_a_nonvoid_compartment_with_no_breaches()
    {
        var g = TestData.RequireGame();
        if (!Ready(g)) return;
        var report = Analyse(g, Mode.Sealed);

        Assert.Contains(report.Rooms, r => !r.Void && r.TileCount == 9);   // the enclosed 3×3
        Assert.Contains(report.Rooms, r => r.Outside);                     // the exterior
        Assert.Empty(report.Breaches);                                     // fully sealed
        Assert.Equal("Small", report.Rating.Size);
        Assert.Equal("A", report.Rating.Condition);
    }

    [SkippableFact]
    public void A_floor_hole_in_an_enclosed_room_is_an_unsealed_breach()
    {
        var g = TestData.RequireGame();
        if (!Ready(g)) return;
        var breach = Assert.Single(Analyse(g, Mode.FloorHole).Breaches);
        Assert.False(breach.OpenToSpace);                 // enclosed, just missing floor
        Assert.Contains((2, 2), breach.Tiles);            // the hole is surfaced for the canvas highlight
    }

    [SkippableFact]
    public void A_wall_gap_is_pinpointed_to_the_missing_wall_not_the_whole_room()
    {
        var g = TestData.RequireGame();
        if (!Ready(g)) return;
        var breach = Assert.Single(Analyse(g, Mode.WallGap).Breaches);
        Assert.True(breach.OpenToSpace);                     // the interior escaped through the gap
        Assert.Equal(9, breach.ExposedFloorCount);           // the whole 3×3 interior vents to space
        Assert.Equal((0, 2), Assert.Single(breach.Tiles));   // highlight is ONLY the one missing-wall tile
    }

    [SkippableFact]
    public void Floor_with_no_walls_highlights_the_perimeter_where_walls_are_missing()
    {
        var g = TestData.RequireGame();
        if (!Ready(g)) return;
        var report = Analyse(g, Mode.FloorOnly);
        var breach = Assert.Single(report.Breaches);
        Assert.True(breach.OpenToSpace);
        Assert.Equal(9, breach.ExposedFloorCount);           // every floor tile vents — nothing is enclosed
        Assert.Equal(12, breach.Tiles.Count);                // the ring of missing-wall positions round the 3×3
        Assert.DoesNotContain(report.Rooms, r => !r.Void);   // there is no sealed compartment at all
    }

    // A full-width 5×1 door (wall-wall-portal-wall-wall) dividing a 5×7 hull into a top
    // and bottom room. Proves the user's multi-compartment case: an OPEN door already
    // partitions the hull into two sealed rooms (its IsWall cells seal each side), and
    // closing it changes nothing — the game rooms an open and a closed door identically.
    // Floor runs under the whole interior INCLUDING the doorway row: the door's centre
    // portal tile is IsPortal-not-IsWall, so it sinks into a room and would mark it Void
    // if it had no sealed floor beneath (exactly as the game's CreateRooms floods it).
    private static ShipDocument BuildDoored(Catalog catalog, string doorDef)
    {
        var doc = new ShipDocument(catalog);
        void Place(string def, int x, int y) => new PlaceCommand(new Placement { DefName = def, X = x, Y = y }).Do(doc);
        for (var x = 0; x < 5; x++) { Place("ItmWall1x1", x, 0); Place("ItmWall1x1", x, 6); }   // top & bottom hull
        for (var y = 1; y <= 5; y++)
        {
            Place("ItmWall1x1", 0, y); Place("ItmWall1x1", 4, y);                                // side hull
            for (var x = 1; x < 4; x++) Place("ItmFloorGrate01", x, y);                          // interior floor, incl. under the doorway
        }
        Place(doorDef, 0, 3);   // the whole divider row: walls with a centre portal at (2,3), floored beneath
        return doc;
    }

    [SkippableFact]
    public void A_door_partitions_a_hull_into_two_sealed_rooms_open_or_closed()
    {
        var g = TestData.RequireGame();
        if (!Ready(g)) return;
        if (!g.Catalog.ByDefName.ContainsKey("ItmDoor01Open") || !g.Catalog.ByDefName.ContainsKey("ItmDoor01Closed")) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);

        var open = ShipAnalysis.AnalyzeDocument(BuildDoored(g.Catalog, "ItmDoor01Open"), g.Catalog, specs);
        Assert.Equal(2, open.Rooms.Count(r => !r.Void));   // the OPEN door already makes two sealed compartments
        Assert.Empty(open.Breaches);

        var closed = ShipAnalysis.AnalyzeDocument(BuildDoored(g.Catalog, "ItmDoor01Closed"), g.Catalog, specs);
        // closing the door is topology-invariant: same sealed-room count, same tile split, no breaches
        Assert.Equal(2, closed.Rooms.Count(r => !r.Void));
        Assert.Empty(closed.Breaches);
        Assert.Equal(
            open.Rooms.Where(r => !r.Void).Select(r => r.TileCount).OrderBy(n => n),
            closed.Rooms.Where(r => !r.Void).Select(r => r.TileCount).OrderBy(n => n));
    }
}
