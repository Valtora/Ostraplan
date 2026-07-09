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

    private readonly TextBox _name, _author, _version, _notes;
    private readonly TextBox _publicName, _make, _model, _year, _designation, _description;
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
    public string Author => _author.Text.Trim();
    public string Notes => _notes.Text.Trim();
    public string ModVersion => _version.Text.Trim();
    public bool StagedIntoMods => _toMods.IsChecked == true;
    public string DestinationParent => StagedIntoMods ? _modsDir! : _pickedFolder!;

    /// <summary>Whether to hand the staged mod to Ostrasort for registration + conflict patching right after
    /// export. Only meaningful when staging into the game's Mods folder; ignored for a plain folder export.</summary>
    public bool RegisterWithOstrasort => _registerOstrasort.IsChecked == true && StagedIntoMods;

    /// <summary>The ship's in-game display name. Falls back to <see cref="ShipName"/> when left
    /// blank, and never literally "$TEMPLATE" — either would make the game re-roll a random name
    /// on every spawn instead of keeping this one (see <c>ExportOptions.PublicName</c>).</summary>
    public string PublicName
    {
        get
        {
            var v = _publicName.Text.Trim();
            return v.Length == 0 || v == "$TEMPLATE" ? ShipName : v;
        }
    }
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
        PublicName,
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
        _author = Field(body, "Author", defaultAuthor);
        _version = Field(body, "Mod version", "1.0.0");
        _notes = Field(body, "Notes (optional)", "", multiline: true);

        Header(body, "SHIP IDENTITY (IN-GAME)");
        _publicName = Field(body, "In-game name", defaultName);
        _make = Field(body, "Make", "");
        _model = Field(body, "Model", "");
        _year = Field(body, "Year", "");
        _designation = Field(body, "Designation (class/role, e.g. \"Salvage Tug\")", "");
        _description = Field(body, "Description (optional)", "", multiline: true);
        body.Children.Add(new TextBlock
        {
            Text = "\"In-game name\" is what shows up at the transponder, comms, and broker listings — it's kept " +
                   "exactly as typed. The others are flavor text (make/model/year/designation/description).",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
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
        DialogResult = true;
    }

    private static double ParseDouble(string text, double fallback) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : fallback;

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
