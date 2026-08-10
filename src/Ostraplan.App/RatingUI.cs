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
///
/// <para>Modeless (see <see cref="ReportWindow"/>): the callbacks it needs are fixed for the life of the window and
/// arrive in the constructor, while the measured figures arrive per run through <see cref="SetReport"/>, so a
/// re-run refreshes the open window rather than replacing it.</para>
/// </summary>
public sealed class RatingReportWindow : ReportWindow
{
    private static Brush Ink => ThemeManager.Ink;
    private static Brush Dim => ThemeManager.Dim;
    private static Brush Accent => ThemeManager.Accent;
    private static Brush Warn => ThemeManager.Warn;

    private readonly Action<IReadOnlyList<(int X, int Y)>> _highlightLeak;
    private readonly Action<double>? _onExtraMassChanged;

    public RatingReportWindow(Action<IReadOnlyList<(int X, int Y)>> highlightLeak,
        Action<double>? onExtraMassChanged = null)
    {
        _highlightLeak = highlightLeak;
        _onExtraMassChanged = onExtraMassChanged;

        Title = "Ship Rating";
        // roomy default (the report grew sections), clamped so it still fits smaller screens
        Width = Math.Min(640, SystemParameters.WorkArea.Width - 40);
        Height = Math.Min(1000, SystemParameters.WorkArea.Height - 40);

        // whichever way this window goes away, the canvas must not keep a highlight it can no longer explain
        Closed += (_, _) => highlightLeak([]);
    }

    /// <summary>Show a run's figures, replacing whatever this window was showing. Clears any leak highlight the
    /// previous run left, since its Show buttons are gone with it.</summary>
    public void SetReport(AnalysisReport report, ShipValueEstimate value, BitmapSource? snapshot,
        string? snapshotSvg = null)
    {
        var highlightLeak = _highlightLeak;
        var onExtraMassChanged = _onExtraMassChanged;
        highlightLeak([]);

        var body = new StackPanel { Margin = new Thickness(18) };

        // headline rating
        body.Children.Add(new TextBlock { Text = "SHIP RATING", Foreground = Dim, FontWeight = FontWeights.Bold, FontSize = 11 });
        body.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(report.Rating.Display) ? "None" : report.Rating.Display,
            Foreground = Accent, FontSize = 30, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 2, 0, 10),
        });

        // The game's four displayed rating slots in their canonical order (they read out as the rating
        // string), then the ship's total mass — not a rating slot, but the raw figure behind Maneuver and
        // the one number people want off this report.
        var slots = new UniformGrid { Columns = 5, Margin = new Thickness(0, 0, 0, 4) };
        slots.Children.Add(Slot("Condition", report.Rating.Condition));
        slots.Children.Add(Slot("Rooms", report.Rating.RoomCount));
        slots.Children.Add(Slot("Maneuver", report.Rating.Maneuver));
        slots.Children.Add(Slot("Size", report.Rating.Size));
        slots.Children.Add(Slot("Mass", $"{report.Rating.Mass:#,0} kg"));
        body.Children.Add(slots);
        var rating = report.Rating;
        var maneuverDetail = rating.RcsThrust > 0
            ? $"Maneuver is mass ÷ RCS thrust: {rating.Mass:#,0} kg ÷ {rating.RcsThrust:#,0.#} = " +
              $"{rating.Mass / rating.RcsThrust:#,0.#} (lower is better: <300 A, <500 B, <750 C, <1500 D, else E). " +
              $"Thrust-to-mass ratio: {rating.RcsThrust / rating.Mass:0.####} per kg " +
              $"({rating.RcsThrust * 1000 / rating.Mass:#,0.##} per tonne)."
            : "Maneuver is O: no RCS thrusters installed.";   // mass has its own slot above
        body.Children.Add(new TextBlock
        {
            Text = "Condition assumes a pristine build (A). Room count is your certified compartments. " +
                   "Mass sums the installed structure. In game the ship also carries its cargo, so a loaded one " +
                   "reads heavier there. " + maneuverDetail,
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 12),
        });

        AddPropulsion(body, report.Propulsion, onExtraMassChanged);

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

        SetBody(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body });
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

    /// <summary>
    /// The propulsion block: what the design can pull on RCS and on the torch, and how far it can pull it.
    /// The game surfaces none of this outside a nav console's Reserves / Course Plot / Torch Drive modules,
    /// so a planner has to recompute it (see <see cref="Propulsion"/>).
    /// <para>The extra-mass box re-reads the figures in place via <see cref="PropulsionEstimate.WithExtraMass"/>,
    /// which is pure arithmetic over the already-measured counts, so hauling a different load never re-analyses
    /// the ship. <paramref name="persist"/> writes it back onto the document so it saves with the design.</para>
    /// </summary>
    private static void AddPropulsion(Panel body, PropulsionEstimate baseline, Action<double>? persist)
    {
        body.Children.Add(Header("PROPULSION"));

        var slots = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, 0, 4) };
        var (rcsAccelBox, rcsAccel) = LiveSlot("RCS accel");
        var (deltaVBox, deltaV) = LiveSlot("RCS delta-v");
        var (torchAccelBox, torchAccel) = LiveSlot("Torch accel");
        var (reactantBox, reactant) = LiveSlot("Reactant");
        slots.Children.Add(rcsAccelBox);
        slots.Children.Add(deltaVBox);
        slots.Children.Add(torchAccelBox);
        slots.Children.Add(reactantBox);
        body.Children.Add(slots);

        var massLine = new TextBlock { Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) };
        body.Children.Add(massLine);

        // Extra mass: the game's own docked-mass path (Ship.RCSAccelMax divides by this ship's mass plus every
        // docked ship's), which is what makes it the right model for a tug or a salvage hauler. Named "dead
        // weight" rather than "extra mass" because the obvious misreadings are "extra fuel" (it is the opposite:
        // it adds no reaction mass) and "my cargo" (which the game does not weigh at all) — the hint below says
        // both out loud, since a figure that only ever gets worse is not what someone typing a number expects.
        var haul = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        haul.Children.Add(new TextBlock
        {
            Text = "Dead weight to haul", Foreground = Dim, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
        });
        var extraBox = new TextBox
        {
            Width = 110, Text = baseline.ExtraMass > 0 ? baseline.ExtraMass.ToString("0.###") : "",
            VerticalContentAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right,
        };
        haul.Children.Add(extraBox);
        haul.Children.Add(new TextBlock
        {
            Text = "kg", Foreground = Dim, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
        });
        body.Children.Add(haul);
        body.Children.Add(new TextBlock
        {
            Text = "Mass the layout itself does not carry: a ship under tow, or a hold full of salvage. "
                 + "This is not fuel. It adds no reaction mass, so every figure above only gets worse as you "
                 + "raise it. Stowed container cargo weighs nothing in game either, so put it here if you want "
                 + "it counted. Saved with the design.",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        var detail = new TextBlock { Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
        body.Children.Add(detail);

        // Faults are the point of the feature, not an afterthought: they name the missing link rather than
        // leaving a bare dash. They depend only on the layout, so they never change as the haul mass does.
        foreach (var note in baseline.RcsNotes.Concat(baseline.TorchNotes))
            body.Children.Add(new TextBlock
            {
                Text = note, Foreground = Warn, FontSize = 11,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2),
            });

        void Refresh()
        {
            var p = baseline.WithExtraMass(ParseKg(extraBox.Text));

            rcsAccel.Text = p.HasRcsFigures ? Gs(p.RcsAccelG) : Dash;
            deltaV.Text = p.HasRcsFigures && p.RcsReactionMass > 0 ? Speed(p.RcsDeltaV) : Dash;
            torchAccel.Text = p.HasTorchFigures ? Gs(p.TorchAccelG) : Dash;
            reactant.Text = p.HasTorchFigures && p.ReactantSeconds > 0 ? Hours(p.ReactantHours) : Dash;

            // The cargo caveat lives beside the input box, where it is actionable, rather than being repeated here.
            massLine.Text = $"Mass for propulsion: {p.PartsMass:#,0} kg of placed parts"
                + (p.LooseMass > 0 ? $" + {p.LooseMass:#,0} kg loose on deck" : "")
                + (p.ExtraMass > 0 ? $" + {p.ExtraMass:#,0} kg dead weight" : "")
                + $" = {p.Mass:#,0} kg. Gas never counts toward part mass, so burning reaction mass does not "
                + "lighten the ship.";

            var lines = new List<string>();
            if (p.HasRcsFigures)
            {
                var thrusters = $"RCS: {p.RcsClustersPresent} cluster{(p.RcsClustersPresent == 1 ? "" : "s")} "
                    + $"giving {p.RcsThrustNewtons / 1000:#,0.#} kN.";
                if (p.RcsReactionMass > 0)
                    thrusters += $" Reaction mass {p.RcsReactionMass:#,0.#} kg of {p.RcsReactionMassMax:#,0.#} kg"
                        + $" across {p.RcsTankCount} feed position{(p.RcsTankCount == 1 ? "" : "s")}"
                        + $"; brim-full that is {Speed(p.RcsDeltaVFull)}.";
                lines.Add(thrusters);
                lines.Add("Delta-v is set by reaction mass over ship mass alone (the thruster count cancels out of "
                    + "the game's own expression), so more thrusters buy acceleration, never range.");
            }
            if (p.HasTorchFigures)
                lines.Add($"Torch: pellet max {p.PelletMax:0.#} from {p.Lasers} laser array{(p.Lasers == 1 ? "" : "s")} / "
                    + $"{p.Capacitors} capacitor{(p.Capacitors == 1 ? "" : "s")} / {p.Feeders} feeder{(p.Feeders == 1 ? "" : "s")} / "
                    + $"{p.Regulators} regulator{(p.Regulators == 1 ? "" : "s")}, giving {p.TorchThrustNewtons / 1000:#,0} kN at full cycle."
                    + (p.ReactantSeconds > 0 ? $" {p.LimitingReactant} runs out first, at full flow." : ""));
            else if (p.HasReactor)
                lines.Add("Torch figures assume the reactor lit at full cycle and at its ideal core temperature, "
                    + "which is what \"max\" means here; a planned reactor is always installed unlit.");
            detail.Text = string.Join(" ", lines);
        }

        extraBox.TextChanged += (_, _) =>
        {
            Refresh();
            persist?.Invoke(ParseKg(extraBox.Text));
        };
        Refresh();
    }

    private const string Dash = "--";

    private static (UIElement Box, TextBlock Value) LiveSlot(string caption)
    {
        var value = new TextBlock { Foreground = Ink, FontSize = 18, FontWeight = FontWeights.SemiBold };
        var sp = new StackPanel { Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Bottom };
        sp.Children.Add(value);
        sp.Children.Add(new TextBlock { Text = caption, Foreground = Dim, FontSize = 10 });
        return (sp, value);
    }

    /// <summary>Tolerant kg parse: blank, junk or a negative reads as no extra mass rather than rejecting a keystroke
    /// mid-edit (typing "-" or "1e" should not clear the field).</summary>
    private static double ParseKg(string? text) =>
        double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out var kg)
        && double.IsFinite(kg) && kg > 0 ? kg : 0;

    private static string Gs(double g) => $"{g:0.00} G";

    private static string Speed(double metresPerSecond) => metresPerSecond >= 1000
        ? $"{metresPerSecond / 1000:#,0.00} km/s"
        : $"{metresPerSecond:#,0.#} m/s";

    private static string Hours(double h) => h >= 1000 ? $"{h:#,0} h" : $"{h:0.0} h";

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
