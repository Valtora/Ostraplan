using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ostraplan.App;

/// <summary>Severity of a <see cref="Dlg"/> message — sets the accent strip, the icon badge colour, and its glyph.</summary>
public enum DlgKind { Info, Success, Warning, Danger }

/// <summary>
/// The app's own themed replacement for WPF's OS-styled <c>MessageBox</c>. A Fluent-looking card that
/// tracks the light/dark theme, leads with a severity icon + accent strip, lays the message out as
/// readable paragraphs and bullets (parsed from the message text), and labels its buttons with the
/// <b>action</b> — never "Yes"/"No". The <b>safe</b> option is the default, focused, accented button, so
/// neither a reflexive <kbd>Enter</kbd> nor a reflexive click on the highlighted button ever performs a
/// destructive action; the user has to deliberately click the plainly-labelled action button.
///
/// <para>Use the static <see cref="Dlg"/> helpers, not this class directly.</para>
/// </summary>
public sealed class MessageDialog : Window
{
    public enum Choice { Primary, Secondary, Cancel }

    private static Brush Ink => ThemeManager.Ink;

    private Choice _choice;

    /// <param name="buttons">Left-to-right; the <b>last</b> is the safe option (default + cancel + focused).</param>
    internal MessageDialog(DlgKind kind, string title, string body, IReadOnlyList<(string Label, Choice Result)> buttons)
    {
        _choice = buttons[^1].Result;   // Esc / close → the safe result

        Title = title;
        Width = 486;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = ThemeManager.WindowBg;

        var (root, safe) = BuildLayout(kind, title, body, buttons, c => { _choice = c; DialogResult = true; });
        Content = root;
        Loaded += (_, _) => safe?.Focus();
    }

    /// <summary>Build the dialog's visual tree and wire each button to <paramref name="onClick"/>. Shared by the
    /// live dialog and the offscreen preview render (the app's <c>--dlgsmoke</c> path). Returns the root visual
    /// and the safe (last) button, which the live dialog focuses.</summary>
    internal static (FrameworkElement Root, Button? Safe) BuildLayout(
        DlgKind kind, string title, string body, IReadOnlyList<(string Label, Choice Result)> buttons, Action<Choice> onClick)
    {
        var (accent, glyph) = StyleFor(kind);

        var content = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };

        // header: severity badge + title
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            Background = accent,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = glyph,
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 15,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
        header.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = Ink,
            FontWeight = FontWeights.SemiBold,
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 0, 0, 0),
            MaxWidth = 404,
        });
        content.Children.Add(header);

        content.Children.Add(new Border
        {
            Margin = new Thickness(38, 12, 0, 0),
            Child = BuildBody(body),
        });

        // Buttons. The primary action is accent-filled ONLY when the message is benign (info / success) — a risky
        // confirm keeps every button plain so nothing invites a reflexive click; the severity strip carries the
        // weight. The safe option (last) is focused and answers Esc, but is NOT the Enter-default on a multi-button
        // dialog, so a reflexive Enter does nothing. A lone OK notice does take Enter (there's nothing to get wrong).
        var accentPrimary = kind is DlgKind.Info or DlgKind.Success;
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        Button? safe = null;
        for (var i = 0; i < buttons.Count; i++)
        {
            var (label, result) = buttons[i];
            var last = i == buttons.Count - 1;
            var btn = new Button
            {
                Content = label,
                Padding = new Thickness(16, 5, 16, 5),
                Margin = new Thickness(8, 0, 0, 0),
                MinWidth = 84,
                IsDefault = buttons.Count == 1,   // only a single-button notice confirms on Enter
                IsCancel = last,                  // Esc → the safe option
            };
            if (i == 0 && !last && accentPrimary)
            {
                btn.Background = ThemeManager.AccentBg;
                btn.Foreground = ThemeManager.AccentText;
            }
            btn.Click += (_, _) => onClick(result);
            row.Children.Add(btn);
            if (last) safe = btn;
        }
        content.Children.Add(row);

        // accent strip down the left edge
        var rootDock = new DockPanel { Background = ThemeManager.WindowBg };
        var strip = new Border { Width = 5, Background = accent };
        DockPanel.SetDock(strip, Dock.Left);
        rootDock.Children.Add(strip);
        rootDock.Children.Add(content);
        return (rootDock, safe);
    }

    /// <summary>Render the message as wrapped paragraphs and bullets. A line whose trimmed start is "•"
    /// becomes a bullet; "…" an indented continuation; a blank line a paragraph gap; anything else a
    /// paragraph. Lets the existing "\n\n"-and-"•" message strings format themselves.</summary>
    private static UIElement BuildBody(string body)
    {
        var panel = new StackPanel { MaxWidth = 416 };
        var first = true;
        var sectionGap = false;   // a blank source line since the last rendered line -> a wider section break
        foreach (var raw in body.Replace("\r", "").Split('\n'))
        {
            if (raw.Trim().Length == 0) { sectionGap = true; continue; }
            var trimmed = raw.TrimStart();
            var bullet = trimmed.StartsWith('•');
            var cont = trimmed.StartsWith('…');
            var indented = bullet || cont;
            var text = bullet ? "•   " + trimmed.TrimStart('•', ' ', '\t') : (cont ? trimmed : raw);
            // first line flush; a blank line = a section gap; otherwise a tight line break within the group
            var top = first ? 0 : sectionGap ? 14 : indented ? 4 : 6;
            panel.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = Ink,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 21,
                Margin = new Thickness(indented ? 12 : 0, top, 0, 0),
            });
            first = false;
            sectionGap = false;
        }
        return panel;
    }

    private static (Brush Accent, string Glyph) StyleFor(DlgKind kind) => kind switch
    {
        DlgKind.Success => (ThemeManager.Good, "✓"),
        DlgKind.Warning => (ThemeManager.Warn, "!"),
        DlgKind.Danger => (ThemeManager.Bad, "!"),
        _ => (ThemeManager.Accent, "i"),
    };

    internal static MessageDialog Run(Window? owner, DlgKind kind, string title, string body, IReadOnlyList<(string, Choice)> buttons)
    {
        var d = new MessageDialog(kind, title, body, buttons);
        if (owner is not null) { d.Owner = owner; d.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        d.ShowDialog();
        return d;
    }

    internal Choice Result => _choice;
}

/// <summary>
/// Themed message dialogs — the app-wide replacement for <c>MessageBox.Show</c>. One-button notices
/// (<see cref="Info"/>/<see cref="Success"/>/<see cref="Warn"/>/<see cref="Error"/>) and decision
/// dialogs (<see cref="Confirm"/> → bool, <see cref="Choose"/> → three-way) with action-labelled
/// buttons and a safe default. See <see cref="MessageDialog"/> for the anti-reflex rationale.
/// </summary>
public static class Dlg
{
    public static void Info(Window? owner, string title, string body) =>
        MessageDialog.Run(owner, DlgKind.Info, title, body, [(("OK", MessageDialog.Choice.Cancel))]);

    public static void Success(Window? owner, string title, string body) =>
        MessageDialog.Run(owner, DlgKind.Success, title, body, [(("OK", MessageDialog.Choice.Cancel))]);

    public static void Warn(Window? owner, string title, string body) =>
        MessageDialog.Run(owner, DlgKind.Warning, title, body, [(("OK", MessageDialog.Choice.Cancel))]);

    public static void Error(Window? owner, string title, string body) =>
        MessageDialog.Run(owner, DlgKind.Danger, title, body, [(("Close", MessageDialog.Choice.Cancel))]);

    /// <summary>Two-button decision. Returns true iff the user clicked the action button (not Cancel / Esc).
    /// The Cancel button is the focused default, so a reflexive Enter never confirms.</summary>
    public static bool Confirm(Window? owner, DlgKind kind, string title, string body, string confirmVerb, string cancelVerb = "Cancel") =>
        MessageDialog.Run(owner, kind, title, body,
            [(confirmVerb, MessageDialog.Choice.Primary), (cancelVerb, MessageDialog.Choice.Cancel)]).Result == MessageDialog.Choice.Primary;

    /// <summary>Three-button decision (e.g. Save / Discard / Cancel). Returns which was chosen; Esc / close = Cancel.</summary>
    public static MessageDialog.Choice Choose(Window? owner, DlgKind kind, string title, string body,
        string primaryVerb, string secondaryVerb, string cancelVerb = "Cancel") =>
        MessageDialog.Run(owner, kind, title, body,
            [(primaryVerb, MessageDialog.Choice.Primary), (secondaryVerb, MessageDialog.Choice.Secondary), (cancelVerb, MessageDialog.Choice.Cancel)]).Result;

    /// <summary>Drop-in themed replacement for <c>MessageBox.Show</c>: same signature, returns a
    /// <see cref="MessageBoxResult"/>, but rendered as the app's Fluent dialog with a severity accent and the
    /// safe option focused. Maps the image to a severity and the button set to labelled buttons. New code should
    /// prefer the typed helpers above (they give action-labelled buttons); this exists so the whole app can drop
    /// the OS-styled MessageBox in one pass.</summary>
    public static MessageBoxResult Show(Window? owner, string message, string title, MessageBoxButton buttons, MessageBoxImage image)
    {
        var kind = image switch
        {
            MessageBoxImage.Error => DlgKind.Danger,      // Error == Hand == Stop
            MessageBoxImage.Warning => DlgKind.Warning,   // Warning == Exclamation
            _ => DlgKind.Info,                            // Information / Question / None
        };
        IReadOnlyList<(string, MessageDialog.Choice)> list = buttons switch
        {
            MessageBoxButton.OKCancel => [("OK", MessageDialog.Choice.Primary), ("Cancel", MessageDialog.Choice.Cancel)],
            MessageBoxButton.YesNo => [("Yes", MessageDialog.Choice.Primary), ("No", MessageDialog.Choice.Cancel)],
            MessageBoxButton.YesNoCancel => [("Yes", MessageDialog.Choice.Primary), ("No", MessageDialog.Choice.Secondary), ("Cancel", MessageDialog.Choice.Cancel)],
            _ => [("OK", MessageDialog.Choice.Cancel)],
        };
        var choice = MessageDialog.Run(owner, kind, title, message, list).Result;
        return buttons switch
        {
            MessageBoxButton.YesNo => choice == MessageDialog.Choice.Primary ? MessageBoxResult.Yes : MessageBoxResult.No,
            MessageBoxButton.YesNoCancel => choice switch
            {
                MessageDialog.Choice.Primary => MessageBoxResult.Yes,
                MessageDialog.Choice.Secondary => MessageBoxResult.No,
                _ => MessageBoxResult.Cancel,
            },
            MessageBoxButton.OKCancel => choice == MessageDialog.Choice.Primary ? MessageBoxResult.OK : MessageBoxResult.Cancel,
            _ => MessageBoxResult.OK,
        };
    }

    /// <summary>Ownerless overload (centres on screen) — for the app-level crash handler.</summary>
    public static MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage image) =>
        Show(null, message, title, buttons, image);
}
