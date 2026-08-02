using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>Which way the design leaves Ostraplan: as a spawnable mod, or straight into a copy of a save game
/// as a ship the player already owns.</summary>
public enum ExportMode
{
    /// <summary>A <c>data/ships</c> mod folder — shareable, save-independent, obtainable via a kiosk.</summary>
    Mod,

    /// <summary>A new owned ship written into a copy of a save (see <see cref="SaveGrant"/>).</summary>
    Save,
}

/// <summary>
/// Collects the export settings for both ways a design can leave Ostraplan. The ship's identity (name, in-game
/// name, make/model/year/designation/description) and its condition are shared, because both destinations want
/// exactly those; everything else is per-destination and lives in its own tab.
///
/// <para><b>As a mod</b>: how the ship becomes obtainable (kiosk / Special Offer / starting ship) and where to
/// write the mod folder — staged straight into the game's Mods folder (ready to register &amp; test) or to a
/// folder the user picks. Ostraplan writes the mod folder only; registering it in <c>loading_order.json</c> is
/// left to Ostrasort/ModTools.</para>
///
/// <para><b>Into a save game</b>: which save, and what to charge for it. The ship lands in a <b>copy</b> of that
/// save, owned by the player and parked a few kilometres off wherever they are.</para>
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
    private readonly RadioButton _startWeighted, _startExclusive;
    private readonly TextBox _startStation, _startMortgage;
    private readonly double _startWeight;
    private readonly WearControl _wear;
    private readonly RadioButton _toMods, _toFolder;
    private readonly CheckBox _registerOstrasort;
    private readonly TextBlock _folderPath;
    private readonly string? _modsDir;
    private string? _pickedFolder;

    private readonly TabControl _tabs;
    private readonly Button _ok;
    private ComboBox _savePicker = null!;          // built in BuildSaveTab, called from the constructor
    private CheckBox _charge = null!;
    private TextBox _price = null!;
    private TextBlock _saveStatus = null!, _balanceLine = null!;
    private GrantContext? _grantCtx;   // the selected save, read once so the price can be costed against a balance

    /// <summary>Which destination the user is on. The two tabs collect different things and the OK button
    /// validates (and is labelled) accordingly.</summary>
    public ExportMode Mode => _tabs.SelectedIndex == 1 ? ExportMode.Save : ExportMode.Mod;

    /// <summary>The save to grant into, read once when it was selected so the price could be costed. Null unless
    /// <see cref="Mode"/> is <see cref="ExportMode.Save"/> and a usable save is picked.</summary>
    public GrantContext? GrantContext => _grantCtx;

    /// <summary>The credits to charge for a granted ship: 0 when the user left it a gift.</summary>
    public double Price => _charge.IsChecked == true ? ParseDouble(_price.Text, 0) : 0;

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

    /// <summary>The wear to bake into the exported ship (on by default at the vanilla ~88% condition; drag the
    /// slider to 100% or untick to export pristine).</summary>
    public WearOptions Wear => _wear.Wear;

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
        Description,
        _startExclusive.IsChecked == true);

    public ExportDialog(string defaultName, string defaultAuthor, string? modsDir, string? lastFolder,
        DataIndex? index = null, double buyEstimate = 0, bool ostrasortKnown = false, OplanMeta? meta = null,
        IReadOnlyList<SaveEntry>? saves = null, bool linkedToSave = false)
    {
        _modsDir = modsDir;
        _pickedFolder = lastFolder;

        Title = "Export";
        Width = 500;
        MaxHeight = 820;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = ThemeManager.WindowBg;

        var body = new StackPanel { Margin = new Thickness(18) };

        // ---- shared: what the ship IS. Both destinations want exactly these. ----
        _name = Field(body, "Ship name", defaultName);

        Header(body, "SHIP IDENTITY (IN-GAME)");
        // Pre-fill from the design's saved identity (the Ship Info dialog) — edits here flow back to it on export.
        _publicName = Field(body, "In-game name (optional)", meta?.PublicName ?? "");
        _make = Field(body, "Make", meta?.Make ?? "");
        _model = Field(body, "Model", meta?.Model ?? "");
        _year = Field(body, "Year", meta?.Year ?? "");
        _designation = Field(body, "Designation (class/role, e.g. \"Salvage Tug\")", meta?.Designation ?? "");
        _description = Field(body, "Description (optional)", meta?.Description ?? "", multiline: true);
        body.Children.Add(new TextBlock
        {
            Text = "Leave the in-game name blank to use the ship name (or, when replacing a ship, the game's usual " +
                   "varied names). Type a name to pin it — it shows at the transponder, comms, and broker listings. " +
                   "The rest is flavor text (make/model/year/designation/description). Edit these anytime from " +
                   "\"Ship Info\" — they're saved with the design.",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
        });

        // ---- the two destinations. Everything below here is per-destination. ----
        var modTab = new StackPanel { Margin = new Thickness(10, 4, 10, 10) };
        var saveTab = new StackPanel { Margin = new Thickness(10, 4, 10, 10) };
        _tabs = new TabControl
        {
            Margin = new Thickness(0, 16, 0, 0),
            Items =
            {
                new TabItem { Header = "As a mod", Content = modTab },
                new TabItem { Header = "Into a save game", Content = saveTab },
            },
        };
        body.Children.Add(_tabs);

        BuildSaveTab(saveTab, saves, linkedToSave);

        // --- condition / wear: shared, because both destinations bake per-part damage the same way (on by
        // default at the vanilla ~88% used-ship condition) ---
        _wear = new WearControl(defaultOn: true);
        body.Children.Add(_wear);

        // ---- mod tab ----
        _modName = Field(modTab, "Mod name", defaultName);
        _autoModName = defaultName;
        _name.TextChanged += (_, _) => SyncModNameDefault();   // follow the ship name until the user edits the mod name
        _author = Field(modTab, "Author", defaultAuthor);
        _version = Field(modTab, "Mod version", "1.0.0");
        _notes = Field(modTab, "Notes (optional)", "", multiline: true);

        // --- replace an existing ship (override its data/ships entry by strName) ---
        Header(modTab, "REPLACE AN EXISTING SHIP");
        _replaceShip = new CheckBox
        {
            Content = "Replace an existing ship instead of adding a new one",
            Foreground = Ink, Margin = new Thickness(0, 2, 0, 4),
        };
        modTab.Children.Add(_replaceShip);
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
        modTab.Children.Add(_replacePicker);
        modTab.Children.Add(new TextBlock
        {
            Text = "Your design takes over the chosen ship's identity, so the game spawns yours in its place " +
                   "everywhere (brokers, derelicts, missions). Structure only — the original's cargo and crew " +
                   "loadout aren't carried over. It only affects new spawns, not ships already in a save.",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 2, 0, 0),
        });

        // --- delivery: how the ship becomes obtainable in game ---
        Header(modTab, "HOW TO GET IT IN GAME");

        modTab.Children.Add(new TextBlock { Text = "Ship broker kiosks (regular stock):", Foreground = Ink, Margin = new Thickness(0, 2, 0, 3) });
        var brokerWrap = new WrapPanel { Margin = new Thickness(6, 0, 0, 2) };
        foreach (var (pool, label) in KioskExport.BrokerPools)
        {
            var cb = new CheckBox { Content = label, Foreground = Ink, Margin = new Thickness(0, 0, 14, 4), MinWidth = 130 };
            _brokerChecks.Add((pool, cb));
            brokerWrap.Children.Add(cb);
        }
        modTab.Children.Add(brokerWrap);

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
        modTab.Children.Add(weightRow);

        modTab.Children.Add(new TextBlock { Text = "Special Offer (shown only when you own no ship/property):", Foreground = Ink, Margin = new Thickness(0, 10, 0, 3) });
        var specialWrap = new WrapPanel { Margin = new Thickness(6, 0, 0, 2) };
        foreach (var (pool, label) in KioskExport.SpecialOfferPools)
        {
            var cb = new CheckBox { Content = label, Foreground = Ink, Margin = new Thickness(0, 0, 14, 4), MinWidth = 110 };
            _specialChecks.Add((pool, cb));
            specialWrap.Children.Add(cb);
        }
        modTab.Children.Add(specialWrap);
        modTab.Children.Add(new TextBlock
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
        modTab.Children.Add(_startingShip);
        _startWeight = index is not null ? KioskExport.DefaultBrokerWeight(index, StartingShipExport.ShipEventsPool) : 0.16;

        // weighted (alongside vanilla pods) vs guaranteed (only ship offered — pins the start-event pool)
        _startWeighted = new RadioButton
        {
            Content = "Weighted chance (alongside the vanilla salvage pods)",
            GroupName = "startMode", IsChecked = true, IsEnabled = false, Foreground = Ink, Margin = new Thickness(20, 4, 0, 1),
        };
        _startExclusive = new RadioButton
        {
            Content = "Only your ship offered (guaranteed start)",
            GroupName = "startMode", IsEnabled = false, Foreground = Ink, Margin = new Thickness(20, 0, 0, 2),
        };
        modTab.Children.Add(_startWeighted);
        modTab.Children.Add(_startExclusive);
        _startingShip.Checked += (_, _) => { _startWeighted.IsEnabled = true; _startExclusive.IsEnabled = true; };
        _startingShip.Unchecked += (_, _) => { _startWeighted.IsEnabled = false; _startExclusive.IsEnabled = false; };

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
        modTab.Children.Add(startRow);
        modTab.Children.Add(new TextBlock
        {
            Text = "The game has no true ship picker. \"Weighted chance\" adds your ship as one option among the " +
                   "vanilla salvage pods. \"Only your ship offered\" replaces that start-event pool with just your " +
                   "ship, so a fresh Shipbreaker always starts with it (this drops the vanilla pods, and any other " +
                   "mod's start ships, from the roll).",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 2, 0, 0),
        });
        modTab.Children.Add(new TextBlock
        {
            Text = "If another ship mod adds to the same kiosks, run Ostrasort's conflict patch afterward so both " +
                   "mods' ships survive.",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
        });

        Header(modTab, "DESTINATION");

        _toMods = new RadioButton
        {
            Content = "Stage into the game's Mods folder (ready to register & test)",
            Foreground = Ink, IsChecked = true, IsEnabled = modsDir is not null, Margin = new Thickness(0, 2, 0, 2),
        };
        _toFolder = new RadioButton { Content = "Write to a folder…", Foreground = Ink, Margin = new Thickness(0, 2, 0, 2) };
        if (modsDir is null) _toFolder.IsChecked = true;
        modTab.Children.Add(_toMods);
        modTab.Children.Add(_toFolder);

        var folderRow = new DockPanel { Margin = new Thickness(20, 2, 0, 4) };
        var browse = new Button { Content = "Browse…", Padding = new Thickness(10, 2, 10, 2) };
        browse.Click += (_, _) => PickFolder();
        DockPanel.SetDock(browse, Dock.Right);
        folderRow.Children.Add(browse);
        _folderPath = new TextBlock { Foreground = Dim, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Text = _pickedFolder ?? "(no folder chosen)" };
        folderRow.Children.Add(_folderPath);
        modTab.Children.Add(folderRow);

        _registerOstrasort = new CheckBox
        {
            Content = "Register with Ostrasort after exporting (recommended)",
            Foreground = Ink, IsChecked = ostrasortKnown && modsDir is not null, Margin = new Thickness(0, 10, 0, 2),
        };
        modTab.Children.Add(_registerOstrasort);
        modTab.Children.Add(new TextBlock
        {
            Text = "Ostraplan writes the mod folder only — it never edits loading_order.json. Ostrasort registers " +
                   "the mod (and patches kiosk-loot conflicts), so the ship appears in-game. Leave this unticked to " +
                   "register it yourself later.",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 0, 0, 0),
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        _ok = new Button { Content = "Export", Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 4, 16, 4), IsCancel = true };
        _ok.Click += (_, _) => OnOk();
        buttons.Children.Add(_ok);
        buttons.Children.Add(cancel);
        body.Children.Add(buttons);

        _tabs.SelectionChanged += (_, e) => { if (e.OriginalSource == _tabs) SyncMode(); };
        SyncMode();

        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    /// <summary>Keep the commit button's label and enabled state matching the tab in front of the user, so the
    /// button always says what it is about to do.</summary>
    private void SyncMode()
    {
        if (Mode == ExportMode.Save)
        {
            _ok.Content = "Add to save…";
            _ok.IsEnabled = _grantCtx is not null && Affordable();
        }
        else
        {
            _ok.Content = "Export";
            _ok.IsEnabled = true;
        }
    }

    /// <summary>True when the price fits the selected save's balance (always true when not charging).</summary>
    private bool Affordable() => _grantCtx is not { } ctx || Price <= ctx.Balance;

    /// <summary>
    /// The "into a save game" tab: pick a save, then decide what it costs. Selecting a save reads it
    /// (<see cref="SaveGrant.ReadContext"/>) so the price can be checked against a real balance before anything is
    /// written, and so a save that cannot take a grant at all says why here rather than failing after the user
    /// has committed.
    /// </summary>
    private void BuildSaveTab(Panel tab, IReadOnlyList<SaveEntry>? saves, bool linkedToSave)
    {
        tab.Children.Add(new TextBlock
        {
            Text = "Adds this design to a copy of a save game as a new ship you own, parked a few kilometres away " +
                   "and reachable by P.A.S.S. ferry. The original save is never modified.",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8),
        });

        if (linkedToSave)
            tab.Children.Add(new TextBlock
            {
                Text = "This design came from a save. Adding it here creates a separate NEW ship — it does not " +
                       "update the ship you imported. To change that ship, use Analyse ▸ \"Update Ship in Save…\".",
                Foreground = Ink, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            });

        tab.Children.Add(new TextBlock { Text = "SAVE GAME", Foreground = Dim, FontWeight = FontWeights.Bold, FontSize = 11, Margin = new Thickness(0, 2, 0, 4) });
        _savePicker = new ComboBox { DisplayMemberPath = nameof(SaveEntry.Name), MaxDropDownHeight = 240 };
        if (saves is { Count: > 0 }) _savePicker.ItemsSource = saves;
        tab.Children.Add(_savePicker);

        _saveStatus = new TextBlock
        {
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            Text = saves is { Count: > 0 } ? "Pick the save to add the ship to." : "No save games found.",
        };
        tab.Children.Add(_saveStatus);

        _charge = new CheckBox
        {
            Content = "Charge for the ship (deduct from your credits)", Foreground = Ink, IsEnabled = false,
            Margin = new Thickness(0, 14, 0, 2),
        };
        tab.Children.Add(_charge);

        var priceRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 4, 0, 0) };
        priceRow.Children.Add(new TextBlock { Text = "Price ($):", Foreground = Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        _price = new TextBox
        {
            Text = "0", Width = 110, Foreground = Ink, Background = FieldBg, BorderBrush = ThemeManager.PanelBorder,
            Padding = new Thickness(5, 2, 5, 2), CaretBrush = Ink, IsEnabled = false,
        };
        priceRow.Children.Add(_price);
        tab.Children.Add(priceRow);

        _balanceLine = new TextBlock { Foreground = Ink, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24, 6, 0, 0) };
        tab.Children.Add(_balanceLine);

        tab.Children.Add(new TextBlock
        {
            Text = "Leave this unticked and the ship is a gift. Ticked, the price comes off your character's " +
                   "balance, which is how you simulate buying it.",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24, 6, 0, 0),
        });

        _savePicker.SelectionChanged += (_, _) => OnSaveSelected();
        _charge.Checked += (_, _) => SyncPrice();
        _charge.Unchecked += (_, _) => SyncPrice();
        _price.TextChanged += (_, _) => SyncPrice();
    }

    /// <summary>Read the chosen save so the grant can be costed, reporting any reason it can't take one. The read
    /// parses the save's biggest record, so it runs behind a wait cursor.</summary>
    private void OnSaveSelected()
    {
        _grantCtx = null;
        if (_savePicker.SelectedItem is not SaveEntry save)
        {
            _saveStatus.Text = "Pick the save to add the ship to.";
            SyncPrice();
            return;
        }

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            _grantCtx = SaveGrant.ReadContext(save);
            _saveStatus.Text = $"The ship will appear near {_grantCtx.PlayerShipRegId}, about 3–5 km out.";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            _saveStatus.Text = ex.Message;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
        SyncPrice();
    }

    /// <summary>Enable the price controls only once a usable save is chosen, and show the resulting balance.</summary>
    private void SyncPrice()
    {
        var usable = _grantCtx is not null;
        _charge.IsEnabled = usable;
        _price.IsEnabled = usable && _charge.IsChecked == true;

        if (_grantCtx is not { } ctx) _balanceLine.Text = "";
        else if (_charge.IsChecked != true) _balanceLine.Text = $"Balance: {Money(ctx.Balance)} (unchanged — it's a gift)";
        else if (!Affordable()) _balanceLine.Text = $"Balance: {Money(ctx.Balance)} — not enough for {Money(Price)}.";
        else _balanceLine.Text = $"Balance: {Money(ctx.Balance)}  →  {Money(ctx.Balance - Price)}";

        SyncMode();
    }

    private static string Money(double v) => "$" + v.ToString("#,##0.##", CultureInfo.InvariantCulture);

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

        if (Mode == ExportMode.Save)
        {
            if (_grantCtx is null)
            {
                Dlg.Info(this, "Add to save", "Pick a save game to add the ship to.");
                return;
            }
            if (!Affordable())
            {
                Dlg.Info(this, "Add to save",
                    $"That price is more than the character has ({Money(_grantCtx.Balance)}). " +
                    "Lower it, or untick \"Charge for the ship\".");
                return;
            }
            DialogResult = true;
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
