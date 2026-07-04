using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ostraplan.App;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// Offscreen render of a small ship built from real game parts - proves the
/// sprite pipeline (paths, sheet cropping, autotile masks) end to end and
/// leaves smoke.png next to the test binaries for eyeballing.
/// </summary>
public class RenderSmokeTests
{
    [Fact]
    public void Render_small_ship_to_png()
    {
        if (TestData.Game is not { } g) return;
        RunSta(() => Run(g.Catalog));
    }

    [Fact]
    public void Render_primary_airlock_stripes_and_rotated_view()
    {
        if (TestData.Game is not { } g) return;
        RunSta(() =>
        {
            var doc = new ShipDocument(g.Catalog);
            new PlaceCommand(new Placement { DefName = Catalog.PrimaryDocksysDef, X = 0, Y = 0 }).Do(doc);
            for (var x = 0; x < 7; x++)
                for (var y = 2; y < 6; y++)
                    new PlaceCommand(new Placement { DefName = "ItmFloorGrate01", X = x, Y = y }).Do(doc);

            var canvas = new ShipCanvas { Sprites = new SpriteCache() };
            canvas.SetDocument(doc);
            canvas.RotateView(90);
            canvas.Measure(new Size(900, 640));
            canvas.Arrange(new Rect(0, 0, 900, 640));
            canvas.FitContent();
            canvas.UpdateLayout();

            var bitmap = new RenderTargetBitmap(900, 640, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(canvas);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(AppContext.BaseDirectory, "smoke-primary-rotated.png");
            using (var stream = File.Create(path)) encoder.Save(stream);
            Assert.True(new FileInfo(path).Length > 5000);
        });
    }

    [Fact]
    public void Render_illegal_placement_hazard_tint()
    {
        if (TestData.Game is not { } g) return;
        RunSta(() =>
        {
            var doc = new ShipDocument(g.Catalog);
            new PlaceCommand(new Placement { DefName = Catalog.PrimaryDocksysDef, X = 0, Y = 0 }).Do(doc);
            // a bed dropped straight onto bare space (Do bypasses the placement law) is illegal - it needs floor + a headboard wall
            var stray = g.Catalog.ByDefName.ContainsKey("ItmBed01Off") ? "ItmBed01Off" : "ItmWall1x1";
            new PlaceCommand(new Placement { DefName = stray, X = 3, Y = 4 }).Do(doc);

            var cells = ProblemScan.Scan(doc, g.Catalog)
                .Where(p => p.Cells is not null).SelectMany(p => p.Cells!).Distinct().ToList();
            Assert.NotEmpty(cells);   // the stray placement produced hazard cells to tint

            var canvas = new ShipCanvas { Sprites = new SpriteCache() };
            canvas.SetDocument(doc);
            canvas.SetIllegalCells(cells);
            canvas.Measure(new Size(900, 640));
            canvas.Arrange(new Rect(0, 0, 900, 640));
            canvas.FitContent();
            canvas.UpdateLayout();

            var bitmap = new RenderTargetBitmap(900, 640, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(canvas);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(AppContext.BaseDirectory, "smoke-illegal.png");
            using (var stream = File.Create(path)) encoder.Save(stream);
            Assert.True(new FileInfo(path).Length > 5000);
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

    private static void Run(Catalog catalog)
    {
        Assert.True(catalog.ByDefName.ContainsKey("ItmWall1x1"), "wall not in palette");
        Assert.True(catalog.ByDefName.ContainsKey("ItmFloorGrate01"), "floor grate not in palette");
        // doors are built in their Open state, beds Off - that's how the menu names them
        var hasDoor = catalog.ByDefName.ContainsKey("ItmDoor01Open");
        var hasBed = catalog.ByDefName.ContainsKey("ItmBed01Off");

        var doc = new ShipDocument(catalog);
        void Place(string def, int x, int y, int rot = 0) =>
            new PlaceCommand(new Placement { DefName = def, X = x, Y = y, Rot = rot }).Do(doc);

        const int w = 12, h = 9;
        for (var x = 1; x < w - 1; x++)
            for (var y = 1; y < h - 1; y++)
                Place("ItmFloorGrate01", x, y);
        for (var x = 0; x < w; x++)
        {
            if (!(hasDoor && x is >= 3 and <= 7)) Place("ItmWall1x1", x, 0);   // door replaces this span
            Place("ItmWall1x1", x, h - 1);
        }
        for (var y = 1; y < h - 1; y++)
        {
            Place("ItmWall1x1", 0, y);
            Place("ItmWall1x1", w - 1, y);
        }
        if (hasDoor) Place("ItmDoor01Open", 3, 0);
        if (hasBed) Place("ItmBed01Off", 7, 2);

        var canvas = new ShipCanvas { Sprites = new SpriteCache() };
        canvas.SetDocument(doc);
        canvas.Measure(new Size(1000, 700));
        canvas.Arrange(new Rect(0, 0, 1000, 700));
        canvas.FitContent();
        canvas.UpdateLayout();

        var bitmap = new RenderTargetBitmap(1000, 700, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        var path = Path.Combine(AppContext.BaseDirectory, "smoke.png");
        using (var stream = File.Create(path)) encoder.Save(stream);

        Assert.True(new FileInfo(path).Length > 5000, $"smoke.png suspiciously small ({new FileInfo(path).Length} bytes)");
    }
}
