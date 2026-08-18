using System.Collections.Generic;
using System.Linq;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>The cargo add/remove editor (<see cref="CargoEdit"/>) — pure, install-free: stacking, auto-placement,
/// the capacity block ("the Law" for cargo), and removal (one, whole, cascade).</summary>
public class CargoEditTests
{
    private static PartDef Item(string name, int stackLimit = 1, int w = 1, int h = 1, bool sheet = false) =>
        new(name, name + " (friendly)", "MISC", "test",
            new ItemDef(name, "", sheet, null, 0, 1, [], [], []),
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
    public void MaxAddable_counts_free_cells_times_the_stack_limit()
    {
        // a 2×2 grid, stack limit 10 → 4 cells × 10 = 40 into an empty container
        Assert.Equal(40, CargoEdit.MaxAddable([], null, (2, 2), Item("ItmAmmo", stackLimit: 10)));
    }

    [Fact]
    public void MaxAddable_accounts_for_existing_contents_including_stack_top_up()
    {
        var def = Item("ItmAmmo", stackLimit: 10);
        var used = CargoEdit.Add([], null, (2, 2), def, 6)!;   // one cell holds 6/10; three cells free
        // 4 to top up the partial stack + 3 free cells × 10 = 34
        Assert.Equal(34, CargoEdit.MaxAddable(used, null, (2, 2), def));
    }

    [Fact]
    public void MaxAddable_is_zero_for_a_non_stacking_item_in_a_full_grid()
    {
        var def = Item("ItmTool");                              // 1×1, non-stackable
        var full = CargoEdit.Add([], null, (2, 1), def, 2)!;    // both cells taken
        Assert.Equal(0, CargoEdit.MaxAddable(full, null, (2, 1), def));
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

    [Fact]
    public void Rotate_moves_the_item_when_its_own_cell_cannot_take_the_swap()
    {
        // a 1×3 in the LAST column of a 3-wide grid: swapped to 3×1 it would run off the right edge, so anchoring
        // the top-left would refuse a turn there is plainly room for. It slides to a cell that takes it instead.
        var result = CargoEdit.Rotate([Cargo("a", x: 2, y: 0, w: 1, h: 3)], "a", null, (3, 5));
        var rot = Assert.Single(result!);
        Assert.Equal(90, rot.GridRot);
        Assert.Equal((3, 1), (rot.EffW, rot.EffH));
        Assert.True(rot.GridX + rot.EffW <= 3 && rot.GridY + rot.EffH <= 5);
    }

    [Fact]
    public void Rotate_keeps_the_footprint_centred_where_it_was()
    {
        // a 1×3 at (1,1) spans rows 1-3 about a centre at row 2; flat, that centre puts it at (0,2)
        var result = CargoEdit.Rotate([Cargo("a", x: 1, y: 1, w: 1, h: 3)], "a", null, (3, 5));
        var rot = Assert.Single(result!);
        Assert.Equal((0, 2), (rot.GridX, rot.GridY));
    }

    [Fact]
    public void Rotate_refuses_a_sheet_item()
    {
        // walls and floors never turn: the game's Item.RotateCW returns immediately for bHasSpriteSheet, so a
        // rotation authored for one would not survive a load
        var wallDef = Item("ItmWall", w: 2, h: 1, sheet: true);
        var catalog = new Catalog
        {
            Parts = [wallDef],
            ByDefName = new Dictionary<string, PartDef> { ["ItmWall"] = wallDef },
            Loots = new Dictionary<string, LootDef>(),
            Triggers = new Dictionary<string, CondTriggerDef>(),
            Warnings = [],
        };
        var wall = new CargoItem("a", "ItmWall", "Wall", Slotted: false, []) { GridW = 2, GridH = 1 };
        Assert.Null(CargoEdit.Rotate([wall], "a", null, (6, 6), catalog));
        Assert.NotNull(CargoEdit.Rotate([wall], "a", null, (6, 6)));   // geometry alone still allows it
    }

    [Fact]
    public void Move_can_set_the_rotation_the_item_lands_in()
    {
        // a drag turned in hand commits pose and rotation together: a 1×3 laid flat fits a 3-wide grid at row 4
        var result = CargoEdit.Move([Cargo("a", w: 1, h: 3)], "a", null, (3, 5), 0, 4, rot: 90);
        var moved = Assert.Single(result!);
        Assert.Equal(90, moved.GridRot);
        Assert.Equal((0, 4), (moved.GridX, moved.GridY));
    }

    [Fact]
    public void Move_checks_the_fit_against_the_rotation_it_would_land_in()
    {
        // upright, the same 1×3 would need rows 4-6 of a 5-tall grid
        Assert.Null(CargoEdit.Move([Cargo("a", w: 1, h: 3)], "a", null, (3, 5), 0, 4));
    }

    // ---- rotation-aware capacity ----

    [Fact]
    public void Add_lays_an_item_on_its_side_once_it_no_longer_fits_upright()
    {
        // the reported case: a 3×5 Polaris decoy launcher takes five 1×3 decoy missiles, three upright and two
        // flat across the band left over, not the three an upright-only packer would allow.
        var missile = Item("ItmAmmoDecoyMissile01", w: 1, h: 3);
        var result = CargoEdit.Add([], null, (3, 5), missile, 5);
        Assert.NotNull(result);
        Assert.Equal(5, result!.Count);
        Assert.Equal(3, result.Count(c => c.GridRot == 0));
        Assert.Equal(2, result.Count(c => c.GridRot == 90));
    }

    [Fact]
    public void MaxAddable_counts_the_rotated_orientation()
    {
        Assert.Equal(5, CargoEdit.MaxAddable([], null, (3, 5), Item("ItmAmmoDecoyMissile01", w: 1, h: 3)));
    }

    [Fact]
    public void MaxAddable_stops_at_the_upright_count_for_an_item_that_cannot_turn()
    {
        // a sheet item never rotates, so the same grid holds only the three that fit upright
        Assert.Equal(3, CargoEdit.MaxAddable([], null, (3, 5), Item("ItmWallPanel", w: 1, h: 3, sheet: true)));
    }

    [Fact]
    public void Add_tops_up_a_rotated_container_to_capacity_from_a_partial_fill()
    {
        var missile = Item("ItmAmmoDecoyMissile01", w: 1, h: 3);
        var three = CargoEdit.Add([], null, (3, 5), missile, 3)!;
        Assert.Equal(2, CargoEdit.MaxAddable(three, null, (3, 5), missile));
        Assert.NotNull(CargoEdit.Add(three, null, (3, 5), missile, 2));
        Assert.Null(CargoEdit.Add(three, null, (3, 5), missile, 3));   // and no more than that
    }
}
