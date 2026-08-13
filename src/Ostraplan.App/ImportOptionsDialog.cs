using System.Windows;
using System.Windows.Controls;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// The last step before an import: what to bring in besides the ship's structure.
///
/// <para>This exists because the answer used to be decided for you and differed by route. Importing a ship "for
/// editing" kept every container's contents; the same ship imported layout-only, or a template, dropped them
/// without saying so. That is what people reported as cargo going missing depending on which menu item they
/// happened to use.</para>
///
/// <para>Not shown for "your ship, for editing", which always brings everything: that design stays linked to the
/// save and its write-back emits cargo from the imported tree, so leaving contents out would delete them from the
/// save.</para>
/// </summary>
public sealed class ImportOptionsDialog : Window
{
    private readonly CheckBox _contents;
    private readonly CheckBox _loose;

    /// <summary>What the user chose. Read only after a true dialog result.</summary>
    public ImportOptions Options => new(_contents.IsChecked == true, _loose.IsChecked == true);

    /// <param name="heading">What is being imported, e.g. the ship or save name.</param>
    /// <param name="note">What this import route does with identity, wear and the rest.</param>
    /// <param name="acceptVerb">The action button's label.</param>
    public ImportOptionsDialog(string heading, string note, ImportOptions initial, string acceptVerb = "Import")
    {
        Title = "Import";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.WindowBg;

        var body = new StackPanel { Margin = new Thickness(20, 18, 20, 16) };
        body.Children.Add(new TextBlock
        {
            Text = heading, Foreground = ThemeManager.Ink, FontWeight = FontWeights.SemiBold, FontSize = 15,
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new TextBlock
        {
            Text = note, Foreground = ThemeManager.Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        _contents = Option("Container contents",
            "Everything inside lockers, racks and crates, as viewable and editable cargo. Right-click a container "
            + "and choose \"View contents\" once it is in.", initial.ContainerContents);
        _loose = Option("Items lying on the deck",
            "Tools, scrap and other loose objects sitting on the floor. They are cargo, not structure, so they take "
            + "no part in the placement law or the bill of materials.", initial.LooseItems);
        body.Children.Add(_contents);
        body.Children.Add(_loose);

        body.Children.Add(new TextBlock
        {
            Text = "Remembered for next time. Crew are never imported.",
            Foreground = ThemeManager.Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var ok = new Button
        {
            Content = acceptVerb, Padding = new Thickness(18, 5, 18, 5), Margin = new Thickness(0, 0, 8, 0),
            MinWidth = 84, IsDefault = true,
            Background = ThemeManager.AccentBg, Foreground = ThemeManager.AccentText,
        };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 5, 16, 5), MinWidth = 84, IsCancel = true };
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        body.Children.Add(buttons);

        Content = body;
    }

    private static CheckBox Option(string label, string detail, bool initial)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = label, Foreground = ThemeManager.Ink, TextWrapping = TextWrapping.Wrap,
        });
        stack.Children.Add(new TextBlock
        {
            Text = detail, Foreground = ThemeManager.Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0), MaxWidth = 380,
        });
        return new CheckBox { Content = stack, IsChecked = initial, Margin = new Thickness(0, 0, 0, 10) };
    }
}
