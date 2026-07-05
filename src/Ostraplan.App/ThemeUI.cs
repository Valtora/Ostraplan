using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// The "Apply theme…" dialog: pick a wall skin and/or a floor skin, and every wall/floor on the ship
/// re-skins to it. Two thumbnail columns (walls, floors), each optional — leaving one unset keeps
/// those parts as they are. Only sprites change; rooms/airtightness/rating are unaffected.
/// </summary>
public sealed class ThemePickerDialog : Window
{
    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0xD8, 0xDD, 0xE4));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xAF));
    private static readonly Brush FieldBg = new SolidColorBrush(Color.FromRgb(0x1C, 0x1E, 0x23));

    private readonly Column _walls;
    private readonly Column _floors;

    public PartDef? SelectedWall => _walls.Selected;
    public PartDef? SelectedFloor => _floors.Selected;

    public ThemePickerDialog(
        IReadOnlyList<PartVM> wallSkins, string? currentWall, int wallCount,
        IReadOnlyList<PartVM> floorSkins, string? currentFloor, int floorCount)
    {
        Title = "Apply theme…";
        Width = 720; Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x23, 0x26, 0x2C));

        var root = new DockPanel { Margin = new Thickness(16) };

        var note = new TextBlock
        {
            Text = "Re-skin every wall and floor on the ship to a chosen style. Only sprites and names change — "
                 + "rooms, airtightness, certification and rating are unaffected. Select a wall style, a floor style, "
                 + "or both; leave a column untouched to keep those parts as they are.",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        };
        DockPanel.SetDock(note, Dock.Top);
        root.Children.Add(note);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var ok = new Button { Content = "Apply", Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 4, 16, 4), IsCancel = true };
        ok.Click += (_, _) => { DialogResult = true; };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var cols = new Grid();
        cols.ColumnDefinitions.Add(new ColumnDefinition());
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        cols.ColumnDefinitions.Add(new ColumnDefinition());

        _walls = new Column($"WALLS — {wallCount} placed", wallSkins, currentWall);
        _floors = new Column($"FLOORS — {floorCount} placed", floorSkins, currentFloor);
        Grid.SetColumn(_walls.Root, 0);
        Grid.SetColumn(_floors.Root, 2);
        cols.Children.Add(_walls.Root);
        cols.Children.Add(_floors.Root);
        root.Children.Add(cols);

        Content = root;
    }

    /// <summary>One re-skin column: header, "keep current" clear, search box and thumbnail list.</summary>
    private sealed class Column
    {
        private readonly ListBox _list;
        private readonly IReadOnlyList<PartVM> _all;
        public DockPanel Root { get; }

        public PartDef? Selected => (_list.SelectedItem as PartVM)?.Part;

        public Column(string header, IReadOnlyList<PartVM> skins, string? current)
        {
            _all = skins;
            Root = new DockPanel();

            // Construct the list first so the "Keep current" handler captures a non-null field;
            // it's still added to the panel last, keeping it the DockPanel fill element.
            _list = new ListBox
            {
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                ItemTemplate = ThumbRow(), HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };

            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var clear = new Button { Content = "Keep current", Padding = new Thickness(8, 1, 8, 1), FontSize = 11 };
            clear.Click += (_, _) => _list.SelectedItem = null;
            DockPanel.SetDock(clear, Dock.Right);
            head.Children.Add(clear);
            head.Children.Add(new TextBlock { Text = header, Foreground = Dim, FontWeight = FontWeights.Bold, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            DockPanel.SetDock(head, Dock.Top);
            Root.Children.Add(head);

            var search = new TextBox
            {
                Foreground = Ink, Background = FieldBg,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3F, 0x47)),
                Padding = new Thickness(5, 3, 5, 3), CaretBrush = Ink, Margin = new Thickness(0, 0, 0, 6),
            };
            search.TextChanged += (_, _) => Refresh(search.Text);
            DockPanel.SetDock(search, Dock.Top);
            Root.Children.Add(search);

            Root.Children.Add(_list);

            Refresh("");
            // Preselect the ship's current skin so the dialog shows what's applied now.
            if (current is not null && _all.FirstOrDefault(v => v.Part.DefName == current) is { } cur)
            {
                _list.SelectedItem = cur;
                _list.ScrollIntoView(cur);
            }
        }

        private void Refresh(string search)
        {
            var q = search.Trim();
            var keep = _list.SelectedItem as PartVM;
            _list.ItemsSource = _all.Where(vm => vm.Matches(q)).ToList();
            if (keep is not null && _list.Items.Contains(keep)) _list.SelectedItem = keep;
        }
    }

    /// <summary>Row: the skin's thumbnail beside its friendly name over its def/origin sub-line.</summary>
    private static DataTemplate ThumbRow()
    {
        var img = new FrameworkElementFactory(typeof(Image));
        img.SetBinding(Image.SourceProperty, new Binding(nameof(PartVM.Thumb)));
        img.SetValue(FrameworkElement.WidthProperty, 32.0);
        img.SetValue(FrameworkElement.HeightProperty, 32.0);
        img.SetValue(Image.StretchProperty, Stretch.Uniform);
        img.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
        img.SetValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.NearestNeighbor);

        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(PartVM.Friendly)));
        name.SetValue(TextBlock.ForegroundProperty, Ink);
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);

        var sub = new FrameworkElementFactory(typeof(TextBlock));
        sub.SetBinding(TextBlock.TextProperty, new Binding(nameof(PartVM.Sub)));
        sub.SetValue(TextBlock.ForegroundProperty, Dim);
        sub.SetValue(TextBlock.FontSizeProperty, 11.0);
        sub.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);

        var text = new FrameworkElementFactory(typeof(StackPanel));
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        text.AppendChild(name);
        text.AppendChild(sub);

        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        panel.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 3, 2, 3));
        panel.AppendChild(img);
        panel.AppendChild(text);
        return new DataTemplate { VisualTree = panel };
    }
}
