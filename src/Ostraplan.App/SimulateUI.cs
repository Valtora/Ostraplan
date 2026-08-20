using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// Simulate: fire a strike at the design and see what it breaks.
///
/// <para>Two solvers behind one window, because the game has two unrelated damage systems (§26). A
/// <b>micrometeoroid</b> raycasts the sprite colliders and advances each part exactly one break stage; a
/// <b>weapon impact</b> walks the tile grid and prices each cell against the whole break chain, so a missile can
/// take a wall from whole to gone where no micrometeoroid ever will. Sharing a window is a UI convenience; the
/// two never share a code path.</para>
///
/// <para><b>The pivot is not yours to place.</b> The game aims every micrometeoroid at world origin rather than at
/// the ship, so the approach angle is the only free parameter and the convergence point falls out of the ship's
/// own grid anchor. The window draws that point and lets you swing the ghost path about it. The weapon side is
/// the opposite: its aim point is yours, constrained to the bounding-box crossing the game's own
/// <c>FindIntersection</c> computes.</para>
///
/// <para>Damage accumulates across strikes and lives in a <see cref="DamageState"/> beside the document, never in
/// it. Closing the window throws it away, which is what makes "start over" free.</para>
/// </summary>
public sealed class SimulateWindow : Window
{
    private static Brush Ink => ThemeManager.Ink;
    private static Brush Dim => ThemeManager.Dim;
    private static Brush Warn => ThemeManager.Warn;

    private readonly ShipCanvas _board;
    private readonly ShipDocument _doc;
    private readonly DamageState _state = new();

    private readonly TabControl _tabs = new();
    private readonly Slider _angle = new() { Minimum = 0, Maximum = 359.9, Value = 45 };
    private readonly TextBlock _angleLabel = new();
    private readonly Slider _speed = new()
    {
        Minimum = 0, Maximum = MicrometeoroidStrike.MaxClosingSpeedMs, Value = 750,
    };
    private readonly TextBlock _speedLabel = new();
    private readonly ComboBox _attackBox = new();
    private readonly Slider _alongEdge = new() { Minimum = 0, Maximum = 1, Value = 0.5 };
    private readonly TextBlock _frameLine = new();
    private readonly TextBlock _resultLine = new();
    private readonly TextBlock _tallyLine = new();

    private StrikeAnchor _anchor;

    public SimulateWindow(ShipCanvas board, ShipDocument doc)
    {
        _board = board;
        _doc = doc;
        _anchor = MicrometeoroidStrike.AnchorFor(doc);

        Title = "Simulate";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.WindowBg;
        ResizeMode = ResizeMode.NoResize;

        Content = BuildBody();

        _angle.ValueChanged += (_, _) => { UpdateLabels(); RefreshGhost(); };
        _speed.ValueChanged += (_, _) => UpdateLabels();
        _alongEdge.ValueChanged += (_, _) => RefreshGhost();
        _tabs.SelectionChanged += (_, _) => { UpdateLabels(); RefreshGhost(); };
        _board.AimPointChanged += OnAimPoint;

        Loaded += (_, _) =>
        {
            _board.SetAiming(true, new Point(_anchor.DocX, _anchor.DocY));
            UpdateLabels();
            RefreshGhost();
        };
        Closed += (_, _) =>
        {
            _board.AimPointChanged -= OnAimPoint;
            _board.SetAiming(false);
            _board.SetDamageOverlay(DamageOverlay.Empty);
        };
    }

    private bool IsMicrometeoroid => _tabs.SelectedIndex == 0;

    /// <summary>Open on a given solver: 0 micrometeoroid, 1 weapon impact.</summary>
    public void SelectTab(int index) => _tabs.SelectedIndex = Math.Clamp(index, 0, _tabs.Items.Count - 1);

    // ---- layout ----

    private UIElement BuildBody()
    {
        var mm = new StackPanel { Margin = new Thickness(12) };
        mm.Children.Add(Label("Approach angle"));
        mm.Children.Add(_angle);
        mm.Children.Add(_angleLabel);
        mm.Children.Add(Label("Closing speed", new Thickness(0, 10, 0, 0)));
        mm.Children.Add(_speed);
        mm.Children.Add(_speedLabel);
        mm.Children.Add(new TextBlock
        {
            Text = "Only Earth's stratosphere, mesosphere and orbital shells produce micrometeoroids at all, "
                 + "and a circular orbit there closes at about 7.4 km/s. Matching the body's velocity still takes "
                 + "half-strength strikes.",
            Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), FontSize = 11,
        });

        var wp = new StackPanel { Margin = new Thickness(12) };
        wp.Children.Add(Label("Weapon"));
        wp.Children.Add(_attackBox);
        wp.Children.Add(Label("Approach angle", new Thickness(0, 10, 0, 0)));
        wp.Children.Add(new TextBlock
        {
            Text = "The aim point is the crossing of that heading with the ship's bounding box, which is the only "
                 + "place the game lets an impact begin.",
            Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6), FontSize = 11,
        });
        wp.Children.Add(Label("Offset along that edge", new Thickness(0, 6, 0, 0)));
        wp.Children.Add(_alongEdge);

        _tabs.Items.Add(new TabItem { Header = "Micrometeoroid", Content = mm });
        _tabs.Items.Add(new TabItem { Header = "Weapon impact", Content = wp });

        var fire = new Button { Content = "Fire", Padding = new Thickness(18, 4, 18, 4), IsDefault = true };
        fire.Click += (_, _) => Fire();
        var clear = new Button
        {
            Content = "Start over", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0),
        };
        clear.Click += (_, _) =>
        {
            _state.Clear();
            _board.SetDamageOverlay(DamageOverlay.Empty);
            _resultLine.Text = "";
            UpdateTally();
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(12, 4, 12, 12),
        };
        buttons.Children.Add(fire);
        buttons.Children.Add(clear);

        _frameLine.Foreground = Dim;
        _frameLine.TextWrapping = TextWrapping.Wrap;
        _frameLine.FontSize = 11;
        _frameLine.Margin = new Thickness(12, 8, 12, 0);

        _resultLine.Foreground = Ink;
        _resultLine.TextWrapping = TextWrapping.Wrap;
        _resultLine.Margin = new Thickness(12, 8, 12, 0);

        _tallyLine.Foreground = Dim;
        _tallyLine.Margin = new Thickness(12, 4, 12, 0);
        _tallyLine.FontSize = 11;

        var root = new StackPanel();
        root.Children.Add(_tabs);
        root.Children.Add(_frameLine);
        root.Children.Add(_resultLine);
        root.Children.Add(_tallyLine);
        root.Children.Add(buttons);
        return root;
    }

    private static TextBlock Label(string text, Thickness? margin = null) => new()
    {
        Text = text, Foreground = Ink, Margin = margin ?? new Thickness(0, 0, 0, 2),
    };

    /// <summary>Fill the weapon list from the loaded data, so a mod's attack appears without a code change.</summary>
    public void SetAttacks(IReadOnlyDictionary<string, ShipAttackDef> attacks)
    {
        _attackBox.Items.Clear();
        foreach (var a in attacks.Values.OrderByDescending(a => a.TotalDamage))
            _attackBox.Items.Add(new ComboBoxItem { Content = $"{a.Name}  ({a.TotalDamage:0} dmg)", Tag = a });
        if (_attackBox.Items.Count > 0) _attackBox.SelectedIndex = 0;
    }

    private ShipAttackDef? SelectedAttack => (_attackBox.SelectedItem as ComboBoxItem)?.Tag as ShipAttackDef;

    // ---- aiming ----

    private void OnAimPoint(Point doc)
    {
        if (IsMicrometeoroid)
        {
            // The solver's own inverse, so the drag and the strike can never disagree. Null means the cursor names
            // no reachable approach, and the angle simply stays where it was.
            if (MicrometeoroidStrike.AngleFrom(_doc, _anchor, (doc.X, doc.Y)) is { } deg) _angle.Value = deg;
        }
        else
        {
            var b = _doc.Bounds();
            if (b is null) return;
            var cx = (b.Value.MinX + b.Value.MaxX) / 2.0;
            var cy = (b.Value.MinY + b.Value.MaxY) / 2.0;
            var deg = Math.Atan2(cy - doc.Y, cx - doc.X) * 180.0 / Math.PI;
            _angle.Value = deg < 0 ? deg + 360 : deg;
        }
    }

    private void RefreshGhost()
    {
        if (IsMicrometeoroid)
        {
            var (s, e) = MicrometeoroidStrike.GhostPath(_doc, _anchor, _angle.Value);
            _board.SetGhostPath(s == e ? null : (new Point(s.X, s.Y), new Point(e.X, e.Y)));
        }
        else if (WeaponImpact.EntryFor(_doc, _angle.Value, _alongEdge.Value) is { } entry)
        {
            var len = 200.0;
            _board.SetGhostPath((new Point(entry.DocX, entry.DocY),
                                 new Point(entry.DocX + entry.DirX * len, entry.DocY + entry.DirY * len)));
        }
        else _board.SetGhostPath(null);
    }

    private void UpdateLabels()
    {
        _angleLabel.Text = $"{_angle.Value:0.0}°";
        _angleLabel.Foreground = Dim;

        var speed = _speed.Value;
        var mult = MicrometeoroidStrike.MultiplierFor(speed);
        _speedLabel.Text = $"{speed:0} m/s  ({mult:0.0}× the ATC limit, worst case "
                         + $"{MicrometeoroidStrike.WorstCasePool(speed):0} damage)";
        _speedLabel.Foreground = Dim;

        _board.SetAiming(true, IsMicrometeoroid ? new Point(_anchor.DocX, _anchor.DocY) : null);

        _frameLine.Text = IsMicrometeoroid
            ? _anchor.Frame == StrikeFrame.AsImported
                ? "Every micrometeoroid converges on the marked point, which is where this ship's own grid anchor "
                + "puts it. That is the game aiming at world origin rather than at the ship, and it means the "
                + "angle is the only thing that varies."
                : "Every micrometeoroid converges on the marked point. This design has no anchor of its own yet, so "
                + "that is where one Ostraplan exports will sit: just outside the top-left corner. Import the ship "
                + "from your save to measure the hull you are actually flying."
            : "The aim point is constrained to the bounding box, which is the only place the game starts an impact.";
    }

    // ---- firing ----

    private void Fire()
    {
        if (IsMicrometeoroid)
        {
            var r = MicrometeoroidStrike.Fire(_doc, _anchor, _angle.Value, _speed.Value, _state);
            _resultLine.Text = r.Missed
                ? r.StartDoc == r.EndDoc
                    ? "That exact angle fires nothing: the rock starts on the convergence point itself, and the "
                    + "game's raycast travels nowhere. Nudge the angle."
                    : "Missed. The ray crossed without finding anything able to absorb it."
                : Describe(r.Hits.Count, r.Delivered, r.Hits.Count(h => h.ToDef is null && h.Broke));
            _resultLine.Foreground = r.Missed ? Dim : Ink;
        }
        else
        {
            if (SelectedAttack is not { } attack) return;
            if (WeaponImpact.EntryFor(_doc, _angle.Value, _alongEdge.Value) is not { } entry) return;
            var r = WeaponImpact.Fire(_doc, attack, entry, _state);
            _resultLine.Text = r.Missed
                ? "Missed. Nothing along that line could absorb it."
                : Describe(r.Hits.Select(h => h.PlacementId).Distinct().Count(), r.Delivered,
                           r.Hits.Count(h => h.Destroyed));
            _resultLine.Foreground = r.Missed ? Dim : Ink;
        }

        _board.SetDamageOverlay(DamageOverlay.Build(_doc, _state));
        UpdateTally();
    }

    private static string Describe(int parts, double delivered, int destroyed)
    {
        var s = $"{parts} part{(parts == 1 ? "" : "s")} hit for {delivered:0} damage";
        return destroyed > 0 ? $"{s}, {destroyed} destroyed." : $"{s}.";
    }

    private void UpdateTally()
    {
        var ov = DamageOverlay.Build(_doc, _state);
        if (ov.IsEmpty)
        {
            _tallyLine.Text = "The ship is undamaged.";
            _tallyLine.Foreground = Dim;
            return;
        }
        _tallyLine.Text = $"Run so far: {ov.Parts.Count} part{(ov.Parts.Count == 1 ? "" : "s")} damaged, "
                        + $"{ov.Destroyed} destroyed.";
        _tallyLine.Foreground = ov.Destroyed > 0 ? Warn : Dim;
    }
}
