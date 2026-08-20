using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// Browses the ship templates the game would see — core plus every loaded mod's
/// <c>data/ships</c> — as a searchable list. Double-click or Import loads the selection
/// as a fresh editable design.
/// </summary>
public sealed class TemplateBrowserDialog : Window
{
    private static Brush Ink => ThemeManager.Ink;
    private static Brush Dim => ThemeManager.Dim;
    private static Brush FieldBg => ThemeManager.FieldBg;

    private readonly ListBox _list;
    private readonly IReadOnlyList<ShipFileEntry> _all;

    public ShipFileEntry? Selected { get; private set; }

    public TemplateBrowserDialog(IReadOnlyList<ShipFileEntry> ships, DocumentKind kind = DocumentKind.Ship)
    {
        _all = ships;

        Title = kind == DocumentKind.Residence ? "Import an apartment template" : "Import a ship template";
        Width = 460; Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.WindowBg;

        var root = new DockPanel { Margin = new Thickness(16) };

        var search = new TextBox
        {
            Foreground = Ink, Background = FieldBg,
            BorderBrush = ThemeManager.PanelBorder,
            Padding = new Thickness(5, 3, 5, 3), CaretBrush = Ink, Margin = new Thickness(0, 0, 0, 6),
        };
        search.TextChanged += (_, _) => Refresh(search.Text);
        DockPanel.SetDock(search, Dock.Top);
        root.Children.Add(search);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var ok = new Button { Content = "Import", Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 4, 16, 4), IsCancel = true };
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        _list = new ListBox
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            ItemTemplate = RowTemplate(), HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        _list.MouseDoubleClick += (_, _) => Accept();
        _list.KeyDown += (_, e) => { if (e.Key == Key.Enter) Accept(); };
        root.Children.Add(_list);

        Content = root;
        Refresh("");
        search.Focus();
    }

    private void Refresh(string search)
    {
        var q = search.Trim();
        _list.ItemsSource = _all
            .Where(e => q.Length == 0 || e.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (_list.Items.Count > 0 && _list.SelectedIndex < 0) _list.SelectedIndex = 0;
    }

    private void Accept()
    {
        if (_list.SelectedItem is not ShipFileEntry entry) return;
        Selected = entry;
        DialogResult = true;
    }

    private static DataTemplate RowTemplate() =>
        TwoLineRow(nameof(ShipFileEntry.Name), nameof(ShipFileEntry.OriginLabel));

    internal static DataTemplate TwoLineRow(string titleProp, string subProp) =>
        Row(titleProp, subProp, null);

    /// <summary>A row with a third, quieter line for metadata that tells otherwise-identical entries apart.</summary>
    internal static DataTemplate ThreeLineRow(string titleProp, string subProp, string metaProp) =>
        Row(titleProp, subProp, metaProp);

    private static DataTemplate Row(string titleProp, string subProp, string? metaProp)
    {
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(titleProp));
        name.SetValue(TextBlock.ForegroundProperty, Ink);
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);

        var sub = new FrameworkElementFactory(typeof(TextBlock));
        sub.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(subProp));
        sub.SetValue(TextBlock.ForegroundProperty, metaProp is null ? Dim : Ink);
        sub.SetValue(TextBlock.FontSizeProperty, metaProp is null ? 11.0 : 12.0);

        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(MarginProperty, new Thickness(2, 3, 2, 3));
        panel.AppendChild(name);
        panel.AppendChild(sub);

        if (metaProp is not null)
        {
            var meta = new FrameworkElementFactory(typeof(TextBlock));
            meta.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(metaProp));
            meta.SetValue(TextBlock.ForegroundProperty, Dim);
            meta.SetValue(TextBlock.FontSizeProperty, 11.0);
            meta.SetValue(MarginProperty, new Thickness(0, 1, 0, 0));
            panel.AppendChild(meta);
        }

        return new DataTemplate { VisualTree = panel };
    }
}

/// <summary>Where a ship being read in for comparison comes from. Not an import: the ship is read, measured,
/// and dropped, and the design on the canvas is never touched.</summary>
public enum ShipSourceKind
{
    /// <summary>A saved .oplan design.</summary>
    Design,
    /// <summary>A ship template from the install or a loaded mod.</summary>
    Template,
    /// <summary>A ship in one of the player's save games.</summary>
    Save,
}

/// <summary>A row in the source-kind picker.</summary>
public sealed record SourceKindRow(string Title, string Sub, ShipSourceKind Kind);

/// <summary>Asks which kind of ship to read in — a design, a ship template, or a ship in a save — before handing
/// off to the picker for that kind. The three readers already exist; this only chooses between them.</summary>
public sealed class ShipSourceDialog : Window
{
    private readonly ListBox _list;

    public ShipSourceKind? Selected { get; private set; }

    public ShipSourceDialog(string title, string note)
    {
        Title = title;
        Width = 460; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.WindowBg;

        var rows = new List<SourceKindRow>
        {
            new("A design", "One of your saved .oplan designs.", ShipSourceKind.Design),
            new("A ship template", "A stock or modded ship from your Ostranauts install.", ShipSourceKind.Template),
            new("A ship in a save", "A ship you own in one of your save games.", ShipSourceKind.Save),
        };

        var root = new DockPanel { Margin = new Thickness(16) };

        var noteBlock = new TextBlock
        {
            Text = note, Foreground = ThemeManager.Dim, FontSize = 11,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        };
        DockPanel.SetDock(noteBlock, Dock.Top);
        root.Children.Add(noteBlock);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var ok = new Button { Content = "Choose", Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 4, 16, 4), IsCancel = true };
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        _list = new ListBox
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            ItemsSource = rows, SelectedIndex = 0,
            ItemTemplate = TemplateBrowserDialog.TwoLineRow(nameof(SourceKindRow.Title), nameof(SourceKindRow.Sub)),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        _list.MouseDoubleClick += (_, _) => Accept();
        _list.KeyDown += (_, e) => { if (e.Key == Key.Enter) Accept(); };
        root.Children.Add(_list);

        Content = root;
    }

    private void Accept()
    {
        if (_list.SelectedItem is not SourceKindRow row) return;
        Selected = row.Kind;
        DialogResult = true;
    }
}

/// <summary>A row in the ship picker: the ship name (with a "you are here" tag) over its make/model/RegID
/// subtitle (with a "NOT OWNED" tag for stations/other vessels).</summary>
public sealed record ShipRow(string Title, string Sub, SaveShipChoice Choice);

/// <summary>Picks WHICH ship to edit from a save: the player's owned ships (from aMyShips), plus the ship they're
/// currently on if it isn't owned (a station/other vessel — editable but unsupported).</summary>
public sealed class ShipChoiceDialog : Window
{
    private readonly ListBox _list;

    public SaveShipChoice? Selected { get; private set; }

    /// <param name="kind">Which kind the caller has already filtered the list to, for the wording and the row
    /// tags. <b>Null for a mixed list</b>: every vessel and every apartment together, each row tagged with which
    /// it is. That is what a read wants. Nothing is written on those paths, so there is no wrong row to land on,
    /// and filtering would only make the user pick the errand before they pick the thing.</param>
    /// <param name="title">Overrides the window title, for a caller whose errand is not editing.</param>
    /// <param name="note">Overrides the line above the list, likewise.</param>
    public ShipChoiceDialog(string saveName, IReadOnlyList<SaveShipChoice> ships,
        DocumentKind? kind = DocumentKind.Ship, string? title = null, string? note = null)
    {
        Title = title ?? kind switch
        {
            DocumentKind.Residence => "Choose an apartment to edit",
            DocumentKind.Ship => "Choose a ship to edit",
            _ => "Choose a ship or apartment",
        };
        Width = 480; Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.WindowBg;

        var rows = ships.Select(c => new ShipRow(
            c.Name + (c.Current ? "   ·   you are here" : ""),
            Subtitle(c, kind),
            c)).ToList();

        var root = new DockPanel { Margin = new Thickness(16) };

        var noteBlock = new TextBlock
        {
            Text = note ?? kind switch
            {
                // The apartment note says where they come from, because "apartments you own" is a list nobody has
                // seen before and the game itself never shows one: an apartment is registered somewhere different
                // from your vessels, which is why they can be listed apart in the first place.
                DocumentKind.Residence =>
                    $"Apartments in save “{saveName}” that you own, one row per station residence registered to "
                    + "your character. Editing one keeps its registration, its place at the station and the transit "
                    + "route that reaches it; only the layout changes.",
                DocumentKind.Ship =>
                    $"Ships in save “{saveName}” that you own. Ostranauts imports the ship you're standing "
                    + "on, which may be a station — pick the one you mean. Ships you don't own are shown but editing "
                    + "them is unsupported and may break your save.",
                _ =>
                    $"Everything you own in save “{saveName}”: every ship, and every apartment registered to your "
                    + "character. The one you are standing on is only one of them, and it may be a station.",
            },
            Foreground = ThemeManager.Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        };
        DockPanel.SetDock(noteBlock, Dock.Top);
        root.Children.Add(noteBlock);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var ok = new Button { Content = "Choose", Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 4, 16, 4), IsCancel = true };
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        _list = new ListBox
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            ItemsSource = rows, ItemTemplate = TemplateBrowserDialog.TwoLineRow(nameof(ShipRow.Title), nameof(ShipRow.Sub)),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        var currentIdx = rows.FindIndex(r => r.Choice.Current);
        _list.SelectedIndex = currentIdx >= 0 ? currentIdx : (rows.Count > 0 ? 0 : -1);
        _list.MouseDoubleClick += (_, _) => Accept();
        _list.KeyDown += (_, e) => { if (e.Key == Key.Enter) Accept(); };
        root.Children.Add(_list);

        Content = root;
    }

    /// <summary>The subtitle line: make/model/RegID, led by what the row is when the list mixes both kinds, and
    /// tailed by the not-owned warning for the station or other vessel the player happens to be standing on.</summary>
    private static string Subtitle(SaveShipChoice c, DocumentKind? kind)
    {
        var sub = kind is null ? (c.IsResidence ? "APARTMENT   ·   " : "SHIP   ·   ") + c.Sub : c.Sub;
        return c.Owned ? sub : sub + "   ·   NOT OWNED — station/other vessel (unsupported)";
    }

    private void Accept()
    {
        if (_list.SelectedItem is not ShipRow row) return;
        Selected = row.Choice;
        DialogResult = true;
    }
}

/// <summary>
/// A save-game row for the picker: the <b>character</b>, then where they are, then the metadata that tells two
/// otherwise-identical saves apart.
///
/// <para>The ship used to lead and the character to trail it, which reads badly for the commonest case there is:
/// several saves of one character docked at the same station, where the leading line was the same on every row and
/// the thing distinguishing them was the folder name at the end of a dim subtitle.</para>
/// </summary>
public sealed record SaveRow(string Character, string Where, string Meta, SaveEntry Entry);

/// <summary>Picks a save game. Used by every flow that needs one — importing a layout, importing for editing,
/// transferring, choosing a ship to overwrite — so the title and the note above the list are the caller's.</summary>
public sealed class SavePickerDialog : Window
{
    private static Brush Ink => ThemeManager.Ink;
    private static Brush FieldBg => ThemeManager.FieldBg;

    private readonly ListBox _list;

    public SaveEntry? Selected { get; private set; }

    public SavePickerDialog(IReadOnlyList<SaveEntry> saves, string? title = null, string? note = null,
        string? acceptVerb = null)
    {
        Title = title ?? "Choose a save game";
        Width = 500; Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.WindowBg;

        var rows = saves.Select(s => new SaveRow(
            s.PlayerName.Length > 0 ? s.PlayerName : "(unnamed character)",
            s.ShipName.Length > 0 ? s.ShipName : "(unnamed ship)",
            Meta(s), s)).ToList();

        var root = new DockPanel { Margin = new Thickness(16) };

        if (note is { Length: > 0 })
        {
            var noteBlock = new TextBlock
            {
                Text = note,
                Foreground = ThemeManager.Dim, FontSize = 11,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            };
            DockPanel.SetDock(noteBlock, Dock.Top);
            root.Children.Add(noteBlock);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var ok = new Button { Content = acceptVerb ?? "Choose", Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 4, 16, 4), IsCancel = true };
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        _list = new ListBox
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            ItemsSource = rows,
            ItemTemplate = TemplateBrowserDialog.ThreeLineRow(
                nameof(SaveRow.Character), nameof(SaveRow.Where), nameof(SaveRow.Meta)),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        if (rows.Count > 0) _list.SelectedIndex = 0;
        _list.MouseDoubleClick += (_, _) => Accept();
        _list.KeyDown += (_, e) => { if (e.Key == Key.Enter) Accept(); };
        root.Children.Add(_list);

        Content = root;
    }

    /// <summary>The metadata line: when the save was written, how long it has been played, the game build, and the
    /// folder name. The same facts the game's own Load screen shows, in the same order, because that is what a
    /// player is already matching a save against. The folder name goes last: it is the least readable of them and
    /// the one only needed when the rest tie.</summary>
    internal static string Meta(SaveEntry s) =>
        string.Join("  ·  ", new[] { Written(s.When), PlayTime(s.PlayTimeSeconds), Build(s), s.Name }
            .Where(x => x.Length > 0));

    /// <summary>
    /// The game build, as bare version numbers: <c>1.0.0.9</c>, or <c>0.15.1.15 → 1.0.0.9</c> for a save made on
    /// one build and last written by another.
    ///
    /// <para>Both halves earn their place. The creating build is what the game's own Load screen shows and what a
    /// player recognises the save by; the arrow is the only thing that says the file on disk has been through an
    /// update, which is exactly the question asked of a save that will not open. The "Early Access Build:" /
    /// "Release Build:" prefix is dropped — 0.x against 1.x already says it, in a quarter of the width.</para>
    /// </summary>
    private static string Build(SaveEntry s)
    {
        var made = Number(s.GameVersion);
        var last = Number(s.LastSavedVersion);
        if (made.Length == 0) return last;
        return last.Length == 0 || last == made ? made : $"{made} → {last}";
    }

    /// <summary>The version number out of "Early Access Build: 0.15.1.15". Anything not in that shape is kept
    /// whole rather than mangled.</summary>
    private static string Number(string version) =>
        version.LastIndexOf(": ", StringComparison.Ordinal) is var i && i >= 0
            ? version[(i + 2)..].Trim()
            : version.Trim();

    /// <summary>The save's timestamp in the reader's own date format. The file records
    /// <c>yyyy-MM-dd HH:mm:ss</c>; anything that doesn't parse is shown verbatim rather than dropped.</summary>
    private static string Written(string when) =>
        DateTime.TryParseExact(when, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None,
            out var dt)
            ? dt.ToString("d MMM yyyy HH:mm", CultureInfo.CurrentCulture)
            : when;

    /// <summary>Elapsed play time the way the game writes it: <c>1d 10h 52m 14s</c>, with the empty leading units
    /// dropped. <c>playTimeElapsed</c> is in seconds.</summary>
    private static string PlayTime(double seconds)
    {
        if (seconds <= 0) return "";
        var t = TimeSpan.FromSeconds(Math.Round(seconds));
        return t.Days > 0 ? $"{t.Days}d {t.Hours}h {t.Minutes}m"
            : t.Hours > 0 ? $"{t.Hours}h {t.Minutes}m"
            : $"{t.Minutes}m {t.Seconds}s";
    }

    private void Accept()
    {
        if (_list.SelectedItem is not SaveRow row) return;
        Selected = row.Entry;
        DialogResult = true;
    }
}
