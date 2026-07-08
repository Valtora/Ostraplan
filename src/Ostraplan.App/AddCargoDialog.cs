using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
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
    private readonly Func<PartDef, int> _capacityFor;
    private readonly ListBox _list = new();
    private readonly TextBox _search = new();
    private readonly TextBox _qty = new();
    private readonly TextBlock _count = new();
    private readonly Button _minus = new();
    private readonly Button _plus = new();
    private readonly Button _ok = new();
    private readonly TextBlock _cap = new();
    private int _max = 1;
    private bool _clamping;

    /// <summary>The picked item and how many, or null when cancelled.</summary>
    public (PartDef Def, int Quantity)? Chosen { get; private set; }

    /// <param name="capacityFor">How many of a given def still fit the target container (see
    /// <see cref="CargoEdit.MaxAddable"/>) — used to cap the quantity so the picker can't offer more than fits.</param>
    public AddCargoDialog(Catalog catalog, SpriteCache sprites, PartDef container, IReadOnlyList<PartDef> offered,
        Func<PartDef, int> capacityFor)
    {
        _sprites = sprites;
        _offered = offered.OrderBy(p => p.Friendly, StringComparer.OrdinalIgnoreCase).ToList();
        _capacityFor = capacityFor;

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
        // result count sits at the right of the search row (the footer is the quantity stepper's)
        _count.Foreground = Dim;
        _count.VerticalAlignment = VerticalAlignment.Center;
        _count.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(_count, Dock.Right);
        searchRow.Children.Add(_count);
        _search.Background = FieldBg;
        _search.Foreground = Ink;
        _search.BorderBrush = PanelBorder;
        _search.Padding = new Thickness(4, 2, 4, 2);
        _search.TextChanged += (_, _) => Populate();
        searchRow.Children.Add(_search);
        root.Children.Add(searchRow);

        // footer: quantity stepper + buttons
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        footer.Children.Add(new TextBlock { Text = "Qty", Foreground = Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });

        // −/[ N ]/+ stepper. The quantity field uses a bare template (no Fluent clear "×" button, which overlapped
        // the number in the narrow field); it stays digit-only and is clamped to what the selected item can fit.
        _minus.Content = "−";
        StepperButton(_minus);
        _minus.Click += (_, _) => SetQty(Qty - 1);
        footer.Children.Add(_minus);

        _qty.Text = "1";
        _qty.Width = 52;
        _qty.Background = FieldBg;
        _qty.Foreground = Ink;
        _qty.BorderBrush = PanelBorder;
        _qty.BorderThickness = new Thickness(1);
        _qty.Padding = new Thickness(4, 2, 4, 2);
        _qty.Margin = new Thickness(4, 0, 4, 0);
        _qty.Template = BareTextBoxTemplate();
        _qty.HorizontalContentAlignment = HorizontalAlignment.Center;
        _qty.VerticalContentAlignment = VerticalAlignment.Center;
        _qty.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
        _qty.TextChanged += (_, _) => OnQtyTyped();
        footer.Children.Add(_qty);

        _plus.Content = "+";
        StepperButton(_plus);
        _plus.Click += (_, _) => SetQty(Qty + 1);
        footer.Children.Add(_plus);

        _ok.Content = "Add";
        _ok.Padding = new Thickness(14, 3, 14, 3);
        _ok.Margin = new Thickness(10, 0, 0, 0);
        _ok.IsDefault = true;
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(6, 0, 0, 0), IsCancel = true };
        DockPanel.SetDock(_ok, Dock.Right);
        DockPanel.SetDock(cancel, Dock.Right);
        _ok.Click += OnOk;
        // Right-docked children render right-to-left in order added: add Cancel then OK so OK sits leftmost of the pair.
        footer.Children.Add(cancel);
        footer.Children.Add(_ok);

        // "of N" capacity hint sits between the stepper and the buttons
        _cap.Foreground = Dim;
        _cap.VerticalAlignment = VerticalAlignment.Center;
        _cap.Margin = new Thickness(8, 0, 0, 0);
        footer.Children.Add(_cap);
        root.Children.Add(footer);

        // list (fills the middle)
        _list.Background = FieldBg;
        _list.BorderBrush = PanelBorder;
        _list.SelectionChanged += (_, _) => OnSelectionChanged();
        _list.MouseDoubleClick += (_, _) => OnOk(this, new RoutedEventArgs());
        root.Children.Add(_list);

        Content = root;
        Populate();
        Loaded += (_, _) => _search.Focus();
    }

    private int Qty => int.TryParse(_qty.Text, out var n) ? n : 1;

    /// <summary>A small square stepper button, styled to the dialog's field palette.</summary>
    private static void StepperButton(Button b)
    {
        b.Width = 24;
        b.MinWidth = 24;
        b.Padding = new Thickness(0);
        b.VerticalContentAlignment = VerticalAlignment.Center;
        b.FontWeight = FontWeights.Bold;
    }

    /// <summary>A minimal TextBox control template: a bordered content host, no Fluent clear button (which stole
    /// space and hid the digits in the narrow quantity field). Themed from the field brushes the dialog already sets.</summary>
    private static ControlTemplate BareTextBoxTemplate() => (ControlTemplate)XamlReader.Parse(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
        "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='TextBox'>" +
        "<Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' " +
        "BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='3'>" +
        "<ScrollViewer x:Name='PART_ContentHost' Margin='{TemplateBinding Padding}' VerticalAlignment='Center'/>" +
        "</Border></ControlTemplate>");

    /// <summary>Recompute the cap for the newly selected item and re-clamp the quantity to it.</summary>
    private void OnSelectionChanged()
    {
        _max = _list.SelectedItem is ListBoxItem { Tag: PartDef part } ? Math.Max(0, _capacityFor(part)) : 0;
        _cap.Text = _max > 0 ? $"of {_max}" : "container full";
        _ok.IsEnabled = _max > 0;
        SetQty(Qty);   // re-clamp to the new max
    }

    /// <summary>Clamp the typed value into [1, max] and reflect it, without recursing through TextChanged.</summary>
    private void OnQtyTyped()
    {
        if (_clamping || _qty.Text.Length == 0) return;   // allow a transient empty field while editing
        var clamped = Math.Clamp(Qty, _max > 0 ? 1 : 0, _max);
        if (clamped != Qty) SetQty(clamped);
    }

    private void SetQty(int value)
    {
        var v = _max > 0 ? Math.Clamp(value, 1, _max) : 0;
        _clamping = true;
        _qty.Text = v.ToString();
        _qty.CaretIndex = _qty.Text.Length;
        _clamping = false;
        _minus.IsEnabled = v > 1;
        _plus.IsEnabled = v < _max;
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
        if (_list.SelectedItem is not ListBoxItem { Tag: PartDef part } || _max <= 0) return;
        Chosen = (part, Math.Clamp(Qty, 1, _max));
        DialogResult = true;
    }
}
