using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// The inventory viewer/editor: shows what a placed container holds, mirroring the in-game inventory window rather
/// than a flat list. Loose cargo is laid out on the container's tile grid (each item occupying its footprint at
/// its packed cell, stacks collapsed with a count); equipped gear is shown on a paper-doll positioned from the
/// host's <c>dictSlotsLayout</c>. Nested containers and worn items are drill-in-able, with a breadcrumb to climb
/// back out.
///
/// <para>When opened with an edit context (a <see cref="ShipDocument"/> + <see cref="CommandStack"/> + the root
/// <see cref="Placement"/>) it also <b>edits</b> loose cargo: "Add item" offers only what the container accepts
/// (<see cref="ContainerFilter"/>) and drops it into the first free cell (blocked when the grid is full — "the
/// Law" for cargo), and a right-click removes one or the whole stack. Every edit goes through the command stack
/// (undo/redo) and rebuilds the container's <see cref="Placement.Cargo"/>. Equipped-gear slots stay read-only.</para>
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
    private readonly string _rootDefName;
    private readonly IReadOnlyList<CargoItem> _staticCargo;   // read-only fallback when not editing

    private readonly ShipDocument? _doc;
    private readonly CommandStack? _stack;
    private readonly Placement? _root;

    private readonly List<Crumb> _path = [];   // breadcrumb: root → current, by container id (null = root)
    private readonly StackPanel _body;
    private string? _selectedId;   // grid tile selected for the Delete key (editing only)

    private sealed record Crumb(string Title, string? ContainerId);

    private bool Editing => _doc is not null && _stack is not null && _root is not null;

    /// <summary>The container's contents to render — live off the placement when editing (so edits are reflected),
    /// else the static snapshot passed in.</summary>
    private IReadOnlyList<CargoItem> RootCargo => Editing ? _root!.Cargo : _staticCargo;

    /// <summary>The laid-out content panel, for the offscreen preview render (<c>--invsmoke</c>).</summary>
    internal Panel PreviewContent => _body;

    public InventoryWindow(
        Catalog catalog, SpriteCache sprites, string rootDefName, string rootFriendly, IReadOnlyList<CargoItem> rootCargo,
        ShipDocument? doc = null, CommandStack? stack = null, Placement? root = null)
    {
        _catalog = catalog;
        _sprites = sprites;
        _rootDefName = rootDefName;
        _staticCargo = rootCargo;
        _doc = doc;
        _stack = stack;
        _root = root;
        _path.Add(new Crumb(rootFriendly, null));

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

        if (Editing) PreviewKeyDown += OnKeyDown;

        _body = new StackPanel { Margin = new Thickness(18) };
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _body };
        Render();
    }

    // ---- current node resolution (live against the mutable tree) ----

    /// <summary>Resolve the deepest breadcrumb node's def + children from the live cargo tree; null when a drilled
    /// container no longer exists (an edit/undo removed it) — <see cref="Render"/> then pops back to it.</summary>
    private (PartDef? Def, IReadOnlyList<CargoItem> Children)? Resolve()
    {
        IReadOnlyList<CargoItem> children = RootCargo;
        var def = _catalog.Lookup(_rootDefName);
        for (var i = 1; i < _path.Count; i++)
        {
            var node = children.FirstOrDefault(c => c.StrID == _path[i].ContainerId);
            if (node is null) return null;
            children = node.Children;
            def = _catalog.Lookup(node.DefName);
        }
        return (def, children);
    }

    // ---- rendering ----

    /// <summary>Rebuild the view for the current (deepest) node in the breadcrumb.</summary>
    private void Render()
    {
        while (_path.Count > 1 && Resolve() is null) _path.RemoveAt(_path.Count - 1);   // drilled container gone
        _body.Children.Clear();
        if (Resolve() is not { } cur) return;
        var (def, children) = cur;
        var containerId = _path[^1].ContainerId;

        _body.Children.Add(BuildBreadcrumb());

        var loose = children.Where(c => !c.Slotted).ToList();
        var slotted = children.Where(c => c.Slotted).ToList();
        var hasGrid = def?.IsContainer == true || loose.Count > 0;
        var hasSlots = def?.SlotsWeHave.Length > 0 || slotted.Count > 0;

        _body.Children.Add(new TextBlock
        {
            Text = Summary(def, loose.Count, slotted.Count),
            Foreground = Dim,
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 12),
            TextWrapping = TextWrapping.Wrap,
        });

        if (hasGrid)
        {
            var header = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 6, 0, 6) };
            var title = SectionHeader("STORED");
            title.Margin = new Thickness(0);
            DockPanel.SetDock(title, Dock.Left);
            header.Children.Add(title);
            if (Editing && def?.IsContainer == true)
            {
                var add = new Button { Content = "+ Add item…", Padding = new Thickness(8, 1, 8, 1), FontSize = 12, Cursor = Cursors.Hand };
                add.Click += (_, _) => AddItem(def, containerId);
                DockPanel.SetDock(add, Dock.Right);
                header.Children.Add(add);
            }
            _body.Children.Add(header);
            _body.Children.Add(BuildGrid(def, loose));
        }

        if (hasSlots)
        {
            _body.Children.Add(SectionHeader("EQUIPPED"));
            _body.Children.Add(BuildPaperDoll(def, slotted));
        }

        if (!hasGrid && !hasSlots)
            _body.Children.Add(new TextBlock { Text = "This item holds nothing.", Foreground = Dim, Margin = new Thickness(0, 4, 0, 0) });
    }

    private static string Summary(PartDef? def, int loose, int slotted)
    {
        var parts = new List<string>();
        if (def?.ContainerGrid is { } g) parts.Add($"{g.W}×{g.H} grid");
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
        _selectedId = null;
        Render();
    }

    private void DrillInto(CargoItem child)
    {
        _path.Add(new Crumb(child.Friendly ?? child.DefName, child.StrID));
        _selectedId = null;
        Render();
    }

    // ---- grid section ----

    private UIElement BuildGrid(PartDef? def, IReadOnlyList<CargoItem> loose)
    {
        var (gw, gh) = def?.ContainerGrid ?? (6, 6);
        var layout = InventoryGrid.Pack(gw, gh, loose);

        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4) };
        grid.Width = layout.Width * CellPx;
        grid.Height = layout.Height * CellPx;

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

    private UIElement BuildPaperDoll(PartDef? def, IReadOnlyList<CargoItem> slotted)
    {
        var bySlot = new Dictionary<string, CargoItem>(StringComparer.Ordinal);
        foreach (var c in slotted)
            if (c.SlotName is { } s) bySlot.TryAdd(s, c);

        // the slots to draw: the host's declared slots, plus any occupied slot not declared (defensive)
        var slots = new List<string>(def?.SlotsWeHave ?? []);
        foreach (var s in bySlot.Keys)
            if (!slots.Contains(s)) slots.Add(s);

        // any slotted child we couldn't map to a named slot — list it so nothing is hidden
        var unplaced = slotted.Where(c => c.SlotName is null).ToList();

        var layout = def?.SlotLayout ?? new Dictionary<string, (double X, double Y)>();
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
        // a stack's "children" are copies of itself, not cargo — show ×N but don't let you open it. When editing, a
        // container is drillable even when empty (so you can drill in and fill it).
        var isContainer = _catalog.Lookup(item.DefName)?.IsContainer == true;
        var drillable = !item.IsStack && (item.Children.Count > 0 || (Editing && !item.Slotted && isContainer));
        var img = PixelImage(Bmp(item.DefName));

        var overlay = new Grid();
        overlay.Children.Add(img);

        if (count > 1)
            overlay.Children.Add(Badge("×" + count, HorizontalAlignment.Right, VerticalAlignment.Bottom, Accent));
        if (drillable)
            overlay.Children.Add(Badge("⊞", HorizontalAlignment.Left, VerticalAlignment.Top, ThemeManager.Good));

        var selected = Editing && !item.Slotted && item.StrID == _selectedId;
        var border = new Border
        {
            Width = w,
            Height = h,
            Background = FieldBg,
            BorderBrush = selected ? ThemeManager.Warn : drillable ? Accent : PanelBorder,
            BorderThickness = new Thickness(selected ? 2 : drillable ? 1.5 : 1),
            Child = overlay,
            ToolTip = (item.Friendly ?? item.DefName)
                + (count > 1 ? $"  ×{count}" : "")
                + (drillable ? $"  ({item.SubtreeCount - 1} inside — click to open)" : "")
                + (Editing && !item.Slotted ? "  · right-click to remove" : ""),
        };
        if (drillable)
        {
            border.Cursor = Cursors.Hand;
            border.MouseLeftButtonUp += (_, _) => DrillInto(item);
        }
        else if (Editing && !item.Slotted)
        {
            border.Cursor = Cursors.Hand;
            border.MouseLeftButtonUp += (_, _) => { _selectedId = item.StrID; Render(); };
        }
        if (Editing && !item.Slotted)
            border.ContextMenu = RemoveMenu(item);
        return border;
    }

    /// <summary>Right-click menu for a grid tile when editing: remove one, remove the whole stack, and (for a
    /// container) open it.</summary>
    private ContextMenu RemoveMenu(CargoItem item)
    {
        var menu = new ContextMenu();
        if (item.IsStack && item.Stack > 1)
        {
            menu.Items.Add(MenuItem("Remove one", () => Commit(CargoEdit.RemoveOne(_root!.Cargo, item.StrID))));
            menu.Items.Add(MenuItem($"Remove all ×{item.Stack}", () => Commit(CargoEdit.RemoveWhole(_root!.Cargo, item.StrID))));
        }
        else
        {
            var label = item.Children.Count > 0 ? "Remove (with contents)" : "Remove";
            menu.Items.Add(MenuItem(label, () => Commit(CargoEdit.RemoveWhole(_root!.Cargo, item.StrID))));
        }
        return menu;
    }

    private static MenuItem MenuItem(string header, Action act)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => act();
        return mi;
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

    // ---- editing ----

    /// <summary>Add items to the current container: pick from what it accepts, choose a quantity, place into free
    /// cells (blocked when full). Routed through the command stack for undo/redo.</summary>
    private void AddItem(PartDef container, string? containerId)
    {
        var offered = ContainerFilter.AcceptedBy(_catalog, container).Where(i => i.SpriteAbs is not null).ToList();
        if (offered.Count == 0)
        {
            MessageBox.Show(this, "This container accepts nothing that Ostraplan can place.", "Add item",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new AddCargoDialog(_catalog, _sprites, container, offered) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Chosen is not { } pick) return;

        var grid = container.ContainerGrid ?? (6, 6);
        var updated = CargoEdit.Add(_root!.Cargo, containerId, grid, pick.Def, pick.Quantity);
        if (updated is null)
        {
            MessageBox.Show(this,
                $"Not enough room in this container for {pick.Quantity} × {pick.Def.Friendly}.",
                "Won't fit", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Commit(updated);
    }

    /// <summary>Apply a rebuilt cargo tree for the root placement through the command stack (undoable), then
    /// re-render off the now-current tree.</summary>
    private void Commit(IReadOnlyList<CargoItem> newRootCargo)
    {
        _stack!.Push(_doc!, new SetCargoCommand(_root!, _root!.Cargo, newRootCargo));
        _selectedId = null;
        Render();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!Editing) return;
        if (e.Key == Key.Delete && _selectedId is { } id)
        {
            Commit(CargoEdit.RemoveOne(_root!.Cargo, id));
            e.Handled = true;
        }
        else if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            _stack!.Undo(_doc!);
            _selectedId = null;
            Render();
            e.Handled = true;
        }
        else if (e.Key == Key.Y && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            _stack!.Redo(_doc!);
            _selectedId = null;
            Render();
            e.Handled = true;
        }
    }

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
