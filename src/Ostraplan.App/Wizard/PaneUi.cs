using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ostraplan.App.Wizard;

/// <summary>
/// The furniture every settings pane is built from: captions, labelled fields, notes, and the inline problem line.
///
/// <para>It was <see cref="WizardStep"/>'s own, and moved here when the bundle editor needed the same look for
/// panes that are not wizard steps. <see cref="WizardStep"/> still exposes it to its subclasses, so no step
/// changed; this is only where the bodies live now.</para>
/// </summary>
public static class PaneUi
{
    public static Brush Ink => ThemeManager.Ink;
    public static Brush Dim => ThemeManager.Dim;
    public static Brush FieldBg => ThemeManager.FieldBg;

    /// <summary>A pane's root panel: a vertical stack, scrolled by whatever hosts it rather than by itself, so the
    /// scrollbar is in one place and nothing jumps when the pane changes.</summary>
    public static StackPanel Body() => new() { Margin = new Thickness(0, 0, 4, 0) };

    /// <summary>A section caption. Returns the block so a pane whose caption depends on what it is showing can
    /// retitle it later; callers that never change theirs ignore it.</summary>
    public static TextBlock Header(Panel parent, string text) => Add(parent, new TextBlock
    {
        Text = text, Foreground = Dim, FontWeight = FontWeights.Bold, FontSize = 11,
        Margin = new Thickness(0, 16, 0, 5),
    });

    public static TextBlock Note(Panel parent, string text, double indent = 0) =>
        Add(parent, new TextBlock
        {
            Text = text, Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(indent, 4, 0, 0),
        });

    public static TextBox Field(Panel parent, string label, string value, bool multiline = false)
    {
        parent.Children.Add(new TextBlock
        {
            Text = label.ToUpperInvariant(), Foreground = Dim, FontWeight = FontWeights.Bold, FontSize = 11,
            Margin = new Thickness(0, 10, 0, 3),
        });
        var box = new TextBox
        {
            Text = value, Foreground = Ink, Background = FieldBg, BorderBrush = ThemeManager.PanelBorder,
            Padding = new Thickness(5, 3, 5, 3), CaretBrush = Ink,
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

    public static TextBox SmallBox(string value, double width) => new()
    {
        Text = value, Width = width, Foreground = Ink, Background = FieldBg, BorderBrush = ThemeManager.PanelBorder,
        Padding = new Thickness(5, 2, 5, 2), CaretBrush = Ink,
    };

    /// <summary>An inline problem line, placed right under the field it is about and hidden until something is
    /// actually wrong.</summary>
    public static TextBlock Problem(Panel parent, double indent = 0) => Add(parent, new TextBlock
    {
        Foreground = ThemeManager.Bad, FontSize = 12, FontWeight = FontWeights.SemiBold,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(indent, 4, 0, 0),
        Visibility = Visibility.Collapsed,
    });

    /// <summary>Show or clear a <see cref="Problem"/> line, and return the reason so a validation check can be a
    /// single expression.</summary>
    public static string? ShowProblem(TextBlock line, string? reason)
    {
        line.Text = reason ?? "";
        line.Visibility = reason is null ? Visibility.Collapsed : Visibility.Visible;
        return reason;
    }

    public static T Add<T>(Panel parent, T child) where T : UIElement
    {
        parent.Children.Add(child);
        return child;
    }
}
