using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>Which question the docking window is answering. Both are docking compatibility, which is why they
/// share one window and one menu entry; they differ only in what the design is being measured against.</summary>
public enum DockingMode
{
    /// <summary>Against one ship you pick: the full airlock-by-airlock table.</summary>
    OneShip,
    /// <summary>Against the primary airlock of every ship in the install.</summary>
    EveryShip,
}

/// <summary>
/// Whether the design can actually hard-dock, in either of two readings.
///
/// <para><b>Against one ship</b>: pick a ship and get every one of its airlocks against every one of yours.
/// <b>Against every stock ship template</b>: run each of your airlocks against the primary airlock of every
/// ship template in the
/// install, which is the only honest answer to "will this dock with a primary in general" — the game has no
/// such rule, and only ever answers for two specific ships.</para>
///
/// <para>The design is always the <b>incoming</b> ship and the other is the receiver, which is the way round a
/// player meets it: you fly your design in. The game's own check is directional, so the roles are not
/// interchangeable and the window says which is which.</para>
///
/// <para>Selecting a refusal highlights the tiles of <i>your</i> design that are in the way, which is the part
/// that makes it actionable. The collision list the overlay returns is in the other ship's coordinates, so
/// <see cref="DockBlock.DocTile"/> is what gets drawn.</para>
///
/// <para><b>Vocabulary.</b> The game calls the item an <i>airlock</i> ("Primary Airlock", "Secondary Airlock")
/// and uses "docking port" only for the act and the zone. The code's own name for it is a port
/// (<c>aDockingPorts</c>, <c>DockingPortDTO</c>) and the Core types keep that; everything a player reads here
/// says airlock.</para>
/// </summary>
public sealed class DockingWindow : Window
{
    private static Brush Ink => ThemeManager.Ink;
    private static Brush Dim => ThemeManager.Dim;
    private static Brush Accent => ThemeManager.Accent;
    private static Brush Good => ThemeManager.Good;
    private static Brush Bad => ThemeManager.Bad;
    private static Brush Warn => ThemeManager.Warn;

    private readonly Func<DockShip?> _currentDesign;
    private readonly Action<IReadOnlyList<DockPart>> _showGhost;
    private readonly DataIndex? _index;
    private readonly Catalog _catalog;
    private readonly Action<IReadOnlyList<(int X, int Y)>> _highlight;
    private readonly Func<Window, Task<DockShip?>> _pickOther;

    private readonly ContentControl _host = new();
    private readonly ToggleButton _oneShipTab = new() { Content = "Against one ship", Padding = new Thickness(12, 4, 12, 4) };
    private readonly ToggleButton _everyShipTab = new() { Content = "Against every stock ship template", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(6, 0, 0, 0) };
    private readonly ContentControl _buttons = new();
    private readonly CancellationTokenSource _cancel = new();

    private DockShip _design;
    private DockingMode _mode = DockingMode.OneShip;
    private DockReport? _report;
    private (string Receiver, string Incoming)? _selected;
    private bool _stale;
    private DockSurveyResult? _survey;
    private ProgressBar? _bar;
    private TextBlock? _barLabel;
    private bool _running;

    public DockingWindow(DockShip design, Func<DockShip?> currentDesign, Catalog catalog, DataIndex? index,
        Action<IReadOnlyList<(int X, int Y)>> highlight, Action<IReadOnlyList<DockPart>> showGhost,
        Func<Window, Task<DockShip?>> pickOther)
    {
        _design = design;
        _currentDesign = currentDesign;
        _showGhost = showGhost;
        _catalog = catalog;
        _index = index;
        _highlight = highlight;
        _pickOther = pickOther;

        Title = "Docking Compatibility";
        // Only the HEIGHT sizes to content. A declared height left most of the window empty on the common case
        // of one or two airlocks; sizing to content means each pane is as tall as its data and no taller. The
        // width stays declared so the wrapping prose is bounded rather than bidding for it, and MaxHeight is on
        // the window with the body scrolling under a docked button bar (CONVENTIONS.md).
        Width = 500;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 720;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.WindowBg;

        // Two ToggleButtons and a ContentControl rather than a TabControl: it is the repo's own pattern
        // (MainWindow.AddPaletteTab), and it gets the Fluent checked accent without a custom style.
        _oneShipTab.Click += (_, _) => SetMode(DockingMode.OneShip);
        _everyShipTab.Click += (_, _) => SetMode(DockingMode.EveryShip);

        var strip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 14, 18, 0) };
        strip.Children.Add(_oneShipTab);
        strip.Children.Add(_everyShipTab);

        var root = new DockPanel();
        DockPanel.SetDock(strip, Dock.Top);
        root.Children.Add(strip);
        // Docked rather than appended to the body, so the actions stay put instead of scrolling away under a
        // long survey. A DockPanel and not a StackPanel for the same reason (CONVENTIONS.md).
        DockPanel.SetDock(_buttons, Dock.Bottom);
        root.Children.Add(_buttons);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _host });
        Content = root;

        Closed += (_, _) => { _cancel.Cancel(); _highlight([]); _showGhost([]); };
        SetMode(_mode);
    }

    /// <summary>Install a computed report without going through the picker. Exists for the offscreen layout
    /// preview; the app always arrives here through Compare with.</summary>
    internal void ShowReportForPreview(DockReport report)
    {
        _report = report;
        Rebuild();
    }

    /// <summary>Point the window at one of its two readings. Results on the other side are kept: they belong to
    /// the design rather than to which question is on screen.</summary>
    public void SetMode(DockingMode mode)
    {
        _mode = mode;
        _oneShipTab.IsChecked = mode == DockingMode.OneShip;
        _everyShipTab.IsChecked = mode == DockingMode.EveryShip;
        _everyShipTab.IsEnabled = _index is not null;
        Rebuild();
    }

    private void Rebuild()
    {
        _bar = null;
        _barLabel = null;

        var body = new StackPanel { Margin = new Thickness(18, 12, 18, 18) };
        if (_stale) body.Children.Add(StaleBar());

        if (_design.Ports.Count == 0)
        {
            body.Children.Add(Label("No airlock", Accent, 26, FontWeights.Bold));
            body.Children.Add(Note("This design carries no installed airlock, so it can never hard-dock. The "
                + "game collects them through TIsDockSysInstalled (IsDockSys plus IsInstalled); without one, "
                + "Ship.aDocksys stays empty."));
        }
        else if (_mode == DockingMode.OneShip) BuildOneShip(body);
        else BuildEveryShip(body);

        _buttons.Content = ButtonBar();
        _host.Content = body;
    }

    // ---- against one ship ----

    private void BuildOneShip(Panel body)
    {
        if (_report is not { } report)
        {
            body.Children.Add(Label($"{_design.Ports.Count} airlock{Plural(_design.Ports.Count)} on this design",
                Accent, 24, FontWeights.Bold));
            foreach (var p in _design.Ports) body.Children.Add(AirlockLine(p));
            body.Children.Add(Note(
                "Docking is geometric rather than a matter of airlock type: the other ship is turned so its "
                + "airlock faces yours, stepped one tile off, and refused if any part of either hull comes "
                + "within a tile of the other. Primary and Secondary behave identically here. Pick a ship to "
                + "compare against."));
            return;
        }

        var mated = report.Pairs.Count(p => p.Mates);
        body.Children.Add(Label(
            mated > 0 ? $"Docks on {mated} of {report.Pairs.Count} airlock pair{Plural(report.Pairs.Count)}" : "Cannot dock",
            mated > 0 ? Good : Bad, 22, FontWeights.Bold));
        body.Children.Add(Note($"“{_design.Name}” flying in to “{report.Receiver.Name}”."));
        body.Children.Add(BuildPairList(report));
    }

    /// <summary>
    /// One row per airlock pair, rather than a matrix.
    ///
    /// <para>The matrix needed a legend saying which axis was whose, and a legend that has to explain the thing
    /// under it is the thing being wrong. A row reads left to right on its own — their airlock, an arrow, yours,
    /// the verdict — and it costs no width, where a matrix of star-sized columns spread three buttons across the
    /// whole window. Ships carry one to three airlocks each, so the list is short by construction.</para>
    /// </summary>
    private UIElement BuildPairList(DockReport report)
    {
        var grid = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                        // theirs
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                        // arrow
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                        // yours
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });   // verdict
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                        // action

        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Add(grid, Caption(report.Receiver.Name), 0, 0);
        Add(grid, Caption(_design.Name), 0, 2);

        var row = 1;
        foreach (var mate in report.Pairs)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var selected = _selected is { } sel
                && sel.Receiver == mate.ReceiverPort.ItemId && sel.Incoming == mate.IncomingPort.ItemId;
            if (selected)
            {
                // A band behind the whole row, added before the cells so they sit on top of it. This is what
                // says which pair the ship currently ghosted on the planner belongs to.
                var band = new Border { Background = ThemeManager.PanelBg, CornerRadius = new CornerRadius(3) };
                Grid.SetColumnSpan(band, 5);
                Add(grid, band, row, 0);
            }

            Add(grid, Label(PairName(mate.ReceiverPort), Ink, 12, FontWeights.Normal), row, 0);
            Add(grid, Label("→", Dim, 12, FontWeights.Normal), row, 1);
            Add(grid, Label(PairName(mate.IncomingPort), Ink, 12, FontWeights.Normal), row, 2);
            Add(grid, Label(
                mate.Mates ? "Docks" : $"Blocked · {mate.Blocks.Count} tile{Plural(mate.Blocks.Count)}",
                mate.Mates ? Good : Bad, 12, FontWeights.Bold), row, 3);
            Add(grid, ShowButton(mate), row, 4);
            row++;
        }
        return grid;
    }

    /// <summary>The action on a row. Every pair has one, blocked ones included: a refused pose is the picture
    /// worth seeing, because it shows the two hulls overlapping and exactly where.</summary>
    private UIElement ShowButton(DockMate mate)
    {
        var button = new Button
        {
            Content = "Show on Planner",
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(12, 2, 0, 2),
            IsEnabled = mate.Pose is not null,
            ToolTip = mate.Mates
                ? "Draw the two ships docked at this pose."
                : "Draw the two ships at this pose, and highlight the tiles of your design that are in the way.",
        };
        button.Click += (_, _) => Select(mate);
        return button;
    }

    /// <summary>Show one pair: the other ship posed against yours, and the tiles of yours that refuse it.</summary>
    private void Select(DockMate mate)
    {
        if (_report is not { } report) return;
        _selected = (mate.ReceiverPort.ItemId, mate.IncomingPort.ItemId);
        _highlight(BlockedTiles(mate));
        _showGhost(mate.Pose is { } pose ? DockPose.ReceiverParts(report.Receiver, _design, pose) : []);
        Rebuild();
    }

    // ---- against every ship ----

    private void BuildEveryShip(Panel body)
    {
        if (_running)
        {
            _barLabel = Label("Checking…", Accent, 22, FontWeights.Bold);
            body.Children.Add(_barLabel);
            _bar = new ProgressBar { Minimum = 0, Maximum = 1, Value = 0, Height = 6, Margin = new Thickness(0, 8, 0, 8) };
            body.Children.Add(_bar);
            UpdateProgress(0, 0);
            return;
        }

        if (_survey is not { } survey)
        {
            body.Children.Add(Label(
                $"{_design.Ports.Count} airlock{Plural(_design.Ports.Count)} to check", Accent, 24, FontWeights.Bold));
            body.Children.Add(Note(
                "A primary airlock is all but guaranteed to dock with another ship's primary, because the build "
                + "rules keep the space ahead of it clear. A secondary has no such guarantee. This reads every "
                + "ship template in your install, the ones your mods add included, and tries each of your "
                + "airlocks against that ship's primary. Nothing is imported and your design is not touched."));
            return;
        }

        body.Children.Add(Label(
            $"{survey.Ships.Count} ship{Plural(survey.Ships.Count)} with a primary airlock", Accent, 22, FontWeights.Bold));
        body.Children.Add(Note(
            "Your design is the incoming ship in every row. An airlock that takes every primary is as good as a "
            + "primary of your own; one that takes only some is the case worth knowing about, and the ships that "
            + "refuse it are listed under it."
            + (survey.Skipped > 0 ? $" {survey.Skipped} ship(s) carry no primary airlock and were not measured." : "")));

        for (var i = 0; i < survey.Ports.Count; i++) body.Children.Add(AirlockSection(survey, i));
    }

    private UIElement AirlockSection(DockSurveyResult survey, int index)
    {
        var port = survey.Ports[index];
        var mates = survey.MateCount(index);
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };

        panel.Children.Add(Label(AirlockName(port), Ink, 13, FontWeights.Bold));
        panel.Children.Add(Label($"Docks with {mates} of {survey.Ships.Count}",
            mates == survey.Ships.Count ? Good : mates == 0 ? Bad : Warn, 18, FontWeights.Bold));

        var refusals = survey.Ships.Where(s => !s.Mated[index]).ToList();
        if (refusals.Count == 0)
        {
            panel.Children.Add(Note("Every ship with a primary airlock accepts this one."));
            return panel;
        }

        panel.Children.Add(Note($"Refused by {refusals.Count}. Select one to highlight the tiles of your design "
            + "that are in the way."));

        var list = new ListBox
        {
            MaxHeight = 160, Margin = new Thickness(0, 4, 0, 0),
            Background = ThemeManager.FieldBg, BorderBrush = ThemeManager.PanelBorder,
            ItemsSource = refusals.Select(s => s.ShipName).ToList(),
        };
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedIndex < 0 || list.SelectedIndex >= refusals.Count) return;
            var (mate, receiver) = DockSurvey.Explain(_design, refusals[list.SelectedIndex], port, _catalog);
            _highlight(BlockedTiles(mate));
            _showGhost(receiver is not null && mate.Pose is { } pose
                ? DockPose.ReceiverParts(receiver, _design, pose)
                : []);
        };
        panel.Children.Add(list);
        return panel;
    }

    private void UpdateProgress(int done, int total)
    {
        if (_bar is not null) { _bar.Maximum = Math.Max(1, total); _bar.Value = done; }
        if (_barLabel is not null) _barLabel.Text = total > 0 ? $"Checking… {done} of {total}" : "Checking…";
    }

    private async Task RunSurvey()
    {
        if (_index is null) return;

        _running = true;
        _highlight([]);
        Rebuild();

        // Reported through IProgress so the handler runs back on the UI thread. It updates the bar in place
        // rather than rebuilding: a rebuild per ship would recreate every control ~200 times over.
        var progress = new Progress<(int Done, int Total)>(p => { if (_running) UpdateProgress(p.Done, p.Total); });
        var (design, index, catalog, token) = (_design, _index, _catalog, _cancel.Token);

        try
        {
            _survey = await Ui.OffThread(SweepWork(design, index, catalog, progress, token), token);
        }
        catch (OperationCanceledException)
        {
            return;   // the window is closing
        }
        catch (Exception ex)
        {
            _running = false;
            Rebuild();
            Dlg.Error(this, "Docking Compatibility", "Couldn't finish the check.\n\n" + ex.Message);
            return;
        }

        _running = false;
        Rebuild();
    }

    /// <summary>
    /// The sweep as a delegate that closes over nothing UI-owned.
    ///
    /// <para><b>Static, and built here rather than inline, for a reason that cost a release note.</b> Every
    /// lambda in one method shares a single compiler-generated closure, so writing this inline next to a
    /// progress callback that touches the window put the window itself in the pool thread's captures.
    /// <see cref="Ui.OffThread"/> caught it and refused, which is what the guard is for, but only at runtime and
    /// only when someone ran the survey. A static method has no closure to share, and being static and internal
    /// is also what lets a test prove it rather than waiting for a bug report.</para>
    ///
    /// <para>Progress goes out through <see cref="IProgress{T}"/> so the handler lands back on the thread that
    /// created it, which is the UI one.</para>
    /// </summary>
    internal static Func<DockSurveyResult> SweepWork(DockShip design, DataIndex index, Catalog catalog,
        IProgress<(int Done, int Total)> progress, CancellationToken token) =>
        () => DockSurvey.Run(design, index, catalog, (done, total) => progress.Report((done, total)), token);

    // ---- chrome ----

    /// <summary>The actions, rebuilt per mode and docked at the bottom of the window so a long survey scrolls
    /// under them rather than pushing them off.</summary>
    private UIElement ButtonBar()
    {
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(18, 0, 18, 16),
        };

        if (_mode == DockingMode.OneShip)
        {
            var pick = new Button
            {
                Content = _report is null ? "Compare with…" : "Compare with another…",
                Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 8, 0),
                IsEnabled = _design.Ports.Count > 0,
            };
            pick.Click += async (_, _) =>
            {
                if (await _pickOther(this) is not { } other) return;
                _report = DockMating.Cross(other, _design);
                _highlight([]);
                Rebuild();
            };
            bar.Children.Add(pick);
        }
        else
        {
            var run = new Button
            {
                Content = _survey is null ? "Check against every template" : "Check again",
                Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 8, 0),
                IsEnabled = !_running && _design.Ports.Count > 0 && _index is not null,
            };
            run.Click += async (_, _) => await RunSurvey();
            bar.Children.Add(run);
        }

        // Only offered when there is something showing, so it is not a permanently dead button.
        if (_selected is not null)
        {
            var clear = new Button { Content = "Clear", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 8, 0) };
            clear.Click += (_, _) =>
            {
                _selected = null;
                _highlight([]);
                _showGhost([]);
                Rebuild();
            };
            bar.Children.Add(clear);
        }

        var close = new Button { Content = "Close", Padding = new Thickness(12, 3, 12, 3), IsCancel = true };
        close.Click += (_, _) => Close();
        bar.Children.Add(close);

        return bar;
    }

    /// <summary>
    /// The design changed under the window, so what is on screen measured a ship that no longer exists.
    ///
    /// <para>The pose is deliberately <b>left where it was</b> rather than recomputed. Editing to clear a
    /// blockage is the whole reason the ghost is on the canvas, and a ghost that jumped every time you moved the
    /// airlock would be moving the target you are aiming at. Re-run when you want the new answer, exactly as the
    /// Ship Rating report works (see <see cref="ReportWindow.MarkStale"/>).</para>
    /// </summary>
    public void MarkStale()
    {
        if (_stale || (_report is null && _survey is null)) return;
        _stale = true;
        Rebuild();
    }

    private UIElement StaleBar()
    {
        var bar = new DockPanel
        {
            Background = ThemeManager.PanelBg, Margin = new Thickness(0, 0, 0, 10),
            LastChildFill = true,
        };
        var rerun = new Button { Content = "Re-run", Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(8, 6, 8, 6) };
        rerun.Click += (_, _) => Rerun();
        DockPanel.SetDock(rerun, Dock.Right);
        bar.Children.Add(rerun);
        bar.Children.Add(new TextBlock
        {
            Text = "The design has changed since this was measured. The ghosted ship is still at the old pose.",
            Foreground = Warn, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10, 8, 4, 8), VerticalAlignment = VerticalAlignment.Center,
        });
        return bar;
    }

    /// <summary>Measure the design as it stands now, against whatever it was last compared with.</summary>
    private void Rerun()
    {
        if (_currentDesign() is not { } fresh) return;
        _design = fresh;
        _stale = false;

        // The survey is against 162 ships and takes seconds; it is dropped rather than silently re-run, and its
        // button comes back reading "Check against every ship" again.
        _survey = null;

        if (_report is { } report) _report = DockMating.Cross(report.Receiver, _design);

        // The airlock the selection named may not exist any more. Re-select it when it does, so a fix-and-re-run
        // loop keeps showing the pair you were working on; otherwise drop the ghost rather than leave a pose
        // anchored to an airlock that has gone.
        var keep = _selected is { } sel && _report is { } fresh2
            ? fresh2.Pairs.FirstOrDefault(p => p.ReceiverPort.ItemId == sel.Receiver
                                            && p.IncomingPort.ItemId == sel.Incoming)
            : null;
        _selected = null;
        _highlight([]);
        _showGhost([]);
        if (keep is not null) Select(keep);
        else Rebuild();
    }

    private static IReadOnlyList<(int X, int Y)> BlockedTiles(DockMate mate) =>
        [.. mate.Blocks.Where(b => b.DocTile is not null).Select(b => b.DocTile!.Value).Distinct()];

    private UIElement AirlockLine(DockPort port) => new TextBlock
    {
        Text = AirlockName(port), Foreground = Dim, FontSize = 12, Margin = new Thickness(0, 1, 0, 1),
    };

    private static string AirlockName(DockPort port) =>
        $"{port.Class} airlock at ({port.DocTile.X},{port.DocTile.Y})";

    /// <summary>A row's airlock: its class and the tile it sits on, on one line.</summary>
    private static string PairName(DockPort port) => $"{port.Class} ({port.DocTile.X},{port.DocTile.Y})";

    /// <summary>A small dim column heading. Upper case because it labels a column rather than naming a thing,
    /// which is how the other reports already mark their section headers. Trimmed rather than wrapped: a ship
    /// name is data, and a long one would otherwise set this column's width (CONVENTIONS.md).</summary>
    private static TextBlock Caption(string text) => new()
    {
        Text = text.ToUpperInvariant(), Foreground = ThemeManager.Dim, FontSize = 10, FontWeight = FontWeights.Bold,
        Margin = new Thickness(0, 0, 6, 5), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 190,
    };

    private static void Add(Grid grid, UIElement child, int row, int col)
    {
        Grid.SetRow(child, row);
        Grid.SetColumn(child, col);
        grid.Children.Add(child);
    }

    internal static TextBlock Label(string text, Brush brush, double size, FontWeight weight) => new()
    {
        Text = text, Foreground = brush, FontSize = size, FontWeight = weight,
        Margin = new Thickness(0, 2, 6, 2), VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Prose whose length the data decides wraps (CONVENTIONS.md). This window has a declared width, so
    /// the wrap is all that is needed; nothing here is bidding for the window's size.</summary>
    internal static TextBlock Note(string text) => new()
    {
        Text = text, Foreground = ThemeManager.Dim, FontSize = 11,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 2),
    };

    internal static string Plural(int n) => n == 1 ? "" : "s";
}

/// <summary>Pick one line out of a short list. The open-tab route has nothing to show but the tab names, so the
/// two-line browsers the file and save routes use would be mostly empty rows.</summary>
public sealed class ListPickDialog : Window
{
    private readonly ListBox _list;

    /// <summary>The chosen index into the list passed in, or null if the user backed out.</summary>
    public int? SelectedIndex { get; private set; }

    public ListPickDialog(string title, string note, IReadOnlyList<string> items)
    {
        Title = title;
        Width = 420; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.WindowBg;

        var root = new DockPanel { Margin = new Thickness(16) };

        var noteBlock = DockingWindow.Note(note);
        DockPanel.SetDock(noteBlock, Dock.Top);
        root.Children.Add(noteBlock);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var ok = new Button { Content = "Choose", Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Cancel", Padding = new Thickness(16, 4, 16, 4), IsCancel = true });
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        // The list is as long as the data makes it, so it scrolls rather than growing the window past the
        // screen (CONVENTIONS.md on a window that sizes to its content).
        _list = new ListBox
        {
            ItemsSource = items, SelectedIndex = 0, MaxHeight = 260,
            Background = ThemeManager.FieldBg, BorderBrush = ThemeManager.PanelBorder,
        };
        _list.MouseDoubleClick += (_, _) => Accept();
        root.Children.Add(_list);

        Content = root;
    }

    private void Accept()
    {
        if (_list.SelectedIndex < 0) return;
        SelectedIndex = _list.SelectedIndex;
        DialogResult = true;
    }
}
