using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// What may sit on a FLOOR FIXTURE. Under-floor storage bins and racks (ItmRackUnder01,
/// ItmStorageBinFloor…) provide a walkable sealed-floor surface that tags its tiles IsFloorSealed + IsFixture
/// (never IsObstruction). The game refuses to build on one: nothing goes on top of a sub-floor bin except
/// CEILING-LEVEL parts (conduits, overhead lights).
///
/// <para>That rule needs no special case, because the game data already draws the line — a conduit forbids only
/// TILPowerConduitOff and an overhead light only TILLight, so neither tests IsFixture, while a rack's
/// TILObstruction does. 0.8.0 through 0.44.x carried an exemption that let a sealed floor waive the IsFixture
/// forbid, which allowed the rack and (far worse) unroofed every part guarded by IsFixture alone. These tests
/// pin both halves so it cannot come back. See CheckFit's sub-floor-bin note.</para>
/// </summary>
public class FloorFixtureTests
{
    private const string Bin = "ItmRackUnder01";       // the under-floor rack: sealed floor + IsFixture, no obstruction
    private const string Rack = "ItmRack1x201";        // an ordinary fixture (forbids TILObstruction)
    private const string Conduit = "ItmConduit01";     // ceiling level (forbids TILPowerConduitOff)
    private const string CeilingLight = "ItmLitCeiling1x1";

    /// <summary>The premise the whole rule rests on: a sub-floor bin's surface is sealed floor carrying
    /// IsFixture but no IsObstruction. If the game data ever stops doing this, every test below is vacuous.</summary>
    [SkippableFact]
    public void A_subfloor_bin_surface_is_sealed_floor_carrying_a_fixture_but_no_obstruction()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(g.Catalog.Lookup(Bin) is not null, $"{Bin} not in this install");

        var doc = new ShipDocument(g.Catalog);
        new PlaceCommand(new Placement { DefName = Bin, X = 0, Y = 0 }).Do(doc);

        // the bin's central 2×3 (cols 1–2, rows 1–3) is the walkable surface; the outer ring is IsSubTile
        var at = doc.Conds.At(1, 1);
        Assert.True(at?.ContainsKey("IsFloorSealed") == true, "central tile should be sealed floor");
        Assert.True(at?.ContainsKey("IsFixture") == true, "central tile should be a floor fixture");
        Assert.False(at?.ContainsKey("IsObstruction") == true, "a floor fixture must not be an obstruction");
    }

    /// <summary>The regression this file exists for: the game refuses a fixture built on a sub-floor bin, and
    /// so must the Law. Passing this by waiving IsFixture on a sealed floor is what broke everything else.</summary>
    [SkippableFact]
    public void A_fixture_is_refused_on_a_subfloor_bins_sealed_floor()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(g.Catalog.Lookup(Bin) is not null && g.Catalog.Lookup(Rack) is not null,
            $"{Bin} / {Rack} not in this install");

        var doc = new ShipDocument(g.Catalog);
        new PlaceCommand(new Placement { DefName = Bin, X = 0, Y = 0 }).Do(doc);

        var fit = CheckFit.Check(doc, g.Catalog.Lookup(Rack)!, 1, 1, 0);
        Assert.False(fit.Ok, "a rack must not build on a sub-floor bin — the game refuses it");
    }

    /// <summary>The other half of the rule: ceiling-level parts DO go over a bin. They need no exemption to do
    /// it, because their forbid masks never test IsFixture — which is exactly why deleting the waiver was safe.</summary>
    [SkippableTheory]
    [InlineData(Conduit)]
    [InlineData(CeilingLight)]
    public void A_ceiling_level_part_still_fits_over_a_subfloor_bin(string def)
    {
        var g = TestData.RequireGame();
        Skip.IfNot(g.Catalog.Lookup(Bin) is not null && g.Catalog.Lookup(def) is not null,
            $"{Bin} / {def} not in this install");

        var part = g.Catalog.Lookup(def)!;
        Assert.DoesNotContain(
            part.Item.SocketForbids.Where(l => l is not (null or "" or "Blank")).SelectMany(g.Catalog.LootConds),
            c => c == "IsFixture");

        var doc = new ShipDocument(g.Catalog);
        new PlaceCommand(new Placement { DefName = Bin, X = 0, Y = 0 }).Do(doc);

        var fit = CheckFit.Check(doc, part, 1, 1, 0);
        Assert.True(fit.Ok, $"{def} is ceiling level and must still fit over a bin; reason=" + fit.Reason);
    }

    [SkippableFact]
    public void A_real_obstruction_still_blocks()
    {
        // an IsObstruction fixture (a normal appliance/wall) refuses placement, as it always did
        var g = TestData.RequireGame();
        Skip.IfNot(g.Catalog.Lookup("ItmWall1x1") is not null && g.Catalog.Lookup(Rack) is not null,
            $"ItmWall1x1 / {Rack} not in this install");

        var doc = new ShipDocument(g.Catalog);
        for (var y = 0; y < 4; y++)
            for (var x = 0; x < 3; x++)
                new PlaceCommand(new Placement { DefName = "ItmFloorGrate01", X = x, Y = y }).Do(doc);
        new PlaceCommand(new Placement { DefName = "ItmWall1x1", X = 1, Y = 1 }).Do(doc);   // an obstruction on the floor

        var fit = CheckFit.Check(doc, g.Catalog.Lookup(Rack)!, 1, 1, 0);
        Assert.False(fit.Ok, "a rack must not fit on top of a wall (IsObstruction)");
    }
}
