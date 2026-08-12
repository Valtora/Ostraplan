using System.Text;
using System.Text.Json;

namespace Ostraplan.Core;

/// <summary>
/// Turns a <see cref="JsonException"/> into something a user can act on: what the parser objected to, where in the
/// file, and the text either side of it.
///
/// <para>Every JSON Ostraplan reads from a save was written by the game, so a parse failure means the file is
/// unusual rather than merely wrong, and "it couldn't be parsed" on its own leaves nobody able to say why. Saves are
/// written as a single minified line of tens of megabytes, so a line number alone is no help either: the excerpt is
/// the part that identifies the fault.</para>
/// </summary>
internal static class JsonDiagnostic
{
    /// <summary>How much of the surrounding text to show either side of the fault.</summary>
    private const int Window = 60;

    /// <summary>The parser's complaint, the 1-based location, and an excerpt with a caret under the offending
    /// character. Multi-line, for a dialog rather than a log line.</summary>
    public static string Describe(JsonException ex, string json)
    {
        var sb = new StringBuilder(Complaint(ex));

        if (ex.LineNumber is { } line && ex.BytePositionInLine is { } bytePos)
        {
            sb.Append($"\nLine {line + 1}, position {bytePos + 1}");
            if (ex.Path is { Length: > 0 } path && path != "$") sb.Append($", at {path}");
            sb.Append('.');
            if (Excerpt(json, line, bytePos) is { } excerpt) sb.Append("\n\n").Append(excerpt);
        }

        return sb.ToString();
    }

    /// <summary>The parser's own message, minus the location it appends. That location is restated in our own
    /// 1-based terms, and having both would read as two different positions for one fault.</summary>
    private static string Complaint(JsonException ex)
    {
        var text = ex.Message.Trim();
        var cut = text.IndexOf(" Path: ", StringComparison.Ordinal);
        if (cut < 0) cut = text.IndexOf(" LineNumber: ", StringComparison.Ordinal);
        if (cut > 0) text = text[..cut].TrimEnd();
        return text.EndsWith('.') ? text : text + ".";
    }

    /// <summary>The text either side of the fault, with a caret under it. Null when the position can't be located
    /// in the text we were handed.</summary>
    private static string? Excerpt(string json, long line, long bytePos)
    {
        if (LineStart(json, line) is not { } start) return null;

        var at = Advance(json, start, bytePos);

        // Deliberately not clamped to the line: newlines are escaped in the output, so crossing one costs nothing,
        // and the commonest failure of all — a truncated record, where the fault is past the last line's end — has
        // nothing at all to show if the window can't reach back into the text before it.
        var from = Math.Max(0, at - Window);
        var to = Math.Min(json.Length, at + Window);

        var lead = from > 0 ? "…" : "";
        var before = lead + Escape(json.AsSpan(from, at - from));
        var after = Escape(json.AsSpan(at, to - at)) + (to < json.Length ? "…" : "");

        return $"  {before}{after}\n  {new string(' ', before.Length)}^";
    }

    /// <summary>The index the given 0-based line starts at, or null if the text has no such line.</summary>
    private static int? LineStart(string json, long line)
    {
        if (line <= 0) return 0;
        var at = 0;
        for (long seen = 0; seen < line; seen++)
        {
            var nl = json.IndexOf('\n', at);
            if (nl < 0) return null;
            at = nl + 1;
        }
        return at;
    }

    /// <summary>
    /// Walk forward from a line's start until <paramref name="bytePos"/> UTF-8 bytes have gone by, and return the
    /// char index that lands on.
    ///
    /// <para>The parser counts bytes and the string is UTF-16, so on a save carrying any non-ASCII text at all
    /// (a crew name, a typed ship name) the two indices diverge and a byte offset used as a char offset points at
    /// the wrong character.</para>
    /// </summary>
    private static int Advance(string json, int start, long bytePos)
    {
        long bytes = 0;
        var at = start;
        while (at < json.Length && bytes < bytePos)
        {
            var c = json[at];
            if (c == '\n') break;
            bytes += c < 0x80 ? 1 : c < 0x800 ? 2 : char.IsSurrogate(c) ? 2 : 3;
            at++;
        }
        return at;
    }

    /// <summary>Render control characters visibly. An unescaped newline or a stray NUL inside a string is one of
    /// the things that breaks a save's JSON, so showing it as whitespace would hide the very fault being reported.</summary>
    private static string Escape(ReadOnlySpan<char> span)
    {
        var sb = new StringBuilder(span.Length);
        foreach (var c in span)
            sb.Append(c switch
            {
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                < ' ' or '\x7f' => $"\\u{(int)c:x4}",
                _ => c.ToString(),
            });
        return sb.ToString();
    }
}
