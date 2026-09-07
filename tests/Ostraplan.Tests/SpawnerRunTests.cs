using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// Running a loot spawner (#65): the port of the game's own loot roll, and turning what it names into cargo on the
/// deck. The grammar tests need no install; the ones that roll a real table do.
/// </summary>
public class SpawnerRunTests
{
    // ---- the grammar ----

    [Fact]
    public void A_group_of_alternatives_parses_with_its_chances_and_its_range()
    {
        // ItmLootSpawnEngineering, the target 483 of the game's own spawners point at. The two alternatives share
        // one draw, so this is 80% good salvage, 10% junk and 10% nothing.
        var groups = LootUnit.ParseGroups(["ItmRandomEngineeringLoot=0.8x1|ItmScrapTrash=0.1x1-2"]);

        var group = Assert.Single(groups);
        Assert.Equal(2, group.Count);
        Assert.Equal("ItmRandomEngineeringLoot", group[0].Name);
        Assert.Equal(0.8, group[0].Chance, 6);
        Assert.Equal(1, group[0].Min, 6);
        Assert.Equal(1, group[0].Max, 6);       // no range given, so the high end is the low one
        Assert.Equal("ItmScrapTrash", group[1].Name);
        Assert.Equal(0.1, group[1].Chance, 6);
        Assert.Equal(1, group[1].Min, 6);
        Assert.Equal(2, group[1].Max, 6);
    }

    [Fact]
    public void Two_strings_are_two_independent_draws_and_one_string_is_not()
    {
        // ItmLootSpawnRandomScrap authors three separate strings, so it rolls three times over.
        var groups = LootUnit.ParseGroups([
            "ItmRandomPartsScrap=1.0x1-2", "ItmRandomPartsScrap=0.8x1-4", "ItmScrapTrash=0.2x1-5"]);

        Assert.Equal(3, groups.Count);
        Assert.All(groups, g => Assert.Single(g));
    }

    [Fact]
    public void An_entry_with_no_chance_data_is_dropped_rather_than_read_as_a_certainty()
    {
        // The game logs "loot definition shorter than expected" and skips the unit. Reading a bare name as a
        // guaranteed spawn would put items in a ship that the game would never spawn there.
        Assert.Empty(Assert.Single(LootUnit.ParseGroups(["ItmScrapTrash"])));
        Assert.Empty(Assert.Single(LootUnit.ParseGroups(["ItmScrapTrash=0x1"])));   // a zero chance goes too
    }

    // ---- the roll ----

    /// <summary>A synthetic catalog will not do here: the roll reads <c>data/loot</c>, so it needs the install.</summary>
    private static Catalog Loots() => TestData.RequireGame().Catalog;

    [SkippableFact]
    public void The_roll_is_actually_random()
    {
        // The premise for every seeded test below. If the roll returned the same thing regardless, a test that
        // asserts two seeds agree would pass for the wrong reason.
        var catalog = Loots();
        Skip.IfNot(catalog.Loots.ContainsKey("ItmLootSpawnAirlock"), "this install has no ItmLootSpawnAirlock");

        var results = Enumerable.Range(0, 40)
            .Select(seed => string.Join(",", LootRoll.Roll(catalog, "ItmLootSpawnAirlock", 1, new Random(seed)).Names))
            .Distinct()
            .ToList();

        Assert.True(results.Count > 1, "40 seeds of a 5-way table produced one outcome");
    }

    [SkippableFact]
    public void The_same_seed_rolls_the_same_items()
    {
        // The whole point of offering a seed: a result you liked can be got back.
        var catalog = Loots();
        Skip.IfNot(catalog.Loots.ContainsKey("ItmLootSpawnAirlock"), "this install has no ItmLootSpawnAirlock");

        var first = LootRoll.Roll(catalog, "ItmLootSpawnAirlock", 3, new Random(12345)).Names;
        var again = LootRoll.Roll(catalog, "ItmLootSpawnAirlock", 3, new Random(12345)).Names;

        Assert.Equal(first, again);
    }

    [SkippableFact]
    public void Everything_a_roll_names_is_something_this_install_can_actually_place()
    {
        // Measured over the whole spawner corpus while porting: every name the game's own tables produce resolves
        // to a condowner or a cooverlay. A roll that named defs Ostraplan could not look up would lay nothing and
        // report a loss on every run, which is the failure mode this guards.
        var catalog = Loots();
        var targets = new[]
        {
            "ItmLootSpawnEngineering", "ItmLootSpawnProvisions", "ItmLootSpawnAirlock",
            "ItmLootSpawnMedical", "ItmLootSpawnRandomScrap",
        }.Where(catalog.Loots.ContainsKey).ToList();
        Skip.If(targets.Count == 0, "this install has none of the core spawner targets");

        var unresolved = new List<string>();
        foreach (var target in targets)
            for (var seed = 0; seed < 25; seed++)
            {
                var roll = LootRoll.Roll(catalog, target, 2, new Random(seed));
                Assert.Empty(roll.MissingTables);
                Assert.False(roll.Truncated);
                unresolved.AddRange(roll.Names.Where(n => catalog.Lookup(n) is null));
            }

        Assert.Empty(unresolved.Distinct());
    }

    [SkippableFact]
    public void A_table_this_install_does_not_have_is_reported_rather_than_thrown()
    {
        // A design can name a table from a mod that has since been turned off.
        var roll = LootRoll.Roll(Loots(), "NoSuchLootTableAnywhere", 1, new Random(1));

        Assert.Empty(roll.Names);
        Assert.Equal("NoSuchLootTableAnywhere", Assert.Single(roll.MissingTables));
    }

    [SkippableFact]
    public void A_count_of_zero_makes_nothing()
    {
        // 0 and -1 both appear in the game's data, on spawners that only fill a damaged ship.
        var catalog = Loots();
        Skip.IfNot(catalog.Loots.ContainsKey("ItmLootSpawnMedical"), "this install has no ItmLootSpawnMedical");

        Assert.Empty(LootRoll.Roll(catalog, "ItmLootSpawnMedical", 0, new Random(1)).Names);
        Assert.Empty(LootRoll.Roll(catalog, "ItmLootSpawnMedical", -1, new Random(1)).Names);
    }

    // ---- the scatter square ----

    [Fact]
    public void The_scatter_is_a_square_of_two_range_plus_one_a_side()
    {
        // The game's editor draws the spawner icon at exactly this size (localScale = 1 + 2·strRange), and its
        // spawn zone is the same box: GetZoneFromTileRadius is called leaving bCircle false. "Radius" reads like a
        // circle and is not one, which is the whole of what #68 was reporting.
        Assert.Equal((5, 5, 1), new SpawnerSettings { Range = 0 }.ScatterBox(5, 5));
        Assert.Equal((3, 3, 5), new SpawnerSettings { Range = 2 }.ScatterBox(5, 5));

        var tiles = new SpawnerSettings { Range = 1 }.ScatterTiles(0, 0).ToList();
        Assert.Equal(9, tiles.Count);
        Assert.Contains((-1, -1), tiles);   // the corner a circle of radius 1 would not reach
        Assert.Contains((1, 1), tiles);
    }

    [Theory]
    [InlineData(ShipCondition.New, true, false, false)]
    [InlineData(ShipCondition.Damaged, false, true, false)]
    [InlineData(ShipCondition.Derelict, false, false, true)]
    public void Each_condition_reads_its_own_flag(ShipCondition condition, bool nw, bool damaged, bool derelict)
    {
        Assert.True(new SpawnerSettings { WhenNew = nw, WhenDamaged = damaged, WhenDerelict = derelict }
            .FiresWhen(condition));
        Assert.False(new SpawnerSettings { WhenNew = !nw, WhenDamaged = !damaged, WhenDerelict = !derelict }
            .FiresWhen(condition));
    }

    // ---- the run ----

    private static bool HullReady(Catalog c) =>
        c.Lookup("ItmWall1x1") is not null && c.Lookup("ItmFloorGrate01") is not null
        && c.Lookup("SysLootSpawner") is not null;

    /// <summary>A small sealed hull with a floored interior for the cargo to land on.</summary>
    private static ShipDocument Hull(Catalog catalog)
    {
        var doc = new ShipDocument(catalog);
        void Place(string def, int x, int y) => new PlaceCommand(new Placement { DefName = def, X = x, Y = y }).Do(doc);
        for (var x = 0; x < 7; x++) { Place("ItmWall1x1", x, 0); Place("ItmWall1x1", x, 8); }
        for (var y = 1; y <= 7; y++)
        {
            Place("ItmWall1x1", 0, y); Place("ItmWall1x1", 6, y);
            for (var x = 1; x < 6; x++) Place("ItmFloorGrate01", x, y);
        }
        return doc;
    }

    private static LooseObject Spawner(ShipDocument doc, string target, int range = 2, int count = 4,
        int x = 3, int y = 4)
    {
        var o = new LooseObject
        {
            DefName = "SysLootSpawner", X = x, Y = y,
            Spawner = new SpawnerSettings { Target = target, Range = range, Count = count },
        };
        new PlaceLooseCommand(o).Do(doc);
        return o;
    }

    [SkippableFact]
    public void Running_a_spawner_leaves_its_cargo_and_takes_the_spawner_away()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(HullReady(g.Catalog), "this install lacks one of the probe defs");
        Skip.IfNot(g.Catalog.Loots.ContainsKey("ItmLootSpawnRandomScrap"), "no ItmLootSpawnRandomScrap here");

        var doc = Hull(g.Catalog);
        var spawner = Spawner(doc, "ItmLootSpawnRandomScrap");

        var plan = SpawnerRun.Plan(doc, g.Catalog, [spawner], ShipCondition.New, seed: 4);
        Assert.False(plan.IsEmpty);
        plan.ToCommand().Do(doc);

        // The spawner is gone and real items are in its place. Keeping both would export a ship that arrives
        // carrying the cargo twice: once as the items, once as the spawner rolling again in game.
        Assert.DoesNotContain(spawner, doc.LooseObjects);
        Assert.All(doc.LooseObjects, o => Assert.Null(o.Spawner));
        Assert.Equal(plan.Placed.Count, doc.LooseObjects.Count);
        Assert.NotEmpty(doc.LooseObjects);
    }

    [SkippableFact]
    public void Undoing_a_run_puts_the_spawner_back_and_takes_the_cargo_away()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(HullReady(g.Catalog), "this install lacks one of the probe defs");
        Skip.IfNot(g.Catalog.Loots.ContainsKey("ItmLootSpawnRandomScrap"), "no ItmLootSpawnRandomScrap here");

        var doc = Hull(g.Catalog);
        var spawner = Spawner(doc, "ItmLootSpawnRandomScrap");
        var before = doc.LooseObjects.ToList();

        var command = SpawnerRun.Plan(doc, g.Catalog, [spawner], ShipCondition.New, seed: 9).ToCommand();
        command.Do(doc);
        command.Undo(doc);

        // A run is a conversion, not a commitment: one Ctrl+Z and the design is exactly what it was.
        Assert.Equal(before, doc.LooseObjects);
        Assert.Same(spawner, Assert.Single(doc.LooseObjects));
        Assert.NotNull(spawner.Spawner);
    }

    [SkippableFact]
    public void Everything_a_run_lays_is_inside_the_scatter_square()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(HullReady(g.Catalog), "this install lacks one of the probe defs");
        Skip.IfNot(g.Catalog.Loots.ContainsKey("ItmLootSpawnRandomScrap"), "no ItmLootSpawnRandomScrap here");

        var doc = Hull(g.Catalog);
        var spawner = Spawner(doc, "ItmLootSpawnRandomScrap", range: 1, count: 6);
        var square = spawner.Spawner!.ScatterTiles(spawner.X, spawner.Y).ToHashSet();

        var plan = SpawnerRun.Plan(doc, g.Catalog, [spawner], ShipCondition.New, seed: 3);
        Skip.If(plan.Placed.Count == 0, "this seed rolled nothing");

        foreach (var item in plan.Placed) Assert.Contains((item.X, item.Y), square);
    }

    [SkippableFact]
    public void A_spawner_with_no_scatter_puts_its_cargo_on_its_own_tile()
    {
        // Range 0 is 2,849 of the 3,631 spawners the game's own ships carry, and it is the case that would fail
        // if the run tested its placements against a document the spawner was still standing in.
        var g = TestData.RequireGame();
        Skip.IfNot(HullReady(g.Catalog), "this install lacks one of the probe defs");
        Skip.IfNot(g.Catalog.Loots.ContainsKey("ItmLootSpawnRandomScrap"), "no ItmLootSpawnRandomScrap here");

        var doc = Hull(g.Catalog);
        var spawner = Spawner(doc, "ItmLootSpawnRandomScrap", range: 0, count: 1);

        var plan = SpawnerRun.Plan(doc, g.Catalog, [spawner], ShipCondition.New, seed: 2);
        Skip.If(plan.Rolled == 0, "this seed rolled nothing");

        // Something landed, and it landed where the spawner was standing. Nothing else could have: a square of
        // range 0 is one tile, and the rest of the roll is lost exactly as the game loses it.
        Assert.NotEmpty(plan.Placed);
        Assert.All(plan.Placed, o => Assert.Equal((spawner.X, spawner.Y), (o.X, o.Y)));
    }

    [SkippableFact]
    public void A_person_spawner_is_never_run()
    {
        // Pspec and Pspec Loot make people, and a person is not something a design holds.
        var g = TestData.RequireGame();
        Skip.IfNot(HullReady(g.Catalog), "this install lacks one of the probe defs");

        var doc = Hull(g.Catalog);
        var crew = new LooseObject
        {
            DefName = "SysLootSpawner", X = 3, Y = 4,
            Spawner = new SpawnerSettings { Type = SpawnerType.Pspec, Target = "OKLGScavCrew" },
        };
        new PlaceLooseCommand(crew).Do(doc);

        Assert.False(SpawnerRun.IsRunnable(crew));
        Assert.Empty(SpawnerRun.Runnable(doc));
        Assert.True(SpawnerRun.Plan(doc, g.Catalog, [crew]).IsEmpty);
    }

    [SkippableFact]
    public void An_unconfigured_spawner_is_not_offered()
    {
        // "Blank" is a real loot entry that yields nothing, so running one is a no-op.
        var g = TestData.RequireGame();
        Skip.IfNot(HullReady(g.Catalog), "this install lacks one of the probe defs");

        var doc = Hull(g.Catalog);
        var blank = new LooseObject { DefName = "SysLootSpawner", X = 3, Y = 4 };
        new PlaceLooseCommand(blank).Do(doc);   // the drop seeds the default panel

        Assert.Equal(SpawnerSettings.DefaultTarget, blank.Spawner!.Target);
        Assert.False(SpawnerRun.IsRunnable(blank));
        Assert.Empty(SpawnerRun.Runnable(doc));
    }

    [SkippableFact]
    public void A_spawner_that_does_not_fire_for_this_ship_is_left_where_it_is()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(HullReady(g.Catalog), "this install lacks one of the probe defs");
        Skip.IfNot(g.Catalog.Loots.ContainsKey("ItmLootSpawnRandomScrap"), "no ItmLootSpawnRandomScrap here");

        var doc = Hull(g.Catalog);
        var wreckOnly = new LooseObject
        {
            DefName = "SysLootSpawner", X = 3, Y = 4,
            Spawner = new SpawnerSettings
            {
                Target = "ItmLootSpawnRandomScrap", Range = 1, Count = 3,
                WhenNew = false, WhenDamaged = false, WhenDerelict = true,
            },
        };
        new PlaceLooseCommand(wreckOnly).Do(doc);

        // Rolled for a new ship it makes nothing and stays put: it still has work to do in the case it is
        // written for, so consuming it would throw that away.
        var asNew = SpawnerRun.Plan(doc, g.Catalog, [wreckOnly], ShipCondition.New, seed: 1);
        Assert.True(asNew.IsEmpty);
        Assert.Same(wreckOnly, Assert.Single(asNew.Unfired));

        // Rolled as a derelict it fires.
        var asWreck = SpawnerRun.Plan(doc, g.Catalog, [wreckOnly], ShipCondition.Derelict, seed: 1);
        Assert.Empty(asWreck.Unfired);
        Assert.Same(wreckOnly, Assert.Single(asWreck.Consumed));
    }

    [SkippableFact]
    public void A_run_with_no_seed_reports_the_one_it_used()
    {
        // Left blank the seed is chosen for you, and it comes back so the result can be reproduced.
        var g = TestData.RequireGame();
        Skip.IfNot(HullReady(g.Catalog), "this install lacks one of the probe defs");
        Skip.IfNot(g.Catalog.Loots.ContainsKey("ItmLootSpawnRandomScrap"), "no ItmLootSpawnRandomScrap here");

        var doc = Hull(g.Catalog);
        var spawner = Spawner(doc, "ItmLootSpawnRandomScrap");

        var first = SpawnerRun.Plan(doc, g.Catalog, [spawner], ShipCondition.New, seed: null);
        var repeat = SpawnerRun.Plan(doc, g.Catalog, [spawner], ShipCondition.New, first.Seed);

        Assert.Equal(
            first.Placed.Select(o => (o.DefName, o.X, o.Y, o.Quantity)),
            repeat.Placed.Select(o => (o.DefName, o.X, o.Y, o.Quantity)));
    }

    [SkippableFact]
    public void Cargo_with_nowhere_to_go_is_counted_rather_than_piled_up()
    {
        // The game destroys what DropCOsNearby could not place. One item per tile is Ostraplan's own invariant,
        // so a spawner that rolls more than its square can hold has to lose the remainder the same way rather
        // than stacking objects the plan could not draw.
        var g = TestData.RequireGame();
        Skip.IfNot(HullReady(g.Catalog), "this install lacks one of the probe defs");
        Skip.IfNot(g.Catalog.Loots.ContainsKey("ItmLootSpawnRandomScrap"), "no ItmLootSpawnRandomScrap here");

        var doc = Hull(g.Catalog);
        var spawner = Spawner(doc, "ItmLootSpawnRandomScrap", range: 0, count: 60);

        var plan = SpawnerRun.Plan(doc, g.Catalog, [spawner], ShipCondition.New, seed: 11);

        // One tile to land on, so at most one unstacked object fits and everything else is lost.
        Assert.True(plan.Rolled > plan.Placed.Count);
        Assert.True(plan.NoRoom > 0);
        Assert.True(plan.Placed.Count <= 1);
    }
}
