using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ostraplan.App;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// Locks the Light Viz decal/normal rotation contract in <see cref="SpriteCache"/>: a part drawn at rot R has its
/// PIXELS rotated clockwise by the canvas transform, so <see cref="SpriteCache.GlowPixels"/> must rotate the glow
/// image clockwise by the same R (the game parents the glow quad to the item — a wall lamp's glow bar lies along
/// its wall), and <see cref="SpriteCache.NormalSprite"/> must additionally swizzle the encoded normal VECTORS.
/// Regression for the "wall-light glows perpendicular to their wall" bug.
/// </summary>
public class GlowRotationTests
{
    /// <summary>Write a 2×1 PNG: left pixel red, right pixel green.</summary>
    private static string WriteTestPng()
    {
        var wb = new WriteableBitmap(2, 1, 96, 96, PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, 2, 1), new byte[] { 0, 0, 255, 255, 0, 255, 0, 255 }, 8, 0);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(wb));
        var path = Path.Combine(Path.GetTempPath(), $"ostraplan-glowtest-{Guid.NewGuid():N}.png");
        using var fs = File.Create(path);
        enc.Save(fs);
        return path;
    }

    private static (byte B, byte G, byte R, byte A) Px(byte[] bgra, int w, int x, int y) =>
        (bgra[(y * w + x) * 4], bgra[(y * w + x) * 4 + 1], bgra[(y * w + x) * 4 + 2], bgra[(y * w + x) * 4 + 3]);

    [Fact]
    public void Glow_pixels_rotate_clockwise_with_the_part()
    {
        var path = WriteTestPng();
        try
        {
            var cache = new SpriteCache();

            // rot 0: red left, green right
            var (w0, h0, p0) = cache.GlowPixels(path, 0)!.Value;
            Assert.Equal((2, 1), (w0, h0));
            Assert.Equal((byte)255, Px(p0, w0, 0, 0).R);
            Assert.Equal((byte)255, Px(p0, w0, 1, 0).G);

            // rot 90 CW: the bar turns vertical, red on top (left edge rotates to the top edge)
            var (w90, h90, p90) = cache.GlowPixels(path, 90)!.Value;
            Assert.Equal((1, 2), (w90, h90));
            Assert.Equal((byte)255, Px(p90, w90, 0, 0).R);
            Assert.Equal((byte)255, Px(p90, w90, 0, 1).G);

            // rot 180: red right
            var (w180, _, p180) = cache.GlowPixels(path, 180)!.Value;
            Assert.Equal((byte)255, Px(p180, w180, 1, 0).R);
            Assert.Equal((byte)255, Px(p180, w180, 0, 0).G);

            // rot 270 CW: vertical, red at the bottom
            var (w270, h270, p270) = cache.GlowPixels(path, 270)!.Value;
            Assert.Equal((1, 2), (w270, h270));
            Assert.Equal((byte)255, Px(p270, w270, 0, 1).R);
            Assert.Equal((byte)255, Px(p270, w270, 0, 0).G);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Normal_sprite_swizzles_vectors_per_rotation()
    {
        // one pixel encoding a normal pointing RIGHT in doc space: nx=+1 → r=255; ny=0 → g=128
        var wb = new WriteableBitmap(1, 1, 96, 96, PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, 1, 1), new byte[] { 0, 128, 255, 255 }, 4, 0);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(wb));
        var path = Path.Combine(Path.GetTempPath(), $"ostraplan-normtest-{Guid.NewGuid():N}.png");
        using (var fs = File.Create(path)) enc.Save(fs);
        try
        {
            var cache = new SpriteCache();
            var f = new Fixtures();
            f.Part("N");
            var part = f.Get("N") with { SpriteNormAbs = path };

            byte[] Read(int rot)
            {
                var bmp = cache.NormalSprite(part, rot)!;
                var px = new byte[4];
                new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0).CopyPixels(px, 4, 0);
                return px;   // b, g, r, a
            }

            // 90 CW in doc space rotates "right" to "down": nx=0 (r≈127/128), ny=+1 (g=255)
            var r90 = Read(90);
            Assert.InRange(r90[2], 126, 129);
            Assert.Equal((byte)255, r90[1]);

            // 180: pointing left: nx=−1 (r=0), ny=0
            var r180 = Read(180);
            Assert.Equal((byte)0, r180[2]);
            Assert.InRange(r180[1], 126, 129);

            // 270 CW: pointing up: ny=−1 (g=0)
            var r270 = Read(270);
            Assert.Equal((byte)0, r270[1]);
            Assert.InRange(r270[2], 126, 129);
        }
        finally { File.Delete(path); }
    }
}
