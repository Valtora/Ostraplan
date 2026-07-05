using System.IO;
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
    private List<PartVM> _allParts = [];
    private readonly List<ListBox> _paletteLists = [];
    private ShipDocument? _doc;
    private OplanMeta _meta = new();
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
        Board.HoverChanged += cell => { _hoverCell = cell; TxtCell.Text = cell is { } c ? $"tile {c.X}, {c.Y}" : "—"; };
        Board.ViewChanged += UpdateZoomText;
        Board.Disarmed += ClearPaletteSelection;
        Board.ContextMenuRequested += OnContextMenuRequested;
        Board.GhostReasonChanged += reason => TxtGhost.Text = reason is null ? "" : "⛔ can't place here — " + reason;
        _stack.StateChanged += RefreshChrome;

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
                MessageBox.Show(this, ex.Message, "Ostraplan", MessageBoxButton.OK, MessageBoxImage.Warning);
                var dlg = new OpenFolderDialog { Title = "Pick the Ostranauts folder (inside steamapps\\common)" };
                if (dlg.ShowDialog(this) != true)
                {
                    Close();
                    return;
                }
                _settings.GameRootOverride = dlg.FolderName;
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
                return (idx, cat, spr, vms);
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not load game data:\n\n{ex.Message}", "Ostraplan",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }

        _index = index;
        _catalog = catalog;
        _allParts = parts;
        Board.Sprites = sprites;

        BuildPalette();
        NewDocument();

        var v = env.InstalledVersion ?? "unknown";
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
    }

    private void UpdateZoomText() =>
        TxtZoom.Text = $"zoom {Board.Zoom / 16:0.#}×" + (Board.ViewRot != 0 ? $" · view {Board.ViewRot}°" : "");

    // ---- palette ----

    private void BuildPalette()
    {
        Tabs.Items.Clear();
        _paletteLists.Clear();
        foreach (var category in new[] { "All" }.Concat(Catalog.Categories))
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
            list.ItemsSource = _allParts
                .Where(vm => (category is null || vm.Part.Category == category) && vm.Matches(search))
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

        Board.SetArmed(vm.Part);
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
        _stack.Reset();
        Board.SetDocument(_doc);
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
            MessageBox.Show(this, "Place some parts before running the Ship Rating.", "Ship Rating",
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
            MessageBox.Show(this, "Analysis failed: " + ex.Message, "Ship Rating", MessageBoxButton.OK, MessageBoxImage.Error);
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
            new RatingReportWindow(report, cells => Board.SetLeakCells(cells)) { Owner = this }.ShowDialog();
        }
    }

    // ---- Bill of materials ----

    private void OnMaterialsClick(object sender, RoutedEventArgs e)
    {
        if (_doc is null) return;
        if (_doc.Placements.Count == 0)
        {
            MessageBox.Show(this, "Place some parts before opening the bill of materials.", "Bill of Materials",
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
        {
            ProblemsPanel.Children.Add(new TextBlock
            {
                Text = "●  " + problem.Title,
                Foreground = problem.Severity == ProblemSeverity.Blocking ? ThemeManager.Bad : ThemeManager.Warn,
                ToolTip = new ToolTip { Content = new TextBlock { Text = problem.Detail, MaxWidth = 380, TextWrapping = TextWrapping.Wrap } },
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 1),
            });
        }
    }

    private void RefreshChrome()
    {
        BtnUndo.IsEnabled = _stack.CanUndo;
        BtnRedo.IsEnabled = _stack.CanRedo;
        var name = _doc?.FilePath is { } f ? Path.GetFileNameWithoutExtension(f) : _meta.Name;
        var star = _stack.Dirty ? " *" : "";
        TxtDoc.Text = name + star;
        Title = $"Ostraplan — {name}{star}";
    }

    private bool ConfirmDiscardChanges()
    {
        if (_doc is null || !_stack.Dirty) return true;
        var result = MessageBox.Show(this, $"Save changes to \"{TxtDoc.Text.TrimEnd(' ', '*')}\"?",
            "Ostraplan", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return result switch
        {
            MessageBoxResult.Yes => Save(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    private bool Save()
    {
        if (_doc is null || _index is null) return false;
        if (_doc.FilePath is null) return SaveAs();
        try
        {
            OplanFile.FromDocument(_doc, _index, _meta).Save(_doc.FilePath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        _stack.MarkSaved();
        _settings.Touch(_doc.FilePath);
        _settings.Save();
        return true;
    }

    private bool SaveAs()
    {
        if (_doc is null) return false;
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
            MessageBox.Show(this, ex.Message, "Open failed", MessageBoxButton.OK, MessageBoxImage.Error);
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
        _stack.Reset();
        Board.SetDocument(_doc);
        Board.FitContent();
        OnDocChanged();
        UpdateInspector();
        _settings.Touch(dlg.FileName);
        _settings.Save();

        if (missing.Count > 0)
        {
            var names = string.Join("\n", missing.Select(m => m.Def).Distinct().Take(12));
            MessageBox.Show(this,
                $"{missing.Count} placed part(s) reference defs that are not in the current game+mods data " +
                $"and were skipped:\n\n{names}\n\nEnable the mods this design depends on and reopen.",
                "Missing parts", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

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
        var selected = Board.SelectedPlacements().Where(p => !_doc.IsLocked(p)).ToList();
        if (selected.Count == 0) return;
        _stack.Push(_doc, new RemoveCommand(selected));
        Board.SelectedIds.Clear();
        UpdateInspector();
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
            MessageBox.Show(this, "Place some walls or floors before applying a theme.", "Apply theme",
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
                if (Board.ArmedPart is not null)
                {
                    Board.SetArmed(null);
                    ClearPaletteSelection();
                }
                else
                {
                    Board.SelectedIds.Clear();
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
                if (ConfirmDiscardChanges()) NewDocument();
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
                   ?? (selected.Count == 1 ? _doc?.Part(selected[0]) : null);

        if (part is null)
        {
            InsFriendly.Text = selected.Count > 1 ? $"{selected.Count} parts selected" : "—";
            InsInternal.Text = "";
            InsCategory.Text = "";
            InsSize.Text = "";
            InsOrigin.Text = "";
            InsInputs.Text = "";
            return;
        }

        var lockedNote = Board.ArmedPart is null && selected.Count == 1 && _doc?.IsLocked(selected[0]) == true
            ? "  · fixed to the ship"
            : "";
        InsFriendly.Text = part.Friendly + lockedNote;
        InsInternal.Text = part.DefName;
        InsCategory.Text = part.Category;
        InsSize.Text = $"{part.Item.Width} × {part.Item.Height} tiles"
                       + (part.Item.HasSpriteSheet ? "  (auto-tiling)" : "");
        InsOrigin.Text = part.Origin;
        InsInputs.Text = part.Inputs.Length == 0 ? "none" : string.Join("\n", part.Inputs);
    }

    // ---- toolbar ----

    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        if (ConfirmDiscardChanges()) NewDocument();
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
            MessageBox.Show(this, "Place some parts before exporting.", "Export",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new ExportDialog(_meta.Name, _settings.ExportAuthor ?? _meta.Author, _env.ModsDir, _settings.LastExportDir) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        // don't silently clobber an existing mod folder we may not have created
        var targetDir = Path.Combine(dlg.DestinationParent, ShipExport.SanitizeName(dlg.ShipName));
        if (Directory.Exists(targetDir) && Directory.EnumerateFileSystemEntries(targetDir).Any()
            && MessageBox.Show(this,
                $"A folder named \"{Path.GetFileName(targetDir)}\" already exists at:\n{Path.GetDirectoryName(targetDir)}\n\n" +
                "Overwrite its mod_info.json and ship file? Other files in the folder are left untouched.",
                "Export", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
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
            MessageBox.Show(this, "Export failed:\n\n" + ex.Message, "Export", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        _settings.ExportAuthor = dlg.Author;
        if (!dlg.StagedIntoMods) _settings.LastExportDir = dlg.DestinationParent;
        _settings.Save();

        var registerNote = dlg.StagedIntoMods
            ? "Staged into the game's Mods folder. Register it with Ostrasort (or ModTools) before it appears in-game — Ostraplan never writes loading_order.json."
            : "Copy this folder into Ostranauts_Data/Mods and register it with Ostrasort/ModTools to spawn it in-game.";
        MessageBox.Show(this,
            $"Exported \"{dlg.ShipName}\".\n\n" +
            $"{result.PartCount} parts · {result.RoomCount} certified room(s) · rating {(string.IsNullOrEmpty(result.Rating.Display) ? "None" : result.Rating.Display)}\n\n" +
            $"Written to:\n{result.ModDir}\n\n{registerNote}",
            "Export complete", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Save a PNG image of the ship (sprites only — no grid, overlays or UI) for sharing or reference.</summary>
    private void OnSnapshotClick(object sender, RoutedEventArgs e)
    {
        if (_doc is null || _doc.Placements.Count == 0)
        {
            MessageBox.Show(this, "Place some parts before taking a snapshot.", "Snapshot",
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
            MessageBox.Show(this, "Nothing to snapshot.", "Snapshot", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            using var fs = File.Create(dlg.FileName);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            encoder.Save(fs);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save the snapshot:\n\n" + ex.Message, "Snapshot",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>The Import menu: start a design from an existing ship (template now; save game in P3 slice 3).</summary>
    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = BtnImport, Placement = PlacementMode.Bottom };
        var fromTemplate = new MenuItem { Header = "From ship template…" };
        fromTemplate.Click += (_, _) => ImportTemplate();
        var fromSave = new MenuItem { Header = "From save game…" };
        fromSave.Click += (_, _) => ImportSave();
        menu.Items.Add(fromTemplate);
        menu.Items.Add(fromSave);
        menu.IsOpen = true;
    }

    /// <summary>Pick a save and import the player's ship from it — layout only, behind an explicit confirmation.</summary>
    private async void ImportSave()
    {
        if (_catalog is null || _env is null || !ConfirmDiscardChanges()) return;

        var saves = SaveImport.ListSaves(_env);
        if (saves.Count == 0)
        {
            MessageBox.Show(this, "No save games found in your Ostranauts Saves folder.", "Import",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var picker = new SavePickerDialog(saves) { Owner = this };
        if (picker.ShowDialog() != true || picker.Selected is not { } save) return;

        var ship = save.ShipName.Length > 0 ? $"\"{save.ShipName}\"" : "the player's ship";
        var who = save.PlayerName.Length > 0 ? $"{save.PlayerName}'s " : "";
        if (MessageBox.Show(this,
                $"Import {ship} from {who}save \"{save.Name}\"?\n\n" +
                "Ostraplan imports the ship's LAYOUT ONLY — crew, cargo, installed modules, wear and damage " +
                "are discarded, giving a pristine editable design.",
                "Import from save", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
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
            MessageBox.Show(this, "Import failed:\n\n" + ex.Message, "Import", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        InstallImportedDocument(result);
    }

    /// <summary>Browse core+mod ship templates and import the chosen one as a fresh design.</summary>
    private async void ImportTemplate()
    {
        if (_catalog is null || _index is null || !ConfirmDiscardChanges()) return;

        var ships = TemplateImport.ListShipFiles(_index);
        if (ships.Count == 0)
        {
            MessageBox.Show(this, "No ship templates found in the game data or your mods.", "Import",
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
            MessageBox.Show(this, "Import failed:\n\n" + ex.Message, "Import", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        InstallImportedDocument(result);
    }

    /// <summary>Swap an imported ship in as the active document (no file path — Save prompts Save As).</summary>
    private void InstallImportedDocument(ImportResult result)
    {
        if (_doc is not null) _doc.Changed -= OnDocChanged;
        _doc = result.Doc;
        _doc.FilePath = null;
        _doc.Changed += OnDocChanged;
        _meta = new OplanMeta { Name = result.ShipName };
        _stack.Reset();
        Board.SetDocument(_doc);
        Board.FitContent();
        OnDocChanged();
        UpdateInspector();
        ReportImport(result);
    }

    /// <summary>Tell the user about anything the import dropped (contained cargo, unresolved defs). Silent on a clean import.</summary>
    private void ReportImport(ImportResult result)
    {
        var notes = new List<string>();
        if (result.ContainedDropped > 0)
            notes.Add($"{result.ContainedDropped} contained item(s) — cargo, tools, installed modules — were dropped. Ostraplan imports the layout only.");
        if (result.SystemDropped > 0)
            notes.Add($"{result.SystemDropped} loot-spawner / system object(s) were dropped — they populate the ship at runtime and aren't buildable structure.");
        if (result.Skipped.Count > 0)
        {
            var names = string.Join("\n", result.Skipped.Take(12).Select(s => s.Count > 1 ? $"  {s.DefName} ×{s.Count}" : $"  {s.DefName}"));
            var more = result.Skipped.Count > 12 ? $"\n  …and {result.Skipped.Count - 12} more" : "";
            notes.Add($"{result.Skipped.Sum(s => s.Count)} tile(s) referenced {result.Skipped.Count} def(s) not in your loaded data and were skipped:\n{names}{more}\n\n" +
                      "Enable the mods this ship needs and re-import for a complete layout.");
        }
        if (notes.Count == 0) return;   // clean import — the ship now on the canvas is feedback enough
        MessageBox.Show(this,
            $"Imported \"{result.ShipName}\" — {result.PartCount} parts.\n\n" + string.Join("\n\n", notes),
            "Import", MessageBoxButton.OK, result.Skipped.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
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
    private void OnHelpClick(object sender, RoutedEventArgs e) => ShowHelp();

    /// <summary>Theme picker: apply and persist. DynamicResource + Fluent ThemeMode retint the chrome live.</summary>
    private void OnThemeModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_themeInit) return;
        var mode = CmbTheme.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" };
        _settings.Theme = mode;
        _settings.Save();
        ThemeManager.Apply(mode);
    }

    // The update button stays hidden until an update check finds a newer release (wired below);
    // clicking it opens the download page.
    private void OnUpdateClick(object sender, RoutedEventArgs e) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_updateUrl) { UseShellExecute = true });

    // ---- help ----

    private void ShowHelp()
    {
        (string Keys, string Does)[] rows =
        [
            ("LMB (part armed)", "Place the part; keep dragging to paint it along the cursor"),
            ("Shift + drag (part armed)", "Rubber-band a box and fill it with the part"),
            ("Ctrl + Shift + drag (part armed)", "Hollow box: only the outline is placed — walls, in practice"),
            ("LMB", "Select a part · Ctrl+click adds/removes · drag empty space to box-select"),
            ("Double-click a part", "Flood-select every touching tile of the same type (for bulk delete/replace) · Ctrl+double-click adds the region"),
            ("Drag selection", "Move the selected parts"),
            ("RMB", "Context menu · \"Use as brush\" arms the part to keep drawing it · \"Replace with…\" swaps a same-kind selection (floors for floors, walls for walls) for a compatible part · on stacked tiles lists every layer so you can select the part underneath · after a box-select, \"Select only\" narrows to one layer (e.g. just the walls) to delete · \"Close/Open door\" flips a door's state · cancels placement while armed"),
            ("R / Shift+R", "Rotate CW / CCW: the armed part, a single selected part in place, or a multi-part selection as a group about its centre (walls & floors move but auto-tile rather than turn)"),
            ("M", "Cycle symmetry Off → Vertical → Horizontal → Both; axes centre on the hovered tile when switching on"),
            ("Del", "Delete the selection"),
            ("Ctrl+C / Ctrl+V / Ctrl+D", "Copy / paste (at the cursor) / duplicate the selection"),
            ("Esc", "Cancel placement, then clear selection"),
            ("W A S D", "Pan the view (smooth while held)"),
            ("Q / E", "Rotate the view CCW / CW, like in-game"),
            ("MMB / Space + drag", "Pan the view"),
            ("Mouse wheel", "Zoom (anchored at the cursor)"),
            ("F", "Fit the view to the ship"),
            ("Ctrl+Z / Ctrl+Y", "Undo / redo"),
            ("Ctrl+N / Ctrl+O / Ctrl+S", "New / open / save (Ctrl+Shift+S = save as)"),
            ("F1", "This window"),
        ];

        var grid = new Grid { Margin = new Thickness(18) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var row = 0;
        foreach (var (keys, does) in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var k = new TextBlock
            {
                Text = keys,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD8, 0xA0, 0x3C)),
                Margin = new Thickness(0, 3, 18, 3),
            };
            var d = new TextBlock
            {
                Text = does,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD8, 0xDD, 0xE4)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 3),
                MaxWidth = 520,
            };
            Grid.SetRow(k, row);
            Grid.SetRow(d, row);
            Grid.SetColumn(d, 1);
            grid.Children.Add(k);
            grid.Children.Add(d);
            row++;
        }

        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var note = new TextBlock
        {
            Text = "The placement law is enforced: a part won't place where the game's own rules would refuse it. The ghost " +
                   "glows green when it fits and red when it can't, with the reason (e.g. \"needs a floor beneath\") in the " +
                   "status bar and the offending tiles tinted red. Moving or rotating a part into an illegal spot is allowed " +
                   "but flagged — red-tinted tiles and the PROBLEMS list name what broke. Every ship owns exactly one Primary " +
                   "Airlock, fixed at the 0,0 origin — the game neither sells nor removes it, so Ostraplan seeds it locked " +
                   "(no move/rotate/delete). Red-striped areas are out of bounds: no construction beyond an airlock's mating " +
                   "face. Wall and floor sprites connect automatically.",
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xAF)),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 660,
            Margin = new Thickness(0, 12, 0, 0),
        };
        Grid.SetRow(note, row);
        Grid.SetColumnSpan(note, 2);
        grid.Children.Add(note);

        new Window
        {
            Title = "Ostraplan — controls & keybinds",
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(0x23, 0x26, 0x2C)),
            Content = new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 640 },
        }.ShowDialog();
    }
}
