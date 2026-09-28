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
            Title = $"What's new in Ostraplan v{entries[0].Version}",
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
                Text = $"{entries.Count} releases since you last ran Ostraplan. Click an entry for the details.",
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
    /// <para>Each entry shows only its bold lead, the one-line summary of what changed, and folds the rest under
    /// it. The changelog is written for the repository and runs to a paragraph or more per entry, so showing it
    /// all made the window a wall of text. Nested notes and indented follow-on paragraphs belong to the entry
    /// above them and fold with it.</para>
    ///
    /// <para>The file is hard-wrapped, so an entry spans several source lines and only the first carries the
    /// dash. Continuation lines are joined back onto it before anything is measured or emphasised, since a
    /// <c>**bold**</c> run can straddle the wrap.</para>
    /// </summary>
    private static IEnumerable<UIElement> Render(string markdown)
    {
        var blocks = new List<UIElement>();
        Entry? entry = null;                        // the bullet whose details are still being collected
        var text = new System.Text.StringBuilder();
        var kind = Kind.None;
        var indented = false;

        void CloseEntry()
        {
            if (entry is null) return;
            blocks.Add(EntryRow(entry));
            entry = null;
        }

        void Flush()
        {
            if (kind == Kind.None) return;
            var t = text.ToString();
            text.Clear();
            if (kind == Kind.Entry) entry = new Entry(t);
            else if (entry is not null && (kind == Kind.Note || indented)) entry.Details.Add((t, kind == Kind.Note));
            else { CloseEntry(); blocks.Add(Paragraph(t)); }
            kind = Kind.None;
        }

        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0) { Flush(); continue; }
            var indent = line.Length - trimmed.Length;

            if (trimmed.StartsWith("### ", StringComparison.Ordinal))
            {
                Flush();
                CloseEntry();
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
                if (indent >= 2 && entry is not null) kind = Kind.Note;
                else { CloseEntry(); kind = Kind.Entry; }
                text.Append(trimmed[2..]);
                continue;
            }

            if (kind != Kind.None) text.Append(' ').Append(trimmed);   // the rest of a hard-wrapped line
            else { kind = Kind.Paragraph; indented = indent >= 2; text.Append(trimmed); }
        }
        Flush();
        CloseEntry();
        return blocks;
    }

    private enum Kind { None, Entry, Note, Paragraph }

    /// <summary>One changelog bullet: its full text, and the notes and paragraphs written under it.</summary>
    private sealed class Entry(string text)
    {
        public string Text { get; } = text;
        public List<(string Text, bool Bullet)> Details { get; } = [];
    }

    /// <summary>
    /// An entry as its lead line, with everything else behind a toggle. The lead is the leading <c>**bold**</c>
    /// run when the entry opens with one, and the whole entry when it does not, in which case there is nothing
    /// left to fold. The lead drops its bold: once it is the only thing on the line, weight adds nothing.
    /// </summary>
    private static UIElement EntryRow(Entry entry)
    {
        var (lead, rest) = SplitLead(entry.Text);
        var leadBlock = new TextBlock { Foreground = ThemeManager.Ink, TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
        foreach (var run in Inlines(lead)) { run.FontWeight = FontWeights.Normal; leadBlock.Inlines.Add(run); }

        if (rest.Length == 0 && entry.Details.Count == 0)
        {
            var row = new DockPanel { Margin = new Thickness(4, 6, 0, 0) };
            var glyph = new TextBlock
            {
                Text = "•", Foreground = ThemeManager.Ink, Width = 20, LineHeight = 20, TextAlignment = TextAlignment.Center,
            };
            DockPanel.SetDock(glyph, Dock.Left);
            row.Children.Add(glyph);
            row.Children.Add(leadBlock);
            return row;
        }

        var details = new StackPanel { Margin = new Thickness(0, 2, 0, 6) };
        if (rest.Length > 0) details.Children.Add(Detail(rest, bullet: false));
        foreach (var (text, bullet) in entry.Details) details.Children.Add(Detail(text, bullet));
        return new Expander
        {
            Header = leadBlock, Content = details, Foreground = ThemeManager.Ink, Margin = new Thickness(0, 4, 0, 0),
        };
    }

    /// <summary>Split an entry at the end of its opening <c>**bold**</c> run: (lead, body). An entry that does
    /// not open with one is all lead.</summary>
    internal static (string Lead, string Body) SplitLead(string text)
    {
        if (!text.StartsWith("**", StringComparison.Ordinal)) return (text, "");
        var end = text.IndexOf("**", 2, StringComparison.Ordinal);
        return end < 0 ? (text, "") : (text[..(end + 2)], text[(end + 2)..].Trim());
    }

    /// <summary>A folded line under an entry: dimmer, and bulleted when it was a nested note.</summary>
    private static UIElement Detail(string text, bool bullet)
    {
        var block = new TextBlock
        {
            Foreground = ThemeManager.Dim, TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new Thickness(0, 4, 0, 0),
        };
        foreach (var run in Inlines(text)) block.Inlines.Add(run);
        if (!bullet) return block;

        var row = new DockPanel { Margin = new Thickness(12, 0, 0, 0) };
        var glyph = new TextBlock
        {
            Text = "•", Foreground = ThemeManager.Dim, Width = 14, LineHeight = 20, Margin = new Thickness(0, 4, 0, 0),
        };
        DockPanel.SetDock(glyph, Dock.Left);
        row.Children.Add(glyph);
        row.Children.Add(block);
        return row;
    }

    /// <summary>A paragraph that belongs to no entry: an intro line under a version heading.</summary>
    private static UIElement Paragraph(string text)
    {
        var block = new TextBlock
        {
            Foreground = ThemeManager.Ink, TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new Thickness(0, 10, 0, 0),
        };
        foreach (var run in Inlines(text)) block.Inlines.Add(run);
        return block;
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
