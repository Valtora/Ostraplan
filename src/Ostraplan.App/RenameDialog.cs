using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// Names a placed part, the way the game's own rename box does. Clearing the box restores the part's stock name,
/// which is why there is no separate "clear" action: an empty name <i>is</i> no name (see <see cref="Rename"/>).
/// </summary>
public sealed class RenameDialog : Window
{
    private readonly TextBox _box;

    /// <summary>The chosen name, already normalised — null when the user cleared it.</summary>
    public string? ChosenName { get; private set; }

    public RenameDialog(string stockName, string? currentName)
    {
        Title = "Rename";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.WindowBg;

        var body = new StackPanel { Margin = new Thickness(18) };
        body.Children.Add(new TextBlock
        {
            Text = stockName, Foreground = ThemeManager.Ink, FontWeight = FontWeights.SemiBold, FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new TextBlock
        {
            Text = "The name this part goes by in game. Leave it empty to go back to the stock name.",
            Foreground = ThemeManager.Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 10),
        });

        _box = new TextBox
        {
            Text = currentName ?? "",
            MaxLength = Rename.MaxLength,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(5, 3, 5, 3),
        };
        _box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Accept(); };
        body.Children.Add(_box);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var ok = new Button { Content = "Rename", Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 4, 16, 4), IsCancel = true };
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        body.Children.Add(buttons);

        Content = body;
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    private void Accept()
    {
        ChosenName = Rename.Clean(_box.Text);
        DialogResult = true;
    }
}
