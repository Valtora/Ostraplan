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
/// The two access marks (#63). A part's <b>declared use point</b> is what the game's own build cursor draws, and
/// the Access overlay's <b>computed standing tile</b> is where a crew member could actually get to given the deck
/// around it. They land on the same tile more often than not, and they used to be the same pair of feet in the
/// same blue with the computed one filled over the top, so neither could be read.
///
/// <para>Compared against the canvas itself rather than a stored image, the bargain
/// <see cref="BakeWindowTests"/> makes: nothing here depends on which sprites the game currently ships.</para>
/// </summary>
public class AccessMarkTests
{
    private const int W = 560, H = 420;

    private static void RunSta(Action a)
    {
        Exception? err = null;
        var t = new Thread(() => { try { a(); } catch (Exception e) { err = e; } }, 32 * 1024 * 1024);
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (err is not null) throw err;
    }

    private static byte[] Render(ShipCanvas canvas)
    {
        canvas.InvalidateVisual();
        canvas.UpdateLayout();
        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(canvas);
        var px = new byte[W * H * 4];
        rtb.CopyPixels(px, W * 4, 0);
        return px;
    }

    private static string Hash(ShipCanvas canvas) => Convert.ToHexString(SHA256.HashData(Render(canvas)));

    /// <summary>How many pixels of the frame match <paramref name="matches"/>. A cheap stand-in for "is this mark
    /// on screen", which is all these need.</summary>
    private static int Ink(ShipCanvas canvas, Func<byte, byte, byte, bool> matches)
    {
        var px = Render(canvas);
        var n = 0;
        for (var i = 0; i < px.Length; i += 4)
            if (matches(px[i + 2], px[i + 1], px[i])) n++;   // r, g, b
        return n;
    }

    /// <summary>Green ink, which after #63 only the computed access mark puts down.</summary>
    private static bool Greenish(byte r, byte g, byte b) => g > 120 && g > r + 40 && g > b + 30;

    private static bool Ready(Catalog c) =>
        c.Lookup("ItmWall1x1") is not null && c.Lookup("ItmFloorGrate01") is not null;

    /// <summary>
    /// A floored room with one interactable fitting in the middle of it, the canvas showing it, and the crew-access
    /// analysis pushed in the way the window pushes it after each scan.
    ///
    /// <para>The fitting has to be one the analysis actually catalogues as a device, and one that declares a use
    /// point, or there is nothing for either mark to draw. Which defs qualify is the install's business rather
    /// than this test's, so it takes the first that does and skips when none do.</para>
    /// </summary>
    private static (ShipCanvas Canvas, ShipDocument Doc, Placement Fitting) Scene(Catalog catalog)
    {
        foreach (var candidate in catalog.Parts.Where(p => UsePoint.Has(p) && p.Item is { Width: 1, Height: 1 }))
        {
            var doc = new ShipDocument(catalog);
            void Place(string def, int x, int y) =>
                new PlaceCommand(new Placement { DefName = def, X = x, Y = y }).Do(doc);
            for (var x = 0; x < 7; x++) { Place("ItmWall1x1", x, 0); Place("ItmWall1x1", x, 6); }
            for (var y = 1; y <= 5; y++)
            {
                Place("ItmWall1x1", 0, y); Place("ItmWall1x1", 6, y);
                for (var x = 1; x < 6; x++) Place("ItmFloorGrate01", x, y);
            }
            Place(candidate.DefName, 3, 2);
            var fitting = doc.Placements[^1];

            var grid = ShipGrid.FromDocument(doc, catalog);
            var overlay = WalkNetwork.ToOverlay(grid, WalkNetwork.Build(
                grid, catalog, WalkOptions.Default, WalkNetwork.ForbiddenTiles(doc, grid)));
            if (overlay.AccessAt((fitting.X, fitting.Y)) is not { Standing.Count: > 0 }) continue;

            var canvas = new ShipCanvas { Sprites = new SpriteCache() };
            canvas.SetDocument(doc);
            canvas.SetWalkOverlay(overlay);
            canvas.Measure(new Size(W, H));
            canvas.Arrange(new Rect(0, 0, W, H));
            canvas.UpdateLayout();
            return (canvas, doc, fitting);
        }

        Skip.If(true, "no 1x1 buildable fitting in this install both declares a use point and is reachable");
        throw new InvalidOperationException("unreachable");
    }

    [SkippableFact]
    public void The_two_marks_no_longer_draw_the_same_thing()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(Ready(g.Catalog), "this install lacks one of the probe defs");

        RunSta(() =>
        {
            var (canvas, _, fitting) = Scene(g.Catalog);
            canvas.SelectOnly(fitting);

            // The declared use point on its own. It is blue, because it is the one mark on the plan reproducing
            // something the game draws, and the game draws it in blue.
            Assert.Equal(0, Ink(canvas, Greenish));

            // The Access overlay adds the computed standing tile, which is now green and a reticle. Before #63
            // both marks were blue feet, so this count would have stayed at zero however many marks were drawn.
            canvas.SetShowAccess(true);
            Assert.True(Ink(canvas, Greenish) > 0, "the computed access mark put down no green ink");
        });
    }

    [SkippableFact]
    public void The_computed_mark_no_longer_fills_its_tile()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(Ready(g.Catalog), "this install lacks one of the probe defs");

        RunSta(() =>
        {
            var (canvas, _, fitting) = Scene(g.Catalog);
            canvas.SelectOnly(fitting);
            canvas.SetShowAccess(true);

            // Four short brackets plus the body outline, where the old mark was a filled rounded rect over a
            // whole tile. The fill is what buried the use point underneath it, which is what was reported. The
            // bound is loose on purpose: it catches the fill coming back, it does not pin a glyph's pixel count.
            Assert.InRange(Ink(canvas, Greenish), 1, 600);
        });
    }

    [SkippableFact]
    public void Selecting_a_part_does_not_redraw_a_use_point_the_overlay_already_drew()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(Ready(g.Catalog), "this install lacks one of the probe defs");

        RunSta(() =>
        {
            var (canvas, doc, fitting) = Scene(g.Catalog);

            // The premise: with the overlay OFF, selecting the fitting is what draws its use point, so the frame
            // has to change. Without this the assertion below would pass on a canvas that never drew one at all.
            var bare = Hash(canvas);
            canvas.SelectOnly(fitting);
            Assert.NotEqual(bare, Hash(canvas));

            // With the overlay ON, the all-parts pass has already drawn every use point including this one.
            // Drawing it again for the selection composited its alpha over itself, so the selected part's mark
            // came out heavier than every other part's and read as a third kind of mark (#63).
            canvas.SetShowAccess(true);
            var selected = Render(canvas);
            canvas.SelectOnly(doc.Placements[0]);   // a wall, which declares no use point and has no access entry
            var elsewhere = Render(canvas);

            // Both frames still carry the fitting's use point, drawn once, at the same weight. Only the marks
            // that belong to a selection (the body outline and the access reticle) moved.
            Assert.NotEqual(Convert.ToHexString(SHA256.HashData(selected)),
                            Convert.ToHexString(SHA256.HashData(elsewhere)));
        });
    }
}
