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
}
