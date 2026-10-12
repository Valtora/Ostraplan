using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ostraplan.App;

/// <summary>
/// What a toolbar button that shows an icon instead of its name needs on top of an ordinary button.
///
/// <para><see cref="IconProperty"/> is the glyph, drawn by the <c>ToolIcon</c> content templates in MainWindow.xaml.
/// The button's <c>Content</c> stays its name: it is the label those templates show when Settings asks for labels,
/// and the name a screen reader announces either way.</para>
///
/// <para><see cref="TitleProperty"/> is the bold first line of the tooltip, the name and its key ("Rooms (C)"), which
/// is how an icon-only button says what it is. The sentence under it stays an ordinary <c>ToolTip</c> string, so
/// XAML and code that set it (<c>SyncDocumentKindChrome</c> rewrites some) need not know about the title. Whenever
/// the tooltip becomes a string it is rebuilt with the title on top.</para>
/// </summary>
public static class ToolIcon
{
    /// <summary>Inherited, so the Path inside the button's content reads it off itself. A binding that went looking
    /// for the button instead (FindAncestor) attaches only on a later dispatcher pass, which a window that has not been
    /// shown (the smoke render, the tests) never gets.</summary>
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(Geometry), typeof(ToolIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

    public static Geometry? GetIcon(DependencyObject d) => (Geometry?)d.GetValue(IconProperty);
    public static void SetIcon(DependencyObject d, Geometry? value) => d.SetValue(IconProperty, value);

    public static readonly DependencyProperty TitleProperty = DependencyProperty.RegisterAttached(
        "Title", typeof(string), typeof(ToolIcon), new FrameworkPropertyMetadata(null, OnTitleChanged));

    public static string? GetTitle(DependencyObject d) => (string?)d.GetValue(TitleProperty);
    public static void SetTitle(DependencyObject d, string? value) => d.SetValue(TitleProperty, value);

    /// <summary>How wide the sentence may run before it wraps, so a long one makes a tooltip of a few lines rather
    /// than one that stretches across the canvas.</summary>
    private const double TipWidth = 320;

    private static readonly DependencyPropertyDescriptor ToolTipChanges =
        DependencyPropertyDescriptor.FromProperty(FrameworkElement.ToolTipProperty, typeof(FrameworkElement));

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        // Attached once, when the title is first set. Toolbar buttons live as long as the window, so the
        // descriptor's strong reference to them costs nothing.
        if (e.OldValue is null) ToolTipChanges.AddValueChanged(fe, (_, _) => Compose(fe));
        Compose(fe);
    }

    /// <summary>Put the title above the tooltip's sentence. Only a string is rebuilt, which is also what stops
    /// this re-entering itself: the panel it assigns is not one.</summary>
    private static void Compose(FrameworkElement fe)
    {
        if (fe.ToolTip is not string sentence || GetTitle(fe) is not { Length: > 0 } title) return;

        var panel = new StackPanel { MaxWidth = TipWidth };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        if (sentence.Length > 0)
            panel.Children.Add(new TextBlock
            {
                Text = sentence, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
            });
        fe.ToolTip = panel;
    }

    /// <summary>The tooltip's text as one line, title first, whichever form it is in. For tests and the smoke render.</summary>
    internal static string TipText(FrameworkElement fe) => fe.ToolTip switch
    {
        string s => s,
        Panel p => string.Join(" ", p.Children.OfType<TextBlock>().Select(t => t.Text)),
        _ => "",
    };
}
