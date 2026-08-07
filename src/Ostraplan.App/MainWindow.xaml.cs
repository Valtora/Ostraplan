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
    private bool _themeInit;   // suppress the theme combo's SelectionChanged during initial sync
    // Velopack self-update. Null for a copy the installer doesn't manage (dev / dotnet-run /
    // bare exe) — the update affordance simply never appears there. A downloaded, ready-to-apply
    // update is parked in _pendingUpdate until the user clicks Restart (see CheckForUpdateAsync).
    private readonly VeloUpdate? _updater = VeloUpdate.Create();
    private Velopack.UpdateInfo? _pendingUpdate;
    private readonly CommandStack _stack = new();
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
    private ShipDocument? _doc;
    private SaveShipContext? _saveContext;   // set when a design was imported from a save FOR EDITING — enables writing it back
    private OplanMeta _meta = new();
    private bool _stateDirty;   // non-command persisted edits (ship identity, view orientation) — their unsaved state
    // Parts an opened .oplan referenced whose defs aren't in the current game + mods data. While this is
    // non-empty the design is INCOMPLETE and held read-only: saving would rewrite the file without them, and
    // building over — or moving parts into — the space where they belong can produce a ship that's invalid
    // in-game, so the chrome shows a standing warning. Two ways out, both the user's call: enable the mods
    // (verify with Ostrasort) and reopen to get the parts back, or confirm the drop on Save to let them go for
    // good — which clears this and lifts the hold. See OpenFile / GuardIncompleteSave.
    private IReadOnlyList<OplanPart> _unresolvedParts = [];
    private bool _syncingPalette;
    private IReadOnlyList<RoomSpecDef>? _roomSpecs;   // lazily loaded once for the Ship Rating analysis
    private bool _analysing;
    private FreezeGate _freeze = null!;               // raised while an off-thread read of the LIVE _doc is in flight — see FreezeDoc
    private (int X, int Y)? _hoverCell;               // last hovered tile — the paste anchor
    private List<(string Def, int X, int Y, int Rot, IReadOnlyList<CargoItem> Cargo)> _clip = [];   // copied selection, relative to its top-left (with container contents)
    private (int X, int Y) _clipOrigin;               // the copied selection's original top-left (paste fallback)
    private readonly DispatcherTimer _scanTimer;      // debounces the (now off-thread) problem scan
    private CancellationTokenSource? _scanCts;        // cancels a superseded scan
    private List<Problem> _lastProblems = [];         // the most recent scan result, re-rendered when an alert is dismissed/restored


    public MainWindow()
    {
        InitializeComponent();

        AuditLog.Session(AppVersion);   // open a new section in the on-disk activity trail

        // Reflect the saved theme in the picker (App.OnStartup already applied it). Guarded so the
        // programmatic select doesn't re-apply/persist.
        _themeInit = true;
        CmbTheme.SelectedIndex = _settings.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        _themeInit = false;

        Board.StrokeCommitted += OnStrokeCommitted;
        Board.MoveRequested += OnMoveRequested;
        Board.PosesRequested += OnPosesRequested;
        Board.SelectionChanged += UpdateInspector;
        Board.LooseSelectionChanged += UpdateInspector;
        Board.HoverChanged += cell => { _hoverCell = cell; TxtCell.Text = cell is { } c ? $"tile {c.X}, {c.Y}" : "—"; };
        Board.SelectionSizeChanged += size => TxtSel.Text = size is { } s ? $"{s.W} × {s.H} tiles" : "";
        Board.ViewChanged += UpdateZoomText;
        Board.Disarmed += ClearPaletteSelection;
        Board.ContextMenuRequested += OnContextMenuRequested;
        Board.BrushPicked += OnArmFromTile;   // Alt+LMB eyedropper
        Board.ArmedChanged += UpdateBrushText;
        Board.LooseContextMenuRequested += OnLooseContextMenuRequested;
        Board.BandFilterRequested += OnBandFilterRequested;
        Board.GhostReasonChanged += status => TxtGhost.Text =
            status is { } s ? (s.Advisory ? "⚠ places, but " : s.WillPlace ? "⚠ placing against the rules — " : "⛔ can't place here — ") + s.Reason
            : Board.AirSelection.Count > 0 ? AirHint(Board.AirSelection.Count)
            : "";
        Board.AirSelectionChanged += n => TxtGhost.Text = n > 0 ? AirHint(n) : "";
        // restore the "allow modded parts to break the law" toggle (default off)
        Board.AllowModdedOverrides = _settings.AllowModdedOverrides;
        Board.ZoneStrokeCommitted += OnZoneStrokeCommitted;
        Board.ShowZonesChanged += OnShowZonesChanged;   // refresh the toolbar toggle highlight
        Board.ShowPowerChanged += OnShowPowerChanged;   // (re)compute the overlay off-thread when toggled on
        Board.ShowRoomsChanged += OnShowRoomsChanged;   // same for the room certification
        Board.ShowLightChanged += OnShowLightChanged;   // same for the interior-lighting flood
        Board.ShowWalkChanged += OnShowWalkChanged;     // same for the crew-access analysis
        Board.WireModeChanged += OnWireModeChanged;     // swap the status hint for the wiring instructions
        SyncViewToggles();                              // seed the toolbar highlights from the initial overlay state
        Board.LinkToggleRequested += OnLinkToggleRequested;   // connect/disconnect two devices via the command stack
        Board.ActiveZoneChanged += UpdateZones;   // reflect which zone (if any) is being painted
        _stack.StateChanged += RefreshChrome;
        // Audit every edit/undo/redo, resolving each part's friendly name so the trail records what/where
        // ("Place Nav Station @(12,7)") rather than a context-free "Place" — the detail a bug report needs.
        _stack.Applied += (cmd, action) => AuditLog.Command(action, cmd, DefFriendlyName);

        // the whole editing surface goes dead while an engine reads the live document off-thread (FreezeDoc)
        _freeze = new FreezeGate(frozen => Chrome.IsEnabled = !frozen);

        _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _scanTimer.Tick += (_, _) => RunScan();

        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        Deactivated += (_, _) => Board.ClearPanKeys();   // a KeyUp we never receive must not leave the view drifting
        Loaded += async (_, _) => await LoadDataAsync();
        Closing += (_, e) =>
        {
            if (!ConfirmDiscardChanges()) e.Cancel = true;
            else _settings.Save();
        };
    }

    // ---- startup ----

    private async Task LoadDataAsync()
    {
        while (_env is null)
        {
            try
            {
                _env = GameEnv.Locate(_settings.GameRootOverride);
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
        Board.Sprites = sprites;

        BuildPalette();
        NewDocument();

        var v = env.InstalledVersion ?? "unknown";
        AuditLog.Add($"Loaded game data (Game {v}).");
        TxtVersion.Text = $"Game {v}";

        var warnings = index.Warnings.Concat(catalog.Warnings).ToList();
        if (warnings.Count > 0)
        {
            TxtWarnings.Text = $"{warnings.Count} data warnings";
            TxtWarnings.ToolTip = string.Join("\n", warnings.Take(40));
        }

        UpdateZoomText();
        LoadingOverlay.Visibility = Visibility.Collapsed;

        _ = CheckForUpdateAsync();   // quiet check against the latest GitHub release
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

    private void BuildPalette()
    {
        Tabs.Items.Clear();
        _paletteLists.Clear();

        // ★ Favorites / Recent, always the first tab (see BuildQuickTab).
        Tabs.Items.Add(BuildQuickTab());

        foreach (var category in new[] { "All" }.Concat(Catalog.Categories).Append(ItemsCategory))
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
            // Items tab, so it doesn't drown the structure parts.
            list.ItemsSource = _allParts
                .Where(vm => (category is null ? vm.Part.Category != ItemsCategory : vm.Part.Category == category) && vm.Matches(search))
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
    /// it and syncs the highlight; if it is filtered out by the search, arm directly. Non-buildable parts (the
    /// primary airlock, a closed door) are not in the palette and so are ignored — nothing to paint.
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

    private void NewDocument()
    {
        if (_catalog is null) return;
        if (_doc is not null) _doc.Changed -= OnDocChanged;
        _doc = new ShipDocument(_catalog);
        // every ship owns exactly one Primary Airlock, fixed at the root - seeded
        // outside the undo stack so it can't be undone into nothing, and locked
        // against move/rotate/delete like the game's own
        if (_catalog.ByDefName.ContainsKey(Catalog.PrimaryDocksysDef))
            new PlaceCommand(new Placement { DefName = Catalog.PrimaryDocksysDef, X = 0, Y = 0 }).Do(_doc);
        _doc.Changed += OnDocChanged;
        _meta = new OplanMeta();
        _stateDirty = false;
        _saveContext = null;
        _unresolvedParts = [];
        _stack.Reset();
        Board.SetDocument(_doc);
        Board.SetViewRot(0);
        OnDocChanged();
        UpdateInspector();
    }

    private void OnDocChanged()
    {
        var bounds = _doc?.Bounds();
        var dims = bounds is { } b ? $" · {b.MaxX - b.MinX + 1}×{b.MaxY - b.MinY + 1} tiles" : "";
        TxtParts.Text = $"{_doc?.Placements.Count ?? 0} parts{dims}";
        Board.SetLeakCells([]);   // any Ship Rating leak highlight is stale once the design changes
        ScheduleScan();
        UpdateZones();
        RefreshChrome();
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
        UpdateProblems(problems);
        Board.SetPowerOverlay(power);
        Board.SetRoomOverlay(rooms);
        Board.SetLightScene(light);
        Board.SetWalkOverlay(walk);
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

    private async void OnShipRatingClick(object sender, RoutedEventArgs e)
    {
        if (_analysing || _doc is null || _catalog is null || _index is null) return;
        if (_doc.Placements.Count == 0)
        {
            Dlg.Show(this, "Place some parts before running the Ship Rating.", "Ship Rating",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _analysing = true;
        BtnRating.IsEnabled = false;
        _roomSpecs ??= RoomCertifier.LoadSpecs(_index);
        var (doc, catalog, specs) = (_doc, _catalog, _roomSpecs);

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
                BtnRating.IsEnabled = true;
                _analysing = false;
            }
        }

        if (report is not null)
        {
            Board.SetLeakCells([]);
            var value = ShipValue.Estimate(doc, catalog, specs);
            var snapshot = Board.RenderRatingSnapshot(specs);
            var snapshotSvg = Board.RenderRatingSnapshotSvg(specs);   // scalable variant for the "Save image…" dialog
            new RatingReportWindow(report, value, snapshot, cells => Board.SetLeakCells(cells), snapshotSvg,
                kg => SetExtraMass(doc, kg)) { Owner = this }.ShowDialog();
        }
    }

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
        new MaterialsReportWindow(bom, scope) { Owner = this }.ShowDialog();
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

    /// <summary>One problem as an expandable row: a coloured title, action buttons (Show/View, and Dismiss for a
    /// dismissible warning), and the detail revealed on expand.</summary>
    private FrameworkElement ProblemRow(Problem problem)
    {
        var color = problem.Severity == ProblemSeverity.Blocking ? ThemeManager.Bad : ThemeManager.Warn;

        var header = new DockPanel { LastChildFill = true };

        // Buttons dock right, in reverse visual order (Dismiss rightmost, then Show/View).
        if (problem.DismissKey is { } key)
        {
            var dismiss = new Button
            {
                Content = "Dismiss", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center, ToolTip = "Hide this warning (restore it later with Restore Alerts).",
            };
            dismiss.Click += (_, e) => { e.Handled = true; DismissAlert(key); };
            DockPanel.SetDock(dismiss, Dock.Right);
            header.Children.Add(dismiss);
        }
        if (problem.Cells is { Count: > 0 } cells)
        {
            // A leak/airtightness warning (dismissible) highlights its leak points AND focuses; a plain illegal
            // problem (already hazard-tinted) just pans/zooms into view.
            var isLeak = problem.DismissKey is not null;
            var btn = new Button
            {
                Content = isLeak ? "Show" : "View", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = isLeak ? "Highlight the leak points and bring them into view" : "Pan and zoom the view to this problem",
            };
            btn.Click += (_, e) =>
            {
                e.Handled = true;
                if (isLeak) Board.SetLeakCells(cells);
                Board.FocusTiles(cells);
            };
            DockPanel.SetDock(btn, Dock.Right);
            header.Children.Add(btn);
        }
        header.Children.Add(new TextBlock
        {
            Text = "● " + problem.Title, Foreground = color, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
        });

        return new Expander
        {
            Header = header,
            Foreground = color,
            Margin = new Thickness(0, 1, 0, 1),
            Content = new TextBlock
            {
                Text = problem.Detail, Foreground = ThemeManager.Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 4, 2, 2),
            },
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

    private void OpenFile()
    {
        if (_catalog is null || !ConfirmDiscardChanges()) return;
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

        // designs saved before the primary-airlock convention gain one at the origin
        if (_catalog.ByDefName.ContainsKey(Catalog.PrimaryDocksysDef) && !doc.Placements.Any(doc.IsLocked))
            new PlaceCommand(new Placement { DefName = Catalog.PrimaryDocksysDef, X = 0, Y = 0 }).Do(doc);

        if (_doc is not null) _doc.Changed -= OnDocChanged;
        _doc = doc;
        _doc.FilePath = dlg.FileName;
        _doc.Changed += OnDocChanged;
        _meta = file.Meta;
        _stateDirty = false;
        _saveContext = null;   // a reopened save-derived design re-locates its context on demand (from SourceSave)
        _unresolvedParts = missing;   // a design missing its mods is incomplete: read-only until they're enabled
        _stack.Reset();
        Board.SetDocument(_doc);
        Board.SetViewRot(file.ViewRot);   // restore the saved plan-view orientation
        Board.FitContent();
        OnDocChanged();
        UpdateInspector();
        _settings.Touch(dlg.FileName);
        _settings.Save();
        AuditLog.Add($"Opened {dlg.FileName}.");

        // A reopened save-derived design carries no cargo (the .oplan stores only layout); re-locate its
        // source save and hang each container's contents back on its placement, so the inventory viewer works
        // right away. Eager, off-thread, and silent if the save has moved.
        if (_doc.SourceSave is { } srcSave)
            AttachSavedCargoAsync(_doc, srcSave);

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
    /// Re-attach a reopened save-derived design's cargo: re-locate its source save off the UI thread, rebuild the
    /// <see cref="SaveShipContext"/>, and hang each container's contents back on its placement (matched by
    /// <see cref="Placement.OriginStrID"/>) so the inventory viewer works immediately. Also caches the context so a
    /// later write-back skips a second re-locate. A moved/unreadable save just leaves the cargo unattached — the
    /// design still opens and edits; the write-back flow is where a missing save is reported.
    /// </summary>
    private async void AttachSavedCargoAsync(ShipDocument doc, SaveSourceRef src)
    {
        if (_catalog is null || _env is null) return;
        var (env0, catalog0, save0, reg0) = (_env, _catalog, src.SaveName, src.RegId);
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

        if (ctx is null || !ReferenceEquals(_doc, doc)) return;   // save gone, or the user moved to another design
        foreach (var p in doc.Placements)
            if (p.OriginStrID is { } id && !doc.IsCargoEdited(p) && ctx.CargoByOrigin.TryGetValue(id, out var forest))
                p.Cargo = forest;   // skip edited containers — their .oplan snapshot is authoritative
        _saveContext = ctx;
        UpdateInspector();
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
        _defaultHint ??= TxtHint.Text;
        TxtHint.Text = Board.WireMode
            ? "WIRE MODE · click a device, then another to connect · click a connected one to disconnect · right-click/Esc to cancel"
            : _defaultHint;
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
    private void ToggleDoors(IReadOnlyList<Placement> doors)
    {
        if (_doc is null || _catalog is null || doors.Count == 0) return;
        var commands = new List<IDocCommand>();
        var newIds = new List<Guid>();
        foreach (var p in doors)
        {
            if (_doc.IsLocked(p) || _catalog.DoorToggle(p.DefName) is not { } peer) continue;
            var swapped = p.Restate(peer, p.Rot);
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
    /// Swap each part between its installed and loose form — "Make Loose Item" (uninstall a fixture to its packaged
    /// form on the tile) or "Install item" (the reverse) — as one undo step, keeping tile/rotation and carrying any
    /// cargo; the swapped-in parts become the selection. An installed form that no longer fits isn't blocked, just
    /// flagged by the live problem scan, consistent with moves and replaces landing in an illegal spot.
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
    /// Replace the (unlocked) selection with a compatible buildable part — same render layer and
    /// footprint — chosen from a picker, keeping each part's tile and rotation. One undo step; the
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
                .Where(p => !_doc.IsLocked(p) && _doc.Part(p) is { } part
                            && (_catalog.RenderLayer(part), part.Item.Width, part.Item.Height) == cls)
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

    /// <summary>Paste the clipboard at the hovered tile (else just off the original), selecting the copies.</summary>
    private void PasteClipboard()
    {
        if (_doc is null || _clip.Count == 0) return;
        var anchor = _hoverCell ?? (_clipOrigin.X + 1, _clipOrigin.Y + 1);
        var clones = _clip
            .Select(c => new Placement
            {
                DefName = c.Def, X = anchor.X + c.X, Y = anchor.Y + c.Y, Rot = c.Rot,
                Cargo = Cargo.CloneForest(c.Cargo),   // fresh-id copies of the container's contents
            })
            .ToList();
        _stack.Push(_doc, new CompositeCommand(clones.Select(c => (IDocCommand)new PlaceCommand(c)).ToList()));
        Board.SelectedIds.Clear();
        foreach (var clone in clones) Board.SelectedIds.Add(clone.Id);
        Board.InvalidateVisual();
        UpdateInspector();
    }

    private void OnContextMenuRequested((int X, int Y) cell)
    {
        if (_doc is null) return;
        var stack = _doc.HitTestStack(cell.X, cell.Y);   // topmost first
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
        else if (stack.Count > 1)
        {
            // one tile with parts stacked: floors sit under what's on them, so this is how
            // you reach the part underneath. Click a row to select just it (● = current).
            menu.Items.Add(new MenuItem
            {
                Header = $"{stack.Count} stacked here — click to select:",
                IsEnabled = false,
                FontWeight = FontWeights.SemiBold,
            });
            foreach (var p in stack)
            {
                var target = p;
                var isSel = Board.SelectedIds.Count == 1 && Board.SelectedIds.Contains(p.Id);
                var label = (isSel ? "●  " : "○  ") + (_doc.Part(p)?.Friendly ?? p.DefName)
                            + (_doc.IsLocked(p) ? "   · fixed" : "");
                menu.Items.Add(Item(label, "", (_, _) => Board.SelectOnly(target)));
            }
        }
        else
        {
            var only = stack[0];
            menu.Items.Add(new MenuItem
            {
                Header = (_doc.Part(only)?.Friendly ?? only.DefName) + (_doc.IsLocked(only) ? "  · fixed to the ship" : ""),
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

        // "Replace with…": enabled when the whole (unlocked) selection shares one render layer +
        // footprint and at least one buildable part of that same kind exists to swap in.
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

        // "View contents…": a single container/console/crate — shown even when empty (so an imported empty
        // container isn't "locked"). Not shown for a multi-selection — uses the lone selected part, else topmost.
        var cargoTarget = multi ? null : (selected.Count == 1 ? selected[0] : stack[0]);
        if (cargoTarget is { } ct && CanViewContents(ct))
        {
            var n = ct.Cargo.Count;
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("View contents" + (n > 0 ? $" ({n})" : "") + "…", "", (_, _) => OpenInventory(ct)));
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
        menu.Items.Add(Item("Paste", "Ctrl+V", (_, _) => PasteClipboard(), _clip.Count > 0));
        menu.Items.Add(Item("Rotate CW" + suffix, "R", (_, _) => RotateSelection(90), canRotate));
        menu.Items.Add(Item("Rotate CCW" + suffix, "Shift+R", (_, _) => RotateSelection(-90), canRotate));
        menu.Items.Add(Item("Flip Horizontal" + suffix, "H", (_, _) => FlipSelection(horizontal: true), canRotate));
        menu.Items.Add(Item("Flip Vertical" + suffix, "Shift+H", (_, _) => FlipSelection(horizontal: false), canRotate));
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
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(_settings.IsFavorite(lo.DefName, true) ? "Remove from Favorites" : "Add to Favorites",
            "", (_, _) => ToggleFavoriteByRef(lo.DefName, true)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Delete", "Del", (_, _) => DeleteSelection()));
        menu.IsOpen = true;
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
        var friendly = _doc.Part(p)?.Friendly ?? p.DefName;
        new InventoryWindow(_catalog, _sprites, p.DefName, friendly, p.Cargo, _doc, _stack, p) { Owner = this }.ShowDialog();
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
                if (ConfirmDiscardChanges()) { NewDocument(); AuditLog.Add("New design."); }
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
        InsFriendly.Text = part.Friendly + lockedNote + looseNote;
        InsInternal.Text = part.DefName;
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
        PopulateStats(part);
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
    private void PopulateStats(PartDef part)
    {
        var vals = part.StartingCondValues;

        StatsList.Children.Clear();
        foreach (var (key, label, unit) in KeyStats)
            if (vals.TryGetValue(key, out var v))
                StatsList.Children.Add(StatRow(label, FormatStat(v) + (unit.Length > 0 ? " " + unit : ""), raw: false));
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

    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        if (ConfirmDiscardChanges()) { NewDocument(); AuditLog.Add("New design."); }
    }

    private void OnOpenClick(object sender, RoutedEventArgs e) => OpenFile();
    private void OnSaveClick(object sender, RoutedEventArgs e) => Save();
    private void OnSaveAsClick(object sender, RoutedEventArgs e) => SaveAs();

    /// <summary>
    /// Export the current design as a spawnable local data mod. Runs the P2 engine to bake
    /// <c>aRooms</c>/<c>aRating</c>, reverse-maps every part to the game's centre/CCW coordinates,
    /// and writes a mod folder — never <c>loading_order.json</c> (registration stays with
    /// Ostrasort/ModTools; the dialog and confirmation both say so).
    /// </summary>
    /// <summary>Edit the ship's in-game identity (name/make/model/year/designation/description). The values live
    /// on <see cref="_meta"/>, so they persist in the .oplan and pre-fill the export dialog.</summary>
    private void OnShipInfoClick(object sender, RoutedEventArgs e)
    {
        if (_doc is null) return;
        var dlg = new ShipInfoDialog(_meta) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        if (dlg.PublicName == _meta.PublicName && dlg.Make == _meta.Make && dlg.Model == _meta.Model
            && dlg.Year == _meta.Year && dlg.Designation == _meta.Designation && dlg.Description == _meta.Description)
            return;   // nothing changed — don't dirty the document
        dlg.ApplyTo(_meta);
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
    private void OpenExportWizard(ExportDestination? preselect)
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
        var session = new WizardSession
        {
            Plan = ExportPlan.FromSettings(_settings, _meta, _doc.SourceSave),
            Doc = _doc,
            Catalog = _catalog,
            Specs = _roomSpecs,
            Index = _index,
            Env = _env,
            Settings = _settings,
            Meta = _meta,
            Saves = SaveImport.ListSaves(_env),
            SourceSave = _doc.SourceSave,
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

        // Identity edited in the wizard flows back onto the design's saved metadata, so the two never drift.
        ApplyExportedIdentity(session.Plan.Identity);
    }

    /// <summary>Fold the identity the user typed in the export wizard back into the design's own metadata, marking
    /// the design dirty only when something actually changed.</summary>
    private void ApplyExportedIdentity(ExportMetadata id)
    {
        if (id.PublicName == _meta.PublicName && id.Make == _meta.Make && id.Model == _meta.Model
            && id.Year == _meta.Year && id.Designation == _meta.Designation && id.Description == _meta.Description)
            return;

        _meta.PublicName = id.PublicName; _meta.Make = id.Make; _meta.Model = id.Model;
        _meta.Year = id.Year; _meta.Designation = id.Designation; _meta.Description = id.Description;
        _stateDirty = true;
        RefreshChrome();
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
        menu.Items.Add(LightSliderRow("Sun angle", 0, 360, _settings.LightSunAngle, "0", SetSunAngle));
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
    /// <paramref name="onChange"/> live, and the box shows the current value in <paramref name="format"/>.</summary>
    private static MenuItem LightSliderRow(string label, double min, double max, double value, string format, Action<double> onChange)
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

    /// <summary>The File ▾ dropdown: document lifecycle, import, export, and write-back to a save.</summary>
    private void OnFileMenuClick(object sender, RoutedEventArgs e)
    {
        var m = new ContextMenu();
        m.Items.Add(MenuAction("New", () => OnNewClick(this, e), gesture: "Ctrl+N"));
        m.Items.Add(MenuAction("Open…", () => OnOpenClick(this, e), gesture: "Ctrl+O"));
        m.Items.Add(MenuAction("Save", () => OnSaveClick(this, e), gesture: "Ctrl+S"));
        m.Items.Add(MenuAction("Save As…", () => OnSaveAsClick(this, e), gesture: "Ctrl+Shift+S"));
        m.Items.Add(new Separator());
        m.Items.Add(BuildImportSubmenu());
        m.Items.Add(MenuAction("Export…", () => OnExportClick(this, e), gesture: "Ctrl+E"));
        m.Items.Add(new Separator());
        // write-back is only meaningful for a design imported from a save FOR EDITING
        m.Items.Add(MenuAction("Update Ship in Save…", () => OnUpdateSaveClick(this, e), enabled: _doc?.SourceSave is not null));
        OpenMenuUnder(m, BtnFileMenu);
    }

    /// <summary>The Import ▸ submenu: start a design from an existing ship or a save game.</summary>
    private MenuItem BuildImportSubmenu()
    {
        var import = new MenuItem { Header = "Import" };
        import.Items.Add(MenuAction("From ship template…", ImportTemplate));
        import.Items.Add(MenuAction("From save game (layout only)…", ImportSave));
        import.Items.Add(new Separator());
        import.Items.Add(MenuAction("Your ship, for editing (write back to the save)…", ImportSaveForEditing));
        return import;
    }

    /// <summary>The Design ▾ dropdown: ship identity, wall/floor re-skin, snapshot, and the bill of materials.</summary>
    private void OnDesignMenuClick(object sender, RoutedEventArgs e)
    {
        var m = new ContextMenu();
        m.Items.Add(MenuAction("Ship Info…", () => OnShipInfoClick(this, e), gesture: "Ctrl+I"));
        m.Items.Add(MenuAction("Ship Re-skin…", () => OnThemeClick(this, e)));
        m.Items.Add(new Separator());
        m.Items.Add(MenuAction("Snapshot…", () => OnSnapshotClick(this, e)));
        m.Items.Add(MenuAction("Bill of Materials…", () => OnMaterialsClick(this, e), gesture: "Ctrl+B"));
        OpenMenuUnder(m, BtnDesignMenu);
    }

    /// <summary>The View ▾ dropdown: fit, symmetry, Light Viz dimming, and the mod-override toggle. The overlay
    /// toggles (Zones / Rooms / Power / Light / Wire) now live on the toolbar as highlighted buttons, so they are no
    /// longer duplicated here. State is read live when the menu opens (the active symmetry mode / the checkmark).</summary>
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
        m.Items.Add(MenuAction("Mod overrides", ToggleModOverrides, check: Board.AllowModdedOverrides));
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
    }

    /// <summary>Pick a save and import the player's ship from it — layout only, behind an explicit confirmation.</summary>
    private async void ImportSave()
    {
        if (_catalog is null || _env is null || !ConfirmDiscardChanges()) return;

        var saves = SaveImport.ListSaves(_env);
        if (saves.Count == 0)
        {
            Dlg.Show(this, "No save games found in your Ostranauts Saves folder.", "Import",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var picker = new SavePickerDialog(saves) { Owner = this };
        if (picker.ShowDialog() != true || picker.Selected is not { } save) return;

        var ship = save.ShipName.Length > 0 ? $"\"{save.ShipName}\"" : "the player's ship";
        var who = save.PlayerName.Length > 0 ? $"{save.PlayerName}'s " : "";
        if (!Dlg.Confirm(this, DlgKind.Info, $"Import {ship} for planning?",
                $"From {who}save \"{save.Name}\".\n\n" +
                "Ostraplan imports the ship layout only.\n" +
                "Crew, cargo, installed modules, wear, and damage are discarded, giving a pristine editable design.",
                "Import layout"))
            return;

        var (catalog, zip) = (_catalog, save.ZipPath);
        ImportResult result;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            result = await Ui.OffThread(() => SaveImport.ImportPlayerShip(zip, catalog));
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

        InstallImportedDocument(result);
        AuditLog.Add($"Imported ship from save \"{save.Name}\" (layout only).");
    }

    /// <summary>Import the player's ship FOR EDITING: keeps each part's save identity plus a full context, so
    /// the edited layout can be written back into a copy of the save with crew and cargo preserved.</summary>
    private async void ImportSaveForEditing()
    {
        if (_catalog is null || _env is null || !ConfirmDiscardChanges()) return;

        var saves = SaveImport.ListSaves(_env);
        if (saves.Count == 0)
        {
            Dlg.Show(this, "No save games found in your Ostranauts Saves folder.", "Import",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var picker = new SavePickerDialog(saves) { Owner = this };
        if (picker.ShowDialog() != true || picker.Selected is not { } save) return;

        // choose WHICH ship: the game imports the ship you're standing on, which may be a station. List the
        // player's actually-owned ships (from aMyShips) instead, plus the current ship as an unsupported option.
        var ships = SaveImport.ListPlayerShips(save.ZipPath);
        if (ships.Count == 0)
        {
            Dlg.Show(this,
                "Couldn't find a ship to edit in that save (no owned ships and no current ship on record).",
                "Import", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var shipDlg = new ShipChoiceDialog(save.Name, ships) { Owner = this };
        if (shipDlg.ShowDialog() != true || shipDlg.Selected is not { } chosen) return;

        // editing a ship you don't own (a station, another vessel) is unsupported — gate it behind a stern warning
        if (!chosen.Owned && !ConfirmUnsupportedShip(chosen)) return;

        if (!Dlg.Confirm(this, DlgKind.Info, $"Import \"{chosen.Name}\" for editing?",
                $"Ship {chosen.RegId} from save \"{save.Name}\".\n\n" +
                "You'll redesign the ship's structure out of game.\n" +
                "When you choose the Update Ship in Save action, Ostraplan writes the result back into the save, either as a new copy (the default) or the original in place, keeping crew, cargo, world position, and ship identity.\n\n" +
                "The .oplan you save stays linked to this save.\n" +
                "It references the ship's live state (crew, cargo, wear) rather than embedding it, so keep the save if you want to write back later.\n\n" +
                "For a standalone, shareable ship instead, use Export, which makes a spawnable mod.",
                "Import for editing"))
            return;

        var (catalog, entry, reg) = (_catalog, save, chosen.RegId);
        SaveEditImportResult edit;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            edit = await Ui.OffThread(() => SaveEditImport.ImportForEditing(entry, reg, catalog));
        }
        catch (Exception ex)
        {
            Dlg.Show(this, "Import failed:\n\n" + ex.Message, "Import", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally { Mouse.OverrideCursor = null; }

        if (!OfferStandIns(edit, chosen.Name)) return;   // cancelled at the missing-mods prompt
        InstallImportedDocument(edit.Import, edit.Context);
        AuditLog.Add($"Imported ship \"{chosen.Name}\" ({chosen.RegId}) for editing from save \"{save.Name}\".");
    }

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
    /// </summary>
    private void OnUpdateSaveClick(object sender, RoutedEventArgs e)
    {
        if (_doc?.SourceSave is null)
        {
            Dlg.Show(this, "This design wasn't imported from a save. Use Import > \"Your ship, for editing\" first.",
                "Update ship in save", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        OpenExportWizard(ExportDestination.UpdateShipInSave);
    }

    /// <summary>The stern gate before editing a ship the player doesn't own (a station or another vessel).</summary>
    private bool ConfirmUnsupportedShip(SaveShipChoice c) =>
        Dlg.Confirm(this, DlgKind.Danger, "This isn't your ship",
            $"{c.Name} ({c.RegId}) is a station or another vessel, not one of your ships.\n\n" +
            "Editing something you don't own is not supported, and it can corrupt or break your save.\n" +
            "Ostraplan can't guarantee a valid result, and takes no responsibility for the outcome. You do.",
            "Edit it anyway");

    /// <summary>Browse core+mod ship templates and import the chosen one as a fresh design.</summary>
    private async void ImportTemplate()
    {
        if (_catalog is null || _index is null || !ConfirmDiscardChanges()) return;

        var ships = TemplateImport.ListShipFiles(_index);
        if (ships.Count == 0)
        {
            Dlg.Show(this, "No ship templates found in the game data or your mods.", "Import",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var browser = new TemplateBrowserDialog(ships) { Owner = this };
        if (browser.ShowDialog() != true || browser.Selected is not { } entry) return;

        var (catalog, path) = (_catalog, entry.Path);
        ImportResult result;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            result = await Ui.OffThread(() => TemplateImport.LoadFile(path, catalog));
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

        InstallImportedDocument(result);
        AuditLog.Add($"Imported ship template \"{result.ShipName}\".");
    }

    /// <summary>Swap an imported ship in as the active document (no file path — Save prompts Save As). The
    /// optional context is retained when the ship was imported FOR EDITING, enabling write-back to the save.</summary>
    private void InstallImportedDocument(ImportResult result, SaveShipContext? context = null)
    {
        if (_doc is not null) _doc.Changed -= OnDocChanged;
        _doc = result.Doc;
        _doc.FilePath = null;
        _doc.Changed += OnDocChanged;
        _meta = new OplanMeta { Name = result.ShipName };
        _stateDirty = false;
        _saveContext = context;
        _unresolvedParts = [];   // a fresh import is a complete, saveable design (unlike a reopened .oplan missing its mods)
        _stack.Reset();
        Board.SetDocument(_doc);
        Board.SetViewRot(0);
        Board.FitContent();
        OnDocChanged();
        UpdateInspector();
        ReportImport(result, keptContents: context is not null, skippedHandled: context is not null);
    }

    /// <summary>Tell the user about anything the import dropped (contained cargo, unresolved defs). Silent on a
    /// clean import. <paramref name="keptContents"/> is true for a save import FOR EDITING, where contained cargo
    /// is preserved as viewable container contents rather than discarded (a layout-only / template import drops it).</summary>
    private void ReportImport(ImportResult result, bool keptContents, bool skippedHandled = false)
    {
        var notes = new List<string>();
        if (result.ContainedDropped > 0)
            notes.Add(keptContents
                ? $"{result.ContainedDropped} contained item(s) (cargo, tools, installed modules) were kept as container contents.\n" +
                  "Right-click a container and choose \"View contents\" to see them. They aren't placed on the grid as buildable structure."
                : $"{result.ContainedDropped} contained item(s) were dropped (cargo, tools, installed modules).\nOstraplan imports the layout only.");
        if (result.SystemDropped > 0)
            notes.Add($"{result.SystemDropped} loot spawner and system object(s) were dropped.\nThey populate the ship at runtime, and aren't buildable structure.");
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

    /// <summary>Toggle whether modded parts may be placed where the core-only placement law says they don't fit
    /// (persisted). Core parts stay hard-blocked; overridden modded parts are placed and flagged as warnings.</summary>
    private void ToggleModOverrides()
    {
        Board.AllowModdedOverrides = !Board.AllowModdedOverrides;
        _settings.AllowModdedOverrides = Board.AllowModdedOverrides;
        _settings.Save();
        Board.InvalidateVisual();   // refresh the armed ghost (green/amber/red) under the new rule
        AuditLog.Add(Board.AllowModdedOverrides
            ? "Modded overrides enabled — modded parts may break the placement law (flagged)."
            : "Modded overrides disabled — modded parts are enforced like core.");
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
            var warnings = _catalog?.Warnings ?? [];
            var trail = AuditLog.SessionTrail();

            var content =
                "# Ostraplan diagnostics\n\n" +
                "*Generated by Help ▸ Report a Bug. Your Windows account name and file paths are scrubbed.*\n\n" +
                "## Environment\n\n" + DiagnosticsHeader() + "\n" +
                Section("Recent errors (error.log)", errors, "_None recorded this session or last._") +
                (warnings.Count > 0 ? Section("Catalog load warnings", warnings.Take(200).ToList(), "") : "") +
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

    /// <summary>A one-line, path-free summary of the current design for the bug report's diagnostics.</summary>
    private string DescribeDocument()
    {
        if (_doc is null) return "none";
        var kind = _doc.SourceSave is not null ? "save-derived" : _doc.FilePath is not null ? ".oplan" : "unsaved";
        var dirty = _stack.Dirty ? ", unsaved changes" : "";
        var incomplete = _unresolvedParts.Count > 0 ? $", {_unresolvedParts.Count} missing-mod part(s)" : "";
        return $"{_doc.Placements.Count} parts, {kind}{dirty}{incomplete}";
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

    /// <summary>Theme picker: apply and persist. DynamicResource + Fluent ThemeMode retint the chrome live.</summary>
    private void OnThemeModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_themeInit) return;
        var mode = CmbTheme.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" };
        _settings.Theme = mode;
        AuditLog.Setting("Theme", mode);
        _settings.Save();
        ThemeManager.Apply(mode);
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

    /// <summary>Confirms, then applies the downloaded update and restarts (does not return on success).</summary>
    private void PromptRestartAndApply()
    {
        if (_updater is null || _pendingUpdate is null) return;
        var ver = VeloUpdate.VersionOf(_pendingUpdate);
        if (!Dlg.Confirm(this, DlgKind.Info, "Restart to finish updating",
                $"Ostraplan v{ver} has been downloaded.\n\nOstraplan will close, apply the update, and reopen.",
                "Restart now", "Later"))
            return;
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
            ("Context menu", "RMB", "Use as brush · Replace with… · Find and Replace All… · Make Loose Item / Install item · pick a buried layer on stacked tiles · Select only (after a box-select) · Close/Open door. Also cancels placement while armed."),
            ("Rotate part", "R / Shift+R", "CW / CCW — the armed part, a selected part in place, or a whole selection about its centre (walls & floors auto-tile rather than turn). The brush keeps its angle when you arm another part; the ghost draws a needle towards its leading edge and the status bar reads out the angle."),
            ("Flip selection", "H / Shift+H", "Mirror the selection about its centre — H horizontal (left↔right), Shift+H vertical (up↔down); each part reflects and snaps to a real rotation."),
            ("Symmetry", "M", "Cycle Off → Vertical → Horizontal → Both; axes centre on the hovered tile when switching on. While on, it also drives editing: selecting a part grabs its mirror partner(s), and moving, rotating, or deleting the group keeps it symmetric (the far side tracks in the mirrored direction)."),
            ("Mod overrides", "Toolbar toggle", "Let modded parts place where the core-game rules say they don't fit (ghost turns amber, flagged as a warning — verify in-game). Core parts stay enforced."),
            ("Power overlay", "P", "Show/hide PowerViz: lit conduit runs flow from a live generator/battery, orphaned runs are dim red, and a wired device with no feed gets an amber marker. A powered part also shows its connector badges (blue IN, green OUT) while armed or selected."),
            ("Rooms overlay", "C", "Show/hide RoomViz: every compartment the game would flood-fill, tinted in its own colour and labelled with what it certifies as, its size and its value. A room that certifies as nothing says why — what to add, and which item in it blocks the spec (a canister parked in a quarters, say). Unsealed compartments are red. The exterior isn't tinted, so a room open to space simply loses its tint."),
            ("Light overlay", "L", "Show/hide Light Viz: interior lighting simulated from every fixture and lit device. Each light floods its compartment (bounded by walls) in its own colour, so dark corners and colour clashes show at a glance. The View menu's Light Viz sliders set the light brightness and how far unlit areas darken (from a glow over the full-bright ship up to the in-game dark look)."),
            ("Walk overlay", "K", "Show/hide WalkViz: every tile crew can stand on, tinted by which connected zone it belongs to — two tiles sharing a colour are reachable from each other on foot, two colours mean no route. Fittings nobody can operate are ringed in red at the spot they'd have to stand, and a doorway with vacuum on one side is dashed amber (crossable, but only in a suit). Note a closed door only seals if it is unpowered, locked or damaged; a powered one crew simply open. The View menu can count spacewalks and choose whether Forbid zones apply."),
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
            ("New / open / save", "Ctrl+N / O / S", "New · open · save (Ctrl+Shift+S = Save As)."),
            ("Export", "Ctrl+E", "Export the design as a spawnable local data mod."),
            ("Ship Info / Materials", "Ctrl+I / Ctrl+B", "Edit the in-game identity · open the bill of materials."),
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
