using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// Every weapon in the design, and the firing group each one answers to (#51).
///
/// <para>This is the window the game does not have. In game the only editor is the Weapons MFD, which shows one
/// weapon at a time and steps its group with a button that wraps at nine; its single bulk action copies one
/// weapon's whole page onto <b>every weapon of the same type</b> aboard. So "these three cannons in group 2, those
/// two in group 4" cannot be said at all, and a ship that spawns with its stock groups has to be re-grouped by
/// hand at the console before it is any use in a fight.</para>
///
/// <para>Weapons are sectioned by which way they bear, because that is the fact a grouping lives or dies on: a
/// group holding a fore beam and an aft one can only ever half-fire at anything. Both launcher families cover the
/// whole circle and so sit under a heading of their own rather than being given a side they do not have (see
/// <see cref="WeaponPanel.Facing"/>).</para>
///
/// <para>Edits land immediately and undoably, like the inspector's own controls rather than like the nav-console
/// arranger's Apply/Cancel: there is no arrangement to be resolved here, each row is independent, and a bulk set
/// is pushed as one <see cref="CompositeCommand"/> so it undoes in one step.</para>
/// </summary>
public sealed class WeaponsWindow : Window
{
    private static Brush Ink => ThemeManager.Ink;
    private static Brush Dim => ThemeManager.Dim;

    /// <summary>Column widths, in pixels. Fixed rather than shared-size-scoped: every row is built the same way
    /// and a scope would re-measure the whole list on each rebuild.</summary>
    private const double ColPick = 26, ColName = 210, ColWhat = 190, ColGroup = 110, ColMode = 150, ColTarget = 190;

    private readonly Catalog _catalog;
    private readonly ShipDocument _doc;
    private readonly CommandStack _stack;

    private readonly StackPanel _list = new();
    private readonly TextBlock _tally = new();
    private readonly TextBlock _picked = new();
    private readonly ComboBox _bulkGroup = new();
    private readonly ComboBox _bulkMode = new();
    private readonly ComboBox _bulkTarget = new();

    /// <summary>One weapon in the design, and the checkbox that says whether a bulk action reaches it. Selection
    /// is held by placement id rather than by row, so it survives the rebuild each edit triggers.</summary>
    private sealed record Row(Placement Part, PartDef Def, string Label);

    private readonly HashSet<Guid> _checked = [];
    private bool _loading;

    /// <summary>The laid-out content, for the offscreen preview render (<c>--weaponsmoke</c>).</summary>
    internal Panel PreviewContent => (Panel)Content;

    public WeaponsWindow(Catalog catalog, ShipDocument doc, CommandStack stack)
    {
        _catalog = catalog;
        _doc = doc;
        _stack = stack;

        Title = "Firing groups";
        SizeToContent = SizeToContent.Height;
        Width = ColPick + ColName + ColWhat + ColGroup + ColMode + ColTarget + 80;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        MinWidth = 640;
        MaxHeight = 900;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.WindowBg;

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock
        {
            Text = "A weapon fires when its group's key is pressed at the nav console. Groups are numbered the way "
                   + "the console numbers them, 1 to 9, and a weapon marked \"stock\" is at the group its own part "
                   + "ships with. Tick weapons to set several at once.",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        });

        _tally.Foreground = Ink;
        _tally.FontSize = 12;
        _tally.TextWrapping = TextWrapping.Wrap;
        _tally.Margin = new Thickness(0, 0, 0, 8);
        root.Children.Add(_tally);

        root.Children.Add(new Border
        {
            BorderBrush = ThemeManager.PanelBorder, BorderThickness = new Thickness(1),
            Background = ThemeManager.FieldBg, Padding = new Thickness(8),
            MaxHeight = 520,
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list },
        });

        root.Children.Add(BuildBulkBar());

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var close = new Button { Content = "Close", Padding = new Thickness(18, 4, 18, 4), IsDefault = true, IsCancel = true };
        close.Click += (_, _) => Close();
        buttons.Children.Add(close);
        root.Children.Add(buttons);

        Content = root;
        Render();
    }

    /// <summary>Whether the design has anything for this window to show. Checked before opening, so a design with
    /// no weapons gets a sentence rather than an empty board.</summary>
    public static bool HasWeapons(ShipDocument doc, Catalog catalog) =>
        doc.Placements.Any(p => WeaponPanel.IsWeapon(catalog.Lookup(p.DefName)));

    // ---- the rows ----

    /// <summary>Every weapon in the design, sectioned by bearing and then ordered by where it sits, so two cannons
    /// on the same blister are next to each other in the list as well as on the plan.</summary>
    private IEnumerable<IGrouping<WeaponFacing, Row>> Sections() =>
        _doc.Placements
            .Select(p => (Part: p, Def: _catalog.Lookup(p.DefName)))
            .Where(x => WeaponPanel.IsWeapon(x.Def))
            .Select(x => new Row(x.Part, x.Def!, Rename.Display(x.Part, x.Def)))
            .OrderBy(r => r.Part.Y).ThenBy(r => r.Part.X)
            .GroupBy(r => WeaponPanel.Facing(r.Def, r.Part.Rot))
            .OrderBy(g => (int)g.Key);

    private void Render()
    {
        _loading = true;
        try
        {
            _list.Children.Clear();
            var all = new List<Row>();

            foreach (var section in Sections())
            {
                _list.Children.Add(new TextBlock
                {
                    Text = WeaponPanel.FacingLabel(section.Key).ToUpperInvariant(),
                    Foreground = Dim, FontSize = 11, FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, _list.Children.Count == 0 ? 0 : 10, 0, 4),
                });
                foreach (var row in section)
                {
                    _list.Children.Add(BuildRow(row));
                    all.Add(row);
                }
            }

            // a selection can outlive the parts it named (a weapon deleted while this window was not looking)
            _checked.IntersectWith(all.Select(r => r.Part.Id));
            UpdateTally(all);
        }
        finally { _loading = false; }
    }

    private UIElement BuildRow(Row row)
    {
        var settings = WeaponPanel.Effective(row.Part.Weapon, row.Def);
        var stock = WeaponPanel.DefaultGroup(row.Def);

        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        foreach (var w in new[] { ColPick, ColName, ColWhat, ColGroup, ColMode, ColTarget })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });

        var pick = new CheckBox
        {
            IsChecked = _checked.Contains(row.Part.Id), VerticalAlignment = VerticalAlignment.Center,
            Foreground = Ink,
        };
        pick.Click += (_, _) =>
        {
            if (pick.IsChecked == true) _checked.Add(row.Part.Id); else _checked.Remove(row.Part.Id);
            UpdateTally(Sections().SelectMany(s => s).ToList());
        };
        Add(pick, 0);

        Add(new TextBlock
        {
            Text = row.Label, Foreground = Ink, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0),
            ToolTip = $"{row.Label} at {row.Part.X}, {row.Part.Y}",
        }, 1);

        Add(new TextBlock
        {
            Text = What(row.Def), Foreground = Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0),
        }, 2);

        var group = new ComboBox { Margin = new Thickness(0, 0, 8, 0), FontSize = 12 };
        foreach (var g in WeaponPanel.AllGroups)
            group.Items.Add(g == stock ? $"{WeaponPanel.ToDisplay(g)} (stock)" : WeaponPanel.ToDisplay(g).ToString());
        group.SelectedIndex = settings.Group ?? stock;
        group.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            Apply([row], s => s with { Group = group.SelectedIndex });
        };
        Add(group, 3);

        var mode = new ComboBox { Margin = new Thickness(0, 0, 8, 0), FontSize = 12 };
        mode.Items.Add("Automatic");
        mode.Items.Add("Manual");
        mode.SelectedIndex = settings.Manual ? 1 : 0;
        mode.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            Apply([row], s => s with { Manual = mode.SelectedIndex == 1 });
        };
        Add(mode, 4);

        if (WeaponPanel.OffersTargetMode(row.Def))
        {
            var target = new ComboBox { FontSize = 12 };
            target.Items.Add("Anything");
            target.Items.Add("Missiles + meteoroids");
            target.Items.Add("Ships only");
            target.SelectedIndex = (int)settings.TargetMode;
            target.SelectionChanged += (_, _) =>
            {
                if (_loading) return;
                Apply([row], s => s with { TargetMode = (PdcTargetMode)target.SelectedIndex });
            };
            Add(target, 5);
        }
        else
        {
            // not a cannon: the two target conds are only read down paths a cannon reaches, so there is nothing
            // to offer rather than a control that would do nothing
            Add(new TextBlock
            {
                Text = "—", Foreground = Dim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            }, 5);
        }

        return grid;

        void Add(UIElement child, int column)
        {
            Grid.SetColumn(child, column);
            grid.Children.Add(child);
        }
    }

    /// <summary>What a weapon is and how it reaches, in one line: the class, its arc, and how far that arc goes.
    /// The numbers are the def's, and they are here because they are what makes a grouping sensible.</summary>
    private static string What(PartDef def)
    {
        var cls = WeaponPanel.ClassLabel(WeaponPanel.Classify(def));
        if (WeaponPanel.IsOmnidirectional(def)) return $"{cls}, any bearing";
        var arc = WeaponPanel.ArcAngle(def).ToString("0.#");
        var range = WeaponPanel.ArcRange(def);
        return range > 0 ? $"{cls}, {arc}° to {(range / 1000).ToString("0.#")} km" : $"{cls}, {arc}° arc";
    }

    // ---- the bulk bar ----

    private UIElement BuildBulkBar()
    {
        var bar = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };

        _picked.Foreground = Dim;
        _picked.FontSize = 11;
        _picked.Margin = new Thickness(0, 0, 0, 6);
        bar.Children.Add(_picked);

        var line = new StackPanel { Orientation = Orientation.Horizontal };

        foreach (var g in WeaponPanel.AllGroups) _bulkGroup.Items.Add(WeaponPanel.ToDisplay(g).ToString());
        _bulkGroup.SelectedIndex = 0;
        _bulkGroup.Width = 60;
        _bulkGroup.FontSize = 12;
        line.Children.Add(Label("Set ticked to group"));
        line.Children.Add(_bulkGroup);
        line.Children.Add(Action("Apply", () => Apply(Ticked(), s => s with { Group = _bulkGroup.SelectedIndex })));

        _bulkMode.Items.Add("Automatic");
        _bulkMode.Items.Add("Manual");
        _bulkMode.SelectedIndex = 0;
        _bulkMode.Width = 110;
        _bulkMode.FontSize = 12;
        line.Children.Add(Label("Mode"));
        line.Children.Add(_bulkMode);
        line.Children.Add(Action("Apply", () => Apply(Ticked(), s => s with { Manual = _bulkMode.SelectedIndex == 1 })));

        bar.Children.Add(line);

        var line2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        _bulkTarget.Items.Add("Anything");
        _bulkTarget.Items.Add("Missiles + meteoroids");
        _bulkTarget.Items.Add("Ships only");
        _bulkTarget.SelectedIndex = 0;
        _bulkTarget.Width = 170;
        _bulkTarget.FontSize = 12;
        line2.Children.Add(Label("Cannon target select"));
        line2.Children.Add(_bulkTarget);
        line2.Children.Add(Action("Apply", () => Apply(
            Ticked().Where(r => WeaponPanel.OffersTargetMode(r.Def)).ToList(),
            s => s with { TargetMode = (PdcTargetMode)_bulkTarget.SelectedIndex })));

        line2.Children.Add(Action("Tick all", () =>
        {
            foreach (var r in Sections().SelectMany(s => s)) _checked.Add(r.Part.Id);
            Render();
        }));
        line2.Children.Add(Action("Tick none", () => { _checked.Clear(); Render(); }));
        line2.Children.Add(Action("Reset ticked to stock", () => Apply(Ticked(), _ => WeaponSettings.Default)));
        bar.Children.Add(line2);

        return bar;

        static TextBlock Label(string text) => new()
        {
            Text = text, Foreground = Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };

        static Button Action(string text, Action run)
        {
            var b = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 10, 0), FontSize = 12 };
            b.Click += (_, _) => run();
            return b;
        }
    }

    private IReadOnlyList<Row> Ticked() =>
        [.. Sections().SelectMany(s => s).Where(r => _checked.Contains(r.Part.Id))];

    /// <summary>
    /// Push a change over some weapons as one undo step. The transform runs against each weapon's <b>effective</b>
    /// page, so setting the mode on a mixed selection leaves each one's group where it was; and the result goes
    /// through <see cref="WeaponPanel.Authored"/> per part, so a weapon set to the group its own def ships with
    /// records nothing at all — which is what "Reset ticked to stock" is.
    /// </summary>
    private void Apply(IReadOnlyList<Row> rows, Func<WeaponSettings, WeaponSettings> change)
    {
        if (rows.Count == 0) return;

        var commands = new List<IDocCommand>();
        foreach (var row in rows)
        {
            var before = row.Part.Weapon;
            var after = WeaponPanel.Authored(change(WeaponPanel.Effective(before, row.Def)), row.Def);
            if (!Equals(before, after)) commands.Add(new SetWeaponSettingsCommand(row.Part, before, after));
        }
        if (commands.Count == 0) return;

        _stack.Push(_doc, commands.Count == 1 ? commands[0] : new CompositeCommand(commands));
        Render();
    }

    // ---- the tally ----

    /// <summary>
    /// What each group actually holds, which is the question the window exists to answer. An empty group is left
    /// out; a group with one weapon in it is worth seeing, because a key bound to a single cannon is usually a
    /// slip rather than a plan.
    /// </summary>
    private void UpdateTally(IReadOnlyList<Row> all)
    {
        var byGroup = all
            .GroupBy(r => WeaponPanel.Effective(r.Part.Weapon, r.Def).Group ?? WeaponPanel.DefaultGroup(r.Def))
            .OrderBy(g => g.Key)
            .Select(g => $"{WeaponPanel.ToDisplay(g.Key)}: {g.Count()}")
            .ToList();

        _tally.Text = all.Count == 0
            ? "This design carries no weapons."
            : $"{all.Count} weapon{(all.Count == 1 ? "" : "s")}. Groups in use — {string.Join(",  ", byGroup)}.";

        var ticked = _checked.Count;
        _picked.Text = ticked == 0
            ? "Nothing ticked. Tick a weapon to include it in the actions below."
            : $"{ticked} ticked.";
    }
}
