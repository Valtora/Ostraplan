using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// Building on / reaching over a FLOOR FIXTURE. Under-floor storage bins and racks (ItmRackUnder01,
/// ItmStorageBinFloor…) provide a walkable sealed-floor surface that tags its tiles IsFloorSealed + IsFixture
/// (never IsObstruction). The common TILObstruction forbid mask lists IsFixture, so a fixture placed on — or
/// whose access tile falls on — such a floor used to false-flag as "already occupied", even though the game
/// allows it. CheckFit now treats a sealed floor as a valid base (IsFixture doesn't block there; a real
/// IsObstruction still does).
/// </summary>
public class FloorFixtureTests
{
    [SkippableFact]
    public void A_fixture_fits_on_a_floor_fixtures_sealed_floor()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(g.Catalog.ByDefName.ContainsKey("ItmRackUnder01") && g.Catalog.ByDefName.ContainsKey("ItmRack1x201"),
            "ItmRackUnder01 / ItmRack1x201 not in this install");

        var doc = new ShipDocument(g.Catalog);
        new PlaceCommand(new Placement { DefName = "ItmRackUnder01", X = 0, Y = 0 }).Do(doc);

        // The under-rack's central 2×3 (cols 1–2, rows 1–3) is IsFloorSealed + IsFixture; the outer ring is
        // IsSubTile. A 1×2 rack whose body+right-access (a 2×2 block) lands on that central floor must fit.
        Assert.True(doc.Conds.At(1, 1)?.ContainsKey("IsFloorSealed") == true, "central tile should be sealed floor");
        Assert.True(doc.Conds.At(1, 1)?.ContainsKey("IsFixture") == true, "central tile should be a floor fixture");

        var rack = g.Catalog.Lookup("ItmRack1x201")!;
        var fit = CheckFit.Check(doc, rack, 1, 1, 0);
        Assert.True(fit.Ok, "a rack should fit on the under-rack's sealed floor; reason=" + fit.Reason);
    }

    [SkippableFact]
    public void A_real_obstruction_still_blocks()
    {
        // guard the relaxation: an IsObstruction fixture (a normal appliance/wall) must still refuse placement,
        // even though it is also IsFixture. Only sealed FLOOR fixtures are exempt.
        var g = TestData.RequireGame();
        Skip.IfNot(g.Catalog.ByDefName.ContainsKey("ItmWall1x1") && g.Catalog.ByDefName.ContainsKey("ItmRack1x201"),
            "ItmWall1x1 / ItmRack1x201 not in this install");

        var doc = new ShipDocument(g.Catalog);
        for (var y = 0; y < 4; y++)
            for (var x = 0; x < 3; x++)
                new PlaceCommand(new Placement { DefName = "ItmFloorGrate01", X = x, Y = y }).Do(doc);
        new PlaceCommand(new Placement { DefName = "ItmWall1x1", X = 1, Y = 1 }).Do(doc);   // an obstruction on the floor

        var rack = g.Catalog.Lookup("ItmRack1x201")!;
        var fit = CheckFit.Check(doc, rack, 1, 1, 0);
        Assert.False(fit.Ok, "a rack must not fit on top of a wall (IsObstruction), sealed floor or not");
    }
}
