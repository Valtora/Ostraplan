using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// Picks what a loot spawner spawns: one entry from the set its type is offered (see
/// <see cref="SpawnerCatalog"/>).
///
/// <para>A dialog rather than a dropdown in the inspector because the lists are long. An object spawner is offered
/// every item-typed loot the install declares, which on stock data is 2,797 entries, and a combo box of that is
/// not a control anybody can use. The reporter asked for a search bar for exactly this reason, so the box takes
/// focus on open and the list narrows as you type.</para>
/// </summary>
public sealed class SpawnerTargetDialog : Window
{
    /// <summary>The chosen target name, or null when the dialog was cancelled.</summary>
    public string? Chosen { get; private set; }

    private readonly IReadOnlyList<SpawnerTarget> _all;
    private readonly ListBox _list = new();
    private readonly TextBox _search = new();
    private readonly TextBlock _count = new();

    public SpawnerTargetDialog(Catalog catalog, SpawnerType type, string current)
    {
        _all = SpawnerCatalog.For(catalog, type);

        Title = type switch
        {
            SpawnerType.Pspec => "Choose a person spec",
            SpawnerType.PspecLoot => "Choose a person loot table",
            _ => "Choose a loot table",
        };
        Width = 520;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.WindowBg;

        // DockPanel, not StackPanel: the buttons stay on screen and the list takes what is left, whatever the
        // window is sized to (CONVENTIONS).
        var root = new DockPanel { Margin = new Thickness(16) };

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        header.Children.Add(new TextBlock
        {
            Text = "Filter", Foreground = ThemeManager.Dim, FontSize = 11,
            Margin = new Thickness(0, 0, 0, 4),
        });
        _search.Background = ThemeManager.FieldBg;
        _search.Foreground = ThemeManager.Ink;
        _search.BorderBrush = ThemeManager.PanelBorder;
        _search.BorderThickness = new Thickness(1);
        _search.Padding = new Thickness(6, 4, 6, 4);
        _search.TextChanged += (_, _) => Refill();
        // Down from the box walks into the list without losing the text, which is how you use a filter box.
        _search.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Down or Key.Up) || _list.Items.Count == 0) return;
            _list.Focus();
            if (_list.SelectedIndex < 0) _list.SelectedIndex = 0;
            e.Handled = true;
        };
        header.Children.Add(_search);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };
        _count.Foreground = ThemeManager.Dim;
        _count.FontSize = 11;
        _count.VerticalAlignment = VerticalAlignment.Center;
        _count.HorizontalAlignment = HorizontalAlignment.Left;

        var ok = new Button { Content = "Choose", Padding = new Thickness(18, 4, 18, 4), IsDefault = true };
        ok.Click += (_, _) => Accept();
        var cancel = new Button
        {
            Content = "Cancel", Padding = new Thickness(18, 4, 18, 4), IsCancel = true,
            Margin = new Thickness(8, 0, 0, 0),
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(_count);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        _list.Background = ThemeManager.FieldBg;
        _list.Foreground = ThemeManager.Ink;
        _list.BorderBrush = ThemeManager.PanelBorder;
        _list.BorderThickness = new Thickness(1);
        _list.MouseDoubleClick += (_, _) => Accept();
        root.Children.Add(_list);

        Content = root;

        Refill();
        SelectByName(current);
        Loaded += (_, _) => _search.Focus();
    }

    /// <summary>Narrow the list to what the filter admits, keeping the current pick selected when it survives.</summary>
    private void Refill()
    {
        var search = _search.Text.Trim();
        var kept = _all.Where(t => t.Matches(search)).ToList();
        var wasSelected = (_list.SelectedItem as Row)?.Target.Name;

        _list.ItemsSource = kept.Select(t => new Row(t)).ToList();
        _count.Text = kept.Count == _all.Count
            ? $"{_all.Count} available"
            : $"{kept.Count} of {_all.Count}";

        if (wasSelected is not null) SelectByName(wasSelected);
        if (_list.SelectedIndex < 0 && _list.Items.Count > 0) _list.SelectedIndex = 0;
    }

    private void SelectByName(string name)
    {
        foreach (var item in _list.Items)
            if (item is Row row && string.Equals(row.Target.Name, name, StringComparison.Ordinal))
            {
                _list.SelectedItem = item;
                _list.ScrollIntoView(item);
                return;
            }
    }

    private void Accept()
    {
        if (_list.SelectedItem is not Row row) return;
        Chosen = row.Target.Name;
        DialogResult = true;
    }

    /// <summary>A list row. The list binds to this rather than to the target so <c>ToString</c> decides what is
    /// shown, which keeps the name in front of the reader: it is what the ship file carries and what a bug
    /// report will quote.</summary>
    private sealed record Row(SpawnerTarget Target)
    {
        public override string ToString() => Target.Display;
    }
}
