using System.IO;
using System.Windows;
using System.Windows.Controls;
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
            // a bed dropped straight onto bare space (Do bypasses the placement law) is illegal - it needs floor + a headboard wall.
            // The palette builds the bed in its On/base state (ItmBed01); an older build's key or a plain wall are fallbacks.
            var stray = g.Catalog.ByDefName.ContainsKey("ItmBed01") ? "ItmBed01"
                : g.Catalog.ByDefName.ContainsKey("ItmBed01Off") ? "ItmBed01Off" : "ItmWall1x1";
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
    public void Render_device_links_wireviz()
    {
        // Drives the device-connection overlay end to end: place devices on a floor, wire a breaker box to two of
        // them and a sensor to a third, turn WireViz on, and prove DrawDeviceLinks produces a real frame.
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.ContainsKey("ItmFloorGrate01")) return;
        var box = g.Catalog.Parts.FirstOrDefault(p => DevicePanels.BreakerPanel(g.Catalog, p) is not null);
        Skip.If(box is null, "no breaker-box part in this install");
        var devices = g.Catalog.Parts
            .Where(p => p.DefName != box!.DefName && p.IsSignalable && p.StartingConds.Contains("IsInstalled"))
            .DistinctBy(p => p.DefName).Take(2).ToList();
        Skip.If(devices.Count < 2, "no two other signalable installed parts in this install");
        // A real sensor/device pair too, so the green channel is exercised alongside the violet one.
        var sink = g.Catalog.Parts.FirstOrDefault(p => DevicePanels.SensorPanel(g.Catalog, p) is not null);
        var sensor = sink is null ? null : g.Catalog.Parts.FirstOrDefault(p => p.DefName != sink.DefName
            && p.StartingConds.Contains("IsInstalled")
            && DevicePanels.Satisfies(g.Catalog, p, DevicePanels.SensorPanel(g.Catalog, sink)!.ValidSourceTrigger));
        RunSta(() =>
        {
            var doc = new ShipDocument(g.Catalog);
            for (var x = 0; x < 9; x++)
                for (var y = 0; y < 5; y++)
                    new PlaceCommand(new Placement { DefName = "ItmFloorGrate01", X = x, Y = y }).Do(doc);
            Placement Dev(string def, int x, int y) { var p = new Placement { DefName = def, X = x, Y = y }; new PlaceCommand(p).Do(doc); return p; }
            var hub = Dev(box!.DefName, 1, 2);
            var a = Dev(devices[0].DefName, 5, 1);
            var b = Dev(devices[1].DefName, 5, 3);
            new AddLinkCommand(new DeviceLink(hub.Id, a.Id)).Do(doc);
            new AddLinkCommand(new DeviceLink(hub.Id, b.Id)).Do(doc);
            if (sink is not null && sensor is not null)
            {
                var driven = Dev(sink.DefName, 7, 2);
                var driver = Dev(sensor.DefName, 3, 4);
                new AddSensorLinkCommand(new SensorLink(driver.Id, driven.Id), null).Do(doc);
            }

            var canvas = new ShipCanvas { Sprites = new SpriteCache() };
            canvas.SetDocument(doc);
            canvas.SetShowWire(true);
            // Arm a pick from the breaker box, so the candidate rings and the anchor ring are drawn too — they
            // only appear while a pick is running, and are otherwise never exercised by a render test.
            canvas.BeginWirePick(hub, WireEnd.Driver);
            canvas.Measure(new Size(900, 640));
            canvas.Arrange(new Rect(0, 0, 900, 640));
            canvas.FitContent();
            canvas.UpdateLayout();

            var bitmap = new RenderTargetBitmap(900, 640, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(canvas);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(AppContext.BaseDirectory, "smoke-wires.png");
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
    public void Render_armed_ghost_shows_the_facing_needle_at_each_angle()
    {
        // Issue #13's visual half: the ghost draws a compass needle from its centre to its leading edge, at every
        // angle including 0°, over a dark halo so it survives a busy sprite. One PNG per angle, next to the test
        // binaries, so the direction can be eyeballed rather than only inferred from the trigonometry.
        var g = TestData.RequireGame();
        var part = g.Catalog.Parts
            .Where(p => !p.Item.HasSpriteSheet && p.Item.Width == 1 && p.Item.Height == 1)
            .OrderBy(p => p.DefName, StringComparer.Ordinal)
            .FirstOrDefault();
        if (part is null || !g.Catalog.ByDefName.ContainsKey("ItmFloorGrate01")) return;
        RunSta(() =>
        {
            var doc = new ShipDocument(g.Catalog);
            for (var x = 0; x < 5; x++)
                for (var y = 0; y < 5; y++)
                    new PlaceCommand(new Placement { DefName = "ItmFloorGrate01", X = x, Y = y }).Do(doc);

            var canvas = new ShipCanvas { Sprites = new SpriteCache() };
            canvas.SetDocument(doc);
            canvas.Measure(new Size(400, 400));
            canvas.Arrange(new Rect(0, 0, 400, 400));
            canvas.FitContent();
            canvas.SetArmed(part);
            canvas.SetHover((2, 2));

            foreach (var rot in new[] { 0, 90, 180, 270 })
            {
                canvas.SetArmedRot(rot);
                canvas.UpdateLayout();
                var bitmap = new RenderTargetBitmap(400, 400, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(canvas);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var path = Path.Combine(AppContext.BaseDirectory, $"smoke-needle-{rot}.png");
                using var stream = File.Create(path);
                encoder.Save(stream);
                Assert.True(new FileInfo(path).Length > 3000);
            }
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

    /// <summary>
    /// Drive the WalkViz draw path end to end on a real ship: build the analysis, push the overlay, render. The
    /// engine has its own unit tests, but the drawing (zone geometry bake, the unreachable rings, the EVA-portal
    /// dashes) is only exercised here, and a null or geometry fault in it would otherwise surface at runtime.
    /// </summary>
    [SkippableFact]
    public void Render_walk_overlay()
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            var doc = new ShipDocument(g.Catalog);
            new PlaceCommand(new Placement { DefName = Catalog.PrimaryDocksysDef, X = 0, Y = 0 }).Do(doc);
            for (var x = 0; x < 7; x++)
                for (var y = 2; y < 6; y++)
                    new PlaceCommand(new Placement { DefName = "ItmFloorGrate01", X = x, Y = y }).Do(doc);
            // a wall down the middle, so the overlay has two zones to tint apart
            for (var y = 2; y < 6; y++)
                new PlaceCommand(new Placement { DefName = "ItmWall1x1", X = 3, Y = y }).Do(doc);

            var grid = ShipGrid.FromDocument(doc, g.Catalog);
            var walk = WalkNetwork.Build(grid, g.Catalog, WalkOptions.Default, WalkNetwork.ForbiddenTiles(doc, grid));
            Assert.True(walk.Zones.Count >= 2, $"expected the wall to split the deck, got {walk.Zones.Count} zone(s)");

            var canvas = new ShipCanvas { Sprites = new SpriteCache() };
            canvas.SetDocument(doc);
            canvas.SetShowWalk(true);
            canvas.SetWalkOverlay(WalkNetwork.ToOverlay(grid, walk));
            canvas.Measure(new Size(900, 640));
            canvas.Arrange(new Rect(0, 0, 900, 640));
            canvas.FitContent();
            canvas.UpdateLayout();

            var bitmap = new RenderTargetBitmap(900, 640, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(canvas);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(AppContext.BaseDirectory, "smoke-walk.png");
            using (var stream = File.Create(path)) encoder.Save(stream);
            Assert.True(new FileInfo(path).Length > 5000);
        });
    }

    /// <summary>
    /// Every word drawn on the plan reads left to right, at every view rotation.
    ///
    /// <para>The whole render pass runs under a <c>RotateTransform</c> of <c>ViewRot</c>, so a label is upside
    /// down at 180 degrees unless it counter-rotates about its own anchor. Room labels, connector badges and the
    /// origin marker always did; the zone name did not, and a design turned round showed its zones mirrored.
    /// Asserted as a class rather than one label at a time, so the next thing to draw text on the canvas is held
    /// to it without anyone remembering to add a case.</para>
    /// </summary>
    [SkippableTheory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Every_label_on_the_plan_reads_upright_at_any_view_rotation(int viewRot)
    {
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            var canvas = TextHeavyCanvas(g);
            canvas.SetViewRot(viewRot);
            canvas.UpdateLayout();

            // Force the render pass, then read back the drawing it produced.
            new RenderTargetBitmap(900, 640, 96, 96, PixelFormats.Pbgra32).Render(canvas);

            var glyphs = new List<(GlyphRunDrawing Run, Matrix At)>();
            Collect(VisualTreeHelper.GetDrawing(canvas), Matrix.Identity, glyphs);

            Assert.NotEmpty(glyphs);   // a scene with no text at all would pass this test vacuously
            foreach (var (run, at) in glyphs)
            {
                // Upright means the baseline still runs along +x and the ascent still runs up: no rotation and no
                // flip in the accumulated transform. A tolerance rather than an equality, because the transform is
                // built by composing rotations that do not land on exact zeros.
                var what = string.Concat(run.GlyphRun.Characters);
                Assert.True(Math.Abs(at.M12) < 1e-6 && Math.Abs(at.M21) < 1e-6,
                    $"\"{what}\" is drawn rotated at ViewRot {viewRot} (M12 {at.M12}, M21 {at.M21})");
                Assert.True(at.M11 > 0 && at.M22 > 0,
                    $"\"{what}\" is drawn mirrored at ViewRot {viewRot} (M11 {at.M11}, M22 {at.M22})");
            }
        });
    }

    /// <summary>Walk a drawing tree, accumulating each group's transform, and collect every glyph run with the
    /// transform in force where it is drawn.</summary>
    private static void Collect(Drawing? drawing, Matrix at, List<(GlyphRunDrawing, Matrix)> into)
    {
        switch (drawing)
        {
            case GlyphRunDrawing glyphs:
                into.Add((glyphs, at));
                break;
            case DrawingGroup group:
                if (group.Transform is { } t) { at = Matrix.Multiply(t.Value, at); }
                foreach (var child in group.Children) Collect(child, at, into);
                break;
        }
    }

    /// <summary>A design carrying as much on-canvas text as the editor can show at once: the origin marker, a
    /// named zone, the RoomViz compartment labels, and a selected powered part's IN/OUT connector badges.</summary>
    private static ShipCanvas TextHeavyCanvas((GameEnv Env, DataIndex Index, Catalog Catalog) g)
    {
        var doc = new ShipDocument(g.Catalog);
        new PlaceCommand(new Placement { DefName = Catalog.PrimaryDocksysDef, X = 0, Y = 0 }).Do(doc);
        for (var x = 1; x < 7; x++)
            for (var y = 1; y < 6; y++)
                new PlaceCommand(new Placement { DefName = "ItmFloorGrate01", X = x, Y = y }).Do(doc);
        for (var x = 0; x < 8; x++)
        {
            new PlaceCommand(new Placement { DefName = "ItmWall1x1", X = x, Y = 0 }).Do(doc);
            new PlaceCommand(new Placement { DefName = "ItmWall1x1", X = x, Y = 6 }).Do(doc);
        }
        for (var y = 1; y < 6; y++)
        {
            new PlaceCommand(new Placement { DefName = "ItmWall1x1", X = 0, Y = y }).Do(doc);
            new PlaceCommand(new Placement { DefName = "ItmWall1x1", X = 7, Y = y }).Do(doc);
        }
        new CreateZoneCommand(new ShipZone { Name = "Hold", Tiles = [(2, 2), (3, 2), (2, 3), (3, 3)] }).Do(doc);

        var canvas = new ShipCanvas { Sprites = new SpriteCache() };
        canvas.SetDocument(doc);
        canvas.SetShowZones(true);
        canvas.SetShowRooms(true);
        canvas.SetRoomOverlay(RoomOverlay.Build(doc, g.Catalog, RoomCertifier.LoadSpecs(g.Index)));

        // A powered part selected draws its IN/OUT connector badges, which is the other family of canvas text.
        var powered = g.Catalog.Parts.FirstOrDefault(pd => pd.IsPowered && pd.Item.Width == 1 && pd.Item.Height == 1);
        if (powered is not null)
        {
            var part = new Placement { DefName = powered.DefName, X = 3, Y = 4 };
            new PlaceCommand(part).Do(doc);
            canvas.SelectedIds.Add(part.Id);
        }

        canvas.Measure(new Size(900, 640));
        canvas.Arrange(new Rect(0, 0, 900, 640));
        canvas.FitContent();
        canvas.UpdateLayout();
        return canvas;
    }

    [SkippableFact]
    public void Render_the_docked_ghost_beside_the_design()
    {
        // The other ship is drawn from the same sprite pipeline as your own, at the pose DockPose works out, and
        // it autotiles against its OWN conditions rather than the document's — a ghost with no conds draws every
        // wall as an isolated stub, which is right for a build cursor and wrong for a whole hull. This renders
        // the design alone and then with a real stock ship docked to it, and holds the second to being visibly
        // more than the first. Leaves both PNGs beside the test binaries for eyeballing.
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            var doc = new ShipDocument(g.Catalog);
            new PlaceCommand(new Placement { DefName = Catalog.PrimaryDocksysDef, X = 0, Y = 0 }).Do(doc);
            for (var x = 0; x < 7; x++)
                for (var y = 2; y < 6; y++)
                    new PlaceCommand(new Placement { DefName = "ItmFloorGrate01", X = x, Y = y }).Do(doc);

            var lookup = DockDefs.For(g.Catalog);
            var design = DockShip.FromDocument(doc, g.Catalog, lookup, "design");
            Skip.If(design.Ports.Count == 0, "the seeded airlock did not register as a port");

            // A real ship with a primary airlock, read in its own template frame.
            DockShip? other = null;
            foreach (var file in TemplateImport.ListShipFiles(g.Index))
            {
                foreach (var tmpl in ShipTemplate.ParseFileChecked(File.ReadAllText(file.Path), out _))
                {
                    var ship = DockShip.FromTemplate(tmpl, g.Catalog, lookup);
                    if (ship.Ports.Any(p => !p.TypeB) && ship.Parts.Count is > 40 and < 4000) other = ship;
                    if (other is not null) break;
                }
                if (other is not null) break;
            }
            Skip.If(other is null, "no suitable stock ship with a primary airlock");

            var mate = DockMating.Mate(other!, design, other!.Ports.First(p => !p.TypeB), design.Ports[0]);
            Skip.If(mate.Pose is null, "the pair produced no pose");
            var posed = DockPose.ReceiverParts(other!, design, mate.Pose!);
            Assert.NotEmpty(posed);

            var canvas = new ShipCanvas { Sprites = new SpriteCache() };
            canvas.SetDocument(doc);
            canvas.Measure(new Size(900, 640));
            canvas.Arrange(new Rect(0, 0, 900, 640));
            canvas.FitContent();
            canvas.UpdateLayout();

            var bare = Snap(canvas, "smoke-docked-none.png");

            Assert.False(canvas.HasDockedGhost);
            canvas.SetDockedGhost(posed);
            Assert.True(canvas.HasDockedGhost);
            canvas.UpdateLayout();
            var ghosted = Snap(canvas, "smoke-docked-ghost.png");

            // A ghost that drew nothing, or drew off-screen, would leave the two frames the same size.
            Assert.True(ghosted > bare,
                $"the docked ghost added nothing to the frame ({bare} -> {ghosted} bytes)");

            // And it comes off again.
            canvas.SetDockedGhost([]);
            Assert.False(canvas.HasDockedGhost);
        });
    }

    private static long Snap(ShipCanvas canvas, string name)
    {
        var bitmap = new RenderTargetBitmap(900, 640, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(AppContext.BaseDirectory, name);
        using (var stream = File.Create(path)) encoder.Save(stream);
        return new FileInfo(path).Length;
    }

    [SkippableFact]
    public void Render_the_docking_window_layout()
    {
        // The window's three states, rendered offscreen to PNGs beside the test binaries. It asserts only that
        // each one drew something and stayed inside its declared width; the point is the same as --dlgsmoke's,
        // which is to make a layout eyeballable without launching the app. The pane that pushed this in was the
        // pair list: it replaced a matrix whose star-sized columns spread three buttons across the whole window.
        var g = TestData.RequireGame();
        RunSta(() =>
        {
            ThemeManager.Apply("dark");

            var doc = new ShipDocument(g.Catalog);
            new PlaceCommand(new Placement { DefName = Catalog.PrimaryDocksysDef, X = 0, Y = 0 }).Do(doc);
            for (var x = 0; x < 7; x++)
                for (var y = 2; y < 6; y++)
                    new PlaceCommand(new Placement { DefName = "ItmFloorGrate01", X = x, Y = y }).Do(doc);

            var lookup = DockDefs.For(g.Catalog);
            var design = DockShip.FromDocument(doc, g.Catalog, lookup, "Your Design");
            Skip.If(design.Ports.Count == 0, "the seeded airlock did not register");

            DockShip? other = null;
            foreach (var file in TemplateImport.ListShipFiles(g.Index))
            {
                foreach (var t in ShipTemplate.ParseFileChecked(File.ReadAllText(file.Path), out _))
                {
                    var ship = DockShip.FromTemplate(t, g.Catalog, lookup);
                    if (ship.Ports.Count >= 2) other = ship;
                    if (other is not null) break;
                }
                if (other is not null) break;
            }
            Skip.If(other is null, "no stock ship carries two airlocks");

            var window = new DockingWindow(design, () => design, g.Catalog, g.Index,
                _ => { }, _ => { }, _ => Task.FromResult<DockShip?>(null));

            SnapWindow(window, "dock-window-start.png");
            window.ShowReportForPreview(DockMating.Cross(other!, design));
            SnapWindow(window, "dock-window-pairs.png");
            window.SetMode(DockingMode.EveryShip);
            SnapWindow(window, "dock-window-survey.png");
        });
    }

    /// <summary>Render a window's content offscreen at its declared width. Asserts the frame is non-trivial and
    /// that nothing forced the layout wider than the window says it is, which is the failure a wrapping
    /// TextBlock with no bound produces (CONVENTIONS.md).</summary>
    private static void SnapWindow(Window window, string name)
    {
        const int w = 500;
        var root = (FrameworkElement)window.Content;
        if (root is Panel panel) panel.Background = ThemeManager.WindowBg;   // the window paints this, the visual does not
        root.Width = w;
        root.Measure(new Size(w, double.PositiveInfinity));
        Assert.True(root.DesiredSize.Width <= w + 0.5,
            $"{name}: content wants {root.DesiredSize.Width:0} px in a {w} px window");
        var h = Math.Max(1, root.DesiredSize.Height);
        root.Arrange(new Rect(0, 0, w, h));
        root.UpdateLayout();

        var bmp = new RenderTargetBitmap(w, (int)Math.Ceiling(h), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        var path = Path.Combine(AppContext.BaseDirectory, name);
        using (var stream = File.Create(path)) encoder.Save(stream);
        Assert.True(new FileInfo(path).Length > 1000, $"{name} suspiciously small");
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
