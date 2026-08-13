using System;
using System.IO;
using System.Linq;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The render order things sharing a tile are drawn in: the automatic part (render layer, canisters under what
/// they feed, body bottom edge) and the manual override (Move Back / Move Forward / Reset order). Mostly game-free
/// — the rule reads conditions, which the synthetic catalog carries — with one install-gated check against the
/// real defs that started this: a gas canister on a Miura "Hydra" RCS regulator's gas-input tile.
/// </summary>
public class ZOrderTests
{
    /// <summary>A catalog with the game's own vessel trigger, a deck, a big rig and a canister that sits on it.</summary>
    private static Catalog Cat() => new Fixtures()
        .Trig("TIsVessel", ["IsVessel01", "IsVesselH2", "IsVesselO2"], bAnd: false)
        .Floor()
        .Fixture("Rig", 3, 3)
        .Fixture("Panel")
        .Part("Can", tileConds: ["IsFixture"], startingConds: ["IsVessel01"], category: "HVAC")
        .Part("Ration", tileConds: [], startingConds: [], category: "MISC")
        .Conduit()
        .Build();

    private static string[] Order(ShipDocument doc) => [.. doc.RenderOrder().Select(i => i.DefName)];

    [Fact]
    public void A_canister_draws_under_the_part_it_feeds_whichever_went_down_first()
    {
        var cat = Cat();
        foreach (var canisterFirst in new[] { true, false })
        {
            var doc = new ShipDocument(cat);
            if (canisterFirst) Fixtures.Place(doc, "Can", 2, 1);
            Fixtures.Place(doc, "Rig", 0, 0);
            if (!canisterFirst) Fixtures.Place(doc, "Can", 2, 1);

            // Both are fixtures on the same row, so every other term ties and insertion order used to decide it:
            // drop the canister second and it sat on top of the rig (the reported bug).
            Assert.Equal(["Can", "Rig"], Order(doc));
        }
    }

    [Fact]
    public void A_smaller_body_inside_a_larger_one_draws_under_it()
    {
        var cat = Cat();
        var doc = new ShipDocument(cat);
        Fixtures.Place(doc, "Rig", 0, 0);      // rows 0..2, bottom edge 3
        Fixtures.Place(doc, "Panel", 1, 0);    // row 0 only, bottom edge 1 — inside the rig's body

        Assert.Equal(["Panel", "Rig"], Order(doc));
    }

    [Fact]
    public void The_render_layer_outranks_everything_below_it()
    {
        var cat = Cat();
        var doc = new ShipDocument(cat);
        Fixtures.Place(doc, "Conduit", 0, 0);
        Fixtures.Place(doc, "Can", 0, 0);
        Fixtures.Place(doc, "Floor", 0, 0);

        // A canister ranks under other fixtures, but never under a deck plate or over a conduit run.
        Assert.Equal(["Floor", "Can", "Conduit"], Order(doc));
    }

    [Fact]
    public void Loose_clutter_draws_over_parts_but_a_loose_canister_draws_under_them()
    {
        var cat = Cat();
        var doc = new ShipDocument(cat);
        Fixtures.Place(doc, "Floor", 0, 0);
        Fixtures.Place(doc, "Rig", 0, 0);
        new PlaceLooseCommand(new LooseObject { DefName = "Ration", X = 0, Y = 0 }).Do(doc);
        Assert.Equal(["Floor", "Rig", "Ration"], Order(doc));

        new RemoveLooseCommand(doc.LooseAt(0, 0)!).Do(doc);
        new PlaceLooseCommand(new LooseObject { DefName = "Can", X = 0, Y = 0 }).Do(doc);
        Assert.Equal(["Floor", "Can", "Rig"], Order(doc));   // a canister is a canister, installed or dropped
    }

    [Fact]
    public void The_click_stack_is_the_draw_order_reversed_and_includes_loose_items()
    {
        var cat = Cat();
        var doc = new ShipDocument(cat);
        Fixtures.Place(doc, "Floor", 0, 0);
        Fixtures.Place(doc, "Rig", 0, 0);
        new PlaceLooseCommand(new LooseObject { DefName = "Ration", X = 0, Y = 0 }).Do(doc);

        Assert.Equal(["Ration", "Rig", "Floor"], doc.RenderStackAt(0, 0).Select(i => i.DefName));
        Assert.True(doc.RenderStackAt(0, 0)[0].IsLoose);
    }

    [Fact]
    public void A_nudge_moves_one_step_and_undoes_cleanly()
    {
        var cat = Cat();
        var doc = new ShipDocument(cat);
        var rig = Fixtures.Place(doc, "Rig", 0, 0);
        var panel = Fixtures.Place(doc, "Panel", 1, 0);
        Assert.Equal(["Panel", "Rig"], Order(doc));

        var stack = new CommandStack();
        var changes = ZOrder.Nudge(doc, new RenderItem(panel, null), 1, 0, forward: true);
        Assert.NotEmpty(changes);
        stack.Push(doc, new SetZOrderCommand(changes, "Move forward"));
        Assert.Equal(["Rig", "Panel"], Order(doc));

        stack.Undo(doc);
        Assert.Equal(["Panel", "Rig"], Order(doc));
        stack.Redo(doc);
        Assert.Equal(["Rig", "Panel"], Order(doc));
    }

    [Fact]
    public void A_nudge_at_the_end_of_the_pile_changes_nothing()
    {
        var cat = Cat();
        var doc = new ShipDocument(cat);
        var rig = Fixtures.Place(doc, "Rig", 0, 0);
        Fixtures.Place(doc, "Panel", 1, 0);

        // the rig is already the top of its pile, and the deck under it is a different layer, so there is nowhere
        // further forward to go — the menu entry greys out rather than pushing a no-op undo step
        Assert.Empty(ZOrder.Nudge(doc, new RenderItem(rig, null), 1, 0, forward: true));
    }

    [Fact]
    public void A_nudge_never_pushes_a_fixture_under_the_deck()
    {
        var cat = Cat();
        var doc = new ShipDocument(cat);
        Fixtures.Place(doc, "Floor", 0, 0);
        var can = Fixtures.Place(doc, "Can", 0, 0);
        var rig = Fixtures.Place(doc, "Rig", 0, 0);

        // the canister is the bottom of the FIXTURE pile; the floor is not in that pile at all
        Assert.Empty(ZOrder.Nudge(doc, new RenderItem(can, null), 0, 0, forward: false));
        Assert.Equal(["Floor", "Can", "Rig"], Order(doc));
    }

    [Fact]
    public void Reset_clears_the_biases_a_nudge_wrote_across_the_pile()
    {
        var cat = Cat();
        var doc = new ShipDocument(cat);
        var rig = Fixtures.Place(doc, "Rig", 0, 0);
        var can = Fixtures.Place(doc, "Can", 2, 1);

        var stack = new CommandStack();
        stack.Push(doc, new SetZOrderCommand(ZOrder.Nudge(doc, new RenderItem(can, null), 2, 1, forward: true), "f"));
        Assert.Equal(["Rig", "Can"], Order(doc));
        Assert.True(rig.ZBias != 0 || can.ZBias != 0);

        stack.Push(doc, new SetZOrderCommand(ZOrder.Reset(doc, new RenderItem(can, null), 2, 1), "r"));
        Assert.Equal(0, rig.ZBias);
        Assert.Equal(0, can.ZBias);
        Assert.Equal(["Can", "Rig"], Order(doc));   // back under the automatic rule
    }

    [Fact]
    public void Repeated_nudges_do_not_drift_the_bias_values()
    {
        var cat = Cat();
        var doc = new ShipDocument(cat);
        var rig = Fixtures.Place(doc, "Rig", 0, 0);
        var can = Fixtures.Place(doc, "Can", 2, 1);

        for (var i = 0; i < 8; i++)
        {
            var forward = i % 2 == 0;
            var changes = ZOrder.Nudge(doc, new RenderItem(can, null), 2, 1, forward);
            new SetZOrderCommand(changes, "n").Do(doc);
        }
        // renumbering from the pile's own floor keeps a shuffled pile in {0, 1} rather than walking off
        Assert.InRange(can.ZBias, 0, 1);
        Assert.InRange(rig.ZBias, 0, 1);
    }

    [Fact]
    public void An_oplan_restores_the_manual_order_and_omits_it_when_automatic()
    {
        var cat = Cat();
        var file = new OplanFile
        {
            Parts = [new OplanPart { Def = "Rig", X = 0, Y = 0, Z = 1 }, new OplanPart { Def = "Can", X = 2, Y = 1 }],
            LooseObjects = [new OplanLoose { Def = "Ration", X = 0, Y = 0, Qty = 1, Z = -2 }],
        };
        var tmp = Path.Combine(Path.GetTempPath(), $"ostraplan-test-{Guid.NewGuid():N}.oplan");
        try
        {
            file.Save(tmp);
            Assert.DoesNotContain("\"z\": 0", File.ReadAllText(tmp));   // an automatic part writes no bias at all

            var (doc, missing) = OplanFile.Load(tmp).ToDocument(cat);
            Assert.Empty(missing);
            Assert.Equal(1, doc.Placements.Single(p => p.DefName == "Rig").ZBias);
            Assert.Equal(0, doc.Placements.Single(p => p.DefName == "Can").ZBias);
            Assert.Equal(-2, doc.LooseAt(0, 0)!.ZBias);
            Assert.Equal(["Ration", "Can", "Rig"], Order(doc));   // the persisted bias is what draws
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void An_uninstall_carries_the_manual_order_across()
    {
        var cat = Cat();
        var doc = new ShipDocument(cat);
        var can = Fixtures.Place(doc, "Can", 0, 0);
        can.ZBias = 3;

        Assert.Equal(3, can.Restate("Can", 0).ZBias);
    }

    [SkippableFact]
    public void The_real_gas_canister_draws_under_the_real_Hydra_regulator()
    {
        var g = TestData.RequireGame();
        const string hydra = "ItmRCSDistro01";      // RCS Intake Regulator: Miura "Hydra", 3x3
        const string canister = "ItmCanister01";    // the gas canister that feeds it, 1x1
        Skip.If(g.Catalog.Lookup(hydra) is null || g.Catalog.Lookup(canister) is null, "defs not in this build");

        var doc = new ShipDocument(g.Catalog);
        Fixtures.Place(doc, hydra, 0, 0);
        Fixtures.Place(doc, canister, 2, 1);   // the GasInput03 tile, on the regulator's own row

        Assert.True(g.Catalog.IsVessel(g.Catalog.Lookup(canister)));
        Assert.Equal([canister, hydra], doc.RenderOrder().Select(i => i.DefName));
    }
}
