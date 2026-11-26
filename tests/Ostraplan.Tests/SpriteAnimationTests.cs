using System.Text.Json;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// Animated parts (<c>objAnimation</c>): the canvas draws frame 0 of the flipbook at one cell's size, as
/// <c>Item.SetupAnimation</c> does. Heater Model B was the report: its skin's sheet was drawn whole, sixteen
/// cells across 8×8 tiles, because the skin's animation was never read.
/// </summary>
public class SpriteAnimationTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void A_def_without_objAnimation_has_no_animation()
    {
        Assert.Null(SpriteAnimation.Parse(Parse("""{ "strName": "ItmHeater01" }""")));
    }

    [Fact]
    public void The_heater_sheet_reads_as_a_four_by_four_flipbook()
    {
        var anim = SpriteAnimation.Parse(Parse("""
            { "objAnimation": { "nFrameCount": 8, "strFrameRate": "8-32", "bRandomStartingFrame": true,
                                "nSheetColumns": 4, "nSheetRows": 4, "bLoop": true } }
            """));
        Assert.Equal(new SpriteAnimation(4, 4, 8), anim);
    }

    [Fact]
    public void One_cell_of_the_sheet_is_the_sprite_size()
    {
        // ItmHeater02Sheet is 128x128 on a 4x4 grid: 32 px a cell, two tiles.
        Assert.Equal((2, 2), new SpriteAnimation(4, 4, 8).Tiles(128, 128));
        // ItmShipWeaponPDC01Roll is 240x128 on 5x2: 48x64 a cell.
        Assert.Equal((3, 4), new SpriteAnimation(5, 2, 9).Tiles(240, 128));
    }

    [Fact]
    public void Frame_zero_is_the_top_left_cell_of_the_image()
    {
        // Item.ConvertArrayIndexToTopLeftIndex(0, 4, 4) = 12, the first cell of Unity's top row; Unity's origin is
        // the bottom-left, so in image pixels that is (0, 0).
        Assert.Equal((0, 0, 32, 32), new SpriteAnimation(4, 4, 8).FirstFrame(128, 128, 2, 2));
        Assert.Equal((0, 0, 48, 64), new SpriteAnimation(5, 2, 9).FirstFrame(240, 128, 3, 4));
    }

    [Fact]
    public void The_crop_follows_the_footprint_where_the_game_does()
    {
        // GetMaterialSheet sizes the cell from the footprint, not from texture / columns. A 1x1 footprint on the
        // heater's 4x4 sheet steps 8 cells a row, so cell 12 lands on row 1 from the bottom, column 4.
        Assert.Equal((64, 96, 16, 16), new SpriteAnimation(4, 4, 8).FirstFrame(128, 128, 1, 1));
    }

    [Fact]
    public void A_texture_too_small_for_the_frame_has_none()
    {
        Assert.Null(new SpriteAnimation(4, 4, 8).FirstFrame(0, 0, 2, 2));
    }

    [SkippableFact]
    public void Heater_Model_B_is_two_by_two_tiles_like_its_footprint()
    {
        var g = TestData.RequireGame();
        var heater = g.Catalog.Lookup("ItmHeater02");
        Skip.If(heater is null || heater.Item.Animation is null, "this install has no animated ItmHeater02");
        Assert.Equal((2, 2), (heater!.Item.Width, heater.Item.Height));
        Assert.Equal((2, 2), SpriteExtent.Tiles(heater));
    }

    [SkippableFact]
    public void A_skin_without_an_animation_keeps_a_still_sprite()
    {
        var g = TestData.RequireGame();
        var off = g.Catalog.Lookup("ItmHeater02Off");
        Skip.If(off is null, "this install has no ItmHeater02Off");
        Assert.Null(off!.Item.Animation);
        Assert.Equal((2, 2), SpriteExtent.Tiles(off));
    }
}
