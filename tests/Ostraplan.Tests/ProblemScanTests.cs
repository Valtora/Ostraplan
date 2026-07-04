using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

public class ProblemScanTests
{
    // geometry mirrors the real ItmDockSys03Closed: 7x2, DockA (0,8)px / DockB (0,24)px,
    // i.e. an outward arrow through the top edge; face line = the footprint's top edge
    private static PartDef Docksys() => new(
        "Dock", "Dock", "HULL", "core",
        new ItemDef("Dock", "", false, null, 0, 7, [.. Enumerable.Repeat("L", 14)], [], []),
        null, [], [],
        ["IsDockSys", "IsInstalled"],
        new Dictionary<string, (double, double)> { ["DockA"] = (0, 8), ["DockB"] = (0, 24) });

    private static PartDef Floor() => new(
        "Floor", "Floor", "HULL", "core",
        new ItemDef("Floor", "", false, null, 0, 1, ["L"], [], []),
        null, [], [],
        ["IsInstalled"],   // installed, but NOT a docking port
        new Dictionary<string, (double, double)>());

    private static Catalog Cat() => new()
    {
        Parts = [],
        ByDefName = new[] { Docksys(), Floor() }.ToDictionary(p => p.DefName),
        Loots = new Dictionary<string, LootDef> { ["L"] = new("L", ["IsX"], []) },
        Triggers = new Dictionary<string, CondTriggerDef>
        {
            [ProblemScan.DocksysTrigger] = new(ProblemScan.DocksysTrigger, ["IsDockSys", "IsInstalled"], [], false),
        },
        Warnings = [],
    };

    private static ShipDocument Doc(Catalog cat, params Placement[] placements)
    {
        var doc = new ShipDocument(cat);
        foreach (var p in placements) new PlaceCommand(p).Do(doc);
        return doc;
    }

    [Fact]
    public void Empty_ship_reports_no_docking_port()
    {
        var problems = ProblemScan.Scan(Doc(Cat()), Cat());
        var problem = Assert.Single(problems);
        Assert.Equal(ProblemSeverity.Blocking, problem.Severity);
        Assert.Equal("No docking port", problem.Title);
    }

    [Fact]
    public void Installed_parts_are_not_mistaken_for_docking_ports()
    {
        // regression: matching ANY trigger req would hit IsInstalled on every part
        var cat = Cat();
        var problems = ProblemScan.Scan(Doc(cat, new Placement { DefName = "Floor", X = 0, Y = 0 }), cat);
        Assert.Contains(problems, p => p.Title == "No docking port");
        Assert.False(ProblemScan.IsDocksys(cat.ByDefName["Floor"], cat));
        Assert.True(ProblemScan.IsDocksys(cat.ByDefName["Dock"], cat));
    }

    [Fact]
    public void Clean_ship_with_port_has_no_problems()
    {
        var cat = Cat();
        var doc = Doc(cat,
            new Placement { DefName = "Dock", X = 0, Y = 0 },
            new Placement { DefName = "Floor", X = 2, Y = 3 });   // below the face - fine
        Assert.Empty(ProblemScan.Scan(doc, cat));
    }

    [Fact]
    public void Tiles_beyond_the_mating_face_are_blocking()
    {
        var cat = Cat();
        var doc = Doc(cat,
            new Placement { DefName = "Dock", X = 0, Y = 0 },     // face along doc y = 0, outward = up
            new Placement { DefName = "Floor", X = 2, Y = -1 });  // above the face
        var problem = Assert.Single(ProblemScan.Scan(doc, cat));
        Assert.Equal(ProblemSeverity.Blocking, problem.Severity);
        Assert.Equal("Construction beyond the airlock", problem.Title);
        Assert.Contains("(2,-1)", problem.Detail);
    }

    [Fact]
    public void Face_follows_rotation()
    {
        var cat = Cat();
        var port = new Placement { DefName = "Dock", X = 0, Y = 0, Rot = 90 };   // outward now +x, face at x = 2
        Assert.True(ProblemScan.TryGetFace(cat.ByDefName["Dock"], port, out var axisY, out var dir, out var face));
        Assert.False(axisY);
        Assert.Equal(1, dir);
        Assert.Equal(2, face, 3);

        var blocked = Doc(cat, port, new Placement { DefName = "Floor", X = 2, Y = 3 });
        Assert.Single(ProblemScan.Scan(blocked, cat));
        var clean = Doc(cat, new Placement { DefName = "Dock", X = 0, Y = 0, Rot = 90 }, new Placement { DefName = "Floor", X = -1, Y = 3 });
        Assert.Empty(ProblemScan.Scan(clean, cat));
    }

    [Fact]
    public void Unrotated_face_matches_the_game_bounds_math()
    {
        var cat = Cat();
        var port = new Placement { DefName = "Dock", X = 0, Y = 0 };
        Assert.True(ProblemScan.TryGetFace(cat.ByDefName["Dock"], port, out var axisY, out var dir, out var face));
        Assert.True(axisY);
        Assert.Equal(-1, dir);        // outward = up (toward negative doc y)
        Assert.Equal(0, face, 3);     // exactly the footprint's top edge
    }

    // ---- per-placement legality (P1) ----

    private const string B = "Blank";

    // 1x1 fixture that needs a sealed floor on its own tile (idx 4 of the 3x3 ring)
    private static PartDef Fixture() => new(
        "Fix", "Bunk", "FURN", "core",
        new ItemDef("Fix", "", false, null, 0, 1, ["FixAdds"],
            [B, B, B, B, "Floor", B, B, B, B], [B, B, B, B, B, B, B, B, B]),
        null, [], [], [], new Dictionary<string, (double, double)>());

    // 1x1 sealed floor: no requirements
    private static PartDef FloorTile() => new(
        "FloorTile", "Floor", "HULL", "core",
        new ItemDef("FloorTile", "", false, null, 0, 1, ["Floor"], [B, B, B, B, B, B, B, B, B], [B, B, B, B, B, B, B, B, B]),
        null, [], [], [], new Dictionary<string, (double, double)>());

    private static Catalog FixtureCat() => new()
    {
        Parts = [],
        ByDefName = new[] { Docksys(), Fixture(), FloorTile() }.ToDictionary(p => p.DefName),
        Loots = new Dictionary<string, LootDef>
        {
            ["Floor"] = new("Floor", ["IsFloor", "IsFloorSealed"], []),
            ["FixAdds"] = new("FixAdds", ["IsFixture", "IsObstruction"], []),
        },
        Triggers = new Dictionary<string, CondTriggerDef>
        {
            [ProblemScan.DocksysTrigger] = new(ProblemScan.DocksysTrigger, ["IsDockSys", "IsInstalled"], [], false),
        },
        Warnings = [],
    };

    [Fact]
    public void Illegal_placements_are_flagged_grouped_by_reason_with_their_cells()
    {
        var cat = FixtureCat();
        // two fixtures dropped on bare space, below the mating face so only the socket rule bites
        var doc = Doc(cat,
            new Placement { DefName = "Dock", X = 0, Y = 5 },
            new Placement { DefName = "Fix", X = 3, Y = 10 },
            new Placement { DefName = "Fix", X = 6, Y = 10 });

        var illegal = Assert.Single(ProblemScan.Scan(doc, cat), p => p.Title.Contains("floor"));
        Assert.Equal(ProblemSeverity.Blocking, illegal.Severity);
        Assert.Contains("2 part", illegal.Title);         // both share a reason -> one grouped entry
        Assert.NotNull(illegal.Cells);
        Assert.Contains((3, 10), illegal.Cells!);
        Assert.Contains((6, 10), illegal.Cells!);
    }

    [Fact]
    public void A_fixture_over_its_own_floor_is_legal_and_unflagged()
    {
        var cat = FixtureCat();
        // floor first, then the fixture on top of it -> legal, and self-exclusion keeps it that way
        var doc = Doc(cat,
            new Placement { DefName = "Dock", X = 0, Y = 5 },
            new Placement { DefName = "FloorTile", X = 3, Y = 10 },
            new Placement { DefName = "Fix", X = 3, Y = 10 });

        Assert.Empty(ProblemScan.Scan(doc, cat));   // no socket breach, envelope clear, and it is constructible
    }
}
