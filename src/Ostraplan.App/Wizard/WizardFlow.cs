namespace Ostraplan.App.Wizard;

/// <summary>Every step the wizard can show. Which of them appear, and in what order, depends on the destination
/// picked in <see cref="Destination"/>.</summary>
public enum StepId
{
    Destination,
    Ship,

    /// <summary>Update-only, and only when the design still has items whose defs aren't loaded. Stand-ins need the
    /// save context; a mod export cannot have unresolved parts, because a design with missing mods is held
    /// read-only from the moment it loads.</summary>
    MissingParts,

    ModDetails,
    Obtainable,
    ModTarget,
    SavePrice,
    UpdateTarget,
    Review,
    Done,
}

/// <summary>
/// Which steps a destination has, where the user is among them, and which of them they are allowed to jump to.
///
/// <para>Deliberately free of WPF types, so the navigation rules that are easy to get wrong (never forwards past
/// an incomplete step; the update destination never resuming one click from rewriting a save) are ordinary unit
/// tests rather than something only a human clicking can check.</para>
/// </summary>
public sealed class WizardFlow
{
    private readonly bool[] _complete;

    public WizardFlow(ExportDestination destination, bool hasUnresolvedParts = false)
    {
        Destination = destination;
        Steps = StepsFor(destination, hasUnresolvedParts);
        _complete = new bool[Steps.Count];
    }

    public ExportDestination Destination { get; }

    public IReadOnlyList<StepId> Steps { get; }

    public int Current { get; private set; }

    public StepId CurrentStep => Steps[Current];

    /// <summary>The steps a destination shows, in rail order. The mod path is the long one; both save paths trade
    /// its three mod-specific steps for a single one.</summary>
    public static IReadOnlyList<StepId> StepsFor(ExportDestination destination, bool hasUnresolvedParts) =>
        destination switch
        {
            ExportDestination.Mod =>
            [
                StepId.Destination, StepId.Ship, StepId.ModDetails, StepId.Obtainable, StepId.ModTarget,
                StepId.Review, StepId.Done,
            ],
            ExportDestination.NewShipInSave =>
                [StepId.Destination, StepId.Ship, StepId.SavePrice, StepId.Review, StepId.Done],
            _ when hasUnresolvedParts =>
            [
                StepId.Destination, StepId.Ship, StepId.MissingParts, StepId.UpdateTarget,
                StepId.Review, StepId.Done,
            ],
            _ => [StepId.Destination, StepId.Ship, StepId.UpdateTarget, StepId.Review, StepId.Done],
        };

    public int IndexOf(StepId step)
    {
        for (var i = 0; i < Steps.Count; i++)
            if (Steps[i] == step) return i;
        return -1;
    }

    public bool IsComplete(int index) => index >= 0 && index < _complete.Length && _complete[index];

    /// <summary>Record that the user has satisfied a step and moved past it. This is what lights the rail up and
    /// what makes a forward jump legal.</summary>
    public void Complete(int index)
    {
        if (index >= 0 && index < _complete.Length) _complete[index] = true;
    }

    /// <summary>
    /// A step's content changed, so anything derived from it is stale. Only Review and Done are cleared, not the
    /// steps in between: those are independent data-entry panes, and clearing them would make the rail forget an
    /// already-filled step every time the user corrected a typo two steps back. Review is the one thing that
    /// genuinely depends on all of them, which is also why it is the step that builds.
    /// </summary>
    public void InvalidateReview()
    {
        var review = IndexOf(StepId.Review);
        for (var i = review; i >= 0 && i < _complete.Length; i++) _complete[i] = false;
    }

    /// <summary>
    /// May the rail jump straight to this step? Backwards is always allowed, and so is any step already completed;
    /// forwards past an incomplete step never is. Done is never reachable by a click: it is where a commit lands.
    /// </summary>
    public bool CanJumpTo(int index)
    {
        if (index < 0 || index >= Steps.Count) return false;
        if (index == Current) return false;                          // no-op, so don't offer it
        if (Steps[index] == StepId.Done) return false;
        if (CurrentStep == StepId.Done) return false;                // the run is over; nothing to go back to
        if (index < Current) return true;

        for (var i = Current; i < index; i++)
            if (!_complete[i]) return false;
        return true;
    }

    public bool JumpTo(int index)
    {
        if (!CanJumpTo(index)) return false;
        Current = index;
        return true;
    }

    /// <summary>Move to the next step, marking the one being left as complete. The caller has already validated it.</summary>
    public bool Advance()
    {
        if (Current + 1 >= Steps.Count) return false;
        Complete(Current);
        Current++;
        return true;
    }

    public bool Back()
    {
        if (Current == 0 || CurrentStep == StepId.Done) return false;
        Current--;
        return true;
    }

    /// <summary>Drop straight onto a step, used when a commit lands on Done.</summary>
    public void GoTo(StepId step)
    {
        var i = IndexOf(step);
        if (i < 0) return;
        for (var j = 0; j < i; j++) _complete[j] = true;
        Current = i;
    }

    /// <summary>
    /// Where to open a wizard whose settings were remembered from last time.
    ///
    /// <para><paramref name="stepValid"/> runs parallel to <see cref="Steps"/> and holds the result of
    /// revalidating each step against the world as it is now: the save may have been deleted, the output folder
    /// may be gone. The first failure wins, so the user lands on the step that can explain itself.</para>
    ///
    /// <para>With everything valid, the mod and new-ship destinations open on <b>Review</b>, so a repeat export is
    /// one click. The update destination never does: landing one click from rewriting a save is a footgun, and it
    /// is the one destination that overwrites something the user already has.</para>
    /// </summary>
    public static int ResumeIndex(ExportDestination destination, IReadOnlyList<bool> stepValid,
        bool hasUnresolvedParts = false)
    {
        var steps = StepsFor(destination, hasUnresolvedParts);
        for (var i = 0; i < steps.Count && i < stepValid.Count; i++)
        {
            if (steps[i] is StepId.Review or StepId.Done) break;
            if (!stepValid[i]) return i;
        }

        if (destination == ExportDestination.UpdateShipInSave) return 0;

        for (var i = 0; i < steps.Count; i++)
            if (steps[i] == StepId.Review) return i;
        return 0;
    }
}
