using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Ostraplan.App.Wizard;

/// <summary>
/// What is about to happen, worked out by running the real engine rather than by describing the settings.
///
/// <para>That distinction matters. Dropped cargo, placement-law warnings and the resulting rating are only
/// knowable once the build has run, which is why the flow this replaces confirmed those things <b>after</b> the
/// engine and before the write. Here the build happens on entering this step, off-thread with a loading state, and
/// the commit that follows performs only the write.</para>
///
/// <para>Anything destructive appears as an acknowledgement the user has to tick: overwriting a mod folder,
/// deleting cargo. That is the second click those actions used to get from a popup, kept, but next to the facts
/// that justify it rather than on top of them.</para>
/// </summary>
public sealed class ReviewStep : WizardStep
{
    private readonly StackPanel _body, _facts, _warnings, _acks;
    private readonly TextBlock _status, _problem;
    private readonly List<CheckBox> _ackBoxes = [];

    private bool _building;
    private bool _built;

    public override string Title => "Review";

    public override bool CanAdvance => !_building && _built;

    public ReviewStep()
    {
        _body = Body();
        _body.Children.Add(new TextBlock
        {
            Text = "Before it is written", Foreground = Ink, FontSize = 15, FontWeight = FontWeights.SemiBold,
        });
        _status = Note(_body, "");
        _facts = Add(_body, new StackPanel { Margin = new Thickness(0, 12, 0, 0) });
        _warnings = Add(_body, new StackPanel { Margin = new Thickness(0, 12, 0, 0) });
        _acks = Add(_body, new StackPanel { Margin = new Thickness(0, 12, 0, 0) });
        _problem = Problem(_body);
        _problem.Margin = new Thickness(0, 14, 0, 0);
        Content = _body;
    }

    public override async Task EnterAsync(WizardSession session)
    {
        _building = true;
        _built = false;
        _facts.Children.Clear();
        _warnings.Children.Clear();
        _acks.Children.Clear();
        _ackBoxes.Clear();
        ShowProblem(_problem, null);
        _status.Text = "Working out what this will do…";

        try
        {
            Render(await session.Driver.BuildAsync(session));
            _built = true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _status.Text = "";
            ShowProblem(_problem, ex.Message);
        }
        finally
        {
            _building = false;
        }
    }

    private void Render(BuildOutcome outcome)
    {
        _status.Text = "This is what will be written. Nothing has been changed yet.";

        foreach (var fact in outcome.Facts)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var label = new TextBlock
            {
                Text = fact.Label, Foreground = Dim, FontSize = 12, Width = 150,
                VerticalAlignment = VerticalAlignment.Top,
            };
            DockPanel.SetDock(label, Dock.Left);
            row.Children.Add(label);
            row.Children.Add(new TextBlock
            {
                Text = fact.Value, Foreground = Ink, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            });
            _facts.Children.Add(row);
        }

        if (outcome.Warnings.Count > 0)
        {
            _warnings.Children.Add(new TextBlock
            {
                Text = $"{outcome.Warnings.Count} warning(s). The ship is still written, so load it and check.",
                Foreground = ThemeManager.Warn, FontSize = 12, FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            foreach (var w in outcome.Warnings.Take(8))
                _warnings.Children.Add(new TextBlock
                {
                    Text = "•   " + w, Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(8, 3, 0, 0),
                });
            if (outcome.Warnings.Count > 8)
                _warnings.Children.Add(new TextBlock
                {
                    Text = $"…and {outcome.Warnings.Count - 8} more", Foreground = Dim, FontSize = 11,
                    Margin = new Thickness(8, 3, 0, 0),
                });
        }

        foreach (var ack in outcome.Acknowledgements)
        {
            var box = new CheckBox
            {
                Content = new TextBlock { Text = ack, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
                Foreground = ThemeManager.Warn, Margin = new Thickness(0, 8, 0, 0),
                VerticalContentAlignment = VerticalAlignment.Top,
            };
            box.Checked += (_, _) => ShowProblem(_problem, null);
            _acks.Children.Add(box);
            _ackBoxes.Add(box);
        }
    }

    public override string? Validate()
    {
        if (!_built) return ShowProblem(_problem, "Nothing has been built yet.");
        return _ackBoxes.Any(b => b.IsChecked != true)
            ? ShowProblem(_problem, "Tick the boxes above to confirm what this will overwrite or delete.")
            : ShowProblem(_problem, null);
    }

    /// <summary>Swap the pane over to the commit in progress. The write is the part that cannot be undone, so it
    /// says so plainly rather than leaving the review text standing behind a spinner.</summary>
    public void ShowCommitting(string verb)
    {
        _status.Text = $"{verb}…";
        ShowProblem(_problem, null);
    }

    /// <summary>A write that failed for a reason the user can act on. The review stays on screen, so they can fix
    /// the cause and press the button again.</summary>
    public void ShowFailure(string message)
    {
        _status.Text = "Nothing was written.";
        ShowProblem(_problem, message);
    }
}
