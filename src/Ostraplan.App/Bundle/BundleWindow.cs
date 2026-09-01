using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Ostraplan.App.Wizard;
using Ostraplan.Core;

namespace Ostraplan.App.Bundle;

/// <summary>
/// The Ship Bundle editor: several designs gathered into one mod, each configured on its own, exported together.
///
/// <para><b>Why it is not a fourth export destination.</b> The export wizard is built around the design that is
/// open: one <c>WizardSession</c>, one document, one set of answers. A pack is not about the open design at all,
/// and it outlives the sitting it was made in, which is what the <c>.oplanmod</c> is for. What the two do share is
/// the part that is genuinely the same question, <see cref="ObtainablePanel"/>, asked here once per ship.</para>
///
/// <para><b>Members are files.</b> Nothing here reads an open tab: see <see cref="BundleMember"/>.</para>
/// </summary>
public sealed class BundleWindow : Window
{
    private readonly Catalog _catalog;
    private readonly DataIndex _index;
    private readonly GameEnv _env;
    private readonly AppSettings _settings;
    private readonly IReadOnlyList<RoomSpecDef> _specs;
    private readonly SpriteCache? _sprites;
    private readonly Func<string, bool> _isOpenAndDirty;

    private readonly List<BundleMember> _members = [];
    private BundleFile _file = new();
    private string? _path;
    private bool _dirty;

    // ---- chrome ----
    private readonly TextBox _modName, _author, _version, _notes;
    private readonly CheckBox _exclusiveStart, _register, _replace;
    private readonly ListBox _list;
    private readonly ComboBox _replacePicker;
    private readonly TextBox _shipName;
    private readonly Border _wearHost, _memberPane;
    private readonly ObtainablePanel _obtainable = new();
    private readonly RadioButton _toMods, _toFolder;
    private readonly TextBlock _folderPath, _problem, _emptyHint;
    private readonly Button _remove, _export;
    private WearControl _wear;

    private BundleMember? _selected;
    private bool _populating;
    private string? _folder;

    public BundleWindow(
        Catalog catalog, DataIndex index, GameEnv env, AppSettings settings, IReadOnlyList<RoomSpecDef> specs,
        SpriteCache? sprites, Func<string, bool> isOpenAndDirty)
    {
        _catalog = catalog;
        _index = index;
        _env = env;
        _settings = settings;
        _specs = specs;
        _sprites = sprites;
        _isOpenAndDirty = isOpenAndDirty;

        Title = "Ship Bundle";
        Width = 1040;
        Height = 800;
        MinWidth = 900;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.WindowBg;

        // ---- the mod itself ----
        var modBox = new StackPanel { Margin = new Thickness(16, 12, 16, 4) };
        modBox.Children.Add(new TextBlock
        {
            Text = "The mod", Foreground = PaneUi.Ink, FontSize = 15, FontWeight = FontWeights.SemiBold,
        });

        var row = PaneUi.Add(modBox, new Grid { Margin = new Thickness(0, 6, 0, 0) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });

        var nameCol = new StackPanel();
        var authorCol = new StackPanel();
        var versionCol = new StackPanel();
        Grid.SetColumn(nameCol, 0); Grid.SetColumn(authorCol, 2); Grid.SetColumn(versionCol, 4);
        row.Children.Add(nameCol); row.Children.Add(authorCol); row.Children.Add(versionCol);

        _modName = PaneUi.Field(nameCol, "Mod name", "");
        _author = PaneUi.Field(authorCol, "Author", "");
        _version = PaneUi.Field(versionCol, "Version", "1.0.0");
        _notes = PaneUi.Field(modBox, "Notes (optional)", "", multiline: true);
        _notes.Height = 34;
        foreach (var box in new[] { _modName, _author, _version, _notes })
            box.TextChanged += (_, _) => MarkDirty();

        _exclusiveStart = PaneUi.Add(modBox, new CheckBox
        {
            Content = "Guaranteed Shipbreaker start: offer only this mod's ships, dropping the vanilla salvage pods",
            Foreground = PaneUi.Ink, Margin = new Thickness(0, 10, 0, 0),
        });
        _exclusiveStart.Checked += (_, _) => MarkDirty();
        _exclusiveStart.Unchecked += (_, _) => MarkDirty();
        _exclusiveStart.ToolTip =
            "The career rolls one pool, so \"only mine\" can be said once for the mod and not once per ship. It " +
            "affects only the ships in this pack that are offered as a starting ship.";

        // ---- the ships ----
        var listPanel = new DockPanel { Margin = new Thickness(16, 8, 8, 8) };
        var listHeader = new TextBlock
        {
            Text = "Ships in this mod", Foreground = PaneUi.Ink, FontSize = 15, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
        };
        DockPanel.SetDock(listHeader, Dock.Top);
        listPanel.Children.Add(listHeader);

        var listButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(listButtons, Dock.Bottom);
        var add = new Button { Content = "Add designs…", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 6, 0) };
        add.Click += (_, _) => AddDesigns();
        _remove = new Button { Content = "Remove", Padding = new Thickness(12, 3, 12, 3), IsEnabled = false };
        _remove.Click += (_, _) => RemoveSelected();
        listButtons.Children.Add(add);
        listButtons.Children.Add(_remove);
        listPanel.Children.Add(listButtons);

        _list = new ListBox
        {
            Background = ThemeManager.PanelBg, BorderBrush = ThemeManager.PanelBorder, Width = 300,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.SelectionChanged += (_, _) => OnSelectionChanged();
        listPanel.Children.Add(_list);

        // ---- one ship's settings ----
        var memberBody = new StackPanel();
        _shipName = PaneUi.Field(memberBody, "Ship name in game data", "");
        _shipName.TextChanged += (_, _) => OnShipNameTyped();
        PaneUi.Note(memberBody,
            "The design's own name unless you change it here. It is what the game keys the ship, its pictures and " +
            "its kiosk listing on, so two ships in one mod cannot share it.");

        PaneUi.Header(memberBody, "REPLACE AN EXISTING SHIP");
        _replace = PaneUi.Add(memberBody, new CheckBox
        {
            Content = "Replace an existing ship instead of adding a new one",
            Foreground = PaneUi.Ink, Margin = new Thickness(0, 2, 0, 4),
        });
        _replacePicker = PaneUi.Add(memberBody, new ComboBox
        {
            Margin = new Thickness(20, 0, 0, 2), IsEnabled = false,
            DisplayMemberPath = nameof(ShipFileEntry.Name), MaxDropDownHeight = 260,
        });
        _replace.Checked += (_, _) => { _replacePicker.IsEnabled = true; SaveMember(); };
        _replace.Unchecked += (_, _) => { _replacePicker.IsEnabled = false; SaveMember(); };
        _replacePicker.SelectionChanged += (_, _) => SaveMember();

        _wearHost = PaneUi.Add(memberBody, new Border { Margin = new Thickness(0, 4, 0, 0) });
        _wear = NewWearControl();
        _wearHost.Child = _wear;

        PaneUi.Header(memberBody, "OBTAINABLE IN GAME");
        memberBody.Children.Add(_obtainable);
        _obtainable.Changed += SaveMember;

        _memberPane = new Border { Child = memberBody };
        _emptyHint = new TextBlock
        {
            Text = "Add the designs you want in this mod, then pick one to say how the game should hand it out.",
            Foreground = PaneUi.Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 40, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 360, TextAlignment = TextAlignment.Center,
        };
        var memberHost = new Grid();
        memberHost.Children.Add(_memberPane);
        memberHost.Children.Add(_emptyHint);
        var memberScroll = new ScrollViewer
        {
            Content = memberHost, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(14, 8, 16, 8),
        };

        var middle = new DockPanel();
        DockPanel.SetDock(listPanel, Dock.Left);
        middle.Children.Add(listPanel);
        middle.Children.Add(memberScroll);

        // ---- where it goes ----
        var target = new StackPanel { Margin = new Thickness(16, 6, 16, 0) };
        var targetRow = PaneUi.Add(target, new StackPanel { Orientation = Orientation.Horizontal });
        targetRow.Children.Add(new TextBlock
        {
            Text = "Write to:", Foreground = PaneUi.Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });
        _toMods = new RadioButton
        {
            Content = "the game's Mods folder", GroupName = "bundleTarget", IsChecked = true,
            Foreground = PaneUi.Ink, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0),
        };
        _toFolder = new RadioButton
        {
            Content = "a folder:", GroupName = "bundleTarget", Foreground = PaneUi.Ink,
            VerticalAlignment = VerticalAlignment.Center,
        };
        targetRow.Children.Add(_toMods);
        targetRow.Children.Add(_toFolder);
        _folderPath = new TextBlock
        {
            Foreground = PaneUi.Dim, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 340,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0),
            Text = "(no folder chosen)",
        };
        targetRow.Children.Add(_folderPath);
        var browse = new Button { Content = "Browse…", Padding = new Thickness(10, 2, 10, 2) };
        browse.Click += (_, _) => PickFolder();
        targetRow.Children.Add(browse);
        _register = new CheckBox
        {
            Content = "Register with Ostrasort", Foreground = PaneUi.Ink,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 0, 0),
        };
        targetRow.Children.Add(_register);
        _toMods.Checked += (_, _) => MarkDirty(settingsOnly: true);
        _toFolder.Checked += (_, _) => MarkDirty(settingsOnly: true);

        _problem = PaneUi.Problem(target);

        // ---- buttons ----
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 10, 16, 14),
        };
        _export = new Button { Content = "Review and export…", Padding = new Thickness(18, 5, 18, 5), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        _export.Click += async (_, _) => await ReviewAndExport();
        var save = new Button { Content = "Save pack", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 6, 0) };
        save.Click += (_, _) => SavePack(saveAs: false);
        var saveAs = new Button { Content = "Save as…", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 6, 0) };
        saveAs.Click += (_, _) => SavePack(saveAs: true);
        var open = new Button { Content = "Open pack…", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 6, 0) };
        open.Click += (_, _) => OpenPack();
        var close = new Button { Content = "Close", Padding = new Thickness(14, 5, 14, 5), IsCancel = true };
        close.Click += (_, _) => Close();
        buttons.Children.Add(_export);
        buttons.Children.Add(open);
        buttons.Children.Add(save);
        buttons.Children.Add(saveAs);
        buttons.Children.Add(close);

        var root = new DockPanel();
        DockPanel.SetDock(modBox, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(target, Dock.Bottom);
        root.Children.Add(modBox);
        root.Children.Add(buttons);
        root.Children.Add(target);
        root.Children.Add(middle);
        Content = root;

        Closing += OnClosing;
        LoadTargetSettings();
        LoadFile(new BundleFile { Mod = { Author = settings.ExportAuthor ?? "" } }, null);
    }

    // ---- the pack ----

    private void LoadFile(BundleFile file, string? path)
    {
        _file = file;
        _path = path;
        _members.Clear();

        foreach (var entry in file.Ships)
        {
            var resolved = path is null ? entry.Path : BundleFile.ResolveDesignPath(path, entry.Path);
            _members.Add(Read(entry, resolved));
        }

        _populating = true;
        try
        {
            _modName.Text = file.Mod.Name;
            _author.Text = file.Mod.Author is { Length: > 0 } a ? a : _settings.ExportAuthor ?? "";
            _version.Text = file.Mod.Version;
            _notes.Text = file.Mod.Notes;
            _exclusiveStart.IsChecked = file.Mod.ExclusiveStart;
        }
        finally { _populating = false; }

        _dirty = false;
        RefreshList(select: _members.FirstOrDefault());
        RefreshTitle();
    }

    private BundleMember Read(BundleEntry entry, string path)
    {
        var member = BundleMember.Read(entry, path, _catalog);
        member.OpenWithUnsavedEdits = _isOpenAndDirty(path);
        return member;
    }

    /// <summary>Open a pack without asking for it. The file dialog is what a person uses; this is for the
    /// <c>--bundlesmoke</c> development render, which has no one to click it.</summary>
    internal void OpenPack(string path) => LoadFile(BundleFile.Load(path), path);

    private void OpenPack()
    {
        if (!ConfirmDiscard()) return;
        var dlg = new OpenFileDialog
        {
            Title = "Open a ship pack",
            Filter = $"Ostraplan ship pack (*.{BundleFile.Extension})|*.{BundleFile.Extension}|All files (*.*)|*.*",
            InitialDirectory = _settings.BundleExport?.LastPackDir ?? "",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            LoadFile(BundleFile.Load(dlg.FileName), dlg.FileName);
            RememberPackDir(dlg.FileName);
        }
        catch (Exception ex)
        {
            Dlg.Error(this, "Ship Bundle", "That pack could not be read.\n\n" + ex.Message);
        }
    }

    private bool SavePack(bool saveAs)
    {
        var path = _path;
        if (saveAs || path is null)
        {
            var dlg = new SaveFileDialog
            {
                Title = "Save the ship pack",
                Filter = $"Ostraplan ship pack (*.{BundleFile.Extension})|*.{BundleFile.Extension}",
                FileName = ShipExport.SanitizeName(
                    _modName.Text.Trim() is { Length: > 0 } n ? n : "Ship pack") + "." + BundleFile.Extension,
                InitialDirectory = _settings.BundleExport?.LastPackDir ?? "",
            };
            if (dlg.ShowDialog(this) != true) return false;
            path = dlg.FileName;
        }

        CollectMod();
        // Paths are stored relative to where the pack is being saved, so a folder holding a pack and its designs
        // can be moved or shared whole.
        _file.Ships = [.. _members.Select(m =>
        {
            m.Entry.Path = BundleFile.StoreDesignPath(path, m.Path);
            return m.Entry;
        })];
        _file.Game = new OplanGame
        {
            VersionAtSave = _env.InstalledVersion,
            VersionVerified = GameEnv.VerifiedGameVersion,
        };

        try
        {
            _file.Save(path);
        }
        catch (Exception ex)
        {
            Dlg.Error(this, "Ship Bundle", "That pack could not be saved.\n\n" + ex.Message);
            return false;
        }

        _path = path;
        _dirty = false;
        RememberPackDir(path);
        RefreshTitle();
        return true;
    }

    private void RememberPackDir(string path)
    {
        var settings = _settings.BundleExport ??= new LastBundleExport();
        settings.LastPackDir = Path.GetDirectoryName(path);
        _settings.Save();
    }

    private void CollectMod()
    {
        _file.Mod.Name = _modName.Text.Trim();
        _file.Mod.Author = _author.Text.Trim();
        _file.Mod.Version = _version.Text.Trim();
        _file.Mod.Notes = _notes.Text.Trim();
        _file.Mod.ExclusiveStart = _exclusiveStart.IsChecked == true;
    }

    // ---- members ----

    private void AddDesigns()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Add designs to this mod",
            Filter = "Ostraplan ship (*.oplan)|*.oplan|All files (*.*)|*.*",
            Multiselect = true,
            InitialDirectory = _settings.BundleExport?.LastPackDir ?? "",
        };
        if (dlg.ShowDialog(this) != true) return;

        var added = new List<BundleMember>();
        var already = new List<string>();
        foreach (var path in dlg.FileNames)
        {
            if (_members.Any(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                already.Add(Path.GetFileName(path));
                continue;
            }
            var member = Read(new BundleEntry { Path = path }, path);
            _members.Add(member);
            added.Add(member);
        }

        if (already.Count > 0)
            Dlg.Info(this, "Already in this mod",
                "These designs are already in the pack, so they were not added again:\n\n" +
                string.Join("\n", already.Select(a => "- " + a)));

        if (added.Count == 0) return;
        MarkDirty();
        RefreshList(select: added[0]);

        // A design that cannot go in a mod is said so on the spot rather than at the write, where it would be a
        // refusal after the user thought the work was done.
        if (added.FirstOrDefault(m => m.Problem is not null) is { } bad)
            Dlg.Warn(this, "That design can't go in a mod yet", $"\"{bad.Name}\": {bad.Problem}");
    }

    private void RemoveSelected()
    {
        if (_selected is not { } member) return;
        _members.Remove(member);
        MarkDirty();
        RefreshList(select: _members.FirstOrDefault());
    }

    private void RefreshList(BundleMember? select)
    {
        _populating = true;
        try
        {
            _list.Items.Clear();
            foreach (var member in _members) _list.Items.Add(Row(member));
            var index = select is null ? -1 : _members.IndexOf(select);
            _list.SelectedIndex = index >= 0 ? index : _members.Count > 0 ? 0 : -1;
        }
        finally { _populating = false; }

        OnSelectionChanged();
        ShowProblems();
    }

    private ListBoxItem Row(BundleMember member)
    {
        var stack = new StackPanel { Margin = new Thickness(2), MaxWidth = 268 };
        stack.Children.Add(new TextBlock
        {
            Text = member.Name, Foreground = member.Problem is null ? PaneUi.Ink : ThemeManager.Bad,
            FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        stack.Children.Add(new TextBlock
        {
            Text = member.Detail,
            Foreground = member.Problem is null && !member.OpenWithUnsavedEdits ? PaneUi.Dim : ThemeManager.Warn,
            FontSize = 11, TextWrapping = TextWrapping.Wrap,
        });
        return new ListBoxItem { Content = stack, Padding = new Thickness(6, 4, 6, 4) };
    }

    private void OnSelectionChanged()
    {
        if (_populating) return;

        _selected = _list.SelectedIndex >= 0 && _list.SelectedIndex < _members.Count
            ? _members[_list.SelectedIndex]
            : null;

        _remove.IsEnabled = _selected is not null;
        _memberPane.Visibility = _selected is null ? Visibility.Collapsed : Visibility.Visible;
        _emptyHint.Visibility = _selected is null ? Visibility.Visible : Visibility.Collapsed;
        if (_selected is not { } member) return;

        _populating = true;
        try
        {
            if (_replacePicker.ItemsSource is null)
                _replacePicker.ItemsSource = TemplateImport.ListShipFiles(_index);

            _shipName.Text = member.Name;
            _replace.IsChecked = member.Entry.Replaces is { Length: > 0 };
            _replacePicker.IsEnabled = _replace.IsChecked == true;
            _replacePicker.SelectedItem = member.Entry.Replaces is { Length: > 0 } r
                ? ((IReadOnlyList<ShipFileEntry>)_replacePicker.ItemsSource!)
                    .FirstOrDefault(e => string.Equals(e.Name, r, StringComparison.OrdinalIgnoreCase))
                : null;

            _wearHost.Child = _wear = NewWearControl();
            _wear.SetWear(member.Entry.Wear.ToOptions());
            _wear.Changed += SaveMember;

            _obtainable.Load(_index, member.Entry.Delivery, member.PartCount, ShipValue.Estimate(
                member.Doc ?? new ShipDocument(_catalog), _catalog, _specs).BuyEstimate);
        }
        finally { _populating = false; }
    }

    private static WearControl NewWearControl() => new(
        fullNote: "The ship is built undamaged, the way a design with no wear on it should arrive.");

    private void OnShipNameTyped()
    {
        if (_populating || _selected is not { } member) return;
        var typed = _shipName.Text.Trim();
        // Blank means "whatever the design calls itself", which is what an override is the exception to.
        member.Entry.NameOverride = typed.Length == 0 || typed == member.Meta.Name ? null : typed;
        MarkDirty();
        RefreshRow(member);
        ShowProblems();
    }

    private void SaveMember()
    {
        if (_populating || _selected is not { } member) return;

        member.Entry.Replaces = _replace.IsChecked == true && _replacePicker.SelectedItem is ShipFileEntry pick
            ? TemplateImport.ResolveShipStrName(pick.Path) ?? pick.Name
            : null;
        member.Entry.Wear = BundleWear.From(_wear.Wear);
        _obtainable.Save(member.Entry.Delivery);

        // A wreck is damaged by the game when it first loads, so baking wear on top would double-damage every
        // part. The single-design export does the same thing on the way out of its Obtainable step.
        if (member.Entry.Delivery.DerelictOnly && member.Entry.Wear.On && member.Entry.Wear.Target < 1.0)
            member.Entry.Wear = BundleWear.From(WearOptions.Pristine);

        MarkDirty();
        RefreshRow(member);
        ShowProblems();
    }

    private void RefreshRow(BundleMember member)
    {
        var index = _members.IndexOf(member);
        if (index < 0 || index >= _list.Items.Count) return;
        _populating = true;
        try { _list.Items[index] = Row(member); _list.SelectedIndex = index; }
        finally { _populating = false; }
    }

    // ---- validation ----

    /// <summary>Everything that would stop this pack being written, in the order a person would fix them.</summary>
    private List<string> Problems()
    {
        var problems = new List<string>();
        if (_modName.Text.Trim().Length == 0) problems.Add("Give the mod a name.");
        if (_members.Count == 0) problems.Add("Add at least one design.");

        foreach (var member in _members.Where(m => m.Problem is not null))
            problems.Add($"\"{member.Name}\": {member.Problem}");

        foreach (var member in _members.Where(m => m.Doc is not null && !m.Entry.Delivery.AnyRoute
                                                   && !m.Entry.Delivery.NoDeliveryRoute))
            problems.Add($"\"{member.Name}\" has no way to be obtained in game. Pick a route for it, or say so " +
                         "under Advanced.");

        if (Ready() is { Count: > 0 } ships) problems.AddRange(BundleExport.Validate(ToOptions(ships, "")));
        return problems;
    }

    private void ShowProblems()
    {
        var problems = Problems();
        PaneUi.ShowProblem(_problem, problems.Count == 0 ? null : string.Join("\n", problems.Take(4))
            + (problems.Count > 4 ? $"\n(and {problems.Count - 4} more)" : ""));
        _export.IsEnabled = problems.Count == 0;
    }

    private List<BundleMember> Ready() => [.. _members.Where(m => m.Doc is not null)];

    private BundleOptions ToOptions(IReadOnlyList<BundleMember> members, string destination) => new(
        _modName.Text.Trim(), _author.Text.Trim(), _notes.Text.Trim(),
        _version.Text.Trim() is { Length: > 0 } v ? v : "1.0.0",
        _env.InstalledVersion ?? GameEnv.VerifiedGameVersion, destination,
        [.. members.Select(m => new BundleShip(
            m.Doc!, m.Name,
            new ExportMetadata(m.Meta.PublicName, m.Meta.Make, m.Meta.Model, m.Meta.Year, m.Meta.Designation,
                m.Meta.Description),
            m.Entry.Wear.ToOptions(),
            m.Entry.Delivery.ToDelivery(
                m.Meta.PublicName is { Length: > 0 } p ? p : m.Name, m.Meta.Description),
            m.Entry.Replaces))],
        _exclusiveStart.IsChecked == true,
        _file.LastWritten);

    // ---- where it goes ----

    private void LoadTargetSettings()
    {
        var remembered = _settings.BundleExport ?? new LastBundleExport();
        _folder = remembered.Folder ?? _settings.LastExportDir;
        _toMods.IsEnabled = _env.ModsDir is not null;
        _toMods.IsChecked = remembered.StagedIntoMods && _env.ModsDir is not null;
        _toFolder.IsChecked = _toMods.IsChecked != true;
        _folderPath.Text = _folder ?? "(no folder chosen)";
        _register.IsChecked = remembered.RegisterWithOstrasort && _env.ModsDir is not null;
        _register.IsEnabled = _env.ModsDir is not null;
    }

    private void SaveTargetSettings()
    {
        var remembered = _settings.BundleExport ??= new LastBundleExport();
        remembered.StagedIntoMods = _toMods.IsChecked == true;
        remembered.Folder = _folder;
        remembered.RegisterWithOstrasort = _register.IsChecked == true && remembered.StagedIntoMods;
        _settings.Save();
    }

    private void PickFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Choose where to write the mod folder" };
        if (_folder is not null) dlg.InitialDirectory = _folder;
        if (dlg.ShowDialog(this) != true) return;
        _folder = dlg.FolderName;
        _folderPath.Text = _folder;
        _toFolder.IsChecked = true;
        MarkDirty(settingsOnly: true);
    }

    private string? Destination()
    {
        if (_toMods.IsChecked == true) return _env.ModsDir;
        if (string.IsNullOrWhiteSpace(_folder)) { Dlg.Warn(this, "Ship Bundle", "Choose a folder to write to."); return null; }
        if (!Directory.Exists(_folder))
        {
            Dlg.Warn(this, "Ship Bundle", $"That folder no longer exists:\n\n{_folder}");
            return null;
        }
        return _folder;
    }

    // ---- export ----

    private async Task ReviewAndExport()
    {
        ShowProblems();
        if (Problems() is { Count: > 0 }) return;
        if (Destination() is not { } destination) return;

        CollectMod();
        SaveTargetSettings();

        var members = Ready();
        var dialog = new BundleReviewDialog(
            _catalog, _index, _env, _settings, _specs, _sprites, ToOptions(members, destination),
            _register.IsChecked == true && _toMods.IsChecked == true)
        { Owner = this };

        dialog.ShowDialog();

        // Read whatever was written rather than the dialog result: closing the report with the window's own X is
        // still an export that happened, and forgetting it would leave the next one unable to sweep a dropped ship.
        if (dialog.Written is not { } written) return;
        _file.LastWritten = [.. written];
        MarkDirty();
        if (_path is not null) SavePack(saveAs: false);
        await Task.CompletedTask;
    }

    // ---- window state ----

    private void MarkDirty(bool settingsOnly = false)
    {
        if (_populating) return;
        if (!settingsOnly) _dirty = true;
        RefreshTitle();
        ShowProblems();
    }

    private void RefreshTitle() =>
        Title = "Ship Bundle" + (_path is { } p ? " — " + Path.GetFileNameWithoutExtension(p) : "") + (_dirty ? " *" : "");

    private bool ConfirmDiscard()
    {
        if (!_dirty) return true;
        var choice = Dlg.Choose(this, DlgKind.Warning, "Save this pack?",
            "This pack has changes you have not saved.", "Save", "Discard");
        return choice switch
        {
            MessageDialog.Choice.Primary => SavePack(saveAs: false),
            MessageDialog.Choice.Secondary => true,
            _ => false,
        };
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmDiscard()) e.Cancel = true;
    }
}
