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

        var forest = Cargo.BuildForest("A", children, itemsById, EmptyCatalog());

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
    public void BuildForest_is_empty_for_a_part_with_no_children()
    {
        var forest = Cargo.BuildForest(
            "X", new Dictionary<string, List<string>>(), new Dictionary<string, JsonNode>(), EmptyCatalog());
        Assert.Empty(forest);
    }
}
