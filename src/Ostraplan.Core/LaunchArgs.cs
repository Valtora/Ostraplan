using System.IO;

namespace Ostraplan.Core;

/// <summary>What a launch was asked to open, read off its command line.</summary>
public static class LaunchArgs
{
    /// <summary>
    /// The files on a command line: what Explorer passes for a double-clicked design or pack, or anything named by
    /// hand. A flag is not a file and neither is its value, which for every developer flag is a folder; a path that
    /// is not an existing file is left out for the same reason. Made absolute, since they may be handed to a copy
    /// of the app already running in another folder.
    /// </summary>
    public static IReadOnlyList<string> Files(IEnumerable<string> args, Func<string, bool> fileExists) =>
        args.Where(a => a.Length > 0 && !a.StartsWith("--", StringComparison.Ordinal) && fileExists(a))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
