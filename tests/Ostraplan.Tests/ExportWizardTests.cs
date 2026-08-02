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
        IReadOnlyList<SaveEntry>? saves = null)
    {
        var doc = new ShipDocument(g.Catalog);
        new PlaceCommand(new Placement { DefName = "ItmWall1x1", X = 0, Y = 0 }).Do(doc);
        return new WizardSession
        {
            Plan = new ExportPlan { Destination = destination, ShipName = shipName },
            Doc = doc,
            Catalog = g.Catalog,
            Specs = [],
            Index = g.Index,
            Env = g.Env,
            Settings = new AppSettings(),
            Meta = new OplanMeta { Name = shipName },
            Saves = saves ?? [],
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
    public void Identity_is_read_only_when_updating_a_ship_in_a_save()
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            // SaveEdit preserves the original record's identity verbatim, so offering to edit it would be a lie
            var session = Session(g, ExportDestination.UpdateShipInSave);
            var step = new ShipStep();
            step.Enter(session);

            var boxes = Descendants<TextBox>(step).ToList();
            Assert.Equal("Test Ship", boxes[0].Text);        // the design name stays editable
            Assert.False(boxes[0].IsReadOnly);
            Assert.All(boxes.Skip(1), b => Assert.True(b.IsReadOnly));
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

    // ---- the capture guard ----

    [Fact]
    public void The_plan_is_safe_to_hand_to_a_background_build()
    {
        // Every wizard build closes over the plan and Core objects, never over a control. This is the shape the
        // guard has to accept, or the whole design would have to hoist twenty fields by hand at each call site.
        var plan = new ExportPlan { ShipName = "Kestrel" };

        Ui.VerifyCaptures(() => plan.ShipName.Length);
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
