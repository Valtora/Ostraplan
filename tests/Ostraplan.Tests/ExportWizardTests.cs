using System.IO;
using System.Windows;
using System.Windows.Controls;
using Ostraplan.App;
using Ostraplan.App.Wizard;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The export wizard's panes and shell. A WPF window cannot be driven headlessly, but its constructor is where a
/// layout mistake lands and its validation is plain state, so building it on an STA thread catches both without a
/// human clicking anything.
///
/// <para>Replaces ExportDialogTests, which pinned the shape of the tabbed dialog the wizard retires.
/// <see cref="WizardFlowTests"/> covers the navigation rules, which need no window at all.</para>
/// </summary>
public class ExportWizardTests
{
    private static WizardSession Session((GameEnv Env, DataIndex Index, Catalog Catalog) g,
        ExportDestination destination = ExportDestination.Mod, string shipName = "Test Ship",
        IReadOnlyList<SaveEntry>? saves = null, SaveSourceRef? sourceSave = null,
        AppSettings? settings = null, bool ostrasortKnown = false, SaveSourceRef? updateTarget = null)
    {
        var doc = new ShipDocument(g.Catalog) { SourceSave = sourceSave };
        new PlaceCommand(new Placement { DefName = "ItmWall1x1", X = 0, Y = 0 }).Do(doc);

        // built the way MainWindow builds it, so a test that supplies settings exercises the real restore path
        var live = settings ?? new AppSettings();
        var meta = new OplanMeta { Name = shipName };
        var plan = ExportPlan.FromSettings(live, meta, sourceSave);
        plan.Destination = destination;

        return new WizardSession
        {
            SourceSave = sourceSave,
            UpdateTarget = updateTarget,
            Plan = plan,
            Doc = doc,
            Catalog = g.Catalog,
            Specs = [],
            Index = g.Index,
            Env = g.Env,
            Settings = live,
            Meta = meta,
            Saves = saves ?? [],
            OstrasortKnown = ostrasortKnown,
        };
    }

    // ---- the shell ----

    [SkippableFact]
    public void The_wizard_opens_on_the_destination_step_with_all_three_offered()
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            var session = Session(g);
            var wizard = new ExportWizard(session);

            var rail = Rail(wizard);
            Assert.Equal(
                ["Destination", "The ship", "Mod details", "Obtainable in game", "Where to write", "Review", "Done"],
                rail);
            Assert.IsType<DestinationStep>(Pane(wizard));
        });
    }

    [SkippableFact]
    public void An_unavailable_destination_is_shown_disabled_rather_than_hidden()
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            // hiding it would teach nobody that it exists, so it stays visible with the reason on it
            var wizard = new ExportWizard(Session(g));

            var tiles = Descendants<RadioButton>(Pane(wizard)!).ToList();
            Assert.Equal(3, tiles.Count);
            Assert.True(tiles[0].IsEnabled);                          // as a mod, always available
            Assert.Contains(tiles, t => !t.IsEnabled);                // the two not wired up in this build
        });
    }

    [SkippableFact]
    public void The_rail_shrinks_when_a_save_destination_is_picked()
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            // the point of choosing the destination first: later steps suit one path instead of all three
            var wizard = new ExportWizard(Session(g, ExportDestination.NewShipInSave));

            Assert.Equal(["Destination", "The ship", "Save & price", "Review", "Done"], Rail(wizard));
        });
    }

    // ---- the ship step ----

    [SkippableFact]
    public void The_ship_step_refuses_a_blank_name_and_says_so_inline()
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            var session = Session(g, shipName: "");
            var step = new ShipStep();
            step.Enter(session);

            var reason = step.Validate();

            Assert.NotNull(reason);
            Assert.Contains("name", reason, StringComparison.OrdinalIgnoreCase);
            // the reason is rendered, not just returned: a refusal the user cannot see is a dead button
            Assert.Contains(Descendants<TextBlock>(step),
                t => t.Visibility == Visibility.Visible && t.Text == reason);
        });
    }

    [SkippableFact]
    public void Wear_defaults_to_the_vanilla_used_condition()
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            var session = Session(g);
            var step = new ShipStep();
            step.Enter(session);
            step.Leave(session);

            Assert.True(session.Plan.Wear.Enabled);
            Assert.Equal(WearModel.VanillaUsedCondition, session.Plan.Wear.TargetCondition, 2);
        });
    }

    [SkippableFact]
    public void Identity_is_editable_when_updating_a_ship_in_a_save()
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            // SaveEdit writes the identity onto the ship on this path, so the boxes have to accept an edit
            var session = Session(g, ExportDestination.UpdateShipInSave);
            var step = new ShipStep();
            step.Enter(session);

            var boxes = Descendants<TextBox>(step).ToList();
            Assert.Equal("Test Ship", boxes[0].Text);        // the design name
            Assert.All(boxes, b => Assert.False(b.IsReadOnly));
            Assert.All(boxes, b => Assert.True(b.IsEnabled));
        });
    }

    [SkippableFact]
    public void Identity_edited_when_updating_a_ship_in_a_save_flows_back_onto_the_plan()
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            var session = Session(g, ExportDestination.UpdateShipInSave);
            var step = new ShipStep();
            step.Enter(session);
            // 0 name, 1 in-game name, 2 make, 3 model
            Descendants<TextBox>(step).ToList()[3].Text = "Ibex";

            step.Leave(session);

            Assert.Equal("Ibex", session.Plan.Identity.Model);
        });
    }

    [SkippableFact]
    public void Identity_edited_in_the_wizard_flows_back_onto_the_plan()
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            var session = Session(g);
            var step = new ShipStep();
            step.Enter(session);
            Descendants<TextBox>(step).ToList()[1].Text = "Kestrel";   // the in-game name

            step.Leave(session);

            Assert.Equal("Kestrel", session.Plan.Identity.PublicName);
        });
    }

    // ---- the mod steps ----

    [SkippableFact]
    public void The_write_target_step_refuses_a_folder_export_with_no_folder_chosen()
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            var session = Session(g);
            session.Plan.Mod.StagedIntoMods = false;
            var step = new ModTargetStep();
            step.Enter(session);

            var reason = step.Validate();

            Assert.NotNull(reason);
            Assert.Contains("folder", reason, StringComparison.OrdinalIgnoreCase);
        });
    }

    [SkippableFact]
    public void The_mod_details_step_refuses_a_replacement_with_no_ship_picked()
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            var session = Session(g, shipName: "Nothing Named This");
            var step = new ModDetailsStep();
            step.Enter(session);
            Descendants<CheckBox>(step).First().IsChecked = true;      // "replace an existing ship"
            Descendants<ComboBox>(step).First().SelectedItem = null;

            var reason = step.Validate();

            Assert.NotNull(reason);
            Assert.Contains("replace", reason, StringComparison.OrdinalIgnoreCase);
        });
    }

    [SkippableFact]
    public void The_mod_name_follows_the_ship_name_until_the_user_edits_it()
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            var session = Session(g, shipName: "Kestrel");
            var step = new ModDetailsStep();
            step.Enter(session);

            step.Leave(session);
            Assert.Equal("", session.Plan.Mod.ModName);                // blank = still following the ship name

            Descendants<TextBox>(step).First().Text = "My Own Mod";
            step.Leave(session);
            Assert.Equal("My Own Mod", session.Plan.Mod.ModName);      // a user edit sticks
        });
    }

    // ---- the save & price step ----

    [SkippableFact]
    public void A_grant_is_a_gift_until_the_user_asks_to_be_charged()
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            var session = Session(g, ExportDestination.NewShipInSave);
            session.Driver = new NewShipDriver();
            var step = new SavePriceStep();
            step.Enter(session);

            step.Leave(session);

            Assert.False(session.Plan.NewShip.Charge);
            Assert.Equal(0, session.Plan.NewShip.Price);
        });
    }

    [SkippableFact]
    public void The_save_step_blocks_until_a_save_is_picked()
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            var session = Session(g, ExportDestination.NewShipInSave);
            session.Driver = new NewShipDriver();
            var step = new SavePriceStep();
            step.Enter(session);

            var reason = step.Validate();

            Assert.NotNull(reason);
            Assert.Contains("save", reason, StringComparison.OrdinalIgnoreCase);
        });
    }

    [SkippableFact]
    public void The_new_ship_destination_is_offered_only_when_saves_exist()
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            var driver = new NewShipDriver();

            // shown but disabled with the reason, rather than hidden
            Assert.Equal("No save games found.", driver.Unavailable(Session(g)));
            Assert.Null(driver.Unavailable(
                Session(g, saves: [new SaveEntry("A save", "Ship", "Player", "now", @"C:\nope\a\a.zip")])));
        });
    }

    [SkippableFact]
    public void An_unreadable_save_reports_itself_instead_of_arming_the_commit()
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            // a save entry pointing at nothing: the failure has to surface here, not after the user has committed
            var bogus = new SaveEntry("Ghost Save", "Ship", "Player", "now", @"C:\nope\ghost\ghost.zip");
            var session = Session(g, ExportDestination.NewShipInSave);
            var driver = new NewShipDriver();
            session.Driver = driver;

            var reason = driver.UseSaveAsync(session, bogus).GetAwaiter().GetResult();

            Assert.NotNull(reason);
            Assert.Null(driver.Context);
        });
    }

    // ---- what an export requires ----

    [SkippableFact]
    public void A_mod_nothing_can_spawn_is_refused()
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            // with no kiosk, Special Offer, start or derelict field, the mod writes a ship file the game will
            // never place, which is the commonest first-time mistake and invisible until it fails in game
            var session = Session(g);
            var step = new ObtainableStep();
            step.Populate(session);

            var reason = step.Validate();

            Assert.NotNull(reason);
            Assert.Contains("at least one way", reason);
        });
    }

    [SkippableTheory]
    [InlineData(0)]   // a broker kiosk
    [InlineData(1)]   // a Special Offer slot
    public void Any_single_route_satisfies_the_requirement(int checkbox)
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            var session = Session(g);
            var step = new ObtainableStep();
            step.Populate(session);

            Descendants<CheckBox>(step).ToList()[checkbox].IsChecked = true;

            Assert.Null(step.Validate());
        });
    }

    [SkippableFact]
    public void A_derelict_field_on_its_own_is_a_route()
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            var session = Session(g);
            var step = new ObtainableStep();
            step.Populate(session);

            Descendants<CheckBox>(step).First(c => (string)c.Content == "Small").IsChecked = true;
            step.Leave(session);

            Assert.Null(step.Validate());
            Assert.Equal(["RandomDerelictSmall"], session.Plan.Mod.DerelictPools);
        });
    }

    [SkippableFact]
    public void A_derelict_only_export_leaves_the_wear_to_the_game()
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            // the spawner damages a wreck on load, so baking more on top double-damages every part
            var session = Session(g);
            Assert.True(session.Plan.Wear.Enabled);
            var step = new ObtainableStep();
            step.Populate(session);
            Descendants<CheckBox>(step).First(c => (string)c.Content == "Big").IsChecked = true;

            step.Leave(session);

            Assert.False(session.Plan.Wear.Enabled);
        });
    }

    [SkippableFact]
    public void A_wear_setting_the_user_chose_survives_a_derelict_export()
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            var session = Session(g);
            session.Plan.WearChosen = true;   // the user moved the slider themselves
            var step = new ObtainableStep();
            step.Populate(session);
            Descendants<CheckBox>(step).First(c => (string)c.Content == "Big").IsChecked = true;

            step.Leave(session);

            Assert.True(session.Plan.Wear.Enabled);
        });
    }

    [SkippableFact]
    public void The_advanced_escape_hatch_satisfies_the_requirement()
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            // a bare ship file is a real output for a modpack; it just has to be asked for rather than forgotten
            var session = Session(g);
            var step = new ObtainableStep();
            step.Populate(session);
            Assert.NotNull(step.Validate());

            NoRouteBox(step).IsChecked = true;

            Assert.Null(step.Validate());
            step.Leave(session);
            Assert.True(session.Plan.Mod.NoDeliveryRoute);
        });
    }

    [SkippableFact]
    public void The_advanced_section_opens_on_an_empty_step_and_stays_shut_on_a_busy_one()
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            var empty = new ObtainableStep();
            empty.Populate(Session(g));
            Assert.True(Descendants<Expander>(empty).Single().IsExpanded);

            var busy = Session(g);
            busy.Plan.Mod.BrokerPools = ["RandomShipBrokerOKLG"];
            var step = new ObtainableStep();
            step.Populate(busy);
            Assert.False(Descendants<Expander>(step).Single().IsExpanded);
        });
    }

    [SkippableFact]
    public void A_real_route_overrides_the_escape_hatch()
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            // "no route" while a route is ticked is a contradiction, so the route wins and the hatch clears
            var session = Session(g);
            var step = new ObtainableStep();
            step.Populate(session);
            var hatch = NoRouteBox(step);
            hatch.IsChecked = true;

            Descendants<CheckBox>(step).First(c => c.Content is string s && s.StartsWith("OKLG")).IsChecked = true;

            Assert.False(hatch.IsChecked);
            Assert.False(hatch.IsEnabled);
            step.Leave(session);
            Assert.False(session.Plan.Mod.NoDeliveryRoute);
            Assert.Equal(["RandomShipBrokerOKLG"], session.Plan.Mod.BrokerPools);
        });
    }

    private static CheckBox NoRouteBox(ObtainableStep step) =>
        Descendants<CheckBox>(step).Single(c =>
            c.Content is TextBlock t && t.Text.StartsWith("No route", StringComparison.Ordinal));

    [SkippableFact]
    public void A_blocking_design_problem_reaches_Review_as_an_acknowledgement()
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            // one wall is not a ship: ProblemScan rates "no docking port" blocking, and until now the wizard
            // never mentioned it
            var session = Session(g);
            session.Plan.Mod.BrokerPools = ["RandomShipBrokerOKLG"];

            var outcome = new ModDriver().ReviewAsync(session).GetAwaiter().GetResult();

            Assert.Contains(outcome.Acknowledgements, a => a.StartsWith("No docking port"));
        });
    }

    // ---- what Review actually builds ----

    /// <summary>
    /// Review runs the real engine, so what it reports is what the write produces rather than a restatement of the
    /// settings. Driven directly rather than through the wizard: a bare STA thread has no
    /// <see cref="SynchronizationContext"/>, so awaiting the shell's navigation would resume UI work on the thread
    /// pool. See <see cref="UiThreadGuardTests"/>.
    /// </summary>
    [SkippableFact]
    public void The_mod_review_reports_the_real_build_and_the_real_target()
    {
        var g = TestData.RequireGame();
        var dir = Path.Combine(Path.GetTempPath(), "OstraplanWizardTest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            RunSta(() =>
            {
                var session = Session(g, shipName: "Kestrel");
                session.Plan.Mod.StagedIntoMods = false;
                session.Plan.Mod.Folder = dir;
                var driver = new ModDriver();

                var outcome = driver.BuildAsync(session).GetAwaiter().GetResult();

                Assert.Contains(outcome.Facts, f => f.Label == "Rating");
                Assert.Contains(outcome.Facts, f => f.Label == "Writes to" && f.Value == Path.Combine(dir, "Kestrel"));
                Assert.Empty(outcome.Acknowledgements);   // nothing to overwrite yet
            });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [SkippableFact]
    public void An_existing_mod_folder_becomes_an_acknowledgement_rather_than_a_popup()
    {
        var g = TestData.RequireGame();
        var dir = Path.Combine(Path.GetTempPath(), "OstraplanWizardTest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "Kestrel"));
        File.WriteAllText(Path.Combine(dir, "Kestrel", "mod_info.json"), "{}");
        try
        {
            RunSta(() =>
            {
                var session = Session(g, shipName: "Kestrel");
                session.Plan.Mod.StagedIntoMods = false;
                session.Plan.Mod.Folder = dir;

                var outcome = new ModDriver().BuildAsync(session).GetAwaiter().GetResult();

                var ack = Assert.Single(outcome.Acknowledgements);
                Assert.Contains("already exists", ack);
            });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [SkippableFact]
    public void The_overwrite_check_follows_a_customised_mod_name_not_the_ship_name()
    {
        var g = TestData.RequireGame();
        var dir = Path.Combine(Path.GetTempPath(), "OstraplanWizardTest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            RunSta(() =>
            {
                // the dialog this replaces checked SanitizeName(shipName), which is not where the export writes
                // once the mod has been renamed, so its warning watched a folder nothing was going to touch
                var session = Session(g, shipName: "Kestrel");
                session.Plan.Mod.StagedIntoMods = false;
                session.Plan.Mod.Folder = dir;
                session.Plan.Mod.ModName = "Valtora Fleet Pack";

                var outcome = new ModDriver().BuildAsync(session).GetAwaiter().GetResult();

                Assert.Contains(outcome.Facts,
                    f => f.Label == "Writes to" && f.Value == Path.Combine(dir, "Valtora Fleet Pack"));
            });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- the update destination ----

    /// <summary>A save entry that names nothing on disk — enough for the availability rules, which only count them.</summary>
    private static SaveEntry FakeSave(string name = "Some Save") =>
        new(name, "Kestrel", "A Pilot", "2026-01-01 00:00:00", $@"C:\nowhere\{name}.zip");

    [SkippableFact]
    public void The_update_destination_needs_a_save_to_write_into()
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            var driver = new UpdateDriver();

            var reason = driver.Unavailable(Session(g));

            Assert.NotNull(reason);
            Assert.Contains("No save games found", reason);
        });
    }

    [SkippableFact]
    public void The_update_destination_is_available_for_a_save_linked_design()
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            var session = Session(g, ExportDestination.UpdateShipInSave, saves: [FakeSave()],
                sourceSave: new SaveSourceRef("Some Save", "H-ABC"));

            Assert.Null(new UpdateDriver().Unavailable(session));
        });
    }

    /// <summary>
    /// A design that never came from a save is no longer turned away: it is asked which ship to replace. That is
    /// what lets a stock template be moved onto a live ship, which cannot otherwise be done without redrawing the
    /// whole layout by hand.
    /// </summary>
    [SkippableFact]
    public void The_update_destination_is_available_to_a_design_with_no_source_save()
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            var session = Session(g, ExportDestination.UpdateShipInSave, saves: [FakeSave()]);

            Assert.Null(session.SourceSave);
            Assert.Null(new UpdateDriver().Unavailable(session));
        });
    }

    /// <summary>
    /// A target chosen before the wizard opened is used as-is. The picker has to run before the wizard exists —
    /// cancelling it must abandon the action rather than open a window onto a step that only says why it cannot
    /// continue — so the answer arrives on the session, and preparing must not ask again.
    /// </summary>
    [SkippableFact]
    public void A_target_chosen_before_the_wizard_opened_is_used_without_asking_again()
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            // The save named is not on disk, so preparing fails at the relocate. That failure IS the assertion:
            // reaching it proves the driver took the pre-chosen target instead of trying to show a picker, which
            // would block this test forever on a modal dialog.
            var session = Session(g, ExportDestination.UpdateShipInSave, saves: [FakeSave("Elsewhere")],
                updateTarget: new SaveSourceRef("Elsewhere", "H-ABC"));
            var driver = new UpdateDriver();
            session.Driver = driver;

            var reason = driver.PrepareAsync(session).GetAwaiter().GetResult();

            Assert.NotNull(reason);
            Assert.Contains("Couldn't re-locate the ship in that save", reason);
        });
    }

    /// <summary>
    /// A context cached from an earlier rebind must not stand in for the ship the user just picked. Both live on
    /// the session at once — the main window keeps the first context it sees for the rest of the run — so without
    /// the identity check the design would be written over whichever ship was chosen first.
    /// </summary>
    [SkippableFact]
    public void A_cached_context_for_another_ship_does_not_override_the_chosen_target()
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            var session = Session(g, ExportDestination.UpdateShipInSave, saves: [FakeSave("Elsewhere")],
                updateTarget: new SaveSourceRef("Elsewhere", "H-ABC"));
            session.SaveContext = new SaveShipContext
            {
                Source = new SaveSourceRef("An Earlier Save", "H-OLD"),
                ZipPath = @"C:\nope\earlier\earlier.zip",
                ShipRecord = new System.Text.Json.Nodes.JsonObject(),
                Origins = new Dictionary<string, OriginPart>(),
                ItemsById = new Dictionary<string, System.Text.Json.Nodes.JsonNode>(),
                CosById = new Dictionary<string, System.Text.Json.Nodes.JsonNode>(),
            };
            var driver = new UpdateDriver();
            session.Driver = driver;

            var reason = driver.PrepareAsync(session).GetAwaiter().GetResult();

            // it went to relocate the chosen ship rather than settling for the cached one
            Assert.NotNull(reason);
            Assert.Contains("Couldn't re-locate the ship in that save", reason);
            Assert.Null(driver.Context);
        });
    }

    [SkippableFact]
    public void A_source_save_that_has_gone_blocks_with_the_reason_rather_than_failing_at_the_write()
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            var session = Session(g, ExportDestination.UpdateShipInSave, saves: [FakeSave()],
                sourceSave: new SaveSourceRef("A Save That Is Not There", "H-ABC"));
            var driver = new UpdateDriver();
            session.Driver = driver;

            var reason = driver.PrepareAsync(session).GetAwaiter().GetResult();

            Assert.NotNull(reason);
            Assert.Contains("no longer in your Saves folder", reason);
            Assert.Null(driver.Context);
        });
    }

    [SkippableFact]
    public void The_update_rail_carries_a_missing_parts_step_only_when_there_is_something_to_resolve()
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            var session = Session(g, ExportDestination.UpdateShipInSave,
                sourceSave: new SaveSourceRef("Some Save", "H-ABC"));

            // no save context located, so nothing is known to be unresolved
            var wizard = new ExportWizard(session, ExportDestination.UpdateShipInSave);

            Assert.Equal(
                ["Destination", "The ship", "Write target & cost", "Review", "Done"],
                Rail(wizard));
        });
    }

    [SkippableFact]
    public void A_preselected_destination_still_prepares_before_the_user_can_leave_step_one()
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            // Analyse > Update Ship in Save... preselects rather than clicks, so nothing raises the tile's Checked
            // handler. Without the shell preparing on open, Next would advance with no located save context.
            var session = Session(g, ExportDestination.UpdateShipInSave, saves: [FakeSave()],
                sourceSave: new SaveSourceRef("A Save That Is Not There", "H-ABC"));
            var wizard = new ExportWizard(session, ExportDestination.UpdateShipInSave);

            wizard.OpenedAsync().GetAwaiter().GetResult();

            var step = (DestinationStep)Pane(wizard)!;
            var reason = step.Validate();
            Assert.NotNull(reason);
            Assert.Contains("no longer in your Saves folder", reason);
        });
    }

    // ---- navigation state that a slow step leaves behind ----

    /// <summary>
    /// The regression that shipped first: switching destination disabled Next for the duration of the prepare, and
    /// nothing ever turned it back on, so the wizard was dead from step one.
    ///
    /// <para>The cause was structural rather than local. A pane's <see cref="WizardStep.CanAdvance"/> can change
    /// asynchronously, and the shell only re-read it when it navigated, which is exactly what it could no longer
    /// do. Any future slow step has the same shape, which is why this is asserted through the real pick path.</para>
    /// </summary>
    [SkippableFact]
    public void Switching_destination_and_back_leaves_Next_usable()
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            var wizard = new ExportWizard(Session(g,
                saves: [new SaveEntry("A save", "Ship", "Player", "now", @"C:\nope\a\a.zip")]));
            wizard.OpenedAsync().GetAwaiter().GetResult();
            var step = (DestinationStep)wizard.CurrentPane!;
            Assert.True(wizard.NextEnabled);

            step.PickAsync(ExportDestination.NewShipInSave).GetAwaiter().GetResult();
            Assert.True(wizard.NextEnabled);

            step.PickAsync(ExportDestination.Mod).GetAwaiter().GetResult();
            Assert.True(wizard.NextEnabled);
        });
    }

    [SkippableFact]
    public void Populating_a_pane_is_not_an_edit_to_it()
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            // Enter assigns IsChecked and slider values, which raise the same events a user's click does. Left
            // unguarded, merely walking to a step would report itself as an edit and throw away Review's build.
            var session = Session(g);
            var step = new ObtainableStep();
            var raised = 0;
            step.Changed += () => raised++;

            step.Populate(session);
            Assert.Equal(0, raised);

            Descendants<CheckBox>(step).First().IsChecked = true;   // not vacuous: a real click still reports
            Assert.Equal(1, raised);
        });
    }

    // ---- reopening on a remembered export ----

    /// <summary>
    /// Reopening lands at the beginning, but a repeat export is still one click: every step that revalidates is
    /// marked complete, so the rail jumps straight to Review without walking them again.
    ///
    /// <para>The first smoke test found the alternative: landing on Review after a successful export showed a
    /// review of something already written, one reflexive click from writing it a second time.</para>
    /// </summary>
    [SkippableFact]
    public void Reopening_a_remembered_export_starts_at_the_beginning_with_Review_one_click_away()
    {
        var g = TestData.RequireGame();
        var dir = Path.Combine(Path.GetTempPath(), "OstraplanWizardTest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            RunSta(() =>
            {
                var settings = new AppSettings
                {
                    LastExportDir = dir,
                    LastExport = new LastExport
                    {
                        Destination = "mod", StagedIntoMods = false,
                        BrokerPools = ["RandomShipBrokerOKLG"],   // a route, or the obtainability gate wins first
                    },
                };
                var wizard = new ExportWizard(Session(g, settings: settings));

                wizard.OpenedAsync(resume: true).GetAwaiter().GetResult();

                Assert.IsType<DestinationStep>(wizard.CurrentPane);
                Assert.True(wizard.ReviewIsOneClickAway);
            });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [SkippableFact]
    public void A_remembered_export_whose_folder_has_gone_opens_on_the_step_that_can_say_so()
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            var settings = new AppSettings
            {
                LastExportDir = @"C:\nope\this\was\deleted",
                LastExport = new LastExport
                    {
                        Destination = "mod", StagedIntoMods = false,
                        BrokerPools = ["RandomShipBrokerOKLG"],   // a route, or the obtainability gate wins first
                    },
            };
            var wizard = new ExportWizard(Session(g, settings: settings));

            wizard.OpenedAsync(resume: true).GetAwaiter().GetResult();

            var pane = Assert.IsType<ModTargetStep>(wizard.CurrentPane);
            Assert.Contains("no longer exists", pane.Validate());
            Assert.False(wizard.ReviewIsOneClickAway);   // and the rail will not skip past the problem
        });
    }

    // ---- remembered settings ----

    [Fact]
    public void A_customised_broker_weight_survives_a_round_trip()
    {
        var settings = new AppSettings();
        var plan = new ExportPlan();
        plan.Mod.BrokerWeight = 0.42;

        plan.SaveTo(settings);
        var restored = ExportPlan.FromSettings(settings, new OplanMeta(), null);

        Assert.Equal(0.42, restored.Mod.BrokerWeight);
    }

    [SkippableFact]
    public void A_remembered_broker_weight_is_not_overwritten_by_the_games_default()
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            // the game's own weight is the starting point for a first export, not an override applied every time
            var session = Session(g);
            session.Plan.Mod.BrokerWeight = 0.42;
            var step = new ObtainableStep();

            step.Populate(session);
            step.Leave(session);

            Assert.Equal(0.42, session.Plan.Mod.BrokerWeight);
        });
    }

    [SkippableFact]
    public void Registering_with_Ostrasort_is_recommended_on_a_first_export_when_it_is_installed()
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            var first = Session(g, ostrasortKnown: true);           // nothing remembered yet
            var step = new ModTargetStep();
            step.Populate(first);
            step.Leave(first);
            Assert.True(first.Plan.Mod.RegisterWithOstrasort);

            // but once the user has answered, their answer stands
            var settings = new AppSettings { LastExport = new LastExport { RegisterWithOstrasort = false } };
            var later = Session(g, settings: settings, ostrasortKnown: true);
            later.Plan.Mod.RegisterWithOstrasort = false;
            var step2 = new ModTargetStep();
            step2.Populate(later);
            step2.Leave(later);
            Assert.False(later.Plan.Mod.RegisterWithOstrasort);
        });
    }

    // ---- the capture guard ----

    [Fact]
    public void The_plan_is_safe_to_hand_to_a_background_build()
    {
        // Every wizard build closes over the plan and Core objects, never over a control. This is the shape the
        // guard has to accept, or the whole design would have to hoist twenty fields by hand at each call site.
        var plan = new ExportPlan { ShipName = "Kestrel" };

        Ui.VerifyCaptures(() => plan.ShipName.Length);
    }

    [SkippableFact]
    public void The_cost_step_builds_and_stands_down_when_there_is_nothing_to_deduct()
    {
        // The cost readout is a built-in-code ledger + balance meter, so a row-index or column-span slip lands in
        // the constructor. Entering with no driver also exercises the path where the tally is collapsed entirely:
        // a table of zeros would be noise when nothing is being charged.
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            var session = Session(g, ExportDestination.UpdateShipInSave,
                sourceSave: new SaveSourceRef("Cold Open", "J-P3HF"));

            var step = new UpdateTargetStep();
            step.Enter(session);

            Assert.Null(step.Validate());   // nothing to charge → never blocks Next
        });
    }

    // ---- helpers ----

    private static IReadOnlyList<string> Rail(ExportWizard wizard) =>
        [.. Descendants<Border>(wizard)
            .Select(b => b.Child as TextBlock)
            .Where(t => t is not null && t.FontSize == 12)
            .Select(t => t!.Text)];

    private static WizardStep? Pane(ExportWizard wizard) =>
        Descendants<ContentControl>(wizard).Select(c => c.Content as WizardStep).FirstOrDefault(s => s is not null);

    /// <summary>Walk the logical tree. The visual tree is not built until the window renders, and these tests
    /// deliberately never show one.</summary>
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T hit) yield return hit;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
