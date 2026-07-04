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

    // the only player-buildable docking port (DockSys03ClosedInstall, HULL tab);
    // ships need >=1 installed docksys to ever dock, so new designs start with one
    private const string SeedDocksysDef = "ItmDockSys03Closed";

    public MainWindow()
    {
        InitializeComponent();

        Board.PlaceRequested += OnPlaceRequested;
        Board.MoveRequested += OnMoveRequested;
        Board.SelectionChanged += UpdateInspector;
        Board.HoverChanged += cell => TxtCell.Text = cell is { } c ? $"tile {c.X}, {c.Y}" : "—";
        Board.ViewChanged += () => TxtZoom.Text = $"zoom {Board.Zoom / 16:0.#}×";
        Board.Disarmed += ClearPaletteSelection;
        Board.ContextMenuRequested += OnContextMenuRequested;
        _stack.StateChanged += RefreshChrome;

        PreviewKeyDown += OnPreviewKeyDown;
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

        TxtZoom.Text = $"zoom {Board.Zoom / 16:0.#}×";
        LoadingOverlay.Visibility = Visibility.Collapsed;
    }

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
        // seed the docking port at the origin (movable; outside the undo stack so
        // a fresh document is not dirty and cannot be undone into nothing)
        if (_catalog.ByDefName.ContainsKey(SeedDocksysDef))
            new PlaceCommand(new Placement { DefName = SeedDocksysDef, X = 0, Y = 0 }).Do(_doc);
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
        TxtDockWarn.Text = HasDocksys() ? "" : "⚠ no docking port — ship can't dock";
        RefreshChrome();
    }

    /// <summary>Ship.aDocksys collects installed COs triggering TIsDockSysInstalled; no port = can never dock.</summary>
    private bool HasDocksys()
    {
        if (_doc is null || _catalog is null) return false;
        string[] reqs = _catalog.Triggers.TryGetValue("TIsDockSysInstalled", out var ct) && ct.Reqs.Length > 0
            ? ct.Reqs
            : ["IsDockSysInstalled"];
        return _doc.Placements.Any(p => _doc.Part(p)?.StartingConds.Any(reqs.Contains) == true);
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

    private void OnPlaceRequested(PartDef part, int x, int y, int rot)
    {
        if (_doc is null) return;
        _stack.Push(_doc, new PlaceCommand(new Placement
        {
            DefName = part.DefName,
            X = x,
            Y = y,
            Rot = part.Item.HasSpriteSheet ? 0 : rot,
        }));
    }

    private void OnMoveRequested(IReadOnlyList<Placement> placements, int dx, int dy)
    {
        if (_doc is null || placements.Count == 0) return;
        _stack.Push(_doc, new MoveCommand(placements, dx, dy));
    }

    private void DeleteSelection()
    {
        if (_doc is null) return;
        var selected = Board.SelectedPlacements();
        if (selected.Count == 0) return;
        _stack.Push(_doc, new RemoveCommand(selected));
        Board.SelectedIds.Clear();
        UpdateInspector();
    }

    private void RotateSelection(int delta)
    {
        if (_doc is null) return;
        var commands = Board.SelectedPlacements()
            .Where(p => _doc.Part(p)?.Item.HasSpriteSheet != true)   // walls/floors don't rotate
            .Select(p => (IDocCommand)new RotateCommand(_doc, p, delta))
            .ToList();
        if (commands.Count == 0) return;
        _stack.Push(_doc, commands.Count == 1 ? commands[0] : new CompositeCommand(commands));
    }

    private void DuplicateSelection()
    {
        if (_doc is null) return;
        var selected = Board.SelectedPlacements();
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

    private void OnContextMenuRequested(Placement hit)
    {
        if (_doc is null) return;
        var selected = Board.SelectedPlacements();
        if (selected.Count == 0) return;
        var canRotate = selected.Any(p => _doc.Part(p)?.Item.HasSpriteSheet != true);
        var suffix = selected.Count > 1 ? $" ({selected.Count})" : "";

        static MenuItem Item(string header, string gesture, RoutedEventHandler onClick, bool enabled = true)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture, IsEnabled = enabled };
            item.Click += onClick;
            return item;
        }

        var menu = new ContextMenu { PlacementTarget = Board };
        menu.Items.Add(new MenuItem
        {
            Header = _doc.Part(hit)?.Friendly ?? hit.DefName,
            IsEnabled = false,
            FontWeight = FontWeights.SemiBold,
        });
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Duplicate" + suffix, "", (_, _) => DuplicateSelection()));
        menu.Items.Add(Item("Rotate CW" + suffix, "R", (_, _) => RotateSelection(90), canRotate));
        menu.Items.Add(Item("Rotate CCW" + suffix, "Shift+R", (_, _) => RotateSelection(-90), canRotate));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Delete" + suffix, "Del", (_, _) => DeleteSelection()));
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
            case Key.S when ctrl:
                if (shift) SaveAs();
                else Save();
                e.Handled = true;
                break;
            case Key.O when ctrl:
                OpenFile();
                e.Handled = true;
                break;
            case Key.N when ctrl:
                if (ConfirmDiscardChanges()) NewDocument();
                e.Handled = true;
                break;
            case Key.F when !ctrl:
                Board.FitContent();
                e.Handled = true;
                break;
        }
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
            InsTools.Text = "";
            return;
        }

        InsFriendly.Text = part.Friendly;
        InsInternal.Text = part.DefName;
        InsCategory.Text = part.Category;
        InsSize.Text = $"{part.Item.Width} × {part.Item.Height} tiles"
                       + (part.Item.HasSpriteSheet ? "  (auto-tiling)" : "");
        InsOrigin.Text = part.Origin;
        InsInputs.Text = part.Inputs.Length == 0 ? "none" : string.Join("\n", part.Inputs);
        InsTools.Text = part.Tools.Length == 0 ? "none" : string.Join("\n", part.Tools);
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
}
