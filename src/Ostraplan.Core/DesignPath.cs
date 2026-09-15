using System.IO;

namespace Ostraplan.Core;

/// <summary>
/// Whether two design paths name the same file on disk.
///
/// <para>This matters wherever the app decides whether a design is <i>already open</i>. Two tabs on one file both
/// write it, and whichever is saved last silently discards the other's work, so Open switches to the tab holding the
/// file rather than starting a second one. The comparison is on the full path, case-folded, because Windows paths are
/// case-insensitive and a relative path or a stray <c>..</c> still names the same file.</para>
/// </summary>
public static class DesignPath
{
    /// <summary>True when both paths are set and resolve to the same file. A path that cannot be normalised is
    /// compared on its raw form, which still matches itself.</summary>
    public static bool Same(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return string.Equals(Normalise(a), Normalise(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The full form of <paramref name="path"/>, or the path as given when it cannot be resolved.</summary>
    public static string Normalise(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }
}
