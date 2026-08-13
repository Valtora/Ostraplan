using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ostraplan.App;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The "what's new" the app shows itself once after an update: pulling a version's entry out of CHANGELOG.md,
/// deciding whether an update actually happened, and the changelog surviving into the build as a resource.
/// </summary>
public class ReleaseNotesTests
{
    private const string Sample = """
        # Changelog

        Some preamble.

        ## [Unreleased]

        ### Fixed
        - Something not yet shipped.

        ## [0.81.0] 2026-08-14, Draw order

        ### Fixed
        - **A canister no longer draws over the machine it feeds.** It sits on its regulator's own row.
          - A nested note.

        ### Added
        - **Move Back and Move Forward.**

        ## [0.80.0] 2026-08-13, Flight Dynamics

        ### Added
        - Older things.
        """;

    [Fact]
    public void An_entry_is_read_from_its_heading_to_the_next_version()
    {
        var entry = ReleaseNotes.For(Sample, "0.81.0");

        Assert.NotNull(entry);
        Assert.Equal("0.81.0", entry!.Version);
        Assert.Equal("2026-08-14, Draw order", entry.Subtitle);
        Assert.Contains("Move Back and Move Forward", entry.Body);
        Assert.Contains("A nested note.", entry.Body);
        Assert.DoesNotContain("Older things", entry.Body);      // stops at the next version
        Assert.DoesNotContain("not yet shipped", entry.Body);   // and never reaches back into Unreleased
    }

    [Fact]
    public void Unreleased_notes_are_not_shown_as_a_version()
    {
        // notes that have not shipped describe a build nobody is running
        Assert.Null(ReleaseNotes.For(Sample, "Unreleased"));
        Assert.Null(ReleaseNotes.For(Sample, "9.9.9"));
        Assert.Null(ReleaseNotes.For(null, "0.81.0"));
        Assert.Null(ReleaseNotes.For(Sample, ""));
    }

    [Fact]
    public void Only_a_version_going_up_counts_as_an_update()
    {
        Assert.True(ReleaseNotes.IsUpgrade("0.80.0", "0.81.0"));
        Assert.True(ReleaseNotes.IsUpgrade("0.80.0", "1.0.0"));
        Assert.False(ReleaseNotes.IsUpgrade("0.81.0", "0.81.0"));   // same build, run again
        Assert.False(ReleaseNotes.IsUpgrade("0.82.0", "0.81.0"));   // rolled back
        Assert.False(ReleaseNotes.IsUpgrade(null, "0.81.0"));       // fresh install: nothing to compare against
        Assert.False(ReleaseNotes.IsUpgrade("", "0.81.0"));
        Assert.False(ReleaseNotes.IsUpgrade("not-a-version", "0.81.0"));
    }

    [Fact]
    public void The_changelog_ships_inside_the_build()
    {
        // the resource is the whole source of the release notes, and a broken embed would fail silently
        var text = WhatsNewUI.Changelog();
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains("# Changelog", text);
    }

    [Fact]
    public void Every_released_version_in_the_shipped_changelog_parses()
    {
        var text = WhatsNewUI.Changelog()!;
        var versions = text.Replace("\r", "").Split('\n')
            .Where(l => l.StartsWith("## [", System.StringComparison.Ordinal) && !l.StartsWith("## [Unreleased]", System.StringComparison.Ordinal))
            .Select(l => l[4..l.IndexOf(']')])
            .ToList();

        Assert.NotEmpty(versions);
        foreach (var v in versions)
        {
            var entry = ReleaseNotes.For(text, v);
            Assert.NotNull(entry);
            Assert.False(string.IsNullOrWhiteSpace(entry!.Body), $"v{v} has an empty entry");
        }
    }

    [Fact]
    public void An_update_that_crosses_several_releases_shows_them_all_newest_first()
    {
        var entries = ReleaseNotes.Since(Sample, "0.79.0", "0.81.0");

        Assert.Equal(["0.81.0", "0.80.0"], entries.Select(e => e.Version));
        Assert.Empty(ReleaseNotes.Since(Sample, "0.81.0", "0.81.0"));   // same build, nothing crossed
        Assert.Empty(ReleaseNotes.Since(Sample, null, "0.81.0"));       // fresh install
        Assert.Empty(ReleaseNotes.Since(Sample, "0.81.0", "0.80.0"));   // rolled back
    }

    [Fact]
    public void A_version_the_update_did_not_reach_is_left_out()
    {
        // the running build is the ceiling: notes for a release this copy is not on would describe code it lacks
        Assert.Equal(["0.80.0"], ReleaseNotes.Since(Sample, "0.79.0", "0.80.0").Select(e => e.Version));
    }

    [Fact]
    public void The_whats_new_window_renders_the_real_notes()
    {
        // The markdown renderer is the only thing between a released changelog entry and the user, and it runs
        // exactly once per update, so a throw in it would surface as a crash on the first launch after updating.
        // Render the newest shipped entry offscreen and leave the PNG next to the binaries for eyeballing.
        var text = WhatsNewUI.Changelog()!;
        var newest = text.Replace("\r", "").Split('\n')
            .First(l => l.StartsWith("## [", StringComparison.Ordinal) && !l.StartsWith("## [Unreleased]", StringComparison.Ordinal));
        var entry = ReleaseNotes.For(text, newest[4..newest.IndexOf(']')])!;

        RunSta(() =>
        {
            var content = WhatsNewUI.BuildContent([entry], updated: true, _ => { }, () => { });
            content.Measure(new Size(720, double.PositiveInfinity));
            content.Arrange(new Rect(0, 0, 720, Math.Min(content.DesiredSize.Height, 2000)));
            content.UpdateLayout();

            var bitmap = new RenderTargetBitmap(720, (int)Math.Min(content.DesiredSize.Height, 2000), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(AppContext.BaseDirectory, "smoke-whats-new.png");
            using (var stream = File.Create(path)) encoder.Save(stream);
            Assert.True(new FileInfo(path).Length > 5000);
        });
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
