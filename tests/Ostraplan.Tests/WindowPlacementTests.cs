using System.Text.Json;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// Restoring the main window where it was last closed (#69). The whole of the policy is
/// <see cref="WindowPlacement.FitTo"/>, which is what stands between a stored rectangle and a window opened off the
/// edge of a desktop that has changed shape since. Pure arithmetic, so it runs without the game and without a window.
/// </summary>
public class WindowPlacementTests
{
    // A single 1920x1080 monitor, which is what SystemParameters.VirtualScreen* report for one.
    private const double L = 0, T = 0, W = 1920, H = 1080;

    private static WindowPlacement At(double left, double top, double width = 1200, double height = 800) =>
        new() { Left = left, Top = top, Width = width, Height = height };

    [Fact]
    public void A_placement_on_the_desktop_comes_back_exactly_as_it_was()
    {
        var fit = At(240, 120).FitTo(L, T, W, H);

        Assert.NotNull(fit);
        Assert.Equal(240, fit.Left);
        Assert.Equal(120, fit.Top);
        Assert.Equal(1200, fit.Width);
        Assert.Equal(800, fit.Height);
    }

    [Fact]
    public void The_maximised_flag_survives_the_fit()
    {
        var stored = At(240, 120);
        stored.Maximised = true;

        Assert.True(stored.FitTo(L, T, W, H)!.Maximised);
    }

    [Fact]
    public void A_window_on_a_monitor_that_is_gone_is_refused_rather_than_moved()
    {
        // Closed on a second monitor to the right of the primary one, reopened on the laptop panel alone.
        Assert.Null(At(2400, 300).FitTo(L, T, W, H));

        // And the same going the other way: a monitor to the LEFT of the primary one has negative coordinates.
        Assert.Null(At(-2400, 300).FitTo(L, T, W, H));
    }

    [Fact]
    public void A_window_hanging_off_the_edge_is_kept_while_a_grabbable_piece_of_it_is_still_on_screen()
    {
        // 1720 of a 1920-wide desktop: 200px of window left showing, over the 120 minimum.
        Assert.NotNull(At(1720, 300).FitTo(L, T, W, H));

        // 1840: only 80px, which is not enough to aim at.
        Assert.Null(At(1840, 300).FitTo(L, T, W, H));
    }

    [Fact]
    public void A_title_bar_above_the_desktop_is_brought_down_onto_it()
    {
        // The one nudge FitTo makes: a window whose top edge is off the top has no title bar to drag it back by,
        // which is the one case the user cannot recover from by hand.
        var fit = At(240, -80).FitTo(L, T, W, H);

        Assert.NotNull(fit);
        Assert.Equal(0, fit.Top);
        Assert.Equal(240, fit.Left);   // the horizontal position is left exactly where it was
    }

    [Fact]
    public void A_negative_top_that_is_on_the_desktop_is_left_alone()
    {
        // A second monitor above the primary one puts the desktop's top edge at -1080, and a window up there is
        // where the user put it.
        var fit = At(240, -900).FitTo(L, -1080, W, 2160);

        Assert.NotNull(fit);
        Assert.Equal(-900, fit.Top);
    }

    [Theory]
    [InlineData(400, 300)]        // hand-edited far below the floor
    [InlineData(900, 600)]        // the floor itself
    public void A_size_below_the_floor_is_brought_up_to_it(double width, double height)
    {
        var fit = At(100, 100, width, height).FitTo(L, T, W, H);

        Assert.NotNull(fit);
        Assert.Equal(WindowPlacement.MinWidth, fit.Width, 3);
        Assert.Equal(WindowPlacement.MinHeight, fit.Height, 3);
    }

    [Fact]
    public void A_window_bigger_than_the_desktop_is_brought_down_to_it()
    {
        // Closed on a 4K monitor, reopened on a 1080p one.
        var fit = At(0, 0, 3800, 2000).FitTo(L, T, W, H);

        Assert.NotNull(fit);
        Assert.Equal(W, fit.Width, 3);
        Assert.Equal(H, fit.Height, 3);
    }

    [Fact]
    public void A_desktop_smaller_than_the_floor_wins_over_the_floor()
    {
        // A window larger than the desktop cannot be worked with at all, so the ceiling is applied second.
        var fit = At(0, 0, 1200, 800).FitTo(L, T, 800, 500);

        Assert.NotNull(fit);
        Assert.Equal(800, fit.Width, 3);
        Assert.Equal(500, fit.Height, 3);
    }

    [Theory]
    [InlineData(0, 0)]                                              // written before the key existed: 0, not the default size
    [InlineData(-100, 700)]
    [InlineData(double.NaN, 700)]
    [InlineData(double.PositiveInfinity, 700)]
    public void An_unusable_stored_size_opens_the_window_where_it_always_did(double width, double height) =>
        Assert.Null(At(100, 100, width, height).FitTo(L, T, W, H));

    [Fact]
    public void An_unusable_stored_position_is_refused_too() =>
        Assert.Null(At(double.NaN, 100).FitTo(L, T, W, H));

    [Fact]
    public void A_desktop_that_reports_nothing_leaves_the_window_alone() =>
        Assert.Null(At(100, 100).FitTo(L, T, 0, 0));

    // ---- how it is stored ----

    [Fact]
    public void A_settings_file_written_before_this_existed_has_no_placement_and_opens_as_it_always_did()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{ "theme": "dark" }""")!;

        Assert.Null(settings.WindowPlacement);
        Assert.Equal(WindowOpenAs.Last, WindowPlacement.ParseOpenAs(settings.WindowOpenAs));
    }

    [Fact]
    public void The_placement_round_trips_through_the_settings_file()
    {
        var settings = new AppSettings
        {
            WindowPlacement = new WindowPlacement { Left = 12, Top = 34, Width = 1100, Height = 700, Maximised = true },
            WindowOpenAs = WindowOpenAs.Maximised.ToString(),
        };

        var read = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;

        Assert.Equal(12, read.WindowPlacement!.Left);
        Assert.Equal(34, read.WindowPlacement.Top);
        Assert.Equal(1100, read.WindowPlacement.Width);
        Assert.Equal(700, read.WindowPlacement.Height);
        Assert.True(read.WindowPlacement.Maximised);
        Assert.Equal(WindowOpenAs.Maximised, WindowPlacement.ParseOpenAs(read.WindowOpenAs));
    }

    [Theory]
    [InlineData("Maximised", WindowOpenAs.Maximised)]
    [InlineData("maximised", WindowOpenAs.Maximised)]
    [InlineData("Last", WindowOpenAs.Last)]
    [InlineData(null, WindowOpenAs.Last)]
    [InlineData("", WindowOpenAs.Last)]
    [InlineData("fullscreen", WindowOpenAs.Last)]   // a value from a newer build reads as the default, not as nothing
    public void An_unrecognised_open_as_reads_as_the_default(string? stored, WindowOpenAs expected) =>
        Assert.Equal(expected, WindowPlacement.ParseOpenAs(stored));
}
