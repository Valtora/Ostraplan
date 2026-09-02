using System.Security.Cryptography;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ostraplan.App;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The ship's sprites are cached as one drawing covering a <b>window</b> around the view rather than the whole
/// design, so a big design costs what is on screen instead of what it contains. That is only sound while the
/// window holds everything the view can show, and the failure mode is quiet: parts drop out at the edges, or a
/// sprite that overhangs its footprint vanishes, on exactly the designs nobody renders in a test.
///
/// <para>These compare the canvas against <b>itself</b> rather than against a stored image, so they say nothing
/// about which sprites the game ships and survive a game update that changes them. A view reached by two routes
/// has been baked over two different windows, and must come out pixel-identical either way.</para>
/// </summary>
public class BakeWindowTests
{
    private const int W = 900, H = 640;

    private static void RunSta(Action a)
    {
        Exception? err = null;
        var t = new Thread(() => { try { a(); } catch (Exception e) { err = e; } }, 32 * 1024 * 1024);
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (err is not null) throw err;
    }

    private static ShipCanvas Board(ShipDocument doc)
    {
        var canvas = new ShipCanvas { Sprites = new SpriteCache() };
        canvas.SetDocument(doc);
        canvas.Measure(new Size(W, H));
        canvas.Arrange(new Rect(0, 0, W, H));
        canvas.UpdateLayout();
        return canvas;
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

    private static List<(int, int)> Patch(int x, int y, int n) =>
        [.. from dx in Enumerable.Range(0, n) from dy in Enumerable.Range(0, n) select (x + dx, y + dy)];

    /// <summary>A real ship, because the shapes that break this are the ones the game authors: sprites wider than
    /// their footprint, the big tanks' 3x3 body inside a 7x7 socket, and sheet parts that autotile off neighbours
    /// sitting outside the window.
    ///
    /// <para>The Babak, because it is one of the game's own and carries all three at a size worth windowing
    /// (~4,000 parts over 37x95 tiles). It used to be a design of the author's that a mod provided, which is a
    /// hull nobody else has: these tests all failed on any other machine, and on this one the day the mod
    /// went.</para></summary>
    private static ShipDocument RealShip((GameEnv Env, DataIndex Index, Catalog Catalog) g) =>
        TestData.Template(g, "Babak");

    /// <summary>
    /// The ground truth: the same frame with the culling out of the way. Everything above compares the canvas
    /// against itself, which catches a window that fails to follow the view but cannot catch one that is
    /// uniformly too tight, because both sides then lose the same content and agree.
    /// </summary>
    [SkippableTheory]
    [InlineData(0, 0, "middle, zoomed in")]
    [InlineData(-1, -1, "top-left corner")]
    [InlineData(1, 1, "bottom-right corner")]
    public void A_culled_frame_matches_the_same_frame_unculled(int qx, int qy, string _)
    {
        var g = TestData.RequireGame();
        var doc = RealShip(g);
        var b = doc.Bounds()!.Value;
        // the middle, or a corner: the edges are where an under-sized window shows first
        var x = qx < 0 ? b.MinX : qx > 0 ? b.MaxX - 7 : (b.MinX + b.MaxX) / 2;
        var y = qy < 0 ? b.MinY : qy > 0 ? b.MaxY - 7 : (b.MinY + b.MaxY) / 2;
        var patch = Patch(x, y, 8);

        RunSta(() =>
        {
            var culled = Board(doc);
            culled.FocusTiles(patch);

            var whole = Board(doc);
            whole.BakeWholeDesign = true;
            whole.FocusTiles(patch);

            Assert.Equal(Pixels(whole), Pixels(culled));
        });
    }

    /// <summary>The same ground-truth comparison with the plan view turned, where the window has to cover the
    /// viewport's diagonal rather than the viewport.</summary>
    [SkippableFact]
    public void A_culled_frame_matches_unculled_with_the_view_rotated()
    {
        var g = TestData.RequireGame();
        var doc = RealShip(g);
        var b = doc.Bounds()!.Value;
        var patch = Patch((b.MinX + b.MaxX) / 2, (b.MinY + b.MaxY) / 2, 8);

        RunSta(() =>
        {
            foreach (var rot in new[] { 90, 180, 270 })
            {
                var culled = Board(doc);
                culled.RotateView(rot);
                culled.FocusTiles(patch);

                var whole = Board(doc);
                whole.BakeWholeDesign = true;
                whole.RotateView(rot);
                whole.FocusTiles(patch);

                Assert.Equal(Pixels(whole), Pixels(culled));
            }
        });
    }

    /// <summary>Zoom in on one compartment from a framed view, and land on it from a fresh canvas. The first
    /// arrives with a window baked for the whole design, the second with one baked for the compartment.</summary>
    [SkippableFact]
    public void Same_view_reached_two_ways_renders_the_same()
    {
        var g = TestData.RequireGame();
        var doc = RealShip(g);
        var b = doc.Bounds()!.Value;
        var patch = Patch((b.MinX + b.MaxX) / 2, (b.MinY + b.MaxY) / 2, 8);

        RunSta(() =>
        {
            var zoomedOut = Board(doc);
            zoomedOut.FitContent();
            Pixels(zoomedOut);            // bake a window over the whole design first
            zoomedOut.FocusTiles(patch);

            var straightThere = Board(doc);
            straightThere.FocusTiles(patch);

            Assert.Equal(Pixels(straightThere), Pixels(zoomedOut));
        });
    }

    /// <summary>Pan clean out of the baked window and back. The window has to follow the view, and the view it
    /// returns to has to look exactly as it did.</summary>
    [SkippableFact]
    public void Panning_away_and_back_renders_the_same()
    {
        var g = TestData.RequireGame();
        var doc = RealShip(g);
        var b = doc.Bounds()!.Value;
        var patch = Patch((b.MinX + b.MaxX) / 2, (b.MinY + b.MaxY) / 2, 8);

        RunSta(() =>
        {
            var canvas = Board(doc);
            canvas.FocusTiles(patch);
            var before = Pixels(canvas);

            canvas.FocusTiles(Patch(b.MinX, b.MinY, 4));       // far corner, well outside the window
            Pixels(canvas);
            canvas.FocusTiles(Patch(b.MaxX - 3, b.MaxY - 3, 4));   // and the opposite one
            Pixels(canvas);

            canvas.FocusTiles(patch);
            Assert.Equal(before, Pixels(canvas));
        });
    }

    /// <summary>The same, with the plan view turned. A rotated view covers the viewport's diagonal, so the window
    /// it needs is larger than the un-rotated one and a bake that used the plain viewport would come up short in
    /// the corners.</summary>
    [SkippableFact]
    public void A_rotated_view_bakes_a_window_that_covers_its_corners()
    {
        var g = TestData.RequireGame();
        var doc = RealShip(g);
        var b = doc.Bounds()!.Value;
        var patch = Patch((b.MinX + b.MaxX) / 2, (b.MinY + b.MaxY) / 2, 8);

        RunSta(() =>
        {
            var turned = Board(doc);
            turned.RotateView(90);
            turned.FocusTiles(patch);
            var straight = Pixels(turned);

            var panned = Board(doc);
            panned.RotateView(90);
            panned.FocusTiles(Patch(b.MinX, b.MinY, 4));
            Pixels(panned);
            panned.FocusTiles(patch);

            Assert.Equal(straight, Pixels(panned));
        });
    }

    /// <summary>Selecting a part and pressing an arrow key is not a drag, so the cache must not be holding the
    /// "leave the moving parts out" bake when the selection is only being selected.</summary>
    [SkippableFact]
    public void Selecting_a_part_does_not_drop_it_from_the_ship()
    {
        var g = TestData.RequireGame();
        var doc = RealShip(g);
        var b = doc.Bounds()!.Value;
        var patch = Patch((b.MinX + b.MaxX) / 2, (b.MinY + b.MaxY) / 2, 8);

        RunSta(() =>
        {
            var canvas = Board(doc);
            canvas.FocusTiles(patch);
            var unselected = Pixels(canvas);

            var mid = doc.HitTest((b.MinX + b.MaxX) / 2, (b.MinY + b.MaxY) / 2);
            Skip.If(mid is null, "no part at the middle of this ship to select");
            canvas.SelectOnly(mid!);

            // the selection outline is drawn on top, so the ship underneath is what has to be unchanged: clear it
            // again and the frame must return to exactly what it was.
            canvas.SetSelection([]);
            Assert.Equal(unselected, Pixels(canvas));
        });
    }
}
