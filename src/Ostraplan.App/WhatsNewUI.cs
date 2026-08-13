using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// "What's new in Ostraplan" — the release notes for the running version, shown once the first time the app runs
/// after an update, and openable from Help ▸ View Changelog.
///
/// <para>The notes come from the <c>CHANGELOG.md</c> embedded in this assembly (see <see cref="ReleaseNotes"/>),
/// so they describe the build that is actually running rather than whatever GitHub currently calls latest, and
/// they need no network. The window renders the subset of Markdown the changelog actually uses: <c>###</c>
/// section headings, <c>-</c> bullets with one level of nesting, and inline <c>**bold**</c> / <c>*italic*</c> /
/// <c>`code`</c> / links — the bold lead sentence of every entry being the part people scan.</para>
/// </summary>
public static class WhatsNewUI
{
    /// <summary>Where View Changelog and the window's own link point: GitHub's redirect to the newest release,
    /// which is the full published note for it (and may be newer than the running build).</summary>
    public const string LatestReleaseUrl = "https://github.com/Valtora/Ostraplan/releases/latest";

    private const string ResourceName = "Ostraplan.CHANGELOG.md";

    /// <summary>The changelog shipped inside this build, or null when the resource is missing (which only a broken
    /// build can produce — nothing else in the app depends on it, so it degrades to showing no notes).</summary>
    public static string? Changelog()
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (s is null) return null;
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }
        catch { return null; }
    }

    /// <summary>The running build's own entry, or null when this version has no closed changelog heading (a build
    /// made mid-cycle, whose notes are still under Unreleased).</summary>
    public static ReleaseNotes.Entry? EntryFor(string version) => ReleaseNotes.For(Changelog(), version);

    /// <summary>
    /// Show the notes for one or more versions, newest first. <paramref name="updated"/> distinguishes the two
    /// ways in: after an update the window leads with what the update brought, while opening it by hand is a
    /// lookup and says so.
    /// </summary>
    public static void Show(Window owner, IReadOnlyList<ReleaseNotes.Entry> entries, bool updated, Action<string> openUrl)
    {
        if (entries.Count == 0) return;
        Window? window = null;
        var content = BuildContent(entries, updated, openUrl, () => window?.Close());
        window = new Window
        {
            Title = $"Ostraplan v{entries[0].Version} — what's new",
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            Background = ThemeManager.WindowBg,
            Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 640 },
        };
        window.ShowDialog();
    }

    /// <summary>The window's visual tree, built without a window around it — so the offscreen smoke test can render
    /// the real thing (the same seam <see cref="MessageDialog.BuildLayout"/> exposes for the dialog preview).</summary>
    internal static FrameworkElement BuildContent(
        IReadOnlyList<ReleaseNotes.Entry> entries, bool updated, Action<string> openUrl, Action close)
    {
        var body = new StackPanel { Margin = new Thickness(24, 20, 24, 20), MaxWidth = 660, Background = ThemeManager.WindowBg };

        body.Children.Add(new TextBlock
        {
            Text = updated ? $"Updated to Ostraplan v{entries[0].Version}" : $"Ostraplan v{entries[0].Version}",
            Foreground = ThemeManager.Ink, FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
        });
        if (updated && entries.Count > 1)
            body.Children.Add(new TextBlock
            {
                Text = $"You were on v{entries[^1].Version}'s predecessor, so this covers {entries.Count} releases.",
                Foreground = ThemeManager.Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
            });

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            // The lead version is already named in the title above; each one after it needs its own heading, or a
            // multi-release catch-up reads as one enormous list with no idea where a version ends.
            if (i > 0)
                body.Children.Add(new TextBlock
                {
                    Text = $"v{entry.Version}", Foreground = ThemeManager.Ink, FontSize = 15,
                    FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 24, 0, 0),
                });
            if (entry.Subtitle.Length > 0)
                body.Children.Add(new TextBlock
                {
                    Text = entry.Subtitle, Foreground = ThemeManager.Dim, TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0),
                });
            foreach (var block in Render(entry.Body)) body.Children.Add(block);
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
        };
        var releases = new Button { Content = "All releases on GitHub", Padding = new Thickness(14, 4, 14, 4) };
        var closeButton = new Button { Content = "Close", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        buttons.Children.Add(releases);
        buttons.Children.Add(closeButton);
        body.Children.Add(buttons);

        releases.Click += (_, _) => openUrl(LatestReleaseUrl);
        closeButton.Click += (_, _) => close();
        return body;
    }

    /// <summary>
    /// Render the changelog subset the file actually uses: <c>###</c> headings, <c>-</c> bullets with one level of
    /// nesting, and <c>**bold**</c>. Anything unrecognised falls through as a paragraph, so an entry written in
    /// some shape this doesn't know about still reads rather than vanishing.
    ///
    /// <para>The file is hard-wrapped, so an entry spans several source lines and only the first carries the
    /// dash. Continuation lines are joined back onto it before anything is measured or emphasised — rendering
    /// them separately breaks each entry into a bullet plus loose paragraphs, and splits a <c>**bold**</c> run
    /// that happens to straddle the wrap so its markers show up as text.</para>
    /// </summary>
    private static IEnumerable<UIElement> Render(string markdown)
    {
        var blocks = new List<UIElement>();
        var text = new System.Text.StringBuilder();
        var depth = 0;        // 0 = paragraph, 1 = entry, 2 = a note under one
        var pending = false;

        void Flush()
        {
            if (!pending) return;
            blocks.Add(Paragraph(text.ToString(), depth));
            text.Clear();
            pending = false;
        }

        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0) { Flush(); continue; }

            if (trimmed.StartsWith("### ", StringComparison.Ordinal))
            {
                Flush();
                blocks.Add(new TextBlock
                {
                    Text = trimmed[4..].Trim(), Foreground = ThemeManager.KeyAccent, FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 16, 0, 2),
                });
                continue;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                Flush();
                depth = line.Length - trimmed.Length >= 2 ? 2 : 1;   // an indented dash is a note under the entry above
                text.Append(trimmed[2..]);
                pending = true;
                continue;
            }

            if (pending) text.Append(' ').Append(trimmed);   // the rest of a hard-wrapped entry
            else { depth = 0; text.Append(trimmed); pending = true; }
        }
        Flush();
        return blocks;
    }

    /// <summary>One rendered entry. A bullet gets a hanging indent — the glyph in its own column, so a wrapped
    /// entry lines up under its first word rather than under the bullet, which is what makes a list of long
    /// entries scannable.</summary>
    private static UIElement Paragraph(string text, int depth)
    {
        var block = new TextBlock
        {
            Foreground = depth == 2 ? ThemeManager.Dim : ThemeManager.Ink,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
        };
        foreach (var run in Inlines(text)) block.Inlines.Add(run);
        if (depth == 0)
        {
            block.Margin = new Thickness(0, 10, 0, 0);
            return block;
        }

        var row = new DockPanel { Margin = new Thickness(depth == 2 ? 30 : 14, 7, 0, 0) };
        var glyph = new TextBlock { Text = "•", Foreground = block.Foreground, Width = 14, LineHeight = 20 };
        DockPanel.SetDock(glyph, Dock.Left);
        row.Children.Add(glyph);
        row.Children.Add(block);
        return row;
    }

    /// <summary>
    /// Split a line into styled runs: <c>**bold**</c> (every entry leads with one saying what changed, which is
    /// the part people scan), <c>*italic*</c>, <c>`code`</c>, and <c>[text](link)</c> reduced to its text — the
    /// targets are repo-relative paths that mean nothing inside the app.
    ///
    /// <para>An unclosed marker is emitted as the literal character it is, so a stray asterisk in an entry costs
    /// that one character rather than swallowing the rest of the line.</para>
    /// </summary>
    private static IEnumerable<Run> Inlines(string text)
    {
        var plain = new System.Text.StringBuilder();
        var at = 0;

        Run? Flush()
        {
            if (plain.Length == 0) return null;
            var run = new Run(plain.ToString());
            plain.Clear();
            return run;
        }

        while (at < text.Length)
        {
            var (span, style, next) = Marker(text, at);
            if (span is null)
            {
                plain.Append(text[at]);
                at++;
                continue;
            }
            if (Flush() is { } before) yield return before;
            // Recurse into the span: entries nest markers freely (``**`+N` kits**``), and emitting the inner text
            // verbatim would leave the nested markers showing inside an otherwise correctly styled run.
            foreach (var run in Inlines(span))
            {
                if (style == Style.Bold) run.FontWeight = FontWeights.SemiBold;
                else if (style == Style.Italic) run.FontStyle = FontStyles.Italic;
                yield return run;
            }
            at = next;
        }
        if (Flush() is { } tail) yield return tail;
    }

    private enum Style { Plain, Bold, Italic }

    /// <summary>The marker starting at <paramref name="at"/>, as (text, style, index just past it), or nulls when
    /// the character is not the start of a closed marker.</summary>
    private static (string? Span, Style Style, int Next) Marker(string text, int at)
    {
        if (text[at] == '`')
        {
            var close = text.IndexOf('`', at + 1);
            return close < 0 ? (null, Style.Plain, at) : (text[(at + 1)..close], Style.Plain, close + 1);
        }
        if (text[at] == '[')
        {
            var mid = text.IndexOf("](", at + 1, StringComparison.Ordinal);
            var close = mid < 0 ? -1 : text.IndexOf(')', mid + 2);
            return close < 0 ? (null, Style.Plain, at) : (text[(at + 1)..mid], Style.Plain, close + 1);
        }
        if (text[at] != '*') return (null, Style.Plain, at);

        var bold = at + 1 < text.Length && text[at + 1] == '*';
        var marker = bold ? "**" : "*";
        var end = text.IndexOf(marker, at + marker.Length, StringComparison.Ordinal);
        return end < 0
            ? (null, Style.Plain, at)
            : (text[(at + marker.Length)..end], bold ? Style.Bold : Style.Italic, end + marker.Length);
    }
}
