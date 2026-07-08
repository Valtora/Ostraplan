using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ostraplan.App;

/// <summary>
/// Opt-in prompt for <see cref="SelfInstall"/>: install the exe into
/// %LOCALAPPDATA%\Programs\Ostraplan and pick which shortcuts to create. Hand-built and themed the same
/// way as the app's other dialogs (no XAML), matching <see cref="Dlg"/>'s look. (Ostrasort's InstallDialog
/// is XAML-backed; the behaviour is identical.)
/// </summary>
public sealed class InstallDialog : Window
{
    private static Brush Ink => ThemeManager.Ink;
    private static Brush Dim => ThemeManager.Dim;

    private readonly CheckBox _desktop = new() { Content = "Create a Desktop shortcut", IsChecked = true };
    private readonly CheckBox _startMenu = new() { Content = "Create a Start Menu shortcut", IsChecked = true };
    private bool _install;

    private InstallDialog(bool alreadyInstalled)
    {
        Title = alreadyInstalled ? "Ostraplan shortcuts" : "Install Ostraplan";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = ThemeManager.WindowBg;

        var header = alreadyInstalled ? "Refresh your Ostraplan shortcuts?" : "Install Ostraplan for easy launching?";
        var body = alreadyInstalled
            ? $"Ostraplan is already installed at\n{SelfInstall.InstallDir}\n\n" +
              "Choose which shortcuts to (re)create. The installed copy is left as-is."
            : "This copies Ostraplan.exe to\n" +
              $"{SelfInstall.InstallDir}\n\n" +
              "so you have one fixed place to keep and update it, and creates the shortcuts you pick below. " +
              "No admin rights needed, nothing is written outside your user profile, and you can delete that " +
              "folder any time to uninstall.";

        var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 16), MaxWidth = 460 };
        panel.Children.Add(new TextBlock
        {
            Text = header, Foreground = Ink, FontWeight = FontWeights.SemiBold, FontSize = 14,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = body, Foreground = Dim, TextWrapping = TextWrapping.Wrap, LineHeight = 20,
            Margin = new Thickness(0, 0, 0, 14),
        });
        _desktop.Foreground = Ink;
        _desktop.Margin = new Thickness(0, 0, 0, 6);
        _startMenu.Foreground = Ink;
        _startMenu.Margin = new Thickness(0, 0, 0, 16);
        panel.Children.Add(_desktop);
        panel.Children.Add(_startMenu);

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button
        {
            Content = alreadyInstalled ? "Create shortcuts" : "Install",
            Padding = new Thickness(16, 5, 16, 5), MinWidth = 84, IsDefault = true,
            Background = ThemeManager.AccentBg, Foreground = ThemeManager.AccentText,
        };
        var cancel = new Button
        {
            Content = "Not now", Padding = new Thickness(16, 5, 16, 5), MinWidth = 84,
            Margin = new Thickness(8, 0, 0, 0), IsCancel = true,
        };
        ok.Click += (_, _) => { _install = true; DialogResult = true; };
        row.Children.Add(ok);
        row.Children.Add(cancel);
        panel.Children.Add(row);

        Content = panel;
    }

    /// <summary>Shows the dialog; returns the shortcut choice, or null if the user declined ("Not now" / Esc).</summary>
    public static (bool Desktop, bool StartMenu)? Show(Window owner, bool alreadyInstalled)
    {
        var dlg = new InstallDialog(alreadyInstalled)
        {
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        dlg.ShowDialog();
        return dlg._install ? (dlg._desktop.IsChecked == true, dlg._startMenu.IsChecked == true) : null;
    }
}
