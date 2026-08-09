using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>A row in the recovery picker: the design's name over when the snapshot was taken and which design it
/// belongs to.</summary>
public sealed record AutoSaveRow(string Title, string Sub, AutoSaveEntry Entry);

/// <summary>
/// Picks an auto-save snapshot to recover (see <see cref="AutoSaveStore"/>). Newest first, since a recovery is
/// almost always "give me back what I had a minute ago".
/// </summary>
public sealed class AutoSaveRecoveryDialog : Window
{
    private readonly ListBox _list;

    public AutoSaveEntry? Selected { get; private set; }

    public AutoSaveRecoveryDialog(IReadOnlyList<AutoSaveEntry> entries, DateTime now)
    {
        Title = "Recover an auto-save";
        Width = 460; Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.WindowBg;

        var rows = entries.Select(e => new AutoSaveRow(e.DesignName, Describe(e, now), e)).ToList();

        var root = new DockPanel { Margin = new Thickness(16) };

        var note = new TextBlock
        {
            Text = "Opens the snapshot as unsaved changes to the design it came from. Nothing is written until you save.",
            Foreground = ThemeManager.Dim, FontSize = 11,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        };
        DockPanel.SetDock(note, Dock.Top);
        root.Children.Add(note);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var ok = new Button { Content = "Recover", Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 4, 16, 4), IsCancel = true };
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        _list = new ListBox
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            ItemsSource = rows, ItemTemplate = TemplateBrowserDialog.TwoLineRow(nameof(AutoSaveRow.Title), nameof(AutoSaveRow.Sub)),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        if (rows.Count > 0) _list.SelectedIndex = 0;
        _list.MouseDoubleClick += (_, _) => Accept();
        _list.KeyDown += (_, e) => { if (e.Key == Key.Enter) Accept(); };
        root.Children.Add(_list);

        Content = root;
    }

    /// <summary>The row's second line: how long ago, the clock time, and whether the design had a file of its own.</summary>
    private static string Describe(AutoSaveEntry entry, DateTime now)
    {
        var age = now - entry.SavedAt;
        var when = age < TimeSpan.Zero ? "just now"
            : age.TotalMinutes < 1 ? "just now"
            : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes} min ago"
            : age.TotalHours < 24 ? $"{(int)age.TotalHours} hr ago"
            : $"{(int)age.TotalDays} day{((int)age.TotalDays == 1 ? "" : "s")} ago";
        var clock = entry.SavedAt.ToString("ddd HH:mm", CultureInfo.CurrentCulture);
        var origin = entry.IsUntitled ? "never saved to a file" : "recovers onto its own file";
        return $"{when}  ·  {clock}  ·  {origin}";
    }

    private void Accept()
    {
        if (_list.SelectedItem is not AutoSaveRow row) return;
        Selected = row.Entry;
        DialogResult = true;
    }
}
