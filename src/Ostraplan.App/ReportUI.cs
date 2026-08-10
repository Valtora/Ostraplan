using System.Windows;
using System.Windows.Controls;

namespace Ostraplan.App;

/// <summary>
/// The shared shell for a <b>modeless</b> analysis report: the Ship Rating and the Diagnostics checklist. Both are
/// things you read <i>while</i> looking at the ship, so blocking the viewport for the whole time one is open was
/// exactly backwards (raised in discussion #22, about reading Value Opportunities while editing).
///
/// <para>Being modal is what used to guarantee the report and the canvas agreed. Nothing guarantees that once you
/// can edit underneath it, so the window says so rather than letting the figures quietly stop being true:
/// <see cref="MarkStale"/> raises a bar across the top the moment the design changes, and its <b>Re-run</b> button
/// asks the host to recompute. <see cref="SetBody"/> installs a fresh report and lowers the bar again.</para>
///
/// <para>The window is disabled outright while the host has the document frozen (see <c>MainWindow.FreezeDoc</c>).
/// It is an edit route like any other — its dead-weight box writes to the live document — so it has to close with
/// the rest of them while an engine is reading.</para>
/// </summary>
public abstract class ReportWindow : Window
{
    private readonly Border _staleBar;
    private readonly ContentControl _host = new();

    /// <summary>Raised when the user clicks Re-run. The host recomputes and calls the derived window's own setter,
    /// which goes through <see cref="SetBody"/> and clears the stale bar.</summary>
    public event Action? RerunRequested;

    protected ReportWindow()
    {
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.WindowBg;

        var rerun = new Button
        {
            Content = "Re-run",
            Padding = new Thickness(12, 2, 12, 2),
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        rerun.Click += (_, _) => RerunRequested?.Invoke();

        var bar = new DockPanel();
        DockPanel.SetDock(rerun, Dock.Right);
        bar.Children.Add(rerun);
        bar.Children.Add(new TextBlock
        {
            Text = "The design has changed since this ran, so these figures describe the earlier ship.",
            Foreground = ThemeManager.Warn, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        });

        _staleBar = new Border
        {
            Background = ThemeManager.PanelBg,
            BorderBrush = ThemeManager.Warn,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(18, 8, 18, 8),
            Child = bar,
            Visibility = Visibility.Collapsed,
        };

        var root = new DockPanel();
        DockPanel.SetDock(_staleBar, Dock.Top);
        root.Children.Add(_staleBar);
        root.Children.Add(_host);
        Content = root;
    }

    /// <summary>Install the report's content. A fresh body is fresh data, so this lowers the stale bar.</summary>
    protected void SetBody(UIElement body)
    {
        _host.Content = body;
        _staleBar.Visibility = Visibility.Collapsed;
    }

    /// <summary>Say the design has moved on since this report was measured.</summary>
    public void MarkStale() => _staleBar.Visibility = Visibility.Visible;
}
