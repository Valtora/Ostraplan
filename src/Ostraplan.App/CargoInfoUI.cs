using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// What one item in the container view is: the panel the game shows for an object, plus the raw conditions a save
/// editor wants (#37). Opened with Alt+click on a tile, or right-click ▸ Info.
///
/// <para><b>The game's own panel is short</b>, and this says which half is which rather than padding it out. Its
/// mega tool tip carries a name, a description, the factions the object belongs to, a price, and any condition
/// declaring <c>nDisplayType == 1</c> — of which there are four in the whole of stock data, so an ordinary crate
/// shows none. Everything under RAW CONDITIONS is Ostraplan's addition and labelled as such.</para>
///
/// <para>The name is the rename box, exactly as the ship inspector's is (#30): it reads as a plain line until you
/// click it, takes the whole name so typing replaces it, lands on Enter or focus loss, and Escape puts it back.
/// Clearing it, or typing the stock name back, returns the item to its def's name.</para>
/// </summary>
public sealed class CargoInfoWindow : Window
{
    private static Brush Ink => ThemeManager.Ink;
    private static Brush Dim => ThemeManager.Dim;

    private Func<CargoInfo?> _read;
    private Action<string?> _rename;
    private readonly StackPanel _body = new() { Margin = new Thickness(16) };
    private string _wasOnFocus = "";

    /// <param name="read">Re-reads the item on demand, so the panel survives an edit made behind it and closes
    /// itself when the item is removed. A snapshot would go stale the moment anything moved.</param>
    /// <param name="rename">Commits a new name, or null to clear it. No-ops are the caller's to filter.</param>
    public CargoInfoWindow(Func<CargoInfo?> read, Action<string?> rename)
    {
        _read = read;
        _rename = rename;

        Title = "Item";
        Width = 340;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.WindowBg;

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _body };
        Refresh();
    }

    /// <summary>Point the open panel at a different item, rather than opening a second one describing something
    /// else with nothing on screen to say which is which.</summary>
    public void Retarget(Func<CargoInfo?> read, Action<string?> rename)
    {
        _read = read;
        _rename = rename;
        Refresh();
    }

    /// <summary>Rebuild from the live item. Closes the window when the item has gone, which is what a removal
    /// while this is open looks like from here.</summary>
    public void Refresh()
    {
        if (_read() is not { } info) { Close(); return; }

        Title = "Item — " + info.Name;
        _body.Children.Clear();

        // ---- the game's own panel ----
        _body.Children.Add(NameRow(info));
        _body.Children.Add(new TextBlock
        {
            Text = info.Name == info.StockName ? info.DefName : $"{info.StockName}  ·  {info.DefName}",
            Foreground = Dim, FontSize = 11, Margin = new Thickness(0, 2, 0, 10), TextWrapping = TextWrapping.Wrap,
        });

        if (info.Desc is { Length: > 0 } desc)
            _body.Children.Add(new TextBlock
            {
                Text = desc, Foreground = Ink, FontSize = 12,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
            });

        // The game prints "Factions: n/a" for an object belonging to none, and most cargo belongs to none. Said
        // the same way here so a blank line is not mistaken for missing data.
        _body.Children.Add(Row("Factions", info.Factions.Count == 0 ? "n/a" : string.Join(", ", info.Factions)));

        if (info.Price is { } price)
            _body.Children.Add(Row("Value", "~$" + price.ToString("n0", System.Globalization.CultureInfo.InvariantCulture)));

        foreach (var f in info.Figures) _body.Children.Add(Row(f.Label, f.Value, f.Desc));

        if (info.Gases.Count > 0)
        {
            _body.Children.Add(Header("CONTENTS"));
            foreach (var g in info.Gases) _body.Children.Add(Row(g.Label, g.Value));
        }

        // ---- Ostraplan's own ----
        if (info.RawConds.Count > 0)
        {
            var raw = new StackPanel();
            foreach (var c in info.RawConds) raw.Children.Add(Row(c.Label, c.Value));
            _body.Children.Add(new Expander
            {
                Header = $"RAW CONDITIONS ({info.RawConds.Count})",
                Foreground = Dim,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 12, 0, 0),
                Content = raw,
            });
            _body.Children.Add(new TextBlock
            {
                Text = "The def's own values. The game does not show these; they are here for save editing.",
                Foreground = Dim, FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
            });
        }
    }

    /// <summary>The name, as an editable line. A def that resolves to nothing cannot be renamed, so it falls back
    /// to a plain heading rather than a box that would write to nowhere.</summary>
    private UIElement NameRow(CargoInfo info)
    {
        if (!info.Renameable)
            return new TextBlock { Text = info.Name, Foreground = Ink, FontSize = 16, FontWeight = FontWeights.Bold };

        var box = new TextBox
        {
            Text = info.Name,
            Foreground = Ink,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Padding = new Thickness(0),
            MaxLength = Rename.MaxLength,
            ToolTip = "Type a name. Clear it, or type the stock name, to put it back.",
        };

        box.GotKeyboardFocus += (_, _) =>
        {
            _wasOnFocus = box.Text;
            box.SelectAll();   // the whole name, so typing replaces rather than appends
            box.Background = ThemeManager.FieldBg;
            box.BorderThickness = new Thickness(1);
        };
        box.LostKeyboardFocus += (_, _) =>
        {
            box.Background = Brushes.Transparent;
            box.BorderThickness = new Thickness(0);
            Commit(box, info);
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(box, info); Keyboard.ClearFocus(); e.Handled = true; }
            else if (e.Key == Key.Escape) { box.Text = _wasOnFocus; Keyboard.ClearFocus(); e.Handled = true; }
        };
        return box;
    }

    /// <summary>Commit a typed name, unless it says nothing new. Typing the stock name back means "no name", the
    /// same rule the ship inspector's box uses — which matters here because the box shows that name to begin
    /// with.</summary>
    private void Commit(TextBox box, CargoInfo info)
    {
        var typed = Rename.Clean(box.Text);
        var wanted = typed == info.StockName ? null : typed;
        if (wanted == (info.Name == info.StockName ? null : info.Name)) return;
        _rename(wanted);
        Refresh();
    }

    private static UIElement Row(string label, string value, string? desc = null)
    {
        var row = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };

        // A two-column grid rather than a docked pair, because the value has to WRAP: a hold's worth of factions
        // runs to three names and a right-docked block just clips at the window edge with no sign it did.
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var l = new TextBlock { Text = label, Foreground = Dim, FontSize = 12, VerticalAlignment = VerticalAlignment.Top };
        var v = new TextBlock
        {
            Text = value, Foreground = Ink, FontSize = 12,
            Margin = new Thickness(12, 0, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Right,
        };
        Grid.SetColumn(l, 0);
        Grid.SetColumn(v, 1);
        line.Children.Add(l);
        line.Children.Add(v);
        row.Children.Add(line);
        if (desc is { Length: > 0 })
            row.Children.Add(new TextBlock
            {
                Text = desc, Foreground = Dim, FontSize = 10,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0),
            });
        return row;
    }

    private static TextBlock Header(string text) => new()
    {
        Text = text, Foreground = Dim, FontWeight = FontWeights.Bold, FontSize = 11,
        Margin = new Thickness(0, 12, 0, 4),
    };
}
