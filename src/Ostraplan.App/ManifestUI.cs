using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// The item manifest: every item the design carries, wherever it is (#36).
///
/// <para>It exists because an item's <i>location</i> is the thing the plan cannot show you. A container renders as
/// a closed box, so cargo three levels down inside a locker on the far side of the ship is invisible until you go
/// looking for it, and a stray extinguisher dropped on a deck long ago looks like every other sprite. So the list
/// is grouped by item type the way a shop window is, and each row opens onto the individual items with where each
/// one actually sits.</para>
///
/// <para><b>It is not the bill of materials, and deliberately so.</b> The bill counts install kits for buildable
/// structure: a locker is a part you build, and it is priced there. What is <i>inside</i> the locker is cargo,
/// which no report answered before this one, and folding it into the bill would make the bill stop matching what
/// a build costs (see <see cref="BillOfMaterials"/> and <see cref="EditCost"/>, which both exclude a garment's
/// pockets on purpose). The two sit side by side on the Design menu instead.</para>
///
/// <para>Modeless, like the other reports, because the whole point of "show it on the grid" is that the grid is
/// still there to look at. It is also an <i>edit</i> route — rename and delete write to the live document — so it
/// refreshes itself after its own edits and goes dead with the rest of the chrome while the document is frozen.</para>
/// </summary>
public sealed class ManifestWindow : ReportWindow
{
    private static Brush Ink => ThemeManager.Ink;
    private static Brush Dim => ThemeManager.Dim;
    private static Brush Accent => ThemeManager.Accent;
    private static Brush FieldBg => ThemeManager.FieldBg;
    private static Brush PanelBorder => ThemeManager.PanelBorder;

    private readonly ShipDocument _doc;
    private readonly CommandStack _stack;
    private readonly Action<RenderItem> _reveal;

    /// <summary>The zone the list is scoped to, or null for the whole ship.</summary>
    private ShipZone? _zone;

    /// <summary>What the filter box holds. Matched against an item's name, the name it was given, and where it is.</summary>
    private string _filter = "";

    /// <summary>
    /// Whether the list is arranged by what things are or by where they are.
    ///
    /// <para>Two questions, and one shape cannot answer both. By type is the stock list: how many of these does the
    /// ship carry and what are they worth, which is what you ask before buying or selling. By location is the
    /// ship's own organisation: a rack in an engineering bay holding a backpack holding conduits is three
    /// deliberate decisions, and a flat list with a location string against each row has thrown all three away.
    /// Neither replaces the other, so both are here and by type stays the default.</para>
    /// </summary>
    private bool _byLocation;

    /// <summary>Which location-tree nodes are open, by their path from the root. Kept across a rebuild for the same
    /// reason <see cref="_open"/> is: an edit must not fold the tree up under whoever just made it.</summary>
    private readonly HashSet<string> _openNodes = new(StringComparer.Ordinal);

    /// <summary>Which def rows are open, by def name. Kept across a rebuild so an edit does not fold the list up
    /// under the person who just made it.</summary>
    private readonly HashSet<string> _open = new(StringComparer.Ordinal);

    /// <summary>Where the list is hung. Held so the filter can redraw only the list: rebuilding the whole body on
    /// every keystroke would destroy the filter box the user is typing into and take the caret with it.</summary>
    private readonly ContentControl _listHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };

    /// <summary>The two figure lines above the table. Held for the same reason as <see cref="_listHost"/>: they
    /// count what the list is showing, so they have to follow the filter without the box being rebuilt underneath
    /// whoever is typing in it.</summary>
    private readonly TextBlock _headline = new()
    {
        Foreground = Accent, FontSize = 26, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 2, 0, 2),
    };

    private readonly TextBlock _summary = new()
    {
        Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
    };

    private Manifest _manifest = Manifest.Empty;

    /// <summary>The laid-out body, for the offscreen preview render (<c>--mansmoke</c>).</summary>
    internal Panel? PreviewContent => Content as Panel;

    /// <summary>Open the first <paramref name="count"/> rows, so the preview render shows the item rows as well as
    /// the def rows. Preview only: the window itself opens a row when it is clicked.</summary>
    internal void PreviewOpen(int count)
    {
        foreach (var line in _manifest.Lines.Take(count)) _open.Add(line.DefName);
        Rebuild();
    }

    /// <summary>Switch the preview render to the location tree. Preview only: the window itself has a picker.</summary>
    internal void PreviewByLocation()
    {
        _byLocation = true;
        Rebuild();
    }

    public ManifestWindow(ShipDocument doc, CommandStack stack, Action<RenderItem> reveal)
    {
        _doc = doc;
        _stack = stack;
        _reveal = reveal;

        Title = "Item Manifest";
        Width = 660; Height = 760;
        MinWidth = 460; MinHeight = 320;

        Refresh();
        RerunRequested += Refresh;
    }

    /// <summary>Re-walk the design and redraw. Cheap enough to do whole — it is one pass over the cargo trees —
    /// which is why this window refreshes itself rather than sitting behind a stale bar the way an analysis report
    /// does. The host calls it whenever the design changes underneath.</summary>
    public void Refresh()
    {
        // The scope can be deleted underneath: zones are ordinary document edits, and one of them is "delete this
        // zone". Fall back to the whole ship rather than going on filtering against a set nothing points at.
        if (_zone is not null && _doc.IndexOfZone(_zone) < 0) _zone = null;
        _manifest = ItemManifest.Build(_doc, _zone?.Tiles);
        Rebuild();
    }

    // ---- layout ----

    /// <summary>
    /// Take an element off whatever last held it, so a rebuild can hang it somewhere new.
    ///
    /// <para>The list and the two figure lines are <b>kept</b> across a rebuild rather than remade, because the
    /// filter updates them without touching the box being typed into. A rebuild then hands each of them to a
    /// freshly built parent, and WPF refuses outright to give an element a second one — so the old parent has to
    /// let go first. Without this the window threw the moment the scope changed or anything was edited.</para>
    /// </summary>
    private static T Detach<T>(T element) where T : FrameworkElement
    {
        switch (element.Parent)
        {
            case Panel p: p.Children.Remove(element); break;
            case ContentControl c when ReferenceEquals(c.Content, element): c.Content = null; break;
            case Decorator d when ReferenceEquals(d.Child, element): d.Child = null; break;
        }
        return element;
    }

    private void Rebuild()
    {
        var root = new DockPanel { Margin = new Thickness(18) };

        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = "ITEM MANIFEST", Foreground = Dim, FontWeight = FontWeights.Bold, FontSize = 11 });
        head.Children.Add(Detach(_headline));
        head.Children.Add(Detach(_summary));
        head.Children.Add(Controls());
        head.Children.Add(ColumnHeader());
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var buttons = Buttons();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        root.Children.Add(new ScrollViewer
        {
            // Always reserved rather than Auto: the column header sits outside the scroller, so a gutter that came
            // and went with the list length would slide the table out from under its own headings.
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            Content = Detach(_listHost),
            Margin = new Thickness(0, 0, 0, 4),
        });

        DrawList();
        SetBody(root);
    }

    /// <summary>
    /// The scope picker and the filter box.
    ///
    /// <para>Scope matches the shop window: the whole ship, or one zone of it. <b>Any</b> zone, not only a Haul or
    /// Barter one, because a manifest scoped to "Engineering" answers the same question a shop scoped to its
    /// counter does, and a design's zones are the only division of a ship it has.</para>
    /// </summary>
    private UIElement Controls()
    {
        var scope = new ComboBox { MinWidth = 200, VerticalContentAlignment = VerticalAlignment.Center };
        scope.Items.Add("Whole ship");
        foreach (var z in _doc.Zones)
            scope.Items.Add($"{ZoneLabel(z)} — {z.Tiles.Count} tile{Plural(z.Tiles.Count)}");
        scope.SelectedIndex = _zone is null ? 0 : _doc.IndexOfZone(_zone) + 1;
        // Handler attached AFTER the initial selection, so setting it up cannot re-enter Refresh mid-rebuild and
        // leave the body that eventually lands describing a scope the window is no longer on.
        scope.SelectionChanged += (_, _) =>
        {
            var i = scope.SelectedIndex;
            _zone = i <= 0 ? null : _doc.Zones[i - 1];
            Refresh();   // the totals change with the scope, so this one redraws the whole body
        };

        var filter = new TextBox
        {
            Text = _filter, MinWidth = 200,
            VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(5, 3, 5, 3),
            ToolTip = "Match an item's name, the name you gave it, or where it is",
        };
        // A watermark rather than a label beside the box: an empty field with nothing in or on it says nothing
        // about what it takes.
        var hint = new TextBlock
        {
            Text = "Filter items", Foreground = Dim, Margin = new Thickness(9, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
            Visibility = _filter.Length == 0 ? Visibility.Visible : Visibility.Collapsed,
        };
        filter.TextChanged += (_, _) =>
        {
            _filter = filter.Text;
            hint.Visibility = filter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            DrawList();
        };

        var box = new Grid { Margin = new Thickness(10, 0, 0, 0) };
        box.Children.Add(filter);
        box.Children.Add(hint);

        var grouping = new ComboBox { MinWidth = 130, VerticalContentAlignment = VerticalAlignment.Center };
        grouping.Items.Add("By type");
        grouping.Items.Add("By location");
        grouping.SelectedIndex = _byLocation ? 1 : 0;
        grouping.ToolTip = "By type counts what the ship carries. By location keeps the nesting: zone, then what "
                         + "it is in, then what that is in.";
        // Attached after the initial selection, exactly as the scope picker is, so setting it up cannot re-enter a
        // redraw part-way through building the body it is going into.
        grouping.SelectionChanged += (_, _) =>
        {
            _byLocation = grouping.SelectedIndex == 1;
            Rebuild();   // the column headings differ between the two, so this is more than a redraw of the list
        };

        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(scope, Dock.Left);
        DockPanel.SetDock(grouping, Dock.Left);
        grouping.Margin = new Thickness(0, 0, 0, 0);
        scope.Margin = new Thickness(0, 0, 8, 0);
        row.Children.Add(scope);
        row.Children.Add(grouping);
        row.Children.Add(box);
        return row;
    }

    /// <summary>
    /// The table's own headings, sharing the rows' column groups so they sit over the figures they name. This is
    /// what says the last column is a price and the one before it a quantity, and it is the reason the window
    /// carries no prose explaining either.
    /// </summary>
    private UIElement ColumnHeader()
    {
        var grid = Columns("mWhere", "mCount", "mValue");
        Put(grid, Heading(_byLocation ? "WHERE IT IS" : "ITEM", TextAlignment.Left, 18), 0);
        // The middle column carries a different figure in each view: how a type is spread about, against what is
        // inside the level on that row.
        Put(grid, Heading(_byLocation ? "INSIDE" : "WHERE", TextAlignment.Right, 12), 1);
        Put(grid, Heading("QTY", TextAlignment.Right, 12), 2);
        Put(grid, Heading("VALUE", TextAlignment.Right, 12), 3);
        // Inset to match a row: the buttons' own padding and border on the left, and the same plus the scroll
        // gutter on the right, so the star column is the width it is inside the list.
        grid.Margin = new Thickness(9, 8, 9 + SystemParameters.VerticalScrollBarWidth, 4);
        return grid;
    }

    private static TextBlock Heading(string text, TextAlignment align, double left) => new()
    {
        Text = text, Foreground = Dim, FontSize = 10, FontWeight = FontWeights.Bold,
        TextAlignment = align, Margin = new Thickness(left, 0, 0, 0),
    };

    // ---- the list ----

    private void DrawList()
    {
        var list = new StackPanel();
        // Every row is its own Grid, so the columns only line up if they size together. This is what makes the list
        // read as a table instead of as a stack of chips each as wide as its own text.
        Grid.SetIsSharedSizeScope(list, true);
        var lines = Filtered();

        // The figures count what is on screen, so filtering narrows them too. A headline that stayed on the whole
        // scope would need a sentence underneath explaining that it had.
        var count = lines.Sum(l => l.Count);
        _headline.Text = $"{count} item{Plural(count)} · {lines.Count} type{Plural(lines.Count)}";
        _summary.Text = $"{ScopeLabel} · {lines.Sum(l => l.OnDeckCount)} on the decks · "
            + $"{lines.Sum(l => l.ContainedCount)} in containers · {Credits(lines.Sum(l => l.Value))}";

        if (lines.Count == 0)
            list.Children.Add(new TextBlock
            {
                Text = _manifest.IsEmpty
                    ? _zone is null ? "No items on this design." : "No items in this zone."
                    : "Nothing matches the filter.",
                Foreground = Dim, Margin = new Thickness(9, 8, 0, 6),
            });
        else if (_byLocation)
            // The same lines the by-type view is showing, rearranged — so the filter and the scope mean exactly
            // one thing whichever way the list happens to be grouped.
            foreach (var node in ItemManifest.ByLocation(lines)) list.Children.Add(NodeRow(node, "", 0));
        else
            foreach (var line in lines) list.Children.Add(LineRow(line));

        _listHost.Content = list;
    }

    // ---- the location tree ----

    /// <summary>
    /// One level of the location tree and everything under it, indented by depth.
    ///
    /// <para>Built eagerly rather than on first open, unlike the by-type rows: a tree's shape <i>is</i> the answer
    /// here, so a level that has not been expanded yet has not told you anything. The cost is bounded by the same
    /// walk the flat list already does.</para>
    /// </summary>
    private UIElement NodeRow(ManifestNode node, string parentPath, int depth)
    {
        var path = parentPath + "/" + node.Label;
        // The top level opens by default and nothing below it does. A tree that starts fully closed shows nothing
        // at all; one that opens two levels floods the view, which on a real ship meant sixty-eight identical
        // pill boxes with their contents hanging off each. One level in is the summary, and the rest is a click.
        var open = _openNodes.Contains(path) || (depth == 0 && !_openNodes.Contains("!" + path));

        var chevron = new TextBlock
        {
            Text = node.Children.Count == 0 ? "" : Chevron(open), Foreground = Dim,
            VerticalAlignment = VerticalAlignment.Center, Width = 14,
        };
        var label = new TextBlock
        {
            Text = node.OwnCount > 1 ? $"{node.Label}  ×{node.OwnCount}" : node.Label,
            Foreground = node.Kind == ManifestNodeKind.Zone ? Accent
                       : node.Entry?.CustomName is not null ? Accent : Ink,
            FontWeight = node.Kind is ManifestNodeKind.Zone or ManifestNodeKind.Host
                ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var inside = new TextBlock
        {
            // What is under a container, said on the container's own row: the figure that makes a collapsed level
            // worth reading, and the reason a tree beats a list of locations.
            Text = node.ContainedCount > 0 ? $"{node.ContainedCount} inside" : "",
            Foreground = Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right, Margin = new Thickness(12, 0, 0, 0),
        };
        var count = new TextBlock
        {
            Text = $"×{node.Count}", Foreground = Accent, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right,
            Margin = new Thickness(12, 0, 0, 0),
        };
        var value = new TextBlock
        {
            Text = Credits(node.Value), Foreground = Dim, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right,
            Margin = new Thickness(12, 0, 0, 0),
        };

        var grid = Columns("mWhere", "mCount", "mValue");
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(chevron);
        head.Children.Add(label);
        Put(grid, head, 0);
        Put(grid, inside, 1);
        Put(grid, count, 2);
        Put(grid, value, 3);

        var kids = new StackPanel { Visibility = open ? Visibility.Visible : Visibility.Collapsed };
        foreach (var child in node.Children) kids.Children.Add(NodeRow(child, path, depth + 1));

        var header = new Button
        {
            Content = grid, Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 4, 8, 4),
            ToolTip = node.Entry is null ? null : "Show it on the plan, or right-click to rename or remove it",
        };
        header.Click += (_, _) =>
        {
            if (node.Children.Count == 0) { if (node.Entry is { } e) _reveal(e.Host); return; }
            var nowOpen = kids.Visibility != Visibility.Visible;
            // Two marks rather than one: a default-open level needs somewhere to record having been closed, or it
            // springs back open the next time anything redraws.
            if (nowOpen) { _openNodes.Add(path); _openNodes.Remove("!" + path); }
            else { _openNodes.Remove(path); _openNodes.Add("!" + path); }
            kids.Visibility = nowOpen ? Visibility.Visible : Visibility.Collapsed;
            chevron.Text = Chevron(nowOpen);
        };
        if (node.Entry is { } entry) header.ContextMenu = NodeMenu(entry);

        var host = new StackPanel { Margin = new Thickness(depth == 0 ? 0 : 14, 1, 0, 1) };
        host.Children.Add(header);
        host.Children.Add(kids);
        return host;
    }

    /// <summary>The per-item actions, on the right button rather than as three buttons per row: a tree is mostly
    /// structure, and a column of buttons at every depth would bury it.</summary>
    private ContextMenu NodeMenu(ManifestEntry entry)
    {
        var menu = new ContextMenu();
        var show = new MenuItem { Header = "Show on the plan" };
        show.Click += (_, _) => _reveal(entry.Host);
        var rename = new MenuItem { Header = entry.CustomName is null ? "Rename…" : "Rename or clear…" };
        rename.Click += (_, _) => RenameEntry(entry);
        var delete = new MenuItem { Header = "Remove" };
        delete.Click += (_, _) => DeleteEntry(entry);
        menu.Items.Add(show);
        menu.Items.Add(rename);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
        return menu;
    }

    /// <summary>The lines the filter leaves. A line survives when its own name matches or any of its items does,
    /// and a line kept for an item's sake shows only the items that matched — so filtering for "Electrical" opens
    /// onto that crate rather than onto every crate on the ship.</summary>
    private IReadOnlyList<ManifestLine> Filtered()
    {
        if (_filter.Trim() is not { Length: > 0 } needle) return _manifest.Lines;

        var result = new List<ManifestLine>();
        foreach (var line in _manifest.Lines)
        {
            if (Has(line.Friendly, needle) || Has(line.DefName, needle)) { result.Add(line); continue; }
            var hits = line.Entries.Where(e => Match(e, needle)).ToList();
            if (hits.Count == 0) continue;
            result.Add(line with { Entries = hits, Count = hits.Sum(e => e.Count), Value = hits.Sum(e => e.Value) });
        }
        return result;
    }

    private static bool Match(ManifestEntry e, string needle) =>
        Has(e.Name, needle) || Has(e.DefName, needle) || Has(e.Where, needle);

    private static bool Has(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    // ---- one def's row, and the items under it ----

    private UIElement LineRow(ManifestLine line)
    {
        var open = _open.Contains(line.DefName);

        // The chevron sits in the same cell as the name rather than taking a column of its own, so every name
        // starts at the same x whichever rows happen to be open.
        var chevron = new TextBlock
        {
            Text = Chevron(open), Foreground = Dim, VerticalAlignment = VerticalAlignment.Center,
        };
        var name = new TextBlock
        {
            Text = line.Friendly, Foreground = Ink, Margin = new Thickness(18, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var where = new TextBlock
        {
            Text = Spread(line), Foreground = Dim, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
        };
        var count = new TextBlock
        {
            Text = $"×{line.Count}", Foreground = Accent, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right,
            Margin = new Thickness(12, 0, 0, 0),
        };
        var value = new TextBlock
        {
            Text = Credits(line.Value), Foreground = Dim, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right,
            Margin = new Thickness(12, 0, 0, 0),
        };

        var grid = Columns("mWhere", "mCount", "mValue");
        Put(grid, chevron, 0);
        Put(grid, name, 0);
        Put(grid, where, 1);
        Put(grid, count, 2);
        Put(grid, value, 3);

        // Filled lazily on first open. A hold with a thousand rounds in it would otherwise build a thousand rows
        // and their buttons before drawing anything, for a list nobody has asked to see inside yet.
        var body = new StackPanel
        {
            Margin = new Thickness(22, 0, 0, 8),
            Visibility = open ? Visibility.Visible : Visibility.Collapsed,
        };
        if (open) Populate(body, line);

        // A plain Button, deliberately, where an on/off affordance would ordinarily be a ToggleButton (see
        // CONVENTIONS.md). Fluent's checked state paints the whole row in the accent, and this row is not one
        // label but four columns that carry their own theme colours — the count, the value and the location all
        // went unreadable on the accent the moment a row was opened. Nothing is lost by dropping it: a disclosure
        // row's state is already told by its chevron and by the items appearing underneath it, which is more than
        // a view toggle ever has to say for itself.
        var header = new Button
        {
            Content = grid, Cursor = Cursors.Hand,
            // Stretch on the button as well as on its content: without it Fluent sizes a button to its own text,
            // which left every row as wide as its own name and the figures nowhere near each other.
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 5, 8, 5),
            ToolTip = $"Show each one, and where it is. Right-click to remove all ×{line.Count}.",
        };
        // The bulk removal lives on the right button rather than in a column of its own: it is destructive and
        // scoped to the whole ship, and a permanent button for it would sit a few pixels from the disclosure click
        // that every row invites. The tooltip above is what makes it findable.
        var menu = new ContextMenu();
        var removeAll = new MenuItem { Header = $"Remove all ×{line.Count} from {ReachLabel}" };
        removeAll.Click += (_, _) => DeleteLine(line);
        menu.Items.Add(removeAll);
        header.ContextMenu = menu;
        header.Click += (_, _) =>
        {
            var nowOpen = !_open.Contains(line.DefName);
            if (nowOpen) _open.Add(line.DefName); else _open.Remove(line.DefName);
            if (nowOpen && body.Children.Count == 0) Populate(body, line);
            body.Visibility = nowOpen ? Visibility.Visible : Visibility.Collapsed;
            chevron.Text = Chevron(nowOpen);
        };

        var host = new StackPanel { Margin = new Thickness(0, 1, 0, 1) };
        host.Children.Add(header);
        host.Children.Add(body);
        return host;
    }

    private void Populate(Panel body, ManifestLine line)
    {
        foreach (var entry in line.Entries) body.Children.Add(EntryRow(entry));
    }

    private static string Chevron(bool open) => open ? "▾" : "▸";

    /// <summary>How a def's items are spread about, which is the whole question for a stray: four crates all in a
    /// hold read differently from three in a hold and one on a corridor deck.</summary>
    private static string Spread(ManifestLine line) =>
        line.OnDeckCount == 0 ? "in containers"
        : line.ContainedCount == 0 ? "on the decks"
        : $"{line.OnDeckCount} on the decks";

    /// <summary>
    /// A row's columns: the first fills the width and the rest size to their own content, but they size
    /// <b>together across every row</b> through the named shared-size groups, which is what turns a stack of rows
    /// into a table with figures under each other. Named groups rather than fixed pixel widths, so the columns
    /// still line up at any UI scale and against any item name.
    /// </summary>
    private static Grid Columns(params string[] groups)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var group in groups)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = group });
        return grid;
    }

    private static void Put(Grid grid, UIElement child, int column)
    {
        Grid.SetColumn(child, column);
        grid.Children.Add(child);
    }

    /// <summary>A credit figure, always printed. A def the game publishes no price for reads as <c>0 cr</c> rather
    /// than as a blank cell: under a VALUE heading a zero says what it is worth, where a gap is indistinguishable
    /// from a figure that failed to render.</summary>
    private static string Credits(double value) => $"{value:N0} cr";

    private UIElement EntryRow(ManifestEntry entry)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = entry.Count > 1 ? $"{entry.Name}  ×{entry.Count}" : entry.Name,
            // A name someone gave the item is the point of naming it, so it reads as the accent rather than as
            // just another row of stock text.
            Foreground = entry.CustomName is null ? Ink : Accent,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            // A host's own pockets and pouches are in the tree and are written to the save, so they are listed —
            // but they are part of the thing holding them rather than cargo anyone put there, and a row that did
            // not say so would read as a stray. It rides on the location because that is the question it answers.
            Text = entry.Where + (entry.Intrinsic ? " · part of it" : ""),
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap,
        });

        var value = new TextBlock
        {
            Text = Credits(entry.Value), Foreground = Dim, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right,
            Margin = new Thickness(12, 0, 0, 0),
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };
        actions.Children.Add(Small("Show", "Select it and centre the plan on it", () => _reveal(entry.Host)));
        // One fixed label rather than one that grows when the item has a name: the box itself says that clearing
        // it puts the stock name back, and a button that changes width leaves the column ragged.
        actions.Children.Add(Small("Rename",
            "Give it a name, the way the game does. Clear the box to put the stock name back.",
            () => RenameEntry(entry)));
        actions.Children.Add(Small("Delete", "Remove it from the design", () => DeleteEntry(entry)));

        // The entries share their own column groups rather than the def rows' — they are indented under a row, so
        // lining their figures up with the totals above would put neither column where it belongs.
        var grid = Columns("eValue", "eActions");
        Put(grid, text, 0);
        Put(grid, value, 1);
        Put(grid, actions, 2);

        return new Border
        {
            Background = FieldBg, BorderBrush = PanelBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3), Padding = new Thickness(8, 4, 6, 4),
            Margin = new Thickness(0, 2, 0, 0), Child = grid,
        };
    }

    private static Button Small(string label, string tip, Action click)
    {
        var b = new Button
        {
            Content = label, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(4, 0, 0, 0),
            FontSize = 11, VerticalAlignment = VerticalAlignment.Center, ToolTip = tip,
        };
        b.Click += (_, _) => click();
        return b;
    }

    // ---- the per-row actions ----

    /// <summary>
    /// Name one item, through the same dialog and the same commands every other rename route uses — a deck item
    /// gets a <see cref="SetLooseCustomNameCommand"/>, an item inside something goes through
    /// <see cref="CargoEdit.Rename"/> onto its host's tree. One undo step either way.
    /// </summary>
    private void RenameEntry(ManifestEntry entry)
    {
        var def = _doc.Catalog.Lookup(entry.DefName);
        if (!Rename.CanRename(def)) return;

        if (entry.ItemId is not { } id)
        {
            if (entry.Host.Loose is not { } lo) return;
            var dlg = new RenameDialog(def!.Friendly, lo.CustomName, "item") { Owner = this };
            if (dlg.ShowDialog() != true) return;
            var chosen = Rename.Typed(dlg.ChosenName, def);   // the stock name typed back means the same as an empty box
            if (chosen == lo.CustomName) return;
            _stack.Push(_doc, new SetLooseCustomNameCommand(lo, lo.CustomName, chosen));
            Refresh();
            return;
        }

        // The cargo tree is immutable and every edit replaces nodes, so the entry's own node is only a snapshot:
        // re-read it by id before asking, exactly as the item info panel does.
        if (ItemManifest.Resolve(entry.Host, id) is not { } item) { Refresh(); return; }
        var itemDlg = new RenameDialog(def!.Friendly, item.CustomName, "item") { Owner = this };
        if (itemDlg.ShowDialog() != true) return;
        var name = Rename.Typed(itemDlg.ChosenName, def);
        if (CargoEdit.Rename(HostCargo(entry.Host), id, name) is not { } next) return;   // null is a no-op
        _stack.Push(_doc, CargoCommand(entry.Host, next));
        Refresh();
    }

    /// <summary>
    /// Remove one item from the design. A deck item goes as a whole object; an item inside something takes its own
    /// contents with it, because a crate you delete does not leave its cargo behind in the container that held it.
    /// Both are one undo step.
    ///
    /// <para><b>It only asks when the delete does more than the row says it will.</b> Confirming an ordinary
    /// removal was friction charged for nothing: the dialog's own closing line was "this is one undo step", which
    /// is the argument against showing it. Undo is the confirmation. What survives is the two cases where the row
    /// on screen is not the whole story: a container that takes cargo down with it, and a host's own pocket, whose
    /// removal leaves the thing holding it with nowhere to keep anything.</para>
    /// </summary>
    private void DeleteEntry(ManifestEntry entry)
    {
        if (entry.ItemId is not { } id)
        {
            if (entry.Host.Loose is not { } lo) return;
            var nested = lo.Cargo.Sum(c => c.SubtreeCount);
            if (nested > 0 && !Dlg.Confirm(this, DlgKind.Warning, "Remove item",
                    $"Remove {Describe(entry.Name, entry.Count)} from the deck at {lo.X}, {lo.Y}."
                    + $"\n\nIt is holding {nested} item{Plural(nested)}, which go with it."
                    + "\n\nThis is one undo step.",
                    "Remove"))
                return;
            _stack.Push(_doc, new RemoveLooseCommand(lo));
            Refresh();
            return;
        }

        if (ItemManifest.Resolve(entry.Host, id) is not { } item) { Refresh(); return; }
        // A stack's children are copies of itself, so only a real container has contents to warn about.
        var contents = item.IsStack ? 0 : item.Children.Sum(c => c.SubtreeCount);
        if (contents > 0 || entry.Intrinsic)
        {
            var note = contents > 0 ? $"\n\nIt is holding {contents} item{Plural(contents)}, which go with it." : "";
            // Removing a host's own pocket is legal but rarely meant: without it the garment reaches the game with
            // nowhere to keep anything. Say so rather than let it read like clearing a stray.
            var intrinsic = entry.Intrinsic
                ? "\n\nThis one comes with whatever holds it rather than being cargo put there, so removing it "
                  + "leaves that item with one less place to keep things."
                : "";
            if (!Dlg.Confirm(this, DlgKind.Warning, "Remove item",
                    $"Remove {Describe(entry.Name, entry.Count)} {entry.Where}.{note}{intrinsic}"
                    + "\n\nThis is one undo step.",
                    "Remove"))
                return;
        }

        _stack.Push(_doc, CargoCommand(entry.Host, CargoEdit.RemoveWhole(HostCargo(entry.Host), id)));
        Refresh();
    }

    /// <summary>
    /// Remove every item of one type in scope, wherever on the ship it is, as a single undo step.
    ///
    /// <para>The manifest is the only view that can offer this, because it is the only one that sees all of them at
    /// once: sixty-eight loose floor panels spread across a dozen containers are sixty-eight separate errands
    /// anywhere else. It <b>does</b> confirm, unlike a single delete, and for the reason a single delete does not:
    /// the scale is the part that is not on screen, and undo being one step is exactly what makes a mis-click
    /// expensive to notice rather than expensive to reverse.</para>
    ///
    /// <para>Grouped by host, and each host's removals chained onto one tree, because two items of the same def in
    /// the same container are two edits to one cargo tree and issuing them independently would have the second
    /// overwrite the first.</para>
    /// </summary>
    private void DeleteLine(ManifestLine line)
    {
        var entries = Filtered().FirstOrDefault(l => l.DefName == line.DefName)?.Entries;
        if (entries is null || entries.Count == 0) return;

        var count = entries.Sum(e => e.Count);
        var places = entries.Select(e => e.Where).Distinct(StringComparer.Ordinal).Count();
        var nested = entries.Sum(e =>
            e.ItemId is { } eid && ItemManifest.Resolve(e.Host, eid) is { IsStack: false } it
                ? it.Children.Sum(c => c.SubtreeCount)
                : e.Host.Loose is { } lo && e.ItemId is null ? lo.Cargo.Sum(c => c.SubtreeCount) : 0);
        var intrinsic = entries.Count(e => e.Intrinsic);

        if (!Dlg.Confirm(this, DlgKind.Warning, "Remove every one",
                $"Remove all ×{count} “{line.Friendly}” from {ReachLabel}, across {places} place{Plural(places)}."
                + (nested > 0 ? $"\n\nThey are holding {nested} item{Plural(nested)}, which go with them." : "")
                + (intrinsic > 0
                    ? $"\n\n{intrinsic} of them {(intrinsic == 1 ? "is a host's own pocket" : "are hosts' own pockets")}"
                      + " rather than cargo put there, so removing them leaves those items with nowhere to keep things."
                    : "")
                + "\n\nThis is one undo step.",
                $"Remove all ×{count}"))
            return;

        var commands = new List<IDocCommand>();

        // Deck objects go whole, one command each.
        foreach (var e in entries.Where(e => e.ItemId is null))
            if (e.Host.Loose is { } lo) commands.Add(new RemoveLooseCommand(lo));

        // Cargo is per host: chain this def's ids onto that host's tree so the last edit carries all of them. An id
        // already taken out by an ancestor's removal simply is not found, and the rewrite leaves the tree alone.
        foreach (var group in entries.Where(e => e.ItemId is not null).GroupBy(e => e.Host))
        {
            var before = HostCargo(group.Key);
            var after = before;
            foreach (var e in group) after = CargoEdit.RemoveWhole(after, e.ItemId!);
            if (!ReferenceEquals(after, before)) commands.Add(CargoCommand(group.Key, after));
        }

        if (commands.Count == 0) return;
        _stack.Push(_doc, commands.Count == 1 ? commands[0] : new CompositeCommand(commands));
        Refresh();
    }

    private static string Describe(string name, int count) =>
        count > 1 ? $"all ×{count} of “{name}”" : $"“{name}”";

    private static IReadOnlyList<CargoItem> HostCargo(RenderItem host) =>
        host.Placement is { } p ? p.Cargo : host.Loose?.Cargo ?? [];

    private static IDocCommand CargoCommand(RenderItem host, IReadOnlyList<CargoItem> next) =>
        host.Placement is { } p
            ? new SetCargoCommand(p, p.Cargo, next)
            : new SetLooseCargoCommand(host.Loose!, host.Loose!.Cargo, next);

    // ---- the trimmings ----

    private string ScopeLabel => _zone is null ? "whole ship" : $"zone “{ZoneLabel(_zone)}”";

    /// <summary>What a bulk action would actually reach. The filter narrows the list, so a row's count is what the
    /// filter left rather than what the scope holds, and a removal that said "the whole ship" while acting on a
    /// filtered subset would be describing the wrong operation.</summary>
    private string ReachLabel =>
        _filter.Trim().Length == 0 ? ScopeLabel : $"{ScopeLabel}, matching “{_filter.Trim()}”";

    private static string ZoneLabel(ShipZone z) => string.IsNullOrWhiteSpace(z.Name) ? "unnamed zone" : z.Name;

    private UIElement Buttons()
    {
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var copy = new Button { Content = "Copy list", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) => CopyToClipboard();
        var close = new Button { Content = "Close", Padding = new Thickness(16, 4, 16, 4), IsCancel = true };
        close.Click += (_, _) => Close();
        buttons.Children.Add(copy);
        buttons.Children.Add(close);
        return buttons;
    }

    /// <summary>The manifest as plain text, every row expanded whatever the window is showing — a pasted list is
    /// read once and cannot be clicked open.</summary>
    private void CopyToClipboard()
    {
        var sb = new StringBuilder();
        // The same rows and the same figures the window is showing, filter included, so a pasted list matches what
        // was on screen when it was taken.
        var lines = Filtered();
        sb.AppendLine($"Item manifest ({ScopeLabel})");
        sb.AppendLine($"{lines.Sum(l => l.Count)} items, {lines.Count} types, "
            + $"{lines.Sum(l => l.OnDeckCount)} on the decks, {lines.Sum(l => l.ContainedCount)} in containers, "
            + Credits(lines.Sum(l => l.Value)));
        sb.AppendLine();
        if (_byLocation)
            // Indented, because the nesting IS the content of this view: flattening it on the way to the clipboard
            // would paste the list the other grouping already gives.
            foreach (var node in ItemManifest.ByLocation(lines)) AppendNode(sb, node, 0);
        else
            foreach (var line in lines)
            {
                sb.AppendLine($"{line.Count,6}x  {line.Friendly,-48}  {Credits(line.Value),12}");
                foreach (var e in line.Entries)
                    sb.AppendLine($"          {(e.Count > 1 ? $"x{e.Count} " : "")}{e.Name} — {e.Where}"
                        + (e.Intrinsic ? " (part of it)" : "") + $"  {Credits(e.Value)}");
            }
        try { Clipboard.SetText(sb.ToString()); } catch { /* clipboard may be locked by another app */ }
    }

    /// <summary>One location-tree node and everything under it, as indented text.</summary>
    private static void AppendNode(StringBuilder sb, ManifestNode node, int depth)
    {
        var indent = new string(' ', depth * 2);
        var own = node.OwnCount > 1 ? $" x{node.OwnCount}" : "";
        sb.AppendLine($"{indent}- {node.Label}{own}  ({node.Count} item{Plural(node.Count)}, {Credits(node.Value)})");
        foreach (var child in node.Children) AppendNode(sb, child, depth + 1);
    }

    private static string Plural(int n) => n == 1 ? "" : "s";
}
