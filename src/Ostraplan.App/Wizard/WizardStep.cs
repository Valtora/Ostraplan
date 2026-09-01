using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ostraplan.App.Wizard;

/// <summary>
/// One pane of the export wizard.
///
/// <para>The contract is deliberately small. <see cref="Enter"/> fills the pane's controls from the plan,
/// <see cref="Validate"/> answers whether the user may move on, and <see cref="Leave"/> writes the controls back
/// into the plan. Because the plan is the only thing that persists between steps, a pane never has to know what
/// came before it or what comes next.</para>
///
/// <para>Next is always enabled. Clicking it validates, and a refusal shows its reason <b>inline, next to the
/// field that caused it</b> rather than in a popup — a disabled button that will not say why is the worse of the
/// two failures.</para>
/// </summary>
public abstract class WizardStep : UserControl
{
    /// <summary>This step's label in the rail.</summary>
    public abstract string Title { get; }

    /// <summary>Fill the pane from the plan. Called on every entry, forwards or backwards, so a pane always
    /// reflects the current state rather than whatever it was left showing.</summary>
    public virtual void Enter(WizardSession session) { }

    /// <summary>Anything slow the pane needs on entry, awaited by the shell after <see cref="Enter"/>. Review
    /// builds here; nothing else needs it.</summary>
    public virtual Task EnterAsync(WizardSession session) => Task.CompletedTask;

    /// <summary>Null when the step is satisfied. Otherwise the reason, which the pane has already rendered beside
    /// the offending field (see <see cref="ShowProblem"/>).</summary>
    public virtual string? Validate() => null;

    /// <summary>
    /// Write the pane back into the plan. Called on the way out in every direction, including backwards and out of
    /// a step that does not validate, because storing what the user typed is not the same question as whether they
    /// may move on: losing a half-finished field to a Back click would be worse than keeping it.
    /// </summary>
    public virtual void Leave(WizardSession session) { }

    /// <summary>Whether the Next button is usable at all. This is for the mechanical cases only — a build in
    /// flight, a commit under way. A refusal the <b>user</b> can fix belongs in <see cref="Validate"/>, so that
    /// clicking Next explains itself rather than leaving a dead button on screen.</summary>
    public virtual bool CanAdvance => true;

    /// <summary>Raised when the pane's state changed enough that the shell should re-evaluate and drop any cached
    /// build. Wire it to the controls that feed the engine, not to every keystroke of a free-text note.</summary>
    public event Action? Changed;

    /// <summary>
    /// Report a change the shell should react to. Suppressed while <see cref="Enter"/> is populating the pane,
    /// because assigning <c>IsChecked</c> or a slider value raises the same events a user's click does. Without
    /// this, merely visiting a step would report itself as an edit and throw away the build behind Review.
    /// </summary>
    protected void OnChanged()
    {
        if (!_populating) Changed?.Invoke();
    }

    private bool _populating;

    /// <summary>True while <see cref="Enter"/> is filling the pane in. A pane that records "the user changed this"
    /// has to consult it: the controls raise the same events either way.</summary>
    protected bool IsPopulating => _populating;

    /// <summary>The shell's way in to <see cref="Enter"/>: populating a pane from the plan is not an edit to it.</summary>
    internal void Populate(WizardSession session)
    {
        _populating = true;
        try { Enter(session); }
        finally { _populating = false; }
    }

    // ---- shared pane furniture ----
    //
    // The bodies live in PaneUi, because the bundle editor builds the same kind of pane without being a wizard
    // step. These stay so that every step still reads as it did.

    protected static Brush Ink => PaneUi.Ink;
    protected static Brush Dim => PaneUi.Dim;
    protected static Brush FieldBg => PaneUi.FieldBg;

    /// <inheritdoc cref="PaneUi.Body"/>
    protected static StackPanel Body() => PaneUi.Body();

    /// <inheritdoc cref="PaneUi.Header"/>
    protected static TextBlock Header(Panel parent, string text) => PaneUi.Header(parent, text);

    /// <inheritdoc cref="PaneUi.Note"/>
    protected static TextBlock Note(Panel parent, string text, double indent = 0) => PaneUi.Note(parent, text, indent);

    /// <inheritdoc cref="PaneUi.Field"/>
    protected static TextBox Field(Panel parent, string label, string value, bool multiline = false) =>
        PaneUi.Field(parent, label, value, multiline);

    /// <inheritdoc cref="PaneUi.SmallBox"/>
    protected static TextBox SmallBox(string value, double width) => PaneUi.SmallBox(value, width);

    /// <inheritdoc cref="PaneUi.Problem"/>
    protected static TextBlock Problem(Panel parent, double indent = 0) => PaneUi.Problem(parent, indent);

    /// <inheritdoc cref="PaneUi.ShowProblem"/>
    protected static string? ShowProblem(TextBlock line, string? reason) => PaneUi.ShowProblem(line, reason);

    /// <inheritdoc cref="PaneUi.Add"/>
    protected static T Add<T>(Panel parent, T child) where T : UIElement => PaneUi.Add(parent, child);
}
