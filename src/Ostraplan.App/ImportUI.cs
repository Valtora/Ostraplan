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
    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0xD8, 0xDD, 0xE4));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xAF));
    private static readonly Brush FieldBg = new SolidColorBrush(Color.FromRgb(0x1C, 0x1E, 0x23));

    private readonly ListBox _list;
    private readonly IReadOnlyList<ShipFileEntry> _all;

    public ShipFileEntry? Selected { get; private set; }

    public TemplateBrowserDialog(IReadOnlyList<ShipFileEntry> ships)
    {
        _all = ships;

        Title = "Import a ship template";
        Width = 460; Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x23, 0x26, 0x2C));

        var root = new DockPanel { Margin = new Thickness(16) };

        var search = new TextBox
        {
            Foreground = Ink, Background = FieldBg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3F, 0x47)),
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

    private static DataTemplate RowTemplate()
    {
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ShipFileEntry.Name)));
        name.SetValue(TextBlock.ForegroundProperty, Ink);
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);

        var origin = new FrameworkElementFactory(typeof(TextBlock));
        origin.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ShipFileEntry.Origin)));
        origin.SetValue(TextBlock.ForegroundProperty, Dim);
        origin.SetValue(TextBlock.FontSizeProperty, 11.0);

        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(MarginProperty, new Thickness(2, 3, 2, 3));
        panel.AppendChild(name);
        panel.AppendChild(origin);
        return new DataTemplate { VisualTree = panel };
    }
}
