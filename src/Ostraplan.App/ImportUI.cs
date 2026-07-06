using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// Browses the ship templates the game would see — core plus every loaded mod's
/// <c>data/ships</c> — as a searchable list. Double-click or Import loads the selection
/// as a fresh editable design.
/// </summary>
public sealed class TemplateBrowserDialog : Window
{
    private static Brush Ink => ThemeManager.Ink;
    private static Brush Dim => ThemeManager.Dim;
    private static Brush FieldBg => ThemeManager.FieldBg;

    private readonly ListBox _list;
    private readonly IReadOnlyList<ShipFileEntry> _all;

    public ShipFileEntry? Selected { get; private set; }

    public TemplateBrowserDialog(IReadOnlyList<ShipFileEntry> ships)
    {
        _all = ships;

        Title = "Import a ship template";
        Width = 460; Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.WindowBg;

        var root = new DockPanel { Margin = new Thickness(16) };

        var search = new TextBox
        {
            Foreground = Ink, Background = FieldBg,
            BorderBrush = ThemeManager.PanelBorder,
            Padding = new Thickness(5, 3, 5, 3), CaretBrush = Ink, Margin = new Thickness(0, 0, 0, 6),
        };
        search.TextChanged += (_, _) => Refresh(search.Text);
        DockPanel.SetDock(search, Dock.Top);
        root.Children.Add(search);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var ok = new Button { Content = "Import", Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 4, 16, 4), IsCancel = true };
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        _list = new ListBox
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            ItemTemplate = RowTemplate(), HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        _list.MouseDoubleClick += (_, _) => Accept();
        _list.KeyDown += (_, e) => { if (e.Key == Key.Enter) Accept(); };
        root.Children.Add(_list);

        Content = root;
        Refresh("");
        search.Focus();
    }

    private void Refresh(string search)
    {
        var q = search.Trim();
        _list.ItemsSource = _all
            .Where(e => q.Length == 0 || e.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (_list.Items.Count > 0 && _list.SelectedIndex < 0) _list.SelectedIndex = 0;
    }

    private void Accept()
    {
        if (_list.SelectedItem is not ShipFileEntry entry) return;
        Selected = entry;
        DialogResult = true;
    }

    private static DataTemplate RowTemplate() =>
        TwoLineRow(nameof(ShipFileEntry.Name), nameof(ShipFileEntry.Origin));

    internal static DataTemplate TwoLineRow(string titleProp, string subProp)
    {
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(titleProp));
        name.SetValue(TextBlock.ForegroundProperty, Ink);
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);

        var sub = new FrameworkElementFactory(typeof(TextBlock));
        sub.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(subProp));
        sub.SetValue(TextBlock.ForegroundProperty, Dim);
        sub.SetValue(TextBlock.FontSizeProperty, 11.0);

        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(MarginProperty, new Thickness(2, 3, 2, 3));
        panel.AppendChild(name);
        panel.AppendChild(sub);
        return new DataTemplate { VisualTree = panel };
    }
}

/// <summary>A row in the ship picker: the ship name (with a "you are here" tag) over its make/model/RegID
/// subtitle (with a "NOT OWNED" tag for stations/other vessels).</summary>
public sealed record ShipRow(string Title, string Sub, SaveShipChoice Choice);

/// <summary>Picks WHICH ship to edit from a save: the player's owned ships (from aMyShips), plus the ship they're
/// currently on if it isn't owned (a station/other vessel — editable but unsupported).</summary>
public sealed class ShipChoiceDialog : Window
{
    private readonly ListBox _list;

    public SaveShipChoice? Selected { get; private set; }

    public ShipChoiceDialog(string saveName, IReadOnlyList<SaveShipChoice> ships)
    {
        Title = "Choose a ship to edit";
        Width = 480; Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.WindowBg;

        var rows = ships.Select(c => new ShipRow(
            c.Name + (c.Current ? "   ·   you are here" : ""),
            c.Owned ? c.Sub : c.Sub + "   ·   NOT OWNED — station/other vessel (unsupported)",
            c)).ToList();

        var root = new DockPanel { Margin = new Thickness(16) };

        var note = new TextBlock
        {
            Text = $"Ships in save “{saveName}” that you own. Ostranauts imports the ship you're standing " +
                   "on, which may be a station — pick the one you mean. Ships you don't own are shown but editing " +
                   "them is unsupported and may break your save.",
            Foreground = ThemeManager.Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        };
        DockPanel.SetDock(note, Dock.Top);
        root.Children.Add(note);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var ok = new Button { Content = "Choose", Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 4, 16, 4), IsCancel = true };
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        _list = new ListBox
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            ItemsSource = rows, ItemTemplate = TemplateBrowserDialog.TwoLineRow(nameof(ShipRow.Title), nameof(ShipRow.Sub)),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        var currentIdx = rows.FindIndex(r => r.Choice.Current);
        _list.SelectedIndex = currentIdx >= 0 ? currentIdx : (rows.Count > 0 ? 0 : -1);
        _list.MouseDoubleClick += (_, _) => Accept();
        _list.KeyDown += (_, e) => { if (e.Key == Key.Enter) Accept(); };
        root.Children.Add(_list);

        Content = root;
    }

    private void Accept()
    {
        if (_list.SelectedItem is not ShipRow row) return;
        Selected = row.Choice;
        DialogResult = true;
    }
}

/// <summary>A save-game row for the picker: the player's ship name over "{player} · {save}".</summary>
public sealed record SaveRow(string ShipDisplay, string Sub, SaveEntry Entry);

/// <summary>Picks a save game to import the player's ship from. Shows each save's ship + character.</summary>
public sealed class SavePickerDialog : Window
{
    private static Brush Ink => ThemeManager.Ink;
    private static Brush FieldBg => ThemeManager.FieldBg;

    private readonly ListBox _list;

    public SaveEntry? Selected { get; private set; }

    public SavePickerDialog(IReadOnlyList<SaveEntry> saves)
    {
        Title = "Import a ship from a save game";
        Width = 460; Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.WindowBg;

        var rows = saves.Select(s => new SaveRow(
            s.ShipName.Length > 0 ? s.ShipName : "(unnamed ship)",
            string.Join("  ·  ", new[] { s.PlayerName, s.Name }.Where(x => x.Length > 0)),
            s)).ToList();

        var root = new DockPanel { Margin = new Thickness(16) };

        var note = new TextBlock
        {
            Text = "Imports the player's ship as a pristine layout — crew, cargo, wear and damage are discarded.",
            Foreground = ThemeManager.Dim, FontSize = 11,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        };
        DockPanel.SetDock(note, Dock.Top);
        root.Children.Add(note);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var ok = new Button { Content = "Import", Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 4, 16, 4), IsCancel = true };
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        _list = new ListBox
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            ItemsSource = rows, ItemTemplate = TemplateBrowserDialog.TwoLineRow(nameof(SaveRow.ShipDisplay), nameof(SaveRow.Sub)),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        if (rows.Count > 0) _list.SelectedIndex = 0;
        _list.MouseDoubleClick += (_, _) => Accept();
        _list.KeyDown += (_, e) => { if (e.Key == Key.Enter) Accept(); };
        root.Children.Add(_list);

        Content = root;
    }

    private void Accept()
    {
        if (_list.SelectedItem is not SaveRow row) return;
        Selected = row.Entry;
        DialogResult = true;
    }
}
