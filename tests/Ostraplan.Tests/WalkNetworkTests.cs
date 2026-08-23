using System.Linq;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// Unit tests for <see cref="WalkNetwork"/> — the port of the game's <c>Tile.IsWalkable</c>, the jump-point-search
/// adjacency rule, and the range + sight gate <c>Interaction.Triggered</c> applies before a device can be used.
/// The synthetic cases pin the algorithm precisely; the real-data cases guard the interaction/door facts the port
/// is built on, so a game patch that moves them surfaces here rather than as a silently wrong overlay.
/// </summary>
public class WalkNetworkTests
{
    // A game-free catalog whose parts carry the real tile conditions the rule reads.
    private static Fixtures Base()
    {
        var f = new Fixtures();
        f.Floor();
        f.Wall();
        f.Part("DoorOpen", tileConds: ["IsPortal", "IsObstruction", "IsFloor", "IsFloorSealed"],
            startingConds: ["IsPortal"], category: "HULL");
        f.Part("DoorClosed", tileConds: ["IsPortal", "IsWall", "IsObstruction", "IsFloor", "IsFloorSealed"],
            startingConds: ["IsPortal"], category: "HULL");
        f.Part("DoorStuck", tileConds: ["IsPortal", "IsWall", "IsPortalStuck", "IsObstruction", "IsFloor", "IsFloorSealed"],
            startingConds: ["IsPortal"], category: "HULL");
        // a solid fixture: blocks (IsObstruction + IsFixture)
        f.Part("Bench", tileConds: ["IsFixture", "IsObstruction", "IsFloor", "IsFloorSealed"], category: "FURN");
        // an under-floor rack: walkable (no IsObstruction) but never a place to stand and work
        f.Part("Rack", tileConds: ["IsFixture", "IsFloor", "IsFloorSealed"], category: "FURN");
        return f;
    }

    private static WalkResult Analyze(Catalog cat, ShipDocument doc, WalkOptions? opts = null)
    {
        var grid = ShipGrid.FromDocument(doc, cat);
        return WalkNetwork.Build(grid, cat, opts, WalkNetwork.ForbiddenTiles(doc, grid));
    }

    /// <summary>A horizontal strip of floor from x0 to x1 inclusive at row y.</summary>
    private static Placement[] Strip(string def, int x0, int x1, int y) =>
        [.. Enumerable.Range(x0, x1 - x0 + 1).Select(x => Fixtures.P(def, x, y))];

    // ---- the walkable rule ----

    [Fact]
    public void Floor_is_walkable_and_a_wall_is_not()
    {
        var cat = Base().Build();
        var doc = Fixtures.Doc(cat, Fixtures.P("Floor", 0, 0), Fixtures.P("Wall", 1, 0));
        var grid = ShipGrid.FromDocument(doc, cat);

        var r = WalkNetwork.Build(grid, cat);

        Assert.True(r.Walkable[grid.Index(1, 1)]);    // the floor (doc (0,0) → grid (1,1), one tile of pad)
        Assert.False(r.Walkable[grid.Index(2, 1)]);   // the wall
    }

    [Fact]
    public void A_solid_fixture_blocks_but_a_floor_fixture_does_not()
    {
        var cat = Base().Build();
        var doc = Fixtures.Doc(cat, Fixtures.P("Bench", 0, 0), Fixtures.P("Rack", 1, 0));
        var grid = ShipGrid.FromDocument(doc, cat);

        var r = WalkNetwork.Build(grid, cat);

        Assert.False(r.Walkable[grid.Index(1, 1)]);   // IsObstruction + IsFixture
        Assert.True(r.Walkable[grid.Index(2, 1)]);    // IsFixture alone: walk over it
    }

    // ---- doors: state is NOT cosmetic to walking ----

    [Fact]
    public void An_open_door_joins_two_compartments()
    {
        var cat = Base().Build();
        var doc = Fixtures.Doc(cat, [.. Strip("Floor", 0, 1, 0), Fixtures.P("DoorOpen", 2, 0), .. Strip("Floor", 3, 4, 0)]);

        var r = Analyze(cat, doc);

        Assert.Single(r.Zones, z => z.TileCount > 1);
    }

    [Fact]
    public void A_powered_closed_door_still_joins_them()
    {
        // ItmDoor01ClosedOn adds plain TILPortalClosed (no IsPortalStuck): crew simply open it.
        var cat = Base().Build();
        var doc = Fixtures.Doc(cat, [.. Strip("Floor", 0, 1, 0), Fixtures.P("DoorClosed", 2, 0), .. Strip("Floor", 3, 4, 0)]);

        var r = Analyze(cat, doc);

        Assert.Single(r.Zones, z => z.TileCount > 1);
    }

    [Fact]
    public void A_stuck_door_seals_a_section_off()
    {
        // ItmDoor01Closed (unpowered), …ClosedOnLocked and …Dmg add TILPortalClosedStuck → IsPortalStuck.
        var cat = Base().Build();
        var doc = Fixtures.Doc(cat, [
            .. Strip("Floor", 0, 1, 0), Fixtures.P("DoorStuck", 2, 0), .. Strip("Floor", 3, 4, 0)]);

        var r = Analyze(cat, doc);

        Assert.Equal(2, r.Zones.Count(z => z.TileCount >= 2));
    }

    // ---- connectivity ----

    [Fact]
    public void A_diagonal_through_a_perfect_corner_does_not_connect()
    {
        // two floor tiles touching only at a corner, with both shared orthogonals walled:
        //   . W          the game's JumpPointSearch rejects a diagonal whose two behind-orthogonals
        //   W .          are both blocked, so these are separate zones
        var cat = Base().Build();
        var doc = Fixtures.Doc(cat,
            Fixtures.P("Floor", 0, 0), Fixtures.P("Wall", 1, 0),
            Fixtures.P("Wall", 0, 1), Fixtures.P("Floor", 1, 1));

        var r = Analyze(cat, doc);

        var floors = r.Zones.Where(z => z.TileCount == 1).ToList();
        Assert.Equal(2, floors.Count);
    }

    [Fact]
    public void A_diagonal_with_one_orthogonal_open_does_connect()
    {
        //   . .          one side of the corner is open floor, so the diagonal is legal
        //   W .
        var cat = Base().Build();
        var doc = Fixtures.Doc(cat,
            Fixtures.P("Floor", 0, 0), Fixtures.P("Floor", 1, 0),
            Fixtures.P("Wall", 0, 1), Fixtures.P("Floor", 1, 1));

        var r = Analyze(cat, doc);

        Assert.Single(r.Zones, z => z.TileCount == 3);
    }

    // ---- exterior / spacewalks ----

    [Fact]
    public void Two_pods_are_separate_inside_but_join_when_spacewalks_count()
    {
        var cat = Base().Build();
        var doc = Fixtures.Doc(cat, [.. Strip("Floor", 0, 1, 0), .. Strip("Floor", 6, 7, 0)]);

        var inside = Analyze(cat, doc);
        Assert.Equal(2, inside.Zones.Count);
        Assert.All(inside.Zones, z => Assert.False(z.Exterior));

        var eva = Analyze(cat, doc, new WalkOptions(IncludeExterior: true));
        var joined = Assert.Single(eva.Zones);
        Assert.True(joined.Exterior);
    }

    // ---- forbid zones ----

    [Fact]
    public void A_forbid_zone_cuts_a_corridor_when_respected()
    {
        var cat = Base().Build();
        var doc = Fixtures.Doc(cat, Strip("Floor", 0, 4, 0));
        var zone = new ShipZone { Name = "No", TileConds = [ShipZone.CondForbid], Tiles = [(2, 0)] };
        new CreateZoneCommand(zone).Do(doc);
        var grid = ShipGrid.FromDocument(doc, cat);
        var forbidden = WalkNetwork.ForbiddenTiles(doc, grid);

        Assert.Equal(2, WalkNetwork.Build(grid, cat, WalkOptions.Default, forbidden).Zones.Count);
        // the same design read for a crew member the zone does not bind
        Assert.Single(WalkNetwork.Build(grid, cat, new WalkOptions(RespectForbidZones: false), forbidden).Zones);
    }

    // ---- device reach ----

    private static Catalog ReachCatalog(double range)
    {
        var f = Base();
        f.Interaction("Use", "use", range, "Control Panel");
        // a console whose "use" point is one tile below its centre (48/16 px = 3 tiles is too far for a 1×1;
        // -16 px puts it on the tile directly below, the way a real cooler's use point sits off its body)
        f.Part("Console", tileConds: ["IsFixture", "IsObstruction", "IsFloor", "IsFloorSealed"],
            startingConds: ["IsInstalled"], category: "CTRL",
            mapPoints: new Dictionary<string, (double X, double Y)> { ["use"] = (0, -16) },
            interactions: ["Use"]);
        return f.Build();
    }

    [Fact]
    public void A_console_with_open_floor_at_its_use_point_is_reachable()
    {
        var cat = ReachCatalog(range: 0);
        var doc = Fixtures.Doc(cat, Fixtures.P("Console", 0, 0), Fixtures.P("Floor", 0, 1));

        var dev = Assert.Single(Analyze(cat, doc).Devices);

        Assert.True(dev.Reachable);
        Assert.Equal(WalkBlock.None, dev.Reason);
        Assert.Equal("Control Panel", dev.Action);
    }

    [Fact]
    public void A_console_whose_use_point_is_walled_over_is_unreachable()
    {
        var cat = ReachCatalog(range: 0);
        var doc = Fixtures.Doc(cat, Fixtures.P("Console", 0, 0), Fixtures.P("Wall", 0, 1));

        var dev = Assert.Single(Analyze(cat, doc).Devices);

        Assert.False(dev.Reachable);
        Assert.Equal(WalkBlock.NoStandingTile, dev.Reason);
    }

    [Fact]
    public void Range_lets_a_crew_member_stand_back_from_the_target_point()
    {
        // the use point itself is a solid fixture, so range 0 fails and range 2 succeeds off to the side
        var cat0 = ReachCatalog(range: 0);
        var doc0 = Fixtures.Doc(cat0, Fixtures.P("Console", 0, 0), Fixtures.P("Bench", 0, 1), Fixtures.P("Floor", 1, 1));
        Assert.False(Assert.Single(Analyze(cat0, doc0).Devices).Reachable);

        var cat2 = ReachCatalog(range: 2);
        var doc2 = Fixtures.Doc(cat2, Fixtures.P("Console", 0, 0), Fixtures.P("Bench", 0, 1), Fixtures.P("Floor", 1, 1));
        Assert.True(Assert.Single(Analyze(cat2, doc2).Devices).Reachable);
    }

    [Fact]
    public void A_floor_fixture_is_a_last_resort_to_stand_on_not_a_refusal()
    {
        // The game's destination choice is two-tier. GetClosestWalkableDestination would rather not park a working
        // crew member on an IsFixture tile — but when nothing else is in range, GetPath falls back to the target
        // tile itself and paths there, which succeeds for anything Tile.IsWalkable admits. Modelling only the
        // preference reports a cargo bay floored wall-to-wall in racks as entirely unusable.
        var cat = ReachCatalog(range: 1);

        // preference: a clean floor tile in range is what it stands on
        var clean = Fixtures.Doc(cat, Fixtures.P("Console", 0, 0), Fixtures.P("Rack", 0, 1), Fixtures.P("Floor", 1, 1));
        Assert.True(Assert.Single(Analyze(cat, clean).Devices).Reachable);

        // fallback: with only the rack, it is still usable — the rack is walkable
        var only = Fixtures.Doc(cat, Fixtures.P("Console", 0, 0), Fixtures.P("Rack", 0, 1));
        var onlyGrid = ShipGrid.FromDocument(only, cat);
        var onlyResult = WalkNetwork.Build(onlyGrid, cat);
        Assert.True(onlyGrid.Has(onlyGrid.Index(1, 2), "IsFixture"));   // the rack really is a fixture
        Assert.True(onlyResult.Walkable[onlyGrid.Index(1, 2)]);         // and really is walkable
        Assert.True(Assert.Single(onlyResult.Devices).Reachable);
    }

    [Fact]
    public void A_fractional_range_rounds_up_like_the_game()
    {
        // GetClosestWalkableDestination sizes its band with Mathf.CeilToInt(fRangeGoal), so a 1.5-tile interaction
        // reaches two tiles. Rounding down cost a whole ring of standing room on every fractional range.
        var cat = ReachCatalog(range: 1.5);
        // the use point is one below the console; the only floor is two tiles further on, i.e. 2 away
        var doc = Fixtures.Doc(cat,
            Fixtures.P("Console", 0, 0), Fixtures.P("Bench", 0, 1), Fixtures.P("Bench", 0, 2), Fixtures.P("Floor", 0, 3));

        Assert.True(Assert.Single(Analyze(cat, doc).Devices).Reachable);
    }

    [Fact]
    public void The_device_zone_is_the_zone_it_is_operated_from()
    {
        var cat = ReachCatalog(range: 0);
        var doc = Fixtures.Doc(cat, Fixtures.P("Console", 0, 0), Fixtures.P("Floor", 0, 1));
        var grid = ShipGrid.FromDocument(doc, cat);

        var r = WalkNetwork.Build(grid, cat);
        var dev = Assert.Single(r.Devices);

        Assert.Equal(r.TileZone[dev.TargetTile], dev.Zone);
    }

    [Fact]
    public void A_device_declaring_no_target_point_is_reached_from_beside_its_body()
    {
        // Wall lights and the like declare no "use" point at all, so GetPos falls back to the item centre — which
        // for a wall fitting is the wall. Taken literally with range 0 that demands standing inside the wall, which
        // no layout can satisfy, so the finding would be pure noise (44 of the core fleet's wall lights).
        var f = Base();
        f.Interaction("Switch", "use", 0);
        f.Part("WallLamp", tileConds: ["IsWall", "IsObstruction"],
            startingConds: ["IsInstalled"], category: "MISC", interactions: ["Switch"]);   // note: no mapPoints
        var cat = f.Build();
        var doc = Fixtures.Doc(cat, Fixtures.P("WallLamp", 0, 0), Fixtures.P("Floor", 0, 1));

        var dev = Assert.Single(Analyze(cat, doc).Devices);

        Assert.True(dev.Reachable);
        Assert.Equal(1.0, dev.Range);   // widened from the declared 0
    }

    [Fact]
    public void A_device_reports_every_tile_it_can_be_worked_from_not_merely_the_first()
    {
        // The verdict only ever needed one standable tile. The Access overlay needs all of them, because "which
        // side do you walk up to" is a question a single tile cannot answer.
        var f = Base();
        f.Interaction("Switch", "use", 1);
        f.Part("Console", tileConds: ["IsFixture", "IsObstruction", "IsFloor", "IsFloorSealed"],
            startingConds: ["IsInstalled"], category: "CTRL",
            mapPoints: new Dictionary<string, (double X, double Y)> { ["use"] = (0, 0) },
            interactions: ["Switch"]);
        var cat = f.Build();
        // Floor on one side of the console only, so the answer is a side rather than a ring.
        var doc = Fixtures.Doc(cat,
            Fixtures.P("Wall", -1, 0), Fixtures.P("Console", 0, 0), Fixtures.P("Wall", 1, 0),
            Fixtures.P("Wall", -1, -1), Fixtures.P("Wall", 0, -1), Fixtures.P("Wall", 1, -1),
            Fixtures.P("Floor", -1, 1), Fixtures.P("Floor", 0, 1), Fixtures.P("Floor", 1, 1));

        var dev = Assert.Single(Analyze(cat, doc).Devices);

        Assert.True(dev.Reachable);
        // Only the open row below, and all three of it. The console's own tile is a fixture, and the walls are not
        // standable at all, so a correct answer is exactly the floor strip.
        var grid = ShipGrid.FromDocument(doc, cat);
        var standing = dev.StandingTiles.Select(grid.GridToDoc).OrderBy(t => t.X).ToList();
        Assert.Equal([(-1, 1), (0, 1), (1, 1)], standing);
        // Nearest to the target point first, because that is the one the game settles on and so the one worth
        // marking. The console's target point is its own tile, so the tile directly below it wins.
        Assert.Equal((0, 1), grid.GridToDoc(dev.StandingTiles[0]));
    }

    [Fact]
    public void The_overlay_can_be_asked_about_any_tile_of_a_part_not_only_its_anchor()
    {
        // The user points at a sprite, not at an anchor, so a wide console has to answer from either end.
        var f = Base();
        f.Interaction("Switch", "use", 1);
        f.Part("Desk", w: 2, h: 1, tileConds: ["IsFixture", "IsObstruction", "IsFloor", "IsFloorSealed"],
            startingConds: ["IsInstalled"], category: "CTRL",
            mapPoints: new Dictionary<string, (double X, double Y)> { ["use"] = (0, 0) },
            interactions: ["Switch"]);
        var cat = f.Build();
        var doc = Fixtures.Doc(cat,
            Fixtures.P("Desk", 0, 0),
            Fixtures.P("Floor", 0, 1), Fixtures.P("Floor", 1, 1));

        var grid = ShipGrid.FromDocument(doc, cat);
        var ov = WalkNetwork.ToOverlay(grid, WalkNetwork.Build(grid, cat));

        var left = ov.AccessAt((0, 0));
        var right = ov.AccessAt((1, 0));
        Assert.NotNull(left);
        Assert.Same(left, right);                       // one answer, reachable from either tile of the part
        Assert.Contains((0, 1), left!.Standing);
        Assert.Null(ov.AccessAt((0, 1)));               // the floor in front is not itself a device
    }

    [Fact]
    public void A_wall_embedded_device_is_not_blinded_by_its_own_hull()
    {
        // A sensor sits IN the hull line, so its sight origin is inside the wall and every ray out crosses the
        // neighbouring wall tiles' occluder boxes. The game escapes this with a physics raycast (which cannot
        // reproduce headlessly and does not hit the collider it starts inside), so sight is granted for embedded
        // parts rather than reporting every hull sensor, antenna and weapon on the ship as unusable.
        var f = Base();
        f.Interaction("Panel", "use", 2);
        f.Part("Sensor", tileConds: ["IsWall", "IsObstruction", "IsFixture"],
            startingConds: ["IsInstalled"], category: "SENS",
            mapPoints: new Dictionary<string, (double X, double Y)> { ["use"] = (0, -16) },
            shadowBoxes: [new ShadowBox(0, 0, 0.5, 0.5, false)], interactions: ["Panel"]);
        var cat = f.Build();
        // hull line with the sensor in it, open floor below
        var doc = Fixtures.Doc(cat,
            Fixtures.P("Wall", -1, 0), Fixtures.P("Sensor", 0, 0), Fixtures.P("Wall", 1, 0),
            Fixtures.P("Floor", -1, 1), Fixtures.P("Floor", 0, 1), Fixtures.P("Floor", 1, 1));

        var dev = Assert.Single(Analyze(cat, doc).Devices);

        Assert.True(dev.Reachable);
        Assert.NotEqual(WalkBlock.SightBlocked, dev.Reason);
    }

    [Fact]
    public void Hull_mounted_kit_reads_as_EVA_only_not_unreachable()
    {
        // A console with nothing but vacuum at its use point: a suited crew member reaches it, so it is EVA-only
        // rather than broken. Without the distinction, external rotors and cargo pods swamp the Law report
        // (the core "Hand Of God" alone reports 33 of its 35 fittings unusable).
        var cat = ReachCatalog(range: 0);
        var doc = Fixtures.Doc(cat, Fixtures.P("Console", 0, 0));

        var dev = Assert.Single(Analyze(cat, doc).Devices);

        Assert.False(dev.Reachable);
        Assert.True(dev.EvaOnly);
        Assert.False(dev.Blocked);                        // so it stays out of the Law report
        Assert.Empty(Analyze(cat, doc).Unreachable);
    }

    [Fact]
    public void A_walled_in_device_is_blocked_not_merely_EVA_only()
    {
        // the difference that matters: a suit does not help when the use point is solid wall
        var cat = ReachCatalog(range: 0);
        var doc = Fixtures.Doc(cat, Fixtures.P("Console", 0, 0), Fixtures.P("Wall", 0, 1));

        var dev = Assert.Single(Analyze(cat, doc).Devices);

        Assert.True(dev.Blocked);
        Assert.False(dev.EvaOnly);
        Assert.Single(Analyze(cat, doc).Unreachable);
    }

    [Fact]
    public void Mineable_rock_is_terrain_not_a_fitting()
    {
        // Rock and ice carry an ACTMine interaction, but a block in the middle of an asteroid being unreachable
        // is what rock IS. Without this, Port Mojave alone reports 1,811 "unreachable devices".
        var f = Base();
        f.Interaction("Mine", "use", 1);
        f.Part("Regolith", tileConds: ["IsWall", "IsObstruction"],
            startingConds: ["IsInstalled", "IsMineable"], category: "HULL", interactions: ["Mine"]);
        var cat = f.Build();
        var doc = Fixtures.Doc(cat, Fixtures.P("Regolith", 0, 0), Fixtures.P("Regolith", 1, 0));

        Assert.Empty(Analyze(cat, doc).Devices);
    }

    [Fact]
    public void Loose_stock_is_not_treated_as_a_fixture_to_reach()
    {
        var f = Base();
        f.Interaction("Use", "use", 0);
        f.Part("Crate", tileConds: ["IsFloor", "IsFloorSealed"], category: "MISC", interactions: ["Use"]);   // no IsInstalled
        var cat = f.Build();
        var doc = Fixtures.Doc(cat, Fixtures.P("Crate", 0, 0));

        Assert.Empty(Analyze(cat, doc).Devices);
    }

    [Fact]
    public void An_unreachable_device_is_marked_on_its_own_body_not_on_its_target_tile()
    {
        // The target tile is where a crew member would STAND, which is a tile belonging to some other part —
        // the floor in front, or the wall the fitting is set into. Marking that names the wrong thing, and
        // clicking the mark selects the wrong part (a light in an alcove reads as the console behind it).
        var cat = ReachCatalog(range: 0);
        var doc = Fixtures.Doc(cat, Fixtures.P("Console", 0, 0), Fixtures.P("Wall", 0, 1));
        var grid = ShipGrid.FromDocument(doc, cat);
        var r = WalkNetwork.Build(grid, cat);

        var dev = Assert.Single(r.Devices);
        Assert.True(dev.Blocked);
        Assert.DoesNotContain(dev.TargetTile, dev.BodyTiles);   // the two really are different tiles here
        Assert.Equal([grid.Index(1, 1)], dev.BodyTiles);        // the console's own 1×1 body

        var ov = WalkNetwork.ToOverlay(grid, r);
        Assert.Equal([(0, 0)], ov.UnreachableDevices);          // doc coords of the console, not of the wall
    }

    // ---- overlay projection ----

    [Fact]
    public void Overlay_projects_zones_into_document_coordinates()
    {
        var cat = Base().Build();
        var doc = Fixtures.Doc(cat, Strip("Floor", 0, 1, 0));
        var grid = ShipGrid.FromDocument(doc, cat);

        var ov = WalkNetwork.ToOverlay(grid, WalkNetwork.Build(grid, cat));

        var zone = Assert.Single(ov.Zones);
        Assert.Contains((0, 0), zone);
        Assert.Contains((1, 0), zone);
    }

    // ---- real data: the facts the port stands on ----

    [SkippableFact]
    public void Real_catalog_resolves_interaction_reach_data()
    {
        var g = TestData.RequireGame();

        // ranges are per-interaction, so a single hardcoded radius would be wrong
        var expected = new (string Name, double Range)[]
            { ("GUINavStation", 0), ("GUIAirPump", 1), ("GUICooler", 2), ("GUIReactor", 3) };
        foreach (var (name, range) in expected)
        {
            Assert.True(g.Catalog.InteractionDefs.TryGetValue(name, out var ia), $"{name} missing");
            Assert.Equal("use", ia!.TargetPoint);
            Assert.Equal(range, ia.TargetPointRange);
        }

        // and a real device joins its condowner's aInteractions to them
        Assert.True(g.Catalog.ByDefName.TryGetValue("ItmCooler01On", out var cooler), "ItmCooler01On missing");
        var cooled = Assert.Single(g.Catalog.InteractionsFor(cooler!));
        Assert.Equal("GUICooler", cooled.Name);
        Assert.True(cooler!.MapPoints.ContainsKey("use"), "the cooler declares no use point");
    }

    [SkippableFact]
    public void Real_data_still_marks_the_stuck_doors_stuck()
    {
        var g = TestData.RequireGame();

        // the four defs whose closed state genuinely seals a section off
        foreach (var def in new[] { "ItmDoor01Closed", "ItmDoor01ClosedOnLocked", "ItmDoor01ClosedDmg", "ItmDockSys03ClosedDmg" })
        {
            var part = g.Catalog.Lookup(def);
            Skip.If(part is null, $"{def} not in this install");
            var conds = part!.Item.SocketAdds.SelectMany(g.Catalog.LootConds).ToHashSet();
            Assert.Contains("IsPortalStuck", conds);
        }

        // …and the powered closed door that does not
        var on = g.Catalog.Lookup("ItmDoor01ClosedOn");
        Skip.If(on is null, "ItmDoor01ClosedOn not in this install");
        Assert.DoesNotContain("IsPortalStuck", on!.Item.SocketAdds.SelectMany(g.Catalog.LootConds).ToHashSet());
    }

    [SkippableFact]
    public void A_real_ship_walks_as_one_body_with_its_devices_reachable()
    {
        var g = TestData.RequireGame();
        var path = System.IO.Path.Combine(g.Env.CoreDataDir, "ships", "Baleen.json");
        Skip.IfNot(System.IO.File.Exists(path), "the Baleen template is not in this install");
        var tmpl = ShipTemplate.ParseFile(System.IO.File.ReadAllText(path)).FirstOrDefault();
        Skip.If(tmpl is null, "the Baleen file holds no ship");

        var grid = ShipGrid.FromTemplate(tmpl!, new PartResolver(g.Index), g.Catalog);
        var r = WalkNetwork.Build(grid, g.Catalog);

        // a dev-authored ship is walkable end to end and its fittings are usable
        Assert.NotEmpty(r.Devices);
        var main = r.Zones[r.LargestZone];
        Assert.True(main.TileCount > 50, $"largest interior zone is only {main.TileCount} tiles");
        var unreachable = r.Unreachable.ToList();
        Assert.True(unreachable.Count * 4 < r.Devices.Count,
            $"{unreachable.Count} of {r.Devices.Count} devices unreachable on a stock ship: "
            + string.Join(", ", unreachable.Take(8).Select(d => $"{d.Friendly} ({d.Reason})")));
    }
}
