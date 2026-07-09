using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// Collects the export settings: ship name, in-game identity, how the ship becomes obtainable
/// (kiosk / Special Offer / starting ship), and where to write the mod folder — staged straight into
/// the game's Mods folder (ready to register &amp; test) or to a folder the user picks. Ostraplan writes
/// the mod folder only; registering it in <c>loading_order.json</c> is left to Ostrasort/ModTools.
/// </summary>
public sealed class ExportDialog : Window
{
    private static Brush Ink => ThemeManager.Ink;
    private static Brush Dim => ThemeManager.Dim;
    private static Brush FieldBg => ThemeManager.FieldBg;

    private readonly TextBox _name, _modName, _author, _version, _notes;
    private readonly TextBox _publicName, _make, _model, _year, _designation, _description;
    private readonly CheckBox _replaceShip;
    private readonly ComboBox _replacePicker;
    private string _autoModName = "";   // the last value we auto-filled into _modName, to detect a user edit
    private readonly List<(string Pool, CheckBox Box)> _brokerChecks = [];
    private readonly List<(string Pool, CheckBox Box)> _specialChecks = [];
    private readonly TextBox _brokerWeight;
    private readonly CheckBox _startingShip;
    private readonly TextBox _startStation, _startMortgage;
    private readonly double _startWeight;
    private readonly RadioButton _toMods, _toFolder;
    private readonly CheckBox _registerOstrasort;
    private readonly TextBlock _folderPath;
    private readonly string? _modsDir;
    private string? _pickedFolder;

    public string ShipName => _name.Text.Trim();

    /// <summary>The mod's name (its <c>mod_info</c> name + folder), separate from the ship. Auto-filled with a
    /// sensible default — the ship name, or "{replaced ship} - Replaced via Ostraplan" when replacing — but freely
    /// editable; the exporter re-derives the default if it's left blank (<c>ShipExport.ResolveModName</c>).</summary>
    public string ModName => _modName.Text.Trim();

    public string Author => _author.Text.Trim();
    public string Notes => _notes.Text.Trim();
    public string ModVersion => _version.Text.Trim();
    public bool StagedIntoMods => _toMods.IsChecked == true;
    public string DestinationParent => StagedIntoMods ? _modsDir! : _pickedFolder!;

    /// <summary>Whether to hand the staged mod to Ostrasort for registration + conflict patching right after
    /// export. Only meaningful when staging into the game's Mods folder; ignored for a plain folder export.</summary>
    public bool RegisterWithOstrasort => _registerOstrasort.IsChecked == true && StagedIntoMods;

    /// <summary>The raw in-game display name the user typed (may be empty). The exporter resolves the
    /// fallback — the design name for a new ship, or vanilla varied-naming ("$TEMPLATE") for a replacement —
    /// via <c>ShipExport.ResolvePublicName</c>, so this stays exactly what was typed.</summary>
    public string PublicName => _publicName.Text.Trim();

    /// <summary>The existing ship this design should replace (its <c>strName</c>), or null when the "replace"
    /// option is off or nothing is picked. When set, the export overrides that ship instead of adding a new one.</summary>
    public ShipFileEntry? ReplaceShip =>
        _replaceShip.IsChecked == true && _replacePicker.SelectedItem is ShipFileEntry e ? e : null;

    public string Make => _make.Text.Trim();
    public string Model => _model.Text.Trim();
    public string Year => _year.Text.Trim();
    public string Designation => _designation.Text.Trim();
    public string Description => _description.Text.Trim();

    /// <summary>The obtainability options the user selected — which kiosk/Special-Offer pools to add the ship
    /// to, and whether to make it a possible Shipbreaker starting ship. <see cref="ShipDelivery.None"/> when
    /// nothing is ticked (a plain ship-file export).</summary>
    public ShipDelivery Delivery => new(
        _brokerChecks.Where(c => c.Box.IsChecked == true).Select(c => c.Pool).ToList(),
        ParseDouble(_brokerWeight.Text, 0.05),
        _specialChecks.Where(c => c.Box.IsChecked == true).Select(c => c.Pool).ToList(),
        _startingShip.IsChecked == true,
        _startWeight,
        _startStation.Text.Trim() is { Length: > 0 } s ? s : "OKLG",
        ParseDouble(_startMortgage.Text, 0),
        PublicName is { Length: > 0 } pn ? pn : ShipName,
        Description);

    public ExportDialog(string defaultName, string defaultAuthor, string? modsDir, string? lastFolder,
        DataIndex? index = null, double buyEstimate = 0, bool ostrasortKnown = false)
    {
        _modsDir = modsDir;
        _pickedFolder = lastFolder;

        Title = "Export as spawnable mod";
        Width = 500;
        MaxHeight = 820;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = ThemeManager.WindowBg;

        var body = new StackPanel { Margin = new Thickness(18) };

        _name = Field(body, "Ship name", defaultName);
        _modName = Field(body, "Mod name", defaultName);
        _autoModName = defaultName;
        _name.TextChanged += (_, _) => SyncModNameDefault();   // follow the ship name until the user edits the mod name
        _author = Field(body, "Author", defaultAuthor);
        _version = Field(body, "Mod version", "1.0.0");
        _notes = Field(body, "Notes (optional)", "", multiline: true);

        Header(body, "SHIP IDENTITY (IN-GAME)");
        _publicName = Field(body, "In-game name (optional)", "");
        _make = Field(body, "Make", "");
        _model = Field(body, "Model", "");
        _year = Field(body, "Year", "");
        _designation = Field(body, "Designation (class/role, e.g. \"Salvage Tug\")", "");
        _description = Field(body, "Description (optional)", "", multiline: true);
        body.Children.Add(new TextBlock
        {
            Text = "Leave the in-game name blank to use the ship name (or, when replacing a ship, the game's usual " +
                   "varied names). Type a name to pin it — it shows at the transponder, comms, and broker listings. " +
                   "The rest is flavor text (make/model/year/designation/description).",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
        });

        // --- replace an existing ship (override its data/ships entry by strName) ---
        Header(body, "REPLACE AN EXISTING SHIP");
        _replaceShip = new CheckBox
        {
            Content = "Replace an existing ship instead of adding a new one",
            Foreground = Ink, Margin = new Thickness(0, 2, 0, 4),
        };
        body.Children.Add(_replaceShip);
        _replacePicker = new ComboBox
        {
            Margin = new Thickness(20, 0, 0, 2), IsEnabled = false,
            DisplayMemberPath = nameof(ShipFileEntry.Name), MaxDropDownHeight = 260,
        };
        if (index is not null)
        {
            var ships = TemplateImport.ListShipFiles(index);
            _replacePicker.ItemsSource = ships;
            // pre-select the ship whose name matches this design (the import-a-vanilla-ship → retrofit → replace flow)
            _replacePicker.SelectedItem = ships.FirstOrDefault(s => string.Equals(s.Name, defaultName, StringComparison.OrdinalIgnoreCase));
        }
        _replaceShip.Checked += (_, _) => { _replacePicker.IsEnabled = true; SyncModNameDefault(); };
        _replaceShip.Unchecked += (_, _) => { _replacePicker.IsEnabled = false; SyncModNameDefault(); };
        _replacePicker.SelectionChanged += (_, _) => SyncModNameDefault();
        body.Children.Add(_replacePicker);
        body.Children.Add(new TextBlock
        {
            Text = "Your design takes over the chosen ship's identity, so the game spawns yours in its place " +
                   "everywhere (brokers, derelicts, missions). Structure only — the original's cargo and crew " +
                   "loadout aren't carried over. It only affects new spawns, not ships already in a save.",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 2, 0, 0),
        });

        // --- delivery: how the ship becomes obtainable in game ---
        Header(body, "HOW TO GET IT IN GAME");

        body.Children.Add(new TextBlock { Text = "Ship broker kiosks (regular stock):", Foreground = Ink, Margin = new Thickness(0, 2, 0, 3) });
        var brokerWrap = new WrapPanel { Margin = new Thickness(6, 0, 0, 2) };
        foreach (var (pool, label) in KioskExport.BrokerPools)
        {
            var cb = new CheckBox { Content = label, Foreground = Ink, Margin = new Thickness(0, 0, 14, 4), MinWidth = 130 };
            _brokerChecks.Add((pool, cb));
            brokerWrap.Children.Add(cb);
        }
        body.Children.Add(brokerWrap);

        var weightRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 2, 0, 2) };
        weightRow.Children.Add(new TextBlock { Text = "Weight (how often it appears):", Foreground = Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        var defBrokerWeight = index is not null ? KioskExport.DefaultBrokerWeight(index, "RandomShipBrokerOKLG") : 0.05;
        _brokerWeight = new TextBox
        {
            Text = defBrokerWeight.ToString("0.####", CultureInfo.InvariantCulture),
            Width = 70, Foreground = Ink, Background = FieldBg, BorderBrush = ThemeManager.PanelBorder,
            Padding = new Thickness(5, 2, 5, 2), CaretBrush = Ink,
        };
        weightRow.Children.Add(_brokerWeight);
        body.Children.Add(weightRow);

        body.Children.Add(new TextBlock { Text = "Special Offer (shown only when you own no ship/property):", Foreground = Ink, Margin = new Thickness(0, 10, 0, 3) });
        var specialWrap = new WrapPanel { Margin = new Thickness(6, 0, 0, 2) };
        foreach (var (pool, label) in KioskExport.SpecialOfferPools)
        {
            var cb = new CheckBox { Content = label, Foreground = Ink, Margin = new Thickness(0, 0, 14, 4), MinWidth = 110 };
            _specialChecks.Add((pool, cb));
            specialWrap.Children.Add(cb);
        }
        body.Children.Add(specialWrap);
        body.Children.Add(new TextBlock
        {
            Text = "Heads up: the game always lists a Special Offer ship at \"$0\" — the real price only shows when you " +
                   "click Buy (it's a game quirk, not a pricing error). Add it to a broker kiosk above for a visible " +
                   "list price.",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 2, 0, 0),
        });

        _startingShip = new CheckBox
        {
            Content = "Offer as a starting ship (Shipbreaker career)",
            Foreground = Ink, Margin = new Thickness(0, 10, 0, 2),
        };
        body.Children.Add(_startingShip);
        _startWeight = index is not null ? KioskExport.DefaultBrokerWeight(index, StartingShipExport.ShipEventsPool) : 0.16;

        var startRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 2, 0, 2) };
        startRow.Children.Add(new TextBlock { Text = "Start at ATC:", Foreground = Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        _startStation = new TextBox
        {
            Text = "OKLG", Width = 70, Foreground = Ink, Background = FieldBg, BorderBrush = ThemeManager.PanelBorder,
            Padding = new Thickness(5, 2, 5, 2), CaretBrush = Ink,
        };
        startRow.Children.Add(_startStation);
        startRow.Children.Add(new TextBlock { Text = "Mortgage ($):", Foreground = Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 6, 0) });
        _startMortgage = new TextBox
        {
            Text = Math.Round(buyEstimate).ToString("0", CultureInfo.InvariantCulture),
            Width = 100, Foreground = Ink, Background = FieldBg, BorderBrush = ThemeManager.PanelBorder,
            Padding = new Thickness(5, 2, 5, 2), CaretBrush = Ink,
        };
        startRow.Children.Add(_startMortgage);
        body.Children.Add(startRow);
        body.Children.Add(new TextBlock
        {
            Text = "A starting ship is a weighted option in a fresh Shipbreaker start (alongside the vanilla " +
                   "salvage pods), not a guaranteed pick — the game has no true ship picker.",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 2, 0, 0),
        });
        body.Children.Add(new TextBlock
        {
            Text = "If another ship mod adds to the same kiosks, run Ostrasort's conflict patch afterward so both " +
                   "mods' ships survive.",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
        });

        Header(body, "DESTINATION");

        _toMods = new RadioButton
        {
            Content = "Stage into the game's Mods folder (ready to register & test)",
            Foreground = Ink, IsChecked = true, IsEnabled = modsDir is not null, Margin = new Thickness(0, 2, 0, 2),
        };
        _toFolder = new RadioButton { Content = "Write to a folder…", Foreground = Ink, Margin = new Thickness(0, 2, 0, 2) };
        if (modsDir is null) _toFolder.IsChecked = true;
        body.Children.Add(_toMods);
        body.Children.Add(_toFolder);

        var folderRow = new DockPanel { Margin = new Thickness(20, 2, 0, 4) };
        var browse = new Button { Content = "Browse…", Padding = new Thickness(10, 2, 10, 2) };
        browse.Click += (_, _) => PickFolder();
        DockPanel.SetDock(browse, Dock.Right);
        folderRow.Children.Add(browse);
        _folderPath = new TextBlock { Foreground = Dim, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Text = _pickedFolder ?? "(no folder chosen)" };
        folderRow.Children.Add(_folderPath);
        body.Children.Add(folderRow);

        _registerOstrasort = new CheckBox
        {
            Content = "Register with Ostrasort after exporting (recommended)",
            Foreground = Ink, IsChecked = ostrasortKnown && modsDir is not null, Margin = new Thickness(0, 10, 0, 2),
        };
        body.Children.Add(_registerOstrasort);
        body.Children.Add(new TextBlock
        {
            Text = "Ostraplan writes the mod folder only — it never edits loading_order.json. Ostrasort registers " +
                   "the mod (and patches kiosk-loot conflicts), so the ship appears in-game. Leave this unticked to " +
                   "register it yourself later.",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 0, 0, 0),
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = "Export", Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 4, 16, 4), IsCancel = true };
        ok.Click += (_, _) => OnOk();
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        body.Children.Add(buttons);

        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void PickFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Choose where to write the mod folder" };
        if (_pickedFolder is not null) dlg.InitialDirectory = _pickedFolder;
        if (dlg.ShowDialog(this) != true) return;
        _pickedFolder = dlg.FolderName;
        _folderPath.Text = _pickedFolder;
        _toFolder.IsChecked = true;
    }

    private void OnOk()
    {
        if (ShipName.Length == 0)
        {
            Dlg.Info(this, "Export", "Give the ship a name.");
            return;
        }
        if (!StagedIntoMods && string.IsNullOrWhiteSpace(_pickedFolder))
        {
            Dlg.Info(this, "Export", "Choose a folder to write to.");
            return;
        }
        if (_replaceShip.IsChecked == true && _replacePicker.SelectedItem is not ShipFileEntry)
        {
            Dlg.Info(this, "Export", "Pick the ship to replace, or untick \"Replace an existing ship\".");
            return;
        }
        DialogResult = true;
    }

    private static double ParseDouble(string text, double fallback) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : fallback;

    /// <summary>Keep the mod-name field showing a sensible default (the ship name, or "{replaced ship} - Replaced
    /// via Ostraplan" when replacing) — but only while the user hasn't customised it (the text still equals the
    /// value we last auto-filled, or is blank). A user edit sticks.</summary>
    private void SyncModNameDefault()
    {
        var proposed = ProposedModName();
        if (_modName.Text.Trim().Length == 0 || _modName.Text == _autoModName)
        {
            _modName.Text = proposed;
            _autoModName = proposed;
        }
    }

    private string ProposedModName() =>
        _replaceShip.IsChecked == true && _replacePicker.SelectedItem is ShipFileEntry e
            ? $"{e.Name} - Replaced via Ostraplan"
            : ShipName;

    private static void Header(Panel parent, string text) =>
        parent.Children.Add(new TextBlock { Text = text, Foreground = Dim, FontWeight = FontWeights.Bold, FontSize = 11, Margin = new Thickness(0, 16, 0, 5) });

    private static TextBox Field(Panel parent, string label, string value, bool multiline = false)
    {
        parent.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), Foreground = Dim, FontWeight = FontWeights.Bold, FontSize = 11, Margin = new Thickness(0, 10, 0, 3) });
        var box = new TextBox
        {
            Text = value,
            Foreground = Ink,
            Background = FieldBg,
            BorderBrush = ThemeManager.PanelBorder,
            Padding = new Thickness(5, 3, 5, 3),
            CaretBrush = Ink,
        };
        if (multiline)
        {
            box.AcceptsReturn = true;
            box.TextWrapping = TextWrapping.Wrap;
            box.Height = 48;
            box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
        parent.Children.Add(box);
        return box;
    }
}
