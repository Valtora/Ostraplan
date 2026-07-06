using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>The cargo tree builder (<see cref="Cargo.BuildForest"/>) — pure, install-free.</summary>
public class CargoTests
{
    private static Catalog EmptyCatalog() => new()
    {
        Parts = [],
        ByDefName = new Dictionary<string, PartDef>(),
        Loots = new Dictionary<string, LootDef>(),
        Triggers = new Dictionary<string, CondTriggerDef>(),
        Warnings = [],
    };

    private static readonly Dictionary<string, JsonNode> NoCos = new();

    [Fact]
    public void BuildForest_builds_a_nested_tree_marks_slots_and_guards_cycles()
    {
        // A holds B (loose) and C (slotted); B holds D (loose); D points back to B — a cycle that must be guarded.
        var children = new Dictionary<string, List<string>>
        {
            ["A"] = ["B", "C"],
            ["B"] = ["D"],
            ["D"] = ["B"],
        };
        JsonNode Item(string id, bool slot = false) => new JsonObject
        {
            ["strID"] = id,
            ["strName"] = "Itm" + id,
            [slot ? "strSlotParentID" : "strParentID"] = "parent",
        };
        var itemsById = new Dictionary<string, JsonNode>
        {
            ["B"] = Item("B"), ["C"] = Item("C", slot: true), ["D"] = Item("D"),
        };

        var forest = Cargo.BuildForest("A", children, itemsById, NoCos, EmptyCatalog());

        Assert.Equal(2, forest.Count);                                  // B and C directly under A
        var b = forest.Single(c => c.StrID == "B");
        Assert.Equal("ItmB", b.DefName);
        Assert.False(b.Slotted);                                        // strParentID -> loose cargo
        var d = Assert.Single(b.Children);                              // D nested under B
        Assert.Equal("D", d.StrID);
        Assert.Empty(d.Children);                                       // D->B is a cycle, guarded (B already seen)
        Assert.True(forest.Single(c => c.StrID == "C").Slotted);        // strSlotParentID -> slotted
        Assert.Equal(3, forest.Sum(c => c.SubtreeCount));              // B + D + C
        Assert.Equal(new[] { "B", "C", "D" }, forest.SelectMany(c => c.SubtreeIds()).OrderBy(x => x));
    }

    [Fact]
    public void BuildForest_reads_grid_position_from_the_co_and_a_lone_item_is_not_a_stack()
    {
        var children = new Dictionary<string, List<string>> { ["A"] = ["B"] };
        var itemsById = new Dictionary<string, JsonNode>
        {
            ["B"] = new JsonObject { ["strID"] = "B", ["strName"] = "ItmB", ["strParentID"] = "A" },
        };
        var cosById = new Dictionary<string, JsonNode>
        {
            ["B"] = new JsonObject
            {
                ["strID"] = "B",
                ["inventoryX"] = 2,
                ["inventoryY"] = 3,
                ["aConds"] = new JsonArray("IsStacking=1.0x4"),   // capacity, NOT count — must be ignored
            },
        };

        var b = Assert.Single(Cargo.BuildForest("A", children, itemsById, cosById, EmptyCatalog()));
        Assert.Equal(2, b.GridX);
        Assert.Equal(3, b.GridY);
        Assert.Equal(1, b.Stack);       // no same-def members -> a single item, despite IsStacking=4
        Assert.False(b.IsStack);
        Assert.Equal(1, b.GridW);       // no catalog def -> 1x1 footprint
        Assert.Equal(1, b.GridH);
    }

    [Fact]
    public void BuildForest_collapses_same_def_children_into_a_stack()
    {
        // The game stores a stack as a lead item plus its copies as same-def children (StackCount = members + 1).
        // A container (different def) holding those items is NOT a stack.
        var children = new Dictionary<string, List<string>>
        {
            ["Crate"] = ["lead"],
            ["lead"] = ["m1", "m2"],   // two stacked copies of the lead
        };
        JsonNode Ammo(string id) => new JsonObject { ["strID"] = id, ["strName"] = "ItmAmmo9mm", ["strParentID"] = "p" };
        var itemsById = new Dictionary<string, JsonNode>
        {
            ["lead"] = Ammo("lead"), ["m1"] = Ammo("m1"), ["m2"] = Ammo("m2"),
        };

        var forest = Cargo.BuildForest("Crate", children, itemsById, NoCos, EmptyCatalog());
        var lead = Assert.Single(forest);
        Assert.True(lead.IsStack);
        Assert.Equal(3, lead.Stack);            // 2 members + 1
        Assert.Equal(2, lead.Children.Count);   // members retained (for preservation / splitting later)
    }

    [Fact]
    public void BuildForest_does_not_treat_a_container_of_same_def_items_as_a_stack()
    {
        // A crate (IsContainer) holding two crates is NOT a stack — it stays a drillable container.
        var item = new ItemDef("Crate", "", false, null, 0, 1, [], [], []);
        var crateDef = new PartDef("Crate", "Crate", "MISC", "test", item, null, [], [], [],
            new Dictionary<string, double>(), new Dictionary<string, (double, double)>()) { ContainerGrid = (3, 3) };
        var catalog = new Catalog
        {
            Parts = [],
            ByDefName = new Dictionary<string, PartDef> { ["Crate"] = crateDef },
            Loots = new Dictionary<string, LootDef>(),
            Triggers = new Dictionary<string, CondTriggerDef>(),
            Warnings = [],
        };
        var children = new Dictionary<string, List<string>> { ["outer"] = ["c1", "c2"] };
        JsonNode Crate(string id) => new JsonObject { ["strID"] = id, ["strName"] = "Crate", ["strParentID"] = "outer" };
        var itemsById = new Dictionary<string, JsonNode> { ["c1"] = Crate("c1"), ["c2"] = Crate("c2") };

        var forest = Cargo.BuildForest("outer", children, itemsById, NoCos, catalog);
        Assert.Equal(2, forest.Count);                        // two crates, not collapsed
        Assert.All(forest, c => Assert.False(c.IsStack));     // a container isn't a stack
    }

    [Fact]
    public void BuildForest_is_empty_for_a_part_with_no_children()
    {
        var forest = Cargo.BuildForest(
            "X", new Dictionary<string, List<string>>(), new Dictionary<string, JsonNode>(), NoCos, EmptyCatalog());
        Assert.Empty(forest);
    }
}
