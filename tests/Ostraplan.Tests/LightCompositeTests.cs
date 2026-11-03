using System.Threading;
using Ostraplan.App;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// Light Viz bakes the ship into one composite and used to rebuild it only when a scan handed it a new light scene.
/// The scan is skipped for edits the analysis cannot see, so a spawner placed or deleted with Light Viz on stayed out
/// of the lit view until the overlay was toggled (#79). These hold the edits that change the picture without
/// changing the analysis to marking the composite behind, and <see cref="ShipCanvas.RefreshLightComposite"/>, which
/// the window calls when it skips a scan, to bringing it level.
///
/// <para>Each case checks the <see cref="ScanKey"/> as well, because that is the half of the mechanism that makes
/// the refresh necessary: if one of these edits ever starts moving the key, the scan delivers a scene and this
/// path stops being the one that matters.</para>
/// </summary>
public class LightCompositeTests
{
    private static void RunSta(Action a)
    {
        Exception? err = null;
        var t = new Thread(() => { try { a(); } catch (Exception e) { err = e; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (err is not null) throw err;
    }

    /// <summary>A floored patch with Light Viz on and a scan's scene already landed, so the composite is level.</summary>
    private static (ShipDocument Doc, ShipCanvas Board, AppSettings Settings) LitEditor()
    {
        var cat = new Fixtures().Floor("Floor").Wall("Wall").Build();
        var doc = Fixtures.Doc(cat, Fixtures.P("Floor", 0, 0), Fixtures.P("Floor", 1, 0), Fixtures.P("Wall", 0, 1));
        var board = new ShipCanvas();
        board.SetDocument(doc);
        board.SetShowLight(true);
        board.SetLightScene(LightScene.Empty);   // what the scan the toggle schedules hands back
        Assert.False(board.LightCompositeBehind);
        return (doc, board, new AppSettings());
    }

    private static LooseObject Spawner() => new()
    {
        DefName = "Floor", X = 1, Y = 0,
        Spawner = new SpawnerSettings { Target = "ItmLootSpawnMedical", Range = 2 },
    };

    [Fact]
    public void Placing_a_spawner_with_Light_Viz_on_leaves_the_composite_behind_until_it_is_refreshed()
    {
        RunSta(() =>
        {
            var (doc, board, settings) = LitEditor();
            var before = ScanKey.For(doc, board, settings);

            new PlaceLooseCommand(Spawner()).Do(doc);

            Assert.Equal(before, ScanKey.For(doc, board, settings));   // so the scan is skipped
            Assert.True(board.LightCompositeBehind);
            board.RefreshLightComposite();
            Assert.False(board.LightCompositeBehind);
        });
    }

    [Fact]
    public void Deleting_a_spawner_and_undoing_either_edit_each_leave_it_behind()
    {
        RunSta(() =>
        {
            var (doc, board, settings) = LitEditor();
            var spawner = Spawner();
            var place = new PlaceLooseCommand(spawner);
            place.Do(doc);
            board.RefreshLightComposite();
            var key = ScanKey.For(doc, board, settings);

            var remove = new RemoveLooseCommand(spawner);
            remove.Do(doc);
            Assert.Equal(key, ScanKey.For(doc, board, settings));
            Assert.True(board.LightCompositeBehind);
            board.RefreshLightComposite();

            remove.Undo(doc);   // the spawner comes back
            Assert.True(board.LightCompositeBehind);
            board.RefreshLightComposite();

            place.Undo(doc);    // and goes again
            Assert.True(board.LightCompositeBehind);
            board.RefreshLightComposite();
            Assert.False(board.LightCompositeBehind);
        });
    }

    /// <summary>The same gap was open for every edit that changes how the ship looks without changing what the
    /// analysis reads, not only for spawners.</summary>
    [Fact]
    public void Painted_wear_and_a_draw_order_nudge_leave_it_behind_too()
    {
        RunSta(() =>
        {
            var (doc, board, settings) = LitEditor();
            var key = ScanKey.For(doc, board, settings);

            new SetConditionCommand(doc.Placements[0], null, 0.4).Do(doc);
            Assert.Equal(key, ScanKey.For(doc, board, settings));
            Assert.True(board.LightCompositeBehind);
            board.RefreshLightComposite();

            doc.SetZBias(doc.Placements[1], 1);
            Assert.Equal(key, ScanKey.For(doc, board, settings));
            Assert.True(board.LightCompositeBehind);
        });
    }

    /// <summary>With Light Viz off there is no composite to keep level, so an edit costs no bake.</summary>
    [Fact]
    public void An_edit_with_Light_Viz_off_does_not()
    {
        RunSta(() =>
        {
            var (doc, board, _) = LitEditor();
            board.SetShowLight(false);
            new PlaceLooseCommand(Spawner()).Do(doc);
            Assert.False(board.LightCompositeBehind);
        });
    }
}
