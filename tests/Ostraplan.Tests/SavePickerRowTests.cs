using Ostraplan.App;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// How a save reads in the picker. The character leads and the metadata line is what tells two saves apart, which
/// matters because the commonest thing anyone picks between is several saves of one character docked at one
/// station: every visible field except this line is identical across them.
/// </summary>
public class SavePickerRowTests
{
    private static SaveEntry Save(string when = "2026-08-11 23:28:31", double playTime = 125534.4,
        string version = "Early Access Build: 0.15.1.15", string lastSaved = "Early Access Build: 0.15.1.15",
        string name = "autosave_104_ark valtor_1784122646") =>
        new(name, "K-Leg: Old Emporium", "Ark Valtor", when, @"C:\nope\a.zip", playTime, version, lastSaved);

    [Fact]
    public void The_meta_line_carries_when_how_long_the_build_and_the_folder()
    {
        var meta = SavePickerDialog.Meta(Save());

        Assert.Contains("1d 10h 52m", meta);
        Assert.Contains("0.15.1.15", meta);
        Assert.Contains("autosave_104_ark valtor_1784122646", meta);
        Assert.Contains("2026", meta);
    }

    /// <summary>The build a save was made on is what the game's Load screen shows and what the player recognises
    /// it by; a differing last-writer is the only visible sign the file has been through a game update, which is
    /// the first question anyone asks of a save that won't open.</summary>
    [Fact]
    public void A_save_carried_across_a_game_update_shows_both_builds()
    {
        var meta = SavePickerDialog.Meta(Save(lastSaved: "Release Build: 1.0.0.9"));

        Assert.Contains("0.15.1.15 → 1.0.0.9", meta);
    }

    [Fact]
    public void A_save_never_carried_across_an_update_shows_one_build()
    {
        var meta = SavePickerDialog.Meta(Save());

        Assert.Contains("0.15.1.15", meta);
        Assert.DoesNotContain("→", meta);
    }

    /// <summary>A version string that isn't in the game's "Label: 1.2.3" shape is shown whole rather than cut at a
    /// separator that isn't there.</summary>
    [Fact]
    public void An_unrecognised_version_string_is_kept_whole()
    {
        Assert.Contains("some-custom-build", SavePickerDialog.Meta(
            Save(version: "some-custom-build", lastSaved: "some-custom-build")));
    }

    /// <summary>The game's own Load screen shows a save reading 16539.0 as "4h 35m 39s", which is what fixes
    /// <c>playTimeElapsed</c> as seconds rather than minutes or hours.</summary>
    [Fact]
    public void Play_time_is_read_as_seconds()
    {
        Assert.Contains("4h 35m", SavePickerDialog.Meta(Save(playTime: 16539.0)));
        Assert.Contains("1m 5s", SavePickerDialog.Meta(Save(playTime: 65.0)));
    }

    /// <summary>A save folder can be missing its <c>saveInfo.json</c> entirely and still hold a readable ship, so
    /// every field here is optional. The line collapses rather than showing empty separators.</summary>
    [Fact]
    public void Missing_metadata_collapses_rather_than_leaving_separators()
    {
        var meta = SavePickerDialog.Meta(new SaveEntry("orphan_save", "", "", "", @"C:\nope\a.zip"));

        Assert.Equal("orphan_save", meta);
    }

    /// <summary>A timestamp that isn't in the game's format is shown as it is. Dropping it would lose the only
    /// ordering cue the row has.</summary>
    [Fact]
    public void An_unparseable_timestamp_is_shown_verbatim()
    {
        Assert.Contains("some other format", SavePickerDialog.Meta(Save(when: "some other format")));
    }
}
