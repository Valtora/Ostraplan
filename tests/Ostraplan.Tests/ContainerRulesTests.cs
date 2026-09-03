using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>What a container will hold, read rather than only applied (see <see cref="ContainerRules"/>).</summary>
public class ContainerRulesTests
{
    [Fact]
    public void A_container_naming_no_filter_holds_anything()
    {
        var cat = new Fixtures().Container("Crate", 4, 4).Build();
        var rules = ContainerRules.For(cat, cat.Lookup("Crate")!);

        Assert.True(rules.HoldsAnything);
        Assert.Null(rules.Root);
        Assert.Equal(rules.Offered, rules.Accepted);   // nothing is filtered out
    }

    [Fact]
    public void A_filter_naming_a_missing_trigger_holds_anything_rather_than_nothing()
    {
        // Permissive on unresolved, matching ContainerFilter. A mod that ships a container but not its filter
        // must not produce a box that refuses everything.
        var cat = new Fixtures().Container("Crate", 4, 4, filterCt: "TNotLoaded").Build();
        Assert.True(ContainerRules.For(cat, cat.Lookup("Crate")!).HoldsAnything);
    }

    [Fact]
    public void The_or_path_is_carried_through_rather_than_flattened()
    {
        // TIsFitContainerNavMod's shape: bAND false with two reqs, meaning EITHER. Read as an AND it would demand
        // both, which no item carries, so this is the field that must survive.
        var cat = new Fixtures()
            .Trig("TFitNavMod", reqs: ["IsNavMod", "IsExplosion"], bAnd: false)
            .Container("Console", 3, 3, filterCt: "TFitNavMod")
            .Build();

        var root = ContainerRules.For(cat, cat.Lookup("Console")!).Root!;

        Assert.False(root.All);
        Assert.Equal(["IsNavMod", "IsExplosion"], root.Requires.Select(r => r.Cond));
    }

    [Fact]
    public void A_nested_filter_comes_through_as_a_tree()
    {
        var cat = new Fixtures()
            .Trig("TSolid", reqs: ["IsSolid"], forbids: ["IsInstalled"])
            .Trig("TPocket", forbids: ["IsBackpack"], triggers: ["TSolid"])
            .Container("Pouch", 1, 1, filterCt: "TPocket")
            .Build();

        var root = ContainerRules.For(cat, cat.Lookup("Pouch")!).Root!;

        Assert.Equal(["IsBackpack"], root.Forbids.Select(f => f.Cond));
        var inner = Assert.Single(root.Nested);
        Assert.Equal("TSolid", inner.TriggerName);
        Assert.Equal(["IsSolid"], inner.Requires.Select(r => r.Cond));
        Assert.Equal(["IsInstalled"], inner.Forbids.Select(f => f.Cond));
    }

    [Fact]
    public void An_unresolved_nested_name_reads_as_a_plain_condition()
    {
        // The game's GetCondTrigger wraps a lone cond name into a one-req trigger, so this is data rather than a
        // fault, and CondEval already evaluates it that way.
        var cat = new Fixtures()
            .Trig("TFit", triggers: ["IsSolid"])
            .Container("Box", 2, 2, filterCt: "TFit")
            .Build();

        var root = ContainerRules.For(cat, cat.Lookup("Box")!).Root!;
        Assert.Empty(root.Nested);
        Assert.Equal(["IsSolid"], root.Unresolved.Select(u => u.Cond));
    }

    [Fact]
    public void A_randomised_or_ranked_trigger_says_which_branch_was_assumed()
    {
        var cat = new Fixtures()
            .Trig("TMaybe", reqs: ["IsSolid"], fChance: 0.5)
            .Container("Box", 2, 2, filterCt: "TMaybe")
            .Build();

        var note = Assert.Single(ContainerRules.For(cat, cat.Lookup("Box")!).Notes);
        Assert.Contains("TMaybe", note, System.StringComparison.Ordinal);
    }

    [Fact]
    public void The_accepted_count_is_the_add_pickers_own_pass()
    {
        var cat = new Fixtures()
            .Trig("TSolidOnly", reqs: ["IsSolid"])
            .Part("Rock", startingConds: ["IsSolid"])
            .Part("Puddle", startingConds: ["IsLiquid"])
            .Container("Box", 4, 4, filterCt: "TSolidOnly")
            .Build();

        var box = cat.Lookup("Box")!;
        var rules = ContainerRules.For(cat, box);

        Assert.Equal(ContainerFilter.AcceptedBy(cat, box).Count, rules.Accepted);
        Assert.True(rules.Accepted < rules.Offered);   // the puddle is refused
    }

    [Fact]
    public void The_summary_merges_every_forbid_in_the_tree_into_one_sentence()
    {
        // Safe to merge because a forbid is an and-of-nots on both of the game's paths, at every level.
        var cat = new Fixtures()
            .Trig("TSolid", forbids: ["IsInstalled", "IsOversized"])
            .Trig("TPocket", forbids: ["IsBackpack"], triggers: ["TSolid"])
            .Container("Pouch", 1, 1, filterCt: "TPocket")
            .Build();

        var line = ContainerRules.For(cat, cat.Lookup("Pouch")!).Plain.Single();

        Assert.Equal("Won't hold", line.Label);
        Assert.Equal("IsBackpack, IsInstalled or IsOversized", line.Text);
    }

    [Fact]
    public void The_summary_keeps_each_requirements_own_conjunction()
    {
        // Requirements do NOT merge: an OR node flattened into an AND list is the nav-console mistake, so each
        // node keeps a line of its own and spells out which it is.
        var cat = new Fixtures()
            .Trig("TEither", reqs: ["IsNavMod", "IsExplosion"], bAnd: false)
            .Trig("TBoth", reqs: ["IsSolid", "IsSmall"], triggers: ["TEither"])
            .Container("Box", 2, 2, filterCt: "TBoth")
            .Build();

        var lines = ContainerRules.For(cat, cat.Lookup("Box")!).Plain;

        Assert.Equal(["Must be", "Must be"], lines.Select(l => l.Label));
        Assert.Equal("IsSolid and IsSmall", lines[0].Text);
        Assert.Equal("IsNavMod or IsExplosion", lines[1].Text);
    }

    [Fact]
    public void A_container_that_bars_nothing_has_nothing_to_summarise()
    {
        var cat = new Fixtures().Container("Crate", 4, 4).Build();
        Assert.Empty(ContainerRules.For(cat, cat.Lookup("Crate")!).Plain);
    }

    [Fact]
    public void One_condition_reads_as_itself_rather_than_as_a_list()
    {
        var cat = new Fixtures()
            .Trig("TNoCrates", forbids: ["IsCrate"])
            .Container("Pouch", 1, 1, filterCt: "TNoCrates")
            .Build();

        Assert.Equal("IsCrate", ContainerRules.For(cat, cat.Lookup("Pouch")!).Plain.Single().Text);
    }

    [Fact]
    public void A_slot_is_described_from_the_items_that_declare_it()
    {
        var cat = new Fixtures()
            .Part("PocketA", container: (1, 1), slotKeys: ["pocket01"])
            .Part("PocketB", container: (1, 1), slotKeys: ["pocket01"])
            .Part("Battery", slotKeys: ["power01"])
            .Slot("pocket01", friendly: "Pocket")
            .Build();

        var rules = SlotRules.For(cat, "pocket01");

        Assert.Equal("Pocket", rules.Friendly);
        Assert.Equal(2, rules.Fits);
        Assert.Equal(["PocketA", "PocketB"], rules.Examples);
        Assert.Equal(1, SlotRules.For(cat, "power01").Fits);
        Assert.Equal(0, SlotRules.For(cat, "nothing_declares_this").Fits);
    }

    [Fact]
    public void The_simple_condition_table_is_chunked_seven_at_a_time()
    {
        // conditions_simple is one flat array rather than a list of objects, so the parse is a chunk and a
        // trailing partial row has to be dropped rather than half-read.
        var e = JsonDocument.Parse("""
        {
          "strName": "Simple Conditions",
          "aValues": [
            "IsBackpack", "Backpack", "[us] [is] a backpack.", "2", "2", "Neutral", "false",
            "IsCrate", "Crate", "[us] [is] a cargo crate.", "0", "0", "Neutral", "false",
            "IsTruncated", "Truncated"
          ]
        }
        """).RootElement;

        var conds = SimpleCondDef.ParseTable(e).ToList();

        Assert.Equal(2, conds.Count);                       // the partial third row is dropped
        Assert.Equal("Backpack", conds[0].Friendly);
        Assert.Equal("a backpack", conds[0].Plain);         // the [us] [is] grammar tokens come off the front
        Assert.Equal("a cargo crate", conds[1].Plain);
    }

    [SkippableFact]
    public void A_real_pouch_and_a_real_console_read_the_way_their_data_says()
    {
        var g = TestData.RequireGame();
        var pouch = g.Catalog.Lookup("PocketPouchSmall01");
        Skip.If(pouch is null, "the stock pouch is not in this install");

        var rules = ContainerRules.For(g.Catalog, pouch!);
        Assert.False(rules.HoldsAnything);
        Assert.Equal("TIsFitContainerPocket", rules.Root!.TriggerName);
        // It forbids a backpack and a crate outright, then nests two more triggers.
        Assert.Contains(rules.Root.Forbids, f => f.Cond == "IsBackpack");
        Assert.NotEmpty(rules.Root.Nested);
        // A pouch takes some of what Ostraplan can place, but nothing like all of it.
        Assert.InRange(rules.Accepted, 1, rules.Offered - 1);

        // Every condition it names has the game's own wording behind it, which is the whole point of reading
        // conditions_simple: a rule printed as raw tokens would be no better than the code that applies it.
        var named = rules.Root.Forbids.Concat(rules.Root.Requires).ToList();
        Assert.All(named, c => Assert.NotNull(c.Friendly));
    }

    [SkippableFact]
    public void No_equipment_slot_in_the_game_filters_anything()
    {
        // The finding behind SlotRules, held as a test so a game update that changes it is caught rather than
        // quietly making the panel wrong: every slot declaring strCTAutoSlot is a wound slot.
        var g = TestData.RequireGame();
        var suit = g.Catalog.Lookup("OutfitEVA01");
        Skip.If(suit is null, "the stock EVA suit is not in this install");

        foreach (var slot in suit!.SlotsWeHave)
        {
            var rules = SlotRules.For(g.Catalog, slot);
            Assert.True(rules.Fits > 0, $"nothing in the data declares {slot}");
            Assert.NotEmpty(rules.Examples);
        }
    }
}
