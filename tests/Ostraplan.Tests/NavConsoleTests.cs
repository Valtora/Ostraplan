using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The nav-console module loadout Ostraplan fits to an otherwise-empty console (a built console is a bare frame —
/// its interface is separate module items). A wrong or short list spawns a blank/undrivable console, so the set is
/// pinned here against the game's own stock set. Data-driven detection (the <c>IsNavStation</c> cond) and the
/// import-time stocking are exercised too. No install.
/// </summary>
public class NavConsoleTests
{
    [Fact]
    public void IsConsole_detects_the_nav_station_cond_and_ignores_others()
    {
        var console = new Fixtures().Part("Nav", startingConds: ["IsNavStation"]).Get("Nav");
        var wall = new Fixtures().Wall("W").Get("W");

        Assert.True(NavConsole.IsConsole(console));
        Assert.False(NavConsole.IsConsole(wall));
        Assert.False(NavConsole.IsConsole((PartDef?)null));
    }

    [Fact]
    public void Standard_modules_are_the_games_stock_pod_set_then_the_situational_pair()
    {
        // data/loot ItmNavStationModsPod — the set more core consoles carry than any other, and the one that tiles
        // the console screen — then the two situational modules, last because the order decides who keeps a slot.
        string[] pod =
        [
            "ItmNavModControlToggle", "ItmNavModMap", "ItmNavModControls", "ItmNavModDiagnostics",
            "ItmNavModDisplayControls", "ItmNavModEngineMode", "ItmNavModWarnings", "ItmNavModReserves",
            "ItmNavModSensorsMFD", "ItmNavModTransponder", "ItmNavModTimeZoom", "ItmNavModTargetData",
            "ItmNavModMooringControl",
        ];
        string[] expected = [.. pod, "ItmNavModCoursePlot", "ItmNavModFlightDynamics"];
        var mods = NavConsole.StandardModules;

        Assert.Equal(expected, mods.ToArray());
        Assert.Equal(mods.Count, mods.Distinct().Count());          // no accidental duplicate
        Assert.All(mods, m => Assert.StartsWith("ItmNavMod", m));   // every entry is a nav module def
    }

    [Fact]
    public void Standard_modules_are_drivable_but_carry_no_weapons()
    {
        var mods = NavConsole.StandardModules;
        // the modules that make a console actually flyable, and the mooring page a docking ship needs
        Assert.Contains("ItmNavModControls", mods);
        Assert.Contains("ItmNavModCoursePlot", mods);
        Assert.Contains("ItmNavModSensorsMFD", mods);
        Assert.Contains("ItmNavModMooringControl", mods);
        // deliberately no combat/weapon modules in the exported set
        Assert.DoesNotContain(mods, m =>
            m.Contains("Weapon") || m.Contains("Combat") || m.Contains("Turret") || m.Contains("Gun"));
    }

    // ---- the screen arrangement ----

    [SkippableFact]
    public void The_stock_thirteen_hold_their_game_positions_and_the_situational_pair_trays()
    {
        // Real data: the game's own NavModConfig tiles the screen exactly for the pod set, leaving one 0.15×0.4
        // gap that neither remaining module fits (course plot is 0.25×0.4, flight dynamics 0.25×0.2). Both are
        // aboard; the game's answer to "no room" is the console's edit-menu tray, and so is ours.
        var g = TestData.RequireGame();
        if (g.Catalog.Lookup("ItmStationNav") is not { } console || !NavConsole.IsConsole(console)) return;

        var slots = NavConsole.Arrange(g.Catalog, console, NavConsole.StandardModules);

        Assert.Equal(NavConsole.StandardModules.Count, slots.Count);
        Assert.Equal(["ItmNavModCoursePlot", "ItmNavModFlightDynamics"],
                     slots.Where(s => !s.OnScreen).Select(s => s.DefName).ToArray());
        Assert.All(slots.Where(s => s.OnScreen), s => Assert.Matches(@"^\d\.\d\d\|\d\.\d\d\|\d\.\d\d\|\d\.\d\d$", s.Pos!));
        // the ones that stay are at the game's own rects, keyed the way NavModConfig keys them
        Assert.Equal("0.25|0.00|0.65|0.80", slots.Single(s => s.DefName == "ItmNavModMap").Pos);
        Assert.Equal("NavModMooringControl", slots.Single(s => s.DefName == "ItmNavModMooringControl").Key);
        Assert.Equal("0.65|0.40|0.90|0.60", slots.Single(s => s.DefName == "ItmNavModMooringControl").Pos);
    }

    [Fact]
    public void A_module_whose_slot_is_taken_goes_to_the_tray_and_is_written_empty()
    {
        // Two modules sharing a rect is the game's own shape (mooring and flight dynamics are one slot): whichever
        // is considered first keeps it, and the loser is DisableMod()'d rather than drawn on top.
        var cat = new Fixtures()
            .Part("Nav", startingConds: ["IsNavStation", "IsContainer"], container: (5, 4), gpm: [("NavModConfig", "NavModConfig")])
            .GpmTemplate("NavModConfig", "NavModA", "0|0|0.5|1", "NavModB", "0|0|0.5|1", "NavModC", "0.5|0|1|1")
            .Part("ItmNavModA", gpm: [("NavMod", "NavModA")]).GpmTemplate("NavModA", "strGUIPrefab", "NavModA", "strDefaultPos", "0|0|0.5|1")
            .Part("ItmNavModB", gpm: [("NavMod", "NavModB")]).GpmTemplate("NavModB", "strGUIPrefab", "NavModB", "strDefaultPos", "0|0|0.5|1")
            .Part("ItmNavModC", gpm: [("NavMod", "NavModC")]).GpmTemplate("NavModC", "strGUIPrefab", "NavModC", "strDefaultPos", "0.5|0|1|1")
            .Build();
        string[] mods = ["ItmNavModA", "ItmNavModB", "ItmNavModC"];

        var slots = NavConsole.Arrange(cat, cat.Lookup("Nav")!, mods);

        Assert.Equal("0.00|0.00|0.50|1.00", slots[0].Pos);   // A takes the shared slot: it was considered first
        Assert.Null(slots[1].Pos);                           // B loses it and waits in the tray
        Assert.Equal("0.50|0.00|1.00|1.00", slots[2].Pos);   // C is elsewhere and unaffected
        // and that is exactly what gets baked, tray entries included (the shape SaveModules writes)
        Assert.Equal([("NavModA", "0.00|0.00|0.50|1.00"), ("NavModB", ""), ("NavModC", "0.50|0.00|1.00|1.00")],
                     NavConsole.ConfigEntries(cat, cat.Lookup("Nav")!, mods).ToArray());
    }

    [Fact]
    public void An_off_screen_or_unparseable_rect_is_treated_as_no_place()
    {
        var cat = new Fixtures()
            .Part("Nav", startingConds: ["IsNavStation", "IsContainer"], container: (5, 4), gpm: [("NavModConfig", "NavModConfig")])
            .GpmTemplate("NavModConfig", "NavModOff", "0.5|0.5|1.5|1", "NavModJunk", "not|a|rect|at all")
            .Part("ItmNavModOff", gpm: [("NavMod", "NavModOff")]).GpmTemplate("NavModOff", "strGUIPrefab", "NavModOff")
            .Part("ItmNavModJunk", gpm: [("NavMod", "NavModJunk")]).GpmTemplate("NavModJunk", "strGUIPrefab", "NavModJunk")
            .Part("ItmNavModNone")   // no NavMod prop map at all
            .Build();

        var slots = NavConsole.Arrange(cat, cat.Lookup("Nav")!, ["ItmNavModOff", "ItmNavModJunk", "ItmNavModNone"]);

        Assert.All(slots, s => Assert.False(s.OnScreen));
    }

    [Fact]
    public void A_stored_arrangement_wins_over_the_defaults_and_can_shelve_a_module()
    {
        var cat = Catalog();
        var def = cat.Lookup("Nav")!;
        string[] mods = ["ItmNavModMap", "ItmNavModControls", "ItmNavModWarnings"];
        var stored = new Dictionary<string, string>(System.StringComparer.Ordinal)
        {
            ["NavModMap"] = "0.00|0.00|0.30|0.50",   // the user put the map here
            ["NavModControls"] = "",                 // and shelved the controls
            // NavModWarnings is not mentioned: it keeps the arrangement the game would give it
        };

        var slots = NavConsole.Arrange(cat, def, mods, stored);

        Assert.Equal("0.00|0.00|0.30|0.50", slots[0].Pos);
        Assert.Null(slots[1].Pos);
        Assert.Equal(NavConsole.Arrange(cat, def, mods)[2].Pos, slots[2].Pos);   // untouched, so unchanged
    }

    [Fact]
    public void A_stored_arrangement_still_obeys_the_games_fit_rules()
    {
        // a hand-edited .oplan can name a rect off the screen or on top of another; the game would shelve the
        // loser on load, so the reader does too rather than trusting the file
        var cat = Catalog();
        var def = cat.Lookup("Nav")!;
        var stored = new Dictionary<string, string>(System.StringComparer.Ordinal)
        {
            ["NavModMap"] = "0.00|0.00|0.50|0.50",
            ["NavModControls"] = "0.25|0.25|0.75|0.75",   // overlaps the map
            ["NavModWarnings"] = "0.90|0.90|1.40|1.40",   // off the screen
        };

        var slots = NavConsole.Arrange(cat, def, ["ItmNavModMap", "ItmNavModControls", "ItmNavModWarnings"], stored);

        Assert.Equal("0.00|0.00|0.50|0.50", slots[0].Pos);
        Assert.Null(slots[1].Pos);
        Assert.Null(slots[2].Pos);
    }

    [SkippableFact]
    public void An_arranged_console_exports_the_users_layout_not_the_stock_one()
    {
        var g = TestData.RequireGame();
        if (g.Catalog.Lookup("ItmStationNav") is not { } consoleDef || !NavConsole.IsConsole(consoleDef)) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);
        var doc = new ShipDocument(g.Catalog);
        var console = Fixtures.Place(doc, "ItmStationNav", 0, 0);
        NavConsole.StockEmptyConsoles(doc, g.Catalog);

        // the user swaps the two situational modules onto the screen and shelves the map to make room
        new SetNavLayoutCommand(console, null, new Dictionary<string, string>(System.StringComparer.Ordinal)
        {
            ["NavModMap"] = "",
            ["NavModCoursePlot"] = "0.25|0.00|0.50|0.40",
            ["NavModFlightDynamics"] = "0.25|0.40|0.50|0.60",
        }).Do(doc);

        var (exported, _, _) = ShipExport.Build(doc, g.Catalog, specs, "NavArrangeTest");
        var panel = Assert.Single(
            exported.AItems.First(i => i.StrName == "ItmStationNav").AGPMSettings ?? [], p => p.StrName == "NavModConfig");
        var flat = panel.DictGUIPropMap.Select(x => x as string).ToList();
        string? Pos(string key) => flat.IndexOf(key) is var i && i >= 0 ? flat[i + 1] : null;

        Assert.Equal("", Pos("NavModMap"));                             // shelved by the user
        Assert.Equal("0.25|0.00|0.50|0.40", Pos("NavModCoursePlot"));   // and these two put on screen
        Assert.Equal("0.25|0.40|0.50|0.60", Pos("NavModFlightDynamics"));
        Assert.Equal("0.65|0.40|0.90|0.60", Pos("NavModMooringControl"));   // untouched: still the game's own rect
    }

    [Fact]
    public void Rects_round_to_the_games_two_decimals_and_keep_their_size()
    {
        var r = new NavConsole.NavRect(0.65, 0.40, 0.90, 0.60);

        var moved = r.MovedTo(0.123456, 0.787654);

        Assert.Equal("0.12|0.79|0.37|0.99", NavConsole.FormatRect(moved));
        Assert.Equal(r.W, moved.W, 6);   // a drag moves a panel, it never resizes one
        Assert.Equal(r.H, moved.H, 6);
    }

    [Fact]
    public void Fit_is_the_games_test_so_touching_edges_are_fine()
    {
        var left = new NavConsole.NavRect(0, 0, 0.5, 1);

        Assert.True(NavConsole.RectFits(new NavConsole.NavRect(0.5, 0, 1, 1), [left]));       // shares an edge
        Assert.False(NavConsole.RectFits(new NavConsole.NavRect(0.49, 0, 1, 1), [left]));     // overlaps by a hair
        Assert.False(NavConsole.RectFits(new NavConsole.NavRect(0.5, 0, 1.01, 1), [left]));   // off the screen
        Assert.Null(NavConsole.ParseRect("0.5|0|1.01|1"));                                    // and unparseable as a rect
        Assert.Null(NavConsole.ParseRect(""));                                                // the shelved marker
    }

    [Fact]
    public void A_panel_butted_against_its_neighbour_fits_even_when_the_arithmetic_says_otherwise()
    {
        // The game does this in float32, where 0.05f + 0.10f is 0.15f exactly. In double it is
        // 0.15000000000000002 — a hair past a neighbour starting at 0.15 — so a drop the game accepts read as
        // an overlap and the arrange dialog tinted visibly clear screen red (reported against 1.7.1).
        var neighbour = new NavConsole.NavRect(0.15, 0.4, 0.25, 0.8);   // NavModTargetData's own rect
        var flush = new NavConsole.NavRect(0.05, 0.5, 0.05 + (0.25 - 0.15), 0.5 + (0.8 - 0.6));

        Assert.True(flush.X1 > 0.15);   // the artefact itself: the edges are equal, the doubles are not
        Assert.True(NavConsole.RectFits(flush, [neighbour]));
        // and a real overlap, one whole hundredth of the screen, is still refused
        Assert.False(NavConsole.RectFits(flush.MovedTo(0.06, 0.5), [neighbour]));
    }

    [Fact]
    public void A_console_carrying_the_defs_own_config_is_not_treated_as_arranged()
    {
        // Every console in the core data/ships files carries NavModConfig as a verbatim copy of its def's, because
        // that is what the item spawns with. Storing that would put a redundant map on every imported console and
        // make the save write-back stamp it over one it should leave alone.
        var cat = Catalog();
        var def = cat.Lookup("Nav")!;
        var asSpawned = new Dictionary<string, string>(System.StringComparer.Ordinal)
        {
            ["NavModMap"] = Slice(1, NavConsole.StandardModules.Count),
            ["NavModControls"] = Slice(2, NavConsole.StandardModules.Count),
        };

        Assert.Null(NavConsole.StoredLayout(cat, def, asSpawned));
        Assert.Null(NavConsole.StoredLayout(cat, def, new Dictionary<string, string>()));
        Assert.Null(NavConsole.StoredLayout(cat, def, null));
    }

    [Fact]
    public void A_console_somebody_arranged_in_game_keeps_the_arrangement_it_carries()
    {
        var cat = Catalog();
        var def = cat.Lookup("Nav")!;
        // what SaveModules writes when a player closes the console: every key blanked, the active ones re-anchored
        var arranged = new Dictionary<string, string>(System.StringComparer.Ordinal)
        {
            ["NavModMap"] = "0.00|0.00|0.30|0.50",
            ["NavModControls"] = "",
        };

        Assert.Same(arranged, NavConsole.StoredLayout(cat, def, arranged));
    }

    [SkippableFact]
    public void A_screen_arranged_in_game_survives_an_import()
    {
        // The console GUI writes SaveModules on close, so a console the player has sat at holds their screen in
        // its own NavModConfig. Dropping it left the arrange dialog showing a recomputed stock layout for a ship
        // that had been arranged differently, which is what made the planner and the game disagree (RedTwinkleToes,
        // 1.7.1): Diagnostics on the wrong side, the tomographer trayed, and a strip of screen reading as free.
        var g = TestData.RequireGame();
        Skip.If(g.Catalog.Lookup("ItmStationNav") is not { } d || !NavConsole.IsConsole(d));

        var ship = ShipTemplate.ParseFile("""
            [{
              "strName": "TestShip", "nCols": 4, "nRows": 4,
              "vShipPos": { "x": 0.0, "y": 0.0 },
              "aItems": [
                { "strName": "ItmStationNav", "fX": 0.0, "fY": 0.0, "fRotation": 0.0, "strID": "nav",
                  "aGPMSettings": [ { "strName": "NavModConfig", "dictGUIPropMap": [
                      "NavModDiagnostics", "0.00|0.40|0.10|0.80",
                      "NavModDuhrt", "0.10|0.40|0.20|0.60",
                      "NavModMap", "" ] } ] }
              ]
            }]
            """).Single();

        Assert.Equal("0.00|0.40|0.10|0.80", ship.Items[0].NavLayout!["NavModDiagnostics"]);

        var console = TemplateImport.FromTemplate(ship, g.Catalog).Doc.Placements
            .Single(p => p.DefName == "ItmStationNav");
        Assert.Equal("0.00|0.40|0.10|0.80", console.NavLayout!["NavModDiagnostics"]);
        Assert.Equal("", console.NavLayout["NavModMap"]);   // "" is the game's own shelved marker, not a gap

        // and the arrangement the planner computes from it is the ship's, not the stock one
        var slots = NavConsole.Arrange(g.Catalog, g.Catalog.Lookup("ItmStationNav")!,
            ["ItmNavModDiagnostics", "ItmNavModDuhrt", "ItmNavModMap"], console.NavLayout);
        Assert.Equal("0.00|0.40|0.10|0.80", slots[0].Pos);
        Assert.Equal("0.10|0.40|0.20|0.60", slots[1].Pos);   // on screen, where stock would have shelved it
        Assert.Null(slots[2].Pos);
    }

    // ---- stocking an empty console ----

    /// <summary>A catalog with a 5×4 nav console (the real <c>ItmStationNav</c> grid), every standard module as a
    /// 1×1 loose item with its own screen slot (a column each, so the whole set fits and nothing trays), and a
    /// plain locker to prove the fill is console-only.</summary>
    private static Catalog Catalog(int gridW = 5, int gridH = 4)
    {
        var f = new Fixtures().Part("Nav", startingConds: ["IsNavStation", "IsContainer", "IsInstalled"],
            container: (gridW, gridH), category: "CTRL", gpm: [("NavModConfig", "NavModConfig")]);
        var n = NavConsole.StandardModules.Count;
        var config = new List<string>();
        for (var i = 0; i < n; i++)
        {
            var def = NavConsole.StandardModules[i];
            var key = def["Itm".Length..];                    // ItmNavModMap -> NavModMap, as the game keys it
            var pos = Slice(i, n);
            f.Part(def, gpm: [("NavMod", key)]);
            f.GpmTemplate(key, "strGUIPrefab", key, "strDefaultPos", pos);
            config.Add(key);
            config.Add(pos);
        }
        f.GpmTemplate("NavModConfig", [.. config]);
        f.Container("Locker");
        return f.Build();
    }

    /// <summary>The i-th of n side-by-side full-height screen slots, as the game's anchor-rect string.</summary>
    private static string Slice(int i, int n) => string.Format(
        System.Globalization.CultureInfo.InvariantCulture, "{0:f2}|0|{1:f2}|1", (double)i / n, (i + 1.0) / n);

    [Fact]
    public void An_empty_console_is_stocked_with_the_whole_set_as_authored_cargo()
    {
        var cat = Catalog();
        var console = Fixtures.P("Nav", 0, 0);
        var doc = Fixtures.Doc(cat, console);

        var (consoles, modules, trayed) = NavConsole.StockEmptyConsoles(doc, cat);

        Assert.Equal(1, consoles);
        Assert.Equal(NavConsole.StandardModules.Count, modules);
        Assert.Equal(0, trayed);   // this console's screen has a slot for every one of them
        Assert.Equal(NavConsole.StandardModules.OrderBy(x => x, System.StringComparer.Ordinal),
                     console.Cargo.Select(c => c.DefName).OrderBy(x => x, System.StringComparer.Ordinal));
        // authored, so the write-back synthesizes them and the .oplan keeps them
        Assert.All(console.Cargo, c => Assert.True(c.Authored));
        Assert.True(doc.IsCargoEdited(console));
        // laid out on real cells rather than piled at 0,0
        Assert.Equal(console.Cargo.Count, console.Cargo.Select(c => (c.GridX, c.GridY)).Distinct().Count());
    }

    [Fact]
    public void A_console_that_already_carries_something_is_left_alone()
    {
        var cat = Catalog();
        var console = Fixtures.P("Nav", 0, 0);
        var doc = Fixtures.Doc(cat, console);
        console.Cargo = CargoEdit.Add([], null, (5, 4), cat.Lookup("ItmNavModMap")!, 1)!;

        var (consoles, modules, _) = NavConsole.StockEmptyConsoles(doc, cat);

        Assert.Equal((0, 0), (consoles, modules));
        Assert.Single(console.Cargo);   // a part-stripped salvage console stays part-stripped
    }

    [Fact]
    public void A_console_holding_only_its_slotted_data_chip_is_still_stocked()
    {
        // Every core console carries a DataStore in its "data" slot and nothing else, so "holds anything at all"
        // reads them all as stocked — which is how an imported ship exported with a chip and no screens.
        var cat = Catalog();
        var console = Fixtures.P("Nav", 0, 0);
        var doc = Fixtures.Doc(cat, console);
        var chip = new CargoItem("chip-id", "DataStore", "Data Store", Slotted: true, []) { SlotName = "data" };
        console.Cargo = [chip];

        var (consoles, modules, _) = NavConsole.StockEmptyConsoles(doc, cat);

        Assert.Equal(1, consoles);
        Assert.Equal(NavConsole.StandardModules.Count, modules);
        Assert.Contains(console.Cargo, c => c.StrID == "chip-id");   // the chip is kept, not replaced
        Assert.Equal(NavConsole.StandardModules.OrderBy(x => x, System.StringComparer.Ordinal),
                     console.Cargo.Where(c => !c.Slotted).Select(c => c.DefName)
                         .OrderBy(x => x, System.StringComparer.Ordinal));
    }

    [Fact]
    public void Other_containers_are_never_stocked()
    {
        var cat = Catalog();
        var locker = Fixtures.P("Locker", 0, 0);
        var doc = Fixtures.Doc(cat, locker);

        Assert.Equal((0, 0, 0), NavConsole.StockEmptyConsoles(doc, cat));
        Assert.Empty(locker.Cargo);
    }

    [Fact]
    public void A_smaller_console_grid_takes_what_fits_and_stops()
    {
        var cat = Catalog(gridW: 2, gridH: 2);   // a modded console with room for four modules
        var console = Fixtures.P("Nav", 0, 0);
        var doc = Fixtures.Doc(cat, console);

        var (consoles, modules, _) = NavConsole.StockEmptyConsoles(doc, cat);

        Assert.Equal(1, consoles);
        Assert.Equal(4, modules);
        Assert.Equal(4, console.Cargo.Count);
    }

    [Fact]
    public void Modules_missing_from_the_loaded_data_are_skipped_not_fatal()
    {
        // a catalog with the console but only one module def present (the rest are in mods that aren't loaded)
        var cat = new Fixtures()
            .Part("Nav", startingConds: ["IsNavStation", "IsContainer", "IsInstalled"], container: (5, 4))
            .Part("ItmNavModMap")
            .Build();
        var console = Fixtures.P("Nav", 0, 0);
        var doc = Fixtures.Doc(cat, console);

        Assert.Equal((1, 1, 1), NavConsole.StockEmptyConsoles(doc, cat));   // no layout data: it rides in the tray
        Assert.Equal("ItmNavModMap", Assert.Single(console.Cargo).DefName);
    }
}
