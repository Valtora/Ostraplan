using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// Loot spawners (#55): the panel that decides what a ship spawns with, and the type that decides which array of
/// the ship file the spawner leaves by.
/// </summary>
public class LootSpawnerTests
{
    private static IReadOnlyDictionary<string, string?> Panel(params string?[] flat)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < flat.Length; i += 2)
            if (flat[i] is { } key) map[key] = flat[i + 1];
        return map;
    }

    [Theory]
    [InlineData("Loot", SpawnerType.Loot)]
    [InlineData("Pspec", SpawnerType.Pspec)]
    [InlineData("Pspec Loot", SpawnerType.PspecLoot)]
    public void The_games_three_type_words_round_trip(string wire, SpawnerType type)
    {
        Assert.Equal(type, SpawnerSettings.ParseType(wire));
        Assert.Equal(wire, SpawnerSettings.Wire(type));
    }

    [Fact]
    public void An_unknown_type_reads_as_an_object_spawner_rather_than_being_lost()
    {
        // The template default is Loot, so a mod's typo should import as a spawner that makes objects rather than
        // dropping the spawner and its position with it.
        Assert.Equal(SpawnerType.Loot, SpawnerSettings.ParseType("Rubbish"));
        Assert.Equal(SpawnerType.Loot, SpawnerSettings.ParseType(null));
        Assert.Equal(SpawnerType.Loot, SpawnerSettings.ParseType(""));
    }

    [Fact]
    public void The_type_decides_which_array_the_spawner_leaves_by()
    {
        // Measured over the game's own ships: all 2,954 spawners in aItems are Loot, and all 677 in
        // aShallowPSpecs are Pspec or Pspec Loot. Neither array ever holds the other kind.
        Assert.False(new SpawnerSettings { Type = SpawnerType.Loot }.IsPersonSpawn);
        Assert.True(new SpawnerSettings { Type = SpawnerType.Pspec }.IsPersonSpawn);
        Assert.True(new SpawnerSettings { Type = SpawnerType.PspecLoot }.IsPersonSpawn);
    }

    [Fact]
    public void Reads_the_reporters_own_spawner_panel()
    {
        // The exact panel from issue #55, keyed as the game writes it.
        var settings = SpawnerSettings.FromPanel(Panel(
            "strGUIPrefab", "GUILootSpawn",
            "strFriendlyName", "Loot Spawner",
            "strGUIPrefabRight", null,
            "strType", "Pspec",
            "strLoot", "OKLGScavCrew",
            "strRange", "0",
            "strCount", "1",
            "strNew", "True",
            "strDamaged", "True"));

        Assert.NotNull(settings);
        Assert.Equal(SpawnerType.Pspec, settings.Type);
        Assert.Equal("OKLGScavCrew", settings.Target);
        Assert.Equal(0, settings.Range);
        Assert.Equal(1, settings.Count);
        Assert.True(settings.WhenNew);
        Assert.True(settings.WhenDamaged);
        Assert.True(settings.WhenDerelict);   // absent, and absent means it fires
    }

    [Fact]
    public void A_panel_that_is_not_a_spawner_reads_as_null()
    {
        Assert.Null(SpawnerSettings.FromPanel(Panel("strGUIPrefab", "GUIAirPump", "strInput01", "abc")));
        Assert.Null(SpawnerSettings.FromPanel(Panel("inputConnections", "x")));
    }

    [Fact]
    public void A_missing_condition_flag_means_the_spawner_fires()
    {
        // The safer reading of the two: a spawner that declines to fire is indistinguishable from one that was
        // dropped, and the game's ships write True for these overwhelmingly.
        var settings = SpawnerSettings.FromPanel(Panel("strGUIPrefab", "GUILootSpawn", "strLoot", "X"));
        Assert.NotNull(settings);
        Assert.True(settings.WhenNew);
        Assert.True(settings.WhenDamaged);
        Assert.True(settings.WhenDerelict);
    }

    [Fact]
    public void A_flag_written_False_is_honoured()
    {
        var settings = SpawnerSettings.FromPanel(Panel(
            "strGUIPrefab", "GUILootSpawn", "strLoot", "X",
            "strNew", "False", "strDamaged", "true", "strDerelict", "FALSE"));

        Assert.NotNull(settings);
        Assert.False(settings.WhenNew);
        Assert.True(settings.WhenDamaged);    // case-insensitive, as bool.TryParse is
        Assert.False(settings.WhenDerelict);
    }

    [Fact]
    public void The_panel_written_out_is_the_panel_read_back()
    {
        var before = new SpawnerSettings
        {
            Type = SpawnerType.PspecLoot, Target = "LootPspecOKLG", Range = 3, Count = 2,
            WhenNew = false, WhenDamaged = true, WhenDerelict = false,
        };

        var flat = before.ToPanelKeys();
        var keys = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < flat.Count; i += 2)
            keys[(string)flat[i]!] = flat[i + 1] as string;

        Assert.Equal(before, SpawnerSettings.FromPanel(keys));
    }

    [Fact]
    public void Every_key_the_game_writes_is_written_including_the_flags_the_template_omits()
    {
        var flat = SpawnerSettings.Default.ToPanelKeys();
        var keys = new List<string>();
        for (var i = 0; i + 1 < flat.Count; i += 2) keys.Add((string)flat[i]!);

        Assert.Contains("strType", keys);
        Assert.Contains("strLoot", keys);
        Assert.Contains("strRange", keys);
        Assert.Contains("strCount", keys);
        // The template declares none of these three, so an export that leaves them out hands the spawner a
        // default nothing in Ostraplan knows.
        Assert.Contains("strNew", keys);
        Assert.Contains("strDamaged", keys);
        Assert.Contains("strDerelict", keys);
        Assert.Equal("GUILootSpawn", flat[1]);
    }

    [Fact]
    public void An_unconfigured_spawner_points_at_the_games_own_empty_table()
    {
        Assert.Equal("Blank", SpawnerSettings.Default.Target);
        Assert.Equal(SpawnerType.Loot, SpawnerSettings.Default.Type);
        Assert.Equal(1, SpawnerSettings.Default.Count);
    }

    [Fact]
    public void Clamping_survives_a_hand_edited_file()
    {
        var wild = new SpawnerSettings { Target = "   ", Range = 9_000, Count = -50 }.Clamped();
        Assert.Equal(SpawnerSettings.DefaultTarget, wild.Target);
        Assert.Equal(SpawnerSettings.MaxRange, wild.Range);
        Assert.Equal(SpawnerSettings.MinCount, wild.Count);
    }

    [Fact]
    public void Only_a_person_spawn_can_take_a_synthesised_boarding_role()
    {
        Assert.True(new SpawnerSettings { Type = SpawnerType.Pspec, Target = "Boarding" }.IsBoardingRole);
        Assert.True(new SpawnerSettings { Type = SpawnerType.Pspec, Target = "NotBoarding" }.IsBoardingRole);
        Assert.False(new SpawnerSettings { Type = SpawnerType.Pspec, Target = "OKLGScavCrew" }.IsBoardingRole);
        // A Loot spawner named "Boarding" is a different array entirely and must not displace the arrival point.
        Assert.False(new SpawnerSettings { Type = SpawnerType.Loot, Target = "Boarding" }.IsBoardingRole);
    }

    [Fact]
    public void A_target_lists_its_name_and_only_a_useful_second_word()
    {
        Assert.Equal("EJDRLEO  (LEOfficer)", new SpawnerTarget("EJDRLEO", "LEOfficer").Display);
        Assert.Equal("Boarding", new SpawnerTarget("Boarding", "").Display);
        Assert.Equal("Boarding", new SpawnerTarget("Boarding", null).Display);
        Assert.Equal("Same", new SpawnerTarget("Same", "Same").Display);   // no point saying it twice
    }

    [Fact]
    public void Searching_a_target_matches_either_half_and_ignores_case()
    {
        var t = new SpawnerTarget("EJDRMaintenanceTech", "Technician");
        Assert.True(t.Matches(""));
        Assert.True(t.Matches("ejdr"));
        Assert.True(t.Matches("technician"));
        Assert.True(t.Matches("Maintenance"));
        Assert.False(t.Matches("medical"));
    }

    [SkippableFact]
    public void Each_type_is_offered_the_targets_the_game_actually_uses_for_it()
    {
        var g = TestData.RequireGame();

        var loot = SpawnerCatalog.For(g.Catalog, SpawnerType.Loot);
        var pspec = SpawnerCatalog.For(g.Catalog, SpawnerType.Pspec);
        var pspecLoot = SpawnerCatalog.For(g.Catalog, SpawnerType.PspecLoot);
        Skip.If(loot.Count == 0 && pspec.Count == 0, "This install's data declares no spawner targets.");

        // The three sets are different questions, and offering the whole of data/loot for all of them would be
        // thousands of entries that do nothing in a spawner.
        Assert.NotEqual(loot.Count, pspec.Count);
        Assert.True(pspecLoot.Count < loot.Count);

        // Targets the game's own ships name, each in the set its type is offered.
        Assert.Contains(loot, t => t.Name == "ItmLootSpawnEngineering");
        Assert.Contains(pspec, t => t.Name == SpawnerSettings.BoardingRole);
        Assert.Contains(pspec, t => t.Name == SpawnerSettings.NotBoardingRole);

        Assert.True(SpawnerCatalog.Resolves(g.Catalog, SpawnerType.Loot, "ItmLootSpawnEngineering"));
        Assert.False(SpawnerCatalog.Resolves(g.Catalog, SpawnerType.Loot, "NoSuchLootTable"));
        // Cross-type: a person spec is not an object-spawner target.
        Assert.False(SpawnerCatalog.Resolves(g.Catalog, SpawnerType.Loot, SpawnerSettings.BoardingRole));
    }

    [SkippableFact]
    public void A_spawner_dropped_on_the_deck_gets_a_panel_so_it_is_not_inert()
    {
        var g = TestData.RequireGame();
        // Lookup, not ByDefName: a spawner is a loose item, and those resolve through the index rather than
        // sitting in the buildable-part map.
        Skip.If(g.Catalog.Lookup("SysLootSpawner") is null, "This install has no SysLootSpawner.");

        var doc = new ShipDocument(g.Catalog);
        var spawner = new LooseObject { DefName = "SysLootSpawner", X = 3, Y = 4 };
        new PlaceLooseCommand(spawner).Do(doc);

        // Without the seed the export writes no GUILootSpawn panel at all, the game builds one from the def's
        // template defaults, and the spawner makes nothing while looking placed on the plan.
        Assert.NotNull(spawner.Spawner);
        Assert.Equal(SpawnerSettings.Default, spawner.Spawner);

        // An ordinary deck item is untouched by the seed.
        var crate = new LooseObject { DefName = "SysLootSpawner", X = 9, Y = 9, Spawner = new SpawnerSettings { Target = "Kept" } };
        new PlaceLooseCommand(crate).Do(doc);
        Assert.Equal("Kept", crate.Spawner!.Target);   // an authored panel is never overwritten
    }

    /// <summary>A ship carrying one spawner in each array, which is the separation the export depends on.</summary>
    private const string ShipWithSpawners = """
        [{
          "strName": "Probe", "nCols": 6, "nRows": 6,
          "vShipPos": { "x": 0.0, "y": 0.0 },
          "aItems": [
            { "strName": "ItmWall1x1", "fX": 0.0, "fY": 0.0, "fRotation": 0.0, "strID": "w" },
            { "strName": "SysLootSpawner", "fX": 2.0, "fY": -1.0, "fRotation": 0.0, "strID": "loot",
              "aGPMSettings": [ { "strName": "Panel A", "dictGUIPropMap": [
                "strGUIPrefab", "GUILootSpawn", "strType", "Loot",
                "strLoot", "ItmLootSpawnMedical", "strRange", "2", "strCount", "3",
                "strNew", "True", "strDamaged", "False", "strDerelict", "True" ] } ] }
          ],
          "aShallowPSpecs": [
            { "strName": "SysLootSpawner", "fX": 3.0, "fY": -2.0, "fRotation": 0.0, "strID": "crew",
              "aGPMSettings": [ { "strName": "Panel A", "dictGUIPropMap": [
                "strGUIPrefab", "GUILootSpawn", "strType", "Pspec",
                "strLoot", "OKLGScavCrew", "strRange", "0", "strCount", "1" ] } ] }
          ]
        }]
        """;

    [SkippableFact]
    public void Both_arrays_of_spawners_survive_an_import()
    {
        var g = TestData.RequireGame();
        Skip.If(g.Catalog.Lookup("SysLootSpawner") is null, "This install has no SysLootSpawner.");

        var tmpl = ShipTemplate.ParseFile(ShipWithSpawners).Single();
        var result = TemplateImport.Build(tmpl, g.Catalog, retainOrigin: false,
            ImportOptions.Everything, ShipJson.Largest(ShipWithSpawners));

        // Both used to be lost: the aItems one was dropped with the rest of the IsSystem objects, and
        // aShallowPSpecs was not read at all. An imported station therefore spawned nothing and put its arrivals
        // wherever the export recomputed them.
        Assert.Equal(2, result.SpawnersKept);

        var spawners = result.Doc.LooseObjects.Where(o => o.Spawner is not null).ToList();
        Assert.Equal(2, spawners.Count);

        var loot = spawners.Single(o => !o.Spawner!.IsPersonSpawn).Spawner!;
        Assert.Equal(SpawnerType.Loot, loot.Type);
        Assert.Equal("ItmLootSpawnMedical", loot.Target);
        Assert.Equal(2, loot.Range);
        Assert.Equal(3, loot.Count);
        Assert.False(loot.WhenDamaged);   // the one flag this ship turns off

        var crew = spawners.Single(o => o.Spawner!.IsPersonSpawn).Spawner!;
        Assert.Equal(SpawnerType.Pspec, crew.Type);
        Assert.Equal("OKLGScavCrew", crew.Target);
    }

    /// <summary>A small sealed hull, so the export has an interior to anchor its synthesised spawn points in.</summary>
    private static ShipDocument Hull(Catalog catalog)
    {
        var doc = new ShipDocument(catalog);
        void Place(string def, int x, int y) => new PlaceCommand(new Placement { DefName = def, X = x, Y = y }).Do(doc);
        for (var x = 0; x < 5; x++) { Place("ItmWall1x1", x, 0); Place("ItmWall1x1", x, 6); }
        for (var y = 1; y <= 5; y++)
        {
            Place("ItmWall1x1", 0, y); Place("ItmWall1x1", 4, y);
            for (var x = 1; x < 4; x++) Place("ItmFloorGrate01", x, y);
        }
        return doc;
    }

    private static bool HullReady(Catalog c) =>
        c.Lookup("ItmWall1x1") is not null && c.Lookup("ItmFloorGrate01") is not null
        && c.Lookup("SysLootSpawner") is not null;

    /// <summary>The <c>strLoot</c> a person-spawn entry names.</summary>
    private static string? TargetOf(ExportedShallowPSpec spec)
    {
        foreach (var panel in spec.AGPMSettings)
            for (var i = 0; i + 1 < panel.DictGUIPropMap.Length; i += 2)
                if (panel.DictGUIPropMap[i] as string == "strLoot")
                    return panel.DictGUIPropMap[i + 1] as string;
        return null;
    }

    [SkippableFact]
    public void An_object_spawner_exports_into_aItems_with_a_real_panel()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(HullReady(g.Catalog), "this install lacks one of the probe defs");

        var doc = Hull(g.Catalog);
        new PlaceLooseCommand(new LooseObject
        {
            DefName = "SysLootSpawner", X = 2, Y = 3,
            Spawner = new SpawnerSettings { Target = "ItmLootSpawnMedical", Range = 2, Count = 4 },
        }).Do(doc);

        var (ship, _, _) = ShipExport.Build(doc, g.Catalog, RoomCertifier.LoadSpecs(g.Index), "Spawner Test");

        var spawner = Assert.Single(ship.AItems, i => i.StrName == "SysLootSpawner");
        var panel = Assert.Single(spawner.AGPMSettings!, p => p.DictGUIPropMap.Contains("GUILootSpawn"));

        // Without the panel the game builds one from the def's template defaults and the spawner makes nothing,
        // which is what every spawner Ostraplan exported did before #55.
        var flat = panel.DictGUIPropMap;
        var keys = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < flat.Length; i += 2) keys[(string)flat[i]!] = flat[i + 1] as string;
        Assert.Equal("Loot", keys["strType"]);
        Assert.Equal("ItmLootSpawnMedical", keys["strLoot"]);
        Assert.Equal("2", keys["strRange"]);
        Assert.Equal("4", keys["strCount"]);
    }

    [SkippableFact]
    public void A_person_spawner_leaves_by_the_other_array_entirely()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(HullReady(g.Catalog), "this install lacks one of the probe defs");

        var doc = Hull(g.Catalog);
        new PlaceLooseCommand(new LooseObject
        {
            DefName = "SysLootSpawner", X = 2, Y = 2,
            Spawner = new SpawnerSettings { Type = SpawnerType.Pspec, Target = "OKLGScavCrew" },
        }).Do(doc);

        var (ship, _, _) = ShipExport.Build(doc, g.Catalog, RoomCertifier.LoadSpecs(g.Index), "Crew Test");

        // aItems never holds a person spawn in any ship the game ships, so it must not here either.
        Assert.DoesNotContain(ship.AItems, i => i.StrName == "SysLootSpawner");
        Assert.Contains(ship.AShallowPSpecs!, p => TargetOf(p) == "OKLGScavCrew");
    }

    [SkippableFact]
    public void An_authored_boarding_point_replaces_the_synthesised_one_and_leaves_the_other()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(HullReady(g.Catalog), "this install lacks one of the probe defs");
        var specs = RoomCertifier.LoadSpecs(g.Index);

        // Untouched: the design authors neither role, so both are still synthesised. A ship with no boarding
        // point dumps an arrival at the map origin, often outside the hull, so the fallback has to stay.
        var plain = ShipExport.Build(Hull(g.Catalog), g.Catalog, specs, "Plain").Ship;
        Assert.Equal(2, plain.AShallowPSpecs!.Length);
        Assert.Contains(plain.AShallowPSpecs, p => TargetOf(p) == SpawnerSettings.BoardingRole);
        Assert.Contains(plain.AShallowPSpecs, p => TargetOf(p) == SpawnerSettings.NotBoardingRole);

        var doc = Hull(g.Catalog);
        new PlaceLooseCommand(new LooseObject
        {
            DefName = "SysLootSpawner", X = 3, Y = 5,
            Spawner = new SpawnerSettings { Type = SpawnerType.Pspec, Target = SpawnerSettings.BoardingRole },
        }).Do(doc);

        var ship = ShipExport.Build(doc, g.Catalog, specs, "Authored").Ship;

        // Per role, not all or nothing: the authored Boarding wins, and NotBoarding is still synthesised because
        // the design said nothing about it.
        Assert.Equal(2, ship.AShallowPSpecs!.Length);
        Assert.Single(ship.AShallowPSpecs, p => TargetOf(p) == SpawnerSettings.BoardingRole);
        Assert.Single(ship.AShallowPSpecs, p => TargetOf(p) == SpawnerSettings.NotBoardingRole);

        // And it is the designer's tile that survives, not the one the export would have computed.
        var boarding = ship.AShallowPSpecs.Single(p => TargetOf(p) == SpawnerSettings.BoardingRole);
        var synthesised = plain.AShallowPSpecs.Single(p => TargetOf(p) == SpawnerSettings.BoardingRole);
        Assert.NotEqual((synthesised.FX, synthesised.FY), (boarding.FX, boarding.FY));
    }

    [SkippableFact]
    public void Retuning_a_spawner_undoes()
    {
        var g = TestData.RequireGame();
        // Lookup, not ByDefName: a spawner is a loose item, and those resolve through the index rather than
        // sitting in the buildable-part map.
        Skip.If(g.Catalog.Lookup("SysLootSpawner") is null, "This install has no SysLootSpawner.");

        var doc = new ShipDocument(g.Catalog);
        var obj = new LooseObject { DefName = "SysLootSpawner", X = 1, Y = 1 };
        new PlaceLooseCommand(obj).Do(doc);

        var before = obj.Spawner;
        var after = new SpawnerSettings { Type = SpawnerType.Loot, Target = "ItmLootSpawnMedical", Range = 2 };
        var cmd = new SetSpawnerCommand(obj, before, after);

        cmd.Do(doc);
        Assert.Equal("ItmLootSpawnMedical", obj.Spawner!.Target);
        Assert.Equal(2, obj.Spawner.Range);

        cmd.Undo(doc);
        Assert.Equal(before, obj.Spawner);
    }
}
