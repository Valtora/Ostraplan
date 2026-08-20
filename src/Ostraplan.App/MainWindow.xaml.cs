using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Ostraplan.App.Wizard;
using Ostraplan.Core;

namespace Ostraplan.App;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private SettingsDialog? _settingsDialog;   // the open Settings window, so a folder change can refresh what it shows
    // Velopack self-update. Null for a copy the installer doesn't manage (dev / dotnet-run /
    // bare exe) — the update affordance simply never appears there. A downloaded, ready-to-apply
    // update is parked in _pendingUpdate until the user clicks Restart (see CheckForUpdateAsync).
    private readonly VeloUpdate? _updater = VeloUpdate.Create();
    private Velopack.UpdateInfo? _pendingUpdate;
    private GameEnv? _env;
    private DataIndex? _index;
    private Catalog? _catalog;
    private SpriteCache? _sprites;   // shared with the canvas; also feeds the inventory viewer
    private List<PartVM> _allParts = [];
    private readonly List<ListBox> _paletteLists = [];
    // The ★ quick-access tab: Favorites (top) + Recent (below), each its own list wired into _paletteLists so
    // selection-sync and arming treat them like any other palette list. Headers/empty-state toggle in RefreshQuickLists.
    private ListBox? _favList, _recentList;
    private TextBlock? _favHeader, _recentHeader, _quickEmpty;
    private bool _syncingPalette;
    private IReadOnlyList<RoomSpecDef>? _roomSpecs;   // lazily loaded once for the Ship Rating / Diagnostics analyses
    private bool _analysing;                          // one gate for both on-demand analyses (each freezes the live doc)
    private FreezeGate _freeze = null!;               // raised while an off-thread read of the LIVE _doc is in flight — see FreezeDoc
    // The clipboard is deliberately app-wide rather than per document: copying in one tab and pasting into another
    // is most of the reason for having more than one open (the container renames on discussion #33 are the case
    // that asked for it).
    private List<(string Def, int X, int Y, int Rot, IReadOnlyList<CargoItem> Cargo)> _clip = [];   // copied selection, relative to its top-left (with container contents)
    // The copied selection's original top-left. Only a last resort for a paste: the canvas answers where the cursor
    // is, and this stands in for the one case it cannot, a canvas that has not been laid out yet.
    private (int X, int Y) _clipOrigin;
    private readonly DispatcherTimer _scanTimer;      // debounces the (now off-thread) problem scan
    private CancellationTokenSource? _scanCts;        // cancels a superseded scan
    private readonly DispatcherTimer _autoSaveTimer;  // opt-in rotating snapshots of the open designs (see RunAutoSave)
    private bool _autoSaveWarned;                     // a failing auto-save says so once, then only logs — see RunAutoSave

    // ---- the open documents ----

    /// <summary>Every open design, in tab order. Never empty once the window is constructed: closing the last tab
    /// is refused rather than leaving the editor with nothing in it.</summary>
    private readonly List<DocumentSession> _sessions = [];

    /// <summary>The design the chrome is pointed at. Assigned by <see cref="ActivateSession"/>, whose first call is
    /// in the constructor, before anything below can be read.</summary>
    private DocumentSession _active = null!;

    // Read-only views of the two above, and the tab-management methods below, are internal rather than private so
    // the tab bookkeeping can be tested on the real window (see MainWindowTabsTests). Nothing outside the tests uses
    // them, and neither exposes anything a caller could set.
    internal IReadOnlyList<DocumentSession> OpenSessions => _sessions;
    internal DocumentSession ActiveSession => _active;

    // The per-document state, forwarded to the active session. These were plain fields before tabs, read and
    // written from several hundred places in this file; keeping their names and shapes is what let a second
    // document arrive without touching any of those call sites. See DocumentSession for what lives where.
    private ShipCanvas Board => _active.Board;
    private CommandStack _stack => _active.Stack;
    private ShipDocument? _doc { get => _active.Doc; set => _active.Doc = value; }
    private OplanMeta _meta { get => _active.Meta; set => _active.Meta = value; }
    private SaveShipContext? _saveContext { get => _active.SaveContext; set => _active.SaveContext = value; }
    private bool _stateDirty { get => _active.StateDirty; set => _active.StateDirty = value; }
    private IReadOnlyList<OplanPart> _unresolvedParts { get => _active.UnresolvedParts; set => _active.UnresolvedParts = value; }
    private List<Problem> _lastProblems { get => _active.LastProblems; set => _active.LastProblems = value; }
    private RatingReportWindow? _ratingReport { get => _active.RatingReport; set => _active.RatingReport = value; }
    private DiagnosticsWindow? _diagnosticsReport { get => _active.DiagnosticsReport; set => _active.DiagnosticsReport = value; }
    private FlightWindow? _flightReport { get => _active.FlightReport; set => _active.FlightReport = value; }


    public MainWindow()
    {
        InitializeComponent();

        AuditLog.Session(AppVersion);   // open a new section in the on-disk activity trail

        // The whole editing surface goes dead while an engine reads the live document off-thread (FreezeDoc). An open
        // report window is an edit route too now that it is modeless — its dead-weight box writes to the live
        // document — so it goes dead with the rest of them rather than being left as the one way in. Every open
        // design's reports, not just the analysed one's: the freeze disables the chrome for all of them at once.
        _freeze = new FreezeGate(frozen =>
        {
            Chrome.IsEnabled = !frozen;
            foreach (var s in _sessions)
            {
                if (s.RatingReport is not null) s.RatingReport.IsEnabled = !frozen;
                if (s.DiagnosticsReport is not null) s.DiagnosticsReport.IsEnabled = !frozen;
            }
        });

        _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _scanTimer.Tick += (_, _) => RunScan();

        _autoSaveTimer = new DispatcherTimer();
        _autoSaveTimer.Tick += (_, _) => RunAutoSave();
        RestartAutoSaveTimer();   // off unless the user has opted in

        // The tab the app opens on, built last because activating it touches the timers and the freeze gate above.
        // Its document arrives with the game data (LoadDataAsync); the session has to exist before that, because
        // every piece of per-document state below is read through it. ActivateSession seeds the toolbar highlights.
        ActivateSession(CreateSession());

        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        Deactivated += (_, _) => Board.ClearPanKeys();   // a KeyUp we never receive must not leave the view drifting
        Loaded += async (_, _) => await LoadDataAsync();
        Closing += (_, e) =>
        {
            if (!ConfirmDiscardEverything()) e.Cancel = true;
            else _settings.Save();
        };
    }

    // ---- document tabs ----

    /// <summary>
    /// Build a session and its canvas, and add both to the window. The canvas starts hidden: <see cref="ActivateSession"/>
    /// is what puts one on screen, and it is the only thing that should.
    ///
    /// <para>All of the canvas wiring lives here rather than in the constructor because there is a canvas per open
    /// design now. Every handler is written against <paramref name="board"/> rather than the <see cref="Board"/>
    /// shim, which for input events amounts to the same canvas (a hidden one is not hit-testable, so only the
    /// active one raises them) but says plainly which canvas it means.</para>
    /// </summary>
    internal DocumentSession CreateSession()
    {
        var board = new ShipCanvas
        {
            Visibility = Visibility.Hidden,
            Sprites = _sprites,   // null until the game data lands; LoadDataAsync fills the startup session's in
            // restore the "allow modded parts to break the law" toggle (default off)
            AllowModdedOverrides = _settings.AllowModdedOverrides,
            // Surfaces mode's persisted preferences. All three are visible in the Surfaces bar whenever the mode is
            // on, so a remembered choice explains itself rather than turning up as a brush that won't paint.
            SurfaceGhostOpacity = _settings.SurfaceGhostOpacity,
        };
        if (Enum.TryParse<SurfacePaintMode>(_settings.SurfacePaintMode, out var paintMode)) board.SetPaintMode(paintMode);
        if (Enum.TryParse<SurfaceFocus>(_settings.SurfaceFocus, out var focus)) board.SetLayerFocus(focus);

        board.StrokeCommitted += OnStrokeCommitted;
        board.MoveRequested += OnMoveRequested;
        board.PosesRequested += OnPosesRequested;
        board.SelectionChanged += UpdateInspector;
        board.LooseSelectionChanged += UpdateInspector;
        board.HoverChanged += cell => TxtCell.Text = cell is { } c ? $"tile {c.X}, {c.Y}" : "—";
        board.SelectionSizeChanged += size => TxtSel.Text = size is { } s ? $"{s.W} × {s.H} tiles" : "";
        board.ViewChanged += UpdateZoomText;
        board.Disarmed += ClearPaletteSelection;
        board.ContextMenuRequested += OnContextMenuRequested;
        board.BrushPicked += OnArmFromTile;   // Alt+LMB eyedropper
        board.ArmedChanged += () => { UpdateBrushText(); UpdateSurfaceBar(); };   // slot A is whatever is in hand, however it got there
        board.LooseContextMenuRequested += OnLooseContextMenuRequested;
        board.BandFilterRequested += OnBandFilterRequested;
        board.GhostReasonChanged += status => TxtGhost.Text =
            status is { } s ? (s.Advisory ? "⚠ places, but " : s.WillPlace ? "⚠ placing against the rules — " : "⛔ can't place here — ") + s.Reason
            : board.AirSelection.Count > 0 ? AirHint(board.AirSelection.Count)
            : "";
        board.AirSelectionChanged += n => TxtGhost.Text = n > 0 ? AirHint(n) : "";
        board.ZoneStrokeCommitted += OnZoneStrokeCommitted;
        board.ShowZonesChanged += OnShowZonesChanged;   // refresh the toolbar toggle highlight
        board.ShowPowerChanged += OnShowPowerChanged;   // (re)compute the overlay off-thread when toggled on
        board.ShowRoomsChanged += OnShowRoomsChanged;   // same for the room certification
        board.ShowLightChanged += OnShowLightChanged;   // same for the interior-lighting flood
        board.ShowWalkChanged += OnShowWalkChanged;     // same for the crew-access analysis
        board.WireModeChanged += OnWireModeChanged;     // swap the status hint for the wiring instructions
        board.SurfaceModeChanged += OnSurfaceModeChanged;   // show/hide the Surfaces bar and swap the status hint
        board.LinkToggleRequested += OnLinkToggleRequested;   // connect/disconnect two devices via the command stack
        board.ActiveZoneChanged += UpdateZones;   // reflect which zone (if any) is being painted

        var session = new DocumentSession { Board = board, UntitledSlot = FreeUntitledSlot() };
        session.Stack.StateChanged += RefreshChrome;
        // Audit every edit/undo/redo, resolving each part's friendly name so the trail records what/where
        // ("Place Nav Station @(12,7)") rather than a context-free "Place" — the detail a bug report needs.
        session.Stack.Applied += (cmd, action) => AuditLog.Command(action, cmd, DefFriendlyName);

        _sessions.Add(session);
        CanvasHost.Children.Add(board);
        return session;
    }

    /// <summary>The lowest untitled auto-save bucket no open design is using. See <see cref="AutoSaveStore.KeyFor"/>
    /// for why an untitled design needs one at all.</summary>
    private int FreeUntitledSlot()
    {
        // A design that has been saved somewhere keys on its path instead, so it is not holding a slot any more.
        for (var slot = 0; ; slot++)
            if (_sessions.All(s => s.Doc?.FilePath is not null || s.UntitledSlot != slot)) return slot;
    }

    /// <summary>
    /// Point the whole window at <paramref name="session"/>: show its canvas, hide the one that was up, and refill
    /// every piece of shared chrome from it. Clicking the tab that is already active falls through to a strip
    /// refresh, which is what puts the toggle back down after WPF's own click toggled it up.
    /// </summary>
    internal void ActivateSession(DocumentSession session)
    {
        if (ReferenceEquals(_active, session)) { RefreshDocTabs(); return; }

        // Hidden rather than Collapsed: a background tab keeps being measured and arranged, so its zoom and pan stay
        // meaningful and it is ready to draw the instant it comes back. WPF skips rendering it either way.
        if (_active is not null) _active.Board.Visibility = Visibility.Hidden;
        _active = session;
        session.Board.Visibility = Visibility.Visible;
        TxtCell.Text = "—";   // the tile readout belonged to the canvas the cursor was over, which is no longer this one

        SyncViewToggles();
        UpdateSurfaceBar();
        UpdateBrushText();
        RefreshDocTabs();
        if (_catalog is null) return;   // still loading: LoadDataAsync fills the rest in once there is a document

        UpdateZoomText();
        UpdateZones();
        UpdateInspector();
        UpdateProblems(session.LastProblems);
        RefreshChrome();
        ScheduleScan();   // this design's problems, recomputed rather than trusted from whenever it was last up
        session.Board.Focus();
    }

    /// <summary>
    /// Start the next design in a tab of its own, unless the active tab is an untouched blank — the one the app
    /// opens on, and the one File ▸ New leaves behind. Reusing that is what stops an empty "Untitled ship" tab
    /// accumulating beside every design the user actually opens.
    /// </summary>
    private void BeginDocumentInNewTab()
    {
        if (_active is { IsBlank: true, StateDirty: false } blank && !blank.Stack.Dirty) return;
        ActivateSession(CreateSession());
    }

    /// <summary>
    /// Close one design's tab. Refused for the last one: the editor always has a document in it, and with a single
    /// design open the strip is not even on screen for there to be a ✕ to click.
    ///
    /// <para>The tab is activated before it is asked about, so the unsaved-changes prompt is answered while looking
    /// at the design it names — and so Save writes the right one.</para>
    /// </summary>
    internal void CloseSession(DocumentSession session)
    {
        if (_sessions.Count <= 1) return;
        ActivateSession(session);
        if (!ConfirmDiscardChanges()) return;

        CloseReports(session);
        session.DetachDoc();
        var at = _sessions.IndexOf(session);
        _sessions.Remove(session);
        CanvasHost.Children.Remove(session.Board);
        AuditLog.Add($"Closed \"{session.DisplayName}\".");

        _active = null!;
        ActivateSession(_sessions[Math.Min(at, _sessions.Count - 1)]);
    }

    /// <summary>Step to the next or previous tab (Ctrl+Tab / Ctrl+Shift+Tab), wrapping at both ends.</summary>
    internal void CycleSession(int delta)
    {
        if (_sessions.Count <= 1) return;
        var at = _sessions.IndexOf(_active);
        ActivateSession(_sessions[((at + delta) % _sessions.Count + _sessions.Count) % _sessions.Count]);
    }

    /// <summary>Every open design gets its unsaved-changes prompt before the window closes. Answering Cancel to any
    /// of them cancels the close, leaving that design the one on screen.</summary>
    private bool ConfirmDiscardEverything()
    {
        foreach (var session in _sessions.ToList())
        {
            if (!session.Dirty) continue;
            ActivateSession(session);
            if (!ConfirmDiscardChanges()) return false;
        }
        return true;
    }

    /// <summary>
    /// Rebuild the tab strip. Hidden entirely while one design is open, so a single-document session looks exactly
    /// as it did before tabs existed, and rebuilt wholesale on every chrome refresh — it is a handful of buttons,
    /// and the alternative is keeping a parallel list of them in step with the sessions.
    /// </summary>
    private void RefreshDocTabs()
    {
        DocTabBar.Visibility = _sessions.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        DocTabStrip.Children.Clear();
        if (_sessions.Count <= 1) return;

        foreach (var session in _sessions) DocTabStrip.Children.Add(BuildDocTab(session));

        var add = new Button
        {
            Content = "+", Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(3, 0, 0, 4),
            MinWidth = 0, ToolTip = "Start another design in a new tab (Ctrl+N)",
        };
        add.Click += (_, _) => NewDesign();
        DocTabStrip.Children.Add(add);
    }

    /// <summary>One tab. A ToggleButton on the Fluent chain (see the DocTab style and CONVENTIONS.md): the active
    /// tab wears the theme's own checked accent rather than a hard-set background that Fluent's hover state would
    /// paint over.</summary>
    private ToggleButton BuildDocTab(DocumentSession session)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = session.DisplayName + (session.Dirty ? " *" : ""),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var close = new TextBlock
        {
            Text = "✕", FontSize = 11, Opacity = 0.55, Cursor = Cursors.Hand,
            Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Close this design (Ctrl+W)",
        };
        // PreviewMouseLeftButtonDown, marked handled, so clicking the ✕ closes the tab instead of also selecting it
        // — the same trick the palette's favourite star uses.
        close.PreviewMouseLeftButtonDown += (_, e) => { e.Handled = true; CloseSession(session); };
        row.Children.Add(close);

        var tab = new ToggleButton
        {
            Style = (Style)FindResource("DocTab"),
            Content = row,
            IsChecked = ReferenceEquals(session, _active),
            ToolTip = session.Doc?.FilePath ?? "Not saved to a file yet",
        };
        tab.Click += (_, _) => ActivateSession(session);
        return tab;
    }

    // ---- startup ----

    private async Task LoadDataAsync()
    {
        while (_env is null)
        {
            try
            {
                _env = GameEnv.Locate(_settings.GameRootOverride, _settings.SavesDirOverride);
            }
            catch (DirectoryNotFoundException ex)
            {
                // Ostraplan reads the game's own sprites and data — it can't run without the install. Show why,
                // let the user point at the folder by hand, and fail closed (a clean exit) if they cancel.
                Dlg.Warn(this, "Ostranauts install required",
                    ex.Message + "\n\n" +
                    "Ostraplan needs the Ostranauts install to run.\n" +
                    "Please pick the game folder.");
                var dlg = new OpenFolderDialog { Title = "Pick the Ostranauts folder (inside steamapps\\common)" };
                if (dlg.ShowDialog(this) != true)
                {
                    Dlg.Info(this, "Ostraplan is closing",
                        "Ostraplan can't run without the Ostranauts install, so it will now close.\n\n" +
                        "Launch it again once the game is installed, or when you're ready to pick the folder.");
                    Close();
                    return;
                }
                _settings.GameRootOverride = dlg.FolderName;
                AuditLog.Setting("Game folder", dlg.FolderName);
                _settings.Save();
            }
        }

        TxtLoading.Text = "Loading game data…";
        var env = _env;
        DataIndex index;
        Catalog catalog;
        SpriteCache sprites;
        List<PartVM> parts;
        try
        {
            (index, catalog, sprites, parts) = await Ui.OffThread(() =>
            {
                var idx = DataIndex.Load(env);
                var cat = Catalog.Build(idx);
                var spr = new SpriteCache();
                // thumbnails built here so first palette paint is instant (all frozen)
                var vms = cat.Parts.Select(p => new PartVM(p, spr.Thumb(p))).ToList();
                // The Items tab: every renderable loose item (the whole loose universe), re-tagged into the synthetic
                // ItemsCategory so it lands in its own tab. Only DefName is used when one is dropped, so the cloned
                // category never leaks into placement/export. Skip defs with no sprite on disk (can't be drawn/ghosted).
                vms.AddRange(cat.LooseItems
                    .Where(p => p.SpriteAbs is not null)
                    .Select(p => new PartVM(p with { Category = ItemsCategory }, spr.Thumb(p), isLoose: true)));
                // The Special tab: the installed structure the game places but never offers a build job for
                // (asteroids, signs, station fixtures). Same re-tagging trick as Items, but these are ordinary
                // installed placements rather than loose drops, so they arm and build like any palette part.
                vms.AddRange(cat.SpecialItems
                    .Where(p => p.SpriteAbs is not null)
                    .Select(p => new PartVM(p with { Category = SpecialCategory }, spr.Thumb(p))));
                return (idx, cat, spr, vms);
            });
        }
        catch (Exception ex)
        {
            Dlg.Error(this, "Ostraplan", $"Could not load game data.\n\n{ex.Message}");
            Close();
            return;
        }

        _index = index;
        _catalog = catalog;
        _sprites = sprites;
        _allParts = parts;
        foreach (var s in _sessions) s.Board.Sprites = sprites;   // the startup tab; every later one takes it at creation

        BuildPalette();
        NewDocument();

        var v = env.InstalledVersion ?? "unknown";
        AuditLog.Add($"Loaded game data (Game {v}).");
        TxtVersion.Text = $"Game {v}";

        // Everything goes to the activity log and into a bug report. Only what the user can act on reaches the
        // toolbar: a defect in the game's own data is permanent and none of their doing (see DataWarning), and
        // standing there as a count it just trains them to ignore the badge that matters.
        var warnings = index.Warnings.Concat(catalog.Warnings).ToList();
        foreach (var w in warnings) AuditLog.Add($"Data warning: {w}");
        foreach (var r in index.Repaired) AuditLog.Add($"Data mended on load: {r}");

        var actionable = warnings.Where(w => !w.Core).ToList();
        if (actionable.Count > 0)
        {
            TxtWarnings.Text = actionable.Count == 1 ? "1 data warning" : $"{actionable.Count} data warnings";
            TxtWarnings.ToolTip = string.Join("\n", actionable.Take(40).Select(w => w.ToString()));
        }

        UpdateZoomText();
        LoadingOverlay.Visibility = Visibility.Collapsed;

        ShowWhatsNewAfterUpdate();   // an update landed last restart: say what it brought, once
        _ = CheckForUpdateAsync();   // quiet check against the latest GitHub release
    }

    /// <summary>
    /// The first run after an update, show that version's changelog entry. An update applies on restart, so this
    /// is the only moment the app can say what it brought; before this it just came back looking identical.
    ///
    /// <para>The version that last ran is recorded either way — including when there are no notes to show — so a
    /// build whose entry is still under <c>Unreleased</c> costs the next release its "what's new" rather than
    /// showing it late. A fresh install has nothing to compare against and shows nothing (see
    /// <see cref="ReleaseNotes.IsUpgrade"/>).</para>
    /// </summary>
    private void ShowWhatsNewAfterUpdate()
    {
        var from = _settings.LastRunVersion;
        if (from != AppVersion)
        {
            _settings.LastRunVersion = AppVersion;
            _settings.Save();
        }
        if (!ReleaseNotes.IsUpgrade(from, AppVersion)) return;

        AuditLog.Add($"Updated to v{AppVersion} (from v{from}).");
        // every version the update crossed, not just the newest: releases here batch several bumps, so a user who
        // has been away a while is arriving at more than one
        WhatsNewUI.Show(this, ReleaseNotes.Since(WhatsNewUI.Changelog(), from, AppVersion), updated: true, OpenUrl);
    }

    /// <summary>Help ▸ View Changelog: this build's own notes when the changelog has them, else straight to the
    /// latest release on GitHub. Either way the release page is one click from here.</summary>
    private void ViewChangelog()
    {
        if (WhatsNewUI.EntryFor(AppVersion) is { } entry) WhatsNewUI.Show(this, [entry], updated: false, OpenUrl);
        else OpenUrl(WhatsNewUI.LatestReleaseUrl);
    }

    private void UpdateZoomText() =>
        TxtZoom.Text = $"zoom {Board.Zoom / 16:0.##}×" + (Board.ViewRot != 0 ? $" · view {Board.ViewRot}°" : "");

    /// <summary>The brush's rotation, in the status bar beside the view's. It is sticky across parts by design (so a
    /// row of consoles can all face the same way), which only works if the angle is readable instead of inferred from
    /// the ghost. Blank when nothing is armed, or when the armed part is a wall or floor, which autotile rather than
    /// turn and so ignore the angle entirely.</summary>
    private void UpdateBrushText() =>
        TxtBrush.Text = Board.ArmedPart is { Item.HasSpriteSheet: false } ? $"brush {Board.ArmedRot}°" : "";

    // ---- palette ----

    /// <summary>The synthetic palette category for loose cargo (the ITEMS tab). Not a game build category — it
    /// exists only to group the loose universe into its own tab and to flag an armed brush as a loose drop. Value is
    /// the uppercase tab header, matching the game's HULL/HVAC/… tabs.</summary>
    private const string ItemsCategory = "ITEMS";

    /// <summary>The synthetic palette category for non-buildable installed structure (the SPECIAL tab) — the
    /// asteroids, signs and station fixtures of <see cref="Catalog.SpecialItems"/>. Like
    /// <see cref="ItemsCategory"/> it is a tab label, not a game build category, and only the def name survives
    /// into a placement.</summary>
    private const string SpecialCategory = "SPECIAL";

    /// <summary>True for the two tabs that are Ostraplan's own rather than the game's build menu. They are kept
    /// out of "All", which is the buildable catalogue.</summary>
    private static bool IsSyntheticCategory(string category) =>
        category is ItemsCategory or SpecialCategory;

    private void BuildPalette()
    {
        Tabs.Items.Clear();
        _paletteLists.Clear();

        // ★ Favorites / Recent, always the first tab (see BuildQuickTab).
        Tabs.Items.Add(BuildQuickTab());

        foreach (var category in new[] { "All" }.Concat(Catalog.Categories).Append(ItemsCategory).Append(SpecialCategory))
        {
            var list = NewPaletteList(category == "All" ? null : category);
            _paletteLists.Add(list);
            Tabs.Items.Add(new TabItem { Header = category, Content = list });
        }

        ApplyFavoriteFlags();
        RefreshPalette();

        // Returning users with pins land on ★ (its whole point is the shortcut); first-timers land on the catalog
        // (All) so an empty ★ tab never hides the parts. Index 1 is All (0 is ★).
        Tabs.SelectedIndex = _settings.Favorites.Count > 0 || _settings.RecentParts.Count > 0 ? 0 : 1;
    }

    /// <summary>One palette ListBox. <paramref name="category"/> null = the buildable "All" set; a category name =
    /// that tab; the two ★ lists also pass null and are told apart by reference (see RefreshPalette).</summary>
    private ListBox NewPaletteList(string? category)
    {
        var list = new ListBox
        {
            ItemTemplate = (DataTemplate)FindResource("PartTemplate"),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Tag = category,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            // typeahead runs on TextInput, which fires even when the window
            // handles PreviewKeyDown - pressing R would silently jump the
            // palette to an R-part instead of rotating
            IsTextSearchEnabled = false,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        list.SelectionChanged += OnPaletteSelection;
        return list;
    }

    /// <summary>The ★ tab: a FAVORITES group over a RECENT group, in one scroll region. Each group's own vertical
    /// scrollbar is disabled so both size to their content and the outer ScrollViewer does the scrolling. Both lists
    /// join <see cref="_paletteLists"/> so arming and cross-tab selection-clearing treat them like any other list.</summary>
    private TabItem BuildQuickTab()
    {
        _favList = NewPaletteList(null);
        _recentList = NewPaletteList(null);
        ScrollViewer.SetVerticalScrollBarVisibility(_favList, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_recentList, ScrollBarVisibility.Disabled);
        _paletteLists.Add(_favList);
        _paletteLists.Add(_recentList);

        TextBlock Header(string text)
        {
            var tb = new TextBlock
            {
                Text = text, FontSize = 11, FontWeight = FontWeights.Bold,
                Margin = new Thickness(2, 10, 0, 4),
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "Dim");   // stays dim across theme switches
            return tb;
        }

        _favHeader = Header("FAVORITES");
        _recentHeader = Header("RECENT");
        _quickEmpty = new TextBlock
        {
            Text = "Nothing pinned yet.\n\nClick the ☆ on any part to pin it here for quick reuse. "
                 + "Parts you place on the ship show up under Recent automatically.",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.6, FontSize = 12, Margin = new Thickness(2, 12, 6, 0),
        };

        var stack = new StackPanel();
        stack.Children.Add(_quickEmpty);
        stack.Children.Add(_favHeader);
        stack.Children.Add(_favList);
        stack.Children.Add(_recentHeader);
        stack.Children.Add(_recentList);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = stack,
        };
        return new TabItem { Header = "FAV/REC", Content = scroll };
    }

    private void RefreshPalette()
    {
        var search = TxtSearch.Text.Trim();
        _syncingPalette = true;
        foreach (var list in _paletteLists)
        {
            if (ReferenceEquals(list, _favList) || ReferenceEquals(list, _recentList)) continue;   // ★ lists handled below
            var category = (string?)list.Tag;
            // The "All" tab (null Tag) is the buildable palette only — the huge loose universe stays in its own
            // Items tab, and the non-buildable structure in Special, so neither drowns the structure parts.
            list.ItemsSource = _allParts
                .Where(vm => (category is null ? !IsSyntheticCategory(vm.Part.Category) : vm.Part.Category == category)
                             && vm.Matches(search))
                .ToList();
        }
        RefreshQuickLists(search);
        _syncingPalette = false;
    }

    /// <summary>Resolve a saved Favorites/Recent reference back to its single palette-row instance, or null when the
    /// def is no longer present (a mod that defined it was disabled).</summary>
    private PartVM? FindPart(PartRef r) =>
        _allParts.FirstOrDefault(vm => vm.IsLoose == r.Loose && vm.Part.DefName == r.Def);

    /// <summary>Seed each row's star from the saved favorites (called once after the palette is built).</summary>
    private void ApplyFavoriteFlags()
    {
        foreach (var vm in _allParts)
            vm.IsFavorite = _settings.IsFavorite(vm.Part.DefName, vm.IsLoose);
    }

    /// <summary>Repopulate the ★ tab's two groups from the saved lists (order preserved), applying the current
    /// search filter, and hide any empty group's header — showing the onboarding hint only when both are empty.</summary>
    private void RefreshQuickLists(string search)
    {
        if (_favList is null || _recentList is null) return;

        var favs = _settings.Favorites.Select(FindPart).OfType<PartVM>().Where(vm => vm.Matches(search)).ToList();
        // A favorited part already sits (pinned) in the Favorites group, so drop it from Recent to avoid the
        // duplicate — Recent is for the not-yet-pinned things you just used. Unpinning brings it back here.
        var recents = _settings.RecentParts.Select(FindPart).OfType<PartVM>()
            .Where(vm => vm.Matches(search) && !vm.IsFavorite).ToList();

        var wasSync = _syncingPalette;
        _syncingPalette = true;   // reassigning ItemsSource clears selection — don't let that ripple through the sync
        _favList.ItemsSource = favs;
        _recentList.ItemsSource = recents;
        _syncingPalette = wasSync;

        var hasFav = favs.Count > 0;
        var hasRecent = recents.Count > 0;
        _favHeader!.Visibility = _favList.Visibility = hasFav ? Visibility.Visible : Visibility.Collapsed;
        _recentHeader!.Visibility = _recentList.Visibility = hasRecent ? Visibility.Visible : Visibility.Collapsed;
        _quickEmpty!.Visibility = hasFav || hasRecent ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- favorites / recent ----

    /// <summary>The star on a palette row was clicked: pin/unpin, and swallow the click so the row isn't armed.</summary>
    private void OnFavStarClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PartVM vm }) return;
        e.Handled = true;
        ToggleFavorite(vm);
    }

    /// <summary>Toggle a palette row's favorite state, persist, and refresh the ★ tab.</summary>
    private void ToggleFavorite(PartVM vm)
    {
        vm.IsFavorite = _settings.ToggleFavorite(vm.Part.DefName, vm.IsLoose);
        _settings.Save();
        AuditLog.Add((vm.IsFavorite ? "Favorited " : "Unfavorited ") + vm.Part.Friendly);
        RefreshQuickLists(TxtSearch.Text.Trim());
    }

    /// <summary>Toggle a favorite by def reference (the right-click-menu route from a placed tile / loose item). Updates
    /// the palette row's star when the part is in the palette; otherwise toggles the saved list directly.</summary>
    private void ToggleFavoriteByRef(string def, bool loose)
    {
        if (FindPart(new PartRef(def, loose)) is { } vm) { ToggleFavorite(vm); return; }
        _settings.ToggleFavorite(def, loose);
        _settings.Save();
        RefreshQuickLists(TxtSearch.Text.Trim());
    }

    /// <summary>A placement just committed: record the armed part as most-recently-used and refresh Recent. A repeat
    /// of the current front is a no-op (PushRecent returns false), so a paint stroke doesn't thrash the settings file.</summary>
    private void RecordRecentUse()
    {
        if (Board.ArmedPart is not { } p) return;
        if (!_settings.PushRecent(p.DefName, Board.ArmedLoose)) return;
        _settings.Save();
        RefreshQuickLists(TxtSearch.Text.Trim());
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_paletteLists.Count > 0) RefreshPalette();
    }

    private void OnPaletteSelection(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingPalette || sender is not ListBox { SelectedItem: PartVM vm } origin) return;

        // Surfaces mode with slot B armed: this pick is the pattern's second brush, not a new brush in hand. It has
        // to match what is already armed — same 1×1 wall/floor class — or there is no pattern to make of the pair,
        // in which case the pick falls through and arms normally rather than being swallowed.
        if (_slotBArmed && Board.SurfaceMode && _catalog is not null)
        {
            _slotBArmed = false;
            if (SurfacePaint.IsSurfaceBrush(_catalog, vm.Part) && Board.ArmedPart is { } primary
                && _catalog.RenderLayer(primary) == _catalog.RenderLayer(vm.Part))
            {
                Board.SetPatternB(vm.Part);
                SyncPaletteHighlightToArmed();   // the highlight belongs to what is in hand, which hasn't changed
                UpdateSurfaceBar();
                Board.Focus();
                return;
            }
        }

        _syncingPalette = true;
        foreach (var list in _paletteLists.Where(l => !ReferenceEquals(l, origin)))
            list.SelectedItem = null;
        _syncingPalette = false;

        Board.SetArmed(vm.Part, loose: vm.Part.Category == ItemsCategory);
        AuditLog.Tool(BrushLabel(vm.Part));
        Board.Focus();   // keys (R, Del, Esc) belong to the canvas once a part is armed
        UpdateInspector();
    }

    /// <summary>
    /// The activity-log label for the brush: the part, plus the angle it is armed at when that isn't the default.
    /// The brush rotation is sticky across parts (arm a second part and it keeps the angle you set), so an armed
    /// part's angle is part of what the user did and a bug report needs it to explain a part that went down turned.
    /// Sheet items (walls/floors) autotile rather than rotate, so their angle is never meaningful.
    /// </summary>
    private string BrushLabel(PartDef part) =>
        part.Item.HasSpriteSheet || Board.ArmedRot == 0 ? part.Friendly : $"{part.Friendly} r{Board.ArmedRot}";

    private void ClearPaletteSelection()
    {
        _syncingPalette = true;
        foreach (var list in _paletteLists) list.SelectedItem = null;
        _syncingPalette = false;
        UpdateInspector();
    }

    /// <summary>
    /// Arm the brush with a placed part's def and pose (the Alt+click / RMB "Use as brush" action) and keep
    /// drawing. An eyedropper hands back what you pointed at, so the picked part's <paramref name="rot"/> is
    /// adopted as well as its def: without that you get the part at whatever angle the brush was last left at,
    /// which reads as the tile turning itself on the way into the cursor. The rotation is applied first so the
    /// arming is logged at the angle it actually lands on. Selecting its palette entry (when visible) both arms
    /// it and syncs the highlight; if it is filtered out by the search, arm directly. A part with no palette row
    /// at all (the primary airlock, a closed door) is ignored — nothing to paint.
    /// </summary>
    private void OnArmFromTile(string defName, int rot)
    {
        var vm = _allParts.FirstOrDefault(v => v.Part.DefName == defName);
        if (vm is null) return;
        Board.SetArmedRot(rot);
        foreach (var list in _paletteLists)
            if (list.Items.Contains(vm)) { list.SelectedItem = vm; break; }
        if (Board.ArmedPart?.DefName != defName)
            Board.SetArmed(vm.Part);   // visible nowhere (search-filtered) — arm without a palette highlight
        AuditLog.Tool(BrushLabel(vm.Part));   // collapses to nothing when the palette path already logged this brush
        Board.Focus();
    }

    // ---- document lifecycle ----

    /// <summary>Start a blank design in a tab of its own, which is what File ▸ New, Ctrl+N and the strip's + all
    /// mean. There is nothing to discard first: the design that was open stays open in its own tab.</summary>
    private void NewDesign()
    {
        if (_catalog is null) return;
        BeginDocumentInNewTab();
        NewDocument();
        AuditLog.Add("New design.");
    }

    /// <summary>Put a blank design into the active tab. The tab is chosen by the caller (see
    /// <see cref="BeginDocumentInNewTab"/>); at startup it is the one the constructor made.</summary>
    private void NewDocument()
    {
        if (_catalog is null) return;
        CloseReports(_active);
        var doc = new ShipDocument(_catalog);
        // every ship owns exactly one Primary Airlock, fixed at the root - seeded
        // outside the undo stack so it can't be undone into nothing, and locked
        // against move/rotate/delete like the game's own. Before the document is hung on the session, so seeding it
        // is not an edit anything hears about.
        if (_catalog.ByDefName.ContainsKey(Catalog.PrimaryDocksysDef))
            new PlaceCommand(new Placement { DefName = Catalog.PrimaryDocksysDef, X = 0, Y = 0 }).Do(doc);
        AttachDoc(doc);
        _meta = new OplanMeta();
        _stateDirty = false;
        _saveContext = null;
        _unresolvedParts = [];
        _active.IsBlank = true;   // nothing has been done to it yet, so Open/Import may take this tab over
        _stack.Reset();
        Board.SetDocument(_doc!);
        Board.SetViewRot(0);
        OnDocChanged();
        UpdateInspector();
    }

    /// <summary>
    /// Hang <paramref name="doc"/> on the active session, moving its <see cref="ShipDocument.Changed"/> subscription
    /// off whatever was there. The handler is per session rather than one shared method group because it has to know
    /// which design changed: a report window on a background tab writes to that tab's document, and the chrome that
    /// must not be refreshed for it is the one on screen.
    /// </summary>
    private void AttachDoc(ShipDocument doc)
    {
        var session = _active;
        session.DetachDoc();
        session.Doc = doc;
        session.DocChanged ??= () => DocumentChanged(session);
        doc.Changed += session.DocChanged;
        session.IsBlank = false;
    }

    /// <summary>The active design changed. Kept as the no-argument form the editor has always called.</summary>
    private void OnDocChanged() => DocumentChanged(_active);

    /// <summary>
    /// One design changed. The reports measuring it go stale and its canvas drops any leak highlight whichever tab
    /// it is on; the shared chrome below only belongs to the design actually on screen, so a background tab stops
    /// after refreshing the strip (its dirty star is the one thing about it still visible).
    /// </summary>
    private void DocumentChanged(DocumentSession session)
    {
        session.Board.SetLeakCells([]);   // any Ship Rating leak highlight is stale once the design changes
        // An open report measured the design as it was a moment ago, so an edit is what makes it out of date. It says
        // so rather than going on showing figures for a ship that no longer exists (see ReportWindow).
        session.RatingReport?.MarkStale();
        session.DiagnosticsReport?.MarkStale();
        if (!ReferenceEquals(session, _active)) { RefreshDocTabs(); return; }

        var bounds = _doc?.Bounds();
        var dims = bounds is { } b ? $" · {b.MaxX - b.MinX + 1}×{b.MaxY - b.MinY + 1} tiles" : "";
        TxtParts.Text = $"{_doc?.Placements.Count ?? 0} parts{dims}";
        ScheduleScan();
        UpdateZones();
        RefreshChrome();
    }

    /// <summary>Close one design's analysis reports. Their figures, their leak highlight and their dead-weight box
    /// all belong to the document that produced them, so a document swap — or closing its tab — takes them with it
    /// rather than leaving a report describing one ship while writing into another.</summary>
    private static void CloseReports(DocumentSession session)
    {
        session.RatingReport?.Close();
        session.DiagnosticsReport?.Close();
        session.FlightReport?.Close();   // read-only, but a flight profile for a design that is no longer open is noise
    }

    /// <summary>
    /// Debounce the problem scan and run it off the UI thread. A burst of edits — a paint stroke,
    /// a box-fill, a group move — collapses into one scan that never blocks input; the red tints,
    /// badges and PROBLEMS list settle a beat (~120 ms) after the edits stop. The live armed-ghost
    /// validity stays synchronous (it's computed in the canvas, not here), so placement feedback is
    /// still instant. A superseding edit cancels the in-flight scan.
    /// </summary>
    private void ScheduleScan()
    {
        if (_doc is null || _catalog is null) return;
        _scanTimer.Stop();
        _scanTimer.Start();
    }

    private async void RunScan()
    {
        _scanTimer.Stop();
        if (_doc is null || _catalog is null) return;

        _scanCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();
        var token = cts.Token;
        var session = _active;            // whose design this is: the user may switch tabs while it runs
        var snapshot = _doc.Snapshot();   // UI thread, cheap; immutable while the scan runs
        var catalog = _catalog;
        var showPower = Board.ShowPower;   // only pay for the power flood when PowerViz is on
        var showLight = Board.ShowLight;   // and the interior-lighting flood only when Light Viz is on
        var showWalk = Board.ShowWalk;     // and the walk analysis only when WalkViz is on
        // WalkViz reads the persisted View-menu switches; the Law report always uses the defaults, so the two never
        // disagree about what the ship IS — only about what the overlay is currently asking.
        var walkOpts = new WalkOptions(_settings.WalkIncludeExterior, _settings.WalkRespectForbidZones);
        // RoomViz: only certify while the overlay is on, and only when the data index is up (specs come from it).
        // Loaded here on the UI thread, then handed to the scan as an immutable list.
        var roomSpecs = Board.ShowRooms && _index is { } index ? _roomSpecs ??= RoomCertifier.LoadSpecs(index) : null;

        // Exterior daylight: the persisted parallax location + sun angle, resolved on the scan thread
        var sun = showLight && _settings.LightSunParallax is { Length: > 0 } sunName
            ? new SunSettings(sunName, _settings.LightSunAngle) : null;

        List<Problem> problems;
        PowerOverlay power;
        RoomOverlay rooms;
        LightScene light;
        WalkOverlay walk;
        try
        {
            (problems, power, rooms, light, walk) = await Ui.OffThread(() =>
            {
                var probs = ProblemScan.Scan(snapshot, catalog);
                var pov = PowerOverlay.Empty;
                var lov = LightScene.Empty;
                var wov = WalkOverlay.Empty;
                // Power, light and walk all flood the same grid — build it once when any overlay is on.
                if (showPower || showLight || showWalk)
                {
                    var grid = ShipGrid.FromDocument(snapshot, catalog);
                    if (showPower) pov = PowerNetwork.ToOverlay(grid, PowerNetwork.Build(grid, catalog));
                    if (showLight) lov = LightNetwork.Build(grid, catalog, sun);
                    if (showWalk)
                        wov = WalkNetwork.ToOverlay(grid, WalkNetwork.Build(
                            grid, catalog, walkOpts, WalkNetwork.ForbiddenTiles(snapshot, grid)));
                }
                var rov = roomSpecs is null ? RoomOverlay.Empty : RoomOverlay.Build(snapshot, catalog, roomSpecs);
                return (probs, pov, rov, lov, wov);
            }, token);
        }
        catch (OperationCanceledException) { return; }
        if (token.IsCancellationRequested || !ReferenceEquals(cts, _scanCts)) return;   // superseded

        // The overlays go on the canvas that was scanned, whichever tab that is now on — they are that design's,
        // and a hidden canvas simply draws them when it comes back. Only the shared PROBLEMS list is guarded on the
        // scan still describing what is on screen; the tab switched to has its own scan already scheduled.
        session.Board.SetPowerOverlay(power);
        session.Board.SetRoomOverlay(rooms);
        session.Board.SetLightScene(light);
        session.Board.SetWalkOverlay(walk);
        if (ReferenceEquals(session, _active)) UpdateProblems(problems);
        else session.LastProblems = problems;
    }

    /// <summary>
    /// Freeze editing while an engine reads the <b>live</b> document on a pool thread.
    /// <para>
    /// The scan can hand off a <see cref="ShipDocument.Snapshot"/> because it only reads the tile grid. The export,
    /// save-edit and rating engines can't: they also read cargo, zones, loose items and device links, none of which
    /// Snapshot copies. So they take the real <c>_doc</c>, and it must not change under them — a torn read means a
    /// wrong export, or a collection mutated mid-enumeration. Freezing for the run is cheaper and far safer than
    /// deep-copying the whole document.
    /// </para>
    /// <para>
    /// It must close <b>every</b> edit route, so it disables <c>Chrome</c> — the whole editing surface — rather than
    /// picking off individual controls. Gating just the canvas and the keyboard left the toolbar live, and Undo
    /// during a (multi-second) Ship Rating mutates the very list the analysis is walking. Window-level keys don't
    /// route through the disabled tree, so <see cref="OnPreviewKeyDown"/> checks the gate too. The chrome greying
    /// out is honest: the app really is busy, and the rating already has a progress dialog up.
    /// </para>
    /// Nestable (see <see cref="FreezeGate"/>): overlapping runs — an export started while a rating is still going —
    /// thaw on the <b>last</b> scope out, not the first.
    /// </summary>
    private IDisposable FreezeDoc() => _freeze.Enter();

    // ---- Ship Rating (rooms · airtightness · certification · rating) ----

    private async void OnShipRatingClick(object sender, RoutedEventArgs e) => await ShowRatingReport();

    /// <summary>
    /// Run the Ship Rating and show it, or refresh the report already open (the Re-run button on its stale bar comes
    /// back through here). The report is modeless, so the design can move on underneath it; what keeps that honest is
    /// <see cref="ReportWindow.MarkStale"/> from <see cref="OnDocChanged"/> and this path to recompute.
    /// </summary>
    private async Task ShowRatingReport()
    {
        if (_analysing || _doc is null || _catalog is null || _index is null) return;
        if (_doc.Placements.Count == 0)
        {
            Dlg.Show(this, "Place some parts before running the Ship Rating.", "Ship Rating",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _analysing = true;
        BtnRating.IsEnabled = BtnDiagnostics.IsEnabled = false;
        _roomSpecs ??= RoomCertifier.LoadSpecs(_index);
        var (doc, catalog, specs) = (_doc, _catalog, _roomSpecs);
        // The session, not the shim, for everything the report is bound to. The freeze below rules out a tab switch
        // mid-analysis, but the window outlives this method: its Closed handler must clear the field on the session
        // that owns it rather than on whichever one is active whenever the user gets round to closing it.
        var session = _active;

        var progress = new RatingProgressDialog { Owner = this };
        var reporter = new Progress<(string Stage, double Frac)>(p => progress.Update(p.Stage, p.Frac));
        AnalysisReport? report = null;
        progress.Show();
        // AnalyzeDocument is pure computation over the live document (no IO), so it has no failure a user could act
        // on: anything it throws is our bug. Let it reach the app's handler, which logs the stack to error.log —
        // "Analysis failed: <message>" swallowed exactly that. The document is frozen while it reads.
        using (FreezeDoc())
        {
            try
            {
                // allowUiCapture: the analysis lambda captures only doc/catalog/specs/reporter, but `progress` is
                // a local of this same scope that the reporter's lambda captures, so the compiler files them all
                // in ONE closure and the guard sees a UI-owned dialog in it. Nothing here touches the dialog off
                // the UI thread: Progress<T> captures the SynchronizationContext at construction and posts
                // Update back to it, which is the whole point of using it. Without the opt-out the guard throws
                // before Task.Run, so in a Debug build Ship Rating logged an error and rendered nothing.
                report = await Ui.OffThread(
                    () => ShipAnalysis.AnalyzeDocument(doc, catalog, specs, reporter), allowUiCapture: true);
            }
            finally
            {
                progress.Close();
                BtnRating.IsEnabled = BtnDiagnostics.IsEnabled = true;
                _analysing = false;
                SyncDocumentKindChrome();   // Diagnostics stays disabled on a residence
            }
        }

        if (report is null) return;

        var value = ShipValue.Estimate(doc, catalog, specs);
        var snapshot = session.Board.RenderRatingSnapshot(specs);
        var snapshotSvg = session.Board.RenderRatingSnapshotSvg(specs);   // scalable variant for the "Save image…" dialog

        if (session.RatingReport is null)
        {
            // The callbacks are bound to the document that produced the report, which is why CloseReports drops the
            // window on a document swap rather than letting a stale one write into the new design.
            var window = new RatingReportWindow(cells => session.Board.SetLeakCells(cells), kg => SetExtraMass(doc, kg),
                residence: doc.IsResidence)
            {
                Owner = this,
            };
            window.RerunRequested += async () => await ShowRatingReport();
            window.Closed += (_, _) => session.RatingReport = null;
            session.RatingReport = window;
            window.SetReport(report, value, snapshot, snapshotSvg);
            window.Show();
        }
        else
        {
            session.RatingReport.SetReport(report, value, snapshot, snapshotSvg);
            session.RatingReport.Activate();
        }
    }

    // ---- Diagnostics (the game's own nav-console ship checklist) ----

    /// <summary>
    /// Runs <see cref="ShipDiagnostics"/> over the live design and shows the checklist. Same shape as the Ship
    /// Rating action — it certifies rooms for the rating-code row, so it is real work rather than a lookup, and
    /// it takes the same freeze and the same one-at-a-time gate.
    /// </summary>
    private async void OnDiagnosticsClick(object sender, RoutedEventArgs e) => await ShowDiagnosticsReport();

    /// <inheritdoc cref="ShowRatingReport"/>
    private async Task ShowDiagnosticsReport()
    {
        if (_analysing || _doc is null || _catalog is null || _index is null) return;
        if (_doc.Placements.Count == 0)
        {
            Dlg.Show(this, "Place some parts before running the diagnostic.", "Ship Diagnostics",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _analysing = true;
        BtnRating.IsEnabled = BtnDiagnostics.IsEnabled = false;
        _roomSpecs ??= RoomCertifier.LoadSpecs(_index);
        var (doc, catalog, specs) = (_doc, _catalog, _roomSpecs);
        var session = _active;   // see ShowRatingReport: the window outlives this method, the shim does not

        var progress = new DiagnosticsProgressDialog { Owner = this };
        var reporter = new Progress<(string Stage, double Frac)>(p => progress.Update(p.Stage, p.Frac));
        ShipDiagnosticReport? report = null;
        progress.Show();
        // Pure computation over the live document, like the rating: anything it throws is our bug, so it reaches
        // the app handler and the stack lands in error.log. allowUiCapture for the same reason as the rating —
        // the reporter's Progress<T> posts back to the UI thread by design (see OnShipRatingClick).
        using (FreezeDoc())
        {
            try
            {
                report = await Ui.OffThread(
                    () => ShipDiagnostics.Analyze(doc, catalog, specs, reporter), allowUiCapture: true);
            }
            finally
            {
                progress.Close();
                BtnRating.IsEnabled = BtnDiagnostics.IsEnabled = true;
                _analysing = false;
                SyncDocumentKindChrome();   // Diagnostics stays disabled on a residence
            }
        }

        if (report is null) return;

        if (session.DiagnosticsReport is null)
        {
            var window = new DiagnosticsWindow { Owner = this };
            window.RerunRequested += async () => await ShowDiagnosticsReport();
            window.Closed += (_, _) => session.DiagnosticsReport = null;
            session.DiagnosticsReport = window;
            window.SetReport(report, session.Meta.Name);
            window.Show();
        }
        else
        {
            session.DiagnosticsReport.SetReport(report, session.Meta.Name);
            session.DiagnosticsReport.Activate();
        }
    }

    // ---- Flight dynamics ----

    private async void OnFlightClick(object sender, RoutedEventArgs e) => await ShowFlightReport();

    /// <summary>
    /// Measure the design's atmospheric flight profile and show it. Cheap next to the rating (one walk of the
    /// placed parts plus the propulsion scan for the RCS figure), so it takes no progress dialog, but it still
    /// goes off-thread and behind the document freeze like every other engine read.
    /// </summary>
    private async Task ShowFlightReport()
    {
        if (_analysing || _doc is null || _catalog is null || _index is null) return;
        if (_doc.Placements.Count == 0)
        {
            Dlg.Show(this, "Place some parts before running the flight report.", "Flight Dynamics",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _analysing = true;
        // Every value the measurement needs, read HERE on the UI thread, then handed to a static helper. See
        // MeasureFlight for why the Ui.OffThread call cannot live in this method.
        var (doc, catalog, index, designName) = (_doc, _catalog, _index, _meta.Name);
        var session = _active;   // see ShowRatingReport: the window outlives this method, the shim does not
        FlightReport report;
        Mouse.OverrideCursor = Cursors.Wait;
        using (FreezeDoc())
        {
            try
            {
                report = await MeasureFlight(doc, catalog, index, designName);
            }
            finally
            {
                Mouse.OverrideCursor = null;
                _analysing = false;
            }
        }

        if (session.FlightReport is null)
        {
            var window = new FlightWindow(_settings) { Owner = this };
            window.RerunRequested += async () => await ShowFlightReport();
            window.Closed += (_, _) => session.FlightReport = null;
            session.FlightReport = window;
            window.SetReport(report);
            window.Show();
        }
        else
        {
            session.FlightReport.SetReport(report);
            session.FlightReport.Activate();
        }
    }

    /// <summary>
    /// The off-thread half of <see cref="ShowFlightReport"/>, deliberately <b>static</b> and in its own method.
    ///
    /// <para>The C# compiler puts every capture in one method-scope closure object, and
    /// <see cref="Ui.VerifyCaptures"/> walks that object's fields rather than only the ones this lambda reads. So
    /// a second lambda anywhere in the same method that captures <c>this</c> — and
    /// <c>window.RerunRequested += async () =&gt; await ShowFlightReport()</c> does exactly that — puts
    /// <c>&lt;&gt;4__this</c> on the shared closure and the guard rejects a lambda that touches nothing UI-owned.
    /// A static method has no <c>this</c> to capture, so the guard stays on rather than being opted out of.</para>
    /// </summary>
    private static Task<FlightReport> MeasureFlight(
        ShipDocument doc, Catalog catalog, DataIndex index, string designName) =>
        Ui.OffThread(() =>
        {
            var grid = ShipGrid.FromDocument(doc, catalog);
            // The RCS figure comes from the propulsion port rather than being re-derived: the game's mixed engine
            // mode fires RCS alongside the rotors, so the flight report needs the same number the Ship Rating shows.
            return new FlightReport(
                FlightDynamics.Measure(doc, grid, catalog),
                Atmosphere.LoadBodies(index),
                Propulsion.Estimate(doc, grid, catalog).RcsThrustNewtons,
                designName);
        });

    // ---- Bill of materials ----

    private void OnMaterialsClick(object sender, RoutedEventArgs e)
    {
        if (_doc is null) return;
        if (_doc.Placements.Count == 0)
        {
            Dlg.Show(this, "Place some parts before opening the bill of materials.", "Bill of Materials",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Scope to the current selection when one is active, else the whole ship.
        var selection = Board.SelectedPlacements();
        var (parts, scope) = selection.Count > 0
            ? ((IEnumerable<Placement>)selection, $"selection · {selection.Count} part{(selection.Count == 1 ? "" : "s")}")
            : (_doc.Placements, "whole ship");

        var bom = BillOfMaterials.Compute(_doc, parts);
        // The retrofit mode always nets the WHOLE design against the starting ship, so it gets its own bill even
        // when the scoped one is a selection. Computing it up front costs a walk of the placements and keeps the
        // report from needing the document at all.
        var whole = selection.Count > 0 ? BillOfMaterials.ComputeAll(_doc) : bom;
        new MaterialsReportWindow(bom, scope, whole, _catalog is null ? null : PickRetrofitSource) { Owner = this }
            .ShowDialog();
    }

    /// <summary>
    /// Read a ship in purely to measure it: a saved design, a ship template, or a ship in a save. Nothing is
    /// imported — the document on the canvas is untouched and the ship read here is dropped as soon as its bill
    /// is counted, which is why none of this opens a tab of its own.
    ///
    /// <para>Null when the user backed out, or when the read failed and has already been reported.</para>
    /// </summary>
    private async Task<RetrofitPick?> PickRetrofitSource(Window owner)
    {
        if (_catalog is null) return null;

        var kindDlg = new ShipSourceDialog("Retrofit from which ship?",
            "The ship you'd be converting. Ostraplan reads its layout to count what it already carries; "
            + "nothing is imported and your design is not touched.") { Owner = owner };
        if (kindDlg.ShowDialog() != true || kindDlg.Selected is not { } kind) return null;

        return kind switch
        {
            ShipSourceKind.Design => RetrofitFromDesign(owner),
            ShipSourceKind.Template => RetrofitFromTemplate(owner),
            ShipSourceKind.Save => await RetrofitFromSave(owner),
            _ => null,
        };
    }

    private RetrofitPick? RetrofitFromDesign(Window owner)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose the design to retrofit from",
            Filter = "Ostraplan ship (*.oplan)|*.oplan|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(owner) != true) return null;

        try
        {
            var file = OplanFile.Load(dlg.FileName);
            var (doc, missing) = file.ToDocument(_catalog!);
            // A starting ship missing parts would under-count what it carries, which reads as material you need to
            // buy and already own. Say so rather than quietly producing a wrong bill.
            if (missing.Count > 0)
                Dlg.Warn(owner, "That design is missing mods",
                    $"{missing.Count} part(s) in “{DesignName(file, dlg.FileName)}” aren't in your current game and "
                    + "mods data, so they can't be counted.\n\n"
                    + "The retrofit bill will over-state what you need to obtain by that much.");
            return new RetrofitPick(DesignName(file, dlg.FileName), BillOfMaterials.ComputeAll(doc));
        }
        catch (Exception ex)
        {
            Dlg.Error(owner, "Retrofit", "Couldn't read that design.\n\n" + ex.Message);
            return null;
        }
    }

    /// <summary>The friendliest name for a design read in for comparison: the name it carries, else its filename.</summary>
    private static string DesignName(OplanFile file, string path) =>
        file.Meta.Name is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(path);

    private RetrofitPick? RetrofitFromTemplate(Window owner)
    {
        if (_index is null) return null;

        var ships = TemplateImport.ListShipFiles(_index);
        if (ships.Count == 0)
        {
            Dlg.Info(owner, "Retrofit", "No ship templates were found in your game data.");
            return null;
        }

        var browser = new TemplateBrowserDialog(ships) { Title = "Retrofit from which template?", Owner = owner };
        if (browser.ShowDialog() != true || browser.Selected is not { } entry) return null;

        try
        {
            // Structure only: the bill counts placed parts, so reading a hold full of cargo would be work for
            // nothing on what is already the slowest half of picking a starting ship.
            var result = TemplateImport.LoadFile(entry.Path, _catalog!, ImportOptions.LayoutOnly);
            return new RetrofitPick(result.ShipName, BillOfMaterials.ComputeAll(result.Doc));
        }
        catch (Exception ex)
        {
            Dlg.Error(owner, "Retrofit", "Couldn't read that ship template.\n\n" + ex.Message);
            return null;
        }
    }

    private async Task<RetrofitPick?> RetrofitFromSave(Window owner)
    {
        if (_env is null) return null;

        var saves = SaveImport.ListSaves(_env);
        if (saves.Count == 0)
        {
            Dlg.Info(owner, "Retrofit", "No save games found in your Ostranauts Saves folder.");
            return null;
        }

        var picker = new SavePickerDialog(saves, "Retrofit from a ship in which save?",
            "Reads the ship's layout to count what it carries. Nothing is written and nothing is imported.",
            "Choose save") { Owner = owner };
        if (picker.ShowDialog() != true || picker.Selected is not { } save) return null;

        var ships = SaveImport.ListPlayerShips(save.ZipPath);
        if (ships.Count == 0)
        {
            Dlg.Warn(owner, "Retrofit",
                "Couldn't find a ship in that save (no owned ships and no current ship on record).");
            return null;
        }

        var shipDlg = new ShipChoiceDialog(save.Name, ships) { Owner = owner };
        if (shipDlg.ShowDialog() != true || shipDlg.Selected is not { } chosen) return null;

        var (catalog, zip, regId) = (_catalog!, save.ZipPath, chosen.RegId);
        try
        {
            var result = await Ui.OffThread(
                () => SaveImport.ImportShipLayout(zip, regId, catalog, ImportOptions.LayoutOnly));
            return new RetrofitPick(chosen.Name, BillOfMaterials.ComputeAll(result.Doc));
        }
        catch (Exception ex)
        {
            Dlg.Error(owner, "Retrofit", "Couldn't read that ship.\n\n" + ex.Message);
            return null;
        }
    }

    private void UpdateProblems(List<Problem> problems)
    {
        _lastProblems = problems;

        // Split into shown vs. dismissed (a warning whose DismissKey the user hid — see ShipDocument.DismissedAlerts).
        var dismissedKeys = _doc?.DismissedAlerts ?? [];
        var shown = problems.Where(p => p.DismissKey is null || !dismissedKeys.Contains(p.DismissKey)).ToList();

        var blocking = shown.Where(p => p.Severity == ProblemSeverity.Blocking).ToList();
        var warnings = shown.Where(p => p.Severity == ProblemSeverity.Warning).ToList();

        // hazard-tint the tiles of every socket-illegal / unconstructible placement (NOT the airtightness leak
        // points — those are a dismissible warning with their own on-demand "Show" highlight, not a red tint).
        Board.SetIllegalCells([.. shown.Where(p => p.Cells is not null && p.DismissKey is null).SelectMany(p => p.Cells!).Distinct()]);

        BadgeBlocking.Visibility = blocking.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BadgeBlockingText.Text = $"!  {blocking.Count}";
        BadgeBlocking.ToolTip = blocking.Count > 0 ? string.Join("\n", blocking.Select(p => p.Title)) : null;
        BadgeWarning.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BadgeWarningText.Text = $"⚠  {warnings.Count}";
        BadgeWarning.ToolTip = warnings.Count > 0 ? string.Join("\n", warnings.Select(p => p.Title)) : null;

        ProblemsPanel.Children.Clear();
        if (shown.Count == 0)
        {
            ProblemsPanel.Children.Add(new TextBlock
            {
                Text = dismissedKeys.Count > 0 ? "None showing." : "None found.",
                Foreground = ThemeManager.Good,
            });
            ProblemsPanel.Children.Add(new TextBlock
            {
                Text = "Placement legality is checked live. Run Ship Rating for the full room, airtightness and certification report.",
                Foreground = ThemeManager.Dim,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
            });
        }
        else
        {
            foreach (var problem in shown.OrderByDescending(p => p.Severity))
                ProblemsPanel.Children.Add(ProblemRow(problem));
        }

        // "Restore Alerts" appears under the list whenever anything is dismissed (whether or not it currently applies).
        if (dismissedKeys.Count > 0)
        {
            var restore = new Button
            {
                Content = $"Restore Alerts ({dismissedKeys.Count})",
                Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left, Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Bring back the warnings you dismissed.",
            };
            restore.Click += (_, _) => RestoreDismissedAlerts();
            ProblemsPanel.Children.Add(restore);
        }
    }

    /// <summary>
    /// One problem as an expandable row: a coloured title, action buttons (Show/View, and Dismiss for a
    /// dismissible warning), and the detail revealed on expand.
    ///
    /// <para>The title and the buttons are <b>stacked</b>, not side by side. They used to share a DockPanel with
    /// the buttons docked right, which works until the title is long: the inspector is a narrow column, the two
    /// fixed-width buttons take their share of it first, and the title wraps into whatever is left — three or
    /// four characters per line for "1 sealed-off compartment". Giving the title the full width and putting the
    /// buttons under it costs one row of height and makes every problem legible at any panel width.</para>
    /// </summary>
    private FrameworkElement ProblemRow(Problem problem)
    {
        var color = problem.Severity == ProblemSeverity.Blocking ? ThemeManager.Bad : ThemeManager.Warn;

        var header = new StackPanel { Margin = new Thickness(0, 1, 0, 2) };
        header.Children.Add(new TextBlock
        {
            Text = "● " + problem.Title, Foreground = color, TextWrapping = TextWrapping.Wrap,
        });

        // One row under the title, left-aligned and in reading order (Show/View first, then Dismiss).
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(11, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        if (problem.Cells is { Count: > 0 } cells)
        {
            // A leak/airtightness warning (dismissible) highlights its leak points AND focuses; a plain illegal
            // problem (already hazard-tinted) just pans/zooms into view.
            var isLeak = problem.DismissKey is not null;
            var btn = ActionButton(isLeak ? "Show" : "View",
                isLeak ? "Highlight the leak points and bring them into view" : "Pan and zoom the view to this problem");
            btn.Click += (_, e) =>
            {
                e.Handled = true;
                if (isLeak) Board.SetLeakCells(cells);
                Board.FocusTiles(cells);
            };
            actions.Children.Add(btn);
        }
        if (problem.DismissKey is { } key)
        {
            var dismiss = ActionButton("Dismiss", "Hide this warning (restore it later with Restore Alerts).");
            dismiss.Click += (_, e) => { e.Handled = true; DismissAlert(key); };
            actions.Children.Add(dismiss);
        }
        if (actions.Children.Count > 0) header.Children.Add(actions);

        return new Expander
        {
            Header = header,
            Foreground = color,
            Margin = new Thickness(0, 1, 0, 1),
            Content = new TextBlock
            {
                Text = problem.Detail, Foreground = ThemeManager.Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(11, 2, 2, 4),
            },
        };

        // MinWidth 0 because the Fluent default is wide enough that two of them would not fit side by side in a
        // narrow inspector, which is the shape this layout exists to fix.
        static Button ActionButton(string content, string tip) => new()
        {
            Content = content, ToolTip = tip, FontSize = 11, MinWidth = 0,
            Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 6, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
    }

    /// <summary>Dismiss a warning by key (persisted in the .oplan), clear any leak highlight, and re-render the
    /// problems from the last scan — no re-scan needed, since dismissal only filters the display.</summary>
    private void DismissAlert(string key)
    {
        if (_doc is null || !_doc.DismissAlert(key)) return;
        Board.SetLeakCells([]);   // if the dismissed warning's leak points were showing, drop them
        _stateDirty = true;
        RefreshChrome();
        UpdateProblems(_lastProblems);
    }

    /// <summary>Restore every dismissed warning (the "Restore Alerts" button).</summary>
    private void RestoreDismissedAlerts()
    {
        if (_doc is null || !_doc.RestoreAlerts()) return;
        _stateDirty = true;
        RefreshChrome();
        UpdateProblems(_lastProblems);
    }

    private void RefreshChrome()
    {
        BtnUndo.IsEnabled = _stack.CanUndo;
        BtnRedo.IsEnabled = _stack.CanRedo;
        var name = _doc?.FilePath is { } f ? Path.GetFileNameWithoutExtension(f) : _meta.Name;
        var star = _stack.Dirty || _stateDirty ? " *" : "";
        var incomplete = _unresolvedParts.Count > 0 ? "  ⚠ MISSING MODS — read-only" : "";
        TxtDoc.Text = name + star + incomplete;
        Title = $"Ostraplan v{AppVersion} — {name}{star}{incomplete}";
        SyncDocumentKindChrome();
        RefreshDocTabs();   // the strip carries the same name and the same unsaved star, for every open design
    }

    /// <summary>
    /// Point the vessel-only chrome at what the document actually is. A residence has no drive and no nav
    /// (GAME-INTERNALS §19), so the nav-console checklist has nothing to check and would report a near-total
    /// failure on a design where nothing is wrong; it is disabled with the reason on its tooltip rather than
    /// left to produce that. The Ship Rating button stays live because its report is also the rooms,
    /// certification and airtightness report, all of which apply unchanged — it re-headlines instead (see
    /// <see cref="RatingReportWindow"/>). Flight Dynamics is gated where its menu is built.
    /// </summary>
    private void SyncDocumentKindChrome()
    {
        var residence = _doc?.IsResidence == true;

        BtnRating.Content = residence ? "Residence Report" : "Ship Rating";
        BtnRating.ToolTip = residence
            ? "Analyse rooms, airtightness and certification for this residence"
            : "Analyse rooms, airtightness, certification and the Ship Rating for the current design";

        // Never re-enable mid-analysis: ShowRatingReport/ShowDiagnosticsReport own the button state while a run
        // is in flight, and this method is reached from OnDocChanged, which a completing run can race.
        if (!_analysing) BtnDiagnostics.IsEnabled = !residence;
        BtnDiagnostics.ToolTip = residence
            ? "Not applicable to a residence: the checklist reads a nav console, a drive and a transponder, none "
              + "of which a residence has"
            : "The game's own nav-console checklist: transponder, antenna, reactor, thrusters, power and life support";
    }

    private bool ConfirmDiscardChanges()
    {
        if (_doc is null || (!_stack.Dirty && !_stateDirty)) return true;
        var name = _doc.FilePath is { } f ? Path.GetFileNameWithoutExtension(f) : _meta.Name;

        // An incomplete design (missing-mod parts) is saveable too, on confirmation — Save() asks about dropping
        // them, and Cancel there falls back out through here. So it needs no special case: offering a Save that
        // silently failed was the reason this branch existed.
        return Dlg.Choose(this, DlgKind.Info, "Save changes?",
            $"“{name}” has unsaved changes.", "Save", "Don't save") switch
        {
            MessageDialog.Choice.Primary => Save(),
            MessageDialog.Choice.Secondary => true,
            _ => false,
        };
    }

    private bool Save()
    {
        if (_doc is null || _index is null) return false;
        if (!GuardIncompleteSave()) return false;
        if (_doc.FilePath is null) return SaveAs();
        try
        {
            var file = OplanFile.FromDocument(_doc, _index, _meta);
            file.ViewRot = Board.ViewRot;   // reopen in the orientation it was saved in
            file.Save(_doc.FilePath);
        }
        catch (Exception ex)
        {
            Dlg.Show(this, ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        _stack.MarkSaved();
        _stateDirty = false;
        AuditLog.Add($"Saved {_doc.FilePath}.");
        _settings.Touch(_doc.FilePath);
        _settings.Save();
        return true;
    }

    private bool SaveAs()
    {
        if (_doc is null) return false;
        if (!GuardIncompleteSave()) return false;
        var dlg = new SaveFileDialog
        {
            Filter = "Ostraplan ship (*.oplan)|*.oplan",
            FileName = string.Join("_", _meta.Name.Split(Path.GetInvalidFileNameChars())),
        };
        if (dlg.ShowDialog(this) != true) return false;
        _doc.FilePath = dlg.FileName;
        _meta.Name = Path.GetFileNameWithoutExtension(dlg.FileName);
        return Save();
    }

    /// <summary>
    /// Saving a design whose .oplan referenced parts from mods that aren't loaded <b>drops those parts for good</b>
    /// — the file is rewritten from what's on the canvas, and they never made it there. That is only ever a
    /// mistake or a decision, so ask rather than assume either: the design is read-only until the user says which.
    ///
    /// <para>Confirming is a real answer, not a bypass. Dropping the parts is exactly right when the mod is one
    /// the user has deliberately moved off, and refusing outright left them with no way to say so short of hand
    /// editing the file. So the choice sticks: the parts leave <see cref="_unresolvedParts"/>, the design becomes
    /// complete as it now stands, and the standing warning clears.</para>
    ///
    /// <para>Returns true when it's safe to save. See <see cref="_unresolvedParts"/>.</para>
    /// </summary>
    private bool GuardIncompleteSave()
    {
        if (_unresolvedParts.Count == 0) return true;

        var dropped = _unresolvedParts;
        if (!Dlg.Confirm(this, DlgKind.Danger, "Save without the missing-mod parts?",
                $"{dropped.Count} part(s) in this design come from mods that aren't loaded, so they aren't on the canvas:\n\n" +
                FormatMissingDefs(dropped) +
                "\n\nSaving rewrites the design as it stands, which drops them for good.\n\n" +
                "If you still want them, cancel — enable the mods (run Ostrasort to confirm they're subscribed and " +
                "enabled) and reopen this design, and they'll come back.\n\n" +
                "If you're done with those mods, dropping the parts is exactly what you want.",
                "Save without them"))
            return false;

        var names = string.Join(", ", dropped.Select(m => m.Def).Where(d => d.Length > 0).Distinct());
        AuditLog.Add($"Dropped {dropped.Count} missing-mod part(s) from \"{_meta.Name}\" on the user's say-so: {names}");
        _unresolvedParts = [];   // decided: the design is complete as it now stands, so the read-only hold lifts
        RefreshChrome();
        return true;
    }

    // ---- auto-save ----

    /// <summary>
    /// Take a rotating snapshot of the open design (see <see cref="AutoSaveStore"/>). Opt-in, and deliberately not a
    /// save: it writes into <c>%APPDATA%\Ostraplan\autosave</c> and never touches the user's own .oplan, so the
    /// unsaved-changes star stays up and Ctrl+S remains the only thing that writes the file they opened.
    ///
    /// <para>A tick is skipped when there is nothing worth snapshotting, or when taking one would be wrong: no
    /// document, no unsaved changes, a design held read-only because its mods are missing (the .oplan on disk is the
    /// complete one, and a snapshot would be the version with the parts dropped), or an engine reading the live
    /// document off-thread.</para>
    /// </summary>
    private void RunAutoSave()
    {
        if (_index is null || _freeze.IsFrozen) return;
        // Every open design, not only the one on screen. A tab left in the background is exactly the work a crash
        // would be most annoying to lose, and each design keeps its own rotation (see AutoSaveStore).
        foreach (var session in _sessions) RunAutoSave(session);
    }

    /// <inheritdoc cref="RunAutoSave()"/>
    private void RunAutoSave(DocumentSession session)
    {
        if (session.Doc is not { } doc || _index is null) return;
        if (!session.Dirty) return;
        if (session.UnresolvedParts.Count > 0) return;

        var name = doc.FilePath is { } f ? Path.GetFileNameWithoutExtension(f) : session.Meta.Name;
        try
        {
            var file = OplanFile.FromDocument(doc, _index, session.Meta);
            file.ViewRot = session.Board.ViewRot;   // a recovered snapshot reopens in the orientation it was taken in
            var written = AutoSaveStore.Default.Write(
                file, name, doc.FilePath, AutoSaveStore.ClampKeep(_settings.AutoSaveKeep), DateTime.Now,
                session.UntitledSlot);
            AuditLog.Add($"Auto-saved \"{name}\" to {written}.");
        }
        catch (Exception ex)
        {
            AuditLog.Add($"Auto-save of \"{name}\" failed: {ex.Message}");
            if (_autoSaveWarned) return;
            _autoSaveWarned = true;   // said once, then only logged — a broken folder must not interrupt every interval
            Dlg.Warn(this, "Auto-save failed",
                "Ostraplan could not write an auto-save snapshot:\n\n" + ex.Message + "\n\n" +
                "Auto-save stays on and keeps trying, but this won't be reported again this session. " +
                "Save your work with Ctrl+S.");
        }
    }

    /// <summary>Start, stop, or re-interval the auto-save timer from the current settings. Called at startup and
    /// after any change to the switch or the interval.</summary>
    private void RestartAutoSaveTimer()
    {
        _autoSaveTimer.Stop();
        if (!_settings.AutoSave) return;
        _autoSaveTimer.Interval = TimeSpan.FromMinutes(AutoSaveStore.ClampMinutes(_settings.AutoSaveMinutes));
        _autoSaveTimer.Start();
    }

    private void SetAutoSaveEnabled(bool on)
    {
        _settings.AutoSave = on;
        _settings.Save();
        _autoSaveWarned = false;   // a fresh opt-in earns a fresh warning if the store still can't be written
        RestartAutoSaveTimer();
        AuditLog.Setting("Auto-save", on
            ? $"on, every {AutoSaveStore.ClampMinutes(_settings.AutoSaveMinutes)} min, keeping " +
              $"{AutoSaveStore.ClampKeep(_settings.AutoSaveKeep)} per design"
            : "off");
    }

    /// <summary>Persist the auto-save interval and restart the timer on it, so a change takes effect from now rather
    /// than after the interval already running has elapsed.</summary>
    private void SetAutoSaveMinutes(double minutes)
    {
        // AwayFromZero, to match how the row's numeric box formats the same slider value
        var value = AutoSaveStore.ClampMinutes((int)Math.Round(minutes, MidpointRounding.AwayFromZero));
        if (value == _settings.AutoSaveMinutes) return;
        _settings.AutoSaveMinutes = value;
        _settings.Save();
        RestartAutoSaveTimer();
    }

    /// <summary>Persist how many snapshots each design keeps. Lowering it takes effect on that design's next
    /// snapshot, which is when its set is next rotated.</summary>
    private void SetAutoSaveKeep(double keep)
    {
        var value = AutoSaveStore.ClampKeep((int)Math.Round(keep, MidpointRounding.AwayFromZero));
        if (value == _settings.AutoSaveKeep) return;
        _settings.AutoSaveKeep = value;
        _settings.Save();
    }

    /// <summary>
    /// The File ▸ "Auto-save" submenu: the opt-in switch, the interval, how many snapshots each design keeps, and
    /// recovery. Built fresh whenever the File menu opens, so the switch, the slider positions and the snapshot count
    /// are all read live.
    ///
    /// <para>The switch is a real check box reading "Enabled"/"Disabled" rather than a menu tick, because a tick that
    /// is simply absent reads as an unticked <i>option</i> rather than an <i>off feature</i>. Turning it off greys the
    /// interval and keep rows on the spot (the menu stays open through the toggle), so the whole submenu says the
    /// feature is inactive rather than leaving two live-looking sliders that do nothing. Recovery stays available
    /// either way: snapshots already taken are still recoverable once auto-save is switched off.</para>
    /// </summary>
    private MenuItem AutoSaveMenuItem()
    {
        var enabled = _settings.AutoSave;
        var minutes = AutoSaveStore.ClampMinutes(_settings.AutoSaveMinutes);

        // The parent row states the setting too, so the File menu answers "is auto-save on?" without opening this.
        var menu = new MenuItem { Header = enabled ? $"Auto-save (every {minutes} min)" : "Auto-save (off)" };

        var interval = MenuSliderRow("Every", AutoSaveStore.MinIntervalMinutes, AutoSaveStore.MaxIntervalMinutes,
            minutes, "0", SetAutoSaveMinutes, suffix: "min");
        var keep = MenuSliderRow("Keep", AutoSaveStore.MinKeep, AutoSaveStore.MaxKeep,
            AutoSaveStore.ClampKeep(_settings.AutoSaveKeep), "0", SetAutoSaveKeep, suffix: "per design");
        interval.IsEnabled = keep.IsEnabled = enabled;

        // The box is the indicator, not the click target: a menu row swallows a click aimed at a control in its
        // header, so the whole row toggles and the box just reflects the state.
        var box = new CheckBox
        {
            IsChecked = enabled,
            Content = enabled ? "Enabled" : "Disabled",
            IsHitTestVisible = false,
            Focusable = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 3, 8, 3),
        };
        var toggle = new MenuItem { Header = box, StaysOpenOnClick = true };
        toggle.Click += (_, _) =>
        {
            var on = !_settings.AutoSave;
            SetAutoSaveEnabled(on);
            box.IsChecked = on;
            box.Content = on ? "Enabled" : "Disabled";
            interval.IsEnabled = keep.IsEnabled = on;
        };

        menu.Items.Add(toggle);
        menu.Items.Add(interval);
        menu.Items.Add(keep);
        menu.Items.Add(new Separator());

        var snapshots = AutoSaveStore.Default.List();
        menu.Items.Add(MenuAction($"Recover auto-save… ({snapshots.Count})", () => RecoverAutoSave(snapshots),
            enabled: snapshots.Count > 0));
        menu.Items.Add(MenuAction("Open auto-save folder", OpenAutoSaveFolder));
        return menu;
    }

    /// <summary>
    /// Recover an auto-save snapshot: pick one, then load it as the active document.
    ///
    /// <para>A snapshot records the design's own file path (<see cref="OplanFile.AutoSaveOf"/>), so a recovered design
    /// goes back onto that file and Ctrl+S writes where the user expects. It arrives with unsaved changes either way,
    /// because what is now on the canvas is not what is on disk: keeping the recovery is the user's call, and nothing
    /// is written until they make it.</para>
    /// </summary>
    private void RecoverAutoSave(IReadOnlyList<AutoSaveEntry> entries)
    {
        if (_catalog is null || entries.Count == 0) return;

        var picker = new AutoSaveRecoveryDialog(entries, DateTime.Now) { Owner = this };
        if (picker.ShowDialog() != true || picker.Selected is not { } entry) return;

        OplanFile file;
        List<OplanPart> missing;
        ShipDocument doc;
        try
        {
            file = OplanFile.Load(entry.Path);
            (doc, missing) = file.ToDocument(_catalog);
        }
        catch (Exception ex)
        {
            Dlg.Show(this, ex.Message, "Recover failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        AdoptLoadedDocument(file, doc, missing, file.AutoSaveOf, dirty: true);
        AuditLog.Add($"Recovered the auto-save snapshot {entry.Path}.");

        // Same as reopening a save-derived .oplan: a snapshot written by an older build carries no container
        // contents of its own, so they are re-read once from the save it named.
        AttachLegacySavedCargoAsync(doc, file.Source);

        var onto = file.AutoSaveOf is { } path
            ? $"Saving writes it back to {Path.GetFileName(path)}."
            : "This design had never been saved, so saving will ask where to put it.";
        var incomplete = missing.Count > 0
            ? $"\n\nIt uses {missing.Count} part(s) from mods that aren't loaded, so it is held read-only until you " +
              "enable them and reopen. See the warning in the title bar."
            : "";
        Dlg.Info(this, "Recovered",
            $"Recovered \"{_meta.Name}\" as it stood at {entry.SavedAt:HH:mm} on {entry.SavedAt:ddd d MMM}.\n\n" +
            $"It is loaded as unsaved changes — nothing has been written yet. {onto}{incomplete}");
    }

    /// <summary>Open the auto-save folder in Explorer, so the snapshots can be inspected, copied or cleared out by
    /// hand (rotation only ever prunes a design that is still being snapshotted).</summary>
    private void OpenAutoSaveFolder()
    {
        try
        {
            Directory.CreateDirectory(AutoSaveStore.Default.Root);
            OpenUrl(AutoSaveStore.Default.Root);
        }
        catch (Exception ex) { Dlg.Error(this, "Auto-save", ex.Message); }
    }

    private void OpenFile()
    {
        if (_catalog is null) return;
        var dlg = new OpenFileDialog { Filter = "Ostraplan ship (*.oplan)|*.oplan|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) != true) return;

        OplanFile file;
        List<OplanPart> missing;
        ShipDocument doc;
        try
        {
            file = OplanFile.Load(dlg.FileName);
            (doc, missing) = file.ToDocument(_catalog);
        }
        catch (Exception ex)
        {
            Dlg.Show(this, ex.Message, "Open failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        AdoptLoadedDocument(file, doc, missing, dlg.FileName, dirty: false);
        _settings.Touch(dlg.FileName);
        _settings.Save();
        AuditLog.Add($"Opened {dlg.FileName}.");

        // A design written by an older build stored only the layout and left its container contents in the save
        // it named. Re-read them once so the inventory viewer works right away, after which the design owns them
        // and the file stops naming a save at all. Eager, off-thread, and it reports nothing if the save has moved.
        AttachLegacySavedCargoAsync(doc, file.Source);

        if (missing.Count > 0)
            Dlg.Warn(this, "This design is missing mods",
                $"{_meta.Name} uses {missing.Count} part(s) that aren't in your current game and mods data.\n" +
                "They were left out, so this design is incomplete.\n\n" +
                FormatMissingDefs(missing) +
                "\n\nIt depends on these mods.\n\n" +
                FormatModDeps(file.Mods) +
                "\n\nTo get them back: install or subscribe to those mods and enable them, then reopen this design.\n" +
                "Run Ostrasort to confirm they're subscribed, enabled, and in a working load order.\n\n" +
                "Until then the design is held read only — saving would rewrite it without those parts, and building " +
                "over the space where they belong (or moving parts into it) can produce a ship that's invalid in game.\n\n" +
                "If you're done with those mods and want the parts gone, Save and confirm: it will drop them and the " +
                "design becomes editable as it stands.");
    }

    /// <summary>
    /// Swap a loaded <c>.oplan</c> in as the active document — the shared tail of Open and auto-save recovery.
    ///
    /// <para><paramref name="filePath"/> is the file Ctrl+S will write, or null to leave the design untitled.
    /// <paramref name="dirty"/> starts it with unsaved changes, which a recovered snapshot always has: what is on the
    /// canvas is by definition not what is on disk.</para>
    /// </summary>
    private void AdoptLoadedDocument(OplanFile file, ShipDocument doc, List<OplanPart> missing, string? filePath, bool dirty)
    {
        if (_catalog is not { } catalog) return;
        BeginDocumentInNewTab();
        CloseReports(_active);

        // Designs saved before the primary-airlock convention gain one at the origin. IsLocked reads the port's
        // CONDITIONS (Catalog.IsPrimaryDocksys), so a ship whose airlock is pried open, damaged or modded already
        // counts as having one; matching the def name alone used to seed a SECOND airlock here, which moved the
        // written grid frame and left the ship unable to dock.
        if (catalog.ByDefName.ContainsKey(Catalog.PrimaryDocksysDef) && !doc.Placements.Any(doc.IsLocked))
            new PlaceCommand(new Placement { DefName = Catalog.PrimaryDocksysDef, X = 0, Y = 0 }).Do(doc);

        AttachDoc(doc);
        doc.FilePath = filePath;
        _meta = file.Meta;
        _stateDirty = dirty;
        _saveContext = null;   // a reopened design is bound to no save; the write-back asks which ship it replaces
        _unresolvedParts = missing;   // a design missing its mods is incomplete: read-only until they're enabled
        _stack.Reset();
        Board.SetDocument(doc);
        Board.SetViewRot(file.ViewRot);   // restore the saved plan-view orientation
        Board.FitContentWhenReady();      // a tab just built has no layout yet; frame it as soon as it has one
        OnDocChanged();
        UpdateInspector();
    }

    /// <summary>
    /// Re-attach the container contents of a design written by a build that did not store them.
    ///
    /// <para>Up to 0.92.x an <c>.oplan</c> imported for editing recorded the save and ship it came from and
    /// nothing of what its containers held, so reopening one meant going and finding that save. A design now
    /// carries its own contents and names no save, but a file already on disk does not, and this is the only
    /// thing that says where they were. So it runs for a legacy <paramref name="src"/> and for nothing else:
    /// re-locate that save off the UI thread, hang each container's contents back on its placement (matched by
    /// <see cref="Placement.OriginStrID"/>), and cache the context so a write-back in the same sitting skips a
    /// second re-locate. Saving the design then writes the contents into the file and drops the source, after
    /// which this never runs for it again.</para>
    ///
    /// <para>A moved or unreadable save just leaves the contents unattached, with no report: the design still
    /// opens, edits and exports, and the write-back is where a save that cannot be found is worth saying so.</para>
    /// </summary>
    private async void AttachLegacySavedCargoAsync(ShipDocument doc, OplanSource? src)
    {
        if (src is null || src.SaveName.Length == 0 || src.RegId.Length == 0) return;
        if (_catalog is null || _env is null) return;
        var (env0, catalog0, save0, reg0) = (_env, _catalog, src.SaveName, src.RegId);
        var session = _active;   // the tab this design is in; the user is free to work in another one meanwhile
        SaveShipContext? ctx;
        try
        {
            ctx = await Ui.OffThread(() =>
            {
                var match = SaveImport.ListSaves(env0).FirstOrDefault(s => string.Equals(s.Name, save0, StringComparison.Ordinal));
                return match is null ? null : SaveEditImport.RelocateContext(match.ZipPath, match.Name, reg0, catalog0);
            });
        }
        catch { return; }   // unreadable ship: leave cargo unattached rather than nag on open

        // Against the session's own document, not the active one: switching tabs while the save is being re-located
        // must not cost this design its cargo. What is checked is that the tab still holds the design that asked.
        if (ctx is null || !ReferenceEquals(session.Doc, doc)) return;   // save gone, or that design was replaced
        foreach (var p in doc.Placements)
            if (p.OriginStrID is { } id && !doc.IsCargoEdited(p) && ctx.CargoByOrigin.TryGetValue(id, out var forest))
                p.Cargo = forest;   // skip edited containers — their .oplan snapshot is authoritative
        session.SaveContext = ctx;
        if (ReferenceEquals(session, _active)) UpdateInspector();
    }

    /// <summary>Up to a dozen distinct missing def names, bulleted, with an "… and N more" tail.</summary>
    private static string FormatMissingDefs(IReadOnlyList<OplanPart> missing)
    {
        var names = missing.Select(m => m.Def).Where(d => d.Length > 0).Distinct().ToList();
        var shown = string.Join("\n", names.Take(12).Select(n => "   • " + n));
        return names.Count > 12 ? shown + $"\n   … and {names.Count - 12} more" : shown;
    }

    /// <summary>The design's recorded mod dependencies (friendly name, else the loading_order entry), bulleted.</summary>
    private static string FormatModDeps(IReadOnlyList<OplanMod> mods) =>
        mods.Count == 0
            ? "   • (the design records no mod dependencies, so the part may be from a mod you since removed)"
            : string.Join("\n", mods.Select(m => "   • " + (m.Name.Length > 0 ? m.Name : m.Entry)));

    // ---- edits ----

    private void OnStrokeCommitted(IReadOnlyList<IDocCommand> stroke)
    {
        // the canvas already executed these live during the drag; record as ONE undo step
        if (_doc is null || stroke.Count == 0) return;
        _stack.PushExecuted(stroke.Count == 1 ? stroke[0] : new CompositeCommand(stroke.ToList()));
        RecordRecentUse();   // the armed brush just landed at least one part — it's now "recently used"
    }

    private void OnMoveRequested(IReadOnlyList<Placement> placements, int dx, int dy)
    {
        if (_doc is null || placements.Count == 0) return;
        _stack.Push(_doc, new MoveCommand(placements, dx, dy));
    }

    /// <summary>A symmetric move: the canvas has already computed each part's mirrored target pose. One undo step.</summary>
    private void OnPosesRequested(IReadOnlyList<(Placement P, int X, int Y, int Rot)> poses)
    {
        if (_doc is null || poses.Count == 0) return;
        _stack.Push(_doc, new SetPosesCommand(poses.Select(t => (t.P, t.X, t.Y, t.Rot)).ToList()));
    }

    private void DeleteSelection()
    {
        if (_doc is null) return;
        if (Board.SelectedLoose is { } loose)   // a selected loose floor item — remove just it
        {
            _stack.Push(_doc, new RemoveLooseCommand(loose));
            Board.ClearLooseSelection();
            UpdateInspector();
            return;
        }
        var selected = Board.SelectedPlacements().Where(p => !_doc.IsLocked(p)).ToList();
        if (selected.Count == 0) return;
        _stack.Push(_doc, new RemoveCommand(selected));
        Board.SelectedIds.Clear();
        UpdateInspector();
    }

    // ---- zones ----

    // ---- power (PowerViz) ----

    /// <summary>The zone overlay was toggled: just refresh the toolbar highlight (zones are painted data, nothing to
    /// recompute).</summary>
    private void OnShowZonesChanged() => SyncViewToggles();

    /// <summary>PowerViz was toggled: when turned on, kick a scan so the overlay computes (the network flood only
    /// runs while the overlay is on).</summary>
    private void OnShowPowerChanged()
    {
        SyncViewToggles();
        if (Board.ShowPower) ScheduleScan();
    }

    // ---- rooms (RoomViz) ----

    /// <summary>RoomViz was toggled: when turned on, kick a scan so the compartments certify (the flood fill and
    /// certification only run while the overlay is on).</summary>
    private void OnShowRoomsChanged()
    {
        SyncViewToggles();
        if (Board.ShowRooms) ScheduleScan();
    }

    // ---- crew access (WalkViz) ----

    /// <summary>WalkViz was toggled: when turned on, kick a scan so the walk zones and device reach compute (the
    /// analysis only runs while the overlay is on).</summary>
    private void OnShowWalkChanged()
    {
        SyncViewToggles();
        if (Board.ShowWalk) ScheduleScan();
    }

    // ---- lighting (Light Viz) ----

    /// <summary>Light Viz was toggled: when turned on, kick a scan so the interior lighting computes (the flood only
    /// runs while the overlay is on).</summary>
    private void OnShowLightChanged()
    {
        SyncViewToggles();
        if (Board.ShowLight) ScheduleScan();
    }

    private string? _defaultHint;   // the status-bar hint to restore when wire mode turns off

    /// <summary>Wire mode toggled: swap the status-bar hint for the wiring instructions (and back).</summary>
    private void OnWireModeChanged()
    {
        SyncViewToggles();
        UpdateModeHint();
    }

    /// <summary>The status-bar hint belongs to whichever editing mode is on, so both modes route through here rather
    /// than each writing the bar and the later toggle winning. Wire mode takes precedence: its clicks intercept
    /// everything, Surfaces mode only changes what a click lands on.</summary>
    private void UpdateModeHint()
    {
        _defaultHint ??= TxtHint.Text;
        TxtHint.Text =
            Board.WireMode ? "WIRE MODE · click a device, then another to connect · click a connected one to disconnect · right-click/Esc to cancel"
            : Board.SurfaceMode ? "SURFACES · drag to paint a wall/floor skin over the deck · Shift+drag boxes an area · Ctrl at release = outline only · double-click a tile to flood-select its run"
            : _defaultHint;
    }

    // ---- Surfaces mode (paint the deck) ----

    /// <summary>True while the next palette pick fills the pattern's second brush instead of arming the main one.
    /// Cleared by that pick, by clicking slot A, and by leaving the mode — it is a one-shot, so an accidental slot
    /// click never leaves the palette quietly wired to the wrong place.</summary>
    private bool _slotBArmed;

    private void OnSurfaceModeChanged()
    {
        SyncViewToggles();
        UpdateModeHint();
        if (!Board.SurfaceMode) _slotBArmed = false;
        UpdateSurfaceBar();
    }

    private void OnSurfaceToggleClick(object sender, RoutedEventArgs e) => Board.ToggleSurfaceMode();

    /// <summary>Choose which brush slot the next palette pick fills. Slot A is the armed brush itself, so clicking
    /// it just cancels a pending B pick.</summary>
    private void OnSurfaceSlotClick(object sender, RoutedEventArgs e)
    {
        _slotBArmed = ReferenceEquals(sender, SlotB);
        UpdateSurfaceBar();
    }

    private void OnClearSlotBClick(object sender, RoutedEventArgs e)
    {
        Board.SetPatternB(null);
        Board.SetPattern(SurfacePattern.Solid);   // nothing left to alternate with
        _slotBArmed = false;
        UpdateSurfaceBar();
    }

    private void OnSurfacePatternClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tag } && Enum.TryParse<SurfacePattern>(tag, out var pattern))
            Board.SetPattern(pattern);
        UpdateSurfaceBar();
    }

    private void OnSurfaceModeButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tag } && Enum.TryParse<SurfacePaintMode>(tag, out var mode))
        {
            Board.SetPaintMode(mode);
            _settings.SurfacePaintMode = mode.ToString();
            _settings.Save();
        }
        UpdateSurfaceBar();
    }

    private void OnSurfaceFocusClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tag } && Enum.TryParse<SurfaceFocus>(tag, out var focus))
        {
            Board.SetLayerFocus(focus);
            _settings.SurfaceFocus = focus.ToString();
            _settings.Save();
        }
        UpdateSurfaceBar();
    }

    /// <summary>The palette thumbnail already built for a part, or null when it has no palette row.</summary>
    private ImageSource? ThumbFor(PartDef? part) =>
        part is null ? null : _allParts.FirstOrDefault(v => v.Part.DefName == part.DefName)?.Thumb;

    /// <summary>
    /// Redraw the Surfaces bar from the live state: the two brushes, which slot the next pick fills, the pattern,
    /// and the one line of guidance for whatever is missing. The pattern buttons need a usable pair — two 1×1 skins
    /// of the <b>same</b> layer — because a checkerboard of a wall and a floor is not a pattern, it is two different
    /// edits (the canvas ignores a mismatched pair and paints plain, so this only has to explain it).
    /// </summary>
    private void UpdateSurfaceBar()
    {
        SurfaceBar.Visibility = Board.SurfaceMode ? Visibility.Visible : Visibility.Collapsed;
        if (!Board.SurfaceMode || _catalog is null) return;

        var a = SurfacePaint.IsSurfaceBrush(_catalog, Board.ArmedPart) ? Board.ArmedPart : null;
        var b = Board.PatternB;
        var pairOk = a is not null && b is not null && _catalog.RenderLayer(a) == _catalog.RenderLayer(b);

        SlotA.IsChecked = !_slotBArmed;
        SlotB.IsChecked = _slotBArmed;
        TxtSlotA.Text = "A: " + (a?.Friendly ?? "none");
        TxtSlotB.Text = "B: " + (b?.Friendly ?? "none");
        ImgSlotA.Source = ThumbFor(a);
        ImgSlotB.Source = ThumbFor(b);
        BtnClearSlotB.IsEnabled = b is not null;

        PatChecker.IsEnabled = PatRows.IsEnabled = PatCols.IsEnabled = pairOk;
        PatSolid.IsChecked = Board.Pattern == SurfacePattern.Solid;
        PatChecker.IsChecked = Board.Pattern == SurfacePattern.Checker;
        PatRows.IsChecked = Board.Pattern == SurfacePattern.StripesH;
        PatCols.IsChecked = Board.Pattern == SurfacePattern.StripesV;

        FocusBoth.IsChecked = Board.LayerFocus == SurfaceFocus.Both;
        FocusFloors.IsChecked = Board.LayerFocus == SurfaceFocus.Floors;
        FocusWalls.IsChecked = Board.LayerFocus == SurfaceFocus.Walls;
        ModeReplace.IsChecked = Board.PaintMode == SurfacePaintMode.Replace;
        ModeBoth.IsChecked = Board.PaintMode == SurfacePaintMode.ReplaceAndFill;
        ModeFill.IsChecked = Board.PaintMode == SurfacePaintMode.Fill;

        // The line answers whatever is most in the way, in the order it would actually block you: no brush, then a
        // half-set pattern, then the mode you are painting in (which is the one that explains a stroke doing
        // nothing at all).
        TxtSurfaceNote.Text =
            a is null ? "Arm a wall or floor from the palette to paint with. Other parts still place as usual."
            : _slotBArmed ? $"Now pick the second {LayerWord(a)} from the palette."
            : !pairOk && b is not null ? "A and B are different layers — pick a matching pair to pattern with."
            : Board.PaintMode == SurfacePaintMode.Replace
                ? $"Re-skinning {LayerWord(a)}s only — bare tiles are left alone. Switch to Both or Fill to lay new ones."
            : Board.PaintMode == SurfacePaintMode.Fill
                ? $"Laying new {LayerWord(a)}s on bare tiles only — what is already there is left alone."
            : $"Re-skinning {LayerWord(a)}s and laying new ones on bare tiles.";
    }

    /// <summary>"wall" or "floor", for the Surfaces bar's guidance line.</summary>
    private string LayerWord(PartDef part) =>
        _catalog is not null && _catalog.RenderLayer(part) == Catalog.LayerWall ? "wall" : "floor";

    /// <summary>Put the palette highlight back on the armed brush without re-arming anything — used after a pick
    /// that filled slot B, which must not disturb what is in hand.</summary>
    private void SyncPaletteHighlightToArmed()
    {
        _syncingPalette = true;
        var claimed = false;
        foreach (var list in _paletteLists)
        {
            var match = claimed || Board.ArmedPart is not { } armed
                ? null
                : list.Items.OfType<PartVM>().FirstOrDefault(v => v.Part.DefName == armed.DefName);
            list.SelectedItem = match;
            claimed |= match is not null;
        }
        _syncingPalette = false;
    }

    /// <summary>Persist and apply how far Surfaces mode ghosts the non-deck layers (View ▸ Surfaces).</summary>
    private void SetSurfaceGhostPercent(double percent)
    {
        var opacity = Math.Clamp(percent / 100.0, 0, 1);
        Board.SurfaceGhostOpacity = opacity;
        _settings.SurfaceGhostOpacity = opacity;
        _settings.Save();
    }

    /// <summary>The Surfaces submenu: how visible the ghosted layers stay while painting the deck. Persisted, because
    /// how much of the clutter you want as a landmark is a matter of taste and of what you are painting.</summary>
    private MenuItem SurfaceOptionsItem()
    {
        var menu = new MenuItem { Header = "Surfaces" };
        menu.Items.Add(MenuSliderRow("Other layers", 0, 100, _settings.SurfaceGhostOpacity * 100, "0",
            SetSurfaceGhostPercent, suffix: "% visible"));
        return menu;
    }

    /// <summary>Connect two devices, or disconnect them if the directed link already exists — one undo step. The
    /// canvas only offers connectable targets, so the add path is validated (a redundant guard keeps it honest).</summary>
    private void OnLinkToggleRequested(Placement source, Placement target)
    {
        if (_doc is null) return;
        var link = new DeviceLink(source.Id, target.Id);
        string Name(Placement p) => _doc.Part(p)?.Friendly ?? p.DefName;
        if (_doc.Links.Contains(link))
        {
            _stack.Push(_doc, new RemoveLinkCommand(link));
            AuditLog.Add($"Disconnected {Name(source)} → {Name(target)}.");
        }
        else if (DeviceLinks.CanConnect(_doc, source, target))
        {
            _stack.Push(_doc, new AddLinkCommand(link));
            AuditLog.Add($"Connected {Name(source)} → {Name(target)}.");
        }
    }

    private void OnAddZoneClick(object sender, RoutedEventArgs e)
    {
        if (_doc is null) return;
        var zone = new ShipZone
        {
            Name = NextZoneName(),
            Color = ZoneEditorDialog.Presets[_doc.Zones.Count % ZoneEditorDialog.Presets.Length],
            TileConds = { ShipZone.CondHaul },   // a sensible default; change it via Edit
            PersonSpec = "ZonePlayer",
            TargetPSpec = "ZoneCaptainAndCrew",
        };
        _stack.Push(_doc, new CreateZoneCommand(zone));   // self-describes to the audit trail ("Create zone …")
        Board.SetActiveZone(zone.Id);   // arm it so the user can paint straight away
    }

    private string NextZoneName()
    {
        for (var n = (_doc?.Zones.Count ?? 0) + 1; ; n++)
            if (_doc!.Zones.All(z => z.Name != $"Zone {n}")) return $"Zone {n}";
    }

    /// <summary>A paint/erase/box/room-fill stroke finished on the canvas — record it as one undo step.</summary>
    private void OnZoneStrokeCommitted(Guid zoneId, IReadOnlyCollection<(int X, int Y)> before, IReadOnlyCollection<(int X, int Y)> after)
    {
        if (_doc?.Zones.FirstOrDefault(z => z.Id == zoneId) is not { } zone) return;
        _stack.Push(_doc, new SetZoneTilesCommand(zone, before, after));
    }

    private void EditZone(ShipZone zone)
    {
        if (_doc is null) return;
        var before = zone.Meta;
        var dlg = new ZoneEditorDialog(this, "Edit zone", before) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result is { } meta)
            _stack.Push(_doc, new SetZoneMetaCommand(zone, before, meta));
    }

    private void DeleteZone(ShipZone zone)
    {
        if (_doc is null) return;
        if (!Dlg.Confirm(this, DlgKind.Warning, "Delete zone?",
            $"Delete the zone “{zone.Name}” and its painted tiles?", "Delete zone")) return;
        if (Board.ActiveZoneId == zone.Id) Board.SetActiveZone(null);
        _stack.Push(_doc, new DeleteZoneCommand(_doc, zone));
    }

    private static string ZoneTypeLabel(ShipZone z)
    {
        var parts = new List<string>();
        if (z.IsHaul) parts.Add("Haul");
        if (z.IsBarter) parts.Add("Barter");
        if (z.IsForbid) parts.Add("Forbid");
        if (z.IsTrigger) parts.Add("Trigger");
        return parts.Count == 0 ? "—" : string.Join("+", parts);
    }

    private void UpdateZones()
    {
        if (ZonesPanel is null) return;
        ZonesPanel.Children.Clear();
        if (_doc is null || _doc.Zones.Count == 0)
        {
            ZonesPanel.Children.Add(ZoneHint("No zones yet. Click “+ Add” to paint a Haul, Barter or Forbid area."));
            return;
        }
        // Teach the interaction up top: while painting show the paint controls, otherwise how to start.
        ZonesPanel.Children.Add(Board.ActiveZoneId is not null
            ? ZoneHint("Painting · drag add · Ctrl erase · Shift box · double-click fills a room · Esc stops")
            : ZoneHint("Click a zone to paint its tiles. Use Properties to rename or recolour."));
        foreach (var zone in _doc.Zones) ZonesPanel.Children.Add(ZoneRow(zone));
    }

    private static TextBlock ZoneHint(string text) => new()
    {
        Text = text, Foreground = ThemeManager.Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6),
    };

    private FrameworkElement ZoneRow(ShipZone zone)
    {
        var active = Board.ActiveZoneId == zone.Id;

        // left: a larger colour swatch + the zone name (larger) and its type
        var swatch = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(3), Background = ZoneEditorDialog.SolidOf(zone.Color),
            BorderBrush = ThemeManager.PanelBorder, BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = zone.Name, Foreground = active ? ThemeManager.AccentText : ThemeManager.Ink,
            FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 130,
        });
        text.Children.Add(new TextBlock
        {
            Text = ZoneTypeLabel(zone), Foreground = active ? ThemeManager.AccentText : ThemeManager.Dim, FontSize = 11,
        });
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(swatch);
        left.Children.Add(text);

        // right: Properties (rename/recolour/type/role) + delete, sharing the uniform ZoneBtn style. Both mark the
        // click handled so they don't ALSO toggle painting on the enclosing row button.
        var btnStyle = (Style)FindResource("ZoneBtn");
        var props = new Button { Content = "Properties", Style = btnStyle, ToolTip = "Rename, recolour, and set the zone's type and role" };
        props.Click += (_, e) => { e.Handled = true; EditZone(zone); };
        var del = new Button { Content = "✕", Style = btnStyle, ToolTip = "Delete zone" };
        del.Click += (_, e) => { e.Handled = true; DeleteZone(zone); };
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(props);
        right.Children.Add(del);

        var dock = new DockPanel();
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        dock.Children.Add(left);   // fills the remaining width

        // The WHOLE row is the click-to-paint target (a filled chip with a hand cursor) — clicking anywhere on it
        // starts/stops painting, so resizing a zone is discoverable without hunting for a button.
        var row = new Button
        {
            Content = dock,
            Background = active ? ThemeManager.AccentBg : ThemeManager.FieldBg,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(6, 5, 6, 5),
            Margin = new Thickness(0, 3, 0, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = active ? "Click to stop painting this zone" : "Click to paint this zone's tiles",
        };
        row.Click += (_, _) => Board.SetActiveZone(active ? (Guid?)null : zone.Id);
        return row;
    }

    private void RotateSelection(int delta)
    {
        if (_doc is null) return;
        var parts = Board.SelectedPlacements().Where(p => !_doc.IsLocked(p)).ToList();
        if (parts.Count == 0) return;

        // With symmetry on AND the selection a genuine mirror set, the selection holds mirror partners
        // (auto-selected together): rotate the primary side and reflect it onto its partners so the group stays
        // symmetric, rather than spinning the combined bounds. An arbitrary selection (e.g. a fresh paste on one
        // side) is not a partner set — it falls through to the plain group rotate so it turns about its own centre.
        if (Board.SymMode != SymmetryMode.Off && Board.SelectionIsSymmetric())
        {
            var symItems = parts
                .Select(p =>
                {
                    var (w, h) = _doc.FootprintOf(p);
                    return new SymmetryOps.Item(p.DefName, p.X, p.Y, w, h, p.Rot, _doc.Part(p)?.Item.HasSpriteSheet == true);
                })
                .ToList();
            var (cx, cy) = Board.SymCenter;
            var symPoses = SymmetryOps.RotateGroup(symItems, delta, cx, cy,
                Board.SymMode is SymmetryMode.Vertical or SymmetryMode.Both,
                Board.SymMode is SymmetryMode.Horizontal or SymmetryMode.Both);
            var symBatch = new List<(Placement, int, int, int)>(parts.Count);
            for (var i = 0; i < parts.Count; i++)
                symBatch.Add((parts[i], symPoses[i].X, symPoses[i].Y, symPoses[i].Rot));
            _stack.Push(_doc, new SetPosesCommand(symBatch));
            return;
        }

        if (parts.Count == 1)
        {
            // a single part turns in place — but sheet items (walls/floors) auto-tile, they don't rotate
            var p = parts[0];
            if (_doc.Part(p)?.Item.HasSpriteSheet == true) return;
            _stack.Push(_doc, new RotateCommand(_doc, p, delta));
            return;
        }

        // several parts rotate as a group: the whole arrangement turns about its centre,
        // each part both moving and (unless it auto-tiles) turning — GridMath-exact geometry
        var items = parts
            .Select(p =>
            {
                var (w, h) = _doc.FootprintOf(p);
                return new GroupRotate.Item(p.X, p.Y, w, h, p.Rot, _doc.Part(p)?.Item.HasSpriteSheet == true);
            })
            .ToList();
        var poses = GroupRotate.Rotate(items, delta);
        var batch = new List<(Placement, int, int, int)>(parts.Count);
        for (var i = 0; i < parts.Count; i++)
            batch.Add((parts[i], poses[i].X, poses[i].Y, poses[i].Rot));
        _stack.Push(_doc, new SetPosesCommand(batch));
    }

    /// <summary>
    /// Mirror the current selection about its bounding-box centre — <paramref name="horizontal"/> flips
    /// left↔right, otherwise up↔down. Every unlocked part reflects its position and remaps its rotation to a
    /// real 0/90/180/270 (<see cref="GroupFlip"/>), so the result is always buildable. A lone part reflects in
    /// place; sheet walls/floors keep rot 0 but still move. Committed as one undo step (place-and-flag, like a
    /// group rotate — an illegal landing is allowed but flagged by the problem scan).
    /// </summary>
    private void FlipSelection(bool horizontal)
    {
        if (_doc is null) return;
        var parts = Board.SelectedPlacements().Where(p => !_doc.IsLocked(p)).ToList();
        if (parts.Count == 0) return;

        var items = parts
            .Select(p =>
            {
                var (w, h) = _doc.FootprintOf(p);
                return new GroupRotate.Item(p.X, p.Y, w, h, p.Rot, _doc.Part(p)?.Item.HasSpriteSheet == true);
            })
            .ToList();
        var poses = GroupFlip.Flip(items, horizontal);
        var batch = new List<(Placement, int, int, int)>(parts.Count);
        for (var i = 0; i < parts.Count; i++)
            batch.Add((parts[i], poses[i].X, poses[i].Y, poses[i].Rot));
        _stack.Push(_doc, new SetPosesCommand(batch));
    }

    private void DuplicateSelection()
    {
        if (_doc is null) return;
        var selected = Board.SelectedPlacements().Where(p => !_doc.IsLocked(p)).ToList();
        if (selected.Count == 0) return;
        var clones = selected
            .Select(p => new Placement
            {
                DefName = p.DefName, X = p.X + 1, Y = p.Y + 1, Rot = p.Rot,
                Cargo = Cargo.CloneForest(p.Cargo),   // duplicate a container's contents with it
            })
            .ToList();
        _stack.Push(_doc, new CompositeCommand(clones.Select(c => (IDocCommand)new PlaceCommand(c)).ToList()));
        Board.SelectedIds.Clear();
        foreach (var clone in clones) Board.SelectedIds.Add(clone.Id);   // hand the copies to the user's cursor
        Board.InvalidateVisual();
        UpdateInspector();
    }

    /// <summary>
    /// Swap each door in the set between its open and closed state (Open ↔ Closed def),
    /// preserving tile and rotation. Purely cosmetic to the law — the game rooms an open
    /// and a closed door identically — but it lets a design record which doors are shut,
    /// e.g. to picture a multi-compartment ship. Implemented as remove-old + place-new so
    /// it rides the normal undo stack; the new placements become the selection.
    ///
    /// <para>A state change, not an identity change, so it goes through <see cref="Placement.Restate"/>: on a save
    /// edit the door is one the player already owns, and shutting it must not be billed as a new door.</para>
    /// </summary>
    private void ToggleDoors(IReadOnlyList<Placement> doors) => RestateAll(doors, d => _catalog!.DoorToggle(d));

    /// <summary>
    /// Switch each device in the set between its off and on state, keeping tile, rotation and cargo. The game
    /// installs powered fixtures Off and the palette builds the On form instead where it can name one, but a device
    /// whose on-state is a colour variant (the Transponder) falls through and is placed Off with no way to switch it
    /// on. This is that way, and it also covers turning something deliberately off.
    ///
    /// <para>Switching on always lands on the <b>nominal</b> state, never an alarm's alert colour (see
    /// <see cref="Catalog.PowerToggle"/>). It matters to the analysis rather than being cosmetic: the Ship Rating
    /// and the diagnostics both forbid <c>IsOff</c>, so a switched-off transponder really does read as a fault.</para>
    /// </summary>
    private void TogglePower(IReadOnlyList<Placement> devices) => RestateAll(devices, d => _catalog!.PowerToggle(d));

    /// <summary>
    /// Move the selected part or loose item one step down (or up) the pile of things sharing its tile — the manual
    /// override over the automatic draw order (see <see cref="ZOrder"/>). No-op at the end of the pile, so holding
    /// the shortcut down settles rather than cycling.
    /// </summary>
    private void Restack(bool forward, (int X, int Y)? cell = null)
    {
        if (_doc is null || Board.RestackTarget(cell) is not { } t) return;
        var changes = ZOrder.Nudge(_doc, t.Item, t.X, t.Y, forward);
        if (changes.Count == 0) return;
        _stack.Push(_doc, new SetZOrderCommand(changes, forward ? "Move forward" : "Move back"));
        Board.InvalidateVisual();
    }

    /// <summary>Put the whole pile on a tile back under the automatic draw order, clearing the biases a nudge
    /// wrote across it (see <see cref="ZOrder.Reset"/>).</summary>
    private void ResetStackOrder((int X, int Y) cell)
    {
        if (_doc is null || Board.RestackTarget(cell) is not { } t) return;
        var changes = ZOrder.Reset(_doc, t.Item, t.X, t.Y);
        if (changes.Count == 0) return;
        _stack.Push(_doc, new SetZOrderCommand(changes, "Reset draw order"));
        Board.InvalidateVisual();
    }

    /// <summary>The Move Back / Move Forward / Reset order entries for a tile, appended to whichever context menu
    /// is open. Both menus offer them, because a canister and the regulator it leans on are one pile whether the
    /// canister is installed or lying on the deck. Nothing is added when only one thing is drawn on the tile.</summary>
    private void AddRestackItems(ContextMenu menu, (int X, int Y) cell, Func<string, string, RoutedEventHandler, bool, MenuItem> item)
    {
        if (_doc is null || Board.RestackTarget(cell) is not { } t) return;
        var pile = ZOrder.StackAt(_doc, t.X, t.Y, t.Item);
        if (pile.Count < 2) return;

        var at = pile.ToList().FindIndex(i => i.Id == t.Item.Id);
        menu.Items.Add(new Separator());
        menu.Items.Add(item("Move Back", "Ctrl+[", (_, _) => Restack(false, cell), at > 0));
        menu.Items.Add(item("Move Forward", "Ctrl+]", (_, _) => Restack(true, cell), at < pile.Count - 1));
        menu.Items.Add(item("Reset order", "", (_, _) => ResetStackOrder(cell), pile.Any(i => i.ZBias != 0)));
    }

    /// <summary>
    /// Give a placed part a name of its own, the way the game's own rename box does — so a hold of
    /// identical racks can read "spare tool storage" and "spare reactor parts" instead of five identical rows. The
    /// name travels into the game on export and on a save write-back, and comes back on import (see
    /// <see cref="Rename"/>).
    ///
    /// <para>The primary airlock is included, locked though it is: the lock is about geometry, and the game lets a
    /// player rename that port like any other object.</para>
    /// </summary>
    private void RenamePart(Placement p)
    {
        if (_doc is null || _catalog is null) return;
        var part = _doc.Part(p);
        if (!Rename.CanRename(part)) return;

        var dlg = new RenameDialog(part!.Friendly, p.CustomName) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.ChosenName == p.CustomName) return;

        _stack.Push(_doc, new SetCustomNameCommand(p, p.CustomName, dlg.ChosenName));
        Board.InvalidateVisual();
        UpdateInspector();
    }

    /// <summary>
    /// Re-state each part to the def <paramref name="peer"/> maps it to, at the same tile and rotation, as one undo
    /// step; the swapped-in parts become the selection. Shared by the door and power toggles, which differ only in
    /// the mapping.
    ///
    /// <para>A state change, not an identity change, so it goes through <see cref="Placement.Restate"/>: on a save
    /// edit the part is one the player already owns, and switching it must not be billed as a new one.</para>
    /// </summary>
    private void RestateAll(IReadOnlyList<Placement> parts, Func<string, string?> peer)
    {
        if (_doc is null || _catalog is null || parts.Count == 0) return;
        var commands = new List<IDocCommand>();
        var newIds = new List<Guid>();
        foreach (var p in parts)
        {
            if (_doc.IsLocked(p) || peer(p.DefName) is not { } target) continue;
            var swapped = p.Restate(target, p.Rot);
            commands.Add(new RemoveCommand([p]));
            commands.Add(new PlaceCommand(swapped));
            newIds.Add(swapped.Id);
        }
        if (commands.Count == 0) return;
        _stack.Push(_doc, new CompositeCommand(commands));
        Board.SelectedIds.Clear();
        foreach (var id in newIds) Board.SelectedIds.Add(id);
        Board.InvalidateVisual();
        UpdateInspector();
    }

    /// <summary>
    /// Swap each part for the def it maps to, as one undo step, keeping tile/rotation and carrying any cargo; the
    /// swapped-in parts become the selection. Shared by every mapping that changes a part's <b>state</b> rather than
    /// its identity: "Make Loose Item" / "Install item" (<see cref="FormSwap"/>) and "Repair" /
    /// "Repair All" (<see cref="Repair"/>). A result that no longer fits isn't blocked, just flagged by the live
    /// problem scan, consistent with moves and replaces landing in an illegal spot.
    /// </summary>
    private void SwapForms(IReadOnlyList<(Placement Part, string Target)> swaps)
    {
        if (_doc is null || FormSwap.BuildSwap(_doc, swaps) is not { } swap) return;
        _stack.Push(_doc, swap.Cmd);
        Board.SelectedIds.Clear();
        foreach (var p in swap.New) Board.SelectedIds.Add(p.Id);
        Board.InvalidateVisual();
        UpdateInspector();
    }

    /// <summary>
    /// "Repair All": swap every broken part on the ship for the working part the game's own repair job yields
    /// (see <see cref="Repair"/>), as one undo step. It rewrites the whole design rather than a selection, so it
    /// says what it is about to do and how much of it first — and, when nothing is broken, explains where the
    /// <i>other</i> kind of damage lives, since "everything is at 100%" is exactly what someone reaching for this
    /// is after.
    /// </summary>
    private void RepairAll()
    {
        if (_doc is null) return;
        var broken = Repair.RepairableAll(_doc);
        if (broken.Count == 0)
        {
            Dlg.Info(this, "Repair All",
                "Nothing on this ship is broken — every part is already its working form.\n\n" +
                "Wear that a part has accumulated is not part of the design. It lives in the save, and is cleared " +
                "by choosing \"Repair everything\" when you write the design back with File ▸ Update Ship in Save.");
            return;
        }

        var n = broken.Count;
        var distinct = broken.Select(b => b.Part.DefName).Distinct(StringComparer.Ordinal).Count();
        if (!Dlg.Confirm(this, DlgKind.Info, "Repair All",
                $"Repair {n} broken part{(n == 1 ? "" : "s")} ({distinct} kind{(distinct == 1 ? "" : "s")}) into " +
                "their working forms, the way the game's own repair jobs do.\n\n" +
                "Repaired devices come back switched on. This is one undo step, and it does not touch wear a part " +
                "has accumulated — that is the condition choice on the way into a save.",
                $"Repair {n}"))
            return;

        SwapForms(broken);
    }

    /// <summary>
    /// Replace the (unlocked) selection with a compatible buildable part — same render layer and body
    /// size — chosen from a picker, keeping each part's tile and rotation. One undo step; the
    /// swapped-in parts become the selection. Illegal results aren't blocked, just flagged by the
    /// live problem scan, consistent with moves/rotations into illegal spots.
    /// </summary>
    private void ReplaceSelection()
    {
        if (_doc is null || _catalog is null) return;
        var parts = Board.SelectedPlacements().Where(p => !_doc.IsLocked(p)).ToList();
        if (parts.Count == 0 || ReplaceOps.CommonClass(_doc, parts) is not { } cls) return;

        var targetDefs = ReplaceOps.CompatibleTargets(_catalog, cls).Select(t => t.DefName).ToHashSet(StringComparer.Ordinal);
        var vms = _allParts.Where(v => targetDefs.Contains(v.Part.DefName)).ToList();
        if (vms.Count == 0) return;

        var what = parts.Count == 1 ? $"\"{_doc.Part(parts[0])?.Friendly ?? parts[0].DefName}\"" : $"{parts.Count} parts";
        var dlg = new ReplacePickerDialog(vms, what) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Selected is not { } target) return;
        if (ReplaceOps.BuildSwap(_doc, parts, target.DefName) is not { } swap) return;

        _stack.Push(_doc, swap.Cmd);
        Board.SelectedIds.Clear();
        foreach (var p in swap.New) Board.SelectedIds.Add(p.Id);
        Board.InvalidateVisual();
        UpdateInspector();
    }

    /// <summary>
    /// "Find and Replace All…": like <see cref="ReplaceSelection"/> but scoped to every copy of the selected
    /// part anywhere in the ship, not just the current selection — the selection must be one or more copies
    /// of the exact same part (<see cref="ReplaceOps.SoleDef"/>). Locked matches are found (for the count)
    /// but skipped by the swap itself, same as "Replace with…". One undo step; the swapped-in parts become
    /// the selection.
    /// </summary>
    private void FindAndReplace()
    {
        if (_doc is null || _catalog is null) return;
        var selected = Board.SelectedPlacements();
        if (ReplaceOps.SoleDef(selected) is not { } defName) return;
        if (ReplaceOps.CommonClass(_doc, [selected[0]]) is not { } cls) return;

        var found = ReplaceOps.FindAll(_doc, defName);
        var lockedCount = found.Count(p => _doc.IsLocked(p));
        if (found.Count == lockedCount) return;

        var targetDefs = ReplaceOps.CompatibleTargets(_catalog, cls).Select(t => t.DefName).ToHashSet(StringComparer.Ordinal);
        var vms = _allParts.Where(v => targetDefs.Contains(v.Part.DefName)).ToList();
        if (vms.Count == 0) return;

        var friendly = _doc.Part(selected[0])?.Friendly ?? defName;
        var what = $"{found.Count} instance{(found.Count > 1 ? "s" : "")} of \"{friendly}\""
                   + (lockedCount > 0 ? $" ({lockedCount} fixed to the ship, skipped)" : "");
        var dlg = new ReplacePickerDialog(vms, what) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Selected is not { } target) return;
        if (ReplaceOps.BuildSwap(_doc, found, target.DefName) is not { } swap) return;

        _stack.Push(_doc, swap.Cmd);
        Board.SelectedIds.Clear();
        foreach (var p in swap.New) Board.SelectedIds.Add(p.Id);
        Board.InvalidateVisual();
        UpdateInspector();
    }

    /// <summary>
    /// "Theme…": re-skin every wall and every floor on the ship to a chosen cooverlay style, one undo
    /// step (<see cref="ThemeOps"/>). Only sprites/names change; rooms/airtightness/rating are untouched.
    /// </summary>
    private void OnThemeClick(object sender, RoutedEventArgs e)
    {
        if (_doc is null || _catalog is null) return;

        // Wall and floor skins are the buildable variants over the 1×1 wall / floor base (the only
        // footprint they come in). Present each as a palette thumbnail (reusing the built VMs).
        List<PartVM> Skins((int, int, int) cls)
        {
            var defs = ReplaceOps.CompatibleTargets(_catalog, cls).Select(t => t.DefName).ToHashSet(StringComparer.Ordinal);
            return _allParts.Where(v => defs.Contains(v.Part.DefName)).ToList();
        }

        // Placed count + the ship's current skin for a class (non-null only if every such part shares one).
        (int Count, string? Current) State((int Layer, int W, int H) cls)
        {
            var placed = _doc.Placements
                .Where(p => !_doc.IsLocked(p) && _doc.Part(p) is { } part && _catalog.SwapClass(part) == cls)
                .ToList();
            var defs = placed.Select(p => p.DefName).Distinct(StringComparer.Ordinal).ToList();
            return (placed.Count, defs.Count == 1 ? defs[0] : null);
        }

        var wallCls = (Catalog.LayerWall, 1, 1);
        var floorCls = (Catalog.LayerFloor, 1, 1);
        var wallSkins = Skins(wallCls);
        var floorSkins = Skins(floorCls);
        if (wallSkins.Count == 0 && floorSkins.Count == 0) return;
        var (wallCount, wallCurrent) = State(wallCls);
        var (floorCount, floorCurrent) = State(floorCls);
        if (wallCount == 0 && floorCount == 0)
        {
            Dlg.Show(this, "Place some walls or floors before applying a theme.", "Apply theme",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new ThemePickerDialog(wallSkins, wallCurrent, wallCount, floorSkins, floorCurrent, floorCount) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        if (ThemeOps.BuildReskin(_doc, dlg.SelectedWall?.DefName, dlg.SelectedFloor?.DefName) is not { } reskin) return;

        _stack.Push(_doc, reskin.Cmd);
        Board.SelectedIds.Clear();
        foreach (var p in reskin.New) Board.SelectedIds.Add(p.Id);
        Board.InvalidateVisual();
        UpdateInspector();
    }

    /// <summary>Copy the selection to an in-memory clipboard, stored relative to its top-left tile.</summary>
    private void CopySelection()
    {
        if (_doc is null) return;
        var selected = Board.SelectedPlacements().Where(p => !_doc.IsLocked(p)).ToList();
        if (selected.Count == 0) return;
        var minX = selected.Min(p => p.X);
        var minY = selected.Min(p => p.Y);
        _clipOrigin = (minX, minY);
        // snapshot the container contents too (cargo is immutable, so the reference is a valid snapshot) — each
        // paste deep-clones it with fresh ids, so a copied container pastes with its contents
        _clip = selected.Select(p => (p.DefName, p.X - minX, p.Y - minY, p.Rot, p.Cargo)).ToList();
    }

    /// <summary>
    /// The clipboard's parts as fresh placements anchored at <paramref name="anchor"/>.
    ///
    /// <para>Static, and taking the payload rather than reading the field, because <b>the clipboard is shared by
    /// every open design</b> — copying in one tab and pasting into another is most of the point of having tabs. This
    /// is the step that has to carry nothing of the design it was copied from: it builds new
    /// <see cref="Placement"/>s and deep-clones the cargo with fresh ids, so the same clipboard pasted into two
    /// designs gives each an entirely independent set rather than two ships sharing item identity.</para>
    /// </summary>
    internal static List<Placement> ClipboardClones(
        IReadOnlyList<(string Def, int X, int Y, int Rot, IReadOnlyList<CargoItem> Cargo)> clip, (int X, int Y) anchor) =>
        clip.Select(c => new Placement
        {
            DefName = c.Def, X = anchor.X + c.X, Y = anchor.Y + c.Y, Rot = c.Rot,
            Cargo = Cargo.CloneForest(c.Cargo),   // fresh-id copies of the container's contents
        }).ToList();

    /// <summary>
    /// Paste the clipboard at the cursor, selecting the copies. Pastes into whichever design is on screen, which
    /// need not be the one it was copied from, and lands in the same place either way: where you are pointing.
    ///
    /// <para><paramref name="at"/> is the tile a caller already knows the user meant — the right-click menu's Paste,
    /// where the cursor is over the menu popup by the time it is clicked rather than over the tile it was opened on.
    /// Left null, the canvas is asked where the cursor is (see <see cref="ShipCanvas.PasteCell"/>), which also
    /// covers the cursor being off the canvas entirely: over the palette, or another window.</para>
    /// </summary>
    private void PasteClipboard((int X, int Y)? at = null)
    {
        if (_doc is null || _clip.Count == 0) return;
        var clones = ClipboardClones(_clip, at ?? Board.PasteCell ?? _clipOrigin);
        _stack.Push(_doc, new CompositeCommand(clones.Select(c => (IDocCommand)new PlaceCommand(c)).ToList()));
        Board.SelectedIds.Clear();
        foreach (var clone in clones) Board.SelectedIds.Add(clone.Id);
        Board.InvalidateVisual();
        UpdateInspector();
    }

    private void OnContextMenuRequested((int X, int Y) cell)
    {
        if (_doc is null) return;
        var stack = _doc.HitTestStack(cell.X, cell.Y);   // topmost first, placements only (what the actions act on)
        var drawn = _doc.RenderStackAt(cell.X, cell.Y);  // the same pile as drawn, loose items included
        if (stack.Count == 0) return;

        static MenuItem Item(string header, string gesture, RoutedEventHandler onClick, bool enabled = true)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture, IsEnabled = enabled };
            item.Click += onClick;
            return item;
        }

        var menu = new ContextMenu { PlacementTarget = Board };

        var selected = Board.SelectedPlacements();
        var unlocked = selected.Where(p => !_doc.IsLocked(p)).ToList();
        var multi = selected.Count > 1;

        if (multi)
        {
            // a box selection: header + a layer filter to narrow it to one layer, so you can
            // (e.g.) drag a section, "Select only ▸ Walls & doors", then delete just those.
            // The per-tile stacked picker is skipped here — it would collapse the whole
            // selection to a single tile (the earlier bug).
            menu.Items.Add(new MenuItem
            {
                Header = $"{selected.Count} parts selected",
                IsEnabled = false,
                FontWeight = FontWeights.SemiBold,
            });
            var byLayer = selected
                .GroupBy(p => _catalog!.RenderLayer(_doc.Part(p)))
                .Where(g => g.Count() < selected.Count)   // a group that IS the whole selection changes nothing
                .OrderBy(g => g.Key)
                .ToList();
            if (byLayer.Count > 1)
            {
                var pick = new MenuItem { Header = "Select only" };
                foreach (var g in byLayer)
                {
                    var group = g.ToList();
                    pick.Items.Add(Item($"{LayerName(g.Key)} ({group.Count})", "", (_, _) => Board.SetSelection(group)));
                }
                menu.Items.Add(pick);
            }
        }
        else if (drawn.Count > 1)
        {
            // one tile with things stacked: floors sit under what's on them, and a canister can sit under the
            // regulator it feeds, so this is how you reach what is underneath. Click a row to select just it
            // (● = current). Loose items are listed too — they share the one draw order, so a dropped item pushed
            // under a fixture has to be reachable from the same place as everything else. ` steps down the list.
            menu.Items.Add(new MenuItem
            {
                Header = $"{drawn.Count} stacked here — click to select (`):",
                IsEnabled = false,
                FontWeight = FontWeights.SemiBold,
            });
            foreach (var d in drawn)
            {
                var target = d;
                var isSel = d.IsLoose
                    ? Board.SelectedLoose is { } sel && sel.Id == d.Id
                    : Board.SelectedIds.Count == 1 && Board.SelectedIds.Contains(d.Id);
                var name = d.Placement is { } dp
                    ? Rename.Display(dp, _doc.Part(dp)) + (_doc.IsLocked(dp) ? "   · fixed" : "")
                    : (_catalog!.Lookup(d.DefName)?.Friendly ?? d.DefName) + "   · loose";
                menu.Items.Add(Item((isSel ? "●  " : "○  ") + name, "", (_, _) => Board.SelectItem(target)));
            }
        }
        else
        {
            var only = stack[0];
            menu.Items.Add(new MenuItem
            {
                Header = Rename.Display(only, _doc.Part(only)) + (_doc.IsLocked(only) ? "  · fixed to the ship" : ""),
                IsEnabled = false,
                FontWeight = FontWeights.SemiBold,
            });
        }

        // actions on the current selection
        var canAct = unlocked.Count > 0;
        // a multi-selection always rotates as a group (even sheet walls/floors move); a lone
        // part rotates in place only if it isn't a sheet item (walls/floors auto-tile instead)
        var canRotate = unlocked.Count > 1 || unlocked.Any(p => _doc.Part(p)?.Item.HasSpriteSheet != true);
        var suffix = unlocked.Count > 1 ? $" ({unlocked.Count})" : "";

        // "Use as brush" (the eyedropper — formerly double-click): arm the part this menu is about, at its own
        // rotation, if it is buildable. Uses the lone selected part, else the topmost part on the tile.
        var brushPart = selected.Count == 1 ? selected[0] : stack[0];
        var brushDef = _allParts.Any(v => v.Part.DefName == brushPart.DefName) ? brushPart.DefName : null;
        var brushRot = brushPart.Rot;

        // "Replace with…": enabled when the whole (unlocked) selection shares one swap class (render
        // layer + body size) and at least one buildable part of that same kind exists to swap in.
        var canReplace = unlocked.Count > 0
            && ReplaceOps.CommonClass(_doc, unlocked) is { } rcls
            && ReplaceOps.CompatibleTargets(_catalog!, rcls).Count > 0;

        // "Find and Replace All…": the selection must be one or more copies of the exact same part (the
        // "block"), with at least one unlocked copy of it anywhere in the ship and a buildable part of the
        // same kind to swap in. Stricter source check than "Replace with…" (exact def, not just class), but
        // scoped to the whole ship rather than just the selection.
        var findDef = ReplaceOps.SoleDef(selected);
        var findMatches = findDef is not null ? ReplaceOps.FindAll(_doc, findDef) : [];
        var canFindReplace = findDef is not null
            && findMatches.Any(p => !_doc.IsLocked(p))
            && ReplaceOps.CommonClass(_doc, [selected[0]]) is { } fcls
            && ReplaceOps.CompatibleTargets(_catalog!, fcls).Count > 0;

        // door state — flip the selected doors between open and closed
        var toClose = unlocked.Where(p => _catalog!.DoorToggle(p.DefName) is not null && p.DefName.Contains("Open")).ToList();
        var toOpen = unlocked.Where(p => _catalog!.DoorToggle(p.DefName) is not null && p.DefName.Contains("Closed")).ToList();
        if (toClose.Count > 0 || toOpen.Count > 0)
        {
            menu.Items.Add(new Separator());
            if (toClose.Count > 0)
                menu.Items.Add(Item("Close door" + (toClose.Count > 1 ? $" ({toClose.Count})" : ""), "", (_, _) => ToggleDoors(toClose)));
            if (toOpen.Count > 0)
                menu.Items.Add(Item("Open door" + (toOpen.Count > 1 ? $" ({toOpen.Count})" : ""), "", (_, _) => ToggleDoors(toOpen)));
        }

        // power state — switch the selected devices on or off. Split by which way each one can go, so a mixed
        // selection offers both and each entry does exactly what it says.
        var switchable = unlocked.Where(p => _catalog!.PowerToggle(p.DefName) is not null).ToList();
        var toSwitchOn = switchable.Where(p => _catalog!.Lookup(p.DefName)?.StartingConds.Contains("IsOff") == true).ToList();
        var toSwitchOff = switchable.Except(toSwitchOn).ToList();
        if (toSwitchOn.Count > 0 || toSwitchOff.Count > 0)
        {
            menu.Items.Add(new Separator());
            if (toSwitchOn.Count > 0)
                menu.Items.Add(Item("Switch on" + (toSwitchOn.Count > 1 ? $" ({toSwitchOn.Count})" : ""), "", (_, _) => TogglePower(toSwitchOn)));
            if (toSwitchOff.Count > 0)
                menu.Items.Add(Item("Switch off" + (toSwitchOff.Count > 1 ? $" ({toSwitchOff.Count})" : ""), "", (_, _) => TogglePower(toSwitchOff)));
        }

        // rename — a single part, whatever it is (the game renames anything that is not a person, the primary
        // airlock included: its lock is about geometry, not about what it is called). One at a time by nature: a
        // name is the thing that tells two otherwise-identical racks apart, so applying one to a multi-selection
        // would defeat the point.
        var renameTarget = multi ? null : (selected.Count == 1 ? selected[0] : stack[0]);
        if (renameTarget is { } rt && Rename.CanRename(_doc.Part(rt)))
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(rt.CustomName is null ? "Rename…" : "Rename or clear…", "", (_, _) => RenamePart(rt)));
        }

        // installed ⇄ loose form: uninstall a placed fixture to its packaged (loose) form on the tile, or
        // re-install a loose one. Eligibility is the game's own uninstall/install jobs, so only real fixtures
        // qualify (raw hull, walls and the fixed airlock have no such job and never appear).
        var toLoosen = FormSwap.Loosenable(_doc, unlocked);
        var toInstall = FormSwap.Installable(_doc, unlocked);
        if (toLoosen.Count > 0 || toInstall.Count > 0)
        {
            menu.Items.Add(new Separator());
            if (toLoosen.Count > 0)
                menu.Items.Add(Item("Make Loose Item" + (toLoosen.Count > 1 ? $" ({toLoosen.Count})" : ""), "", (_, _) => SwapForms(toLoosen)));
            if (toInstall.Count > 0)
                menu.Items.Add(Item("Install item" + (toInstall.Count > 1 ? $" ({toInstall.Count})" : ""), "", (_, _) => SwapForms(toInstall)));
        }

        // repair — a part that is broken as a def (a damaged wall, a wrecked alarm) swapped for its working form.
        // Only ever offered when the selection actually contains one, so an intact ship never shows it; the
        // whole-ship version is Design ▸ Repair All.
        var toRepair = Repair.Repairable(_doc, unlocked);
        if (toRepair.Count > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Repair" + (toRepair.Count > 1 ? $" ({toRepair.Count})" : ""), "", (_, _) => SwapForms(toRepair)));
        }

        // "View contents…": a single container/console/crate — shown even when empty (so an imported empty
        // container isn't "locked"). Not shown for a multi-selection — uses the lone selected part, else topmost.
        var cargoTarget = multi ? null : (selected.Count == 1 ? selected[0] : stack[0]);
        var contentsShown = false;
        if (cargoTarget is { } ct && CanViewContents(ct))
        {
            var n = ct.Cargo.Count;
            menu.Items.Add(new Separator());
            contentsShown = true;
            menu.Items.Add(Item("View contents" + (n > 0 ? $" ({n})" : "") + "…", "", (_, _) => OpenInventory(ct)));
            // a nav console's screens are its modules, and where each one sits is the console's own arrangement
            if (NavConsole.IsConsole(_doc?.Part(ct)))
                menu.Items.Add(Item("Arrange screen…", "", (_, _) => OpenNavArrange(ct)));
        }

        // "Fill…": how much gas or fuel a canister or tank holds. A different question from "View contents" —
        // that is a container's inventory of items, this is the payload the part itself carries — but they sit
        // together because both are "what is inside this thing".
        var fillTarget = multi ? null : (selected.Count == 1 ? selected[0] : stack[0]);
        if (fillTarget is { } ft && ContainerFill.Describe(_doc?.Part(ft), _catalog!) is not null)
        {
            if (!contentsShown) menu.Items.Add(new Separator());
            menu.Items.Add(Item(ft.Fill is null ? "Fill…" : "Fill (changed)…", "", (_, _) => OpenFill(ft)));
        }

        menu.Items.Add(new Separator());
        if (brushDef is not null)
        {
            menu.Items.Add(Item("Use as brush", "Alt+Click", (_, _) => OnArmFromTile(brushDef, brushRot)));
            menu.Items.Add(Item(_settings.IsFavorite(brushDef, false) ? "Remove from Favorites" : "Add to Favorites",
                "", (_, _) => ToggleFavoriteByRef(brushDef, false)));
        }
        if (canReplace)
            menu.Items.Add(Item("Replace with…" + suffix, "Ctrl+R", (_, _) => ReplaceSelection()));
        if (canFindReplace)
            menu.Items.Add(Item("Find and Replace All…" + (findMatches.Count > 1 ? $" ({findMatches.Count})" : ""), "", (_, _) => FindAndReplace()));
        menu.Items.Add(Item("Duplicate" + suffix, "Ctrl+D", (_, _) => DuplicateSelection(), canAct));
        menu.Items.Add(Item("Copy" + suffix, "Ctrl+C", (_, _) => CopySelection(), canAct));
        // The tile the menu was opened on, not wherever the cursor is by the time the item is clicked — by then it
        // is over the menu popup, which is a window of its own and not the canvas.
        menu.Items.Add(Item("Paste", "Ctrl+V", (_, _) => PasteClipboard(cell), _clip.Count > 0));
        menu.Items.Add(Item("Rotate CW" + suffix, "R", (_, _) => RotateSelection(90), canRotate));
        menu.Items.Add(Item("Rotate CCW" + suffix, "Shift+R", (_, _) => RotateSelection(-90), canRotate));
        menu.Items.Add(Item("Flip Horizontal" + suffix, "H", (_, _) => FlipSelection(horizontal: true), canRotate));
        menu.Items.Add(Item("Flip Vertical" + suffix, "Shift+H", (_, _) => FlipSelection(horizontal: false), canRotate));
        if (!multi) AddRestackItems(menu, cell, Item);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Delete" + suffix, "Del", (_, _) => DeleteSelection(), canAct));
        menu.IsOpen = true;
    }

    /// <summary>Context menu for a loose floor item (the Items palette): change its stacked quantity (when the item
    /// stacks) and delete it. Fired by a right-click on the item, which has already selected it.</summary>
    private void OnLooseContextMenuRequested((int X, int Y) cell)
    {
        if (_doc is null || _catalog is null || Board.SelectedLoose is not { } lo) return;
        var part = _catalog.Lookup(lo.DefName);
        if (part is null) return;

        static MenuItem Item(string header, string gesture, RoutedEventHandler onClick, bool enabled = true)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture, IsEnabled = enabled };
            item.Click += onClick;
            return item;
        }

        var menu = new ContextMenu { PlacementTarget = Board };
        menu.Items.Add(new MenuItem
        {
            Header = part.Friendly + (lo.Quantity > 1 ? $"  · ×{lo.Quantity}" : ""),
            IsEnabled = false, FontWeight = FontWeights.SemiBold,
        });
        menu.Items.Add(new Separator());

        var stackable = part.StackLimit > 1;
        menu.Items.Add(Item(stackable ? "Change Quantity…" : "Change Quantity (not stackable)", "",
            (_, _) => ChangeLooseQuantity(lo, part), stackable));

        // "View contents…": a deck item that can hold things — a crate or toolbox, or a garment, backpack or EVA
        // suit, which store in their own pockets rather than a grid. Shown even when empty, like a placed
        // container, so an item you have not filled yet is not unreachable.
        if (Cargo.CanHoldCargo(part, _catalog))
        {
            var held = lo.Cargo.Sum(c => c.SubtreeCount);
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("View contents" + (held > 0 ? $" ({held})" : "") + "…", "",
                (_, _) => OpenLooseInventory(lo, part)));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Item(_settings.IsFavorite(lo.DefName, true) ? "Remove from Favorites" : "Add to Favorites",
            "", (_, _) => ToggleFavoriteByRef(lo.DefName, true)));
        AddRestackItems(menu, cell, Item);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Delete", "Del", (_, _) => DeleteSelection()));
        menu.IsOpen = true;
    }

    /// <summary>Open the inventory viewer/editor on a deck item's contents — the loose-item counterpart of
    /// <see cref="OpenInventory(Placement)"/>. Edits are undoable through the same command stack.</summary>
    private void OpenLooseInventory(LooseObject lo, PartDef part)
    {
        if (_doc is null || _catalog is null || _sprites is null) return;
        new InventoryWindow(_catalog, _sprites, lo.DefName, part.Friendly, lo.Cargo, _doc, _stack, rootLoose: lo)
        { Owner = this }.ShowDialog();
    }

    /// <summary>Prompt for a new stacked quantity (1..stack limit) and apply it as one undo step.</summary>
    private void ChangeLooseQuantity(LooseObject lo, PartDef part)
    {
        if (_doc is null) return;
        var max = Math.Max(1, part.StackLimit);
        var dlg = new LooseQuantityDialog(part.Friendly, lo.Quantity, max) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Quantity == lo.Quantity) return;
        _stack.Push(_doc, new SetLooseQuantityCommand(lo, lo.Quantity, dlg.Quantity));
        UpdateInspector();
    }

    /// <summary>A part can show an inventory view when it holds cargo, or is a container / equipment-slot host —
    /// even when empty, so an imported empty container isn't unreachable from the viewer.</summary>
    private bool CanViewContents(Placement p)
    {
        if (p.Cargo.Count > 0) return true;
        var part = _doc?.Part(p);
        return part?.IsContainer == true || part?.SlotsWeHave.Length > 0;
    }

    /// <summary>Open the inventory viewer/editor on a placed container's contents (empty is fine — shows the
    /// grid). Passing the document + command stack + placement enables add/remove of loose cargo, undoable.</summary>
    private void OpenInventory(Placement p)
    {
        if (_doc is null || _catalog is null || _sprites is null) return;
        var friendly = Rename.Display(p, _doc.Part(p));
        new InventoryWindow(_catalog, _sprites, p.DefName, friendly, p.Cargo, _doc, _stack, p) { Owner = this }.ShowDialog();
    }

    /// <summary>Open the nav console's screen arrangement — the planner's stand-in for the console's own edit
    /// menu in game (see <see cref="NavArrangeWindow"/>). A console with no modules aboard has nothing to
    /// arrange, and says so rather than opening an empty board.</summary>
    private void OpenNavArrange(Placement p)
    {
        if (_doc is null || _catalog is null) return;
        if (NavConsole.NeedsModules(p.Cargo))
        {
            Dlg.Info(this, "Arrange screen",
                "This console has no modules in it, so there is nothing to arrange. Its screens are separate "
                + "items held inside it: add some under \"View contents\".");
            return;
        }
        new NavArrangeWindow(_catalog, _doc, _stack, p, Rename.Display(p, _doc.Part(p))) { Owner = this }.ShowDialog();
    }

    /// <summary>Set how much gas or fuel a canister or tank holds. The result goes through the undo stack like any
    /// other edit, and changes the ship's value, its reaction mass and (on a torch tank) its burn time, so the
    /// rating report is refreshed with it.</summary>
    private void OpenFill(Placement p)
    {
        if (_doc is null || _catalog is null) return;
        if (ContainerFill.Describe(_doc.Part(p), _catalog) is not { } spec) return;

        var dlg = new FillDialog(Rename.Display(p, _doc.Part(p)), spec, p.Fill, _catalog) { Owner = this };
        if (dlg.ShowDialog() != true || SameFill(p.Fill, dlg.Fill)) return;

        _stack.Push(_doc, new SetFillCommand(p, p.Fill, dlg.Fill));
        UpdateInspector();
    }

    /// <summary>True when two fills say the same thing, so closing the dialog on an unchanged tank pushes nothing
    /// onto the undo stack. Null (stock) and a map that happens to hold the stock amounts are already collapsed to
    /// null by the dialog, so a plain key/value comparison is enough here.</summary>
    private static bool SameFill(IReadOnlyDictionary<string, double>? a, IReadOnlyDictionary<string, double>? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return a.Count == b.Count
               && a.All(kv => b.TryGetValue(kv.Key, out var v) && Math.Abs(v - kv.Value) <= ContainerFill.Epsilon);
    }

    /// <summary>
    /// Filter chips after a Shift+drag rectangle select: one checkable row per render layer in the
    /// catch, toggled live against the full band result — so a drag over a hull section can keep,
    /// say, just the walls without the floors beneath them. Skipped when the catch is one layer
    /// (nothing to filter). Unlike the right-click "Select only", chips combine (walls + conduits).
    /// </summary>
    private void OnBandFilterRequested()
    {
        if (_doc is null || _catalog is null) return;
        var all = Board.SelectedPlacements();
        var byLayer = all
            .GroupBy(p => _catalog.RenderLayer(_doc.Part(p)))
            .OrderBy(g => g.Key)
            .ToList();
        if (byLayer.Count < 2) return;

        var menu = new ContextMenu { PlacementTarget = Board };
        menu.Items.Add(new MenuItem
        {
            Header = $"{all.Count} parts selected — keep:",
            IsEnabled = false,
            FontWeight = FontWeights.SemiBold,
        });
        var included = byLayer.Select(g => g.Key).ToHashSet();
        foreach (var g in byLayer)
        {
            var layer = g.Key;
            var chip = new MenuItem
            {
                Header = $"{LayerName(layer)} ({g.Count()})",
                IsCheckable = true,
                IsChecked = true,
                StaysOpenOnClick = true,   // toggle several chips before dismissing (Esc / click away)
            };
            chip.Click += (_, _) =>
            {
                if (chip.IsChecked) included.Add(layer); else included.Remove(layer);
                Board.SetSelection(all.Where(p => included.Contains(_catalog.RenderLayer(_doc.Part(p)))));
            };
            menu.Items.Add(chip);
        }
        menu.IsOpen = true;
    }

    /// <summary>Friendly name for a render layer, for the context-menu layer filter.</summary>
    private static string LayerName(int layer) => layer switch
    {
        Catalog.LayerFloor => "Floors",
        Catalog.LayerWall => "Walls & doors",
        Catalog.LayerConduit => "Conduits",
        _ => "Fixtures",
    };

    // ---- input ----

    /// <summary>Status-bar hint shown while an enclosed air region is selected for a fill.</summary>
    private static string AirHint(int n) =>
        $"🪣 {n}-tile compartment selected — arm a part and press Enter to fill (Esc to cancel)";

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBoxBase) return;
        if (_freeze.IsFrozen) return;   // an engine is reading the live document off-thread; no edits until it lands
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        switch (e.Key)
        {
            case Key.R when ctrl && !e.IsRepeat:   // Replace the selection with a compatible part (no-op if none)
                ReplaceSelection();
                e.Handled = true;
                break;
            // same key as the game's build mode. Not on auto-repeat: a key held a beat too long would otherwise
            // spin the brush through several 90° steps, and the angle it settles on then follows you onto the
            // next part you arm.
            case Key.R when !ctrl && !e.IsRepeat:
                if (Board.ArmedPart is { } armed)
                {
                    Board.RotateArmed(shift ? -90 : 90);
                    AuditLog.Tool(BrushLabel(armed));   // the brush's angle is part of what the user did
                }
                else RotateSelection(shift ? -90 : 90);
                e.Handled = true;
                break;
            case Key.H when !ctrl && !e.IsRepeat:   // flip the selection: H = horizontal (left↔right), Shift+H = vertical
                FlipSelection(horizontal: !shift);
                e.Handled = true;
                break;
            // re-stack what shares a tile, the usual bracket shortcuts for it. Auto-repeat is allowed: holding one
            // walks the selection down (or up) the pile a step at a time, which is what a nudge is for.
            case Key.OemOpenBrackets when ctrl:
                Restack(forward: false);
                e.Handled = true;
                break;
            case Key.OemCloseBrackets when ctrl:
                Restack(forward: true);
                e.Handled = true;
                break;
            // step down the pile under the cursor. The list in the right-click menu is the exhaustive way through a
            // stack; this is the fast one, and it is what keeps a part drawn underneath one click away.
            //
            // Both OEM keys, because the backtick is not on the same virtual key on every layout: VK_OEM_3 on a US
            // keyboard, VK_OEM_8 on a UK one (where OEM_3 is the apostrophe). Taking both means the key marked `
            // works either way, at the cost of the UK apostrophe key doing it too — and nothing on the canvas
            // takes typed text, so there is nothing for that to collide with.
            case Key.Oem3 or Key.Oem8 when !ctrl && !e.IsRepeat:
                Board.CycleSelectionUnderCursor();
                e.Handled = true;
                break;
            case Key.Delete:
                DeleteSelection();
                e.Handled = true;
                break;
            case Key.D when ctrl && !e.IsRepeat:   // duplicate in place, just off the original
                DuplicateSelection();
                e.Handled = true;
                break;
            case Key.C when ctrl && !e.IsRepeat:
                CopySelection();
                e.Handled = true;
                break;
            case Key.V when ctrl && !e.IsRepeat:
                PasteClipboard();
                e.Handled = true;
                break;
            case Key.Enter when !ctrl && Board.AirSelection.Count > 0:   // fill the selected air region with the armed part
                Board.FillAirSelection();
                e.Handled = true;
                break;
            case Key.Escape:
                if (Board.WireMode)
                {
                    if (Board.ArmedPart is not null) { Board.SetArmed(null); ClearPaletteSelection(); }   // drop a held brush first
                    else if (Board.WireSourceArmed) Board.ClearWireSource();   // then the armed wire source
                    else Board.SetWireMode(false);                            // finally leave wire mode
                }
                else if (Board.AirSelection.Count > 0)
                {
                    Board.ClearAirSelection();   // drop the fill highlight first
                }
                else if (Board.ActiveZoneId is not null)
                {
                    Board.SetActiveZone(null);   // stop painting the zone
                }
                else if (Board.ArmedPart is not null)
                {
                    Board.SetArmed(null);
                    ClearPaletteSelection();
                }
                else
                {
                    Board.SelectedIds.Clear();
                    Board.ClearLooseSelection();
                    Board.InvalidateVisual();
                    UpdateInspector();
                }
                e.Handled = true;
                break;
            case Key.Z when ctrl && shift:   // Ctrl+Shift+Z: redo (the common alias for Ctrl+Y)
                if (_doc is not null) _stack.Redo(_doc);
                e.Handled = true;
                break;
            case Key.Z when ctrl:
                if (_doc is not null) _stack.Undo(_doc);
                e.Handled = true;
                break;
            case Key.Y when ctrl:
                if (_doc is not null) _stack.Redo(_doc);
                e.Handled = true;
                break;
            case Key.A when ctrl && !e.IsRepeat:   // select every part
                if (_doc is not null) { Board.SetSelection(_doc.Placements); UpdateInspector(); }
                e.Handled = true;
                break;
            case Key.S when ctrl && !e.IsRepeat:
                if (shift) SaveAs();
                else Save();
                e.Handled = true;
                break;
            case Key.O when ctrl && !e.IsRepeat:
                OpenFile();
                e.Handled = true;
                break;
            case Key.N when ctrl && !e.IsRepeat:
                NewDesign();
                e.Handled = true;
                break;
            // Tab management. Ctrl+W is free of the pan keys below because those are all guarded on !ctrl, and
            // taking Ctrl+Tab here stops the palette's own TabControl treating it as a tab-strip gesture.
            case Key.W when ctrl && !e.IsRepeat:
                CloseSession(_active);
                e.Handled = true;
                break;
            case Key.Tab when ctrl && !e.IsRepeat:
                CycleSession(shift ? -1 : 1);
                e.Handled = true;
                break;
            case Key.E when ctrl && !e.IsRepeat:   // export as a spawnable mod
                OnExportClick(this, e);
                e.Handled = true;
                break;
            case Key.I when ctrl && !e.IsRepeat:   // ship info (in-game identity)
                OnShipInfoClick(this, e);
                e.Handled = true;
                break;
            case Key.B when ctrl && !e.IsRepeat:   // bill of materials
                OnMaterialsClick(this, e);
                e.Handled = true;
                break;
            case Key.OemComma when ctrl && !e.IsRepeat:   // settings (the usual shortcut for it)
                OnSettingsClick(this, e);
                e.Handled = true;
                break;
            case Key.OemPlus or Key.Add when !ctrl:   // keyboard zoom in (anchored at the view centre)
                Board.ZoomStep(+1);
                e.Handled = true;
                break;
            case Key.OemMinus or Key.Subtract when !ctrl:   // keyboard zoom out
                Board.ZoomStep(-1);
                e.Handled = true;
                break;
            case Key.F when !ctrl:
                Board.FitContent();
                e.Handled = true;
                break;
            case Key.M when !ctrl && !e.IsRepeat:
                Board.CycleSymmetry();
                e.Handled = true;
                break;
            case Key.Z when !ctrl && !e.IsRepeat:
                Board.ToggleZones();
                e.Handled = true;
                break;
            case Key.P when !ctrl && !e.IsRepeat:
                Board.TogglePower();
                e.Handled = true;
                break;
            case Key.C when !ctrl && !e.IsRepeat:   // compartments (the game's own term for rooms)
                Board.ToggleRooms();
                e.Handled = true;
                break;
            case Key.L when !ctrl && !e.IsRepeat:   // Light Viz interior lighting
                Board.ToggleLight();
                e.Handled = true;
                break;
            case Key.T when !ctrl && !e.IsRepeat:   // Surfaces mode: paint the deck (T for tiles)
                Board.ToggleSurfaceMode();
                e.Handled = true;
                break;
            case Key.K when !ctrl && !e.IsRepeat:   // WalKViz crew access (W is the pan key)
                Board.ToggleWalk();
                e.Handled = true;
                break;
            case Key.W or Key.A or Key.S or Key.D when !ctrl:
                Board.SetPanKey(e.Key, true);   // smooth per-frame pan until KeyUp
                e.Handled = true;
                break;
            case Key.E when !ctrl && !e.IsRepeat:   // rotate the view, like in-game
                Board.RotateView(90);
                MarkViewOrientationChanged();
                e.Handled = true;
                break;
            case Key.Q when !ctrl && !e.IsRepeat:
                Board.RotateView(-90);
                MarkViewOrientationChanged();
                e.Handled = true;
                break;
            case Key.F1 when !e.IsRepeat:
                ShowHelp();
                e.Handled = true;
                break;
        }
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.W or Key.A or Key.S or Key.D) Board.SetPanKey(e.Key, false);
    }

    // ---- inspector ----

    private void UpdateInspector()
    {
        var selected = Board.SelectedPlacements();
        var part = Board.ArmedPart
                   ?? (selected.Count == 1 ? _doc?.Part(selected[0]) : null)
                   ?? (Board.SelectedLoose is { } lo ? _catalog?.Lookup(lo.DefName) : null);   // a selected loose floor item

        if (part is null)
        {
            InsFriendly.Text = selected.Count > 1 ? $"{selected.Count} parts selected" : "—";
            InsInternal.Text = "";
            DescBlock.Visibility = Visibility.Collapsed;
            InsCategory.Text = "";
            InsSize.Text = "";
            PriceBlock.Visibility = Visibility.Collapsed;
            InsOrigin.Text = "";
            InsInputs.Text = "";
            StatsBlock.Visibility = Visibility.Collapsed;
            RawExpander.Visibility = Visibility.Collapsed;
            FlagsExpander.Visibility = Visibility.Collapsed;
            return;
        }

        var lockedNote = Board.ArmedPart is null && selected.Count == 1 && _doc?.IsLocked(selected[0]) == true
            ? "  · fixed to the ship"
            : "";
        // a selected loose floor item shows its stacked count
        var looseNote = Board.ArmedPart is null && Board.SelectedLoose is { Quantity: > 1 } sl ? $"  · ×{sl.Quantity}" : "";
        // A named part leads with its own name; the stock one moves alongside so the row still says what the thing
        // actually is. Only ever on a lone selected placement — an armed palette part has no name of its own.
        var named = Board.ArmedPart is null && selected.Count == 1 ? selected[0].CustomName : null;
        InsFriendly.Text = (named ?? part.Friendly) + lockedNote + looseNote;
        InsInternal.Text = named is null ? part.DefName : $"{part.Friendly}  ·  {part.DefName}";
        if (part.Desc is { Length: > 0 } desc)
        {
            InsDesc.Text = desc;
            DescBlock.Visibility = Visibility.Visible;
        }
        else DescBlock.Visibility = Visibility.Collapsed;
        InsCategory.Text = part.Category;
        InsSize.Text = $"{part.Item.Width} × {part.Item.Height} tiles"
                       + (part.Item.HasSpriteSheet ? "  (auto-tiling)" : "");
        if (part.BasePrice > 0)
        {
            InsPrice.Text = "$" + part.BasePrice.ToString("#,##0.##", System.Globalization.CultureInfo.InvariantCulture);
            PriceBlock.Visibility = Visibility.Visible;
        }
        else PriceBlock.Visibility = Visibility.Collapsed;
        InsOrigin.Text = part.Origin;
        InsInputs.Text = part.Inputs.Length == 0 ? "none" : string.Join("\n", part.Inputs);
        // A tank's contents belong to the placed part, not the def, so only a lone selected placement has any
        // (an armed palette part is a def and holds whatever the def holds).
        PopulateStats(part, Board.ArmedPart is null && selected.Count == 1 ? selected[0] : null);
    }

    /// <summary>The curated key figures the inspector surfaces (in this order, only when present) — the raw game
    /// data values the game never shows as numbers. Mass is kilograms; Health is the durability pool
    /// (<c>StatDamageMax</c>); the "work" figures are install/dismantle/repair effort.</summary>
    private static readonly (string Key, string Label, string Unit)[] KeyStats =
    [
        ("StatMass", "Mass", "kg"),
        ("StatDamageMax", "Health", ""),
        ("StatPowerMax", "Power capacity", ""),
        ("StatPower", "Power draw", ""),
        ("StatInstallProgressMax", "Install work", ""),
        ("StatUninstallProgressMax", "Uninstall work", ""),
        ("StatDismantleProgressMax", "Dismantle work", ""),
        ("StatRepairProgressMax", "Repair work", ""),
        ("StatVolume", "Volume", "m³"),
        ("StatGasPressureMax", "Max pressure", ""),
        ("StatThrustStrength", "Thrust", ""),
        ("StatArmorBlunt", "Armor (blunt)", ""),
        ("StatArmorCut", "Armor (cut)", ""),
    ];

    /// <summary>Fill the STATS block (curated key figures), the "All game data (raw)" list (every numeric
    /// <c>Stat*</c> cond verbatim), and the "Conditions (flags)" list (every non-<c>Stat</c> starting cond) for the
    /// selected part — the true, raw figures the game keeps hidden. All three read <see cref="PartDef"/> data
    /// already in memory (the same source as Base Value), so this adds no data loading.</summary>
    private void PopulateStats(PartDef part, Placement? placed)
    {
        var vals = part.StartingCondValues;

        StatsList.Children.Clear();
        foreach (var (key, label, unit) in KeyStats)
            if (vals.TryGetValue(key, out var v))
                StatsList.Children.Add(StatRow(label, FormatStat(v) + (unit.Length > 0 ? " " + unit : ""), raw: false));

        // What this particular tank holds, when it is not simply what the def ships with. Shown against the
        // curated figures rather than in the raw list, which is deliberately the def's own data verbatim.
        if (_catalog is { } cat && ContainerFill.Describe(part, cat) is { } spec)
        {
            var fill = placed?.Fill ?? spec.Stock;
            foreach (var line in spec.Lines)
            {
                var amount = fill.GetValueOrDefault(line.Cond);
                if (amount <= 0) continue;
                StatsList.Children.Add(StatRow(line.Label,
                    FormatStat(amount) + (line.IsGas ? " mol" : " kg"), raw: false));
            }
            if (spec.HasGas)
                StatsList.Children.Add(StatRow("Pressure",
                    FormatStat(spec.PressureFor(ContainerFill.TotalMols(fill))) + " kPa", raw: false));
        }

        StatsBlock.Visibility = StatsList.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        RawList.Children.Clear();
        foreach (var kv in vals.Where(kv => kv.Key.StartsWith("Stat", StringComparison.Ordinal))
                                .OrderBy(kv => kv.Key, StringComparer.Ordinal))
            RawList.Children.Add(StatRow(kv.Key, FormatStat(kv.Value), raw: true));
        RawExpander.Visibility = RawList.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var flags = part.StartingConds.Where(c => !c.StartsWith("Stat", StringComparison.Ordinal))
                                      .OrderBy(c => c, StringComparer.Ordinal).ToList();
        FlagsList.Text = string.Join(", ", flags);
        FlagsExpander.Visibility = flags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string FormatStat(double v) => v.ToString("#,##0.####", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A two-column inspector row: dim label on the left, right-aligned value on the right.</summary>
    private static FrameworkElement StatRow(string label, string value, bool raw)
    {
        var size = raw ? 11.0 : 12.0;
        var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
        var val = new TextBlock { Text = value, Foreground = ThemeManager.Ink, FontSize = size };
        DockPanel.SetDock(val, Dock.Right);
        row.Children.Add(val);
        row.Children.Add(new TextBlock
        {
            Text = label, Foreground = ThemeManager.Dim, FontSize = size,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0),
        });
        return row;
    }

    // ---- toolbar ----

    private void OnNewClick(object sender, RoutedEventArgs e) => NewDesign();

    private void OnOpenClick(object sender, RoutedEventArgs e) => OpenFile();
    private void OnSaveClick(object sender, RoutedEventArgs e) => Save();
    private void OnSaveAsClick(object sender, RoutedEventArgs e) => SaveAs();

    /// <summary>
    /// Export the current design as a spawnable local data mod. Runs the P2 engine to bake
    /// <c>aRooms</c>/<c>aRating</c>, reverse-maps every part to the game's centre/CCW coordinates,
    /// and writes a mod folder — never <c>loading_order.json</c> (registration stays with
    /// Ostrasort/ModTools; the dialog and confirmation both say so).
    /// </summary>
    /// <summary>Edit the ship's in-game identity (name/make/model/year/designation/description) and its
    /// <see cref="DocumentKind"/>. The identity values live on <see cref="_meta"/> and the kind on the document;
    /// both persist in the .oplan, and the identity pre-fills the export dialog.</summary>
    private void OnShipInfoClick(object sender, RoutedEventArgs e)
    {
        if (_doc is null) return;
        var dlg = new ShipInfoDialog(_meta, _doc.Kind) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        if (dlg.PublicName == _meta.PublicName && dlg.Make == _meta.Make && dlg.Model == _meta.Model
            && dlg.Year == _meta.Year && dlg.Designation == _meta.Designation && dlg.Description == _meta.Description
            && dlg.Kind == _doc.Kind)
            return;   // nothing changed — don't dirty the document
        dlg.ApplyTo(_meta);
        // A kind switch retires whichever analysis no longer applies, so any open report is describing the design
        // under the old reading of it. Close rather than mark stale: a re-run would rebuild the same window with
        // the wrong headline (the window is told which it is at construction).
        if (dlg.Kind != _doc.Kind) { _doc.Kind = dlg.Kind; CloseReports(_active); }
        _stateDirty = true;
        RefreshChrome();
    }

    /// <summary>The Ship Rating report's towed-mass box writes a persisted design property (see
    /// <see cref="ShipDocument.ExtraMassKg"/>). It moves no part, so it goes through the same non-command
    /// unsaved-state path as ship identity and view orientation rather than the layout-changed event.</summary>
    private void SetExtraMass(ShipDocument doc, double kg)
    {
        if (kg.Equals(doc.ExtraMassKg)) return;
        doc.ExtraMassKg = kg;
        _stateDirty = true;
        RefreshChrome();
    }

    /// <summary>A Q/E view rotation changes persisted state now (the .oplan stores the orientation), so flag the
    /// design as having unsaved changes — but only for a real document (not the empty startup state).</summary>
    private void MarkViewOrientationChanged()
    {
        if (_doc is null || _stateDirty) return;
        _stateDirty = true;
        RefreshChrome();
    }

    /// <summary>
    /// Open the export wizard (see <see cref="ExportWizard"/>). Everything the export does now lives behind it: the
    /// destination's own steps, the Review that builds before anything is written, and the Done pane that reports
    /// what happened.
    /// </summary>
    private void OnExportClick(object sender, RoutedEventArgs e) => OpenExportWizard(null);

    /// <summary>Build the wizard's session from the live document and show it. <paramref name="preselect"/> picks a
    /// destination up front, which is what <c>Analyse ▸ Update Ship in Save…</c> does.</summary>
    private void OpenExportWizard(ExportDestination? preselect, SaveSourceRef? updateTarget = null)
    {
        if (_doc is null || _catalog is null || _index is null || _env is null) return;
        if (_doc.Placements.Count == 0)
        {
            Dlg.Show(this, "Place some parts before exporting.", "Export",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // specs + a value estimate are needed before the wizard: the estimate pre-fills the starting-ship mortgage
        _roomSpecs ??= RoomCertifier.LoadSpecs(_index);
        // Whatever this design is bound to in this sitting: the import stamps SourceSave, and a design written by
        // an older build has only the context its legacy source was relocated into on open.
        var bound = _doc.SourceSave ?? _saveContext?.Source;
        var session = new WizardSession
        {
            Plan = ExportPlan.FromSettings(_settings, _meta, bound),
            Doc = _doc,
            Catalog = _catalog,
            Specs = _roomSpecs,
            Index = _index,
            Env = _env,
            Settings = _settings,
            Meta = _meta,
            Saves = SaveImport.ListSaves(_env),
            SourceSave = bound,
            UpdateTarget = updateTarget,
            SaveContext = _saveContext,
            Palette = _allParts,
            BuyEstimate = ShipValue.Estimate(_doc, _catalog, _roomSpecs).BuyEstimate,
            OstrasortKnown = OstrasortLauncher.Detect(_settings) is not null,
            RenderPreview = () => Board.RenderGamePreview(_roomSpecs),
        };

        // The wizard reads the live document off-thread while it builds, so the editing surface goes dead for its
        // whole run rather than only around each engine call.
        var wizard = new ExportWizard(session, preselect) { Owner = this };
        using (FreezeDoc()) wizard.ShowDialog();

        // the update destination relocates the save context on demand; keep it for the rest of the session
        _saveContext ??= session.SaveContext;

        // Stand-in parts go straight onto the document rather than through the undo stack, so nothing else would
        // record that the design now has unsaved changes. Same path as ship identity and view orientation.
        if (wizard.DocumentEdited)
        {
            _stateDirty = true;
            RefreshChrome();
        }

        // Name and identity edited in the wizard flow back onto the design's saved metadata, so the two never drift.
        ApplyExportedIdentity(session.Plan.ShipName, session.Plan.Identity);
    }

    /// <summary>Fold the name and identity the user typed in the export wizard back into the design's own metadata,
    /// marking the design dirty only when something actually changed. The name is folded back for the same reason
    /// the rest is: without it the wizard's "Ship name" box holds for that one export and then reverts, because
    /// every export re-seeds it from the design.</summary>
    private void ApplyExportedIdentity(string name, ExportMetadata id)
    {
        var newName = name.Length > 0 ? name : _meta.Name;   // a cancel before the ship step leaves it unset
        if (newName == _meta.Name
            && id.PublicName == _meta.PublicName && id.Make == _meta.Make && id.Model == _meta.Model
            && id.Year == _meta.Year && id.Designation == _meta.Designation && id.Description == _meta.Description)
            return;

        _meta.Name = newName;
        SetMetaIdentity(id);
        _stateDirty = true;
        RefreshChrome();
    }

    /// <summary>Copy an <see cref="ExportMetadata"/> onto the design's metadata. Does not touch the design name or
    /// the dirty flag: the two callers differ on both.</summary>
    private void SetMetaIdentity(ExportMetadata id)
    {
        _meta.PublicName = id.PublicName; _meta.Make = id.Make; _meta.Model = id.Model;
        _meta.Year = id.Year; _meta.Designation = id.Designation; _meta.Description = id.Description;
    }

    /// <summary>Save a PNG image of the ship (sprites only — no grid, overlays or UI) for sharing or reference.</summary>
    private void OnSnapshotClick(object sender, RoutedEventArgs e)
    {
        if (_doc is null || _doc.Placements.Count == 0)
        {
            Dlg.Show(this, "Place some parts before taking a snapshot.", "Snapshot",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Filter = "PNG image (*.png)|*.png",
            FileName = string.Join("_", _meta.Name.Split(Path.GetInvalidFileNameChars())) + ".png",
        };
        if (dlg.ShowDialog(this) != true) return;

        if (Board.RenderSnapshot() is not { } bmp)
        {
            Dlg.Show(this, "Nothing to snapshot.", "Snapshot", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            using var fs = File.Create(dlg.FileName);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            encoder.Save(fs);
            AuditLog.Add($"Saved snapshot {dlg.FileName}.");
        }
        catch (Exception ex)
        {
            Dlg.Show(this, "Could not save the snapshot:\n\n" + ex.Message, "Snapshot",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---- toolbar dropdown menus (File / Design / View) ----

    /// <summary>A menu item wired to an action, optionally disabled, shown as a check state, and/or labelled with
    /// its keyboard shortcut (<paramref name="gesture"/> → the right-aligned <c>InputGestureText</c>, so the dropdown
    /// advertises the shortcut like a standard app menu).</summary>
    private static MenuItem MenuAction(string header, Action act, bool enabled = true, bool? check = null, string? gesture = null)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        if (gesture is not null) item.InputGestureText = gesture;
        if (check is { } c) { item.IsCheckable = true; item.IsChecked = c; }
        item.Click += (_, _) => act();
        return item;
    }

    private static void OpenMenuUnder(ContextMenu menu, UIElement anchor)
    {
        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>The View ▸ "Light Viz" controls. The overlay itself renders game-exact (no brightness/dimming
    /// tuners); what's configurable is the <b>exterior daylight</b>: which parallax location's sun lights shine on
    /// the design (hull-occluded, streaming through windows — glass never blocks light) and the rotation of the
    /// sun constellation (the game's world rotation of its far sun transform). Both persist.</summary>
    private MenuItem LightDimmingItem()
    {
        var menu = new MenuItem { Header = "Light Viz" };
        var current = _settings.LightSunParallax ?? "";
        var sunMenu = new MenuItem { Header = "Exterior sun" };
        sunMenu.Items.Add(MenuAction("None", () => SetSunParallax(null), check: current.Length == 0));
        if (_catalog is { } cat)
            foreach (var p in cat.ParallaxDefs.Values.Where(p => p.SunLightNames.Length > 0).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
                sunMenu.Items.Add(MenuAction(p.Name, () => SetSunParallax(p.Name), check: current == p.Name));
        menu.Items.Add(sunMenu);
        menu.Items.Add(MenuSliderRow("Sun angle", 0, 360, _settings.LightSunAngle, "0", SetSunAngle));
        return menu;
    }

    /// <summary>
    /// The WalkViz submenu: the two questions the game's own answer depends on state a plan does not carry.
    /// "Count spacewalks" admits tiles that are not part of the ship, which is what the game does (walking needs no
    /// floor) but which reads as one all-encompassing zone unless the user asks for it; "Respect Forbid zones"
    /// picks whether the analysis speaks for a crew member the painted zone actually binds. Both persist.
    /// </summary>
    private MenuItem WalkOptionsItem()
    {
        var menu = new MenuItem { Header = "Walk overlay" };
        menu.Items.Add(MenuAction("Count spacewalks", () => SetWalkOption(exterior: !_settings.WalkIncludeExterior),
            check: _settings.WalkIncludeExterior));
        menu.Items.Add(MenuAction("Respect Forbid zones", () => SetWalkOption(forbid: !_settings.WalkRespectForbidZones),
            check: _settings.WalkRespectForbidZones));
        return menu;
    }

    /// <summary>Persist a WalkViz switch and recompute, but only when the overlay is actually on.</summary>
    private void SetWalkOption(bool? exterior = null, bool? forbid = null)
    {
        if (exterior is { } ex) _settings.WalkIncludeExterior = ex;
        if (forbid is { } fb) _settings.WalkRespectForbidZones = fb;
        _settings.Save();
        if (Board.ShowWalk) ScheduleScan();
    }

    /// <summary>A labelled slider plus an editable numeric box hosted in a menu (stays open while adjusting): drag
    /// the slider or type an exact value (committed on Enter or focus loss). Both push through
    /// <paramref name="onChange"/> live, and the box shows the current value in <paramref name="format"/>. An
    /// optional <paramref name="suffix"/> is the unit, shown after the box ("min", "per design").</summary>
    private static MenuItem MenuSliderRow(string label, double min, double max, double value, string format,
        Action<double> onChange, string? suffix = null)
    {
        var slider = new Slider
        {
            Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), Width = 120,
            SmallChange = (max - min) / 40, LargeChange = (max - min) / 5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var box = new TextBox
        {
            Width = 44, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right,
            Margin = new Thickness(6, 0, 0, 0),
            Text = slider.Value.ToString(format, System.Globalization.CultureInfo.InvariantCulture),
        };
        slider.ValueChanged += (_, e) =>
        {
            box.Text = e.NewValue.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
            onChange(e.NewValue);
        };
        void Commit()
        {
            if (double.TryParse(box.Text, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var v))
                slider.Value = Math.Clamp(v, min, max);   // fires ValueChanged → onChange + refreshes the box
            else
                box.Text = slider.Value.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
        }
        // A TextBox inside a menu needs the click handled here or the menu swallows it before it can focus.
        box.PreviewMouseLeftButtonDown += (_, e) => { box.Focus(); box.SelectAll(); e.Handled = true; };
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 2, 8, 2) };
        row.Children.Add(new TextBlock { Text = label, Width = 72, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(slider);
        row.Children.Add(box);
        if (suffix is { Length: > 0 })
            row.Children.Add(new TextBlock
            {
                Text = suffix, Foreground = ThemeManager.Dim, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
            });
        return new MenuItem { Header = row, StaysOpenOnClick = true };
    }

    /// <summary>Apply and persist the Light Viz exterior-sun location (null = no sun), then rescan so the
    /// lighting recomputes with the new daylight.</summary>
    private void SetSunParallax(string? name)
    {
        _settings.LightSunParallax = name;
        _settings.Save();
        if (Board.ShowLight) ScheduleScan();
    }

    /// <summary>Apply and persist the Light Viz sun-constellation angle (degrees), then rescan.</summary>
    private void SetSunAngle(double deg)
    {
        _settings.LightSunAngle = deg;
        _settings.Save();
        if (Board.ShowLight) ScheduleScan();
    }

    /// <summary>The File ▾ dropdown: document lifecycle, auto-save and recovery, import, export, and write-back to a
    /// save.</summary>
    private void OnFileMenuClick(object sender, RoutedEventArgs e)
    {
        var m = new ContextMenu();
        m.Items.Add(MenuAction("New", () => OnNewClick(this, e), gesture: "Ctrl+N"));
        m.Items.Add(MenuAction("Open…", () => OnOpenClick(this, e), gesture: "Ctrl+O"));
        // Only worth an item once there is more than one to close: with a single design open the tab strip is not
        // shown either, and closing the last one is refused.
        m.Items.Add(MenuAction("Close Design", () => CloseSession(_active), enabled: _sessions.Count > 1,
            gesture: "Ctrl+W"));
        m.Items.Add(MenuAction("Save", () => OnSaveClick(this, e), gesture: "Ctrl+S"));
        m.Items.Add(MenuAction("Save As…", () => OnSaveAsClick(this, e), gesture: "Ctrl+Shift+S"));
        m.Items.Add(AutoSaveMenuItem());
        m.Items.Add(new Separator());
        m.Items.Add(BuildImportSubmenu());
        m.Items.Add(MenuAction("Export…", () => OnExportClick(this, e), gesture: "Ctrl+E"));
        m.Items.Add(new Separator());
        // A whole operation rather than a variant of import or export, and it chains both, so it sits beside them
        // rather than inside either. Being findable is the entire point of it existing.
        m.Items.Add(MenuAction("Transfer Ship to Another Save…",
            () => TransferShip(DocumentKind.Ship), enabled: _env is not null));
        m.Items.Add(MenuAction("Transfer Apartment to Another Save…",
            () => TransferShip(DocumentKind.Residence), enabled: _env is not null));
        // A design imported from a save goes back to the ship it came from; any other design is asked which ship
        // in which save it should replace, so it needs only a save to exist. One item rather than two, unlike
        // Import and Transfer above: this one acts on the open design, so it already knows which it is and there
        // is nothing for the user to choose between.
        m.Items.Add(MenuAction(_doc?.IsResidence == true ? "Update Apartment in Save…" : "Update Ship in Save…",
            () => OnUpdateSaveClick(this, e),
            enabled: _doc is not null && _env is not null));
        OpenMenuUnder(m, BtnFileMenu);
    }

    /// <summary>The Import ▸ submenu: start a design from an existing ship or a save game.</summary>
    private MenuItem BuildImportSubmenu()
    {
        var import = new MenuItem { Header = "Import" };
        import.Items.Add(MenuAction("From ship template…", () => ImportTemplate(DocumentKind.Ship)));
        import.Items.Add(MenuAction("From apartment template…", () => ImportTemplate(DocumentKind.Residence)));
        // Named for what it lists, because the complaint about the old wording was that nobody could tell it
        // reached anything but the ship you were standing on.
        import.Items.Add(MenuAction("From a ship or apartment in a save (layout only)…", ImportSave));
        import.Items.Add(new Separator());
        // Two entries rather than one picker with both in it. A ship and an apartment are edited the same way but
        // they are not the same errand, and the ship list is the one people are looking down when they mean the
        // other. Each action lists only its own kind, so neither can be picked by accident.
        import.Items.Add(MenuAction("Your ship, for editing (write back to the save)…",
            () => ImportSaveForEditing(DocumentKind.Ship)));
        import.Items.Add(MenuAction("Your apartment, for editing (write back to the save)…",
            () => ImportSaveForEditing(DocumentKind.Residence)));
        return import;
    }

    /// <summary>The Design ▾ dropdown: ship identity, wall/floor re-skin, snapshot, the bill of materials, and the
    /// atmospheric flight report.</summary>
    private void OnDesignMenuClick(object sender, RoutedEventArgs e)
    {
        var m = new ContextMenu();
        m.Items.Add(MenuAction("Ship Info…", () => OnShipInfoClick(this, e), gesture: "Ctrl+I"));
        m.Items.Add(MenuAction("Ship Re-skin…", () => OnThemeClick(this, e)));
        // Whole-ship like the re-skin above it, and reached the same way. The count can't be shown in the header:
        // the menu is built before the document is walked, and walking every part to label a menu item would run on
        // every menu open. RepairAll reports it in the confirmation instead.
        m.Items.Add(MenuAction("Repair All…", RepairAll, enabled: _doc is not null));
        m.Items.Add(new Separator());
        m.Items.Add(MenuAction("Snapshot…", () => OnSnapshotClick(this, e)));
        m.Items.Add(MenuAction("Bill of Materials…", () => OnMaterialsClick(this, e), gesture: "Ctrl+B"));
        // Atmospheric flight on a design with no drive and no rotors is not a report, it is a column of zeroes.
        m.Items.Add(MenuAction("Flight Dynamics…", () => OnFlightClick(this, e),
            enabled: _doc is not null && !_doc.IsResidence));
        OpenMenuUnder(m, BtnDesignMenu);
    }

    /// <summary>The View ▾ dropdown: fit, symmetry, and the Light Viz / Walk overlay options. The overlay toggles
    /// (Zones / Rooms / Power / Light / Wire) live on the toolbar as highlighted buttons, and the mod-override rule
    /// moved to Settings (it is a preference, not a view), so neither is duplicated here. State is read live when
    /// the menu opens (the active symmetry mode / the checkmark).</summary>
    private void OnViewMenuClick(object sender, RoutedEventArgs e)
    {
        var m = new ContextMenu();
        m.Items.Add(MenuAction("Fit to ship", Board.FitContent, gesture: "F"));
        m.Items.Add(new Separator());

        var sym = new MenuItem { Header = "Symmetry", InputGestureText = "M" };
        foreach (var (mode, label) in new[]
                 {
                     (SymmetryMode.Off, "Off"), (SymmetryMode.Vertical, "Vertical"),
                     (SymmetryMode.Horizontal, "Horizontal"), (SymmetryMode.Both, "Both"),
                 })
            sym.Items.Add(MenuAction(label, () => Board.SetSymmetry(mode), check: Board.SymMode == mode));
        m.Items.Add(sym);

        m.Items.Add(new Separator());
        m.Items.Add(LightDimmingItem());
        m.Items.Add(WalkOptionsItem());
        m.Items.Add(SurfaceOptionsItem());
        OpenMenuUnder(m, BtnViewMenu);
    }

    // ---- toolbar view toggles (promoted from the View menu) ----

    private void OnZonesToggleClick(object sender, RoutedEventArgs e) => Board.ToggleZones();
    private void OnRoomsToggleClick(object sender, RoutedEventArgs e) => Board.ToggleRooms();
    private void OnPowerToggleClick(object sender, RoutedEventArgs e) => Board.TogglePower();
    private void OnLightToggleClick(object sender, RoutedEventArgs e) => Board.ToggleLight();
    private void OnWalkToggleClick(object sender, RoutedEventArgs e) => Board.ToggleWalk();
    private void OnWireToggleClick(object sender, RoutedEventArgs e) => Board.ToggleWireMode();

    /// <summary>Reflect the live overlay state onto the toolbar toggle buttons' IsChecked, so the Fluent theme paints
    /// the active view with its own (theme-aware, correct-contrast) checked accent. Called at startup and from every
    /// ...Changed handler, so the highlight stays in step whether the toggle came from a button, a keyboard gesture,
    /// or code. Assigning IsChecked raises Checked/Unchecked but never Click, so this cannot re-enter the toggles.</summary>
    private void SyncViewToggles()
    {
        BtnZones.IsChecked = Board.ShowZones;
        BtnRooms.IsChecked = Board.ShowRooms;
        BtnPower.IsChecked = Board.ShowPower;
        BtnLight.IsChecked = Board.ShowLight;
        BtnWalk.IsChecked = Board.ShowWalk;
        BtnWire.IsChecked = Board.WireMode;
        BtnSurface.IsChecked = Board.SurfaceMode;
    }

    /// <summary>
    /// Pick a save, pick anything in it you own, and import that as a pristine layout.
    ///
    /// <para>One list for ships and apartments alike, unfiltered. This path writes nothing, so there is no wrong
    /// row to land on and no reason to make the user choose the errand before the thing. It used to skip the
    /// question entirely and take whatever the character was standing on, which is a station as often as not.</para>
    /// </summary>
    private async void ImportSave()
    {
        if (_catalog is null || _env is null) return;

        var saves = SaveImport.ListSaves(_env);
        if (saves.Count == 0)
        {
            Dlg.Show(this, "No save games found in your Ostranauts Saves folder.", "Import",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var picker = new SavePickerDialog(saves, "Import from a save game",
            "Imports a ship or apartment you own as a pristine layout — crew, wear and damage are discarded.",
            "Choose save") { Owner = this };
        if (picker.ShowDialog() != true || picker.Selected is not { } save) return;

        var ships = SaveImport.ListPlayerShips(save.ZipPath);
        if (ships.Count == 0)
        {
            // An unreadable save and a save holding nothing of yours both come back empty, and they are not the
            // same news, so say which.
            Dlg.Show(this, SaveImport.WhyUnreadable(save.ZipPath)
                    ?? "Couldn't find anything you own in that save (no ships, no apartments, and no current ship "
                       + "on record).",
                "Import", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var shipDlg = new ShipChoiceDialog(save.Name, ships, kind: null,
            title: "Import which ship or apartment?",
            note: $"Everything you own in save “{save.Name}”. Only the layout is read: nothing is written to the "
                + "save, now or later.") { Owner = this };
        if (shipDlg.ShowDialog() != true || shipDlg.Selected is not { } chosen) return;

        var noun = chosen.IsResidence ? "apartment" : "ship";
        var who = save.PlayerName.Length > 0 ? $"{save.PlayerName}'s " : "";
        if (AskImportOptions($"Import “{chosen.Name}” for planning?",
                $"{chosen.RegId} from {who}save “{save.Name}”. The design arrives as its own thing: wear and damage "
                + $"are discarded, and it is never written back to that save. To redesign the live {noun} and put "
                + $"the result in the game, use \"your {noun}, for editing\" instead.")
            is not { } options)
            return;

        var (catalog, zip, regId) = (_catalog, save.ZipPath, chosen.RegId);
        ImportResult result;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            result = await Ui.OffThread(() => SaveImport.ImportShipLayout(zip, regId, catalog, options));
        }
        catch (Exception ex)
        {
            Dlg.Show(this, "Import failed:\n\n" + ex.Message, "Import", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        InstallImportedDocument(result, options: options);
        AuditLog.Add($"Imported {noun} \"{chosen.Name}\" ({chosen.RegId}) from save \"{save.Name}\" (layout only).");
    }

    /// <summary>Import the player's ship FOR EDITING: keeps each part's save identity plus a full context, so
    /// the edited layout can be written back into a copy of the save with crew and cargo preserved.</summary>
    private async void ImportSaveForEditing(DocumentKind kind)
    {
        var residence = kind == DocumentKind.Residence;
        var noun = residence ? "apartment" : "ship";

        var edit = await PickAndImportForEditing(
            $"Import your {noun} for editing",
            $"Imports your live {noun} with its identity, crew, cargo and wear intact, so the redesign can be "
            + "written back into the save it came from.",
            "Choose save",
            (save, chosen) =>
            Dlg.Confirm(this, DlgKind.Info, $"Import \"{chosen.Name}\" for editing?",
                $"{(residence ? "Apartment" : "Ship")} {chosen.RegId} from save \"{save.Name}\".\n\n" +
                $"You'll redesign the {noun}'s structure out of game.\n" +
                "When you choose the Update Ship in Save action, Ostraplan writes the result back into the save, either as a new copy (the default) or the original in place, keeping crew, cargo, world position, and ship identity.\n\n" +
                (residence
                    ? "It keeps its registration, its place at the station and the transit route that reaches it: only the layout changes.\n\n"
                    : "") +
                "The .oplan you save is a design and nothing more. It records no save and no ship, so it opens, " +
                "edits and exports whether or not this save still exists, and you can send it to someone who has " +
                $"never seen it. Reopen it another day and the write-back asks which {noun} to write over, this " +
                "one included.\n\n" +
                (residence
                    ? "There is no mod export for an apartment: the game sells one through a Real Estate broker, which a ship mod cannot stock."
                    : "For a standalone, shareable ship instead, use Export, which makes a spawnable mod."),
                "Import for editing"),
            kind);
        if (edit is null) return;

        AuditLog.Add($"Imported {noun} {Describe(edit)} for editing.");
    }

    /// <summary>
    /// Transfer a ship from one save into another: the first half of the trip in one action.
    ///
    /// <para>This was always possible and almost nobody found it, because it was two unrelated menu items in
    /// sequence — import your ship for editing from save A, then export it into save B. Both halves are unchanged;
    /// this only walks them, and lands the user in the wizard with the destination already chosen.</para>
    ///
    /// <para>The ship still arrives on the canvas as a design rather than moving behind the user's back. It is what
    /// they are about to copy into another save, and it is the last chance to look at it.</para>
    /// </summary>
    private async void TransferShip(DocumentKind kind)
    {
        var residence = kind == DocumentKind.Residence;
        var noun = residence ? "apartment" : "ship";

        var edit = await PickAndImportForEditing(
            $"Transfer {(residence ? "an apartment" : "a ship")}: which save is it in?",
            $"Step 1 of 2. Choose the save the {noun} is in now. You'll pick the save it goes to next.",
            "Choose source",
            (save, chosen) =>
            Dlg.Confirm(this, DlgKind.Info, $"Transfer \"{chosen.Name}\" to another save?",
                $"{(residence ? "Apartment" : "Ship")} {chosen.RegId} from save \"{save.Name}\".\n\n" +
                (residence
                    ? "Ostraplan reads the apartment in, then asks which save to add it to and which station in that save it belongs at. It arrives there as a residence you own, registered at that station, in a copy of that save. Both saves keep working: this copies the apartment rather than moving it, and neither original is modified.\n\n"
                    : "Ostraplan reads the ship in, then asks which save to add it to. It arrives there as a brand-new ship you own, parked a few kilometres out, in a copy of that save. Both saves keep working: this copies the ship rather than moving it, and neither original is modified.\n\n") +
                "Layout, cargo, loose items, zones and device wiring all make the trip, and each part keeps the condition it really has.\n\n" +
                (residence
                    ? "The station is chosen fresh in the destination save, so the apartment does not have to land at the same one it came from. You become a homeowner there.\n\n"
                    : "") +
                $"Crew do not come along. They belong to the save they are in, not to the {noun}.",
                $"Read the {noun} in"),
            kind);
        if (edit is null) return;

        AuditLog.Add($"Read {noun} {Describe(edit)} in to transfer to another save.");

        // Straight on to the second half, with the destination preselected: the save picker there is the one that
        // asks where it goes, so the user never returns to a menu to finish what they started.
        OpenExportWizard(ExportDestination.NewShipInSave);
    }

    /// <summary>
    /// Pick a save and one of its ships, import it for editing, and install it as the current design. The shared
    /// body of "Your ship, for editing" and of the transfer, which differ only in the confirmation that explains
    /// what happens next. Null when the user backed out anywhere along it, or the import failed (already reported).
    /// </summary>
    private async Task<SaveEditImportResult?> PickAndImportForEditing(
        string title, string pickerNote, string pickerVerb, Func<SaveEntry, SaveShipChoice, bool> confirm,
        DocumentKind kind = DocumentKind.Ship)
    {
        if (_catalog is null || _env is null) return null;

        var saves = SaveImport.ListSaves(_env);
        if (saves.Count == 0)
        {
            Dlg.Show(this, "No save games found in your Ostranauts Saves folder.", title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        var picker = new SavePickerDialog(saves, title, pickerNote, pickerVerb) { Owner = this };
        if (picker.ShowDialog() != true || picker.Selected is not { } save) return null;

        // choose WHICH ship: the game imports the ship you're standing on, which may be a station. List what the
        // player actually owns instead (aMyShips for vessels, the ship-owner registry for apartments, which never
        // reach aMyShips), plus the current ship as an unsupported option.
        var residence = kind == DocumentKind.Residence;
        var all = SaveImport.ListPlayerShips(save.ZipPath);
        var ships = all.Where(s => s.IsResidence == residence).ToList();
        if (ships.Count == 0)
        {
            // Say which of the three things went wrong. "No apartments" on a save that has three ships in it is a
            // different problem from a save that holds nothing of yours, and both are different from one Ostraplan
            // could not read at all.
            Dlg.Show(this,
                all.Count > 0 && residence
                    ? $"No apartments in that save. Ostraplan found {all.Count} ship(s) there, so the save read "
                      + "fine — you just don't own a residence in it yet. Buy one from a station's Real Estate "
                      + "kiosk, or use \"From apartment template\" to design one from scratch."
                    : SaveImport.WhyUnreadable(save.ZipPath)
                      ?? (residence
                          ? "Couldn't find anything you own in that save."
                          : "Couldn't find a ship in that save (no owned ships and no current ship on record)."),
                title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        var shipDlg = new ShipChoiceDialog(save.Name, ships, kind) { Owner = this };
        if (shipDlg.ShowDialog() != true || shipDlg.Selected is not { } chosen) return null;

        // editing a ship you don't own (a station, another vessel) is unsupported — gate it behind a stern warning
        if (!chosen.Owned && !ConfirmUnsupportedShip(chosen)) return null;
        if (!confirm(save, chosen)) return null;

        var (catalog, entry, reg) = (_catalog, save, chosen.RegId);
        SaveEditImportResult edit;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            edit = await Ui.OffThread(() => SaveEditImport.ImportForEditing(entry, reg, catalog));
        }
        catch (Exception ex)
        {
            Dlg.Show(this, "Import failed:\n\n" + ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
        finally { Mouse.OverrideCursor = null; }

        if (!OfferStandIns(edit, chosen.Name)) return null;   // cancelled at the missing-mods prompt
        InstallImportedDocument(edit.Import, edit.Context);
        return edit;
    }

    /// <summary>An imported ship as an audit-log line names it: display name, registration and source save.</summary>
    private static string Describe(SaveEditImportResult edit) =>
        $"\"{edit.Import.ShipName}\" ({edit.Context.Source.RegId}) from save \"{edit.Context.Source.SaveName}\"";

    /// <summary>
    /// The missing-mod prompt: items whose def isn't in the loaded data are invisible to every engine here but
    /// still sit in the save, so a write-back can corrupt the ship's rooms and grid (see <see cref="Substitution"/>).
    /// Offer a real part to stand in for each. Returns false when the user cancels the import outright.
    /// Applied to the imported document before it is installed, so it lands complete and the stand-ins are part of
    /// the design from the first undo step.
    /// </summary>
    private bool OfferStandIns(SaveEditImportResult edit, string shipName)
    {
        if (_catalog is null) return true;
        var outstanding = Substitution.Outstanding(edit.Doc, edit.Context, _catalog);
        if (outstanding.Count == 0) return true;

        var defs = outstanding
            .GroupBy(u => u.DefName, StringComparer.Ordinal)
            .Select(g => new MissingDefVM(g.Key, g.Count()))
            .OrderByDescending(v => v.Count).ThenBy(v => v.DefName, StringComparer.Ordinal)
            .ToList();

        var dlg = new MissingPartsDialog(defs, _allParts, shipName) { Owner = this };
        if (dlg.ShowDialog() != true) return false;

        var choices = dlg.Choices;
        if (choices.Count == 0) return true;

        var placed = 0;
        using (edit.Doc.SuspendChanged())
            foreach (var item in outstanding)
                if (choices.TryGetValue(item.DefName, out var part))
                {
                    new PlaceCommand(Substitution.StandIn(item, part.DefName, _catalog, edit.Context)).Do(edit.Doc);
                    placed++;
                }

        AuditLog.Add($"Stood in for {placed} unresolved item(s) on \"{shipName}\": "
                     + string.Join(", ", choices.Select(kv => $"{kv.Key} → {kv.Value.DefName}")));
        return true;
    }

    /// <summary>
    /// Open the export wizard with the update destination preselected. The menu item survives because people have
    /// muscle memory for it; it is now one of three ways into the same wizard rather than its own flow.
    ///
    /// <para>A design with no source save is not turned away here. The destination asks which ship to replace, which
    /// is what lets a stock template or a design drawn from scratch be moved onto a live ship.</para>
    /// </summary>
    private void OnUpdateSaveClick(object sender, RoutedEventArgs e)
    {
        if (_env is null) return;

        var saves = SaveImport.ListSaves(_env);
        if (saves.Count == 0)
        {
            Dlg.Show(this, "No save games found in your Ostranauts Saves folder.",
                "Update ship in save", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Ask which ship BEFORE the wizard exists, so backing out of the picker cancels the action outright.
        // Asked from inside the wizard instead, a cancel could only block a step of a window already on screen.
        SaveSourceRef? target = null;
        if ((_doc?.SourceSave ?? _saveContext?.Source) is null)
        {
            if (UpdateDriver.PickTarget(this, saves, _doc?.Kind ?? DocumentKind.Ship) is not { } picked) return;
            target = new SaveSourceRef(picked.Save.Name, picked.RegId);
        }

        OpenExportWizard(ExportDestination.UpdateShipInSave, target);
    }

    /// <summary>The stern gate before editing a ship the player doesn't own (a station or another vessel).</summary>
    private bool ConfirmUnsupportedShip(SaveShipChoice c) =>
        Dlg.Confirm(this, DlgKind.Danger, "This isn't your ship",
            $"{c.Name} ({c.RegId}) is a station or another vessel, not one of your ships.\n\n" +
            "Editing something you don't own is not supported, and it can corrupt or break your save.\n" +
            "Ostraplan can't guarantee a valid result, and takes no responsibility for the outcome. You do.",
            "Edit it anyway");

    /// <summary>Browse core+mod ship templates and import the chosen one as a fresh design.</summary>
    private async void ImportTemplate(DocumentKind kind)
    {
        if (_catalog is null || _index is null) return;

        var residence = kind == DocumentKind.Residence;
        var noun = residence ? "apartment" : "ship";
        var ships = TemplateImport.ListShipFiles(_index, kind);
        if (ships.Count == 0)
        {
            Dlg.Show(this, $"No {noun} templates found in the game data or your mods.", "Import",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var browser = new TemplateBrowserDialog(ships, kind) { Owner = this };
        if (browser.ShowDialog() != true || browser.Selected is not { } entry) return;

        if (AskImportOptions($"Import the {noun} template “{entry.Name}”?",
                residence
                    ? "A template is one of the station residences a Real Estate broker sells. It arrives as a "
                      + "pristine editable design with no in-game identity, wear or damage, and is not tied to any "
                      + "station until you deliver it into a save."
                    : "A template is a stock or modded ship. It arrives as a pristine editable design with no "
                      + "in-game identity, wear or damage.") is not { } options)
            return;

        var (catalog, path) = (_catalog, entry.Path);
        ImportResult result;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            result = await Ui.OffThread(() => TemplateImport.LoadFile(path, catalog, options));
        }
        catch (Exception ex)
        {
            Dlg.Show(this, "Import failed:\n\n" + ex.Message, "Import", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        InstallImportedDocument(result, options: options);
        AuditLog.Add($"Imported {noun} template \"{result.ShipName}\".");
    }

    /// <summary>Swap an imported ship in as the active document (no file path — Save prompts Save As). The
    /// optional context is retained when the ship was imported FOR EDITING, enabling write-back to the save.
    ///
    /// <para>A ship imported for editing brings its in-game identity with it, because that path can write the
    /// identity back: opening Ship Info on blanks would invite typing over an identity the ship already has,
    /// with no way to see what it was.</para></summary>
    private void InstallImportedDocument(ImportResult result, SaveShipContext? context = null, ImportOptions? options = null)
    {
        BeginDocumentInNewTab();
        CloseReports(_active);
        AttachDoc(result.Doc);
        result.Doc.FilePath = null;
        _meta = new OplanMeta { Name = result.ShipName };
        if (context is not null) SetMetaIdentity(SaveEdit.ReadIdentity(context));
        _stateDirty = false;
        _saveContext = context;
        _unresolvedParts = [];   // a fresh import is a complete, saveable design (unlike a reopened .oplan missing its mods)
        _stack.Reset();
        Board.SetDocument(result.Doc);
        Board.SetViewRot(0);
        Board.FitContentWhenReady();   // a tab just built has no layout yet; frame it as soon as it has one
        OnDocChanged();
        UpdateInspector();
        ReportImport(result, options, keptContents: context is not null, skippedHandled: context is not null);
    }

    /// <summary>
    /// Ask what to bring in besides the ship's structure, seeded from the remembered choice and saving it again.
    /// Null when the user backed out. Not used by "your ship, for editing", which always brings everything
    /// (see <see cref="ImportOptionsDialog"/>).
    /// </summary>
    private ImportOptions? AskImportOptions(string heading, string note)
    {
        var initial = new ImportOptions(_settings.ImportContainerContents, _settings.ImportLooseItems);
        var dlg = new ImportOptionsDialog(heading, note, initial) { Owner = this };
        if (dlg.ShowDialog() != true) return null;

        var chosen = dlg.Options;
        _settings.ImportContainerContents = chosen.ContainerContents;
        _settings.ImportLooseItems = chosen.LooseItems;
        _settings.Save();
        return chosen;
    }

    /// <summary>Tell the user what the import brought in and what it left behind. Silent on a clean import.
    /// <paramref name="keptContents"/> is true for a save import FOR EDITING, where contents are always kept, stay
    /// linked to the save rather than becoming the design's own, and nothing is ever lost — whatever isn't shown
    /// (crew and what they carry) survives the write-back untouched, so that path never says "left behind".
    /// <paramref name="options"/> is what the user chose at the dialog (null means everything), so the advice can
    /// distinguish "turn the checkbox on" from "the checkbox was on and these still couldn't come".</summary>
    private void ReportImport(ImportResult result, ImportOptions? options, bool keptContents, bool skippedHandled = false)
    {
        var opts = options ?? ImportOptions.Everything;
        var notes = new List<string>();
        if (result.ContainedKept > 0)
            notes.Add($"{result.ContainedKept} contained item(s) came in as container contents.\n" +
                      "Right-click a container and choose \"View contents\" to see them. They aren't placed on the "
                      + "grid as buildable structure."
                      + (keptContents ? "" : "\nThey belong to the design now, and travel with it through Export."));
        if (keptContents)
        {
            if (result.ContainedDropped > 0)
                notes.Add($"{result.ContainedDropped} item(s) aren't shown as cargo — most are carried by crew.\n" +
                          "They stay in the save untouched, and \"Update Ship in Save\" preserves them.");
        }
        else
        {
            var fetchable = Math.Max(0, result.ContainedDropped - result.CrewDropped - result.DeckDropped);
            if (fetchable > 0)
                notes.Add($"{fetchable} contained item(s) were left behind (cargo, tools, installed modules).\n" +
                          (opts.ContainerContents
                              ? "Their containers couldn't be imported (see the missing parts below)."
                              : "Turn on \"Container contents\" at import to bring them in."));
            if (result.DeckDropped > 0)
                notes.Add($"{result.DeckDropped} item(s) inside containers lying on the deck were left behind.\n" +
                          "A deck container imports without its contents.");
            if (result.CrewDropped > 0)
                notes.Add($"{result.CrewDropped} item(s) carried by crew were left behind. Crew are never imported.");
        }
        if (result.LooseKept > 0)
            notes.Add($"{result.LooseKept} item(s) lying on the deck came in as loose objects.\n" +
                      "They render and travel with the ship, and take no part in the placement law.");
        if (result.LooseDropped > 0)
            notes.Add($"{result.LooseDropped} item(s) lying on the deck were left behind.\n" +
                      "Turn on \"Items lying on the deck\" at import to bring them in.");
        if (result.SystemDropped > 0)
            notes.Add($"{result.SystemDropped} loot spawner and system object(s) were dropped.\nThey populate the ship at runtime, and aren't buildable structure.");
        if (result.NavConsolesStocked > 0)
            notes.Add($"{result.NavConsolesStocked} nav console(s) came in empty and were fitted with the standard " +
                      $"module set ({result.NavModulesInstalled} module(s) in all).\n" +
                      "A console is only a frame: its screens are separate modules held inside it, and a ship from "
                      + "before 1.0 has none at all. Right-click the console and choose \"View contents\" to change "
                      + "what it carries."
                      + (result.NavModulesTrayed > 0
                          ? $"\n{result.NavModulesTrayed} of them are aboard but not on the screen: the stock layout "
                            + "leaves no room. In game, open the console's edit menu and drag one in when the trip "
                            + "calls for it."
                          : ""));
        // skippedHandled: the save-edit path already ran the missing-mods stand-in prompt, which says all of this
        // and more — don't follow it with a second, weaker dialog about the same defs.
        var reportSkipped = result.Skipped.Count > 0 && !skippedHandled;
        if (reportSkipped)
        {
            var names = string.Join("\n", result.Skipped.Take(12).Select(s => s.Count > 1 ? $"   • {s.DefName} (x{s.Count})" : $"   • {s.DefName}"));
            var more = result.Skipped.Count > 12 ? $"\n   …and {result.Skipped.Count - 12} more" : "";
            notes.Add($"{result.Skipped.Sum(s => s.Count)} tile(s) referenced {result.Skipped.Count} def(s) that aren't in your loaded data, and were skipped.\n\n{names}{more}\n\n" +
                      "Enable the mods this ship needs, and import again for a complete layout.");
        }
        if (notes.Count == 0) return;   // clean import, the ship now on the canvas is feedback enough
        var report = $"Imported {result.ShipName}, {result.PartCount} parts.\n\n" + string.Join("\n\n", notes);
        if (reportSkipped) Dlg.Warn(this, "Import", report);
        else Dlg.Info(this, "Import", report);
    }

    private void OnUndoClick(object sender, RoutedEventArgs e)
    {
        if (_doc is not null) _stack.Undo(_doc);
    }

    private void OnRedoClick(object sender, RoutedEventArgs e)
    {
        if (_doc is not null) _stack.Redo(_doc);
    }

    /// <summary>The Help ▾ dropdown: controls/keybinds, report a bug, and the on-disk activity log.</summary>
    private void OnHelpMenuClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = BtnHelp, Placement = PlacementMode.Bottom };
        void Add(string header, Action act)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => act();
            menu.Items.Add(item);
        }
        Add("Controls & keybinds (F1)", ShowHelp);
        Add("View Changelog", ViewChangelog);
        menu.Items.Add(new Separator());
        Add("Report a Bug…", ReportBug);
        menu.Items.Add(new Separator());
        Add("View Activity Log", ViewLogs);
        Add("Open Log Folder", OpenLogFolder);
        Add("Clear Activity Log…", ClearLogs);
        menu.IsOpen = true;
    }

    // ---- report a bug ----

    private const int MaxIssueUrl = 7000;   // GitHub won't accept issue URLs much beyond this

    /// <summary>
    /// Open a pre-filled GitHub issue for Ostraplan in the browser (a short template plus a diagnostics header),
    /// and — because a GitHub issue URL is capped near <see cref="MaxIssueUrl"/> chars — write the <b>full</b>
    /// diagnostics to a scrubbed file the user drags into the issue to attach. That file carries this session's
    /// entire activity trail, the tail of the crash log (<c>error.log</c>), and any catalog load warnings; a
    /// best-effort slice of the recent trail is still folded inline so the report is useful even un-attached.
    /// </summary>
    private void ReportBug()
    {
        try
        {
            var prompt =
                "# Ostraplan bug report\n\n" +
                "## What were you trying to do?\n\n\n" +
                "## What went wrong?\n\n\n" +
                "## Exact steps to reproduce (so I can see it happen too)\n\n1. \n2. \n3. \n\n" +
                "**Screenshots**\nDrag any screenshots in here.\n\n" +
                "---\n" +
                "*Diagnostics (please keep these — they help me reproduce it):*\n" +
                DiagnosticsHeader();

            var reportPath = WriteDiagnosticsFile();   // the complete, unabridged record for attachment

            var head = prompt;
            if (reportPath is not null)
                head += $"\n> A full diagnostics file (`{Path.GetFileName(reportPath)}`) was generated — " +
                        "please **drag it into this issue** to attach it.\n";

            OpenUrl(IssueUrl(head + InlineTrailWithinUrlBudget(head)));

            if (reportPath is not null)
            {
                RevealInExplorer(reportPath);
                AuditLog.Add($"Opened a pre-filled GitHub bug report; wrote diagnostics to {Path.GetFileName(reportPath)}.");
                Dlg.Info(this, "Report a bug",
                    "A GitHub issue has opened in your browser, and a diagnostics file was created and shown in Explorer.\n\n" +
                    $"Please drag \"{Path.GetFileName(reportPath)}\" into the issue to attach it — it holds your full activity " +
                    "trail and any recent errors (with your account name and paths scrubbed), which makes the bug far easier to trace.");
            }
            else
            {
                AuditLog.Add("Opened a pre-filled GitHub bug report (diagnostics file could not be written).");
            }
        }
        catch (Exception ex)
        {
            Dlg.Error(this, "Report a bug", ex.Message);
        }
    }

    private static string IssueUrl(string body) =>
        "https://github.com/Valtora/Ostraplan/issues/new?labels=bug"
        + "&title=" + Uri.EscapeDataString("[Bug] ")
        + "&body=" + Uri.EscapeDataString(body);

    /// <summary>The diagnostics header block (version, OS, game, design summary, mod-override state), shared by
    /// the inline issue body and the attached diagnostics file.</summary>
    private string DiagnosticsHeader() =>
        $"- Ostraplan: v{AppVersion}\n" +
        $"- OS: {DescribeOs()}\n" +
        $"- Game: {_env?.InstalledVersion ?? "unknown"}\n" +
        $"- Design: {DescribeDocument()}\n" +
        $"- Mod overrides: {(Board.AllowModdedOverrides ? "on" : "off")}\n";

    /// <summary>Ostraplan's crash log (unhandled-exception stack traces), beside the activity log.</summary>
    private static string ErrorLogPath => Path.Combine(AuditLog.Dir, "error.log");

    /// <summary>
    /// Compose the full diagnostics report — environment, the tail of the crash log, catalog load warnings, and
    /// this session's <b>entire</b> activity trail — and write it (scrubbed) to
    /// <c>%APPDATA%\Ostraplan\reports\Ostraplan-diagnostics-&lt;timestamp&gt;.md</c>. Returns the path, or null if it
    /// couldn't be written (a report must still open without it).
    /// </summary>
    private string? WriteDiagnosticsFile()
    {
        try
        {
            static string Section(string title, IReadOnlyList<string> lines, string empty) =>
                lines.Count == 0
                    ? $"## {title}\n\n{empty}\n\n"
                    : $"## {title}\n\n```\n{string.Join("\n", lines)}\n```\n\n";

            var errors = LogTail.LastLines(ErrorLogPath, 200);
            // A bug report gets ALL of them, core included: unfixable for the user is not the same as uninteresting
            // to me, and a core defect is often exactly what explains the behaviour being reported.
            var warnings = (_index?.Warnings ?? []).Concat(_catalog?.Warnings ?? [])
                                                   .Select(w => w.ToString()).ToList();
            var repaired = _index?.Repaired ?? [];   // loaded fine, but only after mending — see DataIndex.Parse
            var trail = AuditLog.SessionTrail();

            var content =
                "# Ostraplan diagnostics\n\n" +
                "*Generated by Help ▸ Report a Bug. Your Windows account name and file paths are scrubbed.*\n\n" +
                "## Environment\n\n" + DiagnosticsHeader() + "\n" +
                Section("Recent errors (error.log)", errors, "_None recorded this session or last._") +
                (warnings.Count > 0 ? Section("Catalog load warnings", warnings.Take(200).ToList(), "") : "") +
                (repaired.Count > 0 ? Section("Data files mended on load", repaired.Take(200).ToList(), "") : "") +
                Section("Activity trail (this session, most-recent-last)", trail, "_Nothing logged yet._");

            var dir = Path.Combine(AuditLog.Dir, "reports");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"Ostraplan-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.md");
            File.WriteAllText(path, content);
            return path;
        }
        catch { return null; }
    }

    /// <summary>The largest most-recent-last slice of this session's activity trail whose resulting issue URL
    /// still fits GitHub's limit, as a collapsible block — or "" when even one line won't fit. The full trail is
    /// always in the attached file; this is the best-effort inline copy.</summary>
    private string InlineTrailWithinUrlBudget(string head)
    {
        var recent = AuditLog.Recent(200);
        if (recent.Count == 0) return "";
        for (var take = recent.Count; take > 0; take -= Math.Max(1, take / 8))
        {
            var block = "\n<details>\n<summary>Recent actions (from Ostraplan's activity log)</summary>\n\n```\n"
                        + string.Join("\n", recent.Skip(recent.Count - take)) + "\n```\n</details>\n";
            if (IssueUrl(head + block).Length <= MaxIssueUrl) return block;
        }
        return "";
    }

    /// <summary>Open Explorer with the file pre-selected, so the user can drag it straight into the GitHub issue.</summary>
    private static void RevealInExplorer(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch { /* revealing is a convenience; never fail the report over it */ }
    }

    /// <summary>Resolve a def name to its friendly name for the activity trail (null → the raw def is used).</summary>
    private string? DefFriendlyName(string defName) => _catalog?.Lookup(defName)?.Friendly;

    /// <summary>A one-line, path-free summary of the design on screen for the bug report's diagnostics, with a count
    /// of the others open beside it: how many tabs were up is often the difference between a report that reproduces
    /// and one that does not.</summary>
    private string DescribeDocument()
    {
        if (_doc is null) return "none";
        var kind = (_doc.SourceSave ?? _saveContext?.Source) is not null ? "save-bound"
            : _doc.FilePath is not null ? ".oplan" : "unsaved";
        var dirty = _stack.Dirty ? ", unsaved changes" : "";
        var incomplete = _unresolvedParts.Count > 0 ? $", {_unresolvedParts.Count} missing-mod part(s)" : "";
        var others = _sessions.Count > 1 ? $" (+{_sessions.Count - 1} more open)" : "";
        return $"{_doc.Placements.Count} parts, {kind}{dirty}{incomplete}{others}";
    }

    /// <summary>
    /// A human-readable OS string that tells Windows 11 from 10 (both report 10.0.x via
    /// <see cref="Environment.OSVersion"/> — 11 is build 22000+), with the edition and display
    /// version pulled from the registry when available. Ported from Ostrasort.
    /// </summary>
    private static string DescribeOs()
    {
        var v = Environment.OSVersion.Version;
        var name = v.Major == 10 && v.Build >= 22000 ? "Windows 11" : $"Windows {v.Major}";
        string? edition = null, display = null;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            display = key?.GetValue("DisplayVersion") as string;   // e.g. "24H2"
            if (key?.GetValue("ProductName") as string is { } product)
            {
                // ProductName often still says "Windows 10 <edition>" on 11 — trust only the edition suffix.
                var m = System.Text.RegularExpressions.Regex.Match(product, @"Windows\s+\d+\s+(.+)$");
                if (m.Success) edition = m.Groups[1].Value.Trim();
            }
        }
        catch { /* registry unavailable — fall back to the name/version */ }

        var s = name;
        if (edition is { Length: > 0 }) s += " " + edition;
        s += $" ({v.Major}.{v.Minor}.{v.Build}";
        s += display is { Length: > 0 } ? $", {display})" : ")";
        return s;
    }

    // ---- activity log ----

    /// <summary>Open the on-disk activity log in the default text editor.</summary>
    private void ViewLogs()
    {
        if (!File.Exists(AuditLog.FilePath))
        {
            Dlg.Info(this, "Activity log", "Nothing has been logged yet.");
            return;
        }
        try { OpenUrl(AuditLog.FilePath); }
        catch (Exception ex) { Dlg.Error(this, "Activity log", ex.Message); }
    }

    /// <summary>Open the folder holding the activity log (and settings) in Explorer.</summary>
    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(AuditLog.Dir);
            OpenUrl(AuditLog.Dir);
        }
        catch (Exception ex) { Dlg.Error(this, "Activity log", ex.Message); }
    }

    /// <summary>Empty the on-disk activity log, behind a confirmation.</summary>
    private void ClearLogs()
    {
        if (!Dlg.Confirm(this, DlgKind.Warning, "Clear the activity log?",
                "This empties Ostraplan's on-disk activity log (audit.log) and can't be undone.\n\n" +
                "The log records your actions so a problem can be diagnosed later — keep it if you might report a bug.",
                "Clear log"))
            return;
        AuditLog.Clear();
        Dlg.Info(this, "Activity log", "The activity log has been cleared.");
    }

    // ---- settings ----

    /// <summary>Open Settings (one at a time — a second click brings the open one forward). Modeless, because
    /// UI scale is a thing you judge against the app behind it, not a value you commit blind.</summary>
    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_settingsDialog is { } open) { open.Activate(); return; }

        var dlg = new SettingsDialog(_settings, _env, new SettingsHooks(
            SetTheme, SetUiScale, SetModOverrides, SetGameRoot, SetSavesDir))
        {
            Owner = this,
        };
        dlg.Closed += (_, _) => _settingsDialog = null;
        _settingsDialog = dlg;
        dlg.Show();
    }

    /// <summary>Theme: apply and persist. DynamicResource + Fluent ThemeMode retint the chrome live.</summary>
    private void SetTheme(string mode)
    {
        _settings.Theme = mode;
        AuditLog.Setting("Theme", mode);
        _settings.Save();
        ThemeManager.Apply(mode);
    }

    /// <summary>UI scale: apply to every open window and persist. See <see cref="UiScale"/>.</summary>
    private void SetUiScale(double scale)
    {
        _settings.UiScale = UiScaling.Clamp(scale);
        AuditLog.Setting("UI scale", UiScaling.Percent(_settings.UiScale));
        _settings.Save();
        UiScale.Apply(_settings.UiScale);
    }

    /// <summary>Whether modded parts may be placed where Ostraplan's core-game placement law says they don't fit
    /// (persisted). Core parts stay hard-blocked; overridden modded parts are placed and flagged as warnings.</summary>
    private void SetModOverrides(bool on)
    {
        Board.AllowModdedOverrides = on;
        _settings.AllowModdedOverrides = on;
        _settings.Save();
        Board.InvalidateVisual();   // refresh the armed ghost (green/amber/red) under the new rule
        AuditLog.Add(on
            ? "Modded overrides enabled — modded parts may break the placement law (flagged)."
            : "Modded overrides disabled — modded parts are enforced like core.");
    }

    /// <summary>The Ostranauts install folder (null = auto-detect). Persisted now, read at the next launch: the
    /// data index, catalog and sprite cache are all built from it during startup.</summary>
    private void SetGameRoot(string? path)
    {
        _settings.GameRootOverride = path;
        AuditLog.Setting("Game folder", path ?? "automatic");
        _settings.Save();
        // Deliberately not applied to the running session: the catalog, sprite cache and mod load order in
        // memory all came from the folder this session started on, and swapping the root out from under them
        // would leave the app half on each. The dialog says it takes a restart.
    }

    /// <summary>The Saves folder (null = follow the game's own setting, then the default). Takes effect at once:
    /// saves are listed on demand, so nothing loaded at startup depends on it.</summary>
    private void SetSavesDir(string? path)
    {
        _settings.SavesDirOverride = path;
        AuditLog.Setting("Saves folder", path ?? "automatic");
        _settings.Save();
        RelocateEnvironment();
    }

    /// <summary>Rebuild <see cref="_env"/> so a new Saves folder is live, <b>pinned to the install this session
    /// already loaded</b> (see <see cref="SetGameRoot"/>), and tell an open Settings window what resolved. A
    /// failure keeps the environment the app is already running on: the game data is loaded and usable, and
    /// refusing to update a path is better than losing it.</summary>
    private void RelocateEnvironment()
    {
        try
        {
            _env = GameEnv.Locate(_env?.GameRoot ?? _settings.GameRootOverride, _settings.SavesDirOverride);
        }
        catch (DirectoryNotFoundException ex)
        {
            AuditLog.Add($"Folder setting not applied to the running session: {ex.Message}");
        }
        _settingsDialog?.EnvironmentChanged(_env);
    }

    // ---- update check (Velopack) ----

    /// <summary>This build's version, from the assembly's informational version (git hash stripped).</summary>
    private static string AppVersion =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? "0.0";

    private bool _updateCheckBusy;   // rapid manual clicks must not stack result dialogs

    /// <summary>
    /// Velopack self-update. Runs on every launch and from the Help window's "Check for updates"
    /// button. When a newer release exists it is downloaded in the background and the toolbar button
    /// flips to "Restart to update" - the swap only happens when the user clicks it (see
    /// <see cref="PromptRestartAndApply"/>), so unsaved edits are never discarded. A launch check never
    /// pops a modal; a manual check offers the restart right away and reports when already up to date.
    /// Managed copies only: a dev / portable-unmanaged build (where <see cref="_updater"/> is null) never
    /// shows the affordance, and every network failure stays quiet unless the user asked.
    /// </summary>
    private async Task CheckForUpdateAsync(bool manual = false)
    {
        if (_updater is null)
        {
            if (manual)
                Dlg.Info(this, "Ostraplan",
                    "Automatic updates are delivered to the installed and portable releases.\n\n" +
                    "This copy isn't managed by the installer, so it won't update itself.\n" +
                    "Download the latest release from GitHub to get the installer.");
            return;
        }
        if (_updateCheckBusy) return;
        _updateCheckBusy = true;
        try
        {
            if (_pendingUpdate is not null)   // already downloaded earlier this session
            {
                if (manual) PromptRestartAndApply();
                return;
            }

            Velopack.UpdateInfo? info;
            try
            {
                info = await _updater.CheckAndDownloadAsync();
            }
            catch (Exception ex)
            {
                AuditLog.Add($"Update check failed: {ex.Message}");
                if (manual)
                    Dlg.Warn(this, "Ostraplan", "Couldn't check for updates.\n\n" + ex.Message +
                        "\n\nYou may be offline, or GitHub may be rate limiting.\n" +
                        "Its anonymous API allows about 60 checks an hour per network.");
                return;
            }

            if (info is null)
            {
                if (manual) Dlg.Info(this, "Ostraplan", $"You're on the latest version (v{AppVersion}).");
                return;
            }

            // Downloaded and ready. Surface the restart affordance; a launch check leaves it at that
            // (no modal), a manual check offers the restart now.
            _pendingUpdate = info;
            var ver = VeloUpdate.VersionOf(info);
            BtnUpdate.Content = $"⬆  Restart to update to v{ver}";
            BtnUpdate.ToolTip = $"Ostraplan v{ver} has been downloaded. Click to restart and finish updating.";
            BtnUpdate.Visibility = Visibility.Visible;
            AuditLog.Add($"Update v{ver} downloaded and ready (you are on v{AppVersion}). Restart to apply.");
            if (manual) PromptRestartAndApply();
        }
        finally { _updateCheckBusy = false; }
    }

    /// <summary>
    /// Confirms, then applies the downloaded update and restarts (does not return on success).
    ///
    /// <para>Velopack ends the process itself, so <see cref="Window.Closing"/> never runs — and with it neither
    /// the unsaved-changes prompt that guards every other way out nor the settings write. Both therefore happen
    /// here, by hand, exactly as that handler does them. v0.68.3 shipped without this: clicking the update button
    /// with edits on the canvas threw them away without asking. Cancelling the save prompt cancels the restart,
    /// so the answer "actually, not now" still means what it says.</para>
    /// </summary>
    private void PromptRestartAndApply()
    {
        if (_updater is null || _pendingUpdate is null) return;
        var ver = VeloUpdate.VersionOf(_pendingUpdate);
        var dirty = _sessions.Any(s => s.Dirty);
        if (!Dlg.Confirm(this, DlgKind.Info, "Restart to finish updating",
                $"Ostraplan v{ver} has been downloaded.\n\nOstraplan will close, apply the update, and reopen." +
                (dirty ? "\n\nYou'll be asked about your unsaved changes first." : ""),
                "Restart now", "Later"))
            return;
        if (!ConfirmDiscardEverything()) return;   // Cancel there cancels the restart, not just the save
        _settings.Save();
        try
        {
            AuditLog.Add($"Applying update v{ver} and restarting.");
            _updater.ApplyAndRestart(_pendingUpdate);   // closes this process and relaunches into the new build
        }
        catch (Exception ex)
        {
            AuditLog.Add($"Update failed to apply: {ex.Message}");
            Dlg.Error(this, "Update failed", "Ostraplan couldn't apply the update:\n\n" + ex.Message +
                "\n\nYou can keep using this version, or download the latest release manually.");
        }
    }

    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private void OnUpdateClick(object sender, RoutedEventArgs e) => PromptRestartAndApply();

    // ---- help ----

    private void ShowHelp()
    {
        (string Func, string Keys, string Note)[] rows =
        [
            ("Place / paint", "LMB", "With a part armed: place it; keep dragging to paint along the cursor."),
            ("Box fill", "Shift + drag", "With a part armed: rubber-band a box and fill it with the part."),
            ("Hollow box", "Ctrl + Shift + drag", "With a part armed: place only the outline — walls, in practice."),
            ("Select", "LMB", "Select a part. Ctrl+click adds/removes; drag empty space to box-select."),
            ("Filter box-select", "Shift + drag", "With nothing armed: box-select even when starting on a part, then filter chips let you keep only some layers (e.g. the walls without the floors)."),
            ("Flood-select", "Double-click", "On a part: select every touching tile of the same type (bulk delete or re-skin). Ctrl+double-click adds the region."),
            ("Fill a compartment", "Double-click empty space, then Enter", "Double-click enclosed (sealed) empty space to highlight the whole compartment, then arm a part and press Enter to fill it (Esc to cancel). Areas open to space can't be selected, so a fill never leaks."),
            ("Use as brush", "Alt + click", "Eyedropper: arm the part under the cursor, at its own rotation, so you can keep painting it. Also on the right-click menu."),
            ("Replace with…", "Ctrl+R", "Swap the selection for a compatible part (same layer + footprint) via a picker. Also on the right-click menu."),
            ("Move", "Drag selection", "Move the selected parts."),
            ("Step down a stack", "`", "Select the next thing down the pile under the cursor, wrapping at the bottom — the quick way to reach a part drawn underneath another without going through the right-click list. Loose items are in the pile too."),
            ("Re-stack", "Ctrl+[ / Ctrl+]", "Move the selected part or loose item one step back / forward through the pile sharing its tile, when the automatic draw order isn't what you want. Reset order (right-click) hands that pile back to it. Both stay inside the render layer, so nothing lands under a deck plate or over a conduit run, and the choice is saved with the design."),
            ("Context menu", "RMB", "Use as brush · Replace with… · Find and Replace All… · Make Loose Item / Install item · Repair · Move Back / Move Forward / Reset order · pick a buried layer on stacked tiles · Select only (after a box-select) · Close/Open door. Also cancels placement while armed."),
            ("Rotate part", "R / Shift+R", "CW / CCW — the armed part, a selected part in place, or a whole selection about its centre (walls & floors auto-tile rather than turn). The brush keeps its angle when you arm another part; the ghost draws a needle towards its leading edge and the status bar reads out the angle."),
            ("Flip selection", "H / Shift+H", "Mirror the selection about its centre — H horizontal (left↔right), Shift+H vertical (up↔down); each part reflects and snaps to a real rotation."),
            ("Symmetry", "M", "Cycle Off → Vertical → Horizontal → Both; axes centre on the hovered tile when switching on. While on, it also drives editing: selecting a part grabs its mirror partner(s), and moving, rotating, or deleting the group keeps it symmetric (the far side tracks in the mirrored direction)."),
            ("Mod overrides", "Settings", "Let modded parts place where the core-game rules say they don't fit (ghost turns amber, flagged as a warning — verify in-game). Core parts stay enforced."),
            ("Power overlay", "P", "Show/hide PowerViz: lit conduit runs flow from a live generator/battery, orphaned runs are dim red, and a wired device with no feed gets an amber marker. A powered part also shows its connector badges (blue IN, green OUT) while armed or selected."),
            ("Rooms overlay", "C", "Show/hide RoomViz: every compartment the game would flood-fill, tinted in its own colour and labelled with what it certifies as, its size and its value. A room that certifies as nothing says why — what to add, and which item in it blocks the spec (a canister parked in a quarters, say). Unsealed compartments are red. The exterior isn't tinted, so a room open to space simply loses its tint."),
            ("Light overlay", "L", "Show/hide Light Viz: interior lighting simulated from every fixture and lit device. Each light floods its compartment (bounded by walls) in its own colour, so dark corners and colour clashes show at a glance. The View menu's Light Viz sliders set the light brightness and how far unlit areas darken (from a glow over the full-bright ship up to the in-game dark look)."),
            ("Walk overlay", "K", "Show/hide WalkViz: every tile crew can stand on, tinted by which connected zone it belongs to — two tiles sharing a colour are reachable from each other on foot, two colours mean no route. Fittings nobody can operate are ringed in red at the spot they'd have to stand, and a doorway with vacuum on one side is dashed amber (crossable, but only in a suit). Note a closed door only seals if it is unpowered, locked or damaged; a powered one crew simply open. The View menu can count spacewalks and choose whether Forbid zones apply."),
            ("Surfaces mode", "T", "Treat the deck as a canvas: everything outside the focused layer is ghosted and steps out of the way of clicks, so the floor under a bed is one click away, and a 1×1 wall/floor brush re-skins whatever is already on a tile instead of refusing to land on it. Paint, box-fill (Shift+drag), outline (Ctrl at release) and the compartment fill on a bare room all work as they always did — they just re-skin whatever they land on now. In the Surfaces bar: a second brush and a checkerboard or stripe pattern; SHOW picks the focused layer (Both / Floors / Walls — Floors ghosts the walls too, which is how you reach the floors under them); PAINT picks what a stroke may do (Replace only, the default, so a stroke never spills new deck past a room's edge; Both; or Fill only). View ▸ Surfaces sets how visible the ghosted layers stay. Light Viz switches off while it is on, because a lit composite has no layers left to ghost."),
            ("Wire mode", "Toolbar toggle", "Wire signalable devices: click a device to arm it as the signal source, then click another to connect (or a connected one to disconnect). Connectable devices ring violet, wires draw source→target. Esc / right-click cancels."),
            ("Delete", "Del", "Delete the selection."),
            ("Select all", "Ctrl+A", "Select every part in the design."),
            ("Copy / paste / duplicate", "Ctrl+C / V / D", "Copy · paste at the cursor · duplicate the selection."),
            ("Cancel", "Esc", "Cancel placement, then clear the selection."),
            ("Pan", "W A S D", "Pan the view (smooth while held)."),
            ("Pan (mouse)", "MMB / Space + drag", "Pan the view by dragging."),
            ("Rotate view", "Q / E", "Rotate the plan view CCW / CW, like the in-game camera."),
            ("Zoom", "Mouse wheel / + −", "Wheel zooms at the cursor in fine 0.1× steps (hold Shift for 0.5×); + and − zoom at the view centre."),
            ("Fit to ship", "F", "Fit the view to the whole ship."),
            ("Undo / redo", "Ctrl+Z / Ctrl+Y", "Undo · redo (Ctrl+Shift+Z also redoes)."),
            ("New / open / save", "Ctrl+N / O / S", "New · open · save (Ctrl+Shift+S = Save As). New and Open each start their design in a tab of its own, so nothing you have open is closed to make room."),
            ("Switch / close design", "Ctrl+Tab / Ctrl+W", "Step through the open designs (Ctrl+Shift+Tab goes back) · close the one on screen. The tab strip appears above the canvas as soon as a second design is open; copy and paste work between them."),
            ("Export", "Ctrl+E", "Export the design as a spawnable local data mod."),
            ("Ship Info / Materials", "Ctrl+I / Ctrl+B", "Edit the in-game identity · open the bill of materials."),
            ("Settings", "Ctrl+,", "Theme, UI scale (magnify the whole app for a high-resolution monitor), mod overrides, and the Ostranauts install and Saves folders."),
            ("Diagnostics", "Toolbar", "The game's own ship checklist, from the nav console's Diagnostics module: transponder, antenna, nav station, reactor and its helium-3 and deuterium, RCS thrusters, distributor and reaction mass, backup power, and the four life-support rows — each green or red on the game's own thresholds, with what's missing spelled out under every red one. Backup power is measured at the console's power inputs and O2 stores at the pumps' gas inputs, exactly as the game measures them, so a battery your conduits never reach counts for nothing."),
            ("Help", "F1", "Open this window."),
        ];

        var grid = new Grid { Margin = new Thickness(18) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                   // Function
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                   // Keybinding
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Note

        TextBlock Cell(string text, Brush fg, int r, int c, bool wrap = false, bool bold = false, double? max = null)
        {
            var t = new TextBlock
            {
                Text = text, Foreground = fg, Margin = new Thickness(0, 4, 22, 4),
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            };
            if (wrap) t.TextWrapping = TextWrapping.Wrap;
            if (max is { } m) t.MaxWidth = m;
            Grid.SetRow(t, r);
            Grid.SetColumn(t, c);
            return t;
        }

        var row = 0;
        // column headers
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(Cell("FUNCTION", ThemeManager.Dim, row, 0, bold: true));
        grid.Children.Add(Cell("KEYBINDING", ThemeManager.Dim, row, 1, bold: true));
        grid.Children.Add(Cell("WHAT IT DOES", ThemeManager.Dim, row, 2, bold: true));
        row++;

        var zebra = new SolidColorBrush(Color.FromArgb(0x14, 0x80, 0x80, 0x80));   // faint, reads on both themes
        foreach (var (func, keys, note) in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (row % 2 == 0)   // shade alternate rows for scan-ability (behind the cells)
            {
                var band = new System.Windows.Controls.Border { Background = zebra };
                Grid.SetRow(band, row);
                Grid.SetColumnSpan(band, 3);
                grid.Children.Add(band);
            }
            grid.Children.Add(Cell(func, ThemeManager.Ink, row, 0, bold: true));
            grid.Children.Add(Cell(keys, ThemeManager.KeyAccent, row, 1, bold: true));
            grid.Children.Add(Cell(note, ThemeManager.Ink, row, 2, wrap: true, max: 460));
            row++;
        }

        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var footer = new TextBlock
        {
            Text = "The placement law is enforced: a part won't place where the game's own rules would refuse it. The ghost " +
                   "glows green when it fits and red when it can't, with the reason (e.g. \"needs a floor beneath\") in the " +
                   "status bar and the offending tiles tinted red. Moving or rotating a part into an illegal spot is allowed " +
                   "but flagged — red-tinted tiles and the PROBLEMS list name what broke. Every ship owns exactly one Primary " +
                   "Airlock, fixed at the 0,0 origin — the game neither sells nor removes it, so Ostraplan seeds it locked " +
                   "(no move/rotate/delete). Red-striped areas are out of bounds: no construction beyond an airlock's mating " +
                   "face. Wall and floor sprites connect automatically.",
            Foreground = ThemeManager.Dim,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 720,
            Margin = new Thickness(0, 14, 0, 0),
        };
        Grid.SetRow(footer, row);
        Grid.SetColumnSpan(footer, 3);
        grid.Children.Add(footer);
        row++;

        // version + manual update check
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var about = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        about.Children.Add(new TextBlock
        {
            Text = $"Ostraplan v{AppVersion}", Foreground = ThemeManager.Dim,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
        });
        var checkUpdates = new Button { Content = "Check for updates", Padding = new Thickness(12, 3, 12, 3) };
        checkUpdates.Click += (_, _) => _ = CheckForUpdateAsync(manual: true);
        about.Children.Add(checkUpdates);
        var changelog = new Button { Content = "View Changelog", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(8, 0, 0, 0) };
        changelog.Click += (_, _) => ViewChangelog();
        about.Children.Add(changelog);
        var reportBug = new Button { Content = "Report a bug", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(8, 0, 0, 0) };
        reportBug.Click += (_, _) => ReportBug();
        about.Children.Add(reportBug);
        Grid.SetRow(about, row);
        Grid.SetColumnSpan(about, 3);
        grid.Children.Add(about);

        new Window
        {
            Title = "Ostraplan — controls & keybinds",
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            Background = ThemeManager.WindowBg,
            Content = new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 680 },
        }.ShowDialog();
    }
}
