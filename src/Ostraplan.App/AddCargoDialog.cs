using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// The add-item picker for the inventory editor: a searchable list of exactly what a container accepts (its
/// <c>strContainerCT</c> filter, via <see cref="ContainerFilter"/> — "the Law" for cargo) with real sprites, plus
/// a quantity. Returns the chosen def + count; capacity is enforced by the caller when it places them.
/// </summary>
public sealed class AddCargoDialog : Window
{
    private static Brush Ink => ThemeManager.Ink;
    private static Brush Dim => ThemeManager.Dim;
    private static Brush FieldBg => ThemeManager.FieldBg;
    private static Brush PanelBorder => ThemeManager.PanelBorder;

    private const int MaxRows = 400;   // cap the rendered list (search narrows); avoids a huge unvirtualized list

    private readonly SpriteCache _sprites;
    private readonly IReadOnlyList<PartDef> _offered;
    private readonly ListBox _list = new();
    private readonly TextBox _search = new();
    private readonly TextBox _qty = new();
    private readonly TextBlock _count = new();

    /// <summary>The picked item and how many, or null when cancelled.</summary>
    public (PartDef Def, int Quantity)? Chosen { get; private set; }

    public AddCargoDialog(Catalog catalog, SpriteCache sprites, PartDef container, IReadOnlyList<PartDef> offered)
    {
        _sprites = sprites;
        _offered = offered.OrderBy(p => p.Friendly, StringComparer.OrdinalIgnoreCase).ToList();

        Title = "Add to " + container.Friendly;
        Width = 380;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.WindowBg;

        var root = new DockPanel { Margin = new Thickness(14) };

        // search
        var searchRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(searchRow, Dock.Top);
        searchRow.Children.Add(new TextBlock { Text = "Search", Foreground = Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        _search.Background = FieldBg;
        _search.Foreground = Ink;
        _search.BorderBrush = PanelBorder;
        _search.Padding = new Thickness(4, 2, 4, 2);
        _search.TextChanged += (_, _) => Populate();
        searchRow.Children.Add(_search);
        root.Children.Add(searchRow);

        // footer: quantity + buttons
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        footer.Children.Add(new TextBlock { Text = "Qty", Foreground = Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        _qty.Text = "1";
        _qty.Width = 44;
        _qty.Background = FieldBg;
        _qty.Foreground = Ink;
        _qty.BorderBrush = PanelBorder;
        _qty.Padding = new Thickness(4, 2, 4, 2);
        _qty.HorizontalContentAlignment = HorizontalAlignment.Right;
        _qty.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
        footer.Children.Add(_qty);

        var ok = new Button { Content = "Add", Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(10, 0, 0, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(6, 0, 0, 0), IsCancel = true };
        DockPanel.SetDock(ok, Dock.Right);
        DockPanel.SetDock(cancel, Dock.Right);
        ok.Click += OnOk;
        // Right-docked children render right-to-left in order added: add Cancel then OK so OK sits leftmost of the pair.
        footer.Children.Add(cancel);
        footer.Children.Add(ok);
        _count.Foreground = Dim;
        _count.VerticalAlignment = VerticalAlignment.Center;
        _count.Margin = new Thickness(12, 0, 0, 0);
        footer.Children.Add(_count);
        root.Children.Add(footer);

        // list (fills the middle)
        _list.Background = FieldBg;
        _list.BorderBrush = PanelBorder;
        _list.MouseDoubleClick += (_, _) => OnOk(this, new RoutedEventArgs());
        root.Children.Add(_list);

        Content = root;
        Populate();
        Loaded += (_, _) => _search.Focus();
    }

    private void Populate()
    {
        var term = _search.Text.Trim();
        var matches = _offered.Where(p =>
            term.Length == 0
            || p.Friendly.Contains(term, StringComparison.OrdinalIgnoreCase)
            || p.DefName.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();

        _list.Items.Clear();
        foreach (var part in matches.Take(MaxRows))
            _list.Items.Add(Row(part));

        _count.Text = matches.Count > MaxRows
            ? $"{MaxRows} of {matches.Count} — refine search"
            : $"{matches.Count} item{(matches.Count == 1 ? "" : "s")}";
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
    }

    private ListBoxItem Row(PartDef part)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var img = new Image
        {
            Source = _sprites.Thumb(part),
            Width = 28,
            Height = 28,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 8, 0),
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        panel.Children.Add(img);
        panel.Children.Add(new TextBlock { Text = part.Friendly, Foreground = Ink, VerticalAlignment = VerticalAlignment.Center });
        return new ListBoxItem { Content = panel, Tag = part, Padding = new Thickness(4, 2, 4, 2) };
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (_list.SelectedItem is not ListBoxItem { Tag: PartDef part }) return;
        var qty = int.TryParse(_qty.Text, out var n) ? Math.Max(1, n) : 1;
        Chosen = (part, qty);
        DialogResult = true;
    }
}
