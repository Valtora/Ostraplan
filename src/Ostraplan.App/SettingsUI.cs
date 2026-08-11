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
/// <param name="ModOverrides">Whether modded parts may be placed against the core placement law.</param>
/// <param name="GameRoot">The Ostranauts install folder, or null to go back to auto-detection.</param>
/// <param name="SavesDir">The Saves folder, or null to go back to auto-detection.</param>
public sealed record SettingsHooks(
    Action<string> Theme,
    Action<double> Scale,
    Action<bool> ModOverrides,
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
    private GameEnv? _env;

    private readonly TextBlock _gameRootText, _savesText;
    private bool _init = true;   // suppress the combo's SelectionChanged during the initial fill

    public SettingsDialog(AppSettings settings, GameEnv? env, SettingsHooks hooks)
    {
        _settings = settings;
        _hooks = hooks;
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

        var close = new Button
        {
            Content = "Close", Padding = new Thickness(20, 4, 20, 4), IsDefault = true, IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0),
        };
        close.Click += (_, _) => Close();
        body.Children.Add(close);

        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

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
            "Magnifies everything Ostraplan draws — toolbar, panels, dialogs, reports and the canvas. For a "
            + "high-resolution monitor run at 100% Windows scaling, where the text would otherwise be tiny. "
            + "Dialogs and reports resize with it; the main window keeps the size you gave it.");
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
            Title = "Pick the Ostranauts folder (inside steamapps\\common)",
            InitialDirectory = _env?.GameRoot ?? "",
        };
        if (dlg.ShowDialog(this) != true) return;

        if (!Directory.Exists(Path.Combine(dlg.FolderName, "Ostranauts_Data")))
        {
            Dlg.Warn(this, "Settings",
                $"'{dlg.FolderName}' doesn't look like an Ostranauts install.\n\n" +
                "The folder should hold an Ostranauts_Data folder beside the game's exe.");
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
