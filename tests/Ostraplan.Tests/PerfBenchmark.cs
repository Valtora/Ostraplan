using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ostraplan.App;
using Ostraplan.Core;
using Xunit;
using Xunit.Abstractions;

namespace Ostraplan.Tests;

/// <summary>
/// A repeatable timing baseline for the paths that decide how a big design feels: the per-edit repaint, the
/// analysis passes behind the overlays, and the Core helpers each of those leans on.
///
/// <para><b>This is a measurement, not an assertion.</b> Timings are machine- and load-dependent, so nothing here
/// fails on a number. It exists so a change aimed at responsiveness can be held against a before, the same way
/// <c>--dlgsmoke</c> exists to eyeball a layout change. Excluded from a normal run (see <c>scripts\test.ps1
/// -Benchmark</c>) because it loads real templates and repeats each measurement.</para>
///
/// <para>Read the output with a detailed logger:
/// <c>dotnet test --filter Category=Benchmark --logger "console;verbosity=detailed"</c>.</para>
/// </summary>
[Trait("Category", "Benchmark")]
public sealed class PerfBenchmark(ITestOutputHelper o)
{
    /// <summary>Three points across the real scale range: a large player ship, a mid station, and the largest
    /// template the game ships. Median template is ~900 parts, so the first row is already a big design.</summary>
    public static TheoryData<string> Designs => new() { "Dancing Jack", "Station_SVIR", "LA_MAINTENANCE_1" };

    /// <summary>Mean wall-clock of <paramref name="iters"/> runs, after one warm-up run.</summary>
    private static double Ms(Action a, int iters)
    {
        a();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iters; i++) a();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds / iters;
    }

    private void Row(string label, double ms) => o.WriteLine($"  {label,-28} {ms,9:F2} ms");

    /// <summary>WPF types need an STA thread, and the ship bake recurses over a deep visual tree.</summary>
    private static void RunSta(Action a)
    {
        Exception? err = null;
        var t = new Thread(() => { try { a(); } catch (Exception e) { err = e; } }, 64 * 1024 * 1024);
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (err is not null) throw err;
    }

    [SkippableTheory]
    [MemberData(nameof(Designs))]
    public void Core_paths(string design)
    {
        var g = TestData.RequireGame();
        var doc = Load(g, design, out var name);
        var b = doc.Bounds()!.Value;
        o.WriteLine($"{name}: {doc.Placements.Count} placements, {b.MaxX - b.MinX + 1}x{b.MaxY - b.MinY + 1} tiles");

        Row("RenderOrder()", Ms(() => { var _ = doc.RenderOrder().ToList(); }, 30));
        Row("Snapshot()", Ms(() => doc.Snapshot(), 20));
        Row("Bounds()", Ms(() => doc.Bounds(), 100));
        Row("ProblemScan.BoundingPort", Ms(() => ProblemScan.BoundingPort(doc, g.Catalog), 100));
        Row("ProblemScan.Scan", Ms(() => ProblemScan.Scan(doc, g.Catalog), 5));

        var grid = ShipGrid.FromDocument(doc, g.Catalog);
        Row("ShipGrid.FromDocument", Ms(() => ShipGrid.FromDocument(doc, g.Catalog), 10));
        Row("PowerNetwork.Build", Ms(() => PowerNetwork.Build(grid, g.Catalog), 10));
        Row("LightNetwork.Build", Ms(() => LightNetwork.Build(grid, g.Catalog, null), 5));
        var forbidden = WalkNetwork.ForbiddenTiles(doc, grid);
        Row("WalkNetwork.Build", Ms(() => WalkNetwork.Build(grid, g.Catalog, new WalkOptions(), forbidden), 5));
        var specs = RoomCertifier.LoadSpecs(g.Index);
        Row("RoomOverlay.Build", Ms(() => RoomOverlay.Build(doc, g.Catalog, specs), 5));
    }

    /// <summary>
    /// The headline user-facing number: place one part and repaint, which is what a single click costs and what a
    /// drag-paint pays per tile. Offscreen through <see cref="RenderTargetBitmap"/>, so the rasterisation half is
    /// software and reads slower than the live compositor; the bake half is the same work either way, and the
    /// figure is comparable against itself across a change.
    /// </summary>
    [SkippableTheory]
    [MemberData(nameof(Designs))]
    public void Edit_and_repaint(string design)
    {
        var g = TestData.RequireGame();
        var doc = Load(g, design, out var name);
        var b = doc.Bounds()!.Value;
        o.WriteLine($"{name}: {doc.Placements.Count} placements");

        RunSta(() =>
        {
            var canvas = new ShipCanvas { Sprites = new SpriteCache() };
            canvas.SetDocument(doc);
            canvas.Measure(new Size(1600, 900));
            canvas.Arrange(new Rect(0, 0, 1600, 900));
            canvas.FitContent();
            canvas.UpdateLayout();
            var rtb = new RenderTargetBitmap(1600, 900, 96, 96, PixelFormats.Pbgra32);

            Row("repaint, no edit", Ms(() => { canvas.InvalidateVisual(); canvas.UpdateLayout(); rtb.Render(canvas); }, 10));

            // Each iteration places a fresh tile clear of the hull, so no two collide and every one is a real edit.
            var y = b.MinY;
            Row("place 1 part + repaint", Ms(() =>
            {
                new PlaceCommand(new Placement { DefName = "ItmFloorGrate01", X = b.MaxX + 3, Y = y++ }).Do(doc);
                canvas.UpdateLayout();
                rtb.Render(canvas);
            }, 10));
        });
    }

    private static ShipDocument Load((GameEnv Env, DataIndex Index, Catalog Catalog) g, string design, out string name)
    {
        var f = TemplateImport.ListShipFiles(g.Index)
            .First(x => x.Name.Contains(design, StringComparison.OrdinalIgnoreCase));
        name = f.Name;
        return TemplateImport.LoadFile(f.Path, g.Catalog).Doc;
    }
}
