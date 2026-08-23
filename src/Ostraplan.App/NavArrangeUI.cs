using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// The nav console's screen, arranged the way the game lets a player arrange it. A console is a frame holding
/// hot-swappable modules, and where each one appears is an anchor rect in the console's <c>NavModConfig</c> map
/// rather than anything to do with the inventory grid — so this window is the planner's stand-in for sitting at
/// the console in game and opening its edit menu.
///
/// <para>It mirrors that menu deliberately: the board is the screen in normalized 0..1 coordinates (y up, as the
/// game stores them), a module keeps the size its def gives it and only its corner moves, a drag rounds the
/// corner to two decimals (<c>Draggable.MoveRectTransformUsingAnchors</c>), a module tints red while it would
/// not fit, and dragging one onto the tray shelves it exactly as dropping it over the game's edit-menu list
/// does. Fit is the game's own test (<see cref="NavConsole.RectFits"/>): inside the screen, and not strictly
/// overlapping another placed module, so panels may share an edge.</para>
///
/// <para>One deliberate difference. The game lets you leave a module dropped on top of another, tinted red, and
/// resolves it by shelving one of them the next time the console loads. Here an overlapping drop snaps back
/// instead: a design should not record an arrangement whose outcome is decided later.</para>
/// </summary>
public sealed class NavArrangeWindow : Window
{
    private static Brush Ink => ThemeManager.Ink;
    private static Brush Dim => ThemeManager.Dim;
    private static Brush PanelBorder => ThemeManager.PanelBorder;

    /// <summary>The board, in pixels. 8:5 is close enough to the console's own screen for the stock layout to
    /// read the way it does in game, and the coordinates are normalized either way.</summary>
    private const double BoardW = 720, BoardH = 450;

    /// <summary>The module fill colours <c>Draggable.CheckFit</c> uses: a cold blue for a module that fits, a
    /// dull maroon for one that does not.</summary>
    private static readonly Brush FitFill = Frozen(0x40, 0x4E, 0x61), BadFill = Frozen(0x61, 0x40, 0x4A);

    private readonly Catalog _catalog;
    private readonly ShipDocument _doc;
    private readonly CommandStack _stack;
    private readonly Placement _console;
    private readonly PartDef _def;

    /// <summary>Every module aboard, once per screen key (the game ignores a second module of the same kind:
    /// <c>LoadModules</c> skips a prefab it has already loaded).</summary>
    private readonly List<Mod> _mods = [];

    /// <summary>The working arrangement: screen key → rect, missing = shelved in the tray.</summary>
    private readonly Dictionary<string, NavConsole.NavRect> _placed = new(StringComparer.Ordinal);

    private readonly Canvas _board = new() { Width = BoardW, Height = BoardH, Background = Brushes.Transparent };
    private readonly StackPanel _tray = new();
    private readonly TextBlock _status = new();
    private readonly Border _trayBox;

    // drag state: the module under the cursor, and where in it the cursor grabbed (in board pixels)
    private Mod? _drag;
    private Point _grab;
    private Point _cursor;
    private bool _dragFits;
    private NavConsole.NavRect? _dragFrom;   // where it was before this drag, to snap back to

    private sealed record Mod(string Key, string DefName, string Label, double W, double H);

    /// <summary>The laid-out content, for the offscreen preview render (<c>--navsmoke</c>).</summary>
    internal Panel PreviewContent => (Panel)Content;

    public NavArrangeWindow(Catalog catalog, ShipDocument doc, CommandStack stack, Placement console, string friendly)
    {
        _catalog = catalog;
        _doc = doc;
        _stack = stack;
        _console = console;
        _def = catalog.Lookup(console.DefName)!;

        Title = "Arrange screen — " + friendly;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.WindowBg;

        LoadModules();
        Seed();

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock
        {
            Text = "Drag a module to move it, onto the tray to take it off the screen, and out of the tray to put "
                   + "it back. A module keeps its size; only its place changes. Red means it would not fit, and a "
                   + "drop there snaps back.",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            MaxWidth = BoardW + 260, Margin = new Thickness(0, 0, 0, 10),
        });

        var columns = new StackPanel { Orientation = Orientation.Horizontal };
        columns.Children.Add(new Border
        {
            BorderBrush = PanelBorder, BorderThickness = new Thickness(1), Background = ThemeManager.FieldBg,
            Child = _board,
        });

        _trayBox = new Border
        {
            BorderBrush = PanelBorder, BorderThickness = new Thickness(1), Background = ThemeManager.FieldBg,
            Width = 240, Margin = new Thickness(12, 0, 0, 0), Padding = new Thickness(8),
            // Held to the board's own outer height (its border adds 1 either side). The window sizes to its
            // content and nothing else here bounds it, so without this a console carrying a lot of modules
            // grows the window off the screen instead of scrolling the tray.
            MaxHeight = BoardH + 2,
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _tray },
        };
        columns.Children.Add(_trayBox);
        root.Children.Add(columns);

        _status.Foreground = Dim;
        _status.FontSize = 11;
        _status.Margin = new Thickness(0, 10, 0, 0);
        root.Children.Add(_status);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var reset = new Button { Content = "Reset to stock", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0) };
        var ok = new Button { Content = "Apply", Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 4, 16, 4), IsCancel = true };
        reset.Click += (_, _) => { Seed(useStored: false); Render(); };
        ok.Click += (_, _) => { Apply(); DialogResult = true; };
        buttons.Children.Add(reset);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;
        PreviewMouseMove += OnMove;
        PreviewMouseLeftButtonUp += OnUp;
        Render();
    }

    /// <summary>The modules the console holds, in the order it holds them, one entry per screen key. A module with
    /// no screen position at all (something that is not a nav module, or a modded one whose def declares none) is
    /// left out entirely: it cannot be arranged.</summary>
    private void LoadModules()
    {
        foreach (var item in _console.Cargo.Where(c => !c.Slotted))
        {
            if (NavConsole.KeyFor(_catalog, item.DefName) is not { Length: > 0 } key) continue;
            if (_mods.Any(m => m.Key == key)) continue;
            if (NavConsole.DefaultRect(_catalog, _def, item.DefName) is not { } size) continue;
            _mods.Add(new Mod(key, item.DefName, Label(item), size.W, size.H));
        }
    }

    /// <summary>Start from the arrangement in force: the design's own when it has one, else the one the game
    /// itself would produce. <paramref name="useStored"/> false is the Reset button.</summary>
    private void Seed(bool useStored = true)
    {
        _placed.Clear();
        var slots = NavConsole.Arrange(_catalog, _def, _mods.Select(m => m.DefName),
            useStored ? _console.NavLayout : null);
        foreach (var slot in slots)
            if (slot.Pos is { } pos && NavConsole.ParseRect(pos) is { } rect)
                _placed[slot.Key] = rect;
    }

    /// <summary>A module's name, trimmed to what fits on a panel: the game's own friendly name without the
    /// "Polaris Navigation …  Module" scaffolding every one of them carries.</summary>
    private string Label(CargoItem item)
    {
        var name = item.Friendly ?? _catalog.Lookup(item.DefName)?.Friendly ?? item.DefName;
        foreach (var prefix in new[] { "Polaris Navigation ", "Polaris " })
            if (name.StartsWith(prefix, StringComparison.Ordinal)) { name = name[prefix.Length..]; break; }
        return name.EndsWith(" Module", StringComparison.Ordinal) ? name[..^" Module".Length] : name;
    }

    // ---- rendering ----

    private void Render()
    {
        _board.Children.Clear();
        _tray.Children.Clear();

        foreach (var mod in _mods)
        {
            if (_drag is { } d && d.Key == mod.Key)
            {
                _board.Children.Add(Panel(mod, DragRect(), _dragFits, dragging: true));
                continue;
            }
            if (_placed.TryGetValue(mod.Key, out var rect)) _board.Children.Add(Panel(mod, rect, fits: true, dragging: false));
            else _tray.Children.Add(TrayChip(mod));
        }

        if (_tray.Children.Count == 0)
            _tray.Children.Add(new TextBlock
            {
                Text = "Every module is on the screen.", Foreground = Dim, FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });

        var trayed = _mods.Count(m => !_placed.ContainsKey(m.Key));
        _status.Text = $"{_placed.Count} of {_mods.Count} module(s) on the screen"
                       + (trayed > 0 ? $", {trayed} in the tray. A module in the tray is still aboard the ship: you can put it on the screen in game at any time." : ".");
    }

    /// <summary>One module drawn on the board. The board's y runs down and the game's anchors run up, so the top
    /// edge comes from the rect's <b>max</b> y.</summary>
    private Border Panel(Mod mod, NavConsole.NavRect r, bool fits, bool dragging)
    {
        var el = new Border
        {
            Width = Math.Max(1, r.W * BoardW - 2),
            Height = Math.Max(1, r.H * BoardH - 2),
            Background = fits ? FitFill : BadFill,
            BorderBrush = dragging ? ThemeManager.Accent : PanelBorder,
            BorderThickness = new Thickness(dragging ? 2 : 1),
            CornerRadius = new CornerRadius(2),
            Opacity = dragging ? 0.85 : 1.0,
            Cursor = Cursors.SizeAll,
            ToolTip = mod.DefName,
            Child = new TextBlock
            {
                Text = mod.Label, Foreground = Ink, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(3),
            },
        };
        Canvas.SetLeft(el, r.X0 * BoardW + 1);
        Canvas.SetTop(el, (1 - r.Y1) * BoardH + 1);
        if (!dragging) el.MouseLeftButtonDown += (_, e) => StartDrag(mod, e);
        return el;
    }

    /// <summary>A shelved module in the tray: drag it out to place it, or double-click to drop it in the first
    /// free spot big enough for it.</summary>
    private Border TrayChip(Mod mod)
    {
        var el = new Border
        {
            Background = FitFill, BorderBrush = PanelBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2), Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 0, 4),
            Cursor = Cursors.Hand,
            ToolTip = mod.DefName + " — drag onto the screen, or double-click for the first free spot",
            Child = new TextBlock { Text = mod.Label, Foreground = Ink, FontSize = 12, TextWrapping = TextWrapping.Wrap },
        };
        el.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { PlaceFirstFree(mod); return; }
            StartDrag(mod, e, fromTray: true);
        };
        return el;
    }

    // ---- dragging ----

    private void StartDrag(Mod mod, MouseButtonEventArgs e, bool fromTray = false) =>
        StartDrag(mod, e.GetPosition(_board), fromTray, e);

    private void StartDrag(Mod mod, Point cursor, bool fromTray, MouseButtonEventArgs? e)
    {
        _drag = mod;
        _dragFrom = _placed.TryGetValue(mod.Key, out var was) ? was : null;
        _cursor = cursor;
        // grabbed in the middle when it comes off the tray (there is no panel under the cursor yet), else where
        // the cursor actually landed on the panel, so it does not jump
        _grab = fromTray || _dragFrom is not { } r
            ? new Point(mod.W * BoardW / 2, mod.H * BoardH / 2)
            : new Point(_cursor.X - r.X0 * BoardW, _cursor.Y - (1 - r.Y1) * BoardH);
        _placed.Remove(mod.Key);
        _dragFits = Fits(DragRect(), mod.Key);
        if (e is not null) { CaptureMouse(); e.Handled = true; }
        Render();
    }

    /// <summary>Pick a module up and hold it at a board pixel, for the offscreen preview render
    /// (<c>--navsmoke</c>) — the same path a real drag takes, minus the mouse.</summary>
    internal void PreviewDrag(string defName, double x, double y)
    {
        if (_mods.FirstOrDefault(m => m.DefName == defName) is not { } mod) return;
        StartDrag(mod, new Point(x, y), fromTray: !_placed.ContainsKey(mod.Key), e: null);
        _cursor = new Point(x, y);
        _dragFits = Fits(DragRect(), mod.Key);
        Render();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_drag is null) return;
        _cursor = e.GetPosition(_board);
        _dragFits = !OverTray(e) && Fits(DragRect(), _drag.Key);
        Render();
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag is not { } mod) return;
        var overTray = OverTray(e);
        var rect = DragRect();
        ReleaseMouseCapture();
        _drag = null;

        if (overTray) { /* shelved: it simply stays out of _placed */ }
        else if (Fits(rect, mod.Key)) _placed[mod.Key] = rect;
        else if (_dragFrom is { } back) _placed[mod.Key] = back;   // a bad drop snaps back to where it was
        _dragFrom = null;
        Render();
    }

    /// <summary>Where the dragged module sits right now, from the cursor: the game's own rounding to 2dp, clamped
    /// to the screen so a panel cannot be dragged off the edge and lost.</summary>
    private NavConsole.NavRect DragRect()
    {
        var mod = _drag!;
        var x = (_cursor.X - _grab.X) / BoardW;
        var y = 1 - (_cursor.Y - _grab.Y) / BoardH - mod.H;   // board y is down, anchors are up
        x = Math.Clamp(x, 0, Math.Max(0, 1 - mod.W));
        y = Math.Clamp(y, 0, Math.Max(0, 1 - mod.H));
        return new NavConsole.NavRect(x, y, x + mod.W, y + mod.H).MovedTo(x, y);
    }

    /// <summary>The game's fit test against every other placed module.</summary>
    private bool Fits(NavConsole.NavRect r, string exceptKey) =>
        NavConsole.RectFits(r, _placed.Where(kv => kv.Key != exceptKey).Select(kv => kv.Value));

    private bool OverTray(MouseEventArgs e)
    {
        var p = e.GetPosition(_trayBox);
        return p.X >= 0 && p.Y >= 0 && p.X <= _trayBox.ActualWidth && p.Y <= _trayBox.ActualHeight;
    }

    /// <summary>Drop a shelved module in the first free spot that takes it, scanning the screen in 0.01 steps the
    /// way a drag would land. Does nothing when the screen is full — the tray is where it stays.</summary>
    private void PlaceFirstFree(Mod mod)
    {
        for (var y = 0.0; y <= 1 - mod.H + 1e-9; y = Math.Round(y + 0.01, 2))
            for (var x = 0.0; x <= 1 - mod.W + 1e-9; x = Math.Round(x + 0.01, 2))
            {
                var candidate = new NavConsole.NavRect(x, y, x + mod.W, y + mod.H).MovedTo(x, y);
                if (!Fits(candidate, mod.Key)) continue;
                _placed[mod.Key] = candidate;
                Render();
                return;
            }
        _status.Text = "No room on the screen for " + mod.Label + ". Move something out of the way first.";
    }

    // ---- committing ----

    /// <summary>Write the arrangement onto the console as an undoable edit. A layout that matches what the game
    /// would produce anyway is stored as <c>null</c>, so a design only carries an arrangement the user actually
    /// chose and a later change to the stock set still reaches consoles nobody has touched.</summary>
    private void Apply()
    {
        var layout = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var mod in _mods)
            layout[mod.Key] = _placed.TryGetValue(mod.Key, out var r) ? NavConsole.FormatRect(r) : "";

        var stock = NavConsole.ConfigEntries(_catalog, _def, _mods.Select(m => m.DefName))
            .ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        var isStock = layout.Count == stock.Count && layout.All(kv => stock.GetValueOrDefault(kv.Key) == kv.Value);

        var after = isStock ? null : layout;
        if (Same(_console.NavLayout, after)) return;   // nothing changed: no undo entry for opening a window
        _stack.Push(_doc, new SetNavLayoutCommand(_console, _console.NavLayout, after));
    }

    private static bool Same(IReadOnlyDictionary<string, string>? a, IReadOnlyDictionary<string, string>? b) =>
        a is null && b is null
        || a is not null && b is not null && a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
