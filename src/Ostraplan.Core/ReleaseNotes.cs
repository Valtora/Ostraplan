namespace Ostraplan.Core;

/// <summary>
/// Reads a version's entry out of <c>CHANGELOG.md</c> — the "what's new" the app shows itself once, the first
/// time it runs after an update.
///
/// <para>The changelog is shipped <b>inside the build</b> (an embedded resource) rather than fetched from GitHub,
/// so the notes always describe the copy that is actually running, cost no network call, and work in the portable
/// zip on a machine that is offline. The <c>cut-release</c> flow closes <c>## [Unreleased]</c> into a versioned
/// heading before the build is published, so by the time a user has a release, its heading exists; a build made
/// mid-cycle simply finds nothing and stays quiet.</para>
/// </summary>
public static class ReleaseNotes
{
    /// <summary>One version's entry: the heading text after the version (a date and a tagline, when the release
    /// carried one) and the body beneath it, up to the next version heading.</summary>
    public sealed record Entry(string Version, string Subtitle, string Body);

    /// <summary>
    /// The entry for <paramref name="version"/>, or null when the changelog has no heading for it. Headings are
    /// <c>## [X.Y.Z] date, tagline</c>; <c>## [Unreleased]</c> is deliberately not matchable, since notes that
    /// have not shipped describe a build nobody is running.
    /// </summary>
    public static Entry? For(string? changelog, string? version)
    {
        // A version number, or nothing: "Unreleased" is a heading in the file like any other, and matching it
        // would show a build's own unshipped notes as though they had been released.
        if (string.IsNullOrWhiteSpace(changelog) || !Version.TryParse(version, out _)) return null;

        var lines = changelog.Replace("\r", "").Split('\n');
        var head = $"## [{version}]";
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith(head, StringComparison.Ordinal)) continue;

            var subtitle = lines[i][head.Length..].Trim();
            var body = new List<string>();
            for (var j = i + 1; j < lines.Length && !lines[j].StartsWith("## ", StringComparison.Ordinal); j++)
                body.Add(lines[j]);
            return new Entry(version, subtitle, string.Join("\n", body).Trim('\n'));
        }
        return null;
    }

    /// <summary>
    /// True when the app has just been updated: it ran once as <paramref name="lastRun"/> and is now a strictly
    /// newer <paramref name="current"/>. A first run records the version and shows nothing (a fresh install has
    /// not updated from anything), and so does a downgrade or a re-run of the same build.
    /// </summary>
    public static bool IsUpgrade(string? lastRun, string? current) =>
        Version.TryParse(lastRun, out var was) && Version.TryParse(current, out var now) && now > was;

    /// <summary>
    /// Every entry an update brought: newer than <paramref name="lastRun"/>, up to and including
    /// <paramref name="current"/>, newest first. Releases here batch several version bumps and a user can go a
    /// while between launches, so an update routinely crosses more than one entry and showing only the newest
    /// would bury the rest. Empty when nothing qualifies.
    /// </summary>
    public static IReadOnlyList<Entry> Since(string? changelog, string? lastRun, string? current)
    {
        if (string.IsNullOrWhiteSpace(changelog) || !Version.TryParse(lastRun, out var was)
            || !Version.TryParse(current, out var now) || now <= was) return [];

        var entries = new List<(Version V, Entry E)>();
        foreach (var line in changelog.Replace("\r", "").Split('\n'))
        {
            if (!line.StartsWith("## [", StringComparison.Ordinal)) continue;
            var end = line.IndexOf(']');
            if (end < 4 || !Version.TryParse(line[4..end], out var v) || v <= was || v > now) continue;
            if (For(changelog, line[4..end]) is { } entry) entries.Add((v, entry));
        }
        return [.. entries.OrderByDescending(e => e.V).Select(e => e.E)];
    }
}
