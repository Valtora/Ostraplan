using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>In-game-style progress while the diagnostic runs off the UI thread. Same shape as
/// <see cref="RatingProgressDialog"/>; it certifies rooms for the rating code, so it is not instant on a big ship.</summary>
public sealed class DiagnosticsProgressDialog : Window
{
    private readonly TextBlock _status;
    private readonly ProgressBar _bar;

    public DiagnosticsProgressDialog()
    {
        Title = "Diagnostics";
        Width = 360; Height = 130;
        WindowStyle = WindowStyle.ToolWindow;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.WindowBg;

        _status = new TextBlock { Foreground = ThemeManager.Ink, Margin = new Thickness(0, 0, 0, 10), Text = "Reading ship systems…" };
        _bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 18, Foreground = ThemeManager.Accent };
        Content = new StackPanel { Margin = new Thickness(16), Children = { _status, _bar } };
    }

    public void Update(string stage, double frac)
    {
        _status.Text = stage;
        _bar.Value = frac;
    }
}

/// <summary>
/// The ship checklist: the game's own nav-console diagnostic page (<c>NavModDiagnostics</c>), answered from the
/// design instead of from a ship that already exists. Sixteen rows in the game's order and wording, each green or
/// red on the game's own thresholds, and under every red one a line saying what is missing and where to get it.
///
/// <para>Laid out as the console lays it out — captions in one column, values right-aligned in a second — so a
/// player can read the two side by side. "Copy report" puts a plain-text copy on the clipboard for a bug report
/// or a forum post.</para>
/// </summary>
public sealed class DiagnosticsWindow : Window
{
    private static Brush Ink => ThemeManager.Ink;
    private static Brush Dim => ThemeManager.Dim;
    private static Brush Accent => ThemeManager.Accent;
    private static Brush Good => ThemeManager.Good;
    private static Brush Bad => ThemeManager.Bad;

    private readonly ShipDiagnosticReport _report;
    private readonly string _designName;

    public DiagnosticsWindow(ShipDiagnosticReport report, string designName)
    {
        _report = report;
        _designName = designName;

        Title = "Ship Diagnostics";
        Width = Math.Min(560, SystemParameters.WorkArea.Width - 40);
        Height = Math.Min(820, SystemParameters.WorkArea.Height - 40);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.WindowBg;

        var body = new StackPanel { Margin = new Thickness(18) };

        body.Children.Add(new TextBlock { Text = "SHIP DIAGNOSTICS", Foreground = Dim, FontWeight = FontWeights.Bold, FontSize = 11 });
        var faults = report.FaultCount;
        body.Children.Add(new TextBlock
        {
            Text = faults == 0 ? "All systems nominal" : $"{faults} of {report.Rows.Count} systems need attention",
            Foreground = faults == 0 ? Good : Accent,
            FontSize = 26, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 2, 0, 2),
        });
        body.Children.Add(new TextBlock
        {
            Text = "The game's own status page, from the nav console's Diagnostics module, read off the design. "
                 + "Quantities are what the ship spawns holding, so this is the readout a freshly built or freshly "
                 + "bought ship gives.",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        });

        // The rows themselves: a two-column grid so the values line up like the console's, with each note
        // spanning both columns underneath its row.
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var row = 0;
        foreach (var r in report.Rows)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock
            {
                Text = r.Name, Foreground = Dim, FontFamily = Mono, FontSize = 12,
                Margin = new Thickness(0, 3, 14, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetRow(name, row);
            Grid.SetColumn(name, 0);
            grid.Children.Add(name);

            var value = new TextBlock
            {
                Text = r.Value, FontFamily = Mono, FontSize = 13, FontWeight = FontWeights.SemiBold,
                Foreground = r.State switch { DiagState.Good => Good, DiagState.Bad => Bad, _ => Ink },
                TextAlignment = TextAlignment.Right, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);
            row++;

            if (r.Note is null) continue;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var note = new TextBlock
            {
                Text = r.Note, Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(12, 1, 0, 4),
            };
            Grid.SetRow(note, row);
            Grid.SetColumn(note, 0);
            Grid.SetColumnSpan(note, 2);
            grid.Children.Add(note);
            row++;
        }
        body.Children.Add(grid);

        body.Children.Add(new TextBlock
        {
            Text = "Three rows read differently here than at a console, because a plan is not a running ship. "
                 + "NAV STATION is a real presence test (the console can't report its own absence — it hardcodes "
                 + "ONLINE). TRANSPONDER shows INSTALLED where the console shows the registration ID the game "
                 + "assigns at spawn. REACTOR shows INSTALLED where the console shows OFFLINE until the reactor "
                 + "is lit, which a planned one never is.",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0),
        });
        body.Children.Add(new TextBlock
        {
            Text = "This is the game's checklist, not the whole law. Run Ship Rating for rooms, airtightness, "
                 + "certification and the full propulsion figures.",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var copy = new Button { Content = "Copy report", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) => CopyToClipboard();
        var close = new Button { Content = "Close", Padding = new Thickness(16, 4, 16, 4), IsCancel = true };
        close.Click += (_, _) => Close();
        buttons.Children.Add(copy);
        buttons.Children.Add(close);
        body.Children.Add(buttons);

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body };
    }

    /// <summary>The console's readout is a fixed-width table; borrowing a monospace face is what keeps the value
    /// column readable as a column rather than as ragged prose.</summary>
    private static readonly FontFamily Mono = new("Consolas, Cascadia Mono, Courier New, monospace");

    private void CopyToClipboard()
    {
        var width = ShipDiagnostics.Names.Max(n => n.Length);
        var sb = new StringBuilder();
        sb.AppendLine($"Ship diagnostics — {_designName}");
        sb.AppendLine(_report.FaultCount == 0
            ? "All systems nominal"
            : $"{_report.FaultCount} of {_report.Rows.Count} systems need attention");
        sb.AppendLine();
        foreach (var r in _report.Rows)
        {
            sb.AppendLine($"{r.Name.PadRight(width)}  {r.Value}");
            if (r.Note is not null) sb.AppendLine($"{new string(' ', width)}  ! {r.Note}");
        }
        try { Clipboard.SetText(sb.ToString()); } catch { /* clipboard may be locked by another app */ }
    }
}
