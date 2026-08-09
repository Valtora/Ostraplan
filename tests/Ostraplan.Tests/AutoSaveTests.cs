using System.IO;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>The auto-save snapshot store: how snapshots are keyed to a design, how the set rotates, and what the
/// recovery picker can read back out of a file name. Game-free — the store's root is injected, so everything here
/// runs against a temp directory and never touches <c>%APPDATA%</c>.</summary>
public class AutoSaveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ostraplan-autosave-" + Guid.NewGuid().ToString("N"));
    private readonly AutoSaveStore _store;
    private readonly DateTime _t0 = new(2026, 8, 9, 14, 0, 0, DateTimeKind.Local);

    public AutoSaveTests() => _store = new AutoSaveStore(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
        GC.SuppressFinalize(this);
    }

    private static OplanFile Design(string name) => new() { Meta = new OplanMeta { Name = name } };

    /// <summary>Write <paramref name="count"/> snapshots of one design, a minute apart from <c>_t0</c> plus
    /// <paramref name="startMinute"/>, and return the paths in the order they were written.</summary>
    private List<string> WriteSeries(string name, string? path, int count, int keep, int startMinute = 0)
    {
        var written = new List<string>();
        for (var i = 0; i < count; i++)
            written.Add(_store.Write(Design(name), name, path, keep, _t0.AddMinutes(startMinute + i)));
        return written;
    }

    [Fact]
    public void A_snapshot_round_trips_its_name_timestamp_and_design_path()
    {
        var written = _store.Write(Design("Kestrel"), "Kestrel", @"D:\ships\Kestrel.oplan", keep: 3, _t0);

        var entry = Assert.Single(_store.List());
        Assert.Equal(written, entry.Path);
        Assert.Equal("Kestrel", entry.DesignName);
        Assert.Equal(_t0, entry.SavedAt);
        Assert.False(entry.IsUntitled);

        // the design's own file is recorded inside the snapshot, so recovery can put it back on it
        Assert.Equal(@"D:\ships\Kestrel.oplan", OplanFile.Load(written).AutoSaveOf);
    }

    [Fact]
    public void Rotation_keeps_the_newest_and_drops_everything_older()
    {
        var written = WriteSeries("Kestrel", @"D:\ships\Kestrel.oplan", count: 5, keep: 3);

        var kept = _store.List();
        Assert.Equal(3, kept.Count);
        Assert.Equal(written.Skip(2).Reverse(), kept.Select(e => e.Path));   // newest first
        Assert.All(written.Take(2), p => Assert.False(File.Exists(p)));
    }

    [Fact]
    public void Each_design_rotates_its_own_set()
    {
        WriteSeries("Kestrel", @"D:\ships\Kestrel.oplan", count: 4, keep: 3);
        WriteSeries("Hauler", @"D:\ships\Hauler.oplan", count: 4, keep: 3, startMinute: 10);

        var all = _store.List();
        Assert.Equal(6, all.Count);
        Assert.Equal(3, all.Count(e => e.DesignName == "Kestrel"));
        Assert.Equal(3, all.Count(e => e.DesignName == "Hauler"));
    }

    [Fact]
    public void The_same_file_name_in_two_folders_is_two_designs()
    {
        Assert.NotEqual(AutoSaveStore.KeyFor(@"D:\ships\Kestrel.oplan"), AutoSaveStore.KeyFor(@"D:\old\Kestrel.oplan"));

        WriteSeries("Kestrel", @"D:\ships\Kestrel.oplan", count: 3, keep: 3);
        WriteSeries("Kestrel", @"D:\old\Kestrel.oplan", count: 3, keep: 3, startMinute: 10);

        Assert.Equal(6, _store.List().Count);   // neither evicted the other
    }

    [Fact]
    public void A_designs_key_ignores_path_casing()
    {
        // Windows paths are case-insensitive, so the same file reached two ways must be one design
        Assert.Equal(AutoSaveStore.KeyFor(@"D:\ships\Kestrel.oplan"), AutoSaveStore.KeyFor(@"d:\SHIPS\kestrel.OPLAN"));
    }

    [Fact]
    public void Every_never_saved_design_shares_the_untitled_bucket()
    {
        Assert.Equal(AutoSaveStore.UntitledKey, AutoSaveStore.KeyFor(null));
        Assert.Equal(AutoSaveStore.UntitledKey, AutoSaveStore.KeyFor("   "));

        WriteSeries("Untitled ship", null, count: 2, keep: 3);
        WriteSeries("Another sketch", null, count: 2, keep: 3, startMinute: 10);

        var all = _store.List();
        Assert.Equal(3, all.Count);                       // one shared set of three, not two sets
        Assert.All(all, e => Assert.True(e.IsUntitled));
        Assert.Equal("Another sketch", all[0].DesignName);   // the newest survivor
    }

    [Fact]
    public void A_snapshot_of_an_untitled_design_records_no_design_path()
    {
        var written = _store.Write(Design("Untitled ship"), "Untitled ship", null, keep: 3, _t0);
        Assert.Null(OplanFile.Load(written).AutoSaveOf);
    }

    [Fact]
    public void Lowering_the_keep_count_rotates_on_the_next_snapshot()
    {
        WriteSeries("Kestrel", @"D:\ships\Kestrel.oplan", count: 5, keep: 5);
        Assert.Equal(5, _store.List().Count);

        _store.Write(Design("Kestrel"), "Kestrel", @"D:\ships\Kestrel.oplan", keep: 2, _t0.AddHours(1));
        Assert.Equal(2, _store.List().Count);
    }

    [Fact]
    public void Prune_only_touches_the_design_it_is_given()
    {
        WriteSeries("Kestrel", @"D:\ships\Kestrel.oplan", count: 3, keep: 3);
        WriteSeries("Hauler", @"D:\ships\Hauler.oplan", count: 3, keep: 3, startMinute: 10);

        Assert.Equal(2, _store.Prune(AutoSaveStore.KeyFor(@"D:\ships\Kestrel.oplan"), keep: 1));

        var all = _store.List();
        Assert.Equal(1, all.Count(e => e.DesignName == "Kestrel"));
        Assert.Equal(3, all.Count(e => e.DesignName == "Hauler"));
    }

    [Theory]
    [InlineData("A/B: \"the ship\"?")]        // every character Windows forbids in a file name
    [InlineData("under_scored__name")]        // underscores, which would otherwise fight the __ separator
    [InlineData("   ")]                       // nothing usable left after trimming
    [InlineData("trailing dot.")]             // Windows won't keep a trailing dot
    public void An_awkward_design_name_still_produces_a_readable_snapshot(string name)
    {
        var written = _store.Write(Design(name), name, @"D:\ships\x.oplan", keep: 3, _t0);

        Assert.True(File.Exists(written));
        var entry = Assert.Single(_store.List());
        Assert.NotEmpty(entry.DesignName);
        Assert.Equal(_t0, entry.SavedAt);
    }

    [Fact]
    public void A_very_long_design_name_is_capped_rather_than_blowing_the_path_limit()
    {
        var name = new string('x', 400);
        var written = _store.Write(Design(name), name, @"D:\ships\x.oplan", keep: 3, _t0);

        Assert.True(File.Exists(written));
        Assert.True(Path.GetFileName(written).Length < 100);
    }

    [Fact]
    public void Two_snapshots_within_the_same_second_do_not_overwrite_each_other()
    {
        var first = _store.Write(Design("Kestrel"), "Kestrel", @"D:\ships\Kestrel.oplan", keep: 3, _t0);
        var second = _store.Write(Design("Kestrel"), "Kestrel", @"D:\ships\Kestrel.oplan", keep: 3, _t0);

        Assert.NotEqual(first, second);
        Assert.Equal(2, _store.List().Count);
    }

    [Fact]
    public void Files_the_store_did_not_write_are_ignored()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "not a snapshot");
        File.WriteAllText(Path.Combine(_root, "handwritten.oplan"), "{}");
        File.WriteAllText(Path.Combine(_root, "name__key__nonsense.oplan"), "{}");
        _store.Write(Design("Kestrel"), "Kestrel", @"D:\ships\Kestrel.oplan", keep: 3, _t0);

        var entry = Assert.Single(_store.List());
        Assert.Equal("Kestrel", entry.DesignName);
    }

    [Fact]
    public void An_empty_or_missing_store_lists_nothing()
    {
        Assert.Empty(_store.List());          // never written to, so the directory doesn't even exist
        Assert.Equal(0, _store.Prune(AutoSaveStore.UntitledKey, keep: 3));
    }

    [Fact]
    public void The_interval_and_keep_count_are_clamped_to_something_sane()
    {
        Assert.Equal(AutoSaveStore.MinIntervalMinutes, AutoSaveStore.ClampMinutes(0));
        Assert.Equal(AutoSaveStore.MinIntervalMinutes, AutoSaveStore.ClampMinutes(-5));
        Assert.Equal(AutoSaveStore.MaxIntervalMinutes, AutoSaveStore.ClampMinutes(10_000));
        Assert.Equal(10, AutoSaveStore.ClampMinutes(10));

        Assert.Equal(AutoSaveStore.MinKeep, AutoSaveStore.ClampKeep(0));
        Assert.Equal(AutoSaveStore.MaxKeep, AutoSaveStore.ClampKeep(999));
        Assert.Equal(3, AutoSaveStore.ClampKeep(3));
    }

    [Fact]
    public void Auto_save_is_off_by_default_with_the_documented_defaults()
    {
        var settings = new AppSettings();

        Assert.False(settings.AutoSave);            // opt-in
        Assert.Equal(10, settings.AutoSaveMinutes);
        Assert.Equal(3, settings.AutoSaveKeep);
    }

    [Fact]
    public void An_explicit_save_never_stamps_the_auto_save_marker()
    {
        var path = Path.Combine(_root, "explicit.oplan");
        Directory.CreateDirectory(_root);
        Design("Kestrel").Save(path);

        Assert.Null(OplanFile.Load(path).AutoSaveOf);
        Assert.DoesNotContain("autoSaveOf", File.ReadAllText(path), StringComparison.Ordinal);
    }
}
