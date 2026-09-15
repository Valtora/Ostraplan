using System.IO;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The session store and what a launch reopens from it (#73). The rules are pure, so every combination of settings
/// and leftovers is checked here without a window; the store is exercised against a temp directory of its own.
/// </summary>
public class SessionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ostraplan-session-" + Guid.NewGuid().ToString("N"));
    private readonly SessionStore _store;

    public SessionStoreTests() => _store = new SessionStore(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static SessionTab Tab(string? path, string? backup = null, string? name = null) =>
        new() { Path = path, Backup = backup, Name = name ?? (path is null ? "Untitled" : Path.GetFileNameWithoutExtension(path)) };

    private SessionRestorePlan Plan(SessionManifest? manifest, bool restoreTabs, params string[] existing)
    {
        var present = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return SessionRestore.Plan(manifest, restoreTabs, _store, present.Contains);
    }

    [Fact]
    public void No_session_file_reopens_nothing()
    {
        var plan = Plan(null, restoreTabs: true);
        Assert.Empty(plan.Items);
        Assert.Empty(plan.Missing);
        Assert.Equal(-1, plan.Active);
        Assert.False(plan.ImproperExit);
    }

    [Fact]
    public void A_clean_close_reopens_each_file_in_order_with_the_same_tab_on_screen()
    {
        var manifest = new SessionManifest
        {
            CleanExit = true, Active = 1,
            Tabs = [Tab(@"C:\ships\A.oplan"), Tab(@"C:\ships\B.oplan"), Tab(@"C:\ships\C.oplan")],
        };

        var plan = Plan(manifest, restoreTabs: true, @"C:\ships\A.oplan", @"C:\ships\B.oplan", @"C:\ships\C.oplan");

        Assert.Equal([@"C:\ships\A.oplan", @"C:\ships\B.oplan", @"C:\ships\C.oplan"], plan.Items.Select(i => i.LoadFrom));
        Assert.All(plan.Items, i => Assert.False(i.FromBackup));
        Assert.Equal(1, plan.Active);
        Assert.False(plan.ImproperExit);
    }

    [Fact]
    public void A_file_that_has_gone_is_reported_and_the_active_tab_falls_back()
    {
        var manifest = new SessionManifest
        {
            CleanExit = true, Active = 1,
            Tabs = [Tab(@"C:\ships\A.oplan"), Tab(@"C:\ships\Gone.oplan")],
        };

        var plan = Plan(manifest, restoreTabs: true, @"C:\ships\A.oplan");

        Assert.Single(plan.Items);
        Assert.Equal("Gone", Assert.Single(plan.Missing).Name);
        Assert.Equal(0, plan.Active);   // the one that was on screen did not come back; the last one opened stands in
    }

    [Fact]
    public void With_tabs_not_restored_only_unsaved_changes_come_back()
    {
        var backup = SessionStore.BackupName("abc");
        var manifest = new SessionManifest
        {
            CleanExit = true,
            Tabs = [Tab(@"C:\ships\Clean.oplan"), Tab(@"C:\ships\Edited.oplan", backup), Tab(null, SessionStore.BackupName("def"))],
        };

        var plan = Plan(manifest, restoreTabs: false,
            @"C:\ships\Clean.oplan", @"C:\ships\Edited.oplan", _store.BackupPath(backup), _store.BackupPath(SessionStore.BackupName("def")));

        Assert.Equal(2, plan.Items.Count);
        Assert.All(plan.Items, i => Assert.True(i.FromBackup));
        Assert.Equal(_store.BackupPath(backup), plan.Items[0].LoadFrom);
        Assert.Equal(@"C:\ships\Edited.oplan", plan.Items[0].Tab.Path);   // saving still goes to the design's own file
        Assert.Null(plan.Items[1].Tab.Path);                             // and an untitled one still asks where
        Assert.Empty(plan.Missing);                                      // a clean tab left closed is not "missing"
    }

    [Fact]
    public void A_backup_that_is_gone_falls_back_to_the_file_and_an_untitled_tab_with_none_is_skipped()
    {
        var manifest = new SessionManifest
        {
            CleanExit = false,
            Tabs = [Tab(@"C:\ships\A.oplan", SessionStore.BackupName("lost")), Tab(null)],
        };

        var plan = Plan(manifest, restoreTabs: true, @"C:\ships\A.oplan");

        var item = Assert.Single(plan.Items);
        Assert.False(item.FromBackup);
        Assert.Equal(@"C:\ships\A.oplan", item.LoadFrom);
        Assert.True(plan.ImproperExit);
    }

    [Fact]
    public void The_manifest_round_trips_and_an_unreadable_one_reads_as_none()
    {
        var manifest = new SessionManifest
        {
            CleanExit = true, Active = 1,
            Tabs = [Tab(@"C:\ships\A.oplan"), Tab(null, SessionStore.BackupName("x"), "Sketch")],
        };
        manifest.Tabs[0].ReadOnly = true;   // the lock (#74) is the tab's, and it comes back with the tab
        _store.Save(manifest);

        var back = _store.Load();
        Assert.NotNull(back);
        Assert.True(back.CleanExit);
        Assert.Equal(1, back.Active);
        Assert.True(back.Tabs[0].ReadOnly);
        Assert.False(back.Tabs[1].ReadOnly);
        Assert.Equal(SessionStore.Serialize(manifest), SessionStore.Serialize(back));

        File.WriteAllText(Path.Combine(_root, "session.json"), "{ not json");
        Assert.Null(_store.Load());
    }

    [Fact]
    public void Pruning_keeps_the_backups_that_are_named_and_removes_the_rest()
    {
        var file = new OplanFile();
        var kept = _store.WriteBackup(file, "keep");
        var stale = _store.WriteBackup(file, "stale");
        Assert.True(File.Exists(_store.BackupPath(stale)));
        Assert.Equal(file.ToJson(), File.ReadAllText(_store.BackupPath(kept)));

        Assert.Equal(1, _store.PruneBackups([kept]));
        Assert.True(File.Exists(_store.BackupPath(kept)));
        Assert.False(File.Exists(_store.BackupPath(stale)));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void Only_one_running_copy_can_hold_the_store()
    {
        using (var first = _store.TryLock())
        {
            Assert.NotNull(first);
            Assert.Null(_store.TryLock());   // a second copy of the app restores nothing and records nothing
        }
        using var again = _store.TryLock();   // released when the holder goes, as it is when a process ends
        Assert.NotNull(again);
    }

    [Theory]
    [InlineData(null, SessionCloseMode.Ask)]
    [InlineData("KeepInBackup", SessionCloseMode.KeepInBackup)]
    [InlineData("Ask", SessionCloseMode.Ask)]
    [InlineData("5", SessionCloseMode.Ask)]
    [InlineData("nonsense", SessionCloseMode.Ask)]
    public void An_unrecognised_close_mode_reads_as_ask(string? stored, SessionCloseMode expected) =>
        Assert.Equal(expected, SessionStore.ParseCloseMode(stored));

    [Fact]
    public void The_backup_interval_is_clamped()
    {
        Assert.Equal(SessionStore.MinBackupSeconds, SessionStore.ClampBackupSeconds(0));
        Assert.Equal(SessionStore.MaxBackupSeconds, SessionStore.ClampBackupSeconds(100000));
        Assert.Equal(30, SessionStore.ClampBackupSeconds(30));
    }
}
