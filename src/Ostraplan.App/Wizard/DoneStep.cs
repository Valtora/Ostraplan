using System.Windows;
using System.Windows.Controls;

namespace Ostraplan.App.Wizard;

/// <summary>
/// What happened, as the wizard's last pane rather than as a popup on top of it.
///
/// <para>The three flows this replaces each ended in a <c>Dlg.Success</c> box the user had to dismiss before they
/// could read the wizard behind it. Here the result stays where the run happened, with the same content: where it
/// was written, what it cost, and what to do next in game.</para>
/// </summary>
public sealed class DoneStep : WizardStep
{
    private readonly TextBlock _headline;
    private readonly StackPanel _lines;

    public override string Title => "Done";

    public DoneStep()
    {
        var body = Body();

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new Border
        {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = ThemeManager.Good,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = "✓", Foreground = System.Windows.Media.Brushes.White, FontWeight = FontWeights.Bold,
                FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
        _headline = new TextBlock
        {
            Foreground = Ink, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10, 0, 0, 0), MaxWidth = 400, VerticalAlignment = VerticalAlignment.Center,
        };
        head.Children.Add(_headline);
        body.Children.Add(head);

        _lines = Add(body, new StackPanel { Margin = new Thickness(34, 12, 0, 0) });

        Content = body;
    }

    public void Set(DoneReport report)
    {
        _headline.Text = report.Headline;
        _lines.Children.Clear();
        foreach (var line in report.Lines)
            _lines.Children.Add(new TextBlock
            {
                Text = line,
                Foreground = line.Length == 0 ? Dim : Ink,
                FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, line.Length == 0 ? 4 : 0, 0, 6),
                MaxWidth = 440,
            });
    }
}
