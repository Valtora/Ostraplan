using System.IO;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>Assertions against the live install; every test no-ops on a machine without the game.</summary>
public class GameDataTests
{
    [Fact]
    public void Install_located_and_catalog_builds()
    {
        if (TestData.Game is not { } g) return;
        Assert.NotNull(g.Env.InstalledVersion);
        // core alone has 331 unique menu install defs (154 of them HULL); mods only add
        Assert.True(g.Catalog.Parts.Count >= 300, $"only {g.Catalog.Parts.Count} buildable parts found");
        Assert.True(g.Catalog.Parts.Count(p => p.Category == "HULL") >= 140, "HULL tab suspiciously small");
        foreach (var category in Catalog.Categories)
            Assert.Contains(g.Catalog.Parts, p => p.Category == category);

        // new-document seeding and the problems scan depend on the buildable docking port
        Assert.True(g.Catalog.ByDefName.TryGetValue("ItmDockSys03Closed", out var dock), "buildable docksys missing");
        Assert.True(ProblemScan.IsDocksys(dock, g.Catalog), "docksys part not recognized (ALL-reqs trigger match)");
        Assert.True(g.Catalog.ByDefName.TryGetValue("ItmFloorGrate01", out var floorPart)
                    && !ProblemScan.IsDocksys(floorPart, g.Catalog),
            "floor grate wrongly recognized as a docking port (the Any-match bug)");
        Assert.True(dock!.MapPoints.ContainsKey("DockA") && dock.MapPoints.ContainsKey("DockB"),
            "DockA/DockB map points missing from the docksys condowner");

        // seeded pose: rot 0 at origin -> mating face along doc y = 0, outward up
        Assert.True(ProblemScan.TryGetFace(dock, new Placement { DefName = dock.DefName, X = 0, Y = 0 },
            out var axisY, out var dir, out var face));
        Assert.True(axisY);
        Assert.Equal(-1, dir);
        Assert.Equal(0, face, 3);

        // the Primary Airlock: resolvable for documents, absent from the palette, locked in docs
        Assert.True(g.Catalog.ByDefName.TryGetValue(Catalog.PrimaryDocksysDef, out var primary), "primary airlock unresolved");
        Assert.DoesNotContain(g.Catalog.Parts, p => p.DefName == Catalog.PrimaryDocksysDef);
        Assert.Contains("Primary", primary!.Friendly);
        Assert.True(ProblemScan.IsDocksys(primary, g.Catalog));
        Assert.True(primary.MapPoints.ContainsKey("DockA") && primary.MapPoints.ContainsKey("DockB"));
        Assert.Equal(7, primary.Item.Width);
        var doc = new ShipDocument(g.Catalog);
        Assert.True(doc.IsLocked(new Placement { DefName = Catalog.PrimaryDocksysDef }));
        Assert.False(doc.IsLocked(new Placement { DefName = "ItmDockSys03Closed" }));
    }

    [Fact]
    public void Known_parts_have_game_exact_footprints_and_sprites()
    {
        if (TestData.Game is not { } g) return;

        Assert.True(g.Catalog.ByDefName.TryGetValue("ItmWall1x1", out var wall), "wall not in palette");
        Assert.True(wall!.Item.HasSpriteSheet);
        Assert.Equal("TIsWall", wall.Item.CtSpriteSheet);
        Assert.Equal((1, 1), (wall.Item.Width, wall.Item.Height));
        Assert.True(wall.SpriteAbs is not null && File.Exists(wall.SpriteAbs), "wall sheet png missing");

        Assert.True(g.Catalog.ByDefName.TryGetValue("ItmFloorGrate01", out var floor), "floor grate not in palette");
        Assert.Equal((1, 1), (floor!.Item.Width, floor.Item.Height));
        Assert.Equal("HULL", floor.Category);
        Assert.True(floor.SpriteAbs is not null && File.Exists(floor.SpriteAbs));

        // footprints straight from the items data (independent of palette membership)
        var door = ItemDef.Parse(g.Index.Type("items")["ItmDoor01Closed"].El);
        Assert.Equal((5, 1), (door.Width, door.Height));
        var bed = ItemDef.Parse(g.Index.Type("items")["ItmBed01"].El);
        Assert.Equal(3, bed.Width);
        Assert.Equal(5, bed.Height);
    }

    [Fact]
    public void Wall_socket_loot_expands_to_IsWall_and_trigger_reads_it()
    {
        if (TestData.Game is not { } g) return;
        Assert.True(g.Catalog.Loots.TryGetValue("TILWallAdds", out var loot), "TILWallAdds loot missing");
        Assert.Contains("IsWall", loot!.Conds);
        Assert.True(g.Catalog.Triggers.TryGetValue("TIsWall", out var ct), "TIsWall trigger missing");
        Assert.Contains("IsWall", ct!.Reqs);
    }

    [Fact]
    public void Wall_autotiling_sees_neighbors_including_door_frames()
    {
        if (TestData.Game is not { } g) return;
        var doc = new ShipDocument(g.Catalog);
        void Place(string def, int x, int y) =>
            new PlaceCommand(new Placement { DefName = def, X = x, Y = y }).Do(doc);

        Place("ItmWall1x1", 0, 0);
        Place("ItmWall1x1", 1, 0);
        Place("ItmWall1x1", 0, 1);
        // corner wall at (0,0): east + south neighbors -> mask E|S = 3
        Assert.Equal(3, Autotile.MaskAt(doc.Conds, "TIsWall", 0, 0));

        if (g.Catalog.ByDefName.ContainsKey("ItmDoor01Open"))
        {
            Place("ItmDoor01Open", 2, 5);   // covers (2..6, 5); its end cells add wall conds
            Place("ItmWall1x1", 1, 5);
            Assert.Equal(2, Autotile.MaskAt(doc.Conds, "TIsWall", 1, 5));   // east only
        }
    }

    [Fact]
    public void Real_wall_is_free_standing_but_will_not_stack()
    {
        if (TestData.Game is not { } g) return;
        var wall = g.Catalog.ByDefName["ItmWall1x1"];
        var doc = new ShipDocument(g.Catalog);

        Assert.True(CheckFit.Check(doc, wall, 8, 8, 0, includeEnvelope: false).Ok);   // no reqs -> free-standing
        new PlaceCommand(new Placement { DefName = "ItmWall1x1", X = 8, Y = 8 }).Do(doc);

        var stacked = CheckFit.Check(doc, wall, 8, 8, 0, includeEnvelope: false);     // onto its own obstruction
        Assert.False(stacked.Ok);
        Assert.Equal("tile is already occupied", stacked.Reason);
        Assert.Contains((8, 8), stacked.FailedCells);
    }

    [Fact]
    public void Real_bed_requires_a_sealed_floor_beneath_and_a_wall_at_its_head()
    {
        if (TestData.Game is not { } g) return;
        if (!g.Catalog.ByDefName.TryGetValue("ItmBed01Off", out var bed)) return;
        if (!g.Catalog.ByDefName.ContainsKey("ItmFloorGrate01") || !g.Catalog.ByDefName.ContainsKey("ItmWall1x1")) return;

        var doc = new ShipDocument(g.Catalog);
        void Place(string def, int x, int y) => new PlaceCommand(new Placement { DefName = def, X = x, Y = y }).Do(doc);

        // ItmBed01Off is 3x5: reqs are TILFloor across the footprint + TILWall down the right (head) border.
        // bare space -> the floor requirement can't be met
        var bare = CheckFit.Check(doc, bed, 20, 20, 0, includeEnvelope: false);
        Assert.False(bare.Ok);
        Assert.Equal("needs a sealed floor beneath", bare.Reason);

        // sealed floor (grate adds IsFloor+IsFloorSealed) under the whole footprint, but no headboard wall yet
        for (var y = 20; y < 25; y++)
            for (var x = 20; x < 23; x++)
                Place("ItmFloorGrate01", x, y);
        var noWall = CheckFit.Check(doc, bed, 20, 20, 0, includeEnvelope: false);
        Assert.False(noWall.Ok);
        Assert.Equal("needs a wall alongside", noWall.Reason);

        // wall column at the bed's right edge (x = 23) -> the whole ring is now satisfied
        for (var y = 20; y < 25; y++) Place("ItmWall1x1", 23, y);
        Assert.True(CheckFit.Check(doc, bed, 20, 20, 0, includeEnvelope: false).Ok);
    }

    [Fact]
    public void Real_envelope_makes_construction_beyond_the_airlock_unplaceable()
    {
        if (TestData.Game is not { } g) return;
        if (!g.Catalog.ByDefName.TryGetValue("ItmDockSys03Closed", out var dock)) return;
        var wall = g.Catalog.ByDefName["ItmWall1x1"];
        var doc = new ShipDocument(g.Catalog);
        new PlaceCommand(new Placement { DefName = "ItmDockSys03Closed", X = 0, Y = 0 }).Do(doc);   // face at doc y = 0, outward up

        var beyond = CheckFit.Check(doc, wall, 2, -2, 0, includeEnvelope: true);
        Assert.False(beyond.Ok);
        Assert.Equal("beyond the airlock's mating face", beyond.Reason);

        Assert.True(CheckFit.Check(doc, wall, 2, 5, 0, includeEnvelope: true).Ok);   // inside the hull
    }

    [Fact]
    public void Large_tank_footprint_is_the_7x7_socket_grid_not_the_3x3_sprite()
    {
        if (TestData.Game is not { } g) return;
        var tank = ItemDef.Parse(g.Index.Type("items")["ItmCanisterLH02"].El);

        // The footprint IS 7x7 (nCols=7, 49 adds): the game requires a 7x7 sealed-floor
        // area to place it, so the placement/CheckFit footprint must stay 7x7 or Ostraplan
        // would allow tanks the game refuses. The visible tank is only the center 3x3
        // (TIL2DeckAdds); the outer ring is TILSubfloorAdds — abstracted storage under the floor.
        Assert.Equal((7, 7), (tank.Width, tank.Height));
        Assert.Equal(49, tank.SocketAdds.Length);
        Assert.Contains("TILSubfloorAdds", tank.SocketAdds);
        Assert.Contains("TIL2DeckAdds", tank.SocketAdds);
        Assert.All(tank.SocketReqs.Where(r => r != "Blank"), r => Assert.Equal("TILFloor", r));

        // the ghost distinguishes the two footprints: the outer TILSubfloorAdds ring is
        // under-floor storage, the center TIL2DeckAdds body is above-floor (an obstruction)
        Assert.True(g.Catalog.IsUnderFloorLoot("TILSubfloorAdds"));
        Assert.False(g.Catalog.IsUnderFloorLoot("TIL2DeckAdds"));
    }
}
