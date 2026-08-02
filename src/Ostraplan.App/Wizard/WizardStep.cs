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

    /// <summary>The shell's way in to <see cref="Enter"/>: populating a pane from the plan is not an edit to it.</summary>
    internal void Populate(WizardSession session)
    {
        _populating = true;
        try { Enter(session); }
        finally { _populating = false; }
    }

    // ---- shared pane furniture, matching the dialogs the wizard replaces ----

    protected static Brush Ink => ThemeManager.Ink;
    protected static Brush Dim => ThemeManager.Dim;
    protected static Brush FieldBg => ThemeManager.FieldBg;

    /// <summary>A step's root panel: every pane is a vertical stack, scrolled by the shell rather than by itself,
    /// so the content pane's scrollbar is in one place and nothing jumps on Next.</summary>
    protected static StackPanel Body() => new() { Margin = new Thickness(0, 0, 4, 0) };

    protected static void Header(Panel parent, string text) => parent.Children.Add(new TextBlock
    {
        Text = text, Foreground = Dim, FontWeight = FontWeights.Bold, FontSize = 11,
        Margin = new Thickness(0, 16, 0, 5),
    });

    protected static TextBlock Note(Panel parent, string text, double indent = 0) =>
        Add(parent, new TextBlock
        {
            Text = text, Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(indent, 4, 0, 0),
        });

    protected static TextBox Field(Panel parent, string label, string value, bool multiline = false)
    {
        parent.Children.Add(new TextBlock
        {
            Text = label.ToUpperInvariant(), Foreground = Dim, FontWeight = FontWeights.Bold, FontSize = 11,
            Margin = new Thickness(0, 10, 0, 3),
        });
        var box = new TextBox
        {
            Text = value, Foreground = Ink, Background = FieldBg, BorderBrush = ThemeManager.PanelBorder,
            Padding = new Thickness(5, 3, 5, 3), CaretBrush = Ink,
        };
        if (multiline)
        {
            box.AcceptsReturn = true;
            box.TextWrapping = TextWrapping.Wrap;
            box.Height = 48;
            box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
        parent.Children.Add(box);
        return box;
    }

    protected static TextBox SmallBox(string value, double width) => new()
    {
        Text = value, Width = width, Foreground = Ink, Background = FieldBg, BorderBrush = ThemeManager.PanelBorder,
        Padding = new Thickness(5, 2, 5, 2), CaretBrush = Ink,
    };

    /// <summary>An inline problem line, placed by the pane right under the field it is about and hidden until
    /// something is actually wrong.</summary>
    protected static TextBlock Problem(Panel parent, double indent = 0) => Add(parent, new TextBlock
    {
        Foreground = ThemeManager.Bad, FontSize = 12, FontWeight = FontWeights.SemiBold,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(indent, 4, 0, 0),
        Visibility = Visibility.Collapsed,
    });

    /// <summary>Show or clear a <see cref="Problem"/> line, and return the reason so a Validate override can be a
    /// single expression.</summary>
    protected static string? ShowProblem(TextBlock line, string? reason)
    {
        line.Text = reason ?? "";
        line.Visibility = reason is null ? Visibility.Collapsed : Visibility.Visible;
        return reason;
    }

    protected static T Add<T>(Panel parent, T child) where T : UIElement
    {
        parent.Children.Add(child);
        return child;
    }
}
