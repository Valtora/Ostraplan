using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>Which of the game's two damage systems the window is pointed at. Chosen from the Simulate menu and
/// nowhere else, so there is one switch rather than two that can disagree.</summary>
public enum SimulateMode { Micrometeoroid, WeaponImpact }

/// <summary>
/// Simulate: fire a strike at the design and see what it breaks.
///
/// <para>Two solvers, one at a time, because the game has two unrelated damage systems (§26). A
/// <b>micrometeoroid</b> raycasts the sprite colliders and advances each part exactly one break stage; a
/// <b>weapon impact</b> walks the tile grid and prices each cell against the whole break chain, so a missile can
/// take a wall from whole to gone where no micrometeoroid ever will. The two never share a code path.</para>
///
/// <para><b>The Simulate menu is the only switch.</b> The window shows the solver it was opened on and offers no
/// way to change it, so there is one place that decides rather than two disagreeing. Picking the other entry from
/// the menu re-points the window in place and keeps the damage run, because that run is a property of the ship
/// rather than of the solver being pointed at it.</para>
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

    private readonly TextBlock _pathLabel = new();
    // Floored at the speed below which the multiplier stops moving and capped at the fastest shell the game
    // authors, so every position on the track is a strike the game can actually deliver.
    private readonly Slider _speed = new()
    {
        Minimum = MicrometeoroidStrike.MinClosingSpeedMs,
        Maximum = MicrometeoroidStrike.MaxClosingSpeedMs,
        Value = MicrometeoroidStrike.TensionBeatSpeedMs,
    };
    private readonly TextBlock _speedLabel = new();
    private readonly ComboBox _attackBox = new();
    private readonly TextBlock _frameLine = new();
    private readonly TextBlock _resultLine = new();
    private readonly TextBlock _tallyLine = new();
    private readonly StackPanel _meteoroidPanel = new() { Margin = new Thickness(12, 8, 12, 0) };
    private readonly StackPanel _weaponPanel = new() { Margin = new Thickness(12, 8, 12, 0) };

    private StrikeAnchor _anchor;
    private ((double X, double Y) Start, (double X, double Y) End)? _path;
    private int _shots;
    private SimulateMode _mode = SimulateMode.Micrometeoroid;

    public SimulateWindow(ShipCanvas board, ShipDocument doc)
    {
        _board = board;
        _doc = doc;
        _anchor = MicrometeoroidStrike.AnchorFor(doc);

        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.WindowBg;
        ResizeMode = ResizeMode.NoResize;

        Content = BuildBody();
        SetMode(_mode);

        _speed.ValueChanged += (_, _) => UpdateLabels();
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

    private bool IsMicrometeoroid => _mode == SimulateMode.Micrometeoroid;

    /// <summary>
    /// Point the window at one solver. Called on open and again whenever the Simulate menu is used while the
    /// window is up, which is the only way to change it: the damage run survives the change, because it belongs
    /// to the ship rather than to whatever is being fired at it.
    /// </summary>
    public void SetMode(SimulateMode mode)
    {
        _mode = mode;
        _meteoroidPanel.Visibility = IsMicrometeoroid ? Visibility.Visible : Visibility.Collapsed;
        _weaponPanel.Visibility = IsMicrometeoroid ? Visibility.Collapsed : Visibility.Visible;
        Title = IsMicrometeoroid ? "Simulate — Micrometeoroid strike" : "Simulate — Weapon impact";
        UpdateLabels();
    }

    // ---- layout ----

    private UIElement BuildBody()
    {
        _meteoroidPanel.Children.Add(Label("Impact velocity"));
        _meteoroidPanel.Children.Add(_speed);
        _meteoroidPanel.Children.Add(_speedLabel);

        _weaponPanel.Children.Add(Label("Weapon"));
        _weaponPanel.Children.Add(_attackBox);

        var fire = new Button { Content = "Fire", Padding = new Thickness(18, 4, 18, 4), IsDefault = true };
        fire.Click += (_, _) => Fire();
        var clear = new Button
        {
            Content = "Start over", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0),
        };
        clear.Click += (_, _) =>
        {
            _state.Clear();
            _shots = 0;
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
        root.Children.Add(_meteoroidPanel);
        root.Children.Add(_weaponPanel);
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
              + "Press Fire to hit it again, or drag a new one."
            : "Drag a line across the plan to set the path a strike takes, from where it comes in to where it "
              + "leaves. Releasing fires it.";

        _board.SetAiming(true, IsMicrometeoroid ? new Point(_anchor.DocX, _anchor.DocY) : null);

        // The only note left in the window, because without it the marker is quietly the wrong one: a design with
        // no anchor of its own is being measured in the frame it would be exported into, not the frame it flies in.
        var borrowed = IsMicrometeoroid && _anchor.Frame != StrikeFrame.AsImported;
        _frameLine.Text = borrowed
            ? "This design has no anchor of its own yet, so the marker is where one Ostraplan exports will sit. "
            + "Import the ship from your save to measure the hull you are actually flying."
            : "";
        _frameLine.Visibility = borrowed ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- firing ----

    private void Fire()
    {
        bool landed;
        if (IsMicrometeoroid)
        {
            if (_path is not { } path) return;
            var r = MicrometeoroidStrike.Fire(_doc, path.Start, path.End, _speed.Value, _state);
            landed = !r.Missed;
            _resultLine.Text = r.Missed
                ? "Missed. That path crossed nothing able to absorb it."
                : Describe(r.Hits.Count, r.Delivered, r.Hits.Count(h => h.ToDef is null && h.Broke));
        }
        else
        {
            if (SelectedAttack is not { } attack) return;
            if (_path is not { } path) return;
            if (WeaponImpact.EntryAlong(_doc, path.Start, path.End) is not { } entry) return;
            var r = WeaponImpact.Fire(_doc, attack, entry, _state);
            landed = !r.Missed;
            // A shot that went off and delivered nothing is not a miss, and saying so would be the difference
            // between "aim somewhere else" and "this hull has nothing left to give here".
            _resultLine.Text = r switch
            {
                { Missed: true, Centre: { } spent } =>
                    $"Went off at ({spent.X}, {spent.Y}), but everything within reach of it was already spent.",
                { Missed: true } => "Missed. Nothing along that line was left to hit.",
                _ => Describe(r.Hits.Select(h => h.PlacementId).Distinct().Count(), r.Delivered,
                              r.Hits.Count(h => h.Destroyed))
                     + (r.Centre is { } c ? $"  Went off at ({c.X}, {c.Y})." : ""),
            };
        }

        _resultLine.Foreground = landed ? Ink : Dim;
        // Only a shot that landed counts. Counting every press would say "after 7 hits" for a hull six of them
        // never touched, which is the one number the tally exists to get right.
        if (landed) _shots++;
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
        // The shot count is the point of hitting one line repeatedly: what is destroyed no longer absorbs, so each
        // pass reaches further in and the tally answers "how many of these would it take to get through".
        _tallyLine.Text = $"After {_shots} hit{(_shots == 1 ? "" : "s")}: {ov.Parts.Count} "
                        + $"part{(ov.Parts.Count == 1 ? "" : "s")} damaged, {ov.Destroyed} destroyed.";
        _tallyLine.Foreground = ov.Destroyed > 0 ? Warn : Dim;
    }
}
