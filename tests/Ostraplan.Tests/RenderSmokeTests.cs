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
    [SkippableFact]
    public void Render_small_ship_to_png()
    {
        var g = TestData.RequireGame();
        RunSta(() => Run(g.Catalog));
    }

    [SkippableFact]
    public void Render_primary_airlock_stripes_and_rotated_view()
    {
        var g = TestData.RequireGame();
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

    [SkippableFact]
    public void Render_illegal_placement_hazard_tint()
    {
        var g = TestData.RequireGame();
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

    [SkippableFact]
    public void Render_zone_overlay()
    {
        // Drives the whole zone overlay path end to end (create zones, show the overlay, make one active for
        // painting) and proves DrawZones + the zone commands don't throw and produce a real frame.
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.ContainsKey("ItmFloorGrate01")) return;
        RunSta(() =>
        {
            var doc = new ShipDocument(g.Catalog);
            for (var x = 0; x < 8; x++)
                for (var y = 0; y < 6; y++)
                    new PlaceCommand(new Placement { DefName = "ItmFloorGrate01", X = x, Y = y }).Do(doc);

            var haul = new ShipZone { Name = "Cargo", Color = new ZoneColor(0.24, 0.74, 0.66, 1), TileConds = { ShipZone.CondHaul, ShipZone.CondBarter } };
            for (var x = 0; x < 4; x++) for (var y = 0; y < 3; y++) haul.Tiles.Add((x, y));
            var forbid = new ShipZone { Name = "No-go", Color = new ZoneColor(0.85, 0.24, 0.24, 1), TileConds = { ShipZone.CondForbid } };
            for (var x = 5; x < 8; x++) for (var y = 3; y < 6; y++) forbid.Tiles.Add((x, y));
            new CreateZoneCommand(haul).Do(doc);
            new CreateZoneCommand(forbid).Do(doc);

            var canvas = new ShipCanvas { Sprites = new SpriteCache() };
            canvas.SetDocument(doc);
            canvas.SetShowZones(true);
            canvas.SetActiveZone(haul.Id);   // the active zone is tinted more strongly
            canvas.Measure(new Size(900, 640));
            canvas.Arrange(new Rect(0, 0, 900, 640));
            canvas.FitContent();
            canvas.UpdateLayout();

            var bitmap = new RenderTargetBitmap(900, 640, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(canvas);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(AppContext.BaseDirectory, "smoke-zones.png");
            using (var stream = File.Create(path)) encoder.Save(stream);
            Assert.True(new FileInfo(path).Length > 5000);
        });
    }

    [SkippableFact]
    public void Large_tank_sprite_is_3x3_inside_its_7x7_footprint()
    {
        var g = TestData.RequireGame();
        var def = ItemDef.Parse(g.Index.Type("items")["ItmCanisterLH02"].El);
        var part = new PartDef("ItmCanisterLH02", "D2O Tank", "POWR", "core", def,
            g.Index.ResolveImage(def.Img), [], [], [], new Dictionary<string, double>(), new Dictionary<string, (double, double)>());

        Assert.Equal((7, 7), (part.Item.Width, part.Item.Height));   // socket/placement footprint
        Assert.Equal((3, 3), new SpriteCache().SpriteTiles(part));   // 48x48 sprite -> drawn 3x3, centered
    }

    [SkippableFact]
    public void Render_large_tank_sprite_centered_in_footprint()
    {
        var g = TestData.RequireGame();
        var tank = g.Catalog.Parts.FirstOrDefault(p => p.Item.Width == 7 && p.Item.Height == 7);
        if (tank is null || !g.Catalog.ByDefName.ContainsKey("ItmFloorGrate01")) return;
        RunSta(() =>
        {
            var doc = new ShipDocument(g.Catalog);
            for (var y = 0; y < 7; y++)                       // a 7x7 sealed-floor pad...
                for (var x = 0; x < 7; x++)
                    new PlaceCommand(new Placement { DefName = "ItmFloorGrate01", X = x, Y = y }).Do(doc);
            new PlaceCommand(new Placement { DefName = tank.DefName, X = 0, Y = 0 }).Do(doc);   // ...the tank sits centered on it

            var canvas = new ShipCanvas { Sprites = new SpriteCache() };
            canvas.SetDocument(doc);
            canvas.Measure(new Size(700, 700));
            canvas.Arrange(new Rect(0, 0, 700, 700));
            canvas.FitContent();
            canvas.UpdateLayout();

            var bitmap = new RenderTargetBitmap(700, 700, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(canvas);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(AppContext.BaseDirectory, "smoke-tank.png");
            using (var stream = File.Create(path)) encoder.Save(stream);
            Assert.True(new FileInfo(path).Length > 5000);
        });
    }

    [SkippableFact]
    public void Render_tank_ghost_shades_the_under_floor_reservation()
    {
        var g = TestData.RequireGame();
        var tank = g.Catalog.Parts.FirstOrDefault(p => p.Item.Width == 7 && p.Item.Height == 7);
        if (tank is null || !g.Catalog.ByDefName.ContainsKey("ItmFloorGrate01")) return;
        RunSta(() =>
        {
            var doc = new ShipDocument(g.Catalog);
            for (var y = 0; y < 7; y++)                       // a 7x7 sealed-floor pad the tank fits on
                for (var x = 0; x < 7; x++)
                    new PlaceCommand(new Placement { DefName = "ItmFloorGrate01", X = x, Y = y }).Do(doc);

            var canvas = new ShipCanvas { Sprites = new SpriteCache() };
            canvas.SetDocument(doc);
            canvas.Measure(new Size(700, 700));
            canvas.Arrange(new Rect(0, 0, 700, 700));
            canvas.FitContent();
            canvas.SetArmed(tank);
            canvas.SetHover((3, 3));   // ghost footprint (0,0)-(6,6) lands on the pad -> green, ring shaded
            canvas.UpdateLayout();

            var bitmap = new RenderTargetBitmap(700, 700, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(canvas);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(AppContext.BaseDirectory, "smoke-tank-ghost.png");
            using (var stream = File.Create(path)) encoder.Save(stream);
            Assert.True(new FileInfo(path).Length > 5000);
        });
    }

    [SkippableFact]
    public void Render_symmetry_previews_the_cursor_pose_and_its_mirror()
    {
        // Symmetry now ghosts every mirror, not just the cursor part, so a mirror that won't land is visible
        // before the click. This drives that path end to end (arm a part, enable Vertical symmetry, hover) and
        // proves the multi-ghost render doesn't throw and produces a real frame.
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.ContainsKey("ItmFloorGrate01") || !g.Catalog.ByDefName.ContainsKey("ItmWall1x1")) return;
        RunSta(() =>
        {
            var doc = new ShipDocument(g.Catalog);
            for (var x = 0; x < 10; x++)
                for (var y = 0; y < 6; y++)
                    new PlaceCommand(new Placement { DefName = "ItmFloorGrate01", X = x, Y = y }).Do(doc);

            var canvas = new ShipCanvas { Sprites = new SpriteCache() };
            canvas.SetDocument(doc);
            canvas.Measure(new Size(900, 640));
            canvas.Arrange(new Rect(0, 0, 900, 640));
            canvas.FitContent();
            canvas.SetHover((5, 3));       // axis centre on the pad...
            canvas.CycleSymmetry();        // ...enable Vertical symmetry there
            canvas.SetArmed(g.Catalog.ByDefName["ItmWall1x1"]);
            canvas.SetHover((2, 3));       // arm a wall left of the axis; its mirror previews to the right
            canvas.UpdateLayout();

            var bitmap = new RenderTargetBitmap(900, 640, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(canvas);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(AppContext.BaseDirectory, "smoke-symmetry-ghost.png");
            using (var stream = File.Create(path)) encoder.Save(stream);
            Assert.True(new FileInfo(path).Length > 5000);
        });
    }

    [SkippableFact]
    public void Snapshot_renders_the_ship_to_a_sized_png()
    {
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.ContainsKey("ItmFloorGrate01")) return;
        RunSta(() =>
        {
            var doc = new ShipDocument(g.Catalog);
            for (var x = 0; x < 5; x++)
                for (var y = 0; y < 4; y++)
                    new PlaceCommand(new Placement { DefName = "ItmFloorGrate01", X = x, Y = y }).Do(doc);

            var canvas = new ShipCanvas { Sprites = new SpriteCache() };
            canvas.SetDocument(doc);
            var bmp = canvas.RenderSnapshot(pxPerTile: 32, marginTiles: 1);
            Assert.NotNull(bmp);
            // 5x4 ship + a 1-tile margin each side = 7x6 tiles at 32 px
            Assert.Equal(7 * 32, bmp!.PixelWidth);
            Assert.Equal(6 * 32, bmp.PixelHeight);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            var path = Path.Combine(AppContext.BaseDirectory, "smoke-snapshot.png");
            using (var stream = File.Create(path)) encoder.Save(stream);
            Assert.True(new FileInfo(path).Length > 2000);
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
