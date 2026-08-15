using System;
using System.IO;
using System.Linq;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The render order things sharing a tile are drawn in: the game's own per-def z-scale (<c>fZScale</c>), the terms
/// Ostraplan adds under it where two defs share one (canisters under what they feed, body bottom edge), and the
/// manual override (Move Back / Move Forward / Reset order). Mostly game-free — the synthetic catalog carries the
/// same z-scales the real defs declare — with install-gated checks against the real ones: a gas canister on a Miura
/// "Hydra" RCS regulator's gas-input tile, and the bin/rack and alarm/charger pairs from issue #28.
/// </summary>
public class ZOrderTests
{
    /// <summary>
    /// A catalog with the game's own vessel trigger, a deck, a big rig and a canister that sits on it. The z-scales
    /// are the real ones: deck 0.01, an RCS regulator 0.8, a canister 0.5, conduit 1.02. "Panel" shares the rig's
    /// 0.8 so the two land in one nudge pile, which is the only place a manual bias can still act.
    /// </summary>
    private static Catalog Cat() => new Fixtures()
        .Trig("TIsVessel", ["IsVessel01", "IsVesselH2", "IsVesselO2"], bAnd: false)
        .Floor()
        .Fixture("Rig", 3, 3, zScale: 0.8)
        .Fixture("Panel", zScale: 0.8)
        .Fixture("Pump", zScale: 0.5)          // shares the canister's z-scale: the game ties, the rank decides
        .Part("Can", tileConds: ["IsFixture"], startingConds: ["IsVessel01"], category: "HVAC", zScale: 0.5)
        .Part("Ration", tileConds: [], startingConds: [], category: "MISC")
        .Conduit()
        .Build();

    private static string[] Order(ShipDocument doc) => [.. doc.RenderOrder().Select(i => i.DefName)];

    [Fact]
    public void The_z_scale_decides_the_order_whichever_went_down_first()
    {
        var cat = Cat();
        foreach (var canisterFirst in new[] { true, false })
        {
            var doc = new ShipDocument(cat);
            if (canisterFirst) Fixtures.Place(doc, "Can", 2, 1);
            Fixtures.Place(doc, "Rig", 0, 0);
            if (!canisterFirst) Fixtures.Place(doc, "Can", 2, 1);

            // Both are fixtures on the same row, so a Y-sort ties and insertion order used to decide it: drop the
            // canister second and it sat on top of the rig (issue #28). The defs' own z-scales settle it now.
            Assert.Equal(["Can", "Rig"], Order(doc));
        }
    }

    [Fact]
    public void A_canister_draws_under_a_part_that_shares_its_z_scale()
    {
        var cat = Cat();
        foreach (var canisterFirst in new[] { true, false })
        {
            var doc = new ShipDocument(cat);
            if (canisterFirst) Fixtures.Place(doc, "Can", 0, 0);
            Fixtures.Place(doc, "Pump", 0, 0);
            if (!canisterFirst) Fixtures.Place(doc, "Can", 0, 0);

            // Same z-scale, so the game itself defines no order and the vessel rank is Ostraplan's answer.
            Assert.Equal(["Can", "Pump"], Order(doc));
        }
    }

    [Fact]
    public void A_smaller_body_inside_a_larger_one_draws_under_it()
    {
        var cat = Cat();
        var doc = new ShipDocument(cat);
        Fixtures.Place(doc, "Rig", 0, 0);      // rows 0..2, bottom edge 3
        Fixtures.Place(doc, "Panel", 1, 0);    // row 0 only, bottom edge 1 — inside the rig's body

        Assert.Equal(["Panel", "Rig"], Order(doc));   // same z-scale, so the bottom edge decides
    }

    [Fact]
    public void The_z_scale_outranks_everything_below_it()
    {
        var cat = Cat();
        var doc = new ShipDocument(cat);
        Fixtures.Place(doc, "Conduit", 0, 0);
        Fixtures.Place(doc, "Can", 0, 0);
        Fixtures.Place(doc, "Floor", 0, 0);

        // A canister ranks under the parts it shares a z-scale with, but never under a deck plate or over a
        // conduit run: those are decided a term higher up, by the game's own numbers.
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

        // the rig is already the top of its pile, and the deck under it has a different z-scale, so there is
        // nowhere further forward to go — the menu entry greys out rather than pushing a no-op undo step
        Assert.Empty(ZOrder.Nudge(doc, new RenderItem(rig, null), 1, 0, forward: true));
    }

    [Fact]
    public void A_nudge_never_reaches_past_the_defs_the_game_already_ordered()
    {
        var cat = Cat();
        var doc = new ShipDocument(cat);
        Fixtures.Place(doc, "Floor", 0, 0);
        var can = Fixtures.Place(doc, "Can", 0, 0);
        Fixtures.Place(doc, "Rig", 0, 0);

        // the canister is alone at its z-scale here: neither the deck under it nor the rig over it is in its
        // pile, so there is nothing to trade places with in either direction
        Assert.Empty(ZOrder.Nudge(doc, new RenderItem(can, null), 0, 0, forward: false));
        Assert.Empty(ZOrder.Nudge(doc, new RenderItem(can, null), 0, 0, forward: true));
        Assert.Equal(["Floor", "Can", "Rig"], Order(doc));
    }

    [Fact]
    public void Reset_clears_the_biases_a_nudge_wrote_across_the_pile()
    {
        var cat = Cat();
        var doc = new ShipDocument(cat);
        var rig = Fixtures.Place(doc, "Rig", 0, 0);
        var panel = Fixtures.Place(doc, "Panel", 1, 0);

        var stack = new CommandStack();
        stack.Push(doc, new SetZOrderCommand(ZOrder.Nudge(doc, new RenderItem(panel, null), 1, 0, forward: true), "f"));
        Assert.Equal(["Rig", "Panel"], Order(doc));
        Assert.True(rig.ZBias != 0 || panel.ZBias != 0);

        stack.Push(doc, new SetZOrderCommand(ZOrder.Reset(doc, new RenderItem(panel, null), 1, 0), "r"));
        Assert.Equal(0, rig.ZBias);
        Assert.Equal(0, panel.ZBias);
        Assert.Equal(["Panel", "Rig"], Order(doc));   // back under the automatic rule
    }

    [Fact]
    public void Repeated_nudges_do_not_drift_the_bias_values()
    {
        var cat = Cat();
        var doc = new ShipDocument(cat);
        var rig = Fixtures.Place(doc, "Rig", 0, 0);
        var panel = Fixtures.Place(doc, "Panel", 1, 0);

        for (var i = 0; i < 8; i++)
        {
            var forward = i % 2 == 0;
            var changes = ZOrder.Nudge(doc, new RenderItem(panel, null), 1, 0, forward);
            new SetZOrderCommand(changes, "n").Do(doc);
        }
        // renumbering from the pile's own floor keeps a shuffled pile in {0, 1} rather than walking off
        Assert.InRange(panel.ZBias, 0, 1);
        Assert.InRange(rig.ZBias, 0, 1);
    }

    [Fact]
    public void An_oplan_restores_the_manual_order_and_omits_it_when_automatic()
    {
        var cat = Cat();
        // Panel would draw under the rig automatically (smaller body, same z-scale); the persisted bias flips it.
        var file = new OplanFile
        {
            Parts = [new OplanPart { Def = "Panel", X = 1, Y = 0, Z = 1 }, new OplanPart { Def = "Rig", X = 0, Y = 0 }],
            LooseObjects = [new OplanLoose { Def = "Ration", X = 0, Y = 0, Qty = 1, Z = -2 }],
        };
        var tmp = Path.Combine(Path.GetTempPath(), $"ostraplan-test-{Guid.NewGuid():N}.oplan");
        try
        {
            file.Save(tmp);
            Assert.DoesNotContain("\"z\": 0", File.ReadAllText(tmp));   // an automatic part writes no bias at all

            var (doc, missing) = OplanFile.Load(tmp).ToDocument(cat);
            Assert.Empty(missing);
            Assert.Equal(1, doc.Placements.Single(p => p.DefName == "Panel").ZBias);
            Assert.Equal(0, doc.Placements.Single(p => p.DefName == "Rig").ZBias);
            Assert.Equal(-2, doc.LooseAt(0, 0)!.ZBias);
            Assert.Equal(["Rig", "Panel", "Ration"], Order(doc));   // the persisted bias is what draws
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

    /// <summary>
    /// Issue #28's two pairs, against the real defs. Both used to fall through every term to insertion order, so
    /// which one drew on top followed the order the save happened to list them in, and two identical spots on one
    /// ship could disagree. The game answers both through <c>fZScale</c>: a bulkhead bin is 1.01 against a rack's
    /// default 1.0, and an atmosphere alarm is 0.75 against an EVA charger's 0.2.
    /// </summary>
    [SkippableTheory]
    [InlineData("ItmRack1x101", "ItmStorageBin1x101")]
    [InlineData("ItmChargerBattEVA", "ItmAlarmO2Off")]
    public void The_reported_pairs_draw_in_the_games_order_either_way_round(string under, string over)
    {
        var g = TestData.RequireGame();
        Skip.If(g.Catalog.Lookup(under) is null || g.Catalog.Lookup(over) is null, "defs not in this build");

        foreach (var overFirst in new[] { true, false })
        {
            var doc = new ShipDocument(g.Catalog);
            if (overFirst) Fixtures.Place(doc, over, 0, 0);
            Fixtures.Place(doc, under, 0, 0);
            if (!overFirst) Fixtures.Place(doc, over, 0, 0);

            Assert.Equal([under, over], doc.RenderOrder().Select(i => i.DefName));
        }
    }
}
