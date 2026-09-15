using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ostraplan.Core;

/// <summary>One open design as the session file records it, in tab order.</summary>
public sealed class SessionTab
{
    /// <summary>The design's own <c>.oplan</c>, or null for a design that has never been saved to a file.</summary>
    [JsonPropertyName("path")] public string? Path { get; set; }

    /// <summary>The file name, inside the session store, of this tab's unsaved-changes backup, or null when it has
    /// none. Only a design with unsaved changes has one: a clean design is exactly what is on disk.</summary>
    [JsonPropertyName("backup")] public string? Backup { get; set; }

    /// <summary>What the tab was called, so a design that can no longer be reopened can still be named.</summary>
    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// The designs open when the session file was last written, and whether the app closed cleanly after writing it.
///
/// <para><see cref="CleanExit"/> is false for the whole of a run and set true only by an orderly close, so a file
/// that still reads false on the next launch is the record of a run that ended some other way: a crash, a killed
/// process, a power cut.</para>
/// </summary>
public sealed class SessionManifest
{
    [JsonPropertyName("cleanExit")] public bool CleanExit { get; set; }

    /// <summary>Index into <see cref="Tabs"/> of the design that was on screen.</summary>
    [JsonPropertyName("active")] public int Active { get; set; }

    [JsonPropertyName("tabs")] public List<SessionTab> Tabs { get; set; } = [];

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>What closing the app does with designs that have unsaved changes (#73).</summary>
public enum SessionCloseMode
{
    /// <summary>Ask about each one, as the app always has. The backup is only read after a run that did not close
    /// properly, so the user's own <c>.oplan</c> stays the one place a finished change lives.</summary>
    Ask,

    /// <summary>Close without asking, keep the changes in the backup, and bring them back on the next launch.</summary>
    KeepInBackup,
}

/// <summary>
/// The session store (<c>%APPDATA%\Ostraplan\session</c>): which designs were open, and a backup of the unsaved
/// changes in each (#73).
///
/// <para><b>Not the auto-save store.</b> <see cref="AutoSaveStore"/> keeps a rotating history of timestamped
/// snapshots that the user picks through by hand. This keeps exactly one current copy per open design, overwritten in
/// place, and is read back without being asked for. A backup never touches the design's own <c>.oplan</c>.</para>
///
/// <para><b>One running copy owns it.</b> <see cref="TryLock"/> holds <c>session.lock</c> open for the life of the
/// run. A second copy of the app cannot take it, and then neither reads nor writes the session, so it can neither
/// overwrite the first copy's tabs nor read the first copy's unfinished run as a crash.</para>
///
/// <para>The root is injected so the store is testable against a temp directory; <see cref="Default"/> is the one
/// the app uses.</para>
/// </summary>
public sealed class SessionStore
{
    public const int DefaultBackupSeconds = 30;
    public const int MinBackupSeconds = 5;
    public const int MaxBackupSeconds = 600;

    private const string ManifestName = "session.json";
    private const string LockName = "session.lock";
    private const string BackupExtension = ".oplan";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>The store the app uses: <c>%APPDATA%\Ostraplan\session</c>.</summary>
    public static SessionStore Default { get; } = new(System.IO.Path.Combine(AppSettings.Dir, "session"));

    public string Root { get; }

    public SessionStore(string root) => Root = root;

    public static int ClampBackupSeconds(int seconds) => Math.Clamp(seconds, MinBackupSeconds, MaxBackupSeconds);

    /// <summary>Parse a stored close mode by name, falling back to <see cref="SessionCloseMode.Ask"/>.</summary>
    public static SessionCloseMode ParseCloseMode(string? value) =>
        Enum.TryParse<SessionCloseMode>(value, out var mode) && Enum.IsDefined(mode) ? mode : SessionCloseMode.Ask;

    /// <summary>
    /// Take the store for this run, or null when another running copy of the app already holds it (or the folder
    /// cannot be written at all). Dispose the result to release it; closing the process releases it too, which is
    /// what keeps a crash from leaving the store locked.
    /// </summary>
    public IDisposable? TryLock()
    {
        try
        {
            Directory.CreateDirectory(Root);
            return new FileStream(System.IO.Path.Combine(Root, LockName), FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>The manifest as last written, or null when there is none or it cannot be read. A damaged file is a
    /// missing one: the worst it costs is one launch that opens on a blank design.</summary>
    public SessionManifest? Load()
    {
        try
        {
            var path = System.IO.Path.Combine(Root, ManifestName);
            return File.Exists(path) ? JsonSerializer.Deserialize<SessionManifest>(File.ReadAllText(path), Options) : null;
        }
        catch { return null; }
    }

    /// <summary>The manifest's JSON, which is also what <see cref="Save"/> writes. Exposed so a caller can tell
    /// whether anything has changed since the last write and skip the ones that would write the same bytes.</summary>
    public static string Serialize(SessionManifest manifest) => JsonSerializer.Serialize(manifest, Options);

    /// <summary>Write the manifest through a temp file beside it, so a crash mid-write leaves the last good one.</summary>
    public void Save(SessionManifest manifest) => WriteAtomic(ManifestName, Serialize(manifest));

    /// <summary>The full path of a backup named in a manifest.</summary>
    public string BackupPath(string backup) => System.IO.Path.Combine(Root, backup);

    /// <summary>The backup file name for a tab's id. One per tab, overwritten on every backup of it.</summary>
    public static string BackupName(string id) => id + BackupExtension;

    /// <summary>Write a tab's backup. Returns its file name, for the manifest.</summary>
    public string WriteBackup(OplanFile file, string id)
    {
        var name = BackupName(id);
        WriteAtomic(name, file.ToJson());
        return name;
    }

    /// <summary>Delete one backup. A file that will not go is left for <see cref="PruneBackups"/>.</summary>
    public void DeleteBackup(string backup)
    {
        try { File.Delete(BackupPath(backup)); }
        catch { /* locked or already gone: the next prune tries again */ }
    }

    /// <summary>Delete every backup the manifest does not name. Returns how many went. Run once the session has been
    /// restored, which is what clears out the backup of a design the user closed without saving.</summary>
    public int PruneBackups(IEnumerable<string> keep)
    {
        if (!Directory.Exists(Root)) return 0;
        var wanted = keep.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(Root, "*" + BackupExtension))
            {
                if (wanted.Contains(System.IO.Path.GetFileName(path))) continue;
                try { File.Delete(path); removed++; }
                catch { /* locked: next time */ }
            }
        }
        catch { /* an unreadable store has nothing to prune */ }
        return removed;
    }

    private void WriteAtomic(string name, string contents)
    {
        Directory.CreateDirectory(Root);
        var path = System.IO.Path.Combine(Root, name);
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, contents);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort: the write already failed */ }
            throw;
        }
    }
}

/// <summary>One design to bring back at launch.</summary>
/// <param name="Tab">The tab as the manifest recorded it.</param>
/// <param name="LoadFrom">The file to read: the backup when <paramref name="FromBackup"/>, else the design's own.</param>
/// <param name="FromBackup">True when the design comes back with the unsaved changes it had.</param>
public sealed record SessionRestoreItem(SessionTab Tab, string LoadFrom, bool FromBackup);

/// <summary>What a launch should reopen, and what it could not.</summary>
/// <param name="Items">The designs to open, in tab order.</param>
/// <param name="Active">Index into <paramref name="Items"/> of the one to leave on screen, or -1 for none.</param>
/// <param name="Missing">Designs that should have come back and could not, because their file is gone.</param>
/// <param name="ImproperExit">True when the last run did not close properly.</param>
public sealed record SessionRestorePlan(
    IReadOnlyList<SessionRestoreItem> Items, int Active, IReadOnlyList<SessionTab> Missing, bool ImproperExit);

/// <summary>
/// Decide what to reopen from the last session (#73). Pure, so every combination of settings and leftovers can be
/// tested without a window or a disk.
///
/// <para><b>A backup always wins when there is one.</b> The close decides whether backups survive rather than the
/// launch: closing in <see cref="SessionCloseMode.Ask"/> clears them all once every design has been resolved, so a
/// backup still named by the manifest is either the changes a <see cref="SessionCloseMode.KeepInBackup"/> close kept
/// on purpose, or the changes of a run that never reached its close. Both are unsaved work the user has not
/// discarded, and restoring them is not optional behind the "reopen tabs" setting.</para>
///
/// <para><b>A design with nothing unsaved comes back only when tabs are restored.</b> That is the setting's whole
/// meaning. A design whose file has gone is reported rather than skipped without a word, and an untitled design with
/// no backup has nothing to come back from.</para>
/// </summary>
public static class SessionRestore
{
    public static SessionRestorePlan Plan(
        SessionManifest? manifest, bool restoreTabs, SessionStore store, Func<string, bool> fileExists)
    {
        if (manifest is null) return new SessionRestorePlan([], -1, [], false);

        var items = new List<SessionRestoreItem>();
        var missing = new List<SessionTab>();
        var active = -1;

        for (var i = 0; i < manifest.Tabs.Count; i++)
        {
            var tab = manifest.Tabs[i];
            SessionRestoreItem? item = null;

            if (tab.Backup is { Length: > 0 } backup && fileExists(store.BackupPath(backup)))
                item = new SessionRestoreItem(tab, store.BackupPath(backup), FromBackup: true);
            else if (restoreTabs && tab.Path is { Length: > 0 } path)
            {
                if (fileExists(path)) item = new SessionRestoreItem(tab, path, FromBackup: false);
                else missing.Add(tab);
            }

            if (item is null) continue;
            if (i == manifest.Active) active = items.Count;
            items.Add(item);
        }

        // The tab that was on screen may be one that did not come back; the last one opened stands in for it.
        if (active < 0 && items.Count > 0) active = items.Count - 1;
        return new SessionRestorePlan(items, active, missing, ImproperExit: !manifest.CleanExit);
    }
}
