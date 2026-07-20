using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>The pure Favorites / Recent bookkeeping on <see cref="AppSettings"/> (issue #10). No file I/O, no game
/// install — just the toggle, the dedup-to-front, the length cap, and the loose/buildable disambiguation.</summary>
public class FavoritesRecentTests
{
    [Fact]
    public void ToggleFavorite_adds_then_removes_and_reports_the_new_state()
    {
        var s = new AppSettings();

        Assert.True(s.ToggleFavorite("ItmWall1x1", loose: false));   // now a favorite
        Assert.True(s.IsFavorite("ItmWall1x1", loose: false));
        Assert.Single(s.Favorites);

        Assert.False(s.ToggleFavorite("ItmWall1x1", loose: false));  // toggled back off
        Assert.False(s.IsFavorite("ItmWall1x1", loose: false));
        Assert.Empty(s.Favorites);
    }

    [Fact]
    public void Favorite_keys_are_disambiguated_by_the_loose_flag()
    {
        var s = new AppSettings();
        s.ToggleFavorite("Widget", loose: false);

        Assert.True(s.IsFavorite("Widget", loose: false));
        Assert.False(s.IsFavorite("Widget", loose: true));   // same def name, different universe

        s.ToggleFavorite("Widget", loose: true);
        Assert.Equal(2, s.Favorites.Count);
    }

    [Fact]
    public void PushRecent_moves_the_part_to_the_front_without_duplicating()
    {
        var s = new AppSettings();
        s.PushRecent("A", false);
        s.PushRecent("B", false);
        Assert.True(s.PushRecent("A", false));   // A already present, but not at front -> it moves, list changed

        Assert.Equal(new[] { "A", "B" }, s.RecentParts.Select(r => r.Def));
        Assert.Equal(2, s.RecentParts.Count);    // no duplicate A
    }

    [Fact]
    public void PushRecent_is_a_noop_when_the_part_is_already_the_front()
    {
        var s = new AppSettings();
        s.PushRecent("A", false);
        Assert.False(s.PushRecent("A", false));   // same front -> no change (a paint stroke of one part)
        Assert.Single(s.RecentParts);
    }

    [Fact]
    public void PushRecent_caps_the_list_and_evicts_the_oldest()
    {
        var s = new AppSettings();
        for (var i = 0; i < AppSettings.RecentCap + 3; i++)
            s.PushRecent($"P{i}", false);

        Assert.Equal(AppSettings.RecentCap, s.RecentParts.Count);
        Assert.Equal($"P{AppSettings.RecentCap + 2}", s.RecentParts[0].Def);   // newest first
        Assert.DoesNotContain(s.RecentParts, r => r.Def == "P0");              // oldest evicted
    }
}
