using System.Security.Cryptography;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ostraplan.App;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// Surfaces mode and the Layers panel working together (#79). The Surfaces bar had a SHOW row of its own (Both,
/// Floors, Walls) that ghosted one deck layer to reach the other. It read backwards: at a high ghost strength Floors
/// left the walls in view, and Walls drew the ghosted floors over the walls and hid them. The row is gone and the
/// Layers panel narrows the deck instead, so these hold the two things it was for.
/// </summary>
public class SurfacesViewTests
{
    private const int W = 480, H = 360;

    private static void RunSta(Action a)
    {
        Exception? err = null;
        var t = new Thread(() => { try { a(); } catch (Exception e) { err = e; } }, 32 * 1024 * 1024);
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (err is not null) throw err;
    }

    /// <summary>The floor under a wall is in reach in Surfaces mode once the walls are locked, which is what the
    /// old Floors focus was for.</summary>
    [Fact]
    public void Locking_the_walls_puts_the_floor_under_them_in_reach_in_Surfaces_mode()
    {
        RunSta(() =>
        {
            var cat = new Fixtures().Floor("Floor").Wall("Wall").Build();
            var doc = Fixtures.Doc(cat, Fixtures.P("Floor", 0, 0), Fixtures.P("Wall", 0, 0));
            var board = new ShipCanvas();
            board.SetDocument(doc);
            board.SetSurfaceMode(true);

            Assert.Equal("Wall", board.SurfaceAwareHit(0, 0)?.DefName);   // the wall draws on top and takes the click

            board.SetLayers(LayerView.Default.WithLocked(PlanLayer.Walls, true));
            Assert.Equal("Floor", board.SurfaceAwareHit(0, 0)?.DefName);

            board.SetLayers(LayerView.Default.WithHidden(PlanLayer.Walls, true));
            Assert.Equal("Floor", board.SurfaceAwareHit(0, 0)?.DefName);
        });
    }

    /// <summary>
    /// The ghost slider only reaches what is not deck, so at full strength Surfaces mode draws a deck exactly as the
    /// plan does: walls over the floors under them. The old Walls focus ghosted the floors, and the ghosted pass
    /// went on top, so the floors covered the walls.
    /// </summary>
    [SkippableFact]
    public void At_full_ghost_strength_Surfaces_mode_draws_walls_over_the_floors_under_them()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(g.Catalog.Lookup("ItmFloorGrate01") is not null && g.Catalog.Lookup("ItmWall1x1") is not null,
            "this install lacks one of the probe defs");

        RunSta(() =>
        {
            var doc = new ShipDocument(g.Catalog);
            for (var x = 0; x < 8; x++)
                for (var y = 0; y < 8; y++)
                    new PlaceCommand(new Placement { DefName = "ItmFloorGrate01", X = x, Y = y }).Do(doc);
            for (var x = 0; x < 8; x++)
                new PlaceCommand(new Placement { DefName = "ItmWall1x1", X = x, Y = 3 }).Do(doc);

            var canvas = new ShipCanvas { Sprites = new SpriteCache() };
            canvas.SetDocument(doc);
            canvas.Measure(new Size(W, H));
            canvas.Arrange(new Rect(0, 0, W, H));
            canvas.UpdateLayout();
            canvas.FitContent();
            var plain = Pixels(canvas);

            canvas.SurfaceGhostOpacity = 1.0;
            canvas.SetSurfaceMode(true);
            Assert.Equal(plain, Pixels(canvas));

            // The premise: the walls are something you can see over the deck, so the equality above is about them.
            canvas.SetLayers(LayerView.Default.WithHidden(PlanLayer.Walls, true));
            Assert.NotEqual(plain, Pixels(canvas));
        });
    }

    private static string Pixels(ShipCanvas canvas)
    {
        canvas.InvalidateVisual();
        canvas.UpdateLayout();
        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(canvas);
        var px = new byte[W * H * 4];
        rtb.CopyPixels(px, W * 4, 0);
        return Convert.ToHexString(SHA256.HashData(px));
    }
}
