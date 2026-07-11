using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>Shows the room-annotated ship snapshot in its own window: scroll to zoom (anchored on the cursor),
/// drag to pan, fit-to-window on open, and Save-to-PNG.</summary>
public sealed class SnapshotWindow : Window
{
    public SnapshotWindow(BitmapSource image, string? svg = null)
    {
        Title = "Ship snapshot — scroll to zoom, drag to pan";
        Width = 1000; Height = 900;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.WindowBg;

        var root = new DockPanel { Margin = new Thickness(12) };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var save = new Button { Content = "Save image…", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0) };
        save.Click += (_, _) => RatingReportWindow.SaveSnapshot(this, image, svg);
        var close = new Button { Content = "Close", Padding = new Thickness(16, 4, 16, 4), IsCancel = true };
        close.Click += (_, _) => Close();
        buttons.Children.Add(save);
        buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var scale = new ScaleTransform(1, 1);
        var img = new Image { Source = image, Stretch = Stretch.None, LayoutTransform = scale };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        var sv = new ScrollViewer
        {
            Content = img, Background = Brushes.Black,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        root.Children.Add(sv);
        Content = root;

        // fit the whole ship in view on open (never magnify past 1:1)
        sv.Loaded += (_, _) =>
        {
            if (image.PixelWidth == 0 || image.PixelHeight == 0 || sv.ViewportWidth <= 0) return;
            var fit = Math.Min(sv.ViewportWidth / image.PixelWidth, sv.ViewportHeight / image.PixelHeight);
            if (fit > 0) scale.ScaleX = scale.ScaleY = Math.Min(1.0, fit);
        };

        // cursor-anchored zoom
        sv.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            var mouse = e.GetPosition(sv);
            var before = new Point((sv.HorizontalOffset + mouse.X) / scale.ScaleX, (sv.VerticalOffset + mouse.Y) / scale.ScaleY);
            var ns = Math.Clamp(scale.ScaleX * (e.Delta > 0 ? 1.15 : 1 / 1.15), 0.1, 8.0);
            scale.ScaleX = scale.ScaleY = ns;
            sv.UpdateLayout();
            sv.ScrollToHorizontalOffset(before.X * ns - mouse.X);
            sv.ScrollToVerticalOffset(before.Y * ns - mouse.Y);
        };

        // drag to pan
        Point? last = null;
        img.MouseLeftButtonDown += (_, e) => { last = e.GetPosition(sv); img.CaptureMouse(); Cursor = Cursors.SizeAll; };
        img.MouseMove += (_, e) =>
        {
            if (last is not { } p) return;
            var cur = e.GetPosition(sv);
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset - (cur.X - p.X));
            sv.ScrollToVerticalOffset(sv.VerticalOffset - (cur.Y - p.Y));
            last = cur;
        };
        img.MouseLeftButtonUp += (_, e) => { last = null; img.ReleaseMouseCapture(); Cursor = Cursors.Arrow; };
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
        Action<IReadOnlyList<(int X, int Y)>> highlightLeak, string? snapshotSvg = null)
    {
        Title = "Ship Rating";
        // roomy default (the report grew sections), clamped so it still fits smaller screens
        Width = Math.Min(640, SystemParameters.WorkArea.Width - 40);
        Height = Math.Min(1000, SystemParameters.WorkArea.Height - 40);
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
        var rating = report.Rating;
        var maneuverDetail = rating.RcsThrust > 0
            ? $"Maneuver is mass ÷ RCS thrust: {rating.Mass:#,0} kg ÷ {rating.RcsThrust:#,0.#} = " +
              $"{rating.Mass / rating.RcsThrust:#,0.#} (lower is better: <300 A, <500 B, <750 C, <1500 D, else E). " +
              $"Thrust-to-mass ratio: {rating.RcsThrust / rating.Mass:0.####} per kg " +
              $"({rating.RcsThrust * 1000 / rating.Mass:#,0.##} per tonne)."
            : "Maneuver is O: no RCS thrusters installed" +
              (rating.Mass > 0 ? $" (ship mass {rating.Mass:#,0} kg)." : ".");
        body.Children.Add(new TextBlock
        {
            Text = "Condition assumes a pristine build (A). Room count is your certified compartments. " + maneuverDetail,
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 12),
        });

        // kiosk prices: the game's room-based ship value at the core kiosk rates
        body.Children.Add(Header("KIOSK PRICES"));
        var value2 = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 4) };
        value2.Children.Add(Slot("Sell to kiosk", Money(value.SellEstimate)));
        value2.Children.Add(Slot("Buy from kiosk", Money(value.BuyEstimate)));
        value2.Children.Add(Slot("Build cost", Money(value.BuildCost)));
        body.Children.Add(value2);
        body.Children.Add(new TextBlock
        {
            Text = "Estimates from the game's room maths at the standard kiosk rates. Expect roughly ±15% variation " +
                   "in the final in-game price.",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 12),
        });

        // room-annotated snapshot — opened in its own window so it has room to breathe
        if (snapshot is not null)
        {
            body.Children.Add(Header("SNAPSHOT"));
            body.Children.Add(new TextBlock
            {
                Text = "A room-annotated image of the ship (each compartment coloured and labelled). Save it as a PNG, or as " +
                       "an SVG whose room tints and labels stay sharp at any zoom.",
                Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4),
            });
            var view = new Button { Content = "View room map…", Padding = new Thickness(14, 4, 14, 4), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4) };
            view.Click += (_, _) => new SnapshotWindow(snapshot, snapshotSvg) { Owner = this }.ShowDialog();
            body.Children.Add(view);
        }

        // certified compartments
        Section(body, "CERTIFIED COMPARTMENTS", report.Certified
            .OrderBy(r => r.SpecFriendly)
            .Select(r => Row($"{r.SpecFriendly}", $"{r.TileCount} tiles · {r.Volume:0.#} m³", Ink))
            .ToList(), "No specialised compartments yet.");

        // near-miss rooms: the closest specs per room, including items that BLOCK an
        // otherwise-met spec (a canister/RTA/battery/hatch in a would-be quarters)
        var nearRows = new List<UIElement>();
        foreach (var r in report.Uncertifiable)
        {
            nearRows.Add(Row($"{r.TileCount}-tile room", "", Warn));
            foreach (var line in r.NearMisses)
                nearRows.Add(new TextBlock
                {
                    Text = line, Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(12, 0, 0, 2),
                });
        }
        Section(body, "NEARLY CERTIFIES", nearRows, null);

        // airtightness — each breach's leak points highlight on the canvas. Show toggles to
        // Hide (one highlight at a time, shared with the value-opportunity room highlights);
        // closing this window clears the highlight so it doesn't linger until the next Ship Rating.
        Closed += (_, _) => highlightLeak([]);
        var showButtons = new List<Button>();
        Button MakeShow(IReadOnlyList<(int X, int Y)> tiles)
        {
            var show = new Button { Content = "Show", Padding = new Thickness(8, 1, 8, 1), VerticalAlignment = VerticalAlignment.Top };
            showButtons.Add(show);
            show.Click += (_, _) =>
            {
                if ((string)show.Content == "Show")
                {
                    foreach (var other in showButtons) other.Content = "Show";   // only one highlight at a time
                    show.Content = "Hide";
                    highlightLeak(tiles);
                }
                else
                {
                    show.Content = "Show";
                    highlightLeak([]);
                }
            };
            return show;
        }

        var breaches = report.Breaches
            .OrderByDescending(b => b.ExposedFloorCount).ThenByDescending(b => b.Tiles.Count).ToList();
        body.Children.Add(Header("AIRTIGHTNESS"));
        if (breaches.Count == 0)
            body.Children.Add(new TextBlock { Text = "All compartments are sealed. ✓", Foreground = Ink, Margin = new Thickness(0, 2, 0, 8) });
        else
        {
            foreach (var b in breaches)
            {
                var n = b.Tiles.Count;
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                var show = MakeShow(b.Tiles);
                DockPanel.SetDock(show, Dock.Right);
                row.Children.Add(show);
                row.Children.Add(new TextBlock
                {
                    Text = b.OpenToSpace
                        ? $"{n} leak point{(n == 1 ? "" : "s")} — {b.ExposedFloorCount}-tile area open to space"
                        : $"{b.RoomTileCount}-tile compartment — {n} unsealed tile{(n == 1 ? "" : "s")}",
                    Foreground = Warn, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
                });
                body.Children.Add(row);
            }
        }

        // value opportunities — optional, collapsed by default: what each sealed room could
        // become (or upgrade to) and what that's worth at the broker. Includes empty rooms.
        var oppCount = report.Opportunities.Count + (report.O2BonusActive ? 0 : 1);
        if (oppCount > 0)
        {
            var opp = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            opp.Children.Add(new TextBlock
            {
                Text = "Optional ways to raise the sale price. Each room's contents are multiplied by its " +
                       "certified room modifier; gains shown are sale-price estimates for what's already in the " +
                       "room. Parts you add are worth their own price times the modifier on top.",
                Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6),
            });

            if (!report.O2BonusActive)
                opp.Children.Add(new TextBlock
                {
                    Text = "No working O2 supply: an air pump fed by an installed O2 canister (RTA) at its gas-input " +
                           "tile triples the whole ship's value" +
                           (report.O2PotentialSell >= 1 ? $" (+${report.O2PotentialSell:N0} sale price)." : "."),
                    Foreground = Warn, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
                });
            else
                opp.Children.Add(new TextBlock
                {
                    Text = "×3 O2 supply bonus active (an air pump is fed by an installed O2 canister).",
                    Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
                });

            foreach (var o in report.Opportunities)
            {
                // header row with a Show button that highlights the room's tiles on the canvas,
                // so there's never a question of WHICH room a hint is talking about
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 1) };
                var show = MakeShow(o.Tiles);
                DockPanel.SetDock(show, Dock.Right);
                row.Children.Add(show);
                row.Children.Add(new TextBlock
                {
                    Text = $"{o.TileCount}-tile {(o.Certified ? o.CurrentSpecFriendly : o.CurrentSpecFriendly + " room")}",
                    Foreground = Ink, VerticalAlignment = VerticalAlignment.Center,
                });
                opp.Children.Add(row);
                foreach (var line in o.Lines)
                    opp.Children.Add(new TextBlock
                    {
                        Text = line, Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(12, 0, 0, 2),
                    });
            }

            body.Children.Add(new Expander
            {
                Header = new TextBlock
                {
                    Text = $"VALUE OPPORTUNITIES ({oppCount})",
                    Foreground = Dim, FontWeight = FontWeights.Bold, FontSize = 11,
                },
                IsExpanded = false,
                Margin = new Thickness(0, 12, 0, 0),
                Content = opp,
            });
        }

        var close = new Button { Content = "Close", Padding = new Thickness(16, 4, 16, 4), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        close.Click += (_, _) => Close();
        body.Children.Add(close);

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body };
    }

    private static string Money(double v) => "$" + v.ToString("#,##0", CultureInfo.InvariantCulture);

    /// <summary>Save the room map as a PNG or, when an SVG rendering is available, an SVG (scalable). The chosen
    /// format follows the save dialog's file type / extension.</summary>
    internal static void SaveSnapshot(Window owner, BitmapSource image, string? svg)
    {
        var filter = svg is not null ? "PNG image|*.png|SVG image (scalable)|*.svg" : "PNG image|*.png";
        var dlg = new SaveFileDialog { Title = "Save ship snapshot", Filter = filter, FileName = "ship-rating.png" };
        if (dlg.ShowDialog(owner) != true) return;
        try
        {
            var asSvg = svg is not null &&
                        (dlg.FilterIndex == 2 || dlg.FileName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase));
            if (asSvg)
            {
                File.WriteAllText(dlg.FileName, svg!, new System.Text.UTF8Encoding(false));
            }
            else
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = File.Create(dlg.FileName);
                encoder.Save(stream);
            }
        }
        catch (Exception ex)
        {
            Dlg.Error(owner, "Ship Rating", "Couldn't save the image.\n\n" + ex.Message);
        }
    }

    private static UIElement Slot(string caption, string value, double fontSize = 18)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Bottom };
        sp.Children.Add(new TextBlock { Text = value, Foreground = Ink, FontSize = fontSize, FontWeight = FontWeights.SemiBold });
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
