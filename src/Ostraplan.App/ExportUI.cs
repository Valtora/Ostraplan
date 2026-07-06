using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace Ostraplan.App;

/// <summary>
/// Collects the export settings: ship name, mod metadata, and where to write the mod folder —
/// staged straight into the game's Mods folder (ready to register &amp; test) or to a folder the
/// user picks. Deliberately states that registration in <c>loading_order.json</c> is left to
/// ModTools/Ostrasort — Ostraplan writes the mod folder only, never the load order.
/// </summary>
public sealed class ExportDialog : Window
{
    private static Brush Ink => ThemeManager.Ink;
    private static Brush Dim => ThemeManager.Dim;
    private static Brush FieldBg => ThemeManager.FieldBg;

    private readonly TextBox _name, _author, _version, _notes;
    private readonly RadioButton _toMods, _toFolder;
    private readonly TextBlock _folderPath;
    private readonly string? _modsDir;
    private string? _pickedFolder;

    public string ShipName => _name.Text.Trim();
    public string Author => _author.Text.Trim();
    public string Notes => _notes.Text.Trim();
    public string ModVersion => _version.Text.Trim();
    public bool StagedIntoMods => _toMods.IsChecked == true;
    public string DestinationParent => StagedIntoMods ? _modsDir! : _pickedFolder!;

    public ExportDialog(string defaultName, string defaultAuthor, string? modsDir, string? lastFolder)
    {
        _modsDir = modsDir;
        _pickedFolder = lastFolder;

        Title = "Export as spawnable mod";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = ThemeManager.WindowBg;

        var body = new StackPanel { Margin = new Thickness(18) };

        _name = Field(body, "Ship name", defaultName);
        _author = Field(body, "Author", defaultAuthor);
        _version = Field(body, "Mod version", "1.0.0");
        _notes = Field(body, "Notes (optional)", "", multiline: true);

        body.Children.Add(new TextBlock { Text = "DESTINATION", Foreground = Dim, FontWeight = FontWeights.Bold, FontSize = 11, Margin = new Thickness(0, 14, 0, 4) });

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

        body.Children.Add(new TextBlock
        {
            Text = "Ostraplan writes the mod folder only. Registration in loading_order.json is left to " +
                   "Ostrasort/ModTools, so the ship won't appear in-game until you register it there.",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = "Export", Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 4, 16, 4), IsCancel = true };
        ok.Click += (_, _) => OnOk();
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        body.Children.Add(buttons);

        Content = body;
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
