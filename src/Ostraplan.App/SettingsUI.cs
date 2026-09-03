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
/// <param name="Backdrop">What the plan is drawn on, and its grid markings.</param>
/// <param name="ModOverrides">Whether modded parts may be placed against the core placement law.</param>
/// <param name="NavModuleArt">Whether the arrange window draws the nav modules with the game's own art.</param>
/// <param name="GameRoot">The Ostranauts install folder, or null to go back to auto-detection.</param>
/// <param name="SavesDir">The Saves folder, or null to go back to auto-detection.</param>
public sealed record SettingsHooks(
    Action<string> Theme,
    Action<double> Scale,
    Action<BackdropSettings> Backdrop,
    Action<bool> ModOverrides,
    Action<bool> NavModuleArt,
    Action<string?> GameRoot,
    Action<string?> SavesDir);

/// <summary>
/// Ostraplan's own preferences: appearance (theme and UI scale), the one editing rule that is a preference rather
/// than a view, and the two folders it reads. Everything here is app-wide and persisted in
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
        body.Children.Add(NavArtRow());

        Section(body, "THE PLAN'S BACKDROP");
        body.Children.Add(BackdropKindRow());
        body.Children.Add(_solidRow);
        body.Children.Add(_checkerRow);
        body.Children.Add(_localeRow);
        body.Children.Add(CoarseGridRow());

        Section(body, "EDITING");
        body.Children.Add(ModOverrideRow());

        Section(body, "GAME FOLDERS");
        _gameRootText = PathValue();
        body.Children.Add(PathRow(
            "Ostranauts install",
            _gameRootText,
            "Where Ostraplan reads the game's data and sprites. Found through Steam automatically; set it by hand "
            + "for a non-Steam or relocated install. The data is read at launch, so a change takes effect next "
            + "time Ostraplan starts.",
            PickGameRoot,
            () => ApplyGameRoot(null)));

        _savesText = PathValue();
        body.Children.Add(PathRow(
            "Saves",
            _savesText,
            "Where your save games live, for importing a ship and writing an edit back. Ostraplan follows the "
            + "game's own save location setting, so set this only if your saves are somewhere neither the game "
            + "nor Ostraplan knows about. Applies immediately.",
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
            "Ostraplan's chrome only. The ship canvas stays dark either way, because the game's sprites are pixel "
            + "art drawn for dark space.");
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
            "Scales everything Ostraplan draws — toolbar, panels, dialogs, reports and the canvas. Above 100% for "
            + "a high-resolution monitor run at 100% Windows scaling, where the text would otherwise be tiny; "
            + "below it to fit more into the window you have, on a laptop panel or beside a second copy of the "
            + "app. Dialogs and reports resize with it; the main window keeps the size you gave it.");
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
            "The Arrange screen window shows each module as the panel you would see at the console in game, read "
            + "from your install, so a layout can be judged by eye. Off, or when the art cannot be read, the "
            + "modules are flat labelled panels. A picture of the module, not a live screen: fuel bars, the "
            + "map and the callsign stay blank.");
    }

    // ---- the plan's backdrop (#43) ----

    private readonly ContentControl _solidRow = new();
    private readonly ContentControl _checkerRow = new();
    private readonly ContentControl _localeRow = new();
    private ComboBox? _localeCombo;

    /// <summary>The backdrop as the dialog currently has it. Every control edits a copy of this and pushes the
    /// whole record back, so a change to one field never resets another.</summary>
    private BackdropSettings Current => _settings.BackdropOrDefault();

    private void Push(BackdropSettings next)
    {
        if (_init) return;
        _hooks.Backdrop(next.Clamped());
        SyncBackdropRows();
    }

    /// <summary>Show only the controls the chosen kind actually uses. A checkerboard's second colour and a
    /// locale's dimming are meaningless to each other, and a dialog that shows every control for every kind makes
    /// the reader work out which of them is live.</summary>
    private void SyncBackdropRows()
    {
        var kind = Current.Kind;
        _solidRow.Visibility = kind is BackdropKind.Solid or BackdropKind.Checker ? Visibility.Visible : Visibility.Collapsed;
        _checkerRow.Visibility = kind == BackdropKind.Checker ? Visibility.Visible : Visibility.Collapsed;
        _localeRow.Visibility = kind == BackdropKind.Locale ? Visibility.Visible : Visibility.Collapsed;
    }

    private UIElement BackdropKindRow()
    {
        var combo = new ComboBox { Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
        combo.Items.Add("Solid colour");
        combo.Items.Add("Checkerboard");
        combo.Items.Add("A place from the game");
        combo.SelectedIndex = (int)Current.Kind;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex < 0) return;
            var kind = (BackdropKind)combo.SelectedIndex;
            var next = Current with { Kind = kind };
            // Choosing the game's art with nothing picked yet would show the old backdrop and look like a dead
            // control, so the first locale in the list stands in until the user picks one.
            if (kind == BackdropKind.Locale && next.Locale is null && Locales().FirstOrDefault() is { } first)
                next = next with { Locale = first.Name };
            Push(next);
            if (_localeCombo is { } lc && lc.SelectedIndex < 0 && lc.Items.Count > 0) lc.SelectedIndex = 0;
        };

        _solidRow.Content = SolidRow();
        _checkerRow.Content = CheckerRow();
        _localeRow.Content = LocaleRow();

        return Row("Backdrop", combo,
            "What the plan is drawn on. A dark hull on the near-black default is hard to read, which is what this "
            + "is for. Whatever you pick is also what a Design ▸ Snapshot PNG is drawn on. It applies to every "
            + "open design and is remembered between sessions; it is not part of a design, so a ship you send "
            + "somebody opens on their backdrop, not yours.");
    }

    private UIElement SolidRow()
    {
        var hex = new TextBox
        {
            Width = 100, Text = Current.Solid, FontFamily = new FontFamily("Consolas"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
        };
        var swatches = SwatchGrid(pick =>
        {
            hex.Text = pick;
            Push(Current with { Solid = pick });
        });
        hex.LostFocus += (_, _) =>
        {
            var normalised = Backdrop.NormaliseColour(hex.Text, Current.Solid);
            hex.Text = normalised;
            Push(Current with { Solid = normalised });
        };

        var panel = new StackPanel();
        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        top.Children.Add(hex);
        top.Children.Add(new TextBlock
        {
            Text = "or pick one below", Foreground = Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(top);
        panel.Children.Add(swatches);

        return Row("Colour", panel,
            "Any #RRGGBB, or one of the swatches. On a light colour the plan's grid, hover ring and origin marker "
            + "switch to dark ink so they stay visible.");
    }

    private UIElement CheckerRow()
    {
        var hex = new TextBox
        {
            Width = 100, Text = Current.CheckerAlt, FontFamily = new FontFamily("Consolas"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
        };
        hex.LostFocus += (_, _) =>
        {
            var normalised = Backdrop.NormaliseColour(hex.Text, Current.CheckerAlt);
            hex.Text = normalised;
            Push(Current with { CheckerAlt = normalised });
        };

        var size = new Slider
        {
            Minimum = BackdropSettings.MinCheckerSquare, Maximum = BackdropSettings.MaxCheckerSquare,
            Value = Current.CheckerSquare, TickFrequency = 8, IsSnapToTickEnabled = true,
            Width = 180, VerticalAlignment = VerticalAlignment.Center,
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
        row.Children.Add(hex);
        row.Children.Add(size);
        row.Children.Add(readout);

        return Row("Second colour and square size", row,
            "The other half of the checkerboard, against the colour above. A hull never matches both squares at "
            + "once, which is the whole point of a missing-texture check pattern.");
    }

    private IReadOnlyList<ParallaxLocale> Locales() =>
        _catalog is { } c ? ParallaxCatalog.All(c) : [];

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
            Width = 180, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
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

        var note = locales.Count > 0
            ? "The game's own parallax art for a place, composited into one backdrop. Dimming darkens it so the "
              + "ship stays the thing you are reading; at 0% it is the art as the game draws it. Each place "
              + "always composites the same way, so a screenshot is repeatable."
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

        return Row("Scale markings", combo,
            "A brighter grid line every so many tiles, measured from the ship's origin. The one-tile grid stays, "
            + "so you can still count tiles inside a marking; this is for judging how big a hull is getting "
            + "without counting at all.");
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
            "Places a modded part where Ostraplan's core-game placement rules say it doesn't fit, and flags it as "
            + "a warning to verify in game. Core parts stay enforced either way.");
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
                $"The folder to pick is the one holding {GameEnv.GameExeName} and the Ostranauts_Data folder, " +
                "usually steamapps\\common\\Ostranauts.");
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
            "Ostraplan reads the game's data once, at launch.\n\n" +
            "Restart it for the new install folder to take effect.");
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
                "Ostraplan will use it anyway, in case the saves are yet to be written.\n" +
                "A saves folder holds one folder per save, each with a .zip inside it.");
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
