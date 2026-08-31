using System.IO;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// Sprite measurement (#57): the game's <c>vScale</c> rule, and the caching around it, which is what turned one
/// transient read error into every door on the ship drawing squashed into a single tile until the app restarted.
/// </summary>
public class SpriteExtentTests
{
    /// <summary>A real PNG of the given pixel size, header and all. Only the IHDR is read, but writing a valid
    /// file keeps the test honest about what it is measuring.</summary>
    private static string WritePng(string path, int w, int h)
    {
        using var bmp = new System.Drawing.Bitmap(w, h);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        return path;
    }

    private static string TempPng(int w, int h) =>
        WritePng(Path.Combine(Path.GetTempPath(), "ostraplan-extent-" + Guid.NewGuid().ToString("N")[..8] + ".png"), w, h);

    [Fact]
    public void A_sprite_measures_the_games_own_way()
    {
        // Item.SetData: max(round(px / 16), 1) per axis. 80x16 is a door, five tiles by one.
        Assert.Equal((5, 1), SpriteExtent.FromPixels(80, 16));
        Assert.Equal((1, 1), SpriteExtent.FromPixels(16, 16));
        Assert.Equal((3, 3), SpriteExtent.FromPixels(48, 48));
        Assert.Equal((1, 1), SpriteExtent.FromPixels(4, 4));      // the min-1 floor
        Assert.Equal((1, 1), SpriteExtent.FromPixels(0, 0));
    }

    [Fact]
    public void The_header_read_and_the_pixel_rule_agree()
    {
        var path = TempPng(80, 16);
        try
        {
            Assert.Equal((80, 16), SpriteExtent.PixelSize(path));
            Assert.Equal((5, 1), SpriteExtent.Tiles(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_file_that_cannot_be_read_is_not_remembered_as_one_tile()
    {
        // THE regression. The cache is static and lives as long as the process, so caching a failure meant a
        // single unlucky read left the part drawn at one tile until Ostraplan was restarted — which is what the
        // reporter saw, and what restarting fixed.
        var path = Path.Combine(Path.GetTempPath(), "ostraplan-extent-" + Guid.NewGuid().ToString("N")[..8] + ".png");

        Assert.Equal(SpriteExtent.Unknown, SpriteExtent.Tiles(path));   // not there yet: unknown, and NOT cached

        WritePng(path, 80, 16);
        try
        {
            Assert.Equal((5, 1), SpriteExtent.Tiles(path));   // readable now, so measured now
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_real_measurement_is_cached_so_the_file_is_read_once()
    {
        var path = TempPng(48, 48);
        Assert.Equal((3, 3), SpriteExtent.Tiles(path));
        File.Delete(path);
        // Gone from disk, still measured: the successful read is the one that sticks.
        Assert.Equal((3, 3), SpriteExtent.Tiles(path));
    }

    [Fact]
    public void Measuring_tolerates_another_process_holding_the_file_open_for_write()
    {
        // File.OpenRead asks for FileShare.Read, which a scanner or an indexer holding the file for write is
        // enough to refuse. The game's images are ours to read and nobody else's to be excluded from.
        var path = TempPng(80, 16);
        try
        {
            using (var hog = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
                                            FileShare.ReadWrite | FileShare.Delete))
            {
                Assert.Equal((80, 16), SpriteExtent.PixelSize(path));
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Something_that_is_not_a_png_measures_as_unknown_rather_than_throwing()
    {
        var path = Path.Combine(Path.GetTempPath(), "ostraplan-extent-" + Guid.NewGuid().ToString("N")[..8] + ".png");
        File.WriteAllText(path, "this is not a png, but it is long enough to read a header from");
        try
        {
            Assert.Equal((0, 0), SpriteExtent.PixelSize(path));
            Assert.Equal(SpriteExtent.Unknown, SpriteExtent.Tiles(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_file_too_short_to_hold_a_header_measures_as_unknown()
    {
        var path = Path.Combine(Path.GetTempPath(), "ostraplan-extent-" + Guid.NewGuid().ToString("N")[..8] + ".png");
        File.WriteAllBytes(path, [0x89, (byte)'P', (byte)'N', (byte)'G']);
        try
        {
            Assert.Equal((0, 0), SpriteExtent.PixelSize(path));
        }
        finally { File.Delete(path); }
    }

    [SkippableFact]
    public void A_closed_door_is_five_tiles_wide_like_the_open_one_it_replaces()
    {
        // What the report was actually about. Both states share ItmDoor01's 5x1 geometry and each has its own
        // 80x16 texture, so a toggled door must not change size.
        var g = TestData.RequireGame();
        foreach (var pair in new[] { ("ItmDoor01Open", "ItmDoor01Closed"), ("ItmDoor05Open", "ItmDoor05Closed") })
        {
            var open = g.Catalog.Lookup(pair.Item1);
            var closed = g.Catalog.Lookup(pair.Item2);
            Skip.If(open is null || closed is null, $"this install has no {pair.Item1}");
            Assert.Equal(SpriteExtent.Tiles(open!), SpriteExtent.Tiles(closed!));
            Assert.Equal((5, 1), SpriteExtent.Tiles(closed!));
        }
    }
}
