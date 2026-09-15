using System.Threading;
using System.Windows;
using System.Windows.Media;
using Ostraplan.App;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// What the backdrop builder hands the canvas for each checkerboard (#71). The screen board is one brush for the
/// whole control; the tile-grid board is a plain ground plus a one-tile pattern the canvas lays under its own view
/// transform, which is what makes it move with the ship. STA, because it builds WPF brushes.
/// </summary>
public class BackdropBrushesTests
{
    [Fact]
    public void The_screen_checker_is_one_brush_and_the_tile_checker_is_a_ground_and_a_tile()
    {
        RunSta(() =>
        {
            var brushes = new BackdropBrushes(new SpriteCache());

            var screen = brushes.For(BackdropSettings.Default with { Kind = BackdropKind.Checker }, null);
            Assert.IsType<DrawingBrush>(screen.Brush);
            Assert.Null(screen.TileCell);

            var tile = brushes.For(BackdropSettings.Default with { Kind = BackdropKind.TileChecker }, null);
            var ground = Assert.IsType<SolidColorBrush>(tile.Brush);
            Assert.Equal(Color.FromRgb(0x14, 0x16, 0x1A), ground.Color);   // the first colour, under the pattern
            Assert.NotNull(tile.TileCell);
            Assert.Equal(new Rect(0, 0, 2, 2), tile.TileCell.Bounds);
            Assert.Equal(Color.FromRgb(0x27, 0x20, 0x36), Assert.IsType<SolidColorBrush>(tile.TileFar).Color);

            // Switching between the two boards never flips the ink.
            Assert.Equal(screen.IsLight, tile.IsLight);
        });
    }

    [Fact]
    public void A_tile_brush_starts_at_the_plan_origin_and_is_one_tile_a_repeat()
    {
        RunSta(() =>
        {
            var brushes = new BackdropBrushes(new SpriteCache());
            var cell = brushes.For(BackdropSettings.Default with { Kind = BackdropKind.TileChecker }, null).TileCell!;

            var brush = Assert.IsType<DrawingBrush>(BackdropBrushes.TileBrush(cell, new Vector(37.5, -12), 48));
            Assert.Equal(new Rect(37.5, -12, 48, 48), brush.Viewport);
            Assert.Equal(BrushMappingMode.Absolute, brush.ViewportUnits);
            Assert.Equal(TileMode.Tile, brush.TileMode);
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
