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
        new Dictionary<string, double>(),
        new Dictionary<string, (double, double)> { ["DockA"] = (0, 8), ["DockB"] = (0, 24) });

    // the buildable Secondary: same geometry, but IsTypeB, so Ship.AddCO files it behind every
    // non-TypeB port and it never becomes the bounding port while a Primary exists (BoundingPort).
    private static PartDef DocksysB() => new(
        "DockB", "Secondary Airlock", "HULL", "core",
        new ItemDef("DockB", "", false, null, 0, 7, [.. Enumerable.Repeat("L", 14)], [], []),
        null, [], [],
        ["IsDockSys", "IsInstalled", ProblemScan.TypeBCond],
        new Dictionary<string, double>(),
        new Dictionary<string, (double, double)> { ["DockA"] = (0, 8), ["DockB"] = (0, 24) });

    private static PartDef Floor() => new(
        "Floor", "Floor", "HULL", "core",
        new ItemDef("Floor", "", false, null, 0, 1, ["L"], [], []),
        null, [], [],
        ["IsInstalled"],   // installed, but NOT a docking port
        new Dictionary<string, double>(),
        new Dictionary<string, (double, double)>());

    private static Catalog Cat() => new()
    {
        Parts = [],
        ByDefName = new[] { Docksys(), DocksysB(), Floor() }.ToDictionary(p => p.DefName),
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

    // ---- a Secondary's mating face (issue #29) ----
    //
    // The envelope only ever comes from the bounding port, so nothing stops a part landing ahead of a
    // Secondary. The game is the same, which is why this is advice rather than a block.

    // Primary at y 0 facing up, Secondary at y 20 rotated 180 so it faces DOWN (outward = +y, face at y 22).
    private static ShipDocument TwoPortDoc(Catalog cat, params Placement[] extra) => Doc(cat,
        [.. new[]
        {
            new Placement { DefName = "Dock", X = 0, Y = 0 },
            new Placement { DefName = "DockB", X = 0, Y = 20, Rot = 180 },
        }.Concat(extra)]);

    [Fact]
    public void A_part_ahead_of_a_secondary_face_is_a_dismissible_warning()
    {
        var cat = Cat();
        var doc = TwoPortDoc(cat, new Placement { DefName = "Floor", X = 3, Y = 22 });   // in the corridor, past the face
        var problem = Assert.Single(ProblemScan.Scan(doc, cat));
        Assert.Equal(ProblemSeverity.Warning, problem.Severity);          // never Blocking: the game allows it
        Assert.Equal(ProblemScan.BlockedPortAlertKey, problem.DismissKey);
        Assert.Contains("cannot dock", problem.Title);
        Assert.Contains((3, 22), problem.Cells!);
    }

    [Fact]
    public void The_bounding_port_is_not_reported_twice()
    {
        // A tile past the PRIMARY's face is the Blocking envelope breach and must not also surface as this warning.
        var cat = Cat();
        var doc = TwoPortDoc(cat, new Placement { DefName = "Floor", X = 3, Y = -1 });
        var problem = Assert.Single(ProblemScan.Scan(doc, cat));
        Assert.Equal("Construction beyond the airlock", problem.Title);
    }

    [Fact]
    public void Only_the_ports_own_width_counts_as_its_mating_corridor()
    {
        // Past the face but off to the side: a collar still has room, and flagging the whole half-plane would
        // condemn most of the ship whenever a Secondary faces inboard as an internal docking bay.
        var cat = Cat();
        Assert.Empty(ProblemScan.Scan(TwoPortDoc(cat, new Placement { DefName = "Floor", X = 9, Y = 22 }), cat));
        Assert.Empty(ProblemScan.Scan(TwoPortDoc(cat, new Placement { DefName = "Floor", X = -1, Y = 22 }), cat));
    }

    [Fact]
    public void Inboard_of_the_face_is_clean()
    {
        var cat = Cat();
        Assert.Empty(ProblemScan.Scan(TwoPortDoc(cat, new Placement { DefName = "Floor", X = 3, Y = 19 }), cat));
    }

    [Fact]
    public void Imported_structure_never_trips_the_warning()
    {
        // Same rule as the Blocking check: the game never re-validates existing hull.
        var cat = Cat();
        var doc = TwoPortDoc(cat, new Placement { DefName = "Floor", X = 3, Y = 22, IsGiven = true });
        Assert.Empty(ProblemScan.Scan(doc, cat));
    }

    // ---- per-placement legality (P1) ----

    private const string B = "Blank";

    // 1x1 fixture that needs a sealed floor on its own tile (idx 4 of the 3x3 ring)
    private static PartDef Fixture() => new(
        "Fix", "Bunk", "FURN", "core",
        new ItemDef("Fix", "", false, null, 0, 1, ["FixAdds"],
            [B, B, B, B, "Floor", B, B, B, B], [B, B, B, B, B, B, B, B, B]),
        null, [], [], [], new Dictionary<string, double>(), new Dictionary<string, (double, double)>());

    // 1x1 sealed floor: no requirements
    private static PartDef FloorTile() => new(
        "FloorTile", "Floor", "HULL", "core",
        new ItemDef("FloorTile", "", false, null, 0, 1, ["Floor"], [B, B, B, B, B, B, B, B, B], [B, B, B, B, B, B, B, B, B]),
        null, [], [], [], new Dictionary<string, double>(), new Dictionary<string, (double, double)>());

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
    public void Unsealed_compartment_warning_is_dismissible_and_carries_leak_cells()
    {
        var cat = FixtureCat();
        // a lone sealed-floor tile with no walls floods to open space -> an unsealed (open-to-space) compartment
        var doc = Doc(cat, new Placement { DefName = "FloorTile", X = 3, Y = 3 });

        var warn = Assert.Single(ProblemScan.Scan(doc, cat), p => p.DismissKey == ProblemScan.UnsealedAlertKey);
        Assert.Equal(ProblemSeverity.Warning, warn.Severity);
        Assert.Contains("unsealed compartment", warn.Title);
        Assert.NotNull(warn.Cells);
        Assert.NotEmpty(warn.Cells!);   // leak points are carried, so the sidebar can Show + focus them
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

        // no legality breach — envelope clear, self-excluded, constructible. (A lone floor with no
        // walls is legitimately "open to space", so an airtightness Warning is expected and allowed.)
        Assert.DoesNotContain(ProblemScan.Scan(doc, cat), p => p.Severity == ProblemSeverity.Blocking);
    }

    // A 1x1 part with its own-tile socket constrained (center of the 3x3 ring = index 4).
    private static PartDef P1(string name, string[] adds, string[] reqs, string[] forbids) => new(
        name, name, "HULL", "core",
        new ItemDef(name, "", false, null, 0, 1, adds, reqs, forbids),
        null, [], [], ["IsInstalled"], new Dictionary<string, double>(), new Dictionary<string, (double, double)>());

    // A ship with a wall (free-standing, forbids obstruction on its own tile) and a fixture that mounts
    // THROUGH a wall (reqs a wall on its own tile, forbids another fixture there) — the real air-pump/sensor shape.
    private static Catalog StackCat()
    {
        const string B = "Blank";
        var parts = new[]
        {
            Docksys(),
            P1("Wall", ["WallAdds"], [B, B, B, B, B, B, B, B, B], [B, B, B, B, "Obstruction", B, B, B, B]),
            P1("Fixture", ["FixtureAdds"], [B, B, B, B, "Wall", B, B, B, B], [B, B, B, B, "Fixture", B, B, B, B]),
        };
        return new Catalog
        {
            Parts = [],
            ByDefName = parts.ToDictionary(p => p.DefName),
            Loots = new Dictionary<string, LootDef>
            {
                ["WallAdds"] = new("WallAdds", ["IsWall", "IsObstruction"], []),
                ["FixtureAdds"] = new("FixtureAdds", ["IsFixture", "IsObstruction"], []),
                ["Obstruction"] = new("Obstruction", ["IsObstruction"], []),
                ["Wall"] = new("Wall", ["IsWall"], []),
                ["Fixture"] = new("Fixture", ["IsFixture"], []),
            },
            Triggers = new Dictionary<string, CondTriggerDef>
            {
                [ProblemScan.DocksysTrigger] = new(ProblemScan.DocksysTrigger, ["IsDockSys", "IsInstalled"], [], false),
            },
            Warnings = [],
        };
    }

    [Fact]
    public void A_fixture_mounted_through_a_new_wall_is_not_flagged_occupied()
    {
        // the reported false positive: a new wall + a new fixture that mounts through it, both authored. The
        // wall forbids obstruction on its own tile and the fixture adds it, so a FINAL-STATE check flags the
        // wall — but the game builds the wall first (no fixture yet), so it's legal. Build-order check must pass.
        var cat = StackCat();
        var doc = Doc(cat,
            new Placement { DefName = "Dock", X = 0, Y = 0 },
            new Placement { DefName = "Wall", X = 2, Y = 3 },       // below the airlock face
            new Placement { DefName = "Fixture", X = 2, Y = 3 });   // mounts through the wall on the same tile

        Assert.DoesNotContain(ProblemScan.Scan(doc, cat), p => p.Title.Contains("occupied"));
    }

    [Fact]
    public void Two_stacked_walls_are_still_flagged_occupied()
    {
        // regression the other way: the build-order check must still catch a genuine overlap — the second wall
        // can't be built onto the first (neither depends on the other, so there's no valid order).
        var cat = StackCat();
        var doc = Doc(cat,
            new Placement { DefName = "Dock", X = 0, Y = 0 },
            new Placement { DefName = "Wall", X = 2, Y = 3 },
            new Placement { DefName = "Wall", X = 2, Y = 3 });

        Assert.Contains(ProblemScan.Scan(doc, cat), p => p.Severity == ProblemSeverity.Blocking && p.Title.Contains("occupied"));
    }

    // ---- modded parts break the law as WARNINGS, not blocks (the "allow modded overrides" flip side) ----

    // A 1x1 fixture needing a sealed floor beneath (idx 4), authored by a given source (core or a mod).
    private static PartDef NeedsFloor(string name, string origin) => new(
        name, name, "FURN", origin,
        new ItemDef(name, "", false, null, 0, 1, ["FixAdds"], [B, B, B, B, "Floor", B, B, B, B], [B, B, B, B, B, B, B, B, B]),
        null, [], [], [], new Dictionary<string, double>(), new Dictionary<string, (double, double)>());

    private static Catalog ModCat(params PartDef[] extra) => new()
    {
        Parts = [],
        ByDefName = new[] { Docksys() }.Concat(extra).ToDictionary(p => p.DefName),
        Loots = new Dictionary<string, LootDef>
        {
            ["Floor"] = new("Floor", ["IsFloor", "IsFloorSealed"], []),
            ["FixAdds"] = new("FixAdds", ["IsFixture", "IsObstruction"], []),
            ["WallAdds"] = new("WallAdds", ["IsWall", "IsObstruction"], []),
            ["Wall"] = new("Wall", ["IsWall"], []),
        },
        Triggers = new Dictionary<string, CondTriggerDef>
        {
            [ProblemScan.DocksysTrigger] = new(ProblemScan.DocksysTrigger, ["IsDockSys", "IsInstalled"], [], false),
        },
        Warnings = [],
    };

    [Fact]
    public void IsModded_tracks_the_part_origin()
    {
        Assert.False(NeedsFloor("Core", "core").IsModded);
        Assert.True(NeedsFloor("Mod", "coolmod").IsModded);
    }

    [Fact]
    public void A_modded_illegal_placement_warns_while_the_core_one_blocks()
    {
        var cat = ModCat(NeedsFloor("Core", "core"), NeedsFloor("Mod", "coolmod"));
        // both dropped on bare space (no floor beneath), below the mating face so only the socket rule bites
        var doc = Doc(cat,
            new Placement { DefName = "Dock", X = 0, Y = 5 },
            new Placement { DefName = "Core", X = 3, Y = 10 },
            new Placement { DefName = "Mod", X = 6, Y = 10 });
        var problems = ProblemScan.Scan(doc, cat);

        // core: a hard Blocking problem (the Law is proven for core)
        Assert.Contains(problems, p => p.Severity == ProblemSeverity.Blocking && p.Title.Contains("floor") && !p.Title.Contains("modded"));
        // modded: a Warning that names it "modded" and points at the offending tile — never Blocking
        var warn = Assert.Single(problems, p => p.Title.Contains("modded"));
        Assert.Equal(ProblemSeverity.Warning, warn.Severity);
        Assert.Contains((6, 10), warn.Cells!);
        Assert.DoesNotContain(problems, p => p.Severity == ProblemSeverity.Blocking && p.Cells is not null && p.Cells.Contains((6, 10)));
    }

    [Fact]
    public void An_overridden_modded_part_is_trusted_so_parts_built_on_it_do_not_cascade_flag()
    {
        // a modded WALL placed illegally (needs a floor it doesn't have) still provides its IsWall to the tile, so a
        // core fixture mounted through it is supported. The modded wall warns; the core fixture is NOT flagged.
        var modWall = new PartDef("ModWall", "ModWall", "HULL", "coolmod",
            new ItemDef("ModWall", "", false, null, 0, 1, ["WallAdds"], [B, B, B, B, "Floor", B, B, B, B], [B, B, B, B, B, B, B, B, B]),
            null, [], [], [], new Dictionary<string, double>(), new Dictionary<string, (double, double)>());
        var fixture = new PartDef("Fixture", "Fixture", "FURN", "core",
            new ItemDef("Fixture", "", false, null, 0, 1, ["FixAdds"], [B, B, B, B, "Wall", B, B, B, B], [B, B, B, B, B, B, B, B, B]),
            null, [], [], [], new Dictionary<string, double>(), new Dictionary<string, (double, double)>());
        var cat = ModCat(modWall, fixture);
        var doc = Doc(cat,
            new Placement { DefName = "Dock", X = 0, Y = 5 },
            new Placement { DefName = "ModWall", X = 3, Y = 10 },    // modded, illegal (no floor) -> warns
            new Placement { DefName = "Fixture", X = 3, Y = 10 });   // core, mounts through the wall on the same tile

        var problems = ProblemScan.Scan(doc, cat);
        Assert.Contains(problems, p => p.Severity == ProblemSeverity.Warning && p.Title.Contains("modded"));
        // the core fixture sits on the trusted modded wall, so it must NOT be flagged "needs a wall"
        Assert.DoesNotContain(problems, p => p.Severity == ProblemSeverity.Blocking && p.Title.Contains("wall"));
    }
}
