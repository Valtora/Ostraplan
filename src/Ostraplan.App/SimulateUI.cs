using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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

    /// <summary>
    /// How hard the strike hits, <b>in damage</b>.
    ///
    /// <para>The game parameterises this as a closing velocity scaled against a constant, and it used to be
    /// offered that way: a slider in metres per second, labelled with a multiple of a speed limit the tool never
    /// explained and the game never names to a player. Neither number is one a hull meets. What a part meets is a
    /// pool of damage, which is the unit every other figure here is already in, so that is the unit the control is
    /// in and the velocity is worked out on the way to the solver.</para>
    ///
    /// <para>Bounded by what the game can actually produce, so every position on the track is a real strike:
    /// the multiplier floors at the bottom, and the top is the fastest shell the game authors.</para>
    /// </summary>
    private readonly Slider _damage = new()
    {
        Minimum = MicrometeoroidStrike.MinDamage,
        // Opens at the standard strike and on a range of one until the bodies arrive, so a window shown before
        // (or without) game data can never offer a strength the data does not stand behind.
        Maximum = MicrometeoroidStrike.StandardDamage,
        Value = MicrometeoroidStrike.StandardDamage,
        // One tick, on the only figure in the range that is a real place: the strike you get everywhere the game
        // is actually played. Without it the track reads as a continuum with no landmark, and the default position
        // looks like a midpoint rather than the answer.
        Ticks = [MicrometeoroidStrike.StandardDamage],
        TickPlacement = System.Windows.Controls.Primitives.TickPlacement.BottomRight,
        IsSnapToTickEnabled = false,
    };
    private readonly TextBox _damageBox = new()
    {
        Width = 64, TextAlignment = TextAlignment.Right, Padding = new Thickness(4, 2, 4, 2),
        VerticalContentAlignment = VerticalAlignment.Center,
        ToolTip = "Type a damage figure, or drag the slider",
    };
    private readonly Button _damageReset = new()
    {
        Content = "Reset", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0), FontSize = 11,
    };
    private readonly TextBlock _speedLabel = new();

    /// <summary>Guards the slider and the box against each other: each writes the other, and without this a typed
    /// digit is reformatted under the caret on its way round the loop.</summary>
    private bool _syncingDamage;
    private readonly ComboBox _attackBox = new();
    private readonly TextBlock _resultLine = new();
    private readonly TextBlock _tallyLine = new();
    private readonly StackPanel _meteoroidPanel = new() { Margin = new Thickness(12, 8, 12, 0) };
    private readonly StackPanel _weaponPanel = new() { Margin = new Thickness(12, 8, 12, 0) };

    /// <summary>What the three marks on the plan mean. Shown only once there is something on the plan to read.
    /// The overlay used to be a continuous green-to-red ramp with no key at all, which left every reader to invent
    /// their own thresholds for a scale that has none.</summary>
    private readonly StackPanel _legend = new()
    {
        Margin = new Thickness(12, 8, 12, 0), Visibility = Visibility.Collapsed,
    };

    private readonly StackPanel _changesList = new();
    private readonly Border _changesBox = new() { Visibility = Visibility.Collapsed };

    /// <summary>
    /// What the strike broke <b>about the ship</b>, as against about the parts: a compartment opened to vacuum, a
    /// device the crew can no longer reach, a run of conduit cut, a system that has stopped working.
    ///
    /// <para>This is the half a part count cannot reach. "4 parts damaged" is the same sentence whether the ship
    /// still holds air or not. See <see cref="DamageFallout"/> for what is compared and why it is not the design
    /// warning scan. What is still out of scope is what happens <i>next</i> — the fire, the venting, the reactor
    /// cooking off.</para>
    /// </summary>
    private readonly StackPanel _falloutList = new();
    private readonly Border _falloutBox = new() { Visibility = Visibility.Collapsed };

    /// <summary>The intact ship's own answers, so only what the strike cost is reported. Computed once on the
    /// first shot and kept: the document cannot be edited from behind a modeless report without the window being
    /// rebuilt, and a strike never edits it.</summary>
    private DamageBaseline? _baseline;

    /// <summary>Discards a consequence scan that a later strike has already superseded. The scans run off-thread
    /// and a user firing repeatedly at one line will start several, which can finish in any order.</summary>
    private int _falloutGeneration;

    private StrikeAnchor _anchor;

    /// <summary>The drawn path, in the canvas's own <b>corner</b> frame (<see cref="TileFrame"/>) — the frame the
    /// ghost line is drawn in and the one the coordinates are reported in, so both match the tile readout in the
    /// status bar. It is converted to the solver's centre frame at the moment of firing and nowhere else.</summary>
    private ((double X, double Y) Start, (double X, double Y) End)? _path;
    private int _shots;
    private SimulateMode _mode = SimulateMode.Micrometeoroid;

    public SimulateWindow(ShipCanvas board, ShipDocument doc)
    {
        _board = board;
        _doc = doc;
        _anchor = MicrometeoroidStrike.AnchorFor(doc);

        // A declared height rather than SizeToContent, because two list-shaped panels live in here and a window
        // that sizes to its content has no ceiling of its own (CONVENTIONS.md). Resizable on top of that: how much
        // room the lists want depends on how much damage was done, which is not something a default can know.
        Width = 470; Height = 660;
        MinWidth = 400; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.WindowBg;
        ResizeMode = ResizeMode.CanResize;

        Content = BuildBody();
        SetMode(_mode);

        _damage.ValueChanged += (_, _) =>
        {
            if (_syncingDamage) return;
            _syncingDamage = true;
            _damageBox.Text = $"{_damage.Value:0}";
            _syncingDamage = false;
            UpdateLabels();
        };
        _damageBox.Text = $"{_damage.Value:0}";
        _damageBox.TextChanged += (_, _) =>
        {
            if (_syncingDamage) return;
            // Only a figure that parses moves the slider. A half-typed one leaves it where it is rather than
            // snapping to the clamp, so typing "120" does not pass through 1 and 12 on the way.
            if (!double.TryParse(_damageBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var typed))
                return;
            _syncingDamage = true;
            _damage.Value = Math.Clamp(typed, _damage.Minimum, _damage.Maximum);
            _syncingDamage = false;
            UpdateLabels();
        };
        // Leaving the box is where a typed figure is settled up: out-of-range or unparseable text is replaced by
        // what the slider actually holds, so the two can never be left disagreeing on screen.
        _damageBox.LostFocus += (_, _) =>
        {
            _syncingDamage = true;
            _damageBox.Text = $"{_damage.Value:0}";
            _syncingDamage = false;
        };
        _damageReset.Click += (_, _) => _damage.Value = MicrometeoroidStrike.StandardDamage;

        _board.StrikePathDrawn += OnPathDrawn;

        Loaded += (_, _) =>
        {
            _board.SetAiming(true, PivotForCanvas());
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
        _meteoroidPanel.Children.Add(Label("Strike strength"));

        // The slider, the number and the way back to the default on one line. The box and the button are what a
        // slider on its own cannot give you: an exact figure to compare two runs at, and a way back from having
        // dragged it somewhere.
        var strengthRow = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        DockPanel.SetDock(_damageBox, Dock.Right);
        DockPanel.SetDock(_damageReset, Dock.Right);
        _damage.VerticalAlignment = VerticalAlignment.Center;
        _damage.Margin = new Thickness(0, 0, 8, 0);
        strengthRow.Children.Add(_damageReset);
        strengthRow.Children.Add(_damageBox);
        strengthRow.Children.Add(_damage);
        _meteoroidPanel.Children.Add(strengthRow);
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

        _resultLine.Foreground = Ink;
        _resultLine.TextWrapping = TextWrapping.Wrap;
        _resultLine.Margin = new Thickness(12, 8, 12, 0);

        _tallyLine.Foreground = Dim;
        _tallyLine.Margin = new Thickness(12, 4, 12, 0);
        _tallyLine.FontSize = 11;

        _pathLabel.Foreground = Dim;
        _pathLabel.TextWrapping = TextWrapping.Wrap;
        _pathLabel.Margin = new Thickness(12, 10, 12, 6);

        BuildLegend();

        _falloutList.Margin = new Thickness(8, 6, 8, 6);
        _falloutBox.BorderBrush = ThemeManager.Warn;
        _falloutBox.BorderThickness = new Thickness(1);
        _falloutBox.CornerRadius = new CornerRadius(3);
        _falloutBox.Margin = new Thickness(12, 8, 12, 0);
        _falloutBox.Child = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _falloutList,
        };

        _changesList.Margin = new Thickness(8, 6, 8, 6);
        _changesBox.BorderBrush = ThemeManager.PanelBorder;
        _changesBox.BorderThickness = new Thickness(1);
        _changesBox.CornerRadius = new CornerRadius(3);
        _changesBox.Margin = new Thickness(12, 8, 12, 12);
        _changesBox.Child = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _changesList,
        };

        // DockPanel, not StackPanel: the buttons dock to the bottom and stay there, and the body between them is
        // what gives way. A StackPanel hands every child its desired height whatever it is arranged into.
        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(buttons, Dock.Bottom);

        // The controls take the height they need and the two lists share everything left over, so growing the
        // window grows the lists rather than the padding above them. Star-sized rather than capped: the window has
        // a declared height now, so the lists can give way to it instead of having to guess a ceiling.
        var body = new Grid();
        for (var i = 0; i < 5; i++) body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        // The consequences list is the shorter of the two in practice (a handful of problems against a part per
        // line), so it gets a third of the space and the changes list the rest.
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });

        void Row(UIElement el, int row) { Grid.SetRow(el, row); body.Children.Add(el); }
        Row(_pathLabel, 0);
        Row(_meteoroidPanel, 1);
        Row(_weaponPanel, 1);      // shares the row with the panel it swaps against; only one is ever visible
        Row(_resultLine, 2);
        Row(_tallyLine, 3);
        Row(_legend, 4);
        // Above the part list: what it did to the ship outranks what it did to the parts.
        Row(_falloutBox, 5);
        Row(_changesBox, 6);

        root.Children.Add(buttons);
        root.Children.Add(body);
        return root;
    }

    /// <summary>The key to the plan: one row per state, each showing the mark it is drawn with as well as naming
    /// it, so the row can be matched to the plan by shape and not only by colour.</summary>
    private void BuildLegend()
    {
        _legend.Children.Add(new TextBlock
        {
            Text = "ON THE PLAN", Foreground = Dim, FontSize = 10, FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 3),
        });
        // The three names and their marks, on one line. No glosses: the words carry themselves, and three
        // explanatory clauses stacked above the lists were taking the room the lists needed.
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(LegendRow(DamageGrade.Chipped, "Damaged"));
        row.Children.Add(LegendRow(DamageGrade.Broken, "Broken"));
        row.Children.Add(LegendRow(DamageGrade.Destroyed, "Destroyed"));
        _legend.Children.Add(row);
    }

    private static UIElement LegendRow(DamageGrade grade, string name)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 16, 1) };
        row.Children.Add(new Border
        {
            Width = 18, Height = 11, CornerRadius = new CornerRadius(2),
            Background = ShipCanvas.LegendFill(grade),
            BorderBrush = ShipCanvas.LegendStroke(grade),
            BorderThickness = new Thickness(grade == DamageGrade.Chipped ? 1 : 2),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
        });
        row.Children.Add(new TextBlock
        {
            Text = name, Foreground = Ink, VerticalAlignment = VerticalAlignment.Center, FontSize = 11,
        });
        return row;
    }

    private static TextBlock Label(string text, Thickness? margin = null) => new()
    {
        Text = text, Foreground = Ink, Margin = margin ?? new Thickness(0, 0, 0, 2),
    };

    /// <summary>An English list: "Earth", "Earth and Ceres", "Earth, Ceres and Mars".</summary>
    private static string Join(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };

    /// <summary>
    /// Set the strength range from the game's own bodies.
    ///
    /// <para>The ceiling is data, not a constant: the game clamps <c>fMult</c> only at the bottom, so how hard a
    /// micrometeoroid can hit is decided by the fastest atmosphere band that declares one
    /// (<see cref="MicrometeoroidStrike.FastestClosingSpeed"/>). Without an install there is nothing to derive it
    /// from, and the control stays on the standard strike alone rather than offering a made-up range.</para>
    /// </summary>
    public void SetBodies(IReadOnlyList<CelestialBody> bodies)
    {
        var max = Math.Max(MicrometeoroidStrike.StandardDamage, MicrometeoroidStrike.MaxDamageFor(bodies));
        _syncingDamage = true;
        _damage.Maximum = max;
        _syncingDamage = false;
        // Which bodies can produce a stronger strike than the standard one, read off the same data the ceiling
        // is: in stock content that is Earth alone, and a mod that adds a micrometeoroid band is named too.
        _strongBodies = MicrometeoroidStrike.StrongStrikeBodies(bodies);
        UpdateLabels();
    }

    /// <summary>The bodies whose atmosphere can hit harder than <see cref="MicrometeoroidStrike.StandardDamage"/>.
    /// Empty until the game data arrives, and empty afterwards on data that declares none.</summary>
    private IReadOnlyList<string> _strongBodies = [];

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
        // Both solvers read a drawn line as an aim rather than a path, so the canvas carries it on past the drag.
        _board.SetGhostPath((start, end), extend: true);
        _path = ((start.X, start.Y), (end.X, end.Y));
        UpdateLabels();
        if (committed) Fire();
    }

    /// <summary>The convergence marker where the canvas wants it. The anchor is a solver-frame point and the canvas
    /// draws in its own frame, so this is the one crossing on the way out.</summary>
    private Point PivotForCanvas()
    {
        var (x, y) = TileFrame.CentreToCorner((_anchor.DocX, _anchor.DocY));
        return new Point(x, y);
    }

    /// <summary>The drawn path in the frame the solvers read. Both of them measure against colliders centred on a
    /// part's own position, so a canvas point handed over as-is aims half a tile up and to the left of the line on
    /// screen. This is the only place the two frames meet.</summary>
    private ((double X, double Y) Start, (double X, double Y) End)? SolverPath =>
        _path is not { } p ? null : (TileFrame.CornerToCentre(p.Start), TileFrame.CornerToCentre(p.End));

    private void UpdateLabels()
    {
        // Where the range comes from, in terms of where you fly rather than of how it is computed. The mechanism's
        // own vocabulary (the multiplier, the speed limit, the spawn sites, the shell names) still belongs to the
        // code: what is said here is only what a player can act on. It used to say nothing but "the range the game
        // allows", which left the obvious questions unanswered — "do the velocities have presets, is it a bell
        // curve, are certain ones only present in certain conditions" — when the data answers all three.
        var standard = $"{MicrometeoroidStrike.StandardDamage:0}";
        var where = _strongBodies.Count switch
        {
            0 => "Every micrometeoroid in the game hits at exactly " + standard + ".",
            _ => $"Anywhere you fly, a micrometeoroid hits at exactly {standard}. Only inside "
                 + Join(_strongBodies) + "'s atmosphere can one hit harder, and the faster you are going "
                 + "through it the harder it is.",
        };
        // The one thing the number on the slider does not say for itself: the game rolls the strength of every
        // strike and this is the top of that roll, so a hull that survives this survives all of them.
        _speedLabel.Text = where + " This is the hardest a strike can land: the game rolls under it.";
        _speedLabel.Foreground = Dim;
        _damageReset.IsEnabled = Math.Abs(_damage.Value - MicrometeoroidStrike.StandardDamage) >= 0.5;

        // A drawn line sets a heading, not a distance: it carries on to the far side of the ship, so how far you
        // drag decides the angle and nothing else. Both solvers read it that way.
        _pathLabel.Text = _path is { } p
            ? $"Aim: ({p.Start.X:0.0}, {p.Start.Y:0.0}) → ({p.End.X:0.0}, {p.End.Y:0.0}).  "
              + "Press Fire to hit it again, or drag a new one."
            : "Drag a line across the plan to aim. It carries on along that line until it hits something or "
              + "leaves the ship, however short the drag. Releasing fires it.";

        _board.SetAiming(true, IsMicrometeoroid ? PivotForCanvas() : null);
    }

    // ---- firing ----

    private void Fire()
    {
        bool landed;
        if (IsMicrometeoroid)
        {
            if (SolverPath is not { } path) return;
            // The control is in damage; the solver's parameter is the velocity that produces it.
            var speed = MicrometeoroidStrike.SpeedForDamage(_damage.Value);
            var r = MicrometeoroidStrike.Fire(_doc, path.Start, path.End, speed, _state);
            landed = !r.Missed;
            _resultLine.Text = r.Missed
                ? "Missed. That path crossed nothing able to absorb it."
                : Describe(TallyOf(r), r.Delivered);
        }
        else
        {
            if (SelectedAttack is not { } attack) return;
            if (SolverPath is not { } path) return;
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
                _ => Describe(TallyOf(r), r.Delivered)
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

    /// <summary>
    /// What one shot did, split the way the game splits it.
    ///
    /// <para><b>"Parts damaged" is two unrelated outcomes wearing one word.</b> A part that absorbed some damage
    /// and is still the part you drew has cost you nothing but headroom; a part that filled its pool has been
    /// replaced by a different object, and that is a change to the ship. Reporting a single count let one stand
    /// for the other, which is the difference between a reactor with a dent in it and a reactor that is now a heap
    /// of scrap.</para>
    /// </summary>
    private readonly record struct Tally(int Parts, int Broken, int Destroyed)
    {
        /// <summary>Hit, but still the part the design names.</summary>
        public int Chipped => Parts - Broken - Destroyed;

        /// <summary>Anything that is no longer what was drawn there.</summary>
        public int Changed => Broken + Destroyed;
    }

    private static Tally TallyOf(StrikeResult r) => new(
        r.Hits.Count,
        r.Hits.Count(h => h.Broke && !h.Destroyed),
        r.Hits.Count(h => h.Destroyed));

    /// <summary>The weapon solver reports per <b>cell</b>, so a part spanning several is in the list several times.
    /// Folded to one entry each, and to that part's worst outcome, or a wide part would count as many.</summary>
    private static Tally TallyOf(ImpactResult r)
    {
        var byPart = r.Hits.GroupBy(h => h.PlacementId)
            .Select(g => (Destroyed: g.Any(h => h.Destroyed), Broken: g.Sum(h => h.StagesBroken) > 0))
            .ToList();
        return new Tally(
            byPart.Count,
            byPart.Count(p => p.Broken && !p.Destroyed),
            byPart.Count(p => p.Destroyed));
    }

    private static string Describe(Tally t, double delivered)
    {
        var parts = $"{t.Parts} part{(t.Parts == 1 ? "" : "s")} hit for {delivered:0} damage";
        if (t.Changed == 0)
            return $"{parts}. {(t.Parts == 1 ? "It is" : "All still")} the part{(t.Parts == 1 ? "" : "s")} "
                 + "you drew, just carrying damage.";

        // Worst first, and a term is only printed when it has something in it: "0 destroyed" is noise on a shot
        // that destroyed nothing, and it was the old line's only nod to there being more than one outcome.
        var terms = new List<string>();
        if (t.Destroyed > 0) terms.Add($"{t.Destroyed} destroyed");
        if (t.Broken > 0) terms.Add($"{t.Broken} broke into something else");
        if (t.Chipped > 0) terms.Add($"{t.Chipped} only chipped");
        return $"{parts}: {string.Join(", ", terms)}.";
    }

    private void UpdateTally()
    {
        var ov = DamageOverlay.Build(_doc, _state);
        if (ov.IsEmpty)
        {
            _tallyLine.Text = "The ship is undamaged.";
            _tallyLine.Foreground = Dim;
            _legend.Visibility = Visibility.Collapsed;
            // Start over discards the run, so any consequence scan still in flight is describing a ship that no
            // longer exists. Without the bump it would land afterwards and put the box back up.
            _falloutGeneration++;
            _falloutBox.Visibility = Visibility.Collapsed;
            ShowChanges(ov);
            return;
        }
        // The shot count is the point of hitting one line repeatedly: what is destroyed no longer absorbs, so each
        // pass reaches further in and the tally answers "how many of these would it take to get through".
        _tallyLine.Text = $"After {_shots} hit{(_shots == 1 ? "" : "s")}: {ov.Destroyed} destroyed, "
                        + $"{ov.Broken} broken into something else, {ov.Chipped} carrying damage.";
        _tallyLine.Foreground = ov.ChangedForm > 0 ? Warn : Dim;
        _legend.Visibility = Visibility.Visible;
        ShowChanges(ov);
        UpdateFallout();
    }

    /// <summary>
    /// The parts that are no longer what the design draws, named and with what they turned into.
    ///
    /// <para>This is the half a count cannot give you. "4 parts damaged" is the same sentence whether the ship
    /// still flies or whether the thing that broke was the only reactor, and the design is what says which. Only
    /// changed parts are listed: a chipped part is still the part on the plan, so the plan already shows it.</para>
    /// </summary>
    private void ShowChanges(DamageOverlay ov)
    {
        var changed = ov.Parts.Where(p => p.ChangedForm).ToList();
        _changesBox.Visibility = changed.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _changesList.Children.Clear();
        if (changed.Count == 0) return;

        foreach (var part in changed)
        {
            var line = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 1, 0, 1) };
            line.Inlines.Add(new Run(Friendly(part.OriginalDef)) { Foreground = Ink });
            line.Inlines.Add(new Run("  →  ") { Foreground = Dim });
            line.Inlines.Add(part.Destroyed
                ? new Run("destroyed") { Foreground = Warn, FontWeight = FontWeights.SemiBold }
                : new Run(Friendly(part.CurrentDef)) { Foreground = Warn });
            // What a destroyed part left on the deck. Most chains end in nothing, but the ones that end in scrap
            // end in something the player will find lying there, and saying so is the difference between "it is
            // gone" and "it is gone and here is the pile".
            if (part.Destroyed && part.CurrentDef != part.OriginalDef)
                line.Inlines.Add(new Run($"  ({Friendly(part.CurrentDef)})") { Foreground = Dim });
            _changesList.Children.Add(line);
        }
    }

    private string Friendly(string defName) => _doc.Catalog.Lookup(defName)?.Friendly ?? defName;

    /// <summary>
    /// Ask the damaged hull what the strike cost the ship, and show what it says.
    ///
    /// <para>Off-thread, because four analyses over a station-sized design are not a UI-thread operation, and
    /// generation guarded because firing repeatedly at one line is the whole point of the tool and starts a scan
    /// each time. The document is snapshotted on the calling thread, so nothing the worker touches is shared with
    /// the design being edited.</para>
    /// </summary>
    private async void UpdateFallout()
    {
        var generation = ++_falloutGeneration;
        var catalog = _doc.Catalog;
        // Both hulls are projections rather than snapshots, and that is load-bearing: a projection carries each
        // part's own id across and a snapshot mints new ones, so only this way can the two sides agree that a
        // device on one is the device on the other. Built here, on the UI thread, so the worker below touches
        // nothing the editor holds. The intact side is wanted only until the baseline exists.
        var damaged = _state.Project(_doc);
        var intact = _baseline is null ? new DamageState().Project(_doc) : null;

        DamageFalloutReport report;
        DamageBaseline? baseline;
        try
        {
            var known = _baseline;
            (report, baseline) = await Ui.OffThread(() =>
            {
                var basis = known ?? (intact is null ? null : DamageFallout.Baseline(intact, catalog));
                return basis is null
                    ? (DamageFalloutReport.Empty, basis)
                    : (DamageFallout.Compare(damaged, catalog, basis), basis);
            });
        }
        catch (Exception) { return; }   // a scan that cannot run is not worth taking the window down for

        if (generation != _falloutGeneration) return;   // superseded by a later strike
        _baseline ??= baseline;

        _falloutBox.Visibility = report.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        _falloutList.Children.Clear();
        foreach (var c in report.Consequences)
        {
            var line = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 1, 0, 1) };
            line.Inlines.Add(new Run(c.Title) { Foreground = Warn, FontWeight = FontWeights.SemiBold });
            if (!string.IsNullOrWhiteSpace(c.Detail))
                line.Inlines.Add(new Run("  " + c.Detail) { Foreground = Dim });
            _falloutList.Children.Add(line);
        }
    }
}
