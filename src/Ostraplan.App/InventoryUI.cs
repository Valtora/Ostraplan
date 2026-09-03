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
/// <para><b>A slotted container is drawn with its host, not behind it.</b> A backpack's four pouches sit in a row
/// under its own 4×4 and an EVA suit's compartments across its front, contents and all, positioned from the same
/// <c>dictSlotsLayout</c> and recursing as far as the nesting goes. That is the game's own arrangement rather than
/// an approximation of it: see <see cref="InventoryLayout"/> for the rule and the geometry. Before it, everything
/// a suit held was one level down in a compartment the view would not draw, so a fully stocked Bingham-12 opened
/// on four empty boxes (#59).</para>
///
/// <para>Every grid on screen is live. A drop lands in whichever one the cursor is over, subject to that
/// container's own filter, so an item drags straight from a backpack into one of its pouches. A pouch is still
/// <b>drillable</b> as well: reaching it through the breadcrumb, or clicking it where it is drawn, makes it the
/// <b>active</b> grid, which outlines it and points the summary and <b>Add item</b> at it while its host stays on
/// screen around it.</para>
///
/// <para>When opened with an edit context (a <see cref="ShipDocument"/> + <see cref="CommandStack"/> + the root
/// <see cref="Placement"/>) it also <b>edits</b> loose cargo, undoably: "Add item" offers only what the container
/// accepts (<see cref="ContainerFilter"/>) into the first free cell, turning an item on its side when it no longer
/// fits upright and blocking only when neither orientation does ("the Law"); right-click removes one or the whole
/// stack; and items can be <b>dragged</b> to a new cell, onto a container tile (to move inside), or onto the
/// breadcrumb (to move out), and moved out via a right-click menu. Every edit rebuilds the container's
/// <see cref="Placement.Cargo"/>. Equipped-gear slots stay read-only.</para>
///
/// <para><b>R rotates.</b> During a drag it turns the item in hand: the ghost re-draws at the new footprint and
/// nothing is committed until the drop, which is the game's own model (<c>GUIInventory.RotateCWSelected</c> swaps
/// the footprint with no fit check and leaves validity to the drop). With nothing in hand it turns the selected
/// item where it sits, about its centre. A drag also draws its landing footprint on the grid, green when the drop
/// is legal and red when it is not, so a refusal is visible before the button comes up rather than as a snap-back
/// with no explanation.</para>
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
    // The thing being edited: an installed part, or an item lying on the deck. Exactly one is set when editing.
    private readonly Placement? _root;
    private readonly LooseObject? _rootLoose;

    private readonly List<Crumb> _path = [];   // breadcrumb: root → current, by container id (null = root)
    private readonly StackPanel _body;
    private readonly Canvas _overlay;           // ghost layer for dragging
    private string? _selectedId;                // grid tile selected for the Delete / R keys (editing only)

    // per-render drop-target state (rebuilt each Render)
    private readonly List<GridSurface> _surfaces = [];
    private (int W, int H) _currentGrid = (6, 6);
    private readonly List<(FrameworkElement El, string? ContainerId)> _crumbTargets = [];
    private int _figureRoot;   // the breadcrumb depth the figure is drawn from, which is not always the deepest

    /// <summary>One grid drawn in the figure, with everything a drop onto it needs. There is more than one
    /// whenever a slotted sub-container is shown with its host (a backpack's pouches), so the drag resolves which
    /// grid the cursor is over rather than assuming the only one.</summary>
    private sealed record GridSurface(
        string? ContainerId, PartDef? Def, (int W, int H) Grid, Canvas Canvas, GridLayoutResult Layout, Border Frame);

    // drag state. A drag carries its own rotation so R turns the item in hand and the drop commits pose and
    // rotation as one edit — the game's model, where rotation is done to a picked-up item and the drop is what
    // validates it (GUIInventory.RotateCWSelected has no fit check of its own).
    private CargoItem? _dragItem;
    private bool _dragging;
    private Point _dragStart;
    private int _dragRot;
    private Image? _ghost;
    private Border? _dropHint;   // the landing footprint drawn on the grid, green when it fits and red when it doesn't

    private sealed record Crumb(string Title, string? ContainerId);

    private bool Editing => _doc is not null && _stack is not null && (_root is not null || _rootLoose is not null);

    /// <summary>The contents being edited, off whichever host this window was opened on.</summary>
    private IReadOnlyList<CargoItem> HostCargo => _root is not null ? _root.Cargo : _rootLoose!.Cargo;

    /// <summary>The container's contents to render — live off the host when editing (so edits are reflected),
    /// else the static snapshot passed in.</summary>
    private IReadOnlyList<CargoItem> RootCargo => Editing ? HostCargo : _staticCargo;

    /// <summary>The laid-out content panel, for the offscreen preview render (<c>--invsmoke</c>).</summary>
    internal Panel PreviewContent => _body;

    public InventoryWindow(
        Catalog catalog, SpriteCache sprites, string rootDefName, string rootFriendly, IReadOnlyList<CargoItem> rootCargo,
        ShipDocument? doc = null, CommandStack? stack = null, Placement? root = null, LooseObject? rootLoose = null)
    {
        _catalog = catalog;
        _sprites = sprites;
        _rootDefName = rootDefName;
        _staticCargo = rootCargo;
        _doc = doc;
        _stack = stack;
        _root = root;
        _rootLoose = rootLoose;
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

        _body = new StackPanel { Margin = new Thickness(18) };
        _overlay = new Canvas { IsHitTestVisible = false };
        var host = new Grid();
        host.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _body });
        host.Children.Add(_overlay);
        Content = host;

        if (Editing)
        {
            PreviewKeyDown += OnKeyDown;
            PreviewMouseMove += OnDragMove;
            PreviewMouseLeftButtonUp += OnDragUp;
            LostMouseCapture += (_, _) => EndDrag();   // capture stolen (an alt-tab, a menu): abandon the drag
        }
        Render();
    }

    // ---- current node resolution (live against the mutable tree) ----

    /// <summary>One breadcrumb level resolved against the live cargo tree.</summary>
    private sealed record Level(string DefName, PartDef? Def, CargoItem? Node, IReadOnlyList<CargoItem> Children);

    /// <summary>Resolve every breadcrumb level from the live cargo tree, root first; null when a drilled container
    /// no longer exists (an edit or undo removed it) — <see cref="Render"/> then pops back to it. The whole chain
    /// rather than just the tail, because the figure is drawn from the deepest crumb that is not itself shown
    /// inside its parent (see <see cref="FigureRoot"/>).</summary>
    private List<Level>? Chain()
    {
        var levels = new List<Level> { new(_rootDefName, _catalog.Lookup(_rootDefName), null, RootCargo) };
        for (var i = 1; i < _path.Count; i++)
        {
            var node = levels[^1].Children.FirstOrDefault(c => c.StrID == _path[i].ContainerId);
            if (node is null) return null;
            levels.Add(new Level(node.DefName, _catalog.Lookup(node.DefName), node, node.Children));
        }
        return levels;
    }

    /// <summary>
    /// The level the figure is drawn from: the deepest crumb whose grid is not already on screen as part of its
    /// parent's figure.
    ///
    /// <para>Drilling into a backpack pouch does not replace the view with the pouch, because the pouch is drawn
    /// with the backpack. It makes the pouch the <b>active</b> grid instead, which highlights it and points
    /// <b>Add item</b> at it, and the backpack stays on screen around it. That is what keeps both routes to a
    /// pocket working: it is reachable inline and through the breadcrumb, and the breadcrumb still says where you
    /// are (#59).</para>
    /// </summary>
    private static int FigureRoot(List<Level> chain, Catalog catalog)
    {
        var i = chain.Count - 1;
        while (i > 0 && InventoryLayout.ShowsWithHost(
                   catalog, chain[i - 1].Def, chain[i].Def, chain[i].Node?.SlotName)) i--;
        return i;
    }

    /// <summary>The direct contents of a container anywhere in the live tree (null = the window's own host).</summary>
    private IReadOnlyList<CargoItem> ChildrenOf(string? containerId) =>
        containerId is null ? RootCargo : FindNode(RootCargo, containerId)?.Children ?? [];

    /// <summary>What to call a cargo item on screen: the name the user gave it, else the def's own, else the raw
    /// def name. The one place that decision is made, so a renamed pouch reads the same on its tile, its tooltip,
    /// its breadcrumb and the messages about it (#37).</summary>
    private static string Label(CargoItem item) => item.CustomName ?? item.Friendly ?? item.DefName;

    /// <summary>Find a cargo node anywhere in the live tree by id.</summary>
    private static CargoItem? FindNode(IReadOnlyList<CargoItem> items, string id)
    {
        foreach (var it in items)
        {
            if (it.StrID == id) return it;
            if (FindNode(it.Children, id) is { } found) return found;
        }
        return null;
    }

    // ---- rendering ----

    /// <summary>Rebuild the view for the current (deepest) node in the breadcrumb.</summary>
    private void Render()
    {
        while (_path.Count > 1 && Chain() is null) _path.RemoveAt(_path.Count - 1);   // drilled container gone
        _body.Children.Clear();
        _crumbTargets.Clear();
        _surfaces.Clear();
        if (Chain() is not { } chain) return;

        // The ACTIVE level is the deepest crumb: what the summary describes, what "Add item" fills, and what the
        // keyboard's rotate and delete act in. The FIGURE is drawn from the deepest crumb that its parent does not
        // already draw, so drilling into a pouch highlights it without taking the backpack off screen.
        var active = chain[^1];
        var activeId = _path[^1].ContainerId;
        _currentGrid = active.Def?.ContainerGrid ?? (6, 6);

        _figureRoot = FigureRoot(chain, _catalog);
        var figure = InventoryLayout.Compose(
            _catalog, chain[_figureRoot].DefName, _path[_figureRoot].ContainerId, chain[_figureRoot].Children);

        _body.Children.Add(BuildBreadcrumb());

        TextBlock? hint = null;
        var loose = active.Children.Where(c => !c.Slotted).ToList();
        var slotted = active.Children.Where(c => c.Slotted).ToList();
        var hasGrid = active.Def?.IsContainer == true || loose.Count > 0;
        var hasSlots = active.Def?.SlotsWeHave.Length > 0 || slotted.Count > 0;

        _body.Children.Add(new TextBlock
        {
            // Name the active container once there is more than one grid on screen, or the summary reads as though
            // it described the whole figure and "Add item" looks like it could go anywhere.
            Text = (_figureRoot < _path.Count - 1 ? _path[^1].Title + "  ·  " : "")
                   + Summary(active.Def, loose.Count, slotted.Count),
            Foreground = Dim,
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 12),
            TextWrapping = TextWrapping.Wrap,
        });

        if (hasGrid || hasSlots)
        {
            var header = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 6, 0, 6) };
            var title = SectionHeader(hasGrid ? "STORED" : "EQUIPPED");
            title.Margin = new Thickness(0);
            DockPanel.SetDock(title, Dock.Left);
            header.Children.Add(title);
            if (Editing && active.Def?.IsContainer == true)
            {
                var add = new Button { Content = "+ Add item…", Padding = new Thickness(8, 1, 8, 1), FontSize = 12, Cursor = Cursors.Hand };
                var def = active.Def;
                add.Click += (_, _) => AddItem(def, activeId);
                DockPanel.SetDock(add, Dock.Right);
                header.Children.Add(add);
            }
            _body.Children.Add(header);
            _body.Children.Add(BuildFigure(figure));
            MarkActiveSurface(activeId);
            if (Editing && hasGrid)
            {
                hint = new TextBlock
                {
                    // "A name at the top" is the breadcrumb, so only claim it when an ancestor is actually up there
                    // to drop onto. The right-click "Move to" menu is gated on the same depth for the same reason.
                    Text = "Drag to move (R turns it) · into a container to nest it"
                           + (_surfaces.Count > 1 ? " · onto another grid to move it there" : "")
                           + (_path.Count > 1 ? " · onto a name at the top to move it out" : "")
                           + " · Alt+click for info · right-click to rename or remove"
                           + " · Del takes one, Shift+Del the whole stack"
                           + " · click the title to name this container",
                    Foreground = Dim, FontSize = 11, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap,
                    // A stretched element narrower than its slot is centred by WPF, and FitHintToContent gives
                    // this one a MaxWidth, so without this the prose drifts into the middle of a wider window.
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                _body.Children.Add(hint);
            }
        }
        else
            _body.Children.Add(new TextBlock { Text = "This item holds nothing.", Foreground = Dim, Margin = new Thickness(0, 4, 0, 0) });

        FitHintToContent(hint);
    }

    /// <summary>The narrowest the drag hint is allowed to wrap to. Below this it becomes a column of single words
    /// under a one-slot container, which is worse than a window slightly wider than its contents.</summary>
    private const double HintMinWidth = 320;

    /// <summary>
    /// Hold the drag hint to the width the real content wants.
    ///
    /// <para>A wrapping <see cref="TextBlock"/> in a panel measured at infinite width reports its whole
    /// <b>unwrapped</b> length as its desired width — it only wraps once something constrains it, and nothing here
    /// does. Under <see cref="SizeToContent"/> that length is what the window becomes, so one line of prose was
    /// deciding the size of a window that exists to show a grid: a 1×3 rack with two items in it opened at the
    /// 900px cap, which is how it was reported ("why does the content view have to be so wide on the root of the
    /// object... this looks ridiculous").</para>
    ///
    /// <para>So measure the content with the prose out of the way, then let the prose wrap to whatever that came
    /// to. A big paper doll still gets a wide window and the hint still runs across it on one line; a small
    /// container gets a small window and the hint wraps to three.</para>
    /// </summary>
    private void FitHintToContent(TextBlock? hint)
    {
        if (hint is null) return;
        hint.Visibility = Visibility.Collapsed;
        _body.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var content = _body.DesiredSize.Width - _body.Margin.Left - _body.Margin.Right;
        hint.Visibility = Visibility.Visible;
        hint.MaxWidth = Math.Max(content, HintMinWidth);
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
                // The deepest crumb is the container you are looking inside, and naming it here is the obvious
                // move: it is the one place its name is already on screen (#37). Click to rename when editing.
                var head = new TextBlock
                {
                    Text = _path[i].Title, Foreground = Ink, FontWeight = FontWeights.Bold, FontSize = 15,
                };
                if (Editing)
                {
                    head.Cursor = Cursors.Hand;
                    head.ToolTip = "Click to rename this container";
                    head.MouseLeftButtonUp += (_, _) => { if (!_dragging) RenameCurrent(); };
                }
                bar.Children.Add(head);
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
                link.MouseLeftButtonUp += (_, _) => { if (!_dragging) NavigateTo(depth); };
                bar.Children.Add(link);
                if (Editing) _crumbTargets.Add((link, _path[i].ContainerId));   // an ancestor you can drop onto to move out
            }
        }
        return bar;
    }

    /// <summary>
    /// Rename whatever the deepest breadcrumb points at.
    ///
    /// <para>Two different things wear that crumb and they are named through different commands: at the root it is
    /// the <b>host</b> the window was opened on, a placed part or a deck item, whose name lives on the object
    /// itself; deeper in it is a nested <see cref="CargoItem"/>, whose name lives in the host's cargo tree. Both
    /// are one undo step, and both land in the same place in the game.</para>
    /// </summary>
    private void RenameCurrent()
    {
        if (!Editing || _doc is null) return;

        if (_path[^1].ContainerId is { } nestedId)
        {
            if (FindNode(HostCargo, nestedId) is { } nested) RenameItem(nested);
            return;
        }

        // The root: the placement or deck item this window belongs to.
        var hostDef = _catalog.Lookup(_rootDefName);
        var current = _root?.CustomName ?? _rootLoose?.CustomName;
        var dlg = new RenameDialog(hostDef?.Friendly ?? _rootDefName, current, _root is not null ? "part" : "item")
        {
            Owner = this,
        };
        if (dlg.ShowDialog() != true) return;
        var chosen = Rename.Typed(dlg.ChosenName, hostDef);
        if (chosen == current) return;

        _stack!.Push(_doc, _root is not null
            ? new SetCustomNameCommand(_root, _root.CustomName, chosen)
            : new SetLooseCustomNameCommand(_rootLoose!, _rootLoose!.CustomName, chosen));

        // The crumb IS the title, so both have to move with it.
        _path[0] = _path[0] with { Title = chosen ?? hostDef?.Friendly ?? _rootDefName };
        Title = "Contents — " + _path[0].Title;
        Render();
    }

    private void NavigateTo(int depth)
    {
        if (depth < 0 || depth >= _path.Count - 1) return;
        _path.RemoveRange(depth + 1, _path.Count - depth - 1);
        _selectedId = null;
        Render();
    }

    /// <summary>
    /// Point the breadcrumb at a container anywhere in the tree, rebuilding the whole path down to it.
    ///
    /// <para>The path is rebuilt rather than appended to because the thing clicked is no longer always a child of
    /// the deepest crumb. A pouch is drawn on its host, so what you click may be two levels below where the
    /// breadcrumb currently stands, and appending one crumb would leave a path that resolves to nothing.</para>
    /// </summary>
    private void NavigateToContainer(string? containerId)
    {
        List<CargoItem> trail = [];
        if (containerId is { } id)
        {
            if (TrailTo(RootCargo, id) is not { } found) return;   // gone from under us
            trail = found;
        }

        _path.RemoveRange(1, _path.Count - 1);   // null means the window's own host, which is crumb 0
        foreach (var node in trail) _path.Add(new Crumb(Label(node), node.StrID));
        _selectedId = null;
        Render();
    }

    /// <summary>The cargo nodes from the window's root down to <paramref name="id"/>, or null when it is not in
    /// the tree.</summary>
    private static List<CargoItem>? TrailTo(IReadOnlyList<CargoItem> items, string id)
    {
        foreach (var it in items)
        {
            if (it.StrID == id) return [it];
            if (TrailTo(it.Children, id) is { } deeper) return [it, .. deeper];
        }
        return null;
    }

    private void DrillInto(CargoItem child) => NavigateToContainer(child.StrID);

    // ---- the figure: the host's grid, and every grid drawn with it ----

    /// <summary>
    /// Build one panel's figure: its own grid, a sub-figure for every slotted container the game draws with it,
    /// and a slot cell for everything else, each positioned in cell space from the host's <c>dictSlotsLayout</c>.
    /// Anything the host declares no position for is flowed underneath instead, which is the game's untethered
    /// window (see <see cref="InventoryLayout"/>).
    ///
    /// <para>This is what puts a backpack's four pouches in a row under its 4×4 and an EVA suit's compartments
    /// across its front, contents and all, instead of four empty boxes to be drilled into one at a time.</para>
    /// </summary>
    private FrameworkElement BuildFigure(InventoryPanel panel)
    {
        var def = _catalog.Lookup(panel.DefName);
        var contents = ChildrenOf(panel.ContainerId);
        var loose = contents.Where(c => !c.Slotted).ToList();
        var slotted = contents.Where(c => c.Slotted).ToList();

        var bySlot = new Dictionary<string, CargoItem>(StringComparer.Ordinal);
        foreach (var c in slotted)
            if (c.SlotName is { } s) bySlot.TryAdd(s, c);
        var byPanel = panel.Children
            .Where(p => p.SlotName is not null)
            .ToDictionary(p => p.SlotName!, StringComparer.Ordinal);

        var pinned = new List<(FrameworkElement El, Point At)>();
        var flowed = new List<FrameworkElement>();

        // Measure on the way in: a Canvas has no layout of its own, so its size is the union of what it holds and
        // that has to be known before anything is placed on it.
        void Place(FrameworkElement el, (double X, double Y)? cells)
        {
            el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (cells is { } c) pinned.Add((el, new Point(c.X * CellPx, c.Y * CellPx)));
            else flowed.Add(el);
        }

        if (def?.IsContainer == true || loose.Count > 0)
            Place(BuildGridSurface(panel, def, loose), panel.SelfOffset);

        // the slots to draw: the host's declared slots, plus any occupied slot not declared (defensive)
        var slots = new List<string>(def?.SlotsWeHave ?? []);
        foreach (var s in bySlot.Keys)
            if (!slots.Contains(s)) slots.Add(s);
        foreach (var slot in slots)
        {
            var el = byPanel.TryGetValue(slot, out var sub) ? BuildFigure(sub)
                : bySlot.TryGetValue(slot, out var item) ? SlotCell(item, slot)
                : EmptySlot(slot);
            Place(el, def is not null && def.SlotLayout.TryGetValue(slot, out var pt)
                ? InventoryLayout.ToCells(pt)
                : null);
        }

        // any slotted child we couldn't map to a named slot — flowed so nothing is hidden
        foreach (var c in slotted.Where(c => c.SlotName is null))
            flowed.Add(WrapTile(c, "(unslotted)"));

        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
        if (pinned.Count > 0) stack.Children.Add(PinnedCanvas(pinned));
        if (flowed.Count > 0)
        {
            var wrap = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var el in flowed) wrap.Children.Add(el);
            stack.Children.Add(wrap);
        }
        return stack;
    }

    /// <summary>Lay the positioned elements on a canvas sized to hold them, shifted so the leftmost and topmost sit
    /// at the origin. The shift earns its place on a backpack, whose pouch row starts a fraction of a cell left of
    /// its own grid because that grid is pushed right by <c>self</c>.</summary>
    private static Canvas PinnedCanvas(List<(FrameworkElement El, Point At)> pinned)
    {
        var minX = pinned.Min(p => p.At.X);
        var minY = pinned.Min(p => p.At.Y);
        var canvas = new Canvas
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = pinned.Max(p => p.At.X - minX + p.El.DesiredSize.Width),
            Height = pinned.Max(p => p.At.Y - minY + p.El.DesiredSize.Height),
        };
        foreach (var (el, at) in pinned)
        {
            Canvas.SetLeft(el, at.X - minX);
            Canvas.SetTop(el, at.Y - minY);
            canvas.Children.Add(el);
        }
        return canvas;
    }

    /// <summary>One container's grid: its packed contents over a tile backdrop, registered as a drop target.
    /// Clicking the backdrop makes it the active grid, which is the other half of a pouch staying drillable — the
    /// breadcrumb reaches it, and so does clicking it where it is drawn.</summary>
    private FrameworkElement BuildGridSurface(InventoryPanel panel, PartDef? def, IReadOnlyList<CargoItem> loose)
    {
        var (gw, gh) = def?.ContainerGrid ?? (6, 6);
        var layout = InventoryGrid.Pack(gw, gh, loose);

        var grid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = layout.Width * CellPx,
            Height = layout.Height * CellPx,
        };

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

        // The backdrop sits UNDER the item canvas, so this fires only on a click that missed every tile. A click on
        // an item belongs to the item and reaches OnDragUp instead.
        var containerId = panel.ContainerId;
        cells.MouseLeftButtonUp += (_, _) => { if (!_dragging) ActivateContainer(containerId); };

        var frame = new Border
        {
            Child = grid,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(2),
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
        };
        _surfaces.Add(new GridSurface(containerId, def, (gw, gh), canvas, layout, frame));
        return frame;
    }

    /// <summary>Outline the active grid, but only once there is a second one to tell it apart from. On a plain
    /// container the outline would be decoration around the only thing on screen.</summary>
    private void MarkActiveSurface(string? activeId)
    {
        if (_surfaces.Count < 2) return;
        foreach (var s in _surfaces)
        {
            var active = s.ContainerId == activeId;
            s.Frame.BorderBrush = active ? Accent : Brushes.Transparent;
            if (!active) s.Frame.Cursor = Cursors.Hand;
        }
    }

    /// <summary>Make a grid that is already on screen the active one. The figure itself does not change, since the
    /// whole point is that it was already drawn; what moves is which grid the summary describes, which one
    /// <b>Add item</b> fills, and which one is outlined.</summary>
    private void ActivateContainer(string? containerId)
    {
        if (containerId != _path[^1].ContainerId) NavigateToContainer(containerId);
    }

    private FrameworkElement SlotCell(CargoItem item, string slot)
    {
        var tile = ItemTile(item, SlotPx, SlotPx, item.Stack);
        tile.ToolTip = $"{SlotFriendly(slot)}: {Label(item)}"
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

    private bool IsContainerItem(CargoItem item) => _catalog.Lookup(item.DefName)?.IsContainer == true;

    // A slotted container drills too: an EVA suit's compartments and a backpack's pouches are slotted, and an
    // empty one still has to be openable or there is nowhere to put the battery. Slotted only bars dragging.
    private bool IsDrillable(CargoItem item) =>
        !item.IsStack && (item.Children.Count > 0 || (Editing && IsContainerItem(item)));

    private Border ItemTile(CargoItem item, double w, double h, int count)
    {
        var drillable = IsDrillable(item);   // a stack's "children" are copies, not cargo — never drillable

        var img = PixelImage(Bmp(item.DefName, item.Slotted ? 0 : item.GridRot));   // slot cells never rotate

        var overlay = new Grid();
        overlay.Children.Add(img);   // badges (below) stay upright in the cell corners

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
            ToolTip = Label(item)
                + (count > 1 ? $"  ×{count}" : "")
                + (drillable ? $"  ({item.SubtreeCount - 1} inside — click to open)" : "")
                + "  · Alt+click for info"
                + (Editing && !item.Slotted ? "  · drag to move (R turns it) · right-click to remove or rename" : ""),
        };
        if (Editing && !item.Slotted)
        {
            border.Cursor = Cursors.Hand;
            border.MouseLeftButtonDown += (_, e) =>
            {
                // Alt+click asks what the thing IS rather than moving it, so it has to intercept before the drag
                // arms — otherwise the drag swallows the click and the panel never opens.
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) { ShowInfo(item); e.Handled = true; return; }
                StartDrag(item, e);   // click vs drag decided on button-up
            };
            border.ContextMenu = TileMenu(item);
        }
        else
        {
            // Read-only, or an equipped item: Alt+click still answers "what is this", which is the whole point of
            // the panel and is as useful on a suit's battery as on a crate's rounds.
            border.Cursor = Cursors.Hand;
            border.MouseLeftButtonUp += (_, e) =>
            {
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) { ShowInfo(item); e.Handled = true; return; }
                if (drillable) DrillInto(item);
            };
            if (item.Slotted) border.ContextMenu = SlottedMenu(item);
        }
        return border;
    }

    private CargoInfoWindow? _info;

    /// <summary>
    /// Open the info panel on an item, or re-point the open one. One window, like the ship's Simulate: a second
    /// would be describing a different item with nothing on screen to say which.
    ///
    /// <para>The panel re-reads the item by id rather than holding it, so an edit made behind it lands and a
    /// removal closes it. That matters because the tree is immutable — every edit replaces the node, so a held
    /// reference would go stale on the first move.</para>
    /// </summary>
    private void ShowInfo(CargoItem item)
    {
        var id = item.StrID;

        CargoInfo? Read() =>
            _doc is not null && FindNode(RootCargo, id) is { } live ? CargoInfo.For(live, _doc) : null;

        void DoRename(string? name)
        {
            if (!Editing) return;
            if (CargoEdit.Rename(HostCargo, id, name) is { } next) Commit(next);
        }

        if (_info is { } open)
        {
            open.Retarget(Read, DoRename);
            open.Activate();
            return;
        }
        var window = new CargoInfoWindow(Read, DoRename) { Owner = this };
        window.Closed += (_, _) => _info = null;
        _info = window;
        window.Show();
    }

    /// <summary>An equipped item's menu. It cannot be moved or removed here (slots stay read-only), but it can be
    /// asked about and named, which is the whole of what #37 wanted on items without a container of their own.</summary>
    private ContextMenu SlottedMenu(CargoItem item)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Info…", () => ShowInfo(item)));
        if (Editing) menu.Items.Add(MenuItem(item.CustomName is null ? "Rename…" : "Rename or clear…", () => RenameItem(item)));
        return menu;
    }

    /// <summary>Rename one cargo item through the shared dialog — the other way in, alongside the info panel's
    /// own box, exactly as a placed part offers both (#30, #38).</summary>
    private void RenameItem(CargoItem item)
    {
        if (!Editing) return;
        var def = _catalog.Lookup(item.DefName);
        var dlg = new RenameDialog(def?.Friendly ?? item.DefName, item.CustomName, "item") { Owner = this };
        if (dlg.ShowDialog() != true) return;
        // The stock name typed back means the same as an empty box, exactly as it does for a part.
        var chosen = Rename.Typed(dlg.ChosenName, def);
        if (CargoEdit.Rename(HostCargo, item.StrID, chosen) is { } next)
        {
            Commit(next);
            _info?.Refresh();
        }
    }

    /// <summary>Right-click menu for a grid tile when editing: remove one / the whole stack, and (when nested) move
    /// the item out to a container on the current path.</summary>
    private ContextMenu TileMenu(CargoItem item)
    {
        var menu = new ContextMenu();
        // Info and rename lead, because they act on any item at all — the removal below is the only thing here
        // that needs the item to be loose cargo, and #37's whole point was that naming should not be gated on
        // having a container.
        menu.Items.Add(MenuItem("Info…", () => ShowInfo(item)));
        menu.Items.Add(MenuItem(item.CustomName is null ? "Rename…" : "Rename or clear…", () => RenameItem(item)));
        menu.Items.Add(new Separator());
        if (item.IsStack && item.Stack > 1)
        {
            // The shortcuts are on the labels because the menu is where they are discovered: the grid itself has no
            // room to say it, and Shift+Del is not a thing anyone tries on spec.
            menu.Items.Add(MenuItem("Remove one",
                () => Remove(CargoEdit.RemoveOne(HostCargo, item.StrID)), "Del"));
            menu.Items.Add(MenuItem($"Remove all ×{item.Stack}",
                () => Remove(CargoEdit.RemoveWhole(HostCargo, item.StrID)), "Shift+Del"));
        }
        else
        {
            var label = item.Children.Count > 0 ? "Remove (with contents)" : "Remove";
            menu.Items.Add(MenuItem(label, () => Remove(CargoEdit.RemoveWhole(HostCargo, item.StrID)), "Del"));
        }

        if (_path.Count > 1)   // nested — offer to move the item out to an ancestor container
        {
            var moveTo = new MenuItem { Header = "Move to" };
            for (var i = 0; i < _path.Count - 1; i++)
            {
                var crumb = _path[i];
                moveTo.Items.Add(MenuItem(crumb.Title, () => MoveToTarget(item, crumb.ContainerId)));
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(moveTo);
        }
        return menu;
    }

    private static MenuItem MenuItem(string header, Action act, string? gesture = null)
    {
        var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
        mi.Click += (_, _) => act();
        return mi;
    }

    private FrameworkElement WrapTile(CargoItem item, string note)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        row.Children.Add(ItemTile(item, SlotPx, SlotPx, item.Stack));
        row.Children.Add(new TextBlock
        {
            Text = $"{Label(item)}  {note}",
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

    // ---- editing: add / remove ----

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
        var grid = container.ContainerGrid ?? (6, 6);
        var dlg = new AddCargoDialog(_catalog, _sprites, container, offered,
            def => CargoEdit.MaxAddable(HostCargo, containerId, grid, def),
            // the breadcrumb's tail is what the user is actually looking at, custom name and all
            _path.Count > 0 ? _path[^1].Title : null) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Chosen is not { } pick) return;

        var updated = CargoEdit.Add(HostCargo, containerId, grid, pick.Def, pick.Quantity, _catalog);
        if (updated is null)
        {
            MessageBox.Show(this,
                $"Not enough room in this container for {pick.Quantity} × {pick.Def.Friendly}.",
                "Won't fit", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Commit(updated);
    }

    /// <summary>
    /// Apply a remove, keeping the selection on the tile when there is still something there.
    ///
    /// <para><b>Taking one off a stack leaves the stack.</b> <see cref="CargoEdit.RemoveOne"/> rebuilds the node
    /// with a <c>with</c> expression, so the same <c>StrID</c> is still on the same cell with one fewer in it.
    /// Clearing the selection unconditionally meant every press of Delete had to be preceded by another click, so
    /// emptying a stack of five cost ten actions instead of five. It only clears when the item has actually
    /// gone.</para>
    /// </summary>
    private void Remove(IReadOnlyList<CargoItem> newRootCargo)
    {
        if (_selectedId is { } id && FindNode(newRootCargo, id) is null) _selectedId = null;
        Commit(newRootCargo);
    }

    // ---- editing: drag / move / rotate ----

    private void StartDrag(CargoItem item, MouseButtonEventArgs e)
    {
        _dragItem = item;
        _dragRot = GridMath.Norm(item.GridRot);
        _dragStart = e.GetPosition(_overlay);
        _dragging = false;
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is null) return;
        var p = e.GetPosition(_overlay);
        if (!_dragging)
        {
            if (Math.Abs(p.X - _dragStart.X) < 4 && Math.Abs(p.Y - _dragStart.Y) < 4) return;   // a click, not a drag yet
            _dragging = true;
            // Capture once the drag is real (not on mouse-down, so a plain click is untouched). Without it a
            // release outside the window never reaches OnDragUp and the drag stays live into the next click.
            CaptureMouse();
            BuildGhost();
        }
        PositionGhost(p);
        UpdateDropHint();
    }

    /// <summary>(Re)build the drag ghost at the footprint the item would occupy at its pending rotation, so what
    /// follows the cursor is the size and orientation that will actually land.</summary>
    private void BuildGhost()
    {
        if (_dragItem is not { } item) return;
        if (_ghost is not null) _overlay.Children.Remove(_ghost);
        var (w, h) = Footprint(item, _dragRot);
        _ghost = new Image
        {
            Source = Bmp(item.DefName, _dragRot),
            Width = w * CellPx,
            Height = h * CellPx,
            Stretch = Stretch.Uniform,
            Opacity = 0.7,
            IsHitTestVisible = false,
        };
        RenderOptions.SetBitmapScalingMode(_ghost, BitmapScalingMode.NearestNeighbor);
        _overlay.Children.Add(_ghost);
    }

    /// <summary>Centre the ghost on the cursor, matching the game's own drop anchor (see <see cref="DropCell"/>).</summary>
    private void PositionGhost(Point p)
    {
        if (_ghost is null) return;
        Canvas.SetLeft(_ghost, p.X - _ghost.Width / 2);
        Canvas.SetTop(_ghost, p.Y - _ghost.Height / 2);
    }

    /// <summary>The footprint an item occupies at <paramref name="rot"/>, in tiles — <see cref="CargoItem.EffW"/>/
    /// <see cref="CargoItem.EffH"/> for a rotation the item does not carry yet.</summary>
    private static (int W, int H) Footprint(CargoItem item, int rot) =>
        GridMath.Norm(rot) % 180 == 0 ? (item.GridW, item.GridH) : (item.GridH, item.GridW);

    /// <summary>
    /// The top-left cell a drop at <paramref name="gp"/> (grid-canvas coordinates) lands on, for a
    /// <paramref name="w"/>×<paramref name="h"/> footprint. The item is centred on the cursor rather than
    /// anchored by its top-left corner, which is the game's rule: <c>GUIInventoryWindow.PairXYFromLocalPoint</c>
    /// subtracts half the item's extent (its pixel size less one cell, swapped when the rotation is vertical)
    /// before dividing by the cell size. Grabbing a 1×3 missile by its middle and dropping it therefore leaves it
    /// where the cursor is, instead of shunting it a tile down.
    /// <para>Truncation toward zero is the game's too, and is what makes a drop just past the top or left edge
    /// settle into row or column 0 rather than miss the grid.</para>
    /// </summary>
    private static (int X, int Y) DropCell(Point gp, int w, int h) => (
        (int)((gp.X - (w - 1) * CellPx / 2.0) / CellPx),
        (int)((gp.Y - (h - 1) * CellPx / 2.0) / CellPx));

    /// <summary>
    /// The grid under a point in <see cref="_overlay"/> coordinates, and that point in the grid's own frame.
    ///
    /// <para>There is a list to search rather than one canvas because a backpack draws its pouches beside its own
    /// grid, so "the grid" is whichever one the cursor is actually over. Deepest first, since a sub-grid is drawn
    /// over its host's figure and is the more specific answer where the two could both claim a point.</para>
    /// </summary>
    private (GridSurface Surface, Point At)? SurfaceAt(Point overlayPoint)
    {
        for (var i = _surfaces.Count - 1; i >= 0; i--)
        {
            var canvas = _surfaces[i].Canvas;
            if (!canvas.IsVisible) continue;
            var p = _overlay.TransformToVisual(canvas).Transform(overlayPoint);
            if (p.X >= 0 && p.Y >= 0 && p.X < canvas.ActualWidth && p.Y < canvas.ActualHeight)
                return (_surfaces[i], p);
        }
        return null;
    }

    /// <summary>The grid an item is currently drawn in, so a drop elsewhere is known to be a move BETWEEN
    /// containers and can be held to the target's item filter. Null when it is not on any grid on screen.</summary>
    private GridSurface? SurfaceHolding(string itemId) =>
        _surfaces.FirstOrDefault(s => s.Layout.Items.Any(i => i.Item.StrID == itemId));

    /// <summary>Whether <paramref name="target"/> would take <paramref name="item"/> at all — the container's own
    /// <c>strContainerCT</c>. Only asked when the drop crosses into a different container: an item already sitting
    /// in one is left alone, since an imported container may hold something its filter would refuse today and
    /// rearranging it must not become impossible.</summary>
    private bool AcceptedBy(GridSurface target, CargoItem item) =>
        SurfaceHolding(item.StrID)?.ContainerId == target.ContainerId
        || target.Def is null
        || _catalog.Lookup(item.DefName) is not { } itemDef
        || ContainerFilter.Accepts(_catalog, target.Def, itemDef);

    /// <summary>Draw where the drop would land: the container tile it would nest into, or the footprint it would
    /// occupy, green when that is legal and red when it is not. Removed when the pointer is off every grid.</summary>
    private void UpdateDropHint()
    {
        ClearDropHint();
        if (!_dragging || _dragItem is not { } item) return;
        if (SurfaceAt(Mouse.GetPosition(_overlay)) is not { } hit) return;
        var (surface, gp) = hit;
        var canvas = surface.Canvas;

        Rect rect;
        Brush stroke;
        if (NestTarget(surface.Layout, item, gp) is { } onto)
        {
            rect = new Rect(onto.X * CellPx, onto.Y * CellPx, onto.W * CellPx, onto.H * CellPx);
            stroke = Accent;   // drop-to-nest, the same colour the drillable badge uses
        }
        else
        {
            var (w, h) = Footprint(item, _dragRot);
            var (cx, cy) = DropCell(gp, w, h);
            rect = new Rect(cx * CellPx, cy * CellPx, w * CellPx, h * CellPx);
            // Ask the model rather than re-deriving the rule: this is exactly the edit the drop will attempt.
            var fits = AcceptedBy(surface, item)
                && CargoEdit.Move(HostCargo, item.StrID, surface.ContainerId, surface.Grid, cx, cy, _dragRot) is not null;
            stroke = fits ? ThemeManager.Good : ThemeManager.Bad;
        }

        _dropHint = new Border
        {
            Width = rect.Width,
            Height = rect.Height,
            BorderBrush = stroke,
            BorderThickness = new Thickness(2),
            // A wash of the same colour, so the footprint reads as a block and not just an outline. The theme
            // brushes are frozen, hence a fresh brush rather than an Opacity set on the shared one.
            Background = stroke is SolidColorBrush solid ? new SolidColorBrush(solid.Color) { Opacity = 0.18 } : null,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(_dropHint, rect.X);
        Canvas.SetTop(_dropHint, rect.Y);
        canvas.Children.Add(_dropHint);
    }

    /// <summary>Tear down the drag visuals and state, leaving the tree untouched. The caller decides what (if
    /// anything) the drag commits.</summary>
    private void EndDrag()
    {
        if (_ghost is not null) { _overlay.Children.Remove(_ghost); _ghost = null; }
        ClearDropHint();
        _dragItem = null;      // cleared before the release, so the LostMouseCapture handler it raises is a no-op
        _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private void ClearDropHint()
    {
        if (_dropHint is null) return;
        (_dropHint.Parent as Canvas)?.Children.Remove(_dropHint);
        _dropHint = null;
    }

    /// <summary>The container tile under <paramref name="gp"/> that <paramref name="item"/> would nest into, or
    /// null when the pointer is not over one.</summary>
    private PackedItem? NestTarget(GridLayoutResult layout, CargoItem item, Point gp)
    {
        int cx = (int)(gp.X / CellPx), cy = (int)(gp.Y / CellPx);
        return layout.Items.FirstOrDefault(pi => pi.Item.StrID != item.StrID
            && cx >= pi.X && cx < pi.X + pi.W && cy >= pi.Y && cy < pi.Y + pi.H
            && !pi.Item.IsStack && IsContainerItem(pi.Item));
    }

    private void OnDragUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragItem is null) return;
        var item = _dragItem;
        var dragged = _dragging;
        var rot = _dragRot;
        EndDrag();

        if (!dragged)   // a plain click: drill a container, else select
        {
            if (IsDrillable(item)) DrillInto(item);
            else { _selectedId = item.StrID; Render(); }
            return;
        }

        // dropped onto an ancestor in the breadcrumb → move the item out to it
        var win = e.GetPosition(_overlay);
        foreach (var (el, cid) in _crumbTargets)
            if (BoundsIn(el).Contains(win)) { MoveToTarget(item, cid); return; }

        // dropped over a grid → onto a container tile (nest), else to that cell of THAT grid. The grid is
        // whichever one the cursor is over, so an item drags straight from a backpack into one of its pouches
        // rather than only within the one container the view used to show.
        if (SurfaceAt(win) is { } hit)
        {
            var (surface, gp) = hit;
            if (NestTarget(surface.Layout, item, gp) is { } onto) MoveToTarget(item, onto.Item.StrID);
            else
            {
                var (w, h) = Footprint(item, rot);
                var (cx, cy) = DropCell(gp, w, h);
                MoveWithin(item, surface, cx, cy, rot);
            }
        }
        // dropped nowhere useful → snap back (no change)
    }

    /// <summary>An element's bounds in <see cref="_overlay"/> coordinates (the drop-hit-test frame).</summary>
    private Rect BoundsIn(FrameworkElement el) =>
        el.TransformToVisual(_overlay).TransformBounds(new Rect(new Point(0, 0), el.RenderSize));

    /// <summary>Move an item to a cell of the grid it was dropped on, landing in <paramref name="rot"/> when the
    /// drag turned it in hand. That grid is usually the one it came from (a rearrange), and is a different one
    /// when the drop crossed into a pouch shown beside it, which the container's filter then has a say in.</summary>
    private void MoveWithin(CargoItem item, GridSurface surface, int x, int y, int? rot = null)
    {
        if (!AcceptedBy(surface, item))
        {
            MessageBox.Show(this, $"“{surface.Def?.Friendly ?? "That container"}” won't hold {Label(item)}.",
                "Can't move here", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (CargoEdit.Move(HostCargo, item.StrID, surface.ContainerId, surface.Grid, x, y, rot) is { } result)
        {
            _selectedId = item.StrID;
            Commit(result);
        }
        // else: doesn't fit → snap back (leave unchanged). The drop hint was already showing red, so the refusal
        // is not news and a dialog here would only be in the way.
    }

    /// <summary>Move an item into another container (one nested here, or an ancestor on the breadcrumb) — into its
    /// first free cell, subject to the container's filter and capacity ("the Law").</summary>
    private void MoveToTarget(CargoItem item, string? containerId)
    {
        PartDef? def;
        IReadOnlyList<CargoItem> loose;
        if (containerId is null) { def = _catalog.Lookup(_rootDefName); loose = RootCargo.Where(c => !c.Slotted).ToList(); }
        else if (FindNode(RootCargo, containerId) is { } node) { def = _catalog.Lookup(node.DefName); loose = node.Children.Where(c => !c.Slotted).ToList(); }
        else return;

        if (def?.ContainerGrid is not { } grid) return;
        if (_catalog.Lookup(item.DefName) is { } itemDef && !ContainerFilter.Accepts(_catalog, def, itemDef))
        {
            MessageBox.Show(this, $"“{def.Friendly}” won't hold {Label(item)}.", "Can't move here",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // The item lands in whatever orientation the target has room for, turning it on its side when it no
        // longer fits upright — the same rule the add picker uses.
        var canRotate = _catalog.Lookup(item.DefName)?.CanRotateInInventory == true;
        if (InventoryGrid.FirstFreeCellRotated(grid.W, grid.H, loose, item.GridW, item.GridH, canRotate) is not { } cell)
        {
            MessageBox.Show(this, $"No room left in “{def.Friendly}” for {Label(item)}.",
                "Won't fit", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (CargoEdit.Move(HostCargo, item.StrID, containerId, grid, cell.X, cell.Y, cell.Rot) is { } result)
        {
            _selectedId = item.StrID;
            Commit(result);
        }
    }

    /// <summary>Turn the selected item 90° where it sits (see <see cref="CargoEdit.Rotate"/>, which pivots about
    /// its centre and takes the nearest cell that fits). Rotating an item held in a drag goes through
    /// <see cref="RotateDrag"/> instead.
    /// <para>It turns in the grid the item is actually drawn in, which is not always the active one: with a
    /// backpack's pouches on screen you can select something in a pouch while the backpack is still active, and
    /// rotating against the active container would find nothing there and do nothing at all.</para></summary>
    private void RotateSelected()
    {
        if (_selectedId is not { } id) return;
        var (containerId, grid) = SurfaceHolding(id) is { } s
            ? (s.ContainerId, s.Grid)
            : (_path[^1].ContainerId, _currentGrid);
        if (CargoEdit.Rotate(HostCargo, id, containerId, grid, _catalog) is { } result)
            Commit(result);   // else: nothing in the grid takes the swapped footprint → leave as is
    }

    /// <summary>Turn the item currently held in a drag, in hand: the ghost and the drop hint re-draw at the new
    /// orientation and nothing is committed until the drop. Sheet items (walls, floors) refuse, as they do
    /// everywhere else.</summary>
    private void RotateDrag()
    {
        if (_dragItem is not { } item || !_dragging) return;
        if (_catalog.Lookup(item.DefName) is { Item.HasSpriteSheet: true }) return;
        _dragRot = GridMath.Norm(_dragRot + 90);
        BuildGhost();
        PositionGhost(Mouse.GetPosition(_overlay));
        UpdateDropHint();
    }

    /// <summary>Apply a rebuilt cargo tree for the root placement through the command stack (undoable), then
    /// re-render off the now-current tree. Keeps the current selection (its item usually survives the edit).</summary>
    private void Commit(IReadOnlyList<CargoItem> newRootCargo)
    {
        _stack!.Push(_doc!, _root is not null
            ? new SetCargoCommand(_root, _root.Cargo, newRootCargo)
            : new SetLooseCargoCommand(_rootLoose!, _rootLoose!.Cargo, newRootCargo));
        Render();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!Editing) return;
        switch (e.Key)
        {
            // Shift or Alt takes the whole stack, matching the right-click menu's "Remove all ×N", which was the
            // only route to it. Without a keyboard equivalent a stack of five was five presses even once the
            // selection stopped being dropped between them.
            case Key.Delete when _selectedId is { } id:
                var whole = (Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Alt)) != 0;
                Remove(whole
                    ? CargoEdit.RemoveWhole(HostCargo, id)
                    : CargoEdit.RemoveOne(HostCargo, id));
                e.Handled = true;
                break;
            case Key.Escape when _dragging:
                EndDrag();   // abandon the drag, leaving the item where it was
                e.Handled = true;
                break;
            case Key.R when _dragging:
                RotateDrag();   // turn the item in hand; the drop commits it
                e.Handled = true;
                break;
            case Key.R:
                RotateSelected();
                e.Handled = true;
                break;
            case Key.Z when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                _stack!.Undo(_doc!); _selectedId = null; Render();
                e.Handled = true;
                break;
            case Key.Y when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                _stack!.Redo(_doc!); _selectedId = null; Render();
                e.Handled = true;
                break;
        }
    }

    // ---- sprites ----

    private BitmapSource Bmp(string defName) =>
        _catalog.Lookup(defName) is { } part ? _sprites.Thumb(part) : _sprites.Missing;

    /// <summary>The item's sprite turned to <paramref name="rot"/>. The stored sprite is authored upright; when a
    /// grid item is rotated its footprint (EffW/EffH) is swapped but the sprite must turn with it, or a rotated
    /// item is squashed into the swapped cell (a tall missile laid flat renders as a stretched sliver). Rotating
    /// the BITMAP itself (a rigid 90° pixel turn, dimensions swapped) then letting Stretch.Uniform fit it into the
    /// cell gives no aspect distortion and no layout overflow from an over-tall holder.</summary>
    private BitmapSource Bmp(string defName, int rot)
    {
        var bmp = Bmp(defName);
        var r = GridMath.Norm(rot);
        if (r == 0) return bmp;
        var turned = new TransformedBitmap(bmp, new RotateTransform(r));
        turned.Freeze();
        return turned;
    }

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
