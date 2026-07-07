using System.Collections.Generic;
using System.Linq;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>The cargo add/remove editor (<see cref="CargoEdit"/>) — pure, install-free: stacking, auto-placement,
/// the capacity block ("the Law" for cargo), and removal (one, whole, cascade).</summary>
public class CargoEditTests
{
    private static PartDef Item(string name, int stackLimit = 1, int w = 1, int h = 1) =>
        new(name, name + " (friendly)", "MISC", "test",
            new ItemDef(name, "", false, null, 0, 1, [], [], []),
            "sprite.png", [], [], [], new Dictionary<string, double>(), new Dictionary<string, (double, double)>())
        { StackLimit = stackLimit, InvSize = (w, h) };

    [Fact]
    public void Add_places_a_single_authored_item_into_an_empty_container()
    {
        var result = CargoEdit.Add([], null, (6, 6), Item("ItmScrap"), 1);
        var it = Assert.Single(result!);
        Assert.True(it.Authored);
        Assert.Equal("ItmScrap", it.DefName);
        Assert.Equal(1, it.Stack);
        Assert.False(it.IsStack);
    }

    [Fact]
    public void Add_stacks_identical_stackable_items_into_one_tile()
    {
        var result = CargoEdit.Add([], null, (6, 6), Item("ItmAmmo", stackLimit: 20), 5);
        var it = Assert.Single(result!);        // one tile...
        Assert.True(it.IsStack);
        Assert.Equal(5, it.Stack);              // ...holding five
        Assert.Equal(4, it.Children.Count);     // a lead + four members
        Assert.All(it.Children, m => Assert.True(m.Authored));
    }

    [Fact]
    public void Add_splits_a_quantity_over_the_stack_limit_into_multiple_stacks()
    {
        var result = CargoEdit.Add([], null, (6, 6), Item("ItmAmmo", stackLimit: 10), 25);
        Assert.Equal(3, result!.Count);
        Assert.Equal([10, 10, 5], result.Select(c => c.Stack).OrderByDescending(x => x).ToArray());
    }

    [Fact]
    public void Add_nonstackable_items_take_separate_cells()
    {
        var result = CargoEdit.Add([], null, (6, 6), Item("ItmTool"), 3);
        Assert.Equal(3, result!.Count);
        Assert.All(result, c => Assert.False(c.IsStack));
        Assert.Equal(3, result.Select(c => (c.GridX, c.GridY)).Distinct().Count());   // distinct cells
    }

    [Fact]
    public void Add_tops_up_an_existing_stack_before_taking_a_new_cell()
    {
        var def = Item("ItmAmmo", stackLimit: 10);
        var first = CargoEdit.Add([], null, (6, 6), def, 6)!;
        var second = CargoEdit.Add(first, null, (6, 6), def, 3)!;
        var it = Assert.Single(second);   // still one tile
        Assert.Equal(9, it.Stack);        // 6 + 3
    }

    [Fact]
    public void Add_returns_null_when_the_grid_is_full()
    {
        var def = Item("ItmTool");        // 1x1, non-stackable
        var full = CargoEdit.Add([], null, (2, 1), def, 2)!;   // fills the 2-cell grid
        Assert.Null(CargoEdit.Add(full, null, (2, 1), def, 1));   // no room for a third — capacity ("the Law")
    }

    [Fact]
    public void Add_targets_a_nested_container_by_id_and_preserves_its_identity()
    {
        var box = new CargoItem("box", "ItmCrate", "Crate", Slotted: false, []) { GridW = 1, GridH = 1 };
        var result = CargoEdit.Add([box], "box", (3, 3), Item("ItmScrap"), 1)!;
        var outer = Assert.Single(result);
        Assert.Equal("box", outer.StrID);        // the container node keeps its identity
        var inner = Assert.Single(outer.Children);
        Assert.True(inner.Authored);
    }

    [Fact]
    public void RemoveOne_reduces_a_stack_then_collapses_to_a_single()
    {
        var def = Item("ItmAmmo", stackLimit: 10);
        var two = CargoEdit.Add([], null, (6, 6), def, 2)!;
        var one = CargoEdit.RemoveOne(two, two.Single().StrID);
        var it = Assert.Single(one);
        Assert.Equal(1, it.Stack);
        Assert.False(it.IsStack);
        Assert.Empty(it.Children);
    }

    [Fact]
    public void RemoveOne_removes_a_lone_item_outright()
    {
        var it = new CargoItem("x", "ItmScrap", "Scrap", Slotted: false, []);
        Assert.Empty(CargoEdit.RemoveOne([it], "x"));
    }

    [Fact]
    public void RemoveWhole_removes_a_container_and_its_contents()
    {
        var inner = new CargoItem("inner", "ItmScrap", "Scrap", Slotted: false, []) { Authored = true };
        var box = new CargoItem("box", "ItmCrate", "Crate", Slotted: false, [inner]);
        Assert.Empty(CargoEdit.RemoveWhole([box], "box"));   // box + its contents leave the tree together
    }

    // ---- move / rotate ----

    private static CargoItem Cargo(string id, int x = 0, int y = 0, int w = 1, int h = 1) =>
        new(id, "Itm" + id, id, Slotted: false, []) { GridX = x, GridY = y, GridW = w, GridH = h };

    [Fact]
    public void Move_relocates_an_item_to_a_free_cell()
    {
        var result = CargoEdit.Move([Cargo("a")], "a", null, (6, 6), 3, 2);
        var moved = Assert.Single(result!);
        Assert.Equal("a", moved.StrID);
        Assert.Equal((3, 2), (moved.GridX, moved.GridY));
    }

    [Fact]
    public void Move_returns_null_when_the_target_cell_is_occupied()
    {
        // b materializes at its stored (1,0); moving a onto it collides -> snap back (null)
        Assert.Null(CargoEdit.Move([Cargo("a", 0, 0), Cargo("b", 1, 0)], "a", null, (6, 6), 1, 0));
    }

    [Fact]
    public void Move_returns_null_when_out_of_bounds()
    {
        // a 2-wide item can't sit at x=1 in a 2-wide grid (would overflow the right edge)
        Assert.Null(CargoEdit.Move([Cargo("a", w: 2)], "a", null, (2, 2), 1, 0));
    }

    [Fact]
    public void Move_between_containers_reparents_the_item()
    {
        var box = new CargoItem("box", "ItmCrate", "Crate", Slotted: false, []);
        var result = CargoEdit.Move([box, Cargo("a", 1, 0)], "a", "box", (3, 3), 0, 0);
        Assert.NotNull(result);
        var outerBox = result!.Single(c => c.StrID == "box");
        Assert.Single(outerBox.Children);
        Assert.Equal("a", outerBox.Children[0].StrID);          // a is now inside box...
        Assert.DoesNotContain(result, c => c.StrID == "a");     // ...and no longer a root item
    }

    [Fact]
    public void Rotate_swaps_the_footprint()
    {
        var result = CargoEdit.Rotate([Cargo("a", w: 2, h: 1)], "a", null, (6, 6));
        var rot = Assert.Single(result!);
        Assert.Equal(90, rot.GridRot);
        Assert.Equal(1, rot.EffW);   // a 2×1 becomes 1×2 at 90°
        Assert.Equal(2, rot.EffH);
    }

    [Fact]
    public void Rotate_returns_null_when_the_rotated_footprint_wont_fit()
    {
        // a 3×1 in a 3×1 grid: rotating to 1×3 exceeds the one-tall grid -> reject
        Assert.Null(CargoEdit.Rotate([Cargo("a", w: 3, h: 1)], "a", null, (3, 1)));
    }
}
