using System.IO;

namespace Ostraplan.Core;

/// <summary>
/// Reads the tail of an on-disk log file for a bug report's diagnostics. Used for Ostraplan's own
/// <c>error.log</c> (unhandled-exception stack traces) and the persisted <c>audit.log</c> (prior sessions).
/// Every returned line is run through <see cref="PathScrub"/>, because <c>error.log</c> is written raw — its
/// stack traces embed real file paths and the account name that must never leave the machine unscrubbed.
/// All I/O is swallowed: a diagnostics read must never be the thing that fails a bug report.
/// </summary>
public static class LogTail
{
    /// <summary>The last <paramref name="maxLines"/> lines of <paramref name="path"/> (in file order,
    /// most-recent-last), each scrubbed of user-identifying paths and the account name. Empty when the file is
    /// absent, empty, or unreadable.</summary>
    public static IReadOnlyList<string> LastLines(string path, int maxLines)
    {
        if (maxLines <= 0) return [];
        try
        {
            if (!File.Exists(path)) return [];
            var lines = File.ReadAllLines(path);
            var start = lines.Length <= maxLines ? 0 : lines.Length - maxLines;
            var tail = new List<string>(lines.Length - start);
            for (var i = start; i < lines.Length; i++)
                tail.Add(PathScrub.Clean(lines[i]));
            return tail;
        }
        catch { return []; }
    }
}
