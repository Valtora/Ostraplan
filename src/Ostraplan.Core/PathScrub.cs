using System.IO;
using System.Text;

namespace Ostraplan.Core;

/// <summary>
/// Strips the current Windows account name and user-profile paths out of a string before it is
/// written to the on-disk <see cref="AuditLog"/>, so the trail can be handed to support without
/// leaking who or where the user is. Known roots collapse to %TOKENS% (a game save under
/// <c>…\AppData\LocalLow\Blue Bottle Games</c>, an .oplan under the profile, a OneDrive project
/// path — all lose the <c>C:\Users\&lt;name&gt;</c> segment); a leftover bare occurrence of the account
/// name becomes <c>&lt;user&gt;</c> as a backstop. Roots are matched longest-first so the most specific
/// one wins (LocalLow before Local, both before the bare profile). Pure and idempotent.
/// </summary>
public static class PathScrub
{
    // Ordered longest-first so a nested root (LocalLow inside the profile) is replaced before the
    // shorter prefix it contains. Built once from the running account's real folders.
    private static readonly (string Path, string Token)[] Roots = BuildRoots();
    private static readonly string? UserName = SafeUserName();

    /// <summary>Returns <paramref name="text"/> with any user-identifying path or account name removed.</summary>
    public static string Clean(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var (path, token) in Roots)
            text = text.Replace(path, token, StringComparison.OrdinalIgnoreCase);
        if (UserName is { Length: > 0 })
            text = ReplaceWord(text, UserName, "<user>");
        return text;
    }

    private static (string, string)[] BuildRoots()
    {
        (string Path, string Token)[] raw =
        [
            (LocalLow(), "%LOCALLOW%"),
            (Special(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%"),
            (Special(Environment.SpecialFolder.ApplicationData), "%APPDATA%"),
            (Special(Environment.SpecialFolder.UserProfile), "%USERPROFILE%"),
        ];
        return raw.Where(r => r.Path.Length > 0)
                  .OrderByDescending(r => r.Path.Length)
                  .ToArray();
    }

    private static string Special(Environment.SpecialFolder folder)
    {
        try { return Environment.GetFolderPath(folder).TrimEnd('\\', '/'); }
        catch { return ""; }
    }

    /// <summary>LocalLow isn't a <see cref="Environment.SpecialFolder"/>, but it's always
    /// <c>&lt;profile&gt;\AppData\LocalLow</c> — where Ostranauts keeps its saves and Player.log.</summary>
    private static string LocalLow()
    {
        var profile = Special(Environment.SpecialFolder.UserProfile);
        return profile.Length == 0 ? "" : Path.Combine(profile, "AppData", "LocalLow");
    }

    private static string? SafeUserName()
    {
        try { var u = Environment.UserName; return string.IsNullOrWhiteSpace(u) ? null : u; }
        catch { return null; }
    }

    /// <summary>
    /// Replace every occurrence of <paramref name="word"/> that isn't flanked by other identifier
    /// characters — so a short account name that happens to be a substring of a longer token (a def
    /// name, a friendly name) is left alone, but a standalone appearance is scrubbed.
    /// </summary>
    private static string ReplaceWord(string text, string word, string with)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var idx = text.IndexOf(word, i, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) { sb.Append(text, i, text.Length - i); break; }
            var end = idx + word.Length;
            var boundedLeft = idx == 0 || !IsWordChar(text[idx - 1]);
            var boundedRight = end >= text.Length || !IsWordChar(text[end]);
            sb.Append(text, i, idx - i);
            sb.Append(boundedLeft && boundedRight ? with : text.Substring(idx, word.Length));
            i = end;
        }
        return sb.ToString();
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
