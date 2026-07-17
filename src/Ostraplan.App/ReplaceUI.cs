using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// Picks a compatible replacement for the current selection — the "Replace with…" action.
/// Shows only parts of the same render layer and footprint (already filtered by the caller),
/// each with its palette thumbnail. Double-click or Replace applies the swap.
/// </summary>
public sealed class ReplacePickerDialog : Window
{
    private static Brush Ink => ThemeManager.Ink;
    private static Brush Dim => ThemeManager.Dim;
    private static Brush FieldBg => ThemeManager.FieldBg;

    private readonly ListBox _list;
    private readonly IReadOnlyList<PartVM> _all;

    public PartDef? Selected { get; private set; }

    /// <summary><paramref name="noteText"/> overrides the default explanation — the missing-mod stand-in picker
    /// (<see cref="MissingPartsDialog"/>) offers the whole palette rather than same-layer/footprint swaps, so the
    /// default note would be wrong there.</summary>
    public ReplacePickerDialog(IReadOnlyList<PartVM> compatible, string what, string? title = null, string? noteText = null)
    {
        _all = compatible;

        Title = title ?? "Replace with…";
        Width = 460; Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.WindowBg;

        var root = new DockPanel { Margin = new Thickness(16) };

        var note = new TextBlock
        {
            Text = noteText
                   ?? $"Swap {what} for another part of the same kind — same layer and footprint. Position and rotation are kept.",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        };
        DockPanel.SetDock(note, Dock.Top);
        root.Children.Add(note);

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
        var ok = new Button { Content = "Replace", Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 4, 16, 4), IsCancel = true };
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        _list = new ListBox
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            ItemTemplate = ThumbRow(), HorizontalContentAlignment = HorizontalAlignment.Stretch,
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
        _list.ItemsSource = _all.Where(vm => vm.Matches(q)).ToList();
        if (_list.Items.Count > 0 && _list.SelectedIndex < 0) _list.SelectedIndex = 0;
    }

    private void Accept()
    {
        if (_list.SelectedItem is not PartVM vm) return;
        Selected = vm.Part;
        DialogResult = true;
    }

    /// <summary>Row: the part's thumbnail beside its friendly name over its def/origin sub-line.</summary>
    private static DataTemplate ThumbRow()
    {
        var img = new FrameworkElementFactory(typeof(Image));
        img.SetBinding(Image.SourceProperty, new Binding(nameof(PartVM.Thumb)));
        img.SetValue(WidthProperty, 32.0);
        img.SetValue(HeightProperty, 32.0);
        img.SetValue(Image.StretchProperty, Stretch.Uniform);
        img.SetValue(MarginProperty, new Thickness(0, 0, 8, 0));
        img.SetValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.NearestNeighbor);

        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(PartVM.Friendly)));
        name.SetValue(TextBlock.ForegroundProperty, Ink);
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);

        var sub = new FrameworkElementFactory(typeof(TextBlock));
        sub.SetBinding(TextBlock.TextProperty, new Binding(nameof(PartVM.Sub)));
        sub.SetValue(TextBlock.ForegroundProperty, Dim);
        sub.SetValue(TextBlock.FontSizeProperty, 11.0);

        var text = new FrameworkElementFactory(typeof(StackPanel));
        text.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        text.AppendChild(name);
        text.AppendChild(sub);

        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        panel.SetValue(MarginProperty, new Thickness(2, 3, 2, 3));
        panel.AppendChild(img);
        panel.AppendChild(text);
        return new DataTemplate { VisualTree = panel };
    }
}
