using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>Shows the room-annotated ship snapshot at full size in its own window, with a Save-to-PNG action.</summary>
public sealed class SnapshotWindow : Window
{
    public SnapshotWindow(BitmapSource image)
    {
        Title = "Ship snapshot";
        Width = Math.Min(1100, Math.Max(560, image.PixelWidth + 40));
        Height = Math.Min(1000, Math.Max(520, image.PixelHeight + 100));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.WindowBg;

        var root = new DockPanel { Margin = new Thickness(12) };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var save = new Button { Content = "Save image…", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0) };
        save.Click += (_, _) => RatingReportWindow.SaveSnapshotPng(this, image);
        var close = new Button { Content = "Close", Padding = new Thickness(16, 4, 16, 4), IsCancel = true };
        close.Click += (_, _) => Close();
        buttons.Children.Add(save);
        buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var img = new Image { Source = image, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        root.Children.Add(new Border { Background = Brushes.Black, Child = img, Padding = new Thickness(6) });

        Content = root;
    }
}

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

    public RatingReportWindow(AnalysisReport report, ShipValueEstimate value, BitmapSource? snapshot,
        Action<IReadOnlyList<(int X, int Y)>> highlightLeak)
    {
        Title = "Ship Rating";
        Width = 520; Height = 820;
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

        // estimated value: base is exact (Σ StatBasePrice = the game's parts value); buy/sell are estimates
        body.Children.Add(Header("ESTIMATED VALUE"));
        var value3 = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 4) };
        value3.Children.Add(Slot("Base value", Money(value.BaseValue)));
        value3.Children.Add(Slot("Sell (est.)", Money(value.SellEstimate)));
        value3.Children.Add(Slot("Buy (est.)", Money(value.BuyEstimate)));
        body.Children.Add(value3);
        body.Children.Add(new TextBlock
        {
            Text = "Base value is the pristine parts value (what the game quotes for an undamaged ship). Sell/buy are " +
                   "rough broker estimates — the real price is set per-kiosk and shifts with your faction standing.",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 12),
        });

        // room-annotated snapshot — opened in its own window so it has room to breathe
        if (snapshot is not null)
        {
            body.Children.Add(Header("SNAPSHOT"));
            body.Children.Add(new TextBlock
            {
                Text = "A room-annotated image of the ship (each compartment coloured and labelled).",
                Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4),
            });
            var view = new Button { Content = "View room map…", Padding = new Thickness(14, 4, 14, 4), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4) };
            view.Click += (_, _) => new SnapshotWindow(snapshot) { Owner = this }.ShowDialog();
            body.Children.Add(view);
        }

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

    private static string Money(double v) => "$" + v.ToString("#,##0.##", CultureInfo.InvariantCulture);

    internal static void SaveSnapshotPng(Window owner, BitmapSource image)
    {
        var dlg = new SaveFileDialog { Title = "Save ship snapshot", Filter = "PNG image|*.png", FileName = "ship-rating.png" };
        if (dlg.ShowDialog(owner) != true) return;
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(dlg.FileName);
            encoder.Save(stream);
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, "Couldn't save the image:\n\n" + ex.Message, "Ship Rating", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
