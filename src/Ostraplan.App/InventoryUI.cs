using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// The inventory viewer: shows what a placed container holds, mirroring the in-game inventory window rather than
/// a flat list. Loose cargo is laid out on the container's tile grid (each item occupying its footprint at its
/// packed cell, stacks collapsed with a count); equipped gear is shown on a paper-doll positioned from the host's
/// <c>dictSlotsLayout</c>. Nested containers and worn items are drill-in-able, with a breadcrumb to climb back
/// out — so a crate → a suit → its pockets can all be inspected. Read-only (authoring lands in a later phase).
/// </summary>
public sealed class InventoryWindow : Window
{
    private static Brush Ink => ThemeManager.Ink;
    private static Brush Dim => ThemeManager.Dim;
    private static Brush Accent => ThemeManager.Accent;
    private static Brush FieldBg => ThemeManager.FieldBg;
    private static Brush PanelBorder => ThemeManager.PanelBorder;

    private const int CellPx = 50;   // one inventory grid tile
    private const int SlotPx = 50;   // one paper-doll slot

    private readonly Catalog _catalog;
    private readonly SpriteCache _sprites;
    private readonly List<Node> _path = [];   // breadcrumb: root → current
    private readonly StackPanel _body;

    private sealed record Node(string Title, PartDef? Def, IReadOnlyList<CargoItem> Children);

    /// <summary>The laid-out content panel, for the offscreen preview render (<c>--invsmoke</c>).</summary>
    internal Panel PreviewContent => _body;

    public InventoryWindow(Catalog catalog, SpriteCache sprites, string rootDefName, string rootFriendly, IReadOnlyList<CargoItem> rootCargo)
    {
        _catalog = catalog;
        _sprites = sprites;
        _path.Add(new Node(rootFriendly, catalog.Lookup(rootDefName), rootCargo));

        Title = "Contents — " + rootFriendly;
        // Fit the window to its content (the grid + paper-doll) rather than leave a fixed slab of empty space;
        // clamp so a one-item container isn't a sliver and a big crew figure scrolls instead of filling the screen.
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = 300;
        MinHeight = 180;
        MaxWidth = 900;
        MaxHeight = 820;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.WindowBg;

        _body = new StackPanel { Margin = new Thickness(18) };
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _body };
        Render();
    }

    /// <summary>Rebuild the view for the current (deepest) node in the breadcrumb.</summary>
    private void Render()
    {
        _body.Children.Clear();
        var node = _path[^1];

        _body.Children.Add(BuildBreadcrumb());

        var loose = node.Children.Where(c => !c.Slotted).ToList();
        var slotted = node.Children.Where(c => c.Slotted).ToList();
        var hasGrid = node.Def?.IsContainer == true || loose.Count > 0;
        var hasSlots = node.Def?.SlotsWeHave.Length > 0 || slotted.Count > 0;

        _body.Children.Add(new TextBlock
        {
            Text = Summary(node, loose.Count, slotted.Count),
            Foreground = Dim,
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 12),
            TextWrapping = TextWrapping.Wrap,
        });

        if (hasGrid)
        {
            _body.Children.Add(SectionHeader("STORED"));
            _body.Children.Add(BuildGrid(node, loose));
        }

        if (hasSlots)
        {
            _body.Children.Add(SectionHeader("EQUIPPED"));
            _body.Children.Add(BuildPaperDoll(node, slotted));
        }

        if (!hasGrid && !hasSlots)
            _body.Children.Add(new TextBlock { Text = "This item holds nothing.", Foreground = Dim, Margin = new Thickness(0, 4, 0, 0) });
    }

    private static string Summary(Node node, int loose, int slotted)
    {
        var parts = new List<string>();
        if (node.Def?.ContainerGrid is { } g) parts.Add($"{g.W}×{g.H} grid");
        if (loose > 0) parts.Add($"{loose} stored");
        if (slotted > 0) parts.Add($"{slotted} equipped");
        return parts.Count == 0 ? "Empty." : string.Join("  ·  ", parts);
    }

    // ---- breadcrumb ----

    private UIElement BuildBreadcrumb()
    {
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
        for (var i = 0; i < _path.Count; i++)
        {
            if (i > 0) bar.Children.Add(new TextBlock { Text = "  ▸  ", Foreground = Dim, VerticalAlignment = VerticalAlignment.Center });
            var last = i == _path.Count - 1;
            if (last)
            {
                bar.Children.Add(new TextBlock { Text = _path[i].Title, Foreground = Ink, FontWeight = FontWeights.Bold, FontSize = 15 });
            }
            else
            {
                var depth = i;
                var link = new TextBlock
                {
                    Text = _path[i].Title,
                    Foreground = Accent,
                    FontSize = 15,
                    Cursor = Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                link.MouseLeftButtonUp += (_, _) => NavigateTo(depth);
                bar.Children.Add(link);
            }
        }
        return bar;
    }

    private void NavigateTo(int depth)
    {
        if (depth < 0 || depth >= _path.Count - 1) return;
        _path.RemoveRange(depth + 1, _path.Count - depth - 1);
        Render();
    }

    private void DrillInto(CargoItem child)
    {
        _path.Add(new Node(child.Friendly ?? child.DefName, _catalog.Lookup(child.DefName), child.Children));
        Render();
    }

    // ---- grid section ----

    private UIElement BuildGrid(Node node, IReadOnlyList<CargoItem> loose)
    {
        var (gw, gh) = node.Def?.ContainerGrid ?? (6, 6);
        var layout = InventoryGrid.Pack(gw, gh, loose);

        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4) };
        var w = layout.Width * CellPx;
        var h = layout.Height * CellPx;
        grid.Width = w;
        grid.Height = h;

        // backdrop: one faint cell per tile
        var cells = new UniformGrid { Rows = layout.Height, Columns = layout.Width };
        for (var i = 0; i < layout.Width * layout.Height; i++)
            cells.Children.Add(new Border { Background = FieldBg, BorderBrush = PanelBorder, BorderThickness = new Thickness(0.5) });
        grid.Children.Add(cells);

        // items placed absolutely on top
        var canvas = new Canvas();
        foreach (var block in layout.Items)
        {
            var tile = ItemTile(block.Item, block.W * CellPx, block.H * CellPx, block.Count);
            Canvas.SetLeft(tile, block.X * CellPx);
            Canvas.SetTop(tile, block.Y * CellPx);
            canvas.Children.Add(tile);
        }
        grid.Children.Add(canvas);

        return new Border { Child = grid, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
    }

    // ---- paper-doll section ----

    private UIElement BuildPaperDoll(Node node, IReadOnlyList<CargoItem> slotted)
    {
        var bySlot = new Dictionary<string, CargoItem>(StringComparer.Ordinal);
        foreach (var c in slotted)
            if (c.SlotName is { } s) bySlot.TryAdd(s, c);

        // the slots to draw: the host's declared slots, plus any occupied slot not declared (defensive)
        var slots = new List<string>(node.Def?.SlotsWeHave ?? []);
        foreach (var s in bySlot.Keys)
            if (!slots.Contains(s)) slots.Add(s);

        // any slotted child we couldn't map to a named slot — list it so nothing is hidden
        var unplaced = slotted.Where(c => c.SlotName is null).ToList();

        var layout = node.Def?.SlotLayout ?? new Dictionary<string, (double X, double Y)>();
        var useFigure = slots.Count > 0 && slots.All(s => layout.ContainsKey(s));

        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        if (slots.Count > 0)
            panel.Children.Add(useFigure
                ? PaperDollFigure(slots, bySlot, layout)
                : SlotRow(slots, bySlot));

        foreach (var c in unplaced)
            panel.Children.Add(WrapTile(c, "(unslotted)"));

        return panel;
    }

    /// <summary>Slots positioned on a figure from the host's <c>dictSlotsLayout</c> — the game's paper-doll
    /// arrangement (y is inverted: the game's +y is up). Empty slots show their icon faintly.</summary>
    private UIElement PaperDollFigure(List<string> slots, Dictionary<string, CargoItem> bySlot, IReadOnlyDictionary<string, (double X, double Y)> layout)
    {
        var pts = slots.Select(s => layout[s]).ToList();
        // The game's slot icons are smaller than our cells, so its raw offsets (pockets ~20px apart) would overlap
        // 46px cells — scale so the nearest two slots sit about one cell apart.
        var scale = FitScale(pts);
        double minX = pts.Min(p => p.X), minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        const double pad = 6;
        var canvas = new Canvas
        {
            Width = (pts.Max(p => p.X) - minX) * scale + SlotPx + 2 * pad,
            Height = (maxY - minY) * scale + SlotPx + 2 * pad,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        foreach (var slot in slots)
        {
            var (x, y) = layout[slot];
            var cell = bySlot.TryGetValue(slot, out var item) ? SlotCell(item, slot) : EmptySlot(slot);
            Canvas.SetLeft(cell, (x - minX) * scale + pad);
            Canvas.SetTop(cell, (maxY - y) * scale + pad);   // invert: the game's +y is up
            canvas.Children.Add(cell);
        }
        return new Border { Child = canvas, HorizontalAlignment = HorizontalAlignment.Left };
    }

    /// <summary>A uniform scale for raw <c>dictSlotsLayout</c> offsets so the two nearest slots end up about one
    /// cell (plus a gap) apart — the game draws smaller icons than our <see cref="SlotPx"/> cells. 1 when there's
    /// nothing to separate; clamped so a sprawling crew figure doesn't balloon.</summary>
    private static double FitScale(IReadOnlyList<(double X, double Y)> pts)
    {
        var min = double.MaxValue;
        for (var i = 0; i < pts.Count; i++)
            for (var j = i + 1; j < pts.Count; j++)
            {
                double dx = pts[i].X - pts[j].X, dy = pts[i].Y - pts[j].Y;
                var d = Math.Sqrt(dx * dx + dy * dy);
                if (d > 0.5 && d < min) min = d;
            }
        return min is double.MaxValue ? 1.0 : Math.Clamp((SlotPx + 8) / min, 0.4, 3.0);
    }

    /// <summary>Fallback when the host declares no layout: a plain wrap of slot cells.</summary>
    private UIElement SlotRow(List<string> slots, Dictionary<string, CargoItem> bySlot)
    {
        var wrap = new WrapPanel();
        foreach (var slot in slots)
            wrap.Children.Add(bySlot.TryGetValue(slot, out var item) ? SlotCell(item, slot) : EmptySlot(slot));
        return wrap;
    }

    private FrameworkElement SlotCell(CargoItem item, string slot)
    {
        var tile = ItemTile(item, SlotPx, SlotPx, item.Stack);
        tile.ToolTip = $"{SlotFriendly(slot)}: {item.Friendly ?? item.DefName}"
            + (item.Children.Count > 0 ? $"  ({item.SubtreeCount - 1} inside — click to open)" : "");
        return tile;
    }

    private Border EmptySlot(string slot)
    {
        var content = _catalog.Slots.GetValueOrDefault(slot)?.IconImg is { } icon
            && _catalog.Index?.ResolveImage(icon) is { } abs && SafeLoad(abs) is { } bmp
                ? PixelImage(bmp)
                : (UIElement)new TextBlock { Text = "·", Foreground = Dim, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        return new Border
        {
            Width = SlotPx,
            Height = SlotPx,
            Margin = new Thickness(2),
            Background = FieldBg,
            BorderBrush = PanelBorder,
            BorderThickness = new Thickness(1),
            Opacity = 0.55,
            Child = content,
            ToolTip = SlotFriendly(slot) + " (empty)",
        };
    }

    private string SlotFriendly(string slot) => _catalog.Slots.GetValueOrDefault(slot)?.Friendly ?? slot;

    // ---- one item tile (shared by grid + slots) ----

    private Border ItemTile(CargoItem item, double w, double h, int count)
    {
        // a stack's "children" are copies of itself, not cargo — show ×N but don't let you open it
        var drillable = !item.IsStack && item.Children.Count > 0;
        var img = PixelImage(Bmp(item.DefName));

        var overlay = new Grid();
        overlay.Children.Add(img);

        if (count > 1)
            overlay.Children.Add(Badge("×" + count, HorizontalAlignment.Right, VerticalAlignment.Bottom, Accent));
        if (drillable)
            overlay.Children.Add(Badge("⊞", HorizontalAlignment.Left, VerticalAlignment.Top, ThemeManager.Good));

        var border = new Border
        {
            Width = w,
            Height = h,
            Background = FieldBg,
            BorderBrush = drillable ? Accent : PanelBorder,
            BorderThickness = new Thickness(drillable ? 1.5 : 1),
            Child = overlay,
            ToolTip = (item.Friendly ?? item.DefName)
                + (count > 1 ? $"  ×{count}" : "")
                + (drillable ? $"  ({item.SubtreeCount - 1} inside — click to open)" : ""),
        };
        if (drillable)
        {
            border.Cursor = Cursors.Hand;
            border.MouseLeftButtonUp += (_, _) => DrillInto(item);
        }
        return border;
    }

    private FrameworkElement WrapTile(CargoItem item, string note)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        row.Children.Add(ItemTile(item, SlotPx, SlotPx, item.Stack));
        row.Children.Add(new TextBlock
        {
            Text = $"{item.Friendly ?? item.DefName}  {note}",
            Foreground = Dim,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        });
        return row;
    }

    private static Border Badge(string text, HorizontalAlignment ha, VerticalAlignment va, Brush bg) => new()
    {
        Background = bg,
        CornerRadius = new CornerRadius(2),
        Padding = new Thickness(3, 0, 3, 0),
        Margin = new Thickness(1),
        HorizontalAlignment = ha,
        VerticalAlignment = va,
        Child = new TextBlock { Text = text, Foreground = ThemeManager.AccentText, FontSize = 11, FontWeight = FontWeights.Bold },
    };

    // ---- sprites ----

    private BitmapSource Bmp(string defName) =>
        _catalog.Lookup(defName) is { } part ? _sprites.Thumb(part) : _sprites.Missing;

    private static Image PixelImage(BitmapSource bmp)
    {
        var img = new Image { Source = bmp, Stretch = Stretch.Uniform, Margin = new Thickness(3) };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        return img;
    }

    private BitmapSource? SafeLoad(string abs)
    {
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.UriSource = new Uri(abs, UriKind.Absolute);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    private static TextBlock SectionHeader(string text) => new()
    {
        Text = text, Foreground = Dim, FontWeight = FontWeights.Bold, FontSize = 11, Margin = new Thickness(0, 6, 0, 6),
    };
}
