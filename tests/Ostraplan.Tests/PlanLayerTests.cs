using System.Threading;
using Ostraplan.App;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The Layers panel. A hidden layer is off the plan and a locked one is drawn, and either is out of reach of every
/// pick the mouse makes, which is what lets the floor under an airlock be clicked, box-selected or erased without
/// deleting the airlock to get at it. These lock the classification, the state, and the canvas rule every pick goes
/// through.
/// </summary>
public class PlanLayerTests
{
    private static Catalog Catalog() => new Fixtures()
        .Floor("Floor")
        .Wall("Wall")
        .Door("Door")
        .Conduit("Conduit")
        .Fixture("Bed")
        .Part("Widget", category: "FIXT")
        .Build();

    [Fact]
    public void Each_kind_of_deck_element_lands_on_its_own_layer()
    {
        var cat = Catalog();
        Assert.Equal(PlanLayer.Floors, LayerView.Of(cat, Fixtures.P("Floor", 0, 0)));
        Assert.Equal(PlanLayer.Walls, LayerView.Of(cat, Fixtures.P("Wall", 0, 0)));
        Assert.Equal(PlanLayer.Walls, LayerView.Of(cat, Fixtures.P("Door", 0, 0)));   // a door seals floor too, but is a wall first
        Assert.Equal(PlanLayer.Conduits, LayerView.Of(cat, Fixtures.P("Conduit", 0, 0)));
        Assert.Equal(PlanLayer.Fixtures, LayerView.Of(cat, Fixtures.P("Bed", 0, 0)));
        Assert.Equal(PlanLayer.Loose, LayerView.Of(new LooseObject { DefName = "Widget", X = 0, Y = 0 }));
        Assert.Equal(PlanLayer.Spawners, LayerView.Of(new LooseObject { DefName = "SysLootSpawner", X = 0, Y = 0, Spawner = new SpawnerSettings() }));
    }

    [Fact]
    public void Hidden_and_locked_are_both_out_of_reach_and_only_hidden_is_out_of_sight()
    {
        var view = LayerView.Default.WithHidden(PlanLayer.Walls, true).WithLocked(PlanLayer.Floors, true);

        Assert.True(view.IsHidden(PlanLayer.Walls));
        Assert.False(view.IsPickable(PlanLayer.Walls));
        Assert.False(view.IsHidden(PlanLayer.Floors));
        Assert.False(view.IsPickable(PlanLayer.Floors));
        Assert.True(view.IsPickable(PlanLayer.Fixtures));
        Assert.False(view.IsDefault);
    }

    [Fact]
    public void A_change_that_changes_nothing_hands_back_the_same_state()
    {
        // The canvas skips its rebake on reference equality, so a no-op toggle must not look like a change.
        var view = LayerView.Default.WithLocked(PlanLayer.Walls, true);
        Assert.Same(view, view.WithLocked(PlanLayer.Walls, true));
        Assert.Same(LayerView.Default, LayerView.Default.WithHidden(PlanLayer.Floors, false));
        Assert.False(LayerView.Default.IsHidden(PlanLayer.Walls));   // and the shared default is never mutated
    }

    [Fact]
    public void The_summary_names_what_is_off_in_panel_order()
    {
        Assert.Null(LayerView.Default.Summary());
        var view = LayerView.Default.WithLocked(PlanLayer.Conduits, true).WithHidden(PlanLayer.Walls, true)
            .WithLocked(PlanLayer.Walls, true);
        Assert.Equal("Walls & doors hidden, Conduits locked", view.Summary());
    }

    [Fact]
    public void Saved_state_with_a_name_this_build_does_not_know_still_loads()
    {
        var view = LayerView.Parse(["Walls", "Hologram"], ["Floors"]);
        Assert.True(view.IsHidden(PlanLayer.Walls));
        Assert.True(view.IsLocked(PlanLayer.Floors));
        Assert.Single(view.Hidden);
    }

    [Fact]
    public void The_pick_stack_leaves_out_hidden_and_locked_layers()
    {
        RunSta(() =>
        {
            var cat = Catalog();
            var doc = Fixtures.Doc(cat, Fixtures.P("Floor", 0, 0), Fixtures.P("Wall", 0, 0), Fixtures.P("Conduit", 0, 0));
            var canvas = new ShipCanvas();
            canvas.SetDocument(doc);

            Assert.Equal(3, canvas.PickStackAt(0, 0).Count);

            canvas.SetLayers(LayerView.Default.WithLocked(PlanLayer.Walls, true));
            Assert.DoesNotContain(canvas.PickStackAt(0, 0), i => i.DefName == "Wall");
            Assert.Equal(2, canvas.PickStackAt(0, 0).Count);

            canvas.SetLayers(canvas.Layers.WithHidden(PlanLayer.Conduits, true));
            Assert.Equal(["Floor"], canvas.PickPlacementsAt(0, 0).Select(p => p.DefName));
        });
    }

    [Fact]
    public void Putting_a_layer_out_of_reach_drops_it_from_the_selection()
    {
        // Otherwise Delete would act on a part the user can no longer see or click.
        RunSta(() =>
        {
            var cat = Catalog();
            var doc = Fixtures.Doc(cat, Fixtures.P("Floor", 0, 0), Fixtures.P("Wall", 1, 0));
            var canvas = new ShipCanvas();
            canvas.SetDocument(doc);
            canvas.SetSelection(doc.Placements);
            Assert.Equal(2, canvas.SelectedIds.Count);

            canvas.SetLayers(LayerView.Default.WithHidden(PlanLayer.Walls, true));

            Assert.Equal(["Floor"], canvas.SelectedPlacements().Select(p => p.DefName));
        });
    }

    [Fact]
    public void The_spawners_toggle_is_the_spawners_layer_eye()
    {
        RunSta(() =>
        {
            var canvas = new ShipCanvas();
            canvas.SetDocument(new ShipDocument(Catalog()));
            Assert.True(canvas.ShowSpawners);

            canvas.SetShowSpawners(false);
            Assert.True(canvas.Layers.IsHidden(PlanLayer.Spawners));

            canvas.SetLayers(LayerView.Default);
            Assert.True(canvas.ShowSpawners);
        });
    }

    [Fact]
    public void Arming_a_brush_puts_the_eraser_down()
    {
        RunSta(() =>
        {
            var cat = Catalog();
            var canvas = new ShipCanvas();
            canvas.SetDocument(new ShipDocument(cat));
            canvas.SetEraseMode(true);
            Assert.True(canvas.EraseMode);

            canvas.SetArmed(cat.ByDefName["Bed"]);
            Assert.False(canvas.EraseMode);
        });
    }

    private static void RunSta(Action a)
    {
        Exception? err = null;
        var t = new Thread(() => { try { a(); } catch (Exception e) { err = e; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (err is not null) throw err;
    }
}
