using System.Diagnostics;
using System.IO;
using System.Net.Http;
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
using Ostraplan.Core;

namespace Ostraplan.App;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private bool _themeInit;   // suppress the theme combo's SelectionChanged during initial sync
    private const string ReleasesUrl = "https://github.com/Valtora/Ostraplan/releases";
    private string _updateUrl = ReleasesUrl;
    private readonly CommandStack _stack = new();
    private GameEnv? _env;
    private DataIndex? _index;
    private Catalog? _catalog;
    private SpriteCache? _sprites;   // shared with the canvas; also feeds the inventory viewer
    private List<PartVM> _allParts = [];
    private readonly List<ListBox> _paletteLists = [];
    private ShipDocument? _doc;
    private SaveShipContext? _saveContext;   // set when a design was imported from a save FOR EDITING — enables writing it back
    private OplanMeta _meta = new();
    // Parts an opened .oplan referenced whose defs aren't in the current game + mods data. While this is
    // non-empty the design is INCOMPLETE and treated as read-only: Save is blocked (writing now would
    // permanently drop these parts) and the chrome shows a standing warning, because building over — or
    // moving parts into — the space where they belong can produce a ship that's invalid in-game. Enabling
    // the mods (verify with Ostrasort) and reopening clears it. See OpenFile / GuardIncompleteSave.
    private IReadOnlyList<OplanPart> _unresolvedParts = [];
    private bool _syncingPalette;
    private IReadOnlyList<RoomSpecDef>? _roomSpecs;   // lazily loaded once for the Ship Rating analysis
    private bool _analysing;
    private (int X, int Y)? _hoverCell;               // last hovered tile — the paste anchor
    private List<(string Def, int X, int Y, int Rot)> _clip = [];   // copied selection, relative to its top-left
    private (int X, int Y) _clipOrigin;               // the copied selection's original top-left (paste fallback)
    private readonly DispatcherTimer _scanTimer;      // debounces the (now off-thread) problem scan
    private CancellationTokenSource? _scanCts;        // cancels a superseded scan


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
        Board.SymmetryChanged += () => BtnSym.Content = "Symmetry: " + Board.SymMode switch
        {
            SymmetryMode.Vertical => "V",
            SymmetryMode.Horizontal => "H",
            SymmetryMode.Both => "V+H",
            _ => "Off",
        };
        Board.SelectionChanged += UpdateInspector;
        Board.LooseSelectionChanged += UpdateInspector;
        Board.HoverChanged += cell => { _hoverCell = cell; TxtCell.Text = cell is { } c ? $"tile {c.X}, {c.Y}" : "—"; };
        Board.ViewChanged += UpdateZoomText;
        Board.Disarmed += ClearPaletteSelection;
        Board.ContextMenuRequested += OnContextMenuRequested;
        Board.LooseContextMenuRequested += OnLooseContextMenuRequested;
        Board.GhostReasonChanged += reason => TxtGhost.Text = reason is null ? "" : "⛔ can't place here — " + reason;
        Board.ZoneStrokeCommitted += OnZoneStrokeCommitted;
        Board.ShowZonesChanged += UpdateZonesButton;
        Board.ActiveZoneChanged += UpdateZones;   // reflect which zone (if any) is being painted
        _stack.StateChanged += RefreshChrome;
        _stack.Applied += (cmd, action) => AuditLog.Command(action, cmd);   // audit every edit/undo/redo

        _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _scanTimer.Tick += (_, _) => RunScan();

        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        Deactivated += (_, _) => Board.ClearPanKeys();   // a KeyUp we never receive must not leave the view drifting
        Loaded += async (_, _) => await LoadDataAsync();
        ContentRendered += OnContentRendered;   // one-time first-run offer to install the exe + shortcuts
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
            (index, catalog, sprites, parts) = await Task.Run(() =>
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
                    .Select(p => new PartVM(p with { Category = ItemsCategory }, spr.Thumb(p))));
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
        if (env.VersionMatchesVerified)
        {
            TxtVersion.Text = $"Game {v}";
        }
        else
        {
            TxtVersion.Text = $"Game {v} — Law verified against {GameEnv.VerifiedGameVersion}";
            TxtVersion.SetResourceReference(TextBlock.ForegroundProperty, "Warn");   // tracks the theme
        }

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
        TxtZoom.Text = $"zoom {Board.Zoom / 16:0.#}×" + (Board.ViewRot != 0 ? $" · view {Board.ViewRot}°" : "");

    // ---- palette ----

    /// <summary>The synthetic palette category for loose cargo (the ITEMS tab). Not a game build category — it
    /// exists only to group the loose universe into its own tab and to flag an armed brush as a loose drop. Value is
    /// the uppercase tab header, matching the game's HULL/HVAC/… tabs.</summary>
    private const string ItemsCategory = "ITEMS";

    private void BuildPalette()
    {
        Tabs.Items.Clear();
        _paletteLists.Clear();
        foreach (var category in new[] { "All" }.Concat(Catalog.Categories).Append(ItemsCategory))
        {
            var list = new ListBox
            {
                ItemTemplate = (DataTemplate)FindResource("PartTemplate"),
                BorderThickness = new Thickness(0),
                Tag = category == "All" ? null : category,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                // typeahead runs on TextInput, which fires even when the window
                // handles PreviewKeyDown - pressing R would silently jump the
                // palette to an R-part instead of rotating
                IsTextSearchEnabled = false,
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            list.SelectionChanged += OnPaletteSelection;
            _paletteLists.Add(list);
            Tabs.Items.Add(new TabItem { Header = category, Content = list });
        }
        RefreshPalette();
    }

    private void RefreshPalette()
    {
        var search = TxtSearch.Text.Trim();
        _syncingPalette = true;
        foreach (var list in _paletteLists)
        {
            var category = (string?)list.Tag;
            // The "All" tab (null Tag) is the buildable palette only — the huge loose universe stays in its own
            // Items tab, so it doesn't drown the structure parts.
            list.ItemsSource = _allParts
                .Where(vm => (category is null ? vm.Part.Category != ItemsCategory : vm.Part.Category == category) && vm.Matches(search))
                .ToList();
        }
        _syncingPalette = false;
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
        AuditLog.Tool(vm.Part.Friendly);
        Board.Focus();   // keys (R, Del, Esc) belong to the canvas once a part is armed
        UpdateInspector();
    }

    private void ClearPaletteSelection()
    {
        _syncingPalette = true;
        foreach (var list in _paletteLists) list.SelectedItem = null;
        _syncingPalette = false;
        UpdateInspector();
    }

    /// <summary>
    /// Arm the brush with a placed part's def (the RMB "Use as brush" action) and keep drawing.
    /// Selecting its palette entry (when visible) both arms it and syncs the highlight; if it is
    /// filtered out by the search, arm directly. Non-buildable parts (the primary airlock,
    /// a closed door) are not in the palette and so are silently ignored — nothing to paint.
    /// </summary>
    private void OnArmFromTile(string defName)
    {
        var vm = _allParts.FirstOrDefault(v => v.Part.DefName == defName);
        if (vm is null) return;
        foreach (var list in _paletteLists)
            if (list.Items.Contains(vm)) { list.SelectedItem = vm; Board.Focus(); return; }
        Board.SetArmed(vm.Part);   // visible nowhere (search-filtered) — arm without a palette highlight
        AuditLog.Tool(vm.Part.Friendly);
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
        _saveContext = null;
        _unresolvedParts = [];
        _stack.Reset();
        Board.SetDocument(_doc);
        OnDocChanged();
        UpdateInspector();
        UpdateSaveEditUi();
    }

    /// <summary>Enable "Update Ship in Save…" only for a save-derived design (fresh import, or reopened .oplan
    /// carrying a source reference — the context is re-located on demand).</summary>
    private void UpdateSaveEditUi() => BtnUpdateSave.IsEnabled = _doc?.SourceSave is not null;

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

        List<Problem> problems;
        try
        {
            problems = await Task.Run(() => ProblemScan.Scan(snapshot, catalog), token);
        }
        catch (OperationCanceledException) { return; }
        if (token.IsCancellationRequested || !ReferenceEquals(cts, _scanCts)) return;   // superseded
        UpdateProblems(problems);
    }

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
        try
        {
            report = await Task.Run(() => ShipAnalysis.AnalyzeDocument(doc, catalog, specs, reporter));
        }
        catch (Exception ex)
        {
            Dlg.Show(this, "Analysis failed: " + ex.Message, "Ship Rating", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            progress.Close();
            BtnRating.IsEnabled = true;
            _analysing = false;
        }

        if (report is not null)
        {
            Board.SetLeakCells([]);
            var value = ShipValue.Estimate(doc, catalog, specs);
            var snapshot = Board.RenderRatingSnapshot(specs);
            new RatingReportWindow(report, value, snapshot, cells => Board.SetLeakCells(cells)) { Owner = this }.ShowDialog();
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
        var blocking = problems.Where(p => p.Severity == ProblemSeverity.Blocking).ToList();
        var warnings = problems.Where(p => p.Severity == ProblemSeverity.Warning).ToList();

        // hazard-tint the tiles of every socket-illegal / unconstructible placement
        Board.SetIllegalCells([.. problems.Where(p => p.Cells is not null).SelectMany(p => p.Cells!).Distinct()]);

        BadgeBlocking.Visibility = blocking.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BadgeBlockingText.Text = $"!  {blocking.Count}";
        BadgeBlocking.ToolTip = blocking.Count > 0 ? string.Join("\n", blocking.Select(p => p.Title)) : null;
        BadgeWarning.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BadgeWarningText.Text = $"⚠  {warnings.Count}";
        BadgeWarning.ToolTip = warnings.Count > 0 ? string.Join("\n", warnings.Select(p => p.Title)) : null;

        ProblemsPanel.Children.Clear();
        if (problems.Count == 0)
        {
            ProblemsPanel.Children.Add(new TextBlock
            {
                Text = "None found.",
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
            return;
        }

        foreach (var problem in problems.OrderByDescending(p => p.Severity))
            ProblemsPanel.Children.Add(ProblemRow(problem));
    }

    /// <summary>One problem as an expandable row: a coloured title, a "View" button that pans/zooms the canvas to
    /// the offending tiles, and the detail revealed on expand.</summary>
    private FrameworkElement ProblemRow(Problem problem)
    {
        var color = problem.Severity == ProblemSeverity.Blocking ? ThemeManager.Bad : ThemeManager.Warn;

        var header = new DockPanel { LastChildFill = true };
        if (problem.Cells is { Count: > 0 } cells)
        {
            var view = new Button
            {
                Content = "View", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center, ToolTip = "Pan and zoom the view to this problem",
            };
            view.Click += (_, e) => { e.Handled = true; Board.FocusTiles(cells); };   // don't also toggle the expander
            DockPanel.SetDock(view, Dock.Right);
            header.Children.Add(view);
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

    private void RefreshChrome()
    {
        BtnUndo.IsEnabled = _stack.CanUndo;
        BtnRedo.IsEnabled = _stack.CanRedo;
        var name = _doc?.FilePath is { } f ? Path.GetFileNameWithoutExtension(f) : _meta.Name;
        var star = _stack.Dirty ? " *" : "";
        var incomplete = _unresolvedParts.Count > 0 ? "  ⚠ MISSING MODS — read-only" : "";
        TxtDoc.Text = name + star + incomplete;
        Title = $"Ostraplan v{AppVersion} — {name}{star}{incomplete}";
    }

    private bool ConfirmDiscardChanges()
    {
        if (_doc is null || !_stack.Dirty) return true;
        var name = _doc.FilePath is { } f ? Path.GetFileNameWithoutExtension(f) : _meta.Name;

        // An incomplete design (missing-mod parts) can't be saved — don't offer a Save that will fail;
        // just confirm the discard.
        if (_unresolvedParts.Count > 0)
            return Dlg.Confirm(this, DlgKind.Danger, "Discard your changes?",
                $"“{name}” is missing mods and can't be saved, so continuing will lose your unsaved changes.",
                "Discard changes");

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
            OplanFile.FromDocument(_doc, _index, _meta).Save(_doc.FilePath);
        }
        catch (Exception ex)
        {
            Dlg.Show(this, ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        _stack.MarkSaved();
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

    /// <summary>Refuse to persist a design that still references parts from mods that aren't loaded: writing now
    /// would silently drop them for good, and the incomplete canvas invites edits that break the real ship.
    /// Returns true when it's safe to save. See <see cref="_unresolvedParts"/>.</summary>
    private bool GuardIncompleteSave()
    {
        if (_unresolvedParts.Count == 0) return true;
        Dlg.Warn(this, "Can't save an incomplete design",
            $"This design still has {_unresolvedParts.Count} part(s) from mods that aren't loaded, so it can't be saved.\n\n" +
            FormatMissingDefs(_unresolvedParts) +
            "\n\nSaving now would drop them for good.\n" +
            "Enable the required mods, and run Ostrasort to confirm they're subscribed and enabled.\n" +
            "Then reopen this design and it will be complete and saveable again.");
        return false;
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
        _saveContext = null;   // a reopened save-derived design re-locates its context on demand (from SourceSave)
        _unresolvedParts = missing;   // a design missing its mods is incomplete: read-only until they're enabled
        _stack.Reset();
        Board.SetDocument(_doc);
        Board.FitContent();
        OnDocChanged();
        UpdateInspector();
        UpdateSaveEditUi();
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
                "\n\nInstall or subscribe to those mods and enable them, then reopen this design.\n" +
                "Run Ostrasort to confirm they're subscribed, enabled, and in a working load order.\n\n" +
                "Until then the design is read only, so saving is disabled.\n" +
                "Saving now would permanently drop the missing parts.\n" +
                "Building over the space where they belong (or moving parts into it) can produce a ship that's invalid in game.");
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
            ctx = await Task.Run(() =>
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
    }

    private void OnMoveRequested(IReadOnlyList<Placement> placements, int dx, int dy)
    {
        if (_doc is null || placements.Count == 0) return;
        _stack.Push(_doc, new MoveCommand(placements, dx, dy));
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

    private void OnZonesClick(object sender, RoutedEventArgs e) => Board.ToggleZones();

    private void UpdateZonesButton() => BtnZones.Content = "Zones: " + (Board.ShowZones ? "On" : "Off");

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
        _stack.Push(_doc, new CreateZoneCommand(zone));
        Board.SetActiveZone(zone.Id);   // arm it so the user can paint straight away
        AuditLog.Add($"Added zone “{zone.Name}”.");
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
        UpdateZonesButton();
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

    private void DuplicateSelection()
    {
        if (_doc is null) return;
        var selected = Board.SelectedPlacements().Where(p => !_doc.IsLocked(p)).ToList();
        if (selected.Count == 0) return;
        var clones = selected
            .Select(p => new Placement { DefName = p.DefName, X = p.X + 1, Y = p.Y + 1, Rot = p.Rot })
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
    /// </summary>
    private void ToggleDoors(IReadOnlyList<Placement> doors)
    {
        if (_doc is null || _catalog is null || doors.Count == 0) return;
        var commands = new List<IDocCommand>();
        var newIds = new List<Guid>();
        foreach (var p in doors)
        {
            if (_doc.IsLocked(p) || _catalog.DoorToggle(p.DefName) is not { } peer) continue;
            var swapped = new Placement { DefName = peer, X = p.X, Y = p.Y, Rot = p.Rot };
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
        _clip = selected.Select(p => (p.DefName, p.X - minX, p.Y - minY, p.Rot)).ToList();
    }

    /// <summary>Paste the clipboard at the hovered tile (else just off the original), selecting the copies.</summary>
    private void PasteClipboard()
    {
        if (_doc is null || _clip.Count == 0) return;
        var anchor = _hoverCell ?? (_clipOrigin.X + 1, _clipOrigin.Y + 1);
        var clones = _clip
            .Select(c => new Placement { DefName = c.Def, X = anchor.X + c.X, Y = anchor.Y + c.Y, Rot = c.Rot })
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

        // "Use as brush" (the eyedropper — formerly double-click): arm the part this menu is about,
        // if it is buildable. Uses the lone selected part, else the topmost part on the tile.
        var brushPart = selected.Count == 1 ? selected[0] : stack[0];
        var brushDef = _allParts.Any(v => v.Part.DefName == brushPart.DefName) ? brushPart.DefName : null;

        // "Replace with…": enabled when the whole (unlocked) selection shares one render layer +
        // footprint and at least one buildable part of that same kind exists to swap in.
        var canReplace = unlocked.Count > 0
            && ReplaceOps.CommonClass(_doc, unlocked) is { } rcls
            && ReplaceOps.CompatibleTargets(_catalog!, rcls).Count > 0;

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
            menu.Items.Add(Item("Use as brush", "", (_, _) => OnArmFromTile(brushDef)));
        if (canReplace)
            menu.Items.Add(Item("Replace with…" + suffix, "", (_, _) => ReplaceSelection()));
        menu.Items.Add(Item("Duplicate" + suffix, "Ctrl+D", (_, _) => DuplicateSelection(), canAct));
        menu.Items.Add(Item("Copy" + suffix, "Ctrl+C", (_, _) => CopySelection(), canAct));
        menu.Items.Add(Item("Paste", "Ctrl+V", (_, _) => PasteClipboard(), _clip.Count > 0));
        menu.Items.Add(Item("Rotate CW" + suffix, "R", (_, _) => RotateSelection(90), canRotate));
        menu.Items.Add(Item("Rotate CCW" + suffix, "Shift+R", (_, _) => RotateSelection(-90), canRotate));
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

    /// <summary>Friendly name for a render layer, for the context-menu layer filter.</summary>
    private static string LayerName(int layer) => layer switch
    {
        Catalog.LayerFloor => "Floors",
        Catalog.LayerWall => "Walls & doors",
        Catalog.LayerConduit => "Conduits",
        _ => "Fixtures",
    };

    // ---- input ----

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBoxBase) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        switch (e.Key)
        {
            case Key.R when !ctrl:   // same key as the game's build mode
                if (Board.ArmedPart is not null) Board.RotateArmed(shift ? -90 : 90);
                else RotateSelection(shift ? -90 : 90);
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
            case Key.Escape:
                if (Board.ActiveZoneId is not null)
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
            case Key.Z when ctrl:
                if (_doc is not null) _stack.Undo(_doc);
                e.Handled = true;
                break;
            case Key.Y when ctrl:
                if (_doc is not null) _stack.Redo(_doc);
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
            case Key.W or Key.A or Key.S or Key.D when !ctrl:
                Board.SetPanKey(e.Key, true);   // smooth per-frame pan until KeyUp
                e.Handled = true;
                break;
            case Key.E when !ctrl && !e.IsRepeat:   // rotate the view, like in-game
                Board.RotateView(90);
                e.Handled = true;
                break;
            case Key.Q when !ctrl && !e.IsRepeat:
                Board.RotateView(-90);
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
    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_doc is null || _catalog is null || _index is null || _env is null) return;
        if (_doc.Placements.Count == 0)
        {
            Dlg.Show(this, "Place some parts before exporting.", "Export",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new ExportDialog(_meta.Name, _settings.ExportAuthor ?? _meta.Author, _env.ModsDir, _settings.LastExportDir) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        // don't silently clobber an existing mod folder we may not have created
        var targetDir = Path.Combine(dlg.DestinationParent, ShipExport.SanitizeName(dlg.ShipName));
        if (Directory.Exists(targetDir) && Directory.EnumerateFileSystemEntries(targetDir).Any()
            && !Dlg.Confirm(this, DlgKind.Warning, "Folder already exists",
                $"A folder named \"{Path.GetFileName(targetDir)}\" already exists at:\n{Path.GetDirectoryName(targetDir)}\n\n" +
                "Overwriting replaces its mod_info.json and ship file. Other files in the folder are left untouched.",
                "Overwrite"))
            return;

        _roomSpecs ??= RoomCertifier.LoadSpecs(_index);
        var (doc, catalog, specs) = (_doc, _catalog, _roomSpecs);
        var opts = new ExportOptions(dlg.ShipName, dlg.Author, dlg.Notes, dlg.ModVersion,
            _env.InstalledVersion ?? GameEnv.VerifiedGameVersion, dlg.DestinationParent);

        ExportResult result;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            result = await Task.Run(() => ShipExport.Write(doc, catalog, specs, opts));
        }
        catch (Exception ex)
        {
            Dlg.Show(this, "Export failed:\n\n" + ex.Message, "Export", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        _settings.ExportAuthor = dlg.Author;
        if (!dlg.StagedIntoMods) _settings.LastExportDir = dlg.DestinationParent;
        _settings.Save();
        AuditLog.Add($"Exported mod \"{dlg.ShipName}\" to {result.ModDir}.");

        var registerNote = dlg.StagedIntoMods
            ? "It's staged into the game's Mods folder.\n" +
              "Register it with Ostrasort (or ModTools) before it appears in game.\n" +
              "Ostraplan never writes loading_order.json itself."
            : "Copy this folder into Ostranauts_Data\\Mods.\n" +
              "Then register it with Ostrasort (or ModTools) to spawn it in game.";
        Dlg.Success(this, "Export complete",
            $"Exported {dlg.ShipName}.\n\n" +
            $"{result.PartCount} parts, {result.RoomCount} certified room(s), rating {(string.IsNullOrEmpty(result.Rating.Display) ? "None" : result.Rating.Display)}.\n\n" +
            $"Written to {result.ModDir}\n\n" +
            registerNote);
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

    /// <summary>The Import menu: start a design from an existing ship (template now; save game in P3 slice 3).</summary>
    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = BtnImport, Placement = PlacementMode.Bottom };
        var fromTemplate = new MenuItem { Header = "From ship template…" };
        fromTemplate.Click += (_, _) => ImportTemplate();
        var fromSave = new MenuItem { Header = "From save game (layout only)…" };
        fromSave.Click += (_, _) => ImportSave();
        var forEditing = new MenuItem { Header = "Your ship, for editing (write back to the save)…" };
        forEditing.Click += (_, _) => ImportSaveForEditing();
        menu.Items.Add(fromTemplate);
        menu.Items.Add(fromSave);
        menu.Items.Add(new Separator());
        menu.Items.Add(forEditing);
        menu.IsOpen = true;
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
            result = await Task.Run(() => SaveImport.ImportPlayerShip(zip, catalog));
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
            edit = await Task.Run(() => SaveEditImport.ImportForEditing(entry, reg, catalog));
        }
        catch (Exception ex)
        {
            Dlg.Show(this, "Import failed:\n\n" + ex.Message, "Import", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally { Mouse.OverrideCursor = null; }

        InstallImportedDocument(edit.Import, edit.Context);
        AuditLog.Add($"Imported ship \"{chosen.Name}\" ({chosen.RegId}) for editing from save \"{save.Name}\".");
    }

    /// <summary>Write the edited ship back into a COPY of the save it came from (crew/cargo preserved, original
    /// untouched). Enabled only for a save-derived design; re-locates the context on demand for a reopened .oplan.</summary>
    private async void OnUpdateSaveClick(object sender, RoutedEventArgs e)
    {
        if (_doc is null || _catalog is null || _index is null || _env is null) return;
        if (_doc.SourceSave is not { } src)
        {
            Dlg.Show(this, "This design wasn't imported from a save. Use Import ▸ \"Your ship, for editing\" first.",
                "Update ship in save", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // resolve the context: the in-session one, or re-locate it from the source save (a reopened .oplan)
        var ctx = _saveContext;
        if (ctx is null)
        {
            var match = SaveImport.ListSaves(_env).FirstOrDefault(s => string.Equals(s.Name, src.SaveName, StringComparison.Ordinal));
            if (match is null)
            {
                Dlg.Show(this,
                    $"The source save \"{src.SaveName}\" is no longer in your Saves folder, so this design can't be " +
                    "written back. You can still Export it as a spawnable mod.",
                    "Update ship in save", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var (catalog0, zip0, name0, reg0) = (_catalog, match.ZipPath, match.Name, src.RegId);
            Mouse.OverrideCursor = Cursors.Wait;
            try { ctx = await Task.Run(() => SaveEditImport.RelocateContext(zip0, name0, reg0, catalog0)); }
            catch (Exception ex)
            {
                Dlg.Show(this, "Couldn't re-locate the ship in that save:\n\n" + ex.Message +
                    "\n\nYou can still Export it as a mod.", "Update ship in save", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            finally { Mouse.OverrideCursor = null; }
            _saveContext = ctx;   // cache for further writes this session
        }

        _roomSpecs ??= RoomCertifier.LoadSpecs(_index);
        var (doc, catalog, specs, context) = (_doc, _catalog, _roomSpecs, ctx);

        // options: write target (copy vs in place) + opt-in cost deduction. The diff (counts + base cost) is
        // cheap; the current balance comes from the player CO on this ship (null -> deduction unavailable).
        var diff = ShipDiff.Compute(doc, context);
        var baseCost = EditCost.Compute(diff, catalog, 1.0);
        var balance = SaveEdit.CurrentBalance(context);
        var opts = new UpdateSaveDialog(src.SaveName, diff.KeptCount, diff.MovedCount, diff.NewCount, diff.DeletedCount,
            baseCost, balance) { Owner = this };
        if (opts.ShowDialog() != true) return;

        var charge = opts.Deduct && context.PlayerCoId is { } coId && opts.ResultingBalance is { } newBal
            ? new EditCharge(coId, opts.Cost, newBal)
            : null;

        // build the injected ship off-thread (runs the room/rating engine); a hard integrity failure surfaces here
        JsonObject shipObj;
        InjectReport report;
        Mouse.OverrideCursor = Cursors.Wait;
        try { (shipObj, report) = await Task.Run(() => SaveEdit.BuildInjectedShip(doc, context, catalog, specs, charge)); }
        catch (Exception ex)
        {
            Dlg.Error(this, "Update ship in save", "The edit can't be written back.\n\n" + ex.Message);
            return;
        }
        finally { Mouse.OverrideCursor = null; }

        // loud cargo-loss warning: deleting a container that still holds cargo drops it
        if (report.CargoDropped.Count > 0 && !ConfirmCargoLoss(report.CargoDropped)) return;

        var summary = $"{report.Kept} kept, {report.Moved} moved, {report.Added} added, {report.Deleted} deleted.";
        var costNote = report.Charged is { } c
            ? $"\n\n{Money(c)} was deducted. Your balance is now {Money(report.ResultingBalance ?? 0)}."
            : "";
        var atmoNote = "\n\nThe ship refills with breathable atmosphere when you load it (about 22 kPa O₂ and 80 kPa N₂).";
        var powerNote = report.PowerFixed > 0
            ? $"\n\nRearmed {report.PowerFixed} powered device(s) that had lost their power ticker."
            : "";
        var warn = report.Warnings.Count > 0
            ? $"\n\n{report.Warnings.Count} placement law warning(s). The ship is still written, so load it to check.\n\n" +
              string.Join("\n", report.Warnings.Take(6).Select(w => "   • " + w))
            : "";

        string writtenName;
        string? backupName = null;
        try
        {
            if (opts.InPlace)
            {
                if (!ConfirmInPlace(src.SaveName, opts.Backup)) return;
                var backupPath = SaveEdit.WriteInPlace(context, shipObj, report.ResultingBalance, opts.Backup);
                backupName = backupPath is null ? null : Path.GetFileName(backupPath);
                writtenName = src.SaveName;
            }
            else
            {
                var outDir = SaveEdit.SuggestCopyDir(context);
                if (!Dlg.Confirm(this, DlgKind.Warning, $"Write a copy of \"{src.SaveName}\"?",
                        $"{summary}{costNote}{atmoNote}{powerNote}{warn}\n\n" +
                        $"The copy will be named {Path.GetFileName(outDir)}.\n\n" +
                        "Your original save is not touched.",
                        "Write copy"))
                    return;
                SaveEdit.WriteCopy(context, shipObj, outDir, overwrite: false, report.ResultingBalance);
                writtenName = Path.GetFileName(outDir);
            }
        }
        catch (Exception ex)
        {
            Dlg.Error(this, "Update ship in save", "Writing the save failed.\n\n" + ex.Message);
            return;
        }
        AuditLog.Add($"Updated ship in save — wrote \"{writtenName}\".");

        var backup = opts.InPlace
            ? (backupName is not null
                ? $"\n\nYour original save was backed up first, as a separate save named {backupName}.\n" +
                  "It sits beside this save in your Saves folder, not inside it, so deleting the edited save won't remove it.\n" +
                  "Load that backup in game if you ever need to recover."
                : "\n\nNo backup was made (you unticked it), so this overwrote the original save in place.")
            : "\n\nYour original save is unchanged.";
        Dlg.Success(this, "Ship updated",
            $"Written to the save {writtenName}.\n\n" +
            $"{summary}{costNote}{atmoNote}{powerNote}\n\n" +
            "Open the in game Load menu and press Refresh first.\n" +
            "Ostranauts won't list a just written save until you do.\n" +
            $"Then load {writtenName} to see your edited ship, with crew and cargo intact." +
            backup);
    }

    private static string Money(double v) => "$" + v.ToString("#,##0.##", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The stern gate before editing a ship the player doesn't own (a station or another vessel).</summary>
    private bool ConfirmUnsupportedShip(SaveShipChoice c) =>
        Dlg.Confirm(this, DlgKind.Danger, "This isn't your ship",
            $"{c.Name} ({c.RegId}) is a station or another vessel, not one of your ships.\n\n" +
            "Editing something you don't own is not supported, and it can corrupt or break your save.\n" +
            "Ostraplan can't guarantee a valid result, and takes no responsibility for the outcome. You do.",
            "Edit it anyway");

    /// <summary>The loud in-place confirmation. Detects a running Ostranauts and gates on the user confirming
    /// they're at the Main Menu (editing a loaded save would be clobbered by the next autosave).</summary>
    private bool ConfirmInPlace(string saveName, bool backup)
    {
        var running = System.Diagnostics.Process.GetProcessesByName("Ostranauts").Length > 0;
        var gameWarn = running
            ? "Ostranauts is running.\n" +
              "Editing in place is only safe from the Main Menu.\n" +
              "If this save is loaded, the game will overwrite your edit on its next autosave.\n\n" +
              "Confirm you are at the Main Menu, not in your loaded game, before continuing.\n\n"
            : "";
        var backupLine = backup
            ? "Ostraplan first copies this save to a separate backup save in your Saves folder, beside this one, not inside it.\n" +
              "Then it writes your edit into the original save, replacing it.\n" +
              "If the edit goes wrong, load the backup to recover."
            : "You unticked the backup, so this writes straight into the original save, replacing it.\n" +
              "There will be no backup to roll back to if the edit goes wrong.";
        return Dlg.Confirm(this, DlgKind.Danger, $"Overwrite {saveName} in place?",
            $"{gameWarn}{backupLine}",
            "Overwrite in place");
    }

    /// <summary>The loud, explicit confirmation before an inject drops cargo from deleted containers.</summary>
    private bool ConfirmCargoLoss(IReadOnlyList<CargoLoss> losses)
    {
        var lines = losses.Take(8).Select(l =>
            $"   • {l.ContainerName} ({string.Join(", ", l.Items.Take(6))}{(l.Items.Count > 6 ? $", plus {l.Items.Count - 6} more" : "")})");
        var total = losses.Sum(l => l.Items.Count);
        return Dlg.Confirm(this, DlgKind.Danger, "Cargo will be permanently deleted",
            $"You deleted {losses.Count} container(s) that still hold {total} cargo item(s).\n" +
            "Writing this back will permanently delete that cargo.\n\n" + string.Join("\n", lines) +
            "\n\nTo keep it, cancel now.\n" +
            "Empty those containers in game, then import and edit again.",
            "Delete cargo & continue");
    }

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
            result = await Task.Run(() => TemplateImport.LoadFile(path, catalog));
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
        _saveContext = context;
        _unresolvedParts = [];   // a fresh import is a complete, saveable design (unlike a reopened .oplan missing its mods)
        _stack.Reset();
        Board.SetDocument(_doc);
        Board.FitContent();
        OnDocChanged();
        UpdateInspector();
        UpdateSaveEditUi();
        ReportImport(result, keptContents: context is not null);
    }

    /// <summary>Tell the user about anything the import dropped (contained cargo, unresolved defs). Silent on a
    /// clean import. <paramref name="keptContents"/> is true for a save import FOR EDITING, where contained cargo
    /// is preserved as viewable container contents rather than discarded (a layout-only / template import drops it).</summary>
    private void ReportImport(ImportResult result, bool keptContents)
    {
        var notes = new List<string>();
        if (result.ContainedDropped > 0)
            notes.Add(keptContents
                ? $"{result.ContainedDropped} contained item(s) (cargo, tools, installed modules) were kept as container contents.\n" +
                  "Right-click a container and choose \"View contents\" to see them. They aren't placed on the grid as buildable structure."
                : $"{result.ContainedDropped} contained item(s) were dropped (cargo, tools, installed modules).\nOstraplan imports the layout only.");
        if (result.SystemDropped > 0)
            notes.Add($"{result.SystemDropped} loot spawner and system object(s) were dropped.\nThey populate the ship at runtime, and aren't buildable structure.");
        if (result.Skipped.Count > 0)
        {
            var names = string.Join("\n", result.Skipped.Take(12).Select(s => s.Count > 1 ? $"   • {s.DefName} (x{s.Count})" : $"   • {s.DefName}"));
            var more = result.Skipped.Count > 12 ? $"\n   …and {result.Skipped.Count - 12} more" : "";
            notes.Add($"{result.Skipped.Sum(s => s.Count)} tile(s) referenced {result.Skipped.Count} def(s) that aren't in your loaded data, and were skipped.\n\n{names}{more}\n\n" +
                      "Enable the mods this ship needs, and import again for a complete layout.");
        }
        if (notes.Count == 0) return;   // clean import, the ship now on the canvas is feedback enough
        var report = $"Imported {result.ShipName}, {result.PartCount} parts.\n\n" + string.Join("\n\n", notes);
        if (result.Skipped.Count > 0) Dlg.Warn(this, "Import", report);
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

    private void OnFitClick(object sender, RoutedEventArgs e) => Board.FitContent();
    private void OnSymClick(object sender, RoutedEventArgs e) => Board.CycleSymmetry();
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
        if (SelfInstall.CanOfferInstall() || SelfInstall.IsInstalled())
        {
            menu.Items.Add(new Separator());
            Add(SelfInstall.IsInstalled() ? "Create shortcuts…" : "Install Ostraplan / shortcuts…",
                () => RunInstall(SelfInstall.IsInstalled()));
        }
        menu.Items.Add(new Separator());
        Add("Report a Bug…", ReportBug);
        menu.Items.Add(new Separator());
        Add("View Activity Log", ViewLogs);
        Add("Open Log Folder", OpenLogFolder);
        Add("Clear Activity Log…", ClearLogs);
        menu.IsOpen = true;
    }

    // ---- self-install ----

    /// <summary>
    /// One-time, dismissible first-run offer to install the exe to a fixed per-user location + shortcuts.
    /// Fires once content has rendered, and only when running from somewhere other than the install
    /// location (so a dev/dotnet-run build, or the already-installed copy, never prompts). The Help menu
    /// keeps an "Install / shortcuts" entry for later.
    /// </summary>
    private void OnContentRendered(object? sender, EventArgs e)
    {
        if (_settings.InstallPromptDismissed || !SelfInstall.CanOfferInstall()) return;
        _settings.InstallPromptDismissed = true;   // ask once, never nag — the Help-menu entry stays for later
        _settings.Save();
        RunInstall(alreadyInstalled: false);
    }

    /// <summary>Shows the install prompt and performs the chosen install + shortcut creation.</summary>
    private void RunInstall(bool alreadyInstalled)
    {
        if (InstallDialog.Show(this, alreadyInstalled) is not { } c) return;
        try
        {
            var result = SelfInstall.Install(c.Desktop, c.StartMenu);
            AuditLog.Add(result.Copied
                ? $"Installed Ostraplan to {result.ExePath}."
                : $"Ostraplan already installed at {result.ExePath}.");
            foreach (var s in result.Shortcuts) AuditLog.Add($"Created shortcut: {s}");

            var shortcuts = result.Shortcuts.Count > 0
                ? "Shortcuts created:\n" + string.Join("\n", result.Shortcuts.Select(s => "• " + s))
                : "No shortcuts were created.";
            Dlg.Success(this, "Ostraplan",
                (result.Copied ? $"Ostraplan was installed to:\n{result.ExePath}\n\n" : $"Using the installed copy at:\n{result.ExePath}\n\n") +
                shortcuts +
                "\n\nLaunch Ostraplan from the shortcut or that folder from now on, so updates land in one place.");
        }
        catch (Exception ex)
        {
            Dlg.Error(this, "Install failed", ex.Message);
        }
    }

    // ---- report a bug ----

    private const int MaxIssueUrl = 7000;   // GitHub won't accept issue URLs much beyond this

    /// <summary>
    /// Open a pre-filled GitHub issue for Ostraplan in the browser: a short template (what were you
    /// doing / what went wrong / repro / screenshots) plus auto-diagnostics, and — since the trail is
    /// already scrubbed of usernames and paths — this session's recent activity-log lines folded into a
    /// collapsible block. Falls back to the clipboard for the trail if the URL would get too long for GitHub.
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
                $"- Ostraplan: v{AppVersion}\n" +
                $"- OS: {DescribeOs()}\n" +
                $"- Game: {_env?.InstalledVersion ?? "unknown"} (Law verified against {GameEnv.VerifiedGameVersion})\n" +
                $"- Design: {DescribeDocument()}\n";

            var recent = AuditLog.Recent(25);
            var body = prompt;
            var clipboardFallback = false;
            if (recent.Count > 0)
            {
                var trail = "\n<details>\n<summary>Recent actions (from Ostraplan's activity log)</summary>\n\n```\n"
                            + string.Join("\n", recent) + "\n```\n</details>\n";
                if (IssueUrl(prompt + trail).Length <= MaxIssueUrl)
                    body = prompt + trail;
                else
                {
                    Clipboard.SetText(string.Join(Environment.NewLine, recent));
                    body = prompt + "\n*My recent actions are on the clipboard — paste them below.*\n\n";
                    clipboardFallback = true;
                }
            }

            OpenUrl(IssueUrl(body));
            AuditLog.Add("Opened a pre-filled GitHub bug report" +
                         (clipboardFallback ? " (recent actions copied to the clipboard)." : "."));
            if (clipboardFallback)
                Dlg.Info(this, "Report a bug",
                    "Your recent actions were too long to pre-fill automatically, so they're on your clipboard.\n\n" +
                    "In the GitHub issue that just opened, click into the description and paste them (Ctrl+V) under the diagnostics.");
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

    // ---- update check (mirrors Ostrasort) ----

    private static readonly HttpClient Http = CreateHttp();
    private static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Ostraplan");
        return c;
    }

    /// <summary>This build's version, from the assembly's informational version (git hash stripped).</summary>
    private static string AppVersion =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? "0.0";

    /// <summary>
    /// Compare this build against the latest GitHub release. Runs on every launch (queried live, so a
    /// release published after this build is picked up next start) and on demand from the Help window.
    /// A newer release reveals the toolbar Update button and raises a modal (Dlg.Confirm) offering to
    /// Download Latest Version or dismiss with Not Now - shown on every launch while behind, not only
    /// on the manual check; the manual run additionally reports when you are already up to date.
    /// Ostraplan's repo is private until the public flip, so until then the check finds nothing (the
    /// failure is swallowed).
    /// </summary>
    private async Task CheckForUpdateAsync(bool manual = false)
    {
        try
        {
            var json = await Http.GetStringAsync("https://api.github.com/repos/Valtora/Ostraplan/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() : null;
            if (ParseVersion(tag) > ParseVersion(AppVersion))
            {
                _updateUrl = url ?? ReleasesUrl;
                BtnUpdate.Content = $"⬆  Update: {tag}";
                BtnUpdate.Visibility = Visibility.Visible;
                // A newer release always raises the modal (every launch), not only on the manual
                // check - the toolbar button stays as the persistent affordance after "Not Now".
                // "Download Latest Version" opens the release page.
                if (Dlg.Confirm(this, DlgKind.Info, "Update available",
                        $"{tag} is available to download.\nYou're on v{AppVersion}.",
                        "Download Latest Version", "Not Now"))
                    OpenUrl(_updateUrl);
            }
            else if (manual)
            {
                Dlg.Info(this, "Ostraplan", $"You're on the latest version (v{AppVersion}).");
            }
        }
        catch (Exception ex)
        {
            if (manual)
                Dlg.Warn(this, "Ostraplan", "Couldn't check for updates.\n\n" + ex.Message +
                    "\n\nYou may be offline, or GitHub may be rate limiting.\n" +
                    "Its anonymous API allows about 60 checks an hour per network.");
        }
    }

    private static Version ParseVersion(string s) =>
        Version.TryParse(s.TrimStart('v', 'V').Split('+', '-')[0], out var v) ? v : new Version(0, 0);

    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private void OnUpdateClick(object sender, RoutedEventArgs e) => OpenUrl(_updateUrl);

    // ---- help ----

    private void ShowHelp()
    {
        (string Func, string Keys, string Note)[] rows =
        [
            ("Place / paint", "LMB", "With a part armed: place it; keep dragging to paint along the cursor."),
            ("Box fill", "Shift + drag", "With a part armed: rubber-band a box and fill it with the part."),
            ("Hollow box", "Ctrl + Shift + drag", "With a part armed: place only the outline — walls, in practice."),
            ("Select", "LMB", "Select a part. Ctrl+click adds/removes; drag empty space to box-select."),
            ("Flood-select", "Double-click", "Select every touching tile of the same type (bulk delete or re-skin). Ctrl+double-click adds the region."),
            ("Move", "Drag selection", "Move the selected parts."),
            ("Context menu", "RMB", "Use as brush · Replace with… · Make Loose Item / Install item · pick a buried layer on stacked tiles · Select only (after a box-select) · Close/Open door. Also cancels placement while armed."),
            ("Rotate part", "R / Shift+R", "CW / CCW — the armed part, a selected part in place, or a whole selection about its centre (walls & floors auto-tile rather than turn)."),
            ("Symmetry", "M", "Cycle Off → Vertical → Horizontal → Both; axes centre on the hovered tile when switching on."),
            ("Delete", "Del", "Delete the selection."),
            ("Copy / paste / duplicate", "Ctrl+C / V / D", "Copy · paste at the cursor · duplicate the selection."),
            ("Cancel", "Esc", "Cancel placement, then clear the selection."),
            ("Pan", "W A S D", "Pan the view (smooth while held)."),
            ("Pan (mouse)", "MMB / Space + drag", "Pan the view by dragging."),
            ("Rotate view", "Q / E", "Rotate the plan view CCW / CW, like the in-game camera."),
            ("Zoom", "Mouse wheel", "Zoom, anchored at the cursor."),
            ("Fit to ship", "F", "Fit the view to the whole ship."),
            ("Undo / redo", "Ctrl+Z / Ctrl+Y", "Undo · redo."),
            ("New / open / save", "Ctrl+N / O / S", "New · open · save (Ctrl+Shift+S = Save As)."),
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
