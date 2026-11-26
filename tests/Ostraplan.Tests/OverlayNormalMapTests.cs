using System.IO;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// A cooverlay skin wears its own normal map in Light Viz, as <c>COOverlay.Init</c> → <c>Item.SetAlt</c> gives it
/// one. Before, every skin kept its base item's, so 408 core skins shaded with another part's relief.
/// </summary>
public class OverlayNormalMapTests
{
    private static string? FileOf(PartDef? part) =>
        part?.SpriteNormAbs is { } p ? Path.GetFileNameWithoutExtension(p) : null;

    [SkippableFact]
    public void A_themed_wall_wears_its_own_normal_map()
    {
        var g = TestData.RequireGame();
        var wall = g.Catalog.Lookup("ItmWallAERO01");
        Skip.If(wall is null, "this install has no ItmWallAERO01");
        Assert.Equal("ItmWallBlueCaution01Sheetn", FileOf(wall));
    }

    [SkippableFact]
    public void Heater_Model_B_wears_its_sheet_normal_map()
    {
        var g = TestData.RequireGame();
        var heater = g.Catalog.Lookup("ItmHeater02");
        Skip.If(heater?.Item.Animation is null, "this install has no animated ItmHeater02");
        Assert.Equal("ItmHeater02Sheetn", FileOf(heater));
    }

    [SkippableFact]
    public void A_skin_that_names_no_normal_map_keeps_its_bases()
    {
        // The game loads its "missing" placeholder here; Ostraplan keeps the base's rather than model that.
        var g = TestData.RequireGame();
        var brush = g.Catalog.Lookup("ItmToothbrush02");
        var baseBrush = g.Catalog.Lookup("ItmToothbrush01");
        Skip.If(brush is null || baseBrush is null, "this install has no toothbrush skins");
        Assert.Equal(baseBrush!.SpriteNormAbs, brush!.SpriteNormAbs);
    }
}
