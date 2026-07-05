using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>In-game-style progress while the Ship Rating analysis runs off the UI thread.</summary>
public sealed class RatingProgressDialog : Window
{
    private readonly TextBlock _status;
    private readonly ProgressBar _bar;

    public RatingProgressDialog()
    {
        Title = "Ship Rating";
        Width = 360; Height = 130;
        WindowStyle = WindowStyle.ToolWindow;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.WindowBg;

        _status = new TextBlock { Foreground = ThemeManager.Ink, Margin = new Thickness(0, 0, 0, 10), Text = "Analysing…" };
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
/// The Ship Rating law report: the six-slot rating, certified compartments, rooms that
/// nearly certify (with what they're missing), and airtightness breaches whose unsealed
/// tiles can be highlighted on the canvas.
/// </summary>
public sealed class RatingReportWindow : Window
{
    private static Brush Ink => ThemeManager.Ink;
    private static Brush Dim => ThemeManager.Dim;
    private static Brush Accent => ThemeManager.Accent;
    private static Brush Warn => ThemeManager.Warn;

    public RatingReportWindow(AnalysisReport report, Action<IReadOnlyList<(int X, int Y)>> highlightLeak)
    {
        Title = "Ship Rating";
        Width = 460; Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.WindowBg;

        var body = new StackPanel { Margin = new Thickness(18) };

        // headline rating
        body.Children.Add(new TextBlock { Text = "SHIP RATING", Foreground = Dim, FontWeight = FontWeights.Bold, FontSize = 11 });
        body.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(report.Rating.Display) ? "None" : report.Rating.Display,
            Foreground = Accent, FontSize = 30, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 2, 0, 10),
        });

        var slots = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, 0, 4) };
        slots.Children.Add(Slot("Condition", report.Rating.Condition));
        slots.Children.Add(Slot("Rooms", report.Rating.RoomCount));
        slots.Children.Add(Slot("Maneuver", report.Rating.Maneuver));
        slots.Children.Add(Slot("Size", report.Rating.Size));
        body.Children.Add(slots);
        body.Children.Add(new TextBlock
        {
            Text = "Condition assumes a pristine build (A). Maneuver is mass ÷ RCS thrust (O = no RCS). " +
                   "Room count is your certified compartments.",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 12),
        });

        // certified compartments
        Section(body, "CERTIFIED COMPARTMENTS", report.Certified
            .OrderBy(r => r.SpecFriendly)
            .Select(r => Row($"{r.SpecFriendly}", $"{r.TileCount} tiles · {r.Volume:0.#} m³", Ink))
            .ToList(), "No specialised compartments yet.");

        // near-miss rooms
        Section(body, "NEARLY CERTIFIES", report.Uncertifiable
            .Select(r => Row($"{r.TileCount}-tile room", r.NearMiss ?? "", Warn))
            .ToList(), null);

        // airtightness — each breach's leak points highlight on the canvas. Show toggles to
        // Hide (one breach shown at a time); closing this window clears the highlight so it
        // doesn't linger until the next Ship Rating.
        Closed += (_, _) => highlightLeak([]);
        var breaches = report.Breaches
            .OrderByDescending(b => b.ExposedFloorCount).ThenByDescending(b => b.Tiles.Count).ToList();
        body.Children.Add(Header("AIRTIGHTNESS"));
        if (breaches.Count == 0)
            body.Children.Add(new TextBlock { Text = "All compartments are sealed. ✓", Foreground = Ink, Margin = new Thickness(0, 2, 0, 8) });
        else
        {
            var showButtons = new List<Button>();
            foreach (var b in breaches)
            {
                var breach = b;
                var n = breach.Tiles.Count;
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                var show = new Button { Content = "Show", Padding = new Thickness(8, 1, 8, 1), VerticalAlignment = VerticalAlignment.Top };
                showButtons.Add(show);
                show.Click += (_, _) =>
                {
                    if ((string)show.Content == "Show")
                    {
                        foreach (var other in showButtons) other.Content = "Show";   // only one breach highlighted at a time
                        show.Content = "Hide";
                        highlightLeak(breach.Tiles);
                    }
                    else
                    {
                        show.Content = "Show";
                        highlightLeak([]);
                    }
                };
                DockPanel.SetDock(show, Dock.Right);
                row.Children.Add(show);
                row.Children.Add(new TextBlock
                {
                    Text = breach.OpenToSpace
                        ? $"{n} leak point{(n == 1 ? "" : "s")} — {breach.ExposedFloorCount}-tile area open to space"
                        : $"{breach.RoomTileCount}-tile compartment — {n} unsealed tile{(n == 1 ? "" : "s")}",
                    Foreground = Warn, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
                });
                body.Children.Add(row);
            }
        }

        var close = new Button { Content = "Close", Padding = new Thickness(16, 4, 16, 4), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        close.Click += (_, _) => Close();
        body.Children.Add(close);

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body };
    }

    private static UIElement Slot(string caption, string value)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        sp.Children.Add(new TextBlock { Text = value, Foreground = Ink, FontSize = 18, FontWeight = FontWeights.SemiBold });
        sp.Children.Add(new TextBlock { Text = caption, Foreground = Dim, FontSize = 10 });
        return sp;
    }

    private static TextBlock Header(string text) => new()
    {
        Text = text, Foreground = Dim, FontWeight = FontWeights.Bold, FontSize = 11, Margin = new Thickness(0, 12, 0, 4),
    };

    private static UIElement Row(string left, string right, Brush colour)
    {
        var dp = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        if (!string.IsNullOrEmpty(right))
        {
            var r = new TextBlock { Text = right, Foreground = Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(r, Dock.Right);
            dp.Children.Add(r);
        }
        dp.Children.Add(new TextBlock { Text = left, Foreground = colour, TextWrapping = TextWrapping.Wrap });
        return dp;
    }

    private static void Section(Panel parent, string header, IReadOnlyList<UIElement> rows, string? emptyText)
    {
        if (rows.Count == 0 && emptyText is null) return;
        parent.Children.Add(Header(header));
        if (rows.Count == 0)
            parent.Children.Add(new TextBlock { Text = emptyText, Foreground = Dim, Margin = new Thickness(0, 2, 0, 4) });
        else
            foreach (var r in rows) parent.Children.Add(r);
    }
}
