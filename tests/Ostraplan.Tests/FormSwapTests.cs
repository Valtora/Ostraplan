using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// "Make Loose Item" / "Install item" — the installed⇄loose form swap (<see cref="FormSwap"/>). The synthetic
/// cases pin the swap mechanics (def change, pose kept, cargo carried, one undo step, eligibility); the
/// install-gated cases prove the real game jobs produce the expected map and — the thing that matters for gas
/// canisters — that a swap conserves a canister's baked contents.
/// </summary>
public class FormSwapTests
{
    // ---- synthetic mechanics (game-free) ----

    private static Catalog PairCatalog() => new Fixtures()
        .Fixture("Fix")                 // an installed fixture (obstruction)
        .Part("FixLoose")               // its loose/packaged form (an inert item)
        .Container("Crate")
        .Container("CrateLoose")
        .FormPair("Fix", "FixLoose")
        .FormPair("Crate", "CrateLoose")
        .Build();

    [Fact]
    public void Make_loose_swaps_def_keeps_pose_and_is_one_undo_step()
    {
        var doc = new ShipDocument(PairCatalog());
        var p = Fixtures.Place(doc, "Fix", 2, 3, 90);

        var swaps = FormSwap.Loosenable(doc, [p]);
        Assert.Equal("FixLoose", Assert.Single(swaps).Target);

        var swap = FormSwap.BuildSwap(doc, swaps);
        Assert.NotNull(swap);

        var changed = 0;
        doc.Changed += () => changed++;
        swap!.Value.Cmd.Do(doc);

        Assert.Equal(1, changed);                            // one coalesced notification for the whole swap
        Assert.DoesNotContain(p, doc.Placements);            // the installed form is gone
        var repl = Assert.Single(swap.Value.New);
        Assert.Equal("FixLoose", repl.DefName);
        Assert.Equal((2, 3, 90), (repl.X, repl.Y, repl.Rot)); // same tile and rotation
        Assert.False(repl.IsGiven);                          // uninstalling is an authoring act → re-checked
    }

    [Fact]
    public void Install_swaps_loose_back_to_installed()
    {
        var doc = new ShipDocument(PairCatalog());
        var p = Fixtures.Place(doc, "FixLoose", 1, 1);

        var swaps = FormSwap.Installable(doc, [p]);
        Assert.Equal("Fix", Assert.Single(swaps).Target);

        var swap = FormSwap.BuildSwap(doc, swaps);
        Assert.NotNull(swap);
        swap!.Value.Cmd.Do(doc);
        Assert.Equal("Fix", Assert.Single(swap.Value.New).DefName);
    }

    [Fact]
    public void Make_loose_carries_cargo_across()
    {
        // Uninstalling a stocked container keeps its contents (a container's two forms share their grid).
        var doc = new ShipDocument(PairCatalog());
        var crate = Fixtures.Place(doc, "Crate", 0, 0);
        var widget = new CargoItem("id-1", "Widget", "Widget", Slotted: false, []);
        crate.Cargo = [widget];

        var swap = FormSwap.BuildSwap(doc, FormSwap.Loosenable(doc, [crate]));
        Assert.NotNull(swap);
        swap!.Value.Cmd.Do(doc);

        var repl = Assert.Single(swap.Value.New);
        Assert.Equal("CrateLoose", repl.DefName);
        Assert.Equal(widget, Assert.Single(repl.Cargo));
    }

    [Fact]
    public void Only_parts_with_a_form_are_offered()
    {
        var cat = new Fixtures().Wall().Fixture("Fix").Part("FixLoose").FormPair("Fix", "FixLoose").Build();
        var doc = new ShipDocument(cat);
        var wall = Fixtures.Place(doc, "Wall", 0, 0);   // no form pair — structure, not a fixture
        var fix = Fixtures.Place(doc, "Fix", 1, 0);

        var loosenable = FormSwap.Loosenable(doc, [wall, fix]);
        Assert.Same(fix, Assert.Single(loosenable).Part);          // only the fixture qualifies
        Assert.Empty(FormSwap.Installable(doc, [wall, fix]));      // neither is a loose form
    }

    // ---- save identity across a swap (issue #19) ----

    [Fact]
    public void Uninstalling_a_save_part_records_the_item_it_came_from()
    {
        var doc = new ShipDocument(PairCatalog());
        var p = new Placement { DefName = "Fix", X = 2, Y = 3, OriginStrID = "save-1" };
        new PlaceCommand(p).Do(doc);

        var swap = FormSwap.BuildSwap(doc, FormSwap.Loosenable(doc, [p]))!.Value;
        swap.Cmd.Do(doc);

        var loose = Assert.Single(swap.New);
        Assert.Null(loose.OriginStrID);                    // def changed → the save's item record can't be reused
        Assert.Equal("save-1", loose.SwappedFromStrID);    // but this is still a part the player owns
        Assert.Equal("Fix", loose.SwappedFromDef);         // and we know what to restore it to
    }

    [Fact]
    public void Re_installing_restores_the_save_identity_so_the_round_trip_is_a_no_op()
    {
        var doc = new ShipDocument(PairCatalog());
        var p = new Placement { DefName = "Fix", X = 2, Y = 3, OriginStrID = "save-1" };
        new PlaceCommand(p).Do(doc);

        var out1 = FormSwap.BuildSwap(doc, FormSwap.Loosenable(doc, [p]))!.Value;
        out1.Cmd.Do(doc);

        var back = FormSwap.BuildSwap(doc, FormSwap.Installable(doc, [out1.New[0]]))!.Value;
        back.Cmd.Do(doc);

        var reinstalled = Assert.Single(back.New);
        Assert.Equal("Fix", reinstalled.DefName);
        Assert.Equal("save-1", reinstalled.OriginStrID);   // same def again → the item record IS reusable
        Assert.Null(reinstalled.SwappedFromStrID);         // nothing outstanding to price
        Assert.Null(reinstalled.SwappedFromDef);
    }

    [Fact]
    public void A_part_with_no_save_identity_carries_no_provenance()
    {
        var doc = new ShipDocument(PairCatalog());
        var p = Fixtures.Place(doc, "Fix", 0, 0);          // authored here, never in a save

        var swap = FormSwap.BuildSwap(doc, FormSwap.Loosenable(doc, [p]))!.Value;
        swap.Cmd.Do(doc);

        var loose = Assert.Single(swap.New);
        Assert.Null(loose.SwappedFromStrID);
        Assert.Null(loose.SwappedFromDef);
    }

    [Fact]
    public void Restate_carries_the_earliest_identity_through_a_chain_of_states()
    {
        // The door toggle uses the same primitive as the form swap, on defs with no form pair at all.
        var closed = new Placement { DefName = "DoorClosed", X = 1, Y = 1, OriginStrID = "save-9" };

        var open = closed.Restate("DoorOpen", 0);
        Assert.Null(open.OriginStrID);
        Assert.Equal("save-9", open.SwappedFromStrID);

        // a third state keeps pointing at the ORIGINAL def, so the way home is never lost
        var ajar = open.Restate("DoorAjar", 0);
        Assert.Equal("save-9", ajar.SwappedFromStrID);
        Assert.Equal("DoorClosed", ajar.SwappedFromDef);

        Assert.Equal("save-9", ajar.Restate("DoorClosed", 0).OriginStrID);
    }

    // ---- real game data (install-gated) ----

    [SkippableFact]
    public void Sink_maps_installed_to_loose_and_back()
    {
        var g = TestData.RequireGame();
        Assert.Equal("ItmSink01Loose", g.Catalog.LooseForm("ItmSink01"));
        Assert.Equal("ItmSink01", g.Catalog.InstalledForm("ItmSink01Loose"));
    }

    [SkippableFact]
    public void Gas_canister_keeps_its_full_charge_across_the_swap()
    {
        // The N2 canister ships full of N2, baked into BOTH the installed and loose defs, so Make Loose Item
        // conserves the gas — nothing is invented or lost. This pins that guarantee.
        var g = TestData.RequireGame();
        Assert.Equal("ItmRTAN2Loose", g.Catalog.LooseForm("ItmRTAN2"));

        var installed = g.Catalog.Lookup("ItmRTAN2");
        var loose = g.Catalog.Lookup("ItmRTAN2Loose");
        Skip.If(installed is null || loose is null, "N2 canister defs not present in this build");

        var installedN2 = installed!.StartingCondValues.GetValueOrDefault("StatGasMolN2");
        var looseN2 = loose!.StartingCondValues.GetValueOrDefault("StatGasMolN2");
        Assert.True(installedN2 > 0);          // full, not an empty shell
        Assert.Equal(installedN2, looseN2);    // identical charge in both forms → conserved by the swap
    }

    [SkippableFact]
    public void Themed_wall_floor_conduit_skins_make_loose_to_their_themed_loose_form()
    {
        // Walls/floors/conduits are placed as cooverlay SKINS whose only uninstall job lives on the base condowner,
        // so "Make Loose Item" was silently unavailable on them. The skin now maps to its OWN themed loose form
        // (via strCOBase + the skin's mapModeSwitches), not the generic base drop.
        var g = TestData.RequireGame();
        Assert.Equal("ItmFloorAERO01Loose", g.Catalog.LooseForm("ItmFloorAERO01"));   // themed floor
        Assert.Equal("ItmWallAERO01Loose", g.Catalog.LooseForm("ItmWallAERO01"));     // themed wall
        Assert.Equal("ItmConduit01Loose", g.Catalog.LooseForm("ItmConduit01"));       // conduit variant

        // the base forms still map to their own base loose (unchanged), and a genuinely already-loose skin has none
        Assert.Equal("ItmFloorGrate01Loose", g.Catalog.LooseForm("ItmFloorGrate01"));
        Assert.Null(g.Catalog.LooseForm("ItmFloorAERO01Loose"));
    }

    [SkippableFact]
    public void End_to_end_make_loose_on_a_themed_floor_skin_preserves_the_theme()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(g.Catalog.ByDefName.ContainsKey("ItmFloorAERO01"), "themed floor not buildable in this build");

        var doc = new ShipDocument(g.Catalog);
        var floor = new Placement { DefName = "ItmFloorAERO01", X = 0, Y = 0 };
        new PlaceCommand(floor).Do(doc);

        var swap = FormSwap.BuildSwap(doc, FormSwap.Loosenable(doc, [floor]));
        Assert.NotNull(swap);
        swap!.Value.Cmd.Do(doc);

        var repl = Assert.Single(swap.Value.New);
        Assert.Equal("ItmFloorAERO01Loose", repl.DefName);   // themed, not the generic ItmFloorGrate01Loose
        Assert.NotNull(g.Catalog.Lookup(repl.DefName));       // resolves → renders and analyses
    }

    [SkippableFact]
    public void Nav_station_and_transponder_map_installed_to_loose_via_the_install_inverse()
    {
        // Issue #9: the Nav Station and Transponder families describe their uninstall drop with strLootOut
        // pointing at a runtime-only "…LooseEmpty"/"…LooseChance" marker that has no condowner, so neither
        // aLootCOs nor strLootOut yields a renderable loose form — "Make Loose Item" was silently missing. The
        // loose form is recovered from the inverse of the (resolvable, round-tripping) install job.
        var g = TestData.RequireGame();
        Assert.Equal("ItmStationNavLoose", g.Catalog.LooseForm("ItmStationNav"));       // the reported Nav Console
        Assert.Equal("ItmStationNav", g.Catalog.InstalledForm("ItmStationNavLoose"));   // and it round-trips
        Assert.Equal("ItmTransponder01Loose", g.Catalog.LooseForm("ItmTransponder01Off"));
    }

    [SkippableFact]
    public void End_to_end_make_loose_on_a_nav_console()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(g.Catalog.ByDefName.ContainsKey("ItmStationNav"), "nav station not buildable in this build");

        var doc = new ShipDocument(g.Catalog);
        var nav = new Placement { DefName = "ItmStationNav", X = 0, Y = 0 };
        new PlaceCommand(nav).Do(doc);

        var swap = FormSwap.BuildSwap(doc, FormSwap.Loosenable(doc, [nav]));
        Assert.NotNull(swap);
        swap!.Value.Cmd.Do(doc);

        var repl = Assert.Single(swap.Value.New);
        Assert.Equal("ItmStationNavLoose", repl.DefName);
        Assert.NotNull(g.Catalog.Lookup(repl.DefName));   // resolves → renders and analyses
    }

    [SkippableFact]
    public void The_fixed_airlock_has_no_loose_form()
    {
        var g = TestData.RequireGame();
        Assert.Null(g.Catalog.LooseForm(Catalog.PrimaryDocksysDef));   // no uninstall job → action never offered
    }

    [SkippableFact]
    public void End_to_end_make_loose_on_a_real_sink()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(g.Catalog.ByDefName.ContainsKey("ItmSink01"), "sink not buildable in this build");

        var doc = new ShipDocument(g.Catalog);
        var sink = new Placement { DefName = "ItmSink01", X = 0, Y = 0 };
        new PlaceCommand(sink).Do(doc);

        var swap = FormSwap.BuildSwap(doc, FormSwap.Loosenable(doc, [sink]));
        Assert.NotNull(swap);
        swap!.Value.Cmd.Do(doc);

        var repl = Assert.Single(swap.Value.New);
        Assert.Equal("ItmSink01Loose", repl.DefName);
        Assert.NotNull(g.Catalog.Lookup(repl.DefName));   // resolves → renders and analyses
    }
}
