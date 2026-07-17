using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>One unresolved def in the stand-in dialog: how many items use it, and the part chosen to take its
/// place (null = leave the modded items alone).</summary>
public sealed class MissingDefVM(string defName, int count)
{
    public string DefName { get; } = defName;
    public int Count { get; } = count;
    public PartDef? StandIn { get; set; }

    public string Heading => Count > 1 ? $"{DefName}  ×{Count}" : DefName;
    public string ChoiceText => StandIn is null ? "Leave in place" : $"→ {StandIn.Friendly}";
}

/// <summary>
/// The missing-mod stand-in prompt for a ship imported from a save.
///
/// <para>Loud by design. An item whose def isn't in the loaded data is <b>invisible</b> to Ostraplan yet still
/// sits in the save, and every engine here (rooms, the grid frame, the rating) reads the document — so a missing
/// modded wall lets a room flood through it and a missing part at the hull edge mis-sizes the grid, either of
/// which corrupts the ship on write-back. The honest fixes are to enable the mod, or to put a real part in its
/// place; this dialog offers the second. A stand-in genuinely <b>replaces</b> the item in the written save, so it
/// says so plainly rather than implying the modded part survives.</para>
/// </summary>
public sealed class MissingPartsDialog : Window
{
    private static Brush Ink => ThemeManager.Ink;
    private static Brush Dim => ThemeManager.Dim;

    private readonly IReadOnlyList<MissingDefVM> _defs;
    private readonly IReadOnlyList<PartVM> _palette;

    /// <summary>The chosen stand-ins by def name — empty when the user leaves everything in place.</summary>
    public IReadOnlyDictionary<string, PartDef> Choices =>
        _defs.Where(d => d.StandIn is not null).ToDictionary(d => d.DefName, d => d.StandIn!, StringComparer.Ordinal);

    public MissingPartsDialog(IReadOnlyList<MissingDefVM> defs, IReadOnlyList<PartVM> palette, string shipName)
    {
        _defs = defs;
        _palette = palette;

        Title = "Missing mods";
        Width = 620; Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.WindowBg;

        var root = new DockPanel { Margin = new Thickness(16) };

        var items = defs.Sum(d => d.Count);
        var head = new TextBlock
        {
            Text = $"{items} item{(items == 1 ? "" : "s")} on {shipName} use parts that aren't in your loaded data",
            Foreground = ThemeManager.Warn, FontSize = 15, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        };
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var note = new TextBlock
        {
            Text = "Ostraplan can't see them, so it can't lay them out or work out the ship's rooms and grid around "
                   + "them. Writing back to your save in this state can corrupt rooms and zones.\n\n"
                   + "Best fix: cancel, enable the mods these parts come from (Ostrasort will confirm they're "
                   + "subscribed and enabled), and import again.\n\n"
                   + "Otherwise you can stand a real part in for each. A stand-in REPLACES the item in the save "
                   + "you write — the original modded part is not kept — so pick something the same size where you can.",
            Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        };
        DockPanel.SetDock(note, Dock.Top);
        root.Children.Add(note);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var apply = new Button { Content = "Apply stand-ins", Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var leave = new Button { Content = "Leave them all", Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel import", Padding = new Thickness(16, 4, 16, 4), IsCancel = true };
        apply.Click += (_, _) => { DialogResult = true; };
        leave.Click += (_, _) => { foreach (var d in _defs) d.StandIn = null; DialogResult = true; };
        buttons.Children.Add(apply);
        buttons.Children.Add(leave);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = BuildRows() };
        root.Children.Add(scroll);

        Content = root;
    }

    /// <summary>A row per unresolved def: its name and use count, and a button that opens the part picker.
    /// Built imperatively (rather than as a DataTemplate) because each row's button needs to mutate its own VM
    /// and refresh its caption.</summary>
    private StackPanel BuildRows()
    {
        var panel = new StackPanel();
        foreach (var def in _defs)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };

            var choice = new Button { Padding = new Thickness(10, 3, 10, 3), MinWidth = 190, Content = def.ChoiceText };
            var clear = new Button { Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(6, 0, 0, 0), Content = "Clear", IsEnabled = def.StandIn is not null };
            choice.Click += (_, _) =>
            {
                var dlg = new ReplacePickerDialog(
                    _palette, def.DefName,
                    title: $"Stand in for {def.DefName}",
                    noteText: $"Pick a part to take the place of {def.DefName} (×{def.Count}). It will REPLACE those "
                              + "items in the save you write back — the modded part is not kept. Prefer one with the "
                              + "same footprint, or the ship's rooms and grid will shift.")
                { Owner = this };
                if (dlg.ShowDialog() != true || dlg.Selected is not { } part) return;
                def.StandIn = part;
                choice.Content = def.ChoiceText;
                clear.IsEnabled = true;
            };
            clear.Click += (_, _) =>
            {
                def.StandIn = null;
                choice.Content = def.ChoiceText;
                clear.IsEnabled = false;
            };

            DockPanel.SetDock(clear, Dock.Right);
            DockPanel.SetDock(choice, Dock.Right);
            row.Children.Add(clear);
            row.Children.Add(choice);
            row.Children.Add(new TextBlock
            {
                Text = def.Heading, Foreground = Ink, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            });
            panel.Children.Add(row);
        }
        return panel;
    }
}
