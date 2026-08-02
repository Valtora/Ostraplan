using Ostraplan.App.Wizard;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The export wizard's navigation rules. These are the parts that are easy to get wrong and impossible to notice
/// by hand: a rail that lets you skip a step you haven't filled in, an edit two steps back that leaves a stale
/// Review standing, and a remembered update export that reopens one click away from rewriting a save.
///
/// <para><see cref="WizardFlow"/> holds no WPF types precisely so this can be an ordinary test class rather than
/// something that needs an STA thread and a message pump.</para>
/// </summary>
public class WizardFlowTests
{
    // ---- which steps each destination has ----

    [Fact]
    public void The_mod_destination_walks_seven_steps()
    {
        var flow = new WizardFlow(ExportDestination.Mod);

        Assert.Equal(
            [StepId.Destination, StepId.Ship, StepId.ModDetails, StepId.Obtainable, StepId.ModTarget,
                StepId.Review, StepId.Done],
            flow.Steps);
    }

    [Fact]
    public void The_new_ship_destination_trades_the_mod_steps_for_one()
    {
        var flow = new WizardFlow(ExportDestination.NewShipInSave);

        Assert.Equal(
            [StepId.Destination, StepId.Ship, StepId.SavePrice, StepId.Review, StepId.Done],
            flow.Steps);
    }

    [Fact]
    public void The_update_destination_has_no_save_picker()
    {
        // ShipDocument.SourceSave already names the save and the ship, so there is nothing to pick
        var flow = new WizardFlow(ExportDestination.UpdateShipInSave);

        Assert.DoesNotContain(StepId.SavePrice, flow.Steps);
        Assert.Contains(StepId.UpdateTarget, flow.Steps);
    }

    [Fact]
    public void Missing_parts_appears_only_for_an_update_with_unresolved_defs()
    {
        Assert.Contains(StepId.MissingParts,
            WizardFlow.StepsFor(ExportDestination.UpdateShipInSave, hasUnresolvedParts: true));
        Assert.DoesNotContain(StepId.MissingParts,
            WizardFlow.StepsFor(ExportDestination.UpdateShipInSave, hasUnresolvedParts: false));

        // a stand-in needs the save context, and a mod export cannot have unresolved parts at all
        Assert.DoesNotContain(StepId.MissingParts,
            WizardFlow.StepsFor(ExportDestination.Mod, hasUnresolvedParts: true));
        Assert.DoesNotContain(StepId.MissingParts,
            WizardFlow.StepsFor(ExportDestination.NewShipInSave, hasUnresolvedParts: true));
    }

    // ---- the rail ----

    [Fact]
    public void The_rail_refuses_a_forward_jump_past_an_incomplete_step()
    {
        var flow = new WizardFlow(ExportDestination.Mod);

        Assert.False(flow.CanJumpTo(flow.IndexOf(StepId.Review)));
        Assert.False(flow.JumpTo(flow.IndexOf(StepId.ModTarget)));
        Assert.Equal(0, flow.Current);
    }

    [Fact]
    public void The_rail_allows_backwards_and_any_completed_step()
    {
        var flow = new WizardFlow(ExportDestination.NewShipInSave);
        while (flow.CurrentStep != StepId.Review) flow.Advance();

        Assert.True(flow.CanJumpTo(0));                                  // backwards is always fine
        flow.JumpTo(flow.IndexOf(StepId.Ship));
        Assert.True(flow.CanJumpTo(flow.IndexOf(StepId.Review)));        // and forwards again, now it is complete
    }

    [Fact]
    public void Done_is_never_reachable_by_a_click()
    {
        var flow = new WizardFlow(ExportDestination.NewShipInSave);
        while (flow.Advance()) { }

        Assert.Equal(StepId.Done, flow.CurrentStep);
        Assert.False(flow.CanJumpTo(flow.IndexOf(StepId.Done)));
        Assert.False(flow.Back());                                       // a written save is not something to undo
    }

    [Fact]
    public void An_edit_invalidates_Review_but_not_the_steps_in_between()
    {
        var flow = new WizardFlow(ExportDestination.Mod);
        while (flow.CurrentStep != StepId.Review) flow.Advance();
        var details = flow.IndexOf(StepId.ModDetails);
        flow.JumpTo(details);

        flow.InvalidateReview();

        Assert.True(flow.IsComplete(flow.IndexOf(StepId.Ship)));         // still filled in, so still clickable
        Assert.True(flow.CanJumpTo(flow.IndexOf(StepId.ModTarget)));
        Assert.False(flow.IsComplete(flow.IndexOf(StepId.Review)));      // but the build behind it is stale
    }

    // ---- resume ----

    private static IReadOnlyList<bool> AllValid(ExportDestination d, bool unresolved = false) =>
        [.. WizardFlow.StepsFor(d, unresolved).Select(_ => true)];

    [Fact]
    public void A_remembered_mod_export_reopens_on_Review()
    {
        var steps = WizardFlow.StepsFor(ExportDestination.Mod, false);

        var at = WizardFlow.ResumeIndex(ExportDestination.Mod, AllValid(ExportDestination.Mod));

        Assert.Equal(StepId.Review, steps[at]);
    }

    [Fact]
    public void A_remembered_new_ship_export_reopens_on_Review()
    {
        var steps = WizardFlow.StepsFor(ExportDestination.NewShipInSave, false);

        var at = WizardFlow.ResumeIndex(ExportDestination.NewShipInSave, AllValid(ExportDestination.NewShipInSave));

        Assert.Equal(StepId.Review, steps[at]);
    }

    [Fact]
    public void A_remembered_update_never_reopens_on_Review()
    {
        // one click from rewriting a save the user already has is a footgun, so this destination always walks
        var steps = WizardFlow.StepsFor(ExportDestination.UpdateShipInSave, false);

        var at = WizardFlow.ResumeIndex(ExportDestination.UpdateShipInSave,
            AllValid(ExportDestination.UpdateShipInSave));

        Assert.NotEqual(StepId.Review, steps[at]);
        Assert.Equal(0, at);
    }

    [Fact]
    public void A_step_that_no_longer_validates_wins_over_the_resume_target()
    {
        // the save it was written to last time has been deleted since
        var valid = new[] { true, true, false, true, true };

        var at = WizardFlow.ResumeIndex(ExportDestination.NewShipInSave, valid);

        Assert.Equal(StepId.SavePrice, WizardFlow.StepsFor(ExportDestination.NewShipInSave, false)[at]);
    }

    [Fact]
    public void The_first_failing_step_wins_when_several_fail()
    {
        var valid = new[] { true, false, false, true, true };

        var at = WizardFlow.ResumeIndex(ExportDestination.NewShipInSave, valid);

        Assert.Equal(StepId.Ship, WizardFlow.StepsFor(ExportDestination.NewShipInSave, false)[at]);
    }
}
