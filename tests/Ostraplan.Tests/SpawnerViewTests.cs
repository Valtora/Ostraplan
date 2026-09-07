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
/// The spawners view (#68): hiding the editor objects, and drawing the square they scatter over.
///
/// <para>These compare the canvas against <b>itself</b> rather than against a stored image, the same bargain
/// <see cref="BakeWindowTests"/> makes: they say nothing about which sprites the game ships, and survive a game
/// update that changes them.</para>
/// </summary>
public class SpawnerViewTests
{
    private const int W = 640, H = 480;

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

    /// <summary>A floored patch, optionally with a spawner standing on it.</summary>
    private static ShipDocument Deck(Catalog catalog, LooseObject? spawner)
    {
        var doc = new ShipDocument(catalog);
        for (var x = 0; x < 8; x++)
            for (var y = 0; y < 8; y++)
                new PlaceCommand(new Placement { DefName = "ItmFloorGrate01", X = x, Y = y }).Do(doc);
        if (spawner is not null) new PlaceLooseCommand(spawner).Do(doc);
        return doc;
    }

    private static LooseObject Spawner(int range = 2) => new()
    {
        DefName = "SysLootSpawner", X = 4, Y = 4,
        Spawner = new SpawnerSettings { Target = "ItmLootSpawnMedical", Range = range },
    };

    private static bool Ready(Catalog c) =>
        c.Lookup("ItmFloorGrate01") is not null && c.Lookup("SysLootSpawner") is not null;

    [SkippableFact]
    public void A_hidden_spawner_leaves_the_plan_as_if_it_were_not_there()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(Ready(g.Catalog), "this install lacks one of the probe defs");

        RunSta(() =>
        {
            var bare = Pixels(Board(Deck(g.Catalog, null)));

            var canvas = Board(Deck(g.Catalog, Spawner()));
            canvas.SetSpawnerView(SpawnerScatterWhen.Never, SpawnerScatterStyle.Box);
            var shown = Pixels(canvas);

            // The premise: with the toggle on, a spawner is something you can see. Without this, the assertion
            // below would pass just as well on a canvas that never drew one.
            Assert.NotEqual(bare, shown);

            canvas.SetShowSpawners(false);
            Assert.Equal(bare, Pixels(canvas));

            // …and back again, because hiding is a view and not a deletion.
            canvas.SetShowSpawners(true);
            Assert.Equal(shown, Pixels(canvas));
        });
    }

    [SkippableFact]
    public void Hiding_a_spawner_changes_nothing_about_the_design()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(Ready(g.Catalog), "this install lacks one of the probe defs");

        RunSta(() =>
        {
            var spawner = Spawner();
            var doc = Deck(g.Catalog, spawner);
            var canvas = Board(doc);
            canvas.SetShowSpawners(false);
            Pixels(canvas);

            // A view toggle that quietly dropped the spawner from the document would export a ship that arrives
            // carrying nothing, and nothing on screen would say so.
            Assert.Contains(spawner, doc.LooseObjects);
            Assert.NotNull(spawner.Spawner);
        });
    }

    [SkippableFact]
    public void The_scatter_square_is_drawn_only_when_it_is_asked_for()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(Ready(g.Catalog), "this install lacks one of the probe defs");

        RunSta(() =>
        {
            var canvas = Board(Deck(g.Catalog, Spawner()));

            canvas.SetSpawnerView(SpawnerScatterWhen.Never, SpawnerScatterStyle.Box);
            var origin = Pixels(canvas);

            canvas.SetSpawnerView(SpawnerScatterWhen.Always, SpawnerScatterStyle.Box);
            var always = Pixels(canvas);
            Assert.NotEqual(origin, always);

            // Nothing is selected, so "only when selected" has nothing to draw and must match "never" exactly.
            canvas.SetSpawnerView(SpawnerScatterWhen.Selected, SpawnerScatterStyle.Box);
            Assert.Equal(origin, Pixels(canvas));

            // The two styles are different pictures of the same square.
            canvas.SetSpawnerView(SpawnerScatterWhen.Always, SpawnerScatterStyle.Sprite);
            Assert.NotEqual(always, Pixels(canvas));
        });
    }

    [SkippableFact]
    public void Selecting_a_spawner_brings_its_square_up()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(Ready(g.Catalog), "this install lacks one of the probe defs");

        RunSta(() =>
        {
            var spawner = Spawner();
            var canvas = Board(Deck(g.Catalog, spawner));
            canvas.SetSpawnerView(SpawnerScatterWhen.Selected, SpawnerScatterStyle.Box);
            var unselected = Pixels(canvas);

            canvas.SelectOnlyLoose(spawner);
            var selected = Pixels(canvas);
            Assert.NotEqual(unselected, selected);

            // The square has to be the spawner's own, so a wider scatter is a different picture again.
            canvas.SetDocument(Deck(g.Catalog, Spawner(range: 5)));
            canvas.SelectOnlyLoose(canvas.Doc!.LooseObjects.Single(o => o.Spawner is not null));
            Assert.NotEqual(selected, Pixels(canvas));
        });
    }

    [SkippableFact]
    public void A_scatter_of_zero_still_draws_its_own_tile()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(Ready(g.Catalog), "this install lacks one of the probe defs");

        RunSta(() =>
        {
            var canvas = Board(Deck(g.Catalog, Spawner(range: 0)));

            canvas.SetSpawnerView(SpawnerScatterWhen.Never, SpawnerScatterStyle.Box);
            var origin = Pixels(canvas);

            // 2,849 of the game's own spawners are range 0, and "no box at all" would read as "this view is not
            // working" rather than as "this spawner puts everything on one tile".
            canvas.SetSpawnerView(SpawnerScatterWhen.Always, SpawnerScatterStyle.Box);
            Assert.NotEqual(origin, Pixels(canvas));
        });
    }
}
