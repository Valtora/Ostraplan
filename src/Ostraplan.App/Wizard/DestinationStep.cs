using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Ostraplan.App.Wizard;

/// <summary>
/// Step one: which way the design leaves Ostraplan. The rail behind this step is built from the answer, so every
/// later step suits exactly one destination instead of having to suit all three. That is what keeps the mod path's
/// twenty controls off the save paths.
///
/// <para>A destination that cannot be used is <b>shown, disabled, with the reason on it</b>. Hiding it would teach
/// nobody that it exists, and the reason is usually the actionable part: no saves found, or this design did not
/// come from one.</para>
/// </summary>
public sealed class DestinationStep : WizardStep
{
    private sealed record Tile(ExportDriver Driver, RadioButton Button, TextBlock Reason);

    private readonly List<Tile> _tiles = [];
    private readonly Func<ExportDestination, Task<string?>> _picked;
    private readonly TextBlock _problem;

    private WizardSession? _session;
    private string? _prepareFailure;
    private bool _preparing;
    private bool _syncing;

    public override string Title => "Destination";

    public override bool CanAdvance => !_preparing;

    public DestinationStep(IEnumerable<ExportDriver> drivers, Func<ExportDestination, Task<string?>> picked)
    {
        _picked = picked;

        var body = Body();
        body.Children.Add(new TextBlock
        {
            Text = "Where should this design go?",
            Foreground = Ink, FontSize = 15, FontWeight = FontWeights.SemiBold,
        });
        Note(body, "You can change this at any point. Nothing you have already typed is lost.");

        foreach (var driver in drivers)
        {
            var reason = new TextBlock
            {
                Foreground = ThemeManager.Warn, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0), MaxWidth = 420, Visibility = Visibility.Collapsed,
            };
            var caption = new StackPanel { Margin = new Thickness(4, 0, 0, 0) };
            caption.Children.Add(new TextBlock { Text = driver.Name, Foreground = Ink, FontWeight = FontWeights.SemiBold });
            caption.Children.Add(new TextBlock
            {
                Text = driver.Blurb, Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0), MaxWidth = 420,
            });
            caption.Children.Add(reason);

            var button = new RadioButton
            {
                GroupName = "destination", Content = caption, Margin = new Thickness(0, 16, 0, 0),
                VerticalContentAlignment = VerticalAlignment.Top,
            };
            var destination = driver.Destination;
            button.Checked += (_, _) => OnPicked(destination);

            body.Children.Add(button);
            _tiles.Add(new Tile(driver, button, reason));
        }

        _problem = Problem(body, indent: 0);
        _problem.Margin = new Thickness(0, 18, 0, 0);

        Content = body;
    }

    public override void Enter(WizardSession session)
    {
        _session = session;
        _syncing = true;   // assigning IsChecked raises Checked, which would re-pick and rebuild the rail
        try
        {
            foreach (var tile in _tiles)
            {
                var reason = tile.Driver.Unavailable(session);
                tile.Button.IsEnabled = reason is null;
                tile.Reason.Text = reason ?? "";
                tile.Reason.Visibility = reason is null ? Visibility.Collapsed : Visibility.Visible;
                tile.Button.IsChecked = tile.Driver.Destination == session.Plan.Destination;
            }
        }
        finally
        {
            _syncing = false;
        }

        ShowProblem(_problem, _prepareFailure);
    }

    /// <summary>Seed the reason a destination prepared elsewhere failed (the shell prepares whatever destination
    /// the wizard opens on), so it shows here and blocks Next like any other.</summary>
    public void SetBlocker(string? reason)
    {
        _prepareFailure = reason;
        ShowProblem(_problem, reason);
    }

    public override string? Validate()
    {
        var chosen = _tiles.FirstOrDefault(t => t.Button.IsChecked == true);
        if (chosen is null) return ShowProblem(_problem, "Pick where the design should go.");
        if (_session is { } s && chosen.Driver.Unavailable(s) is { } why) return ShowProblem(_problem, why);
        return ShowProblem(_problem, _prepareFailure);
    }

    private async void OnPicked(ExportDestination destination)
    {
        if (_syncing) return;

        // Selecting a destination can be slow: the update path re-locates its save context rather than finding out
        // at commit that the save has moved. A failure blocks Next, with the reason on this step.
        _prepareFailure = null;
        ShowProblem(_problem, null);
        _preparing = true;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            _prepareFailure = await _picked(destination);
        }
        finally
        {
            _preparing = false;
            Mouse.OverrideCursor = null;
        }
        ShowProblem(_problem, _prepareFailure);
    }
}
