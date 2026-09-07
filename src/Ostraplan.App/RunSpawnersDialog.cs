using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// What to run, for which ship, and off which seed (#65).
///
/// <para>The seed is the reason this is a dialog rather than a menu entry that just goes. A spawner is random by
/// construction, so a run that produced a deck you liked has to be reproducible or it is not a design decision at
/// all: the number is reported afterwards and typed back in here. Left blank it is chosen for you, which is the
/// case almost everybody wants.</para>
/// </summary>
public sealed class RunSpawnersDialog : Window
{
    private readonly RadioButton _selectionOnly;
    private readonly RadioButton[] _conditions;
    private readonly TextBox _seed = new();

    /// <summary>True when only the selected spawners should run, false for every one in the design.</summary>
    public bool SelectionOnly => _selectionOnly.IsChecked == true;

    /// <summary>Which instantiation of the ship to roll for.</summary>
    public ShipCondition Condition =>
        (ShipCondition)Array.FindIndex(_conditions, r => r.IsChecked == true);

    /// <summary>The typed seed, or null to take a fresh one.</summary>
    public int? Seed =>
        int.TryParse(_seed.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <param name="selected">Runnable spawners in the current selection.</param>
    /// <param name="total">Runnable spawners in the whole design.</param>
    public RunSpawnersDialog(int selected, int total)
    {
        Title = "Run loot spawners";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.WindowBg;

        var body = new StackPanel { Margin = new Thickness(20, 18, 20, 16) };
        body.Children.Add(Head("Roll what these spawners would make"));
        body.Children.Add(Note(
            "A spawner is replaced by the items it rolls, because a design holding both would arrive carrying the "
            + "cargo twice. Undo puts it back."));

        // Scope. Only worth a control when the two numbers differ: a design with three spawners, all selected,
        // has nothing to decide.
        _selectionOnly = new RadioButton
        {
            Content = $"The {selected} selected", GroupName = "scope", IsChecked = selected > 0,
            Foreground = ThemeManager.Ink, Margin = new Thickness(0, 2, 0, 0),
        };
        var everything = new RadioButton
        {
            Content = $"All {total} in the design", GroupName = "scope", IsChecked = selected == 0,
            Foreground = ThemeManager.Ink, Margin = new Thickness(0, 2, 0, 0),
        };
        if (selected > 0 && selected != total)
        {
            body.Children.Add(Label("Run"));
            body.Children.Add(_selectionOnly);
            body.Children.Add(everything);
        }
        else
        {
            _selectionOnly.IsChecked = false;
            everything.IsChecked = true;
        }

        // The three condition gates. A spawner authored for a wreck makes nothing on a new ship and is left
        // alone, which is why the case has to be asked for rather than assumed.
        body.Children.Add(Label("As a ship the game builds"));
        _conditions =
        [
            Radio("New", "condition", true),
            Radio("Damaged", "condition", false),
            Radio("Derelict", "condition", false),
        ];
        foreach (var radio in _conditions) body.Children.Add(radio);

        body.Children.Add(Label("Seed"));
        _seed.Background = ThemeManager.FieldBg;
        _seed.Foreground = ThemeManager.Ink;
        _seed.BorderBrush = ThemeManager.PanelBorder;
        _seed.BorderThickness = new Thickness(1);
        _seed.Padding = new Thickness(6, 4, 6, 4);
        _seed.Margin = new Thickness(0, 2, 0, 0);
        body.Children.Add(_seed);
        body.Children.Add(Note("Leave it blank for a fresh roll. The number used is reported afterwards, so a "
            + "result you want back can be typed in here."));

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var ok = new Button
        {
            Content = "Run", Padding = new Thickness(18, 5, 18, 5), Margin = new Thickness(0, 0, 8, 0),
            MinWidth = 84, IsDefault = true,
            Background = ThemeManager.AccentBg, Foreground = ThemeManager.AccentText,
        };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 5, 16, 5), MinWidth = 84, IsCancel = true };
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        body.Children.Add(buttons);

        Content = body;
        Loaded += (_, _) => _seed.Focus();
    }

    private static TextBlock Head(string text) => new()
    {
        Text = text, Foreground = ThemeManager.Ink, FontWeight = FontWeights.SemiBold, FontSize = 15,
        TextWrapping = TextWrapping.Wrap,
    };

    // Prose wraps to the column it sits in rather than setting the window's width (CONVENTIONS).
    private static TextBlock Note(string text) => new()
    {
        Text = text, Foreground = ThemeManager.Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap,
        MaxWidth = 400, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 4),
    };

    private static TextBlock Label(string text) => new()
    {
        Text = text, Foreground = ThemeManager.Dim, FontSize = 11, Margin = new Thickness(0, 10, 0, 0),
    };

    private static RadioButton Radio(string text, string group, bool on) => new()
    {
        Content = text, GroupName = group, IsChecked = on, Foreground = ThemeManager.Ink,
        Margin = new Thickness(0, 2, 0, 0),
    };
}
