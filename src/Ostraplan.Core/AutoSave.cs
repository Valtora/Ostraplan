using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Ostraplan.Core;

/// <summary>One auto-save snapshot on disk, read back from its <b>file name alone</b> — the design's display name,
/// which design it belongs to (<see cref="Key"/>), and when it was taken. The recovery picker is built from these,
/// so listing never opens a snapshot; the .oplan itself is read only when one is actually recovered.</summary>
/// <param name="Path">The snapshot's full path.</param>
/// <param name="DesignName">The design's name as it was when the snapshot was taken (sanitised for the file name).</param>
/// <param name="Key">The rotation key — see <see cref="AutoSaveStore.KeyFor"/>.</param>
/// <param name="SavedAt">Local time the snapshot was taken.</param>
public sealed record AutoSaveEntry(string Path, string DesignName, string Key, DateTime SavedAt)
{
    /// <summary>True for a snapshot of a design that had never been saved to a file.</summary>
    public bool IsUntitled => Key == AutoSaveStore.UntitledKey;
}

/// <summary>
/// The auto-save snapshot store (<c>%APPDATA%\Ostraplan\autosave</c>, beside settings.json and the activity log).
///
/// <para>Auto-save <b>never writes the user's own .oplan</b>. Ctrl+S stays the only thing that does. What this keeps
/// is a rotating set of timestamped snapshots the user can recover from after a crash, and each design keeps its own
/// set: rotation is keyed on the design's file path, so two ships that happen to both be called <c>Kestrel.oplan</c>
/// in different folders never evict each other.</para>
///
/// <para>A design that has never been saved has no path to key on, so every such design shares the one
/// <see cref="UntitledKey"/> bucket. That is only reachable once the user has answered "Don't save" to the discard
/// prompt that New and Open put up, which is what makes the shared bucket safe: giving each unsaved document its own
/// would leave orphan groups that nothing ever rotates out.</para>
///
/// <para>The root is injected rather than fixed so the whole store is testable against a temp directory;
/// <see cref="Default"/> is the one the app uses.</para>
/// </summary>
public sealed class AutoSaveStore
{
    public const int DefaultIntervalMinutes = 10;
    public const int DefaultKeep = 3;
    public const int MinIntervalMinutes = 1;
    public const int MaxIntervalMinutes = 60;
    public const int MinKeep = 1;
    public const int MaxKeep = 20;

    /// <summary>The rotation key shared by every design that has never been saved to a file.</summary>
    public const string UntitledKey = "untitled";

    private const string Extension = ".oplan";
    private const string Separator = "__";      // name__key__stamp.oplan — parsed from the right, so a name can't confuse it
    private const string StampFormat = "yyyyMMdd-HHmmss";
    private const int StampLength = 15;         // the fixed width of StampFormat, so a "-2" collision suffix parses off
    private const int MaxSlugLength = 40;

    /// <summary>The store the app uses: <c>%APPDATA%\Ostraplan\autosave</c>.</summary>
    public static AutoSaveStore Default { get; } = new(Path.Combine(AppSettings.Dir, "autosave"));

    public string Root { get; }

    public AutoSaveStore(string root) => Root = root;

    public static int ClampMinutes(int minutes) => Math.Clamp(minutes, MinIntervalMinutes, MaxIntervalMinutes);
    public static int ClampKeep(int keep) => Math.Clamp(keep, MinKeep, MaxKeep);

    /// <summary>
    /// The rotation key for a design: 8 hex characters of SHA-256 over its full path (case-folded, since Windows
    /// paths are case-insensitive), or <see cref="UntitledKey"/> when it has no path yet.
    ///
    /// <para>Hashing the <i>whole</i> path rather than the file name is the point: same-named designs in different
    /// folders are different designs and keep separate snapshots. A design that is later renamed or moved starts a
    /// new set, and its old set rotates out on its own once nothing writes to it.</para>
    /// </summary>
    public static string KeyFor(string? designPath)
    {
        if (string.IsNullOrWhiteSpace(designPath)) return UntitledKey;
        var full = designPath;
        try { full = Path.GetFullPath(designPath); }
        catch { /* an unnormalisable path still keys consistently on its raw form */ }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant()));
        return Convert.ToHexStringLower(hash.AsSpan(0, 4));
    }

    /// <summary>
    /// Write a snapshot of <paramref name="file"/> and rotate the design's set down to <paramref name="keep"/>.
    /// Returns the path written.
    ///
    /// <para>Stamps <see cref="OplanFile.AutoSaveOf"/> with <paramref name="designPath"/> so recovery can put the
    /// design back on its own file, and writes through a <c>.tmp</c> beside the target so a crash mid-write cannot
    /// leave a truncated snapshot where a good one used to be.</para>
    /// </summary>
    public string Write(OplanFile file, string designName, string? designPath, int keep, DateTime now)
    {
        Directory.CreateDirectory(Root);
        var key = KeyFor(designPath);
        file.AutoSaveOf = designPath;

        var path = FreePath(Slug(designName), key, now);
        var tmp = path + ".tmp";
        try
        {
            file.Save(tmp);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort — the write already failed */ }
            throw;
        }

        Prune(key, keep);
        return path;
    }

    /// <summary>Every snapshot in the store, newest first. Defensive by design: this feeds a menu and a picker, so a
    /// file it cannot make sense of is skipped and an unreadable directory reads as empty rather than throwing.</summary>
    public IReadOnlyList<AutoSaveEntry> List()
    {
        if (!Directory.Exists(Root)) return [];
        var found = new List<AutoSaveEntry>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(Root, "*" + Extension))
                if (Parse(path) is { } entry) found.Add(entry);
        }
        catch { /* an unreadable store is an empty one as far as the UI is concerned */ }
        return found
            .OrderByDescending(e => e.SavedAt)
            .ThenByDescending(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Delete everything past the <paramref name="keep"/> newest snapshots of one design. Returns how many
    /// went. A snapshot that will not delete (open in another program) is left and retried on the next write.</summary>
    public int Prune(string key, int keep)
    {
        var removed = 0;
        foreach (var stale in List().Where(e => e.Key == key).Skip(ClampKeep(keep)))
        {
            try { File.Delete(stale.Path); removed++; }
            catch { /* locked or already gone — the next write tries again */ }
        }
        return removed;
    }

    /// <summary>The snapshot path for a design at <paramref name="now"/>, stepped past any file already there. Two
    /// snapshots of one design inside a second only happen in tests, but a collision must not overwrite.</summary>
    private string FreePath(string slug, string key, DateTime now)
    {
        var stamp = now.ToString(StampFormat, CultureInfo.InvariantCulture);
        var path = Path.Combine(Root, $"{slug}{Separator}{key}{Separator}{stamp}{Extension}");
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(Root, $"{slug}{Separator}{key}{Separator}{stamp}-{n}{Extension}");
        return path;
    }

    /// <summary>Read a snapshot's identity back out of its file name, or null when it isn't one of ours. Parsed from
    /// the right (stamp, then key, then whatever is left is the name), so it holds even if a name slipped a separator
    /// through.</summary>
    private static AutoSaveEntry? Parse(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);

        var stampAt = stem.LastIndexOf(Separator, StringComparison.Ordinal);
        if (stampAt < 0) return null;
        var stamp = stem[(stampAt + Separator.Length)..];
        if (stamp.Length < StampLength) return null;
        if (!DateTime.TryParseExact(stamp[..StampLength], StampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var savedAt))
            return null;

        var head = stem[..stampAt];
        var keyAt = head.LastIndexOf(Separator, StringComparison.Ordinal);
        if (keyAt < 0) return null;
        var key = head[(keyAt + Separator.Length)..];
        var name = head[..keyAt];
        if (key.Length == 0 || name.Length == 0) return null;

        return new AutoSaveEntry(path, name, key, savedAt);
    }

    /// <summary>A design name reduced to a file-name-safe label. Underscores go too, so the <c>__</c> separator stays
    /// unambiguous, and the result is capped so a long name can't push the path over the limit.</summary>
    private static string Slug(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(ch == '_' || Array.IndexOf(invalid, ch) >= 0 ? '-' : ch);

        var slug = sb.ToString().Trim();
        if (slug.Length > MaxSlugLength) slug = slug[..MaxSlugLength];
        slug = slug.TrimEnd(' ', '.');   // Windows won't keep a trailing dot or space
        return slug.Length == 0 ? "design" : slug;
    }
}
