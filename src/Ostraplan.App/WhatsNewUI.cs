using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
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
        var window = Build(entries, updated, openUrl);
        window.Owner = owner;
        window.ShowDialog();
    }

    /// <summary>The window itself, unshown and unowned, so the smoke render can lay out the real thing.</summary>
    internal static Window Build(IReadOnlyList<ReleaseNotes.Entry> entries, bool updated, Action<string> openUrl)
    {
        Window? window = null;
        var content = BuildContent(entries, updated, openUrl, () => window?.Close());
        // A fixed width and a resizable height. The width used to come from the content (SizeToContent both ways), so
        // the window grew sideways whenever an entry opened, and the overlay scrollbar landed on top of the text at
        // the right edge. Now the width is set and the text wraps to it; the height fits the notes up to most of the
        // screen, after which the body scrolls between a header and a footer that stay put.
        window = new Window
        {
            Title = $"What's new in Ostraplan v{entries[0].Version}",
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 720,
            MinWidth = 480,
            MinHeight = 260,
            MaxHeight = Math.Max(360, SystemParameters.WorkArea.Height * 0.85),
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.CanResize,
            Background = ThemeManager.WindowBg,
            Content = content,
        };
        return window;
    }

    /// <summary>The window's visual tree, built without a window around it — so the offscreen smoke test can render
    /// the real thing (the same seam <see cref="MessageDialog.BuildLayout"/> exposes for the dialog preview).</summary>
    internal static FrameworkElement BuildContent(
        IReadOnlyList<ReleaseNotes.Entry> entries, bool updated, Action<string> openUrl, Action close)
    {
        var root = new DockPanel { Background = ThemeManager.WindowBg, LastChildFill = true };

        // ---- header: what this is, and the one control that acts on the whole list ----
        var header = new DockPanel { Margin = new Thickness(24, 18, 24, 10) };
        var expandAll = new TextBlock { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(16, 0, 0, 2) };
        var expandLink = new Hyperlink(new Run("Show all details")) { Foreground = ThemeManager.Accent };
        expandAll.Inlines.Add(expandLink);
        DockPanel.SetDock(expandAll, Dock.Right);
        header.Children.Add(expandAll);

        var heading = new StackPanel();
        heading.Children.Add(new TextBlock
        {
            Text = updated ? $"Updated to Ostraplan v{entries[0].Version}" : $"Ostraplan v{entries[0].Version}",
            Foreground = ThemeManager.Ink, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
        });
        var lede = entries.Count > 1
            ? $"{entries.Count} releases since you last ran Ostraplan. Click an entry for the details."
            : ReleaseNotes.DescribeSubtitle(entries[0].Subtitle);
        if (lede.Length > 0)
            heading.Children.Add(new TextBlock
            {
                Text = lede, Foreground = ThemeManager.Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
            });
        header.Children.Add(heading);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        // ---- footer: pinned, so the buttons are never at the far end of a long catch-up ----
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var releases = new Button { Content = "All releases on GitHub", Padding = new Thickness(14, 4, 14, 4) };
        var closeButton = new Button
        {
            Content = "Close", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(8, 0, 0, 0), IsCancel = true,
        };
        buttons.Children.Add(releases);
        buttons.Children.Add(closeButton);
        var footer = new Border
        {
            BorderBrush = ThemeManager.PanelBorder, BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(24, 12, 24, 14), Child = buttons,
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        // ---- body: the notes. The right margin is wider than the left so the overlay scrollbar never sits on text ----
        var body = new StackPanel { Margin = new Thickness(24, 0, 36, 16) };
        var rows = new List<Disclosure>();
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            // With more than one release each gets its own heading, or a catch-up reads as one long list with no idea
            // where a version ends. A single release is already named in the window's own heading.
            if (entries.Count > 1)
            {
                var title = new TextBlock
                {
                    Foreground = ThemeManager.Ink, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, i == 0 ? 4 : 22, 0, 0),
                };
                title.Inlines.Add(new Run($"v{entry.Version}") { FontSize = 15, FontWeight = FontWeights.SemiBold });
                var about = ReleaseNotes.DescribeSubtitle(entry.Subtitle);
                if (about.Length > 0) title.Inlines.Add(new Run($"   {about}") { Foreground = ThemeManager.Dim });
                body.Children.Add(title);
            }
            foreach (var block in Render(entry.Body, rows)) body.Children.Add(block);
        }
        root.Children.Add(new ScrollViewer
        {
            Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });

        // The link opens everything until everything is open, then closes it all; a row toggled by hand keeps the
        // link's wording honest.
        void SyncLink() =>
            ((Run)expandLink.Inlines.FirstInline).Text = rows.All(r => r.IsOpen) ? "Hide all details" : "Show all details";
        if (rows.Count == 0) expandAll.Visibility = Visibility.Collapsed;
        foreach (var row in rows) row.Toggled += SyncLink;
        expandLink.Click += (_, _) =>
        {
            var open = !rows.All(r => r.IsOpen);
            foreach (var row in rows) row.Set(open);
            SyncLink();
        };

        releases.Click += (_, _) => openUrl(LatestReleaseUrl);
        closeButton.Click += (_, _) => close();
        return root;
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
    private static IEnumerable<UIElement> Render(string markdown, List<Disclosure> rows)
    {
        var blocks = new List<UIElement>();
        Entry? entry = null;                        // the bullet whose details are still being collected
        var text = new System.Text.StringBuilder();
        var kind = Kind.None;
        var indented = false;

        void CloseEntry()
        {
            if (entry is null) return;
            blocks.Add(EntryRow(entry, rows));
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
                    Text = trimmed[4..].Trim().ToUpperInvariant(), Foreground = ThemeManager.KeyAccent,
                    FontWeight = FontWeights.SemiBold, FontSize = 11, Margin = new Thickness(0, 12, 0, 2),
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

    /// <summary>One entry that folds: whether it is open, and the way to open or close it. The header link drives
    /// every row through this, and a row toggled by hand reports back so the link's wording follows.</summary>
    internal sealed class Disclosure(Action<bool> apply)
    {
        public bool IsOpen { get; private set; }
        public event Action? Toggled;

        public void Set(bool open)
        {
            if (IsOpen == open) return;
            IsOpen = open;
            apply(open);
        }

        public void Toggle()
        {
            Set(!IsOpen);
            Toggled?.Invoke();
        }
    }

    /// <summary>Width of the glyph column every entry row starts with, so leads and their details share one left
    /// edge whether or not the row folds.</summary>
    private const double GlyphColumn = 18;

    /// <summary>
    /// An entry as its lead line, with everything else folded under it. The lead is the leading <c>**bold**</c>
    /// run when the entry opens with one, and the whole entry when it does not, in which case there is nothing
    /// left to fold. The lead drops its bold: once it is the only thing on the line, weight adds nothing.
    ///
    /// <para>A flat row rather than an <see cref="Expander"/>. Fluent draws each Expander as a padded card with a
    /// border, so a release of a dozen entries became a dozen stacked boxes, and its header did not wrap a long lead
    /// to the window, cutting it off instead. Here every row is a chevron (or a bullet, when there is nothing to
    /// fold) and the lead text wrapping beside it, with the details indented to the same edge underneath.</para>
    /// </summary>
    private static UIElement EntryRow(Entry entry, List<Disclosure> rows)
    {
        var (lead, rest) = SplitLead(entry.Text);
        var leadBlock = new TextBlock { Foreground = ThemeManager.Ink, TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
        foreach (var run in Inlines(lead)) { run.FontWeight = FontWeights.Normal; leadBlock.Inlines.Add(run); }
        var folds = rest.Length > 0 || entry.Details.Count > 0;

        var glyph = new TextBlock
        {
            Text = folds ? "▸" : "•", Foreground = ThemeManager.Dim, Width = GlyphColumn, LineHeight = 20,
        };
        var line = new DockPanel();
        DockPanel.SetDock(glyph, Dock.Left);
        line.Children.Add(glyph);
        line.Children.Add(leadBlock);

        if (!folds) return new Border { Child = line, Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(-4, 1, -4, 0) };

        var details = new StackPanel { Margin = new Thickness(GlyphColumn, 0, 0, 6), Visibility = Visibility.Collapsed };
        if (rest.Length > 0) details.Children.Add(Detail(rest, bullet: false));
        foreach (var (text, bullet) in entry.Details) details.Children.Add(Detail(text, bullet));

        var hit = new Border
        {
            Child = line, Background = Brushes.Transparent, CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(-4, 1, -4, 0),
            Cursor = Cursors.Hand, Focusable = true,
        };
        var row = new Disclosure(open =>
        {
            details.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            glyph.Text = open ? "▾" : "▸";
        });
        rows.Add(row);

        hit.MouseEnter += (_, _) => hit.Background = ThemeManager.PanelBg;
        hit.MouseLeave += (_, _) => hit.Background = Brushes.Transparent;
        hit.MouseLeftButtonUp += (_, e) => { row.Toggle(); hit.Focus(); e.Handled = true; };
        hit.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Space)) return;
            row.Toggle();
            e.Handled = true;
        };

        var block = new StackPanel();
        block.Children.Add(hit);
        block.Children.Add(details);
        return block;
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
