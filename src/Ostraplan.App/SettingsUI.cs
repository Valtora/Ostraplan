using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// What the Settings dialog needs from its host to make a change real. The dialog owns no policy: it reads the
/// current values out of <see cref="AppSettings"/>, and hands each new one straight back, so persistence, the
/// activity log and the live re-render all stay in <see cref="MainWindow"/> beside every other setting.
/// </summary>
/// <param name="Theme">"system" / "light" / "dark".</param>
/// <param name="Scale">The UI scale factor (1.0 = 100%), already clamped by the dialog.</param>
/// <param name="OpenAs">How the main window should open next launch.</param>
/// <param name="Backdrop">What the plan is drawn on, and its grid markings.</param>
/// <param name="ModOverrides">Whether modded parts may be placed against the core placement law.</param>
/// <param name="NavModuleArt">Whether the arrange window draws the nav modules with the game's own art.</param>
/// <param name="GameRoot">The Ostranauts install folder, or null to go back to auto-detection.</param>
/// <param name="SavesDir">The Saves folder, or null to go back to auto-detection.</param>
/// <param name="RestoreTabs">Whether a launch reopens the designs that were open last time.</param>
/// <param name="SessionBackup">Whether unsaved changes are backed up.</param>
/// <param name="BackupSeconds">Seconds between backups, already clamped by the dialog.</param>
/// <param name="CloseMode">What closing does with unsaved changes.</param>
/// <param name="TipsOn">Whether tips show at all.</param>
/// <param name="TipsAtStartup">When a tip appears by itself.</param>
/// <param name="TipsHours">Hours between startup tips, already clamped by the dialog.</param>
/// <param name="TipsBulb">Whether the bulb sits in the toolbar.</param>
/// <param name="OpenTips">Open the Tip Hub over the given window.</param>
public sealed record SettingsHooks(
    Action<string> Theme,
    Action<double> Scale,
    Action<WindowOpenAs> OpenAs,
    Action<BackdropSettings> Backdrop,
    Action<bool> ModOverrides,
    Action<bool> NavModuleArt,
    Action<string?> GameRoot,
    Action<string?> SavesDir,
    Action<bool> RestoreTabs,
    Action<bool> SessionBackup,
    Action<int> BackupSeconds,
    Action<SessionCloseMode> CloseMode,
    Action<bool> TipsOn,
    Action<TipStartup> TipsAtStartup,
    Action<int> TipsHours,
    Action<bool> TipsBulb,
    Action<Window> OpenTips);

/// <summary>
/// Ostraplan's own preferences: appearance (theme and UI scale), the one editing rule that is a preference rather
/// than a view, when tips appear, and the two folders it reads. Everything here is app-wide and persisted in
/// <c>%APPDATA%\Ostraplan\settings.json</c>; nothing here belongs to a design.
///
/// <para>Changes apply as they are made rather than on OK, which is how every other setting in the app already
/// behaves (the overlay toggles, auto-save). The one exception is the game folder, because the data is read once
/// at launch, and the dialog says so where it is set.</para>
/// </summary>
public sealed class SettingsDialog : Window
{
    private static Brush Ink => ThemeManager.Ink;
    private static Brush Dim => ThemeManager.Dim;

    private readonly AppSettings _settings;
    private readonly SettingsHooks _hooks;
    private readonly Catalog? _catalog;
    private GameEnv? _env;

    private readonly TextBlock _gameRootText, _savesText;
    private bool _init = true;   // suppress the combo's SelectionChanged during the initial fill

    public SettingsDialog(AppSettings settings, Catalog? catalog, GameEnv? env, SettingsHooks hooks)
    {
        _settings = settings;
        _hooks = hooks;
        _catalog = catalog;
        _env = env;

        Title = "Settings";
        Width = 560;
        MaxHeight = 780;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = ThemeManager.WindowBg;

        var body = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };

        Section(body, "APPEARANCE", first: true);
        body.Children.Add(ThemeRow());
        body.Children.Add(ScaleRow());
        body.Children.Add(OpenAsRow());
        body.Children.Add(NavArtRow());

        Section(body, "THE PLAN'S BACKDROP");
        body.Children.Add(_kindRow);
        body.Children.Add(_solidRow);
        body.Children.Add(_checkerColourRow);
        body.Children.Add(_checkerSizeRow);
        body.Children.Add(_localeRow);
        body.Children.Add(_coarseRow);
        body.Children.Add(_resetAllRow);
        RebuildBackdropRows();

        Section(body, "TABS");
        body.Children.Add(RestoreTabsRow());
        body.Children.Add(BackupRow());
        body.Children.Add(CloseModeRow());

        Section(body, "EDITING");
        body.Children.Add(ModOverrideRow());

        Section(body, "TIPS");
        body.Children.Add(TipsOnRow());
        body.Children.Add(TipStartupRow());
        body.Children.Add(TipBulbRow());
        body.Children.Add(TipHubRow());
        SyncTipRows();

        Section(body, "GAME FOLDERS");
        _gameRootText = PathValue();
        body.Children.Add(PathRow(
            "Ostranauts install",
            _gameRootText,
            "Where Ostraplan reads the game's data and sprites. Found through Steam; set it for a non-Steam or "
            + "moved install. Takes effect next launch.",
            PickGameRoot,
            () => ApplyGameRoot(null)));

        _savesText = PathValue();
        body.Children.Add(PathRow(
            "Saves",
            _savesText,
            "Where saves are imported from and written back to. Follows the game's own save location; set it "
            + "only if your saves are somewhere else.",
            PickSavesDir,
            () => ApplySavesDir(null)));

        // Close is docked rather than the last child of the scrolling body: the dialog is capped at MaxHeight and
        // is now long enough to reach it, and a StackPanel gives every child its desired height whatever space it
        // is arranged into, so a scrolled button ends up under the bottom edge (CONVENTIONS).
        var close = new Button
        {
            Content = "Close", Padding = new Thickness(20, 4, 20, 4), IsDefault = true, IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 10, 20, 14),
        };
        close.Click += (_, _) => Close();

        var root = new DockPanel();
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(close);
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;

        SyncBackdropRows();
        RefreshPaths();
        _init = false;
    }

    // ---- appearance ----

    private UIElement ThemeRow()
    {
        var combo = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        combo.Items.Add("Follow Windows");
        combo.Items.Add("Light");
        combo.Items.Add("Dark");
        combo.SelectedIndex = _settings.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        combo.SelectionChanged += (_, _) =>
        {
            if (_init) return;
            _hooks.Theme(combo.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" });
        };
        return Row("Theme", combo,
            "Colours of the menus, panels and dialogs. The ship canvas stays dark either way.");
    }

    private UIElement ScaleRow()
    {
        // Percent on the slider, a factor in the setting: "150%" is what the user is choosing, and it keeps the
        // ticks whole numbers.
        var slider = new Slider
        {
            Minimum = UiScaling.Min * 100, Maximum = UiScaling.Max * 100,
            Value = UiScaling.Clamp(_settings.UiScale) * 100,
            TickFrequency = UiScaling.Step * 100, IsSnapToTickEnabled = true,
            SmallChange = UiScaling.Step * 100, LargeChange = UiScaling.Step * 400,
            Width = 260, VerticalAlignment = VerticalAlignment.Center,
        };
        var readout = new TextBlock
        {
            Text = UiScaling.Percent(slider.Value / 100), Foreground = Ink, Width = 48,
            TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        var reset = new Button
        {
            Content = "Reset", Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        reset.Click += (_, _) => slider.Value = UiScaling.Default * 100;

        slider.ValueChanged += (_, e) =>
        {
            readout.Text = UiScaling.Percent(e.NewValue / 100);
            if (!_init) _hooks.Scale(UiScaling.Clamp(e.NewValue / 100));
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(slider);
        row.Children.Add(readout);
        row.Children.Add(reset);

        return Row("UI scale", row,
            "Size of everything Ostraplan draws: toolbar, panels, dialogs, reports and the canvas. The main window "
            + "keeps its size.");
    }

    private UIElement OpenAsRow()
    {
        var combo = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        combo.Items.Add("Last size and position");
        combo.Items.Add("Maximised");
        combo.SelectedIndex = WindowPlacement.ParseOpenAs(_settings.WindowOpenAs) == WindowOpenAs.Maximised ? 1 : 0;
        combo.SelectionChanged += (_, _) =>
        {
            if (_init) return;
            _hooks.OpenAs(combo.SelectedIndex == 1 ? WindowOpenAs.Maximised : WindowOpenAs.Last);
        };
        return Row("Open as", combo,
            "Whether the main window opens as you left it or maximised. Takes effect next launch.");
    }

    private UIElement NavArtRow()
    {
        var box = new CheckBox
        {
            Content = "Draw nav console modules with the game's own art",
            IsChecked = _settings.NavModuleArt,
            Foreground = Ink,
            VerticalAlignment = VerticalAlignment.Center,
        };
        box.Checked += (_, _) => { if (!_init) _hooks.NavModuleArt(true); };
        box.Unchecked += (_, _) => { if (!_init) _hooks.NavModuleArt(false); };
        return Row("Console module art", box,
            "Shows modules in the Arrange screen window as they look at the console in game. Off, they are flat "
            + "labelled panels. Live readouts such as fuel and the map stay blank.");
    }

    // ---- the plan's backdrop (#43, #71) ----

    // One host per row, so a reset can rebuild every row from the stored settings and each control shows the value
    // it now has. Building a row sets its control's value before its change handler is attached, so a rebuild never
    // reads as the user changing anything.
    private readonly ContentControl _kindRow = new();
    private readonly ContentControl _solidRow = new();
    private readonly ContentControl _checkerColourRow = new();
    private readonly ContentControl _checkerSizeRow = new();
    private readonly ContentControl _localeRow = new();
    private readonly ContentControl _coarseRow = new();
    private readonly ContentControl _resetAllRow = new();

    /// <summary>The backdrop kinds in the order the combo lists them. Not the enum's order: the tile-grid
    /// checkerboard was added after the locale and belongs beside the other checkerboard.</summary>
    private static readonly BackdropKind[] KindOrder =
        [BackdropKind.Solid, BackdropKind.Checker, BackdropKind.TileChecker, BackdropKind.Locale];

    /// <summary>The backdrop as the dialog currently has it. Every control edits a copy of this and pushes the
    /// whole record back, so a change to one field never resets another.</summary>
    private BackdropSettings Current => _settings.BackdropOrDefault();

    private static BackdropSettings Defaults => BackdropSettings.Default;

    private void Push(BackdropSettings next)
    {
        if (_init) return;
        _hooks.Backdrop(next.Clamped());
        SyncBackdropRows();
    }

    /// <summary>Put part of the backdrop back to its default and rebuild the rows, so the controls show it.</summary>
    private void Reset(BackdropSettings next)
    {
        _hooks.Backdrop(next.Clamped());
        RebuildBackdropRows();
    }

    private void RebuildBackdropRows()
    {
        _resets.Clear();
        _kindRow.Content = BackdropKindRow();
        _solidRow.Content = SolidRow();
        _checkerColourRow.Content = CheckerColourRow();
        _checkerSizeRow.Content = CheckerSizeRow();
        _localeRow.Content = LocaleRow();
        _coarseRow.Content = CoarseGridRow();
        _resetAllRow.Content = ResetAllRow();
        SyncBackdropRows();
    }

    /// <summary>Show only the controls the chosen kind actually uses. A checkerboard's second colour and a
    /// locale's dimming are meaningless to each other, and a dialog that shows every control for every kind makes
    /// the reader work out which of them is live. The square size belongs to the screen checkerboard alone: the
    /// tile-grid one takes its size from the tiles.</summary>
    private void SyncBackdropRows()
    {
        var kind = Current.Kind;
        var checker = kind is BackdropKind.Checker or BackdropKind.TileChecker;
        _solidRow.Visibility = kind == BackdropKind.Solid || checker ? Visibility.Visible : Visibility.Collapsed;
        _checkerColourRow.Visibility = checker ? Visibility.Visible : Visibility.Collapsed;
        _checkerSizeRow.Visibility = kind == BackdropKind.Checker ? Visibility.Visible : Visibility.Collapsed;
        _localeRow.Visibility = kind == BackdropKind.Locale ? Visibility.Visible : Visibility.Collapsed;
        var current = Current;
        foreach (var (button, isDefault) in _resets) button.IsEnabled = !isDefault(current);
        if (_resetAllRow.Content is FrameworkElement all)
            all.IsEnabled = current != Defaults.Clamped();
    }

    /// <summary>Every Reset on screen, with the test for whether its value is already the default, so each one
    /// follows the value as it changes rather than only as it stood when the row was built.</summary>
    private readonly List<(Button Button, Func<BackdropSettings, bool> IsDefault)> _resets = [];

    /// <summary>A Reset beside a control: disabled while the value is already the default, and naming the default
    /// either way, so what the default even is never has to be found out by experiment.</summary>
    private Button ResetButton(Func<BackdropSettings, bool> isDefault, string defaultIs, Action reset)
    {
        var button = new Button
        {
            Content = "Reset", Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, IsEnabled = !isDefault(Current),
            ToolTip = "Back to the default: " + defaultIs,
        };
        ToolTipService.SetShowOnDisabled(button, true);
        button.Click += (_, _) => reset();
        _resets.Add((button, isDefault));
        return button;
    }

    /// <summary>The name a colour goes by in the palette, with its hex, or the hex alone.</summary>
    private static string ColourName(string hex) =>
        Backdrop.Palette.FirstOrDefault(s => string.Equals(s.Hex, hex, StringComparison.OrdinalIgnoreCase)) is { } swatch
            ? $"{swatch.Name} ({swatch.Hex})"
            : hex;

    private UIElement BackdropKindRow()
    {
        var combo = new ComboBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        combo.Items.Add("Solid colour");
        combo.Items.Add("Checkerboard, fixed to the screen");
        combo.Items.Add("Checkerboard, on the tile grid");
        combo.Items.Add("A place from the game");
        combo.SelectedIndex = Math.Max(0, Array.IndexOf(KindOrder, Current.Kind));
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex < 0) return;
            var kind = KindOrder[combo.SelectedIndex];
            var next = Current with { Kind = kind };
            // Choosing the game's art with nothing picked yet would show the old backdrop and look like a dead
            // control, so the first locale in the list stands in until the user picks one.
            if (kind == BackdropKind.Locale && next.Locale is null && Locales().FirstOrDefault() is { } first)
                next = next with { Locale = first.Name };
            Push(next);
            if (_localeRow.Content is FrameworkElement && _localeCombo is { } lc && lc.SelectedIndex < 0 && lc.Items.Count > 0)
                lc.SelectedIndex = 0;
        };

        return Row("Backdrop", combo,
            "What every open design, and a Design ▸ Snapshot PNG, is drawn on. The screen checkerboard stays still "
            + "as you pan; the tile-grid one moves with the ship. Not saved in the design. Default: solid colour.");
    }

    private UIElement SolidRow() =>
        ColourRow("Colour", Current.Solid, BackdropSettings.DefaultSolid,
            pick => Current with { Solid = pick }, b => b.Solid,
            "Any #RRGGBB or a swatch. Also the first colour of either checkerboard.");

    private UIElement CheckerColourRow() =>
        ColourRow("Second colour", Current.CheckerAlt, BackdropSettings.DefaultCheckerAlt,
            pick => Current with { CheckerAlt = pick }, b => b.CheckerAlt,
            "The checkerboard's other colour.");

    /// <summary>A colour as a hex box, the swatch palette and a Reset. Shared by the ground colour and the
    /// checkerboard's second colour, which used to have the hex box alone (#71).</summary>
    private UIElement ColourRow(string label, string value, string defaultHex, Func<string, BackdropSettings> with,
        Func<BackdropSettings, string> read, string note)
    {
        var hex = new TextBox
        {
            Width = 100, Text = value, FontFamily = new FontFamily("Consolas"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
        };
        var swatches = SwatchGrid(pick =>
        {
            hex.Text = pick;
            Push(with(pick));
        });
        hex.LostFocus += (_, _) =>
        {
            var normalised = Backdrop.NormaliseColour(hex.Text, read(Current));
            hex.Text = normalised;
            Push(with(normalised));
        };

        var panel = new StackPanel();
        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        top.Children.Add(hex);
        top.Children.Add(new TextBlock
        {
            Text = "or pick one below", Foreground = Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
        });
        top.Children.Add(ResetButton(
            b => string.Equals(read(b), defaultHex, StringComparison.OrdinalIgnoreCase), ColourName(defaultHex),
            () => Reset(with(defaultHex))));
        panel.Children.Add(top);
        panel.Children.Add(swatches);

        return Row(label, panel, note + " Default: " + ColourName(defaultHex) + ".");
    }

    private UIElement CheckerSizeRow()
    {
        var size = new Slider
        {
            Minimum = BackdropSettings.MinCheckerSquare, Maximum = BackdropSettings.MaxCheckerSquare,
            Value = Current.CheckerSquare, TickFrequency = 8, IsSnapToTickEnabled = true,
            Width = 220, VerticalAlignment = VerticalAlignment.Center,
        };
        var readout = new TextBlock
        {
            Text = $"{Current.CheckerSquare} px", Foreground = Ink, Width = 54, TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
        };
        size.ValueChanged += (_, e) =>
        {
            var px = (int)Math.Round(e.NewValue);
            readout.Text = $"{px} px";
            Push(Current with { CheckerSquare = px });
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(size);
        row.Children.Add(readout);
        row.Children.Add(ResetButton(b => b.CheckerSquare == BackdropSettings.DefaultCheckerSquare,
            $"{BackdropSettings.DefaultCheckerSquare} px",
            () => Reset(Current with { CheckerSquare = BackdropSettings.DefaultCheckerSquare })));

        return Row("Square size", row,
            "Size of each checkerboard square on screen. It doesn't change with zoom. "
            + $"Default: {BackdropSettings.DefaultCheckerSquare} px.");
    }

    private IReadOnlyList<ParallaxLocale> Locales() =>
        _catalog is { } c ? ParallaxCatalog.All(c) : [];

    private ComboBox? _localeCombo;

    private UIElement LocaleRow()
    {
        var locales = Locales();
        var combo = new ComboBox { Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
        _localeCombo = combo;
        foreach (var locale in locales) combo.Items.Add(locale.Display);
        combo.SelectedIndex = locales.ToList().FindIndex(l => l.Name == Current.Locale);
        combo.IsEnabled = locales.Count > 0;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0 && combo.SelectedIndex < locales.Count)
                Push(Current with { Locale = locales[combo.SelectedIndex].Name });
        };

        var dim = new Slider
        {
            Minimum = 0, Maximum = 100, Value = Current.LocaleDimming * 100,
            TickFrequency = 5, IsSnapToTickEnabled = true,
            Width = 150, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
        };
        var readout = new TextBlock
        {
            Text = $"{Current.LocaleDimming * 100:0}%", Foreground = Ink, Width = 42, TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
        };
        dim.ValueChanged += (_, e) =>
        {
            readout.Text = $"{e.NewValue:0}%";
            Push(Current with { LocaleDimming = e.NewValue / 100 });
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(combo);
        row.Children.Add(dim);
        row.Children.Add(readout);
        // The place has no default worth resetting to (none is chosen until the kind is), so this is the dimming.
        row.Children.Add(ResetButton(
            b => Math.Abs(b.LocaleDimming - BackdropSettings.DefaultLocaleDimming) < 0.001,
            $"{BackdropSettings.DefaultLocaleDimming * 100:0}% dimming",
            () => Reset(Current with { LocaleDimming = BackdropSettings.DefaultLocaleDimming })));

        var note = locales.Count > 0
            ? "The game's background art for a place. Dimming darkens it so the ship stands out; 0% shows it as "
              + $"the game does. Default dimming: {BackdropSettings.DefaultLocaleDimming * 100:0}%."
            : "No backdrops found in the loaded game data.";

        return Row("Place and dimming", row, note);
    }

    private UIElement CoarseGridRow()
    {
        int[] presets = [0, 5, 10, 20];
        var combo = new ComboBox { Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
        combo.Items.Add("Off");
        combo.Items.Add("Every 5 tiles");
        combo.Items.Add("Every 10 tiles");
        combo.Items.Add("Every 20 tiles");
        var at = Array.IndexOf(presets, Current.CoarseGrid);
        if (at < 0)
        {
            combo.Items.Add($"Every {Current.CoarseGrid} tiles");
            at = combo.Items.Count - 1;
        }
        combo.SelectedIndex = at;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0 && combo.SelectedIndex < presets.Length)
                Push(Current with { CoarseGrid = presets[combo.SelectedIndex] });
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(combo);
        row.Children.Add(ResetButton(b => b.CoarseGrid == Defaults.CoarseGrid, "off",
            () => Reset(Current with { CoarseGrid = Defaults.CoarseGrid })));

        return Row("Scale markings", row,
            "A brighter grid line every so many tiles from the ship's origin, for judging size at a glance. "
            + "Default: off.");
    }

    /// <summary>Everything about the backdrop back to how Ostraplan ships, in one click.</summary>
    private UIElement ResetAllRow()
    {
        var button = new Button
        {
            Content = "Reset the backdrop to the default", Padding = new Thickness(12, 3, 12, 3),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0),
            IsEnabled = Current != Defaults.Clamped(),
            ToolTip = "A solid " + ColourName(BackdropSettings.DefaultSolid) + " with no scale markings. Resets "
                      + "every backdrop setting.",
        };
        ToolTipService.SetShowOnDisabled(button, true);
        button.Click += (_, _) => Reset(Defaults);
        return button;
    }

    // ---- tabs (#73) ----

    private ComboBox? _closeModeCombo;

    private UIElement RestoreTabsRow()
    {
        var box = new CheckBox
        {
            Content = "Reopen the designs that were open last time",
            IsChecked = _settings.RestoreTabs,
            Foreground = Ink,
            VerticalAlignment = VerticalAlignment.Center,
        };
        box.Checked += (_, _) => { if (!_init) _hooks.RestoreTabs(true); };
        box.Unchecked += (_, _) => { if (!_init) _hooks.RestoreTabs(false); };
        return Row("Reopen tabs", box,
            "Reopens each saved design from its file, in the same tab order. Never-saved designs come back only "
            + "through the backup below.");
    }

    private UIElement BackupRow()
    {
        var box = new CheckBox
        {
            Content = "Back up unsaved changes in every tab",
            IsChecked = _settings.SessionBackup,
            Foreground = Ink,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0),
        };
        var slider = new Slider
        {
            Minimum = SessionStore.MinBackupSeconds, Maximum = SessionStore.MaxBackupSeconds,
            Value = SessionStore.ClampBackupSeconds(_settings.SessionBackupSeconds),
            TickFrequency = 5, IsSnapToTickEnabled = true,
            Width = 150, VerticalAlignment = VerticalAlignment.Center,
        };
        var readout = new TextBlock
        {
            Text = $"every {slider.Value:0} s", Foreground = Ink, Width = 76, TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
        };
        void Sync(bool on)
        {
            slider.IsEnabled = readout.IsEnabled = on;
            if (_closeModeCombo is { } combo) combo.IsEnabled = on;
        }
        box.Checked += (_, _) => { Sync(true); if (!_init) _hooks.SessionBackup(true); };
        box.Unchecked += (_, _) => { Sync(false); if (!_init) _hooks.SessionBackup(false); };
        slider.ValueChanged += (_, e) =>
        {
            readout.Text = $"every {e.NewValue:0} s";
            if (!_init) _hooks.BackupSeconds(SessionStore.ClampBackupSeconds((int)Math.Round(e.NewValue)));
        };
        Sync(_settings.SessionBackup);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(box);
        row.Children.Add(slider);
        row.Children.Add(readout);

        return Row("Backup", row,
            "Saves a copy of each design's unsaved changes so a crash loses nothing: the next launch brings them "
            + "back. Your .oplan files are never touched. Separate from File ▸ Auto-save.");
    }

    private UIElement CloseModeRow()
    {
        var combo = new ComboBox { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        _closeModeCombo = combo;
        combo.Items.Add("Ask me to save or discard each one");
        combo.Items.Add("Keep them in the backup for next time");
        combo.SelectedIndex = SessionStore.ParseCloseMode(_settings.CloseWithUnsaved) == SessionCloseMode.KeepInBackup ? 1 : 0;
        combo.IsEnabled = _settings.SessionBackup;
        combo.SelectionChanged += (_, _) =>
        {
            if (_init || combo.SelectedIndex < 0) return;
            _hooks.CloseMode(combo.SelectedIndex == 1 ? SessionCloseMode.KeepInBackup : SessionCloseMode.Ask);
        };
        return Row("When closing with unsaved changes", combo,
            "Keeping them closes without asking and reopens those designs next time, still unsaved, so a change "
            + "lives only in the backup until you save it. Needs Backup on.");
    }

    // ---- editing ----

    private UIElement ModOverrideRow()
    {
        var box = new CheckBox
        {
            Content = "Let modded parts break the placement law",
            IsChecked = _settings.AllowModdedOverrides,
            Foreground = Ink,
            VerticalAlignment = VerticalAlignment.Center,
        };
        box.Checked += (_, _) => { if (!_init) _hooks.ModOverrides(true); };
        box.Unchecked += (_, _) => { if (!_init) _hooks.ModOverrides(false); };
        return Row("Mod overrides", box,
            "Lets you place a modded part where the core-game rules say it doesn't fit, with a warning to check it "
            + "in game. Core parts are always enforced.");
    }

    // ---- tips (#39) ----

    // When and whether tips appear. Which tips appear is the Tip Hub's job, so this section links to it rather than
    // repeating a checkbox per topic.
    private ComboBox? _tipStartup;
    private Slider? _tipHours;
    private CheckBox? _tipBulb;

    private UIElement TipsOnRow()
    {
        var box = new CheckBox
        {
            Content = "Show tips",
            IsChecked = _settings.Tips.Enabled,
            Foreground = Ink,
            VerticalAlignment = VerticalAlignment.Center,
        };
        box.Checked += (_, _) => { if (!_init) { _hooks.TipsOn(true); SyncTipRows(); } };
        box.Unchecked += (_, _) => { if (!_init) { _hooks.TipsOn(false); SyncTipRows(); } };
        return Row("Tips", box,
            "Short notes on features you might not have found. Off means no tip at startup and no bulb in the "
            + "toolbar. Help ▸ Tips lists every one either way.");
    }

    private UIElement TipStartupRow()
    {
        // In TipStartup's own order, so the index is the value.
        var combo = new ComboBox { Width = 200, VerticalAlignment = VerticalAlignment.Center };
        _tipStartup = combo;
        combo.Items.Add("At most once every");
        combo.Items.Add("Every launch");
        combo.Items.Add("Never");
        combo.SelectedIndex = (int)_settings.Tips.StartupMode;
        combo.SelectionChanged += (_, _) =>
        {
            if (_init || combo.SelectedIndex < 0) return;
            _hooks.TipsAtStartup((TipStartup)combo.SelectedIndex);
            SyncTipRows();
        };

        var slider = new Slider
        {
            Minimum = TipSettings.MinIntervalHours, Maximum = TipSettings.MaxIntervalHours,
            Value = _settings.Tips.Hours, TickFrequency = 1, IsSnapToTickEnabled = true,
            Width = 150, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
        };
        _tipHours = slider;
        var readout = new TextBlock
        {
            Text = Hours(_settings.Tips.Hours), Foreground = Ink, Width = 64, TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
        };
        slider.ValueChanged += (_, e) =>
        {
            var hours = TipSettings.ClampHours((int)Math.Round(e.NewValue));
            readout.Text = Hours(hours);
            if (!_init) _hooks.TipsHours(hours);
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(combo);
        row.Children.Add(slider);
        row.Children.Add(readout);
        return Row("At startup", row,
            "A tip waits until the game data has loaded, and never appears on the launch that shows What's New. "
            + $"Default: at most once every {TipSettings.DefaultIntervalHours} hours.");

        static string Hours(int h) => h == 1 ? "1 hour" : $"{h} hours";
    }

    private UIElement TipBulbRow()
    {
        var box = new CheckBox
        {
            Content = "Show the 💡 button in the toolbar",
            IsChecked = _settings.Tips.ShowBulb,
            Foreground = Ink,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _tipBulb = box;
        box.Checked += (_, _) => { if (!_init) _hooks.TipsBulb(true); };
        box.Unchecked += (_, _) => { if (!_init) _hooks.TipsBulb(false); };
        return Row("Toolbar", box, "Click it for the next tip whenever you like.");
    }

    private UIElement TipHubRow()
    {
        var button = new Button
        {
            Content = "Choose topics and tips…", Padding = new Thickness(12, 3, 12, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        button.Click += (_, _) => _hooks.OpenTips(this);
        return Row("Which tips", button, "Turn off a whole topic, such as Shortcuts, or single tips.");
    }

    /// <summary>Grey out what the master switch makes meaningless, and the hours unless they are what decides.</summary>
    private void SyncTipRows()
    {
        var on = _settings.Tips.Enabled;
        if (_tipStartup is { } combo) combo.IsEnabled = on;
        if (_tipBulb is { } bulb) bulb.IsEnabled = on;
        if (_tipHours is { } hours) hours.IsEnabled = on && _settings.Tips.StartupMode == TipStartup.Interval;
    }

    // ---- game folders ----

    private void PickGameRoot()
    {
        var dlg = new OpenFolderDialog
        {
            Title = $"Pick the Ostranauts folder (the one holding {GameEnv.GameExeName})",
            InitialDirectory = _env?.GameRoot ?? "",
        };
        if (dlg.ShowDialog(this) != true) return;

        // The same check the startup gate applies, so a folder accepted here cannot fail at the next launch.
        if (GameEnv.InstallProblem(dlg.FolderName) is { } why)
        {
            Dlg.Warn(this, "Settings",
                why + "\n\n" +
                $"Pick the folder holding {GameEnv.GameExeName}, usually steamapps\\common\\Ostranauts.");
            return;
        }
        ApplyGameRoot(dlg.FolderName);
    }

    private void ApplyGameRoot(string? path)
    {
        if (string.Equals(path, _settings.GameRootOverride, StringComparison.OrdinalIgnoreCase)) return;
        _hooks.GameRoot(path);
        RefreshPaths();
        Dlg.Info(this, "Settings",
            "Restart Ostraplan to use the new install folder.");
    }

    private void PickSavesDir()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Pick your Ostranauts Saves folder",
            InitialDirectory = _env?.SavesDir ?? "",
        };
        if (dlg.ShowDialog(this) != true) return;

        if (GameEnv.ResolveSaves(dlg.FolderName) is not { } resolved)
        {
            Dlg.Warn(this, "Settings", $"'{dlg.FolderName}' isn't a folder Ostraplan can read.");
            return;
        }
        // A save is a subfolder holding a zip. An empty folder is allowed (a new install saves into it later),
        // but say so, because the likeliest reason is the wrong folder.
        var saves = Directory.EnumerateDirectories(resolved).Count(d => Directory.EnumerateFiles(d, "*.zip").Any());
        if (saves == 0)
            Dlg.Warn(this, "Settings",
                $"No save games found in '{resolved}'.\n\n" +
                "Ostraplan will use it anyway. The right folder holds one subfolder per save.");
        ApplySavesDir(dlg.FolderName);
    }

    private void ApplySavesDir(string? path)
    {
        if (string.Equals(path, _settings.SavesDirOverride, StringComparison.OrdinalIgnoreCase)) return;
        _hooks.SavesDir(path);
        RefreshPaths();
    }

    /// <summary>Re-read the resolved folders from the host's environment (a change to either rebuilds it) and
    /// show where each one came from, so "automatic" is never a mystery.</summary>
    private void RefreshPaths(GameEnv? env = null)
    {
        _env = env ?? _env;

        _gameRootText.Text = _settings.GameRootOverride is { Length: > 0 } root
            ? root + "\n(set here)"
            : _env is { } e ? $"{e.GameRoot}\n(found via {e.DiscoveredVia})" : "not found";

        _savesText.Text = _settings.SavesDirOverride is { Length: > 0 }
            ? (GameEnv.ResolveSaves(_settings.SavesDirOverride) ?? _settings.SavesDirOverride) + "\n(set here)"
            : _env?.SavesDir is { } saves
                ? saves + (_env.GameSavesSetting == saves
                    ? "\n(from the game's own save location setting)"
                    : "\n(the default location)")
                : "not found";
    }

    /// <summary>Tell the dialog the host rebuilt its environment, so the resolved paths it shows stay true.</summary>
    public void EnvironmentChanged(GameEnv? env) => RefreshPaths(env);

    // ---- layout helpers ----

    private static void Section(Panel parent, string header, bool first = false)
    {
        if (!first)
            parent.Children.Add(new Border
            {
                Height = 1, Background = ThemeManager.PanelBorder, Margin = new Thickness(0, 18, 0, 0),
            });
        parent.Children.Add(new TextBlock
        {
            Text = header, Foreground = Dim, FontWeight = FontWeights.Bold, FontSize = 11,
            Margin = new Thickness(0, first ? 0 : 14, 0, 2),
        });
    }

    /// <summary>One setting: its name, the control, and the line explaining what it does.</summary>
    private static UIElement Row(string label, UIElement control, string note)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = label, Foreground = Ink, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5),
        });
        panel.Children.Add(control);
        panel.Children.Add(new TextBlock
        {
            Text = note, Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 0),
        });
        return panel;
    }

    /// <summary>
    /// The offered colours as clickable chips: the app's default, black, white, three greys, and seven hues at
    /// three brightnesses.
    ///
    /// <para>Chips are <see cref="Border"/>s rather than buttons on purpose. A chip has to <b>be</b> its colour,
    /// and a Button carrying a local Background loses it to Fluent's hover state the moment the pointer crosses
    /// it (CONVENTIONS), which on a colour picker means every swatch turns grey exactly when you are aiming at
    /// it. A Border has no control template to fight.</para>
    /// </summary>
    private static UIElement SwatchGrid(Action<string> pick)
    {
        var wrap = new WrapPanel { MaxWidth = 460 };
        foreach (var swatch in Backdrop.Palette)
        {
            var (r, g, b) = Backdrop.ParseColour(swatch.Hex)!.Value;
            var chip = new Border
            {
                Width = 26, Height = 20, Margin = new Thickness(0, 0, 4, 4), CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(Color.FromRgb(r, g, b)),
                BorderBrush = ThemeManager.PanelBorder, BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = $"{swatch.Name} ({swatch.Hex})",
            };
            var hex = swatch.Hex;
            chip.MouseLeftButtonDown += (_, _) => pick(hex);
            wrap.Children.Add(chip);
        }
        return wrap;
    }

    private static TextBlock PathValue() => new()
    {
        Foreground = Ink, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A folder setting: the resolved path, a Change… button and the way back to automatic.</summary>
    private static UIElement PathRow(string label, TextBlock value, string note, Action change, Action auto)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        var pick = new Button { Content = "Change…", Padding = new Thickness(12, 2, 12, 2) };
        pick.Click += (_, _) => change();
        var reset = new Button { Content = "Automatic", Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(6, 0, 0, 0) };
        reset.Click += (_, _) => auto();
        buttons.Children.Add(pick);
        buttons.Children.Add(reset);

        var dock = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        dock.Children.Add(buttons);
        dock.Children.Add(value);
        return Row(label, dock, note);
    }
}
