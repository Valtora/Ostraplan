using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Ostraplan.App;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// Tips (#39): the list held to the rules a new tip has to meet, which tip comes next and when one is due, and the
/// settings surviving a round trip and a hand-edited file. All game-free; the last few build WPF controls and run
/// on an STA thread of their own.
/// </summary>
public class TipsTests
{
    // ---- the list ----

    [Fact]
    public void Every_tip_has_a_unique_kebab_case_id()
    {
        var ids = Tips.All.Select(t => t.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", id));
    }

    [Fact]
    public void Every_tip_is_short_plain_and_finished()
    {
        Assert.All(Tips.All, t =>
        {
            Assert.InRange(t.Text.Length, 1, Tips.MaxLength);
            Assert.DoesNotContain('—', t.Text);
            Assert.DoesNotContain('–', t.Text);
            Assert.EndsWith(".", t.Text);
            Assert.DoesNotMatch(@"\s{2,}", t.Text);
        });
    }

    [Fact]
    public void Every_tip_has_a_topic_and_every_topic_has_a_tip()
    {
        Assert.All(Tips.All, t =>
        {
            Assert.NotEmpty(t.Tags);
            Assert.Equal(t.Tags.Count, t.Tags.Distinct().Count());
        });
        // A topic with no tips would be a checkbox in the hub that does nothing.
        foreach (var tag in Enum.GetValues<TipTag>())
        {
            Assert.Contains(Tips.All, t => t.Tags.Contains(tag));
            Assert.False(string.IsNullOrWhiteSpace(Tips.Label(tag)));
        }
    }

    [Fact]
    public void The_first_tip_says_where_to_turn_tips_off()
    {
        // Tips are on for everyone by default, including people who updated into them, so the first thing they see
        // is the way out. If Settings ever renames its section, this text has to follow it.
        var first = Tips.All[0];
        Assert.Equal("welcome", first.Id);
        Assert.Contains("Settings ▸ Tips", first.Text);
    }

    // ---- which tip comes next ----

    private static readonly IReadOnlyList<Tip> Five =
    [
        new("a", "A.", [TipTag.Basics]),
        new("b", "B.", [TipTag.Basics, TipTag.Shortcuts]),
        new("c", "C.", [TipTag.Editing]),
        new("d", "D.", [TipTag.Shortcuts]),
        new("e", "E.", [TipTag.Mods]),
    ];

    private static readonly DateTime Now = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_next_tip_is_the_first_unseen_one_in_list_order()
    {
        var s = new TipSettings();
        Assert.Equal("a", TipPicker.NextUnseen(Five, s)!.Id);

        s.Seen.AddRange(["a", "c"]);
        Assert.Equal("b", TipPicker.NextUnseen(Five, s)!.Id);
    }

    [Fact]
    public void A_tip_added_early_in_the_list_still_reaches_someone_who_is_past_it()
    {
        // The reason tips are tracked by what has been seen and not by a position: a build that inserts a tip for a
        // new feature at its place in the order still shows it to everybody who has read further than that.
        var s = new TipSettings { Seen = ["a", "c", "d", "e"] };
        Assert.Equal("b", TipPicker.NextUnseen(Five, s)!.Id);
    }

    [Fact]
    public void A_hidden_tip_and_every_tip_under_a_hidden_topic_are_skipped()
    {
        var s = new TipSettings();
        s.SetTipHidden("a", true);
        s.SetTagHidden(TipTag.Shortcuts, true);   // b carries it alongside Basics, and one hidden topic is enough

        Assert.Equal(new[] { "c", "e" }, TipPicker.Shown(Five, s).Select(t => t.Id));
        Assert.Equal("c", TipPicker.NextUnseen(Five, s)!.Id);
    }

    [Fact]
    public void Nothing_is_next_once_everything_shown_has_been_seen()
    {
        var s = new TipSettings { Seen = ["a", "b", "c"] };
        s.SetTagHidden(TipTag.Shortcuts, true);
        s.SetTipHidden("e", true);
        Assert.Null(TipPicker.NextUnseen(Five, s));
    }

    [Fact]
    public void Stepping_wraps_both_ways_and_skips_what_is_hidden()
    {
        var s = new TipSettings();
        s.SetTipHidden("b", true);
        Tip Id(string id) => Five.Single(t => t.Id == id);

        Assert.Equal("c", TipPicker.Step(Five, s, Id("a"), +1)!.Id);
        Assert.Equal("a", TipPicker.Step(Five, s, Id("c"), -1)!.Id);
        Assert.Equal("a", TipPicker.Step(Five, s, Id("e"), +1)!.Id);   // off the end, round to the start
        Assert.Equal("e", TipPicker.Step(Five, s, Id("a"), -1)!.Id);   // and the other way
    }

    [Fact]
    public void Hiding_the_tip_on_the_card_moves_on_to_its_neighbour()
    {
        var s = new TipSettings();
        var c = Five.Single(t => t.Id == "c");
        s.SetTipHidden("c", true);
        Assert.Equal("d", TipPicker.Step(Five, s, c, +1)!.Id);
    }

    [Fact]
    public void Stepping_finds_nothing_when_every_tip_is_hidden()
    {
        var s = new TipSettings();
        foreach (var tag in Enum.GetValues<TipTag>()) s.SetTagHidden(tag, true);
        Assert.Null(TipPicker.Step(Five, s, Five[0], +1));
        Assert.Null(TipPicker.ForBulb(Five, s));
    }

    [Fact]
    public void The_bulb_carries_on_round_the_list_once_everything_is_seen()
    {
        var s = new TipSettings();
        Assert.Equal("a", TipPicker.ForBulb(Five, s)!.Id);

        s.Seen.AddRange(["a", "b", "c", "d", "e"]);
        s.Last = "c";
        Assert.Equal("d", TipPicker.ForBulb(Five, s)!.Id);
        s.Last = "e";
        Assert.Equal("a", TipPicker.ForBulb(Five, s)!.Id);
        s.Last = "removed-in-a-later-build";
        Assert.Equal("a", TipPicker.ForBulb(Five, s)!.Id);
    }

    [Fact]
    public void Showing_a_tip_records_it_once()
    {
        var s = new TipSettings();
        var b = Five[1];
        TipPicker.MarkShown(s, b, Now);
        TipPicker.MarkShown(s, b, Now.AddMinutes(1));

        Assert.Equal(new[] { "b" }, s.Seen);
        Assert.Equal("b", s.Last);
        Assert.Equal(Now.AddMinutes(1), s.LastShownUtc);
    }

    [Fact]
    public void The_position_counts_only_the_tips_that_may_be_shown()
    {
        var s = new TipSettings();
        s.SetTagHidden(TipTag.Shortcuts, true);
        Assert.Equal((2, 3), TipPicker.PositionOf(Five, s, Five[2]));   // a, c, e: c is second of three
        Assert.Equal(0, TipPicker.PositionOf(Five, s, Five[1]).Position);
    }

    // ---- when one is due at startup ----

    [Fact]
    public void A_fresh_install_shows_a_tip_on_its_first_launch()
    {
        Assert.True(TipPicker.DueAtStartup(Five, new TipSettings(), Now));
    }

    [Fact]
    public void The_interval_is_measured_from_the_last_tip_shown()
    {
        var s = new TipSettings { LastShownUtc = Now.AddHours(-7) };
        Assert.False(TipPicker.DueAtStartup(Five, s, Now));
        s.LastShownUtc = Now.AddHours(-TipSettings.DefaultIntervalHours);
        Assert.True(TipPicker.DueAtStartup(Five, s, Now));
    }

    [Fact]
    public void A_last_shown_time_in_the_future_counts_as_due()
    {
        // The clock went back. Measuring from a future time would hold tips off until it caught up.
        var s = new TipSettings { LastShownUtc = Now.AddDays(3) };
        Assert.True(TipPicker.DueAtStartup(Five, s, Now));
    }

    [Fact]
    public void Every_launch_ignores_the_clock_and_never_and_off_ignore_everything()
    {
        var s = new TipSettings { AtStartup = nameof(TipStartup.EveryLaunch), LastShownUtc = Now.AddMinutes(-1) };
        Assert.True(TipPicker.DueAtStartup(Five, s, Now));

        s.AtStartup = nameof(TipStartup.Never);
        Assert.False(TipPicker.DueAtStartup(Five, s, Now));

        s.AtStartup = nameof(TipStartup.EveryLaunch);
        s.Enabled = false;
        Assert.False(TipPicker.DueAtStartup(Five, s, Now));
    }

    [Fact]
    public void Startup_tips_go_quiet_once_everything_has_been_seen()
    {
        var s = new TipSettings { AtStartup = nameof(TipStartup.EveryLaunch), Seen = ["a", "b", "c", "d", "e"] };
        Assert.False(TipPicker.DueAtStartup(Five, s, Now));
    }

    // ---- the settings ----

    [Theory]
    [InlineData(null, TipStartup.Interval)]
    [InlineData("EveryLaunch", TipStartup.EveryLaunch)]
    [InlineData("never", TipStartup.Never)]
    [InlineData("SomethingANewerBuildAdded", TipStartup.Interval)]
    [InlineData("7", TipStartup.Interval)]   // parses as a number, but not one that names a mode
    public void An_unrecognised_startup_mode_reads_as_the_default(string? stored, TipStartup expected) =>
        Assert.Equal(expected, new TipSettings { AtStartup = stored }.StartupMode);

    [Theory]
    [InlineData(8, 8)]
    [InlineData(0, TipSettings.MinIntervalHours)]
    [InlineData(-5, TipSettings.MinIntervalHours)]
    [InlineData(10_000, TipSettings.MaxIntervalHours)]
    public void A_hand_edited_interval_is_clamped(int stored, int expected) =>
        Assert.Equal(expected, new TipSettings { IntervalHours = stored }.Hours);

    [Fact]
    public void A_settings_file_from_before_tips_reads_as_tips_on()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{ "theme": "dark" }""")!;
        Assert.True(settings.Tips.Enabled);
        Assert.True(settings.Tips.ShowBulb);
        Assert.Equal(TipStartup.Interval, settings.Tips.StartupMode);

        var nulled = JsonSerializer.Deserialize<AppSettings>("""{ "tips": null }""")!;
        Assert.NotNull(nulled.Tips);
        Assert.True(nulled.Tips.Enabled);
    }

    [Fact]
    public void Tip_settings_survive_a_round_trip()
    {
        var settings = new AppSettings();
        settings.Tips.Enabled = false;
        settings.Tips.AtStartup = nameof(TipStartup.Never);
        settings.Tips.IntervalHours = 20;
        settings.Tips.ShowBulb = false;
        settings.Tips.SetTagHidden(TipTag.Mods, true);
        settings.Tips.SetTipHidden("rooms", true);
        TipPicker.MarkShown(settings.Tips, Tips.All[0], Now);

        var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!.Tips;
        Assert.False(back.Enabled);
        Assert.Equal(TipStartup.Never, back.StartupMode);
        Assert.Equal(20, back.Hours);
        Assert.False(back.ShowBulb);
        Assert.True(back.IsTagHidden(TipTag.Mods));
        Assert.True(back.IsTipHidden("rooms"));
        Assert.Equal(new[] { "welcome" }, back.Seen);
        Assert.Equal("welcome", back.Last);
        Assert.Equal(Now, back.LastShownUtc);
    }

    [Fact]
    public void A_topic_name_this_build_does_not_know_is_kept_and_ignored()
    {
        var s = new TipSettings { HiddenTags = ["Holograms", "mods"] };
        Assert.True(s.IsTagHidden(TipTag.Mods));   // names match whatever their case
        Assert.Equal(new[] { "a", "b", "c", "d" }, TipPicker.Shown(Five, s).Select(t => t.Id));

        s.SetTagHidden(TipTag.Mods, false);
        Assert.Equal(new[] { "Holograms" }, s.HiddenTags);   // turning one back on leaves the stranger alone
    }

    // ---- the window ----

    // The card is built on its own here rather than inside a MainWindow: closing a MainWindow writes the real
    // settings file, and this class runs in parallel with MainWindowTabsTests, which does the same. That the window
    // carries the card is checked there instead.
    [Fact]
    public void The_card_starts_closed_and_shows_what_it_is_given()
    {
        RunSta(() =>
        {
            var card = new TipCard();
            Assert.False(card.IsOpen);
            Assert.Equal(Visibility.Collapsed, card.Visibility);

            card.Show(Tips.All[3], 4, Tips.All.Count);
            Assert.True(card.IsOpen);
            Assert.Equal(Visibility.Visible, card.Visibility);
            Assert.Same(Tips.All[3], card.Current);

            card.Close();
            Assert.False(card.IsOpen);
            Assert.Null(card.Current);
            Assert.Equal(Visibility.Collapsed, card.Visibility);
        });
    }

    [Fact]
    public void The_hub_builds_a_row_for_every_tip_and_a_box_for_every_topic()
    {
        RunSta(() =>
        {
            var changes = 0;
            var hub = new TipHubWindow(Tips.All, new TipSettings(), () => changes++);
            var boxes = Descendants<CheckBox>((DependencyObject)hub.Content).ToList();
            Assert.Equal(Tips.All.Count + Enum.GetValues<TipTag>().Length, boxes.Count);
            Assert.All(boxes, b => Assert.True(b.IsChecked));
            Assert.Equal(0, changes);
            hub.Close();
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
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
