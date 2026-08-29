using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The plan's backdrop (#43): colour handling, the light/dark ink decision, and the settings clamp that stands
/// between a hand-edited <c>settings.json</c> and the renderer.
/// </summary>
public class BackdropTests
{
    [Theory]
    [InlineData("#14161A", 0x14, 0x16, 0x1A)]
    [InlineData("14161A", 0x14, 0x16, 0x1A)]
    [InlineData("  #ffffff  ", 0xFF, 0xFF, 0xFF)]
    [InlineData("#AbCdEf", 0xAB, 0xCD, 0xEF)]
    public void Parses_a_colour_with_or_without_the_hash_and_in_either_case(string hex, byte r, byte g, byte b) =>
        Assert.Equal((r, g, b), Backdrop.ParseColour(hex));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("#GGGGGG")]
    [InlineData("rebeccapurple")]
    public void Refuses_anything_that_is_not_a_six_digit_colour(string? hex) =>
        Assert.Null(Backdrop.ParseColour(hex));

    [Fact]
    public void A_bad_colour_normalises_to_the_fallback_rather_than_throwing()
    {
        // The `is var (r, g, b)` pattern always matches, so this once deconstructed a null and threw. A settings
        // file is user-editable and must never be able to take the app down.
        Assert.Equal("#14161A", Backdrop.NormaliseColour("not a colour", "#14161A"));
        Assert.Equal("#14161A", Backdrop.NormaliseColour(null, "#14161A"));
        Assert.Equal("#ABCDEF", Backdrop.NormaliseColour("abcdef", "#14161A"));
    }

    [Fact]
    public void White_needs_dark_ink_and_the_default_backdrop_does_not()
    {
        Assert.True(Backdrop.IsLight(0xFF, 0xFF, 0xFF));
        Assert.True(Backdrop.IsLight(0xC8, 0xCC, 0xD2));    // "Light grey"
        Assert.False(Backdrop.IsLight(0x14, 0x16, 0x1A));   // the app's own default
        Assert.False(Backdrop.IsLight(0x00, 0x00, 0x00));
        Assert.False(Backdrop.IsLight(0x4A, 0x4F, 0x57));   // mid grey still carries faint white better
    }

    [Fact]
    public void Every_offered_swatch_is_a_colour_the_renderer_can_parse()
    {
        Assert.NotEmpty(Backdrop.Palette);
        foreach (var swatch in Backdrop.Palette)
        {
            Assert.NotNull(Backdrop.ParseColour(swatch.Hex));
            Assert.Equal(swatch.Hex, Backdrop.NormaliseColour(swatch.Hex, "#000000"));
            Assert.False(string.IsNullOrWhiteSpace(swatch.Name));
        }
        Assert.Equal(Backdrop.Palette.Select(s => s.Name).Distinct().Count(), Backdrop.Palette.Count);
    }

    [Fact]
    public void Clamping_puts_a_hand_edited_settings_file_back_in_range()
    {
        var wild = new BackdropSettings
        {
            Solid = "nonsense",
            CheckerAlt = "#12",
            CheckerSquare = 0,          // a zero-size checker square is an infinite loop downstream
            LocaleDimming = -4,         // a negative veil would be brighter than white
            CoarseGrid = 100_000,
        }.Clamped();

        Assert.Equal(BackdropSettings.DefaultSolid, wild.Solid);
        Assert.Equal(BackdropSettings.DefaultCheckerAlt, wild.CheckerAlt);
        Assert.Equal(BackdropSettings.MinCheckerSquare, wild.CheckerSquare);
        Assert.Equal(0, wild.LocaleDimming);
        Assert.Equal(BackdropSettings.MaxCoarseGrid, wild.CoarseGrid);
    }

    [Fact]
    public void The_default_is_the_colour_the_plan_has_always_been_drawn_on()
    {
        var d = BackdropSettings.Default;
        Assert.Equal(BackdropKind.Solid, d.Kind);
        Assert.Equal("#14161A", d.Solid);
        Assert.Equal(0, d.CoarseGrid);   // scale markings are opt-in
        Assert.Null(d.Locale);
    }

    [Fact]
    public void Settings_with_no_backdrop_written_read_as_the_default()
    {
        // A settings.json from a build before this existed has no "backdrop" key at all.
        Assert.Equal(BackdropSettings.Default, new AppSettings().BackdropOrDefault());
    }
}
