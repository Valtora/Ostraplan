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
