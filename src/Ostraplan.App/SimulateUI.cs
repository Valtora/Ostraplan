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
    private readonly TextBlock _pathLabel = new();
    private readonly Slider _speed = new()
    {
        Minimum = 0, Maximum = MicrometeoroidStrike.MaxClosingSpeedMs, Value = 750,
    };
    private readonly TextBlock _speedLabel = new();
    private readonly ComboBox _attackBox = new();
    private readonly TextBlock _frameLine = new();
    private readonly TextBlock _resultLine = new();
    private readonly TextBlock _tallyLine = new();

    private StrikeAnchor _anchor;
    private ((double X, double Y) Start, (double X, double Y) End)? _path;

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

        _speed.ValueChanged += (_, _) => UpdateLabels();
        _tabs.SelectionChanged += (_, _) => UpdateLabels();
        _board.StrikePathDrawn += OnPathDrawn;

        Loaded += (_, _) =>
        {
            _board.SetAiming(true, new Point(_anchor.DocX, _anchor.DocY));
            UpdateLabels();
        };
        Closed += (_, _) =>
        {
            _board.StrikePathDrawn -= OnPathDrawn;
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
        mm.Children.Add(Label("Closing speed"));
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
        wp.Children.Add(new TextBlock
        {
            Text = "Draw the path the same way. A missile still detonates on the first structural tile it meets "
                 + "along it, and the blast falls off from there.",
            Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), FontSize = 11,
        });

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

        _pathLabel.Foreground = Dim;
        _pathLabel.TextWrapping = TextWrapping.Wrap;
        _pathLabel.Margin = new Thickness(12, 10, 12, 6);

        var root = new StackPanel();
        root.Children.Add(_pathLabel);
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

    /// <summary>A path was drawn on the plan. Dragging previews it; releasing arms it, and firing on release is
    /// what makes the tool feel like aiming rather than filling in a form.</summary>
    private void OnPathDrawn(Point start, Point end, bool committed)
    {
        _board.SetGhostPath((start, end));
        _path = ((start.X, start.Y), (end.X, end.Y));
        UpdateLabels();
        if (committed) Fire();
    }

    private void UpdateLabels()
    {
        var speed = _speed.Value;
        var mult = MicrometeoroidStrike.MultiplierFor(speed);
        _speedLabel.Text = $"{speed:0} m/s  ({mult:0.0}× the ATC limit, worst case "
                         + $"{MicrometeoroidStrike.WorstCasePool(speed):0} damage)";
        _speedLabel.Foreground = Dim;

        _pathLabel.Text = _path is { } p
            ? $"Path: ({p.Start.X:0.0}, {p.Start.Y:0.0}) → ({p.End.X:0.0}, {p.End.Y:0.0}).  "
              + "Drag another to fire again."
            : "Drag a line across the plan to set the path a strike takes, from where it comes in to where it "
              + "leaves. Releasing fires it.";

        _board.SetAiming(true, IsMicrometeoroid ? new Point(_anchor.DocX, _anchor.DocY) : null);

        _frameLine.Text = IsMicrometeoroid
            ? "You may draw any path, including ones the game itself cannot produce: in Ostranauts every "
            + "micrometeoroid runs through the single marked point, so a part no line through it reaches is one "
            + "the game will never chip. Draw through the marker to see what really happens to this hull, and "
            + "anywhere else to ask what a hit there would cost."
            + (_anchor.Frame == StrikeFrame.AsImported
                ? ""
                : " This design has no anchor of its own yet, so the marker is where one Ostraplan exports will "
                + "sit. Import the ship from your save to measure the hull you are actually flying.")
            : "The path is yours to draw. The game would only ever start an impact on the hull line, but what "
            + "happens along the path once drawn is its own arithmetic.";
    }

    // ---- firing ----

    private void Fire()
    {
        if (IsMicrometeoroid)
        {
            if (_path is not { } path) return;
            var r = MicrometeoroidStrike.Fire(_doc, path.Start, path.End, _speed.Value, _state);
            _resultLine.Text = r.Missed
                ? "Missed. That path crossed nothing able to absorb it."
                : Describe(r.Hits.Count, r.Delivered, r.Hits.Count(h => h.ToDef is null && h.Broke));
            _resultLine.Foreground = r.Missed ? Dim : Ink;
        }
        else
        {
            if (SelectedAttack is not { } attack) return;
            if (_path is not { } path) return;
            if (WeaponImpact.EntryAlong(_doc, path.Start, path.End) is not { } entry) return;
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
