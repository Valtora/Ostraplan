using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Ostraplan.Core;

namespace Ostraplan.App;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
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


    public MainWindow()
    {
        InitializeComponent();

        Board.StrokeCommitted += OnStrokeCommitted;
        Board.MoveRequested += OnMoveRequested;
        Board.SymmetryChanged += () => BtnSym.Content = "Sym: " + Board.SymMode switch
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
            TxtVersion.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xA3, 0x4E));
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
        if (_doc is not null && _catalog is not null)
            UpdateProblems(ProblemScan.Scan(_doc, _catalog));
        RefreshChrome();
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
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0xC9, 0x8A)),
            });
            ProblemsPanel.Children.Add(new TextBlock
            {
                Text = "Placement law is enforced live. Room, airtightness and certification checks arrive with the P2 law milestone.",
                Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x7E, 0x88)),
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
                Foreground = new SolidColorBrush(problem.Severity == ProblemSeverity.Blocking
                    ? Color.FromRgb(0xE0, 0x5B, 0x5B)
                    : Color.FromRgb(0xE0, 0xA3, 0x4E)),
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
        var commands = Board.SelectedPlacements()
            .Where(p => !_doc.IsLocked(p) && _doc.Part(p)?.Item.HasSpriteSheet != true)   // walls/floors auto-tile; the primary is fixed
            .Select(p => (IDocCommand)new RotateCommand(_doc, p, delta))
            .ToList();
        if (commands.Count == 0) return;
        _stack.Push(_doc, commands.Count == 1 ? commands[0] : new CompositeCommand(commands));
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

        if (stack.Count > 1)
        {
            // layer picker — floors sit under what's on them, so this is how you reach
            // the part underneath. Click a row to select that layer (● marks the current one).
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

        // actions on the current selection (the layer picker above re-points it)
        var selected = Board.SelectedPlacements();
        var unlocked = selected.Where(p => !_doc.IsLocked(p)).ToList();
        var canAct = unlocked.Count > 0;
        var canRotate = unlocked.Any(p => _doc.Part(p)?.Item.HasSpriteSheet != true);
        var suffix = unlocked.Count > 1 ? $" ({unlocked.Count})" : "";

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Duplicate" + suffix, "Ctrl+D", (_, _) => DuplicateSelection(), canAct));
        menu.Items.Add(Item("Copy" + suffix, "Ctrl+C", (_, _) => CopySelection(), canAct));
        menu.Items.Add(Item("Paste", "Ctrl+V", (_, _) => PasteClipboard(), _clip.Count > 0));
        menu.Items.Add(Item("Rotate CW" + suffix, "R", (_, _) => RotateSelection(90), canRotate));
        menu.Items.Add(Item("Rotate CCW" + suffix, "Shift+R", (_, _) => RotateSelection(-90), canRotate));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Delete" + suffix, "Del", (_, _) => DeleteSelection(), canAct));
        menu.IsOpen = true;
    }

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

    // ---- help ----

    private void ShowHelp()
    {
        (string Keys, string Does)[] rows =
        [
            ("LMB (part armed)", "Place the part; keep dragging to paint it along the cursor"),
            ("Shift + drag (part armed)", "Rubber-band a box and fill it with the part"),
            ("Ctrl + Shift + drag (part armed)", "Hollow box: only the outline is placed — walls, in practice"),
            ("LMB", "Select a part · Ctrl+click adds/removes · drag empty space to box-select"),
            ("Drag selection", "Move the selected parts"),
            ("RMB", "Context menu (Duplicate, Rotate, Delete) · on stacked tiles, lists every layer so you can select the part underneath · cancels placement while armed"),
            ("R / Shift+R", "Rotate the armed part or the selection CW / CCW (walls & floors auto-tile instead)"),
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
