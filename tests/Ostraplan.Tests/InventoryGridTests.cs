using System.Collections.Generic;
using System.Linq;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>The inventory grid packer (<see cref="InventoryGrid.Pack"/>) — pure, install-free. Mirrors the game's
/// honour-stored-cell-then-nearest-free fill (<c>GUIInventoryItem.AddToWindow</c>).</summary>
public class InventoryGridTests
{
    private static CargoItem Item(string def, int x, int y, int w = 1, int h = 1, int stack = 1) =>
        new(def + "#" + x + "," + y + (stack > 1 ? "s" + stack : ""), def, def, false, [])
        { GridX = x, GridY = y, GridW = w, GridH = h, Stack = stack };

    private static bool Overlaps(PackedItem a, PackedItem b) =>
        a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H;

    private static void AssertNoOverlaps(GridLayoutResult r)
    {
        for (var i = 0; i < r.Items.Count; i++)
            for (var j = i + 1; j < r.Items.Count; j++)
                Assert.False(Overlaps(r.Items[i], r.Items[j]), $"{r.Items[i].Item.DefName} overlaps {r.Items[j].Item.DefName}");
    }

    [Fact]
    public void Honours_distinct_stored_positions()
    {
        // the real backpack case: two different items at (0,0) and (1,0) stay put
        var r = InventoryGrid.Pack(4, 4, [Item("A", 0, 0), Item("B", 1, 0)]);
        var a = r.Items.Single(p => p.Item.DefName == "A");
        var b = r.Items.Single(p => p.Item.DefName == "B");
        Assert.Equal((0, 0), (a.X, a.Y));
        Assert.Equal((1, 0), (b.X, b.Y));
    }

    [Fact]
    public void Packs_a_colliding_stored_position_to_the_nearest_free_cell()
    {
        // two distinct items both stored at (0,0) (an unmaterialised container) — the second moves aside
        var r = InventoryGrid.Pack(4, 4, [Item("A", 0, 0), Item("B", 0, 0)]);
        var a = r.Items.Single(p => p.Item.DefName == "A");
        var b = r.Items.Single(p => p.Item.DefName == "B");
        Assert.Equal((0, 0), (a.X, a.Y));
        Assert.Equal((1, 0), (b.X, b.Y));   // nearest free to (0,0), row-major
        AssertNoOverlaps(r);
    }

    [Fact]
    public void Collapses_identical_items_in_one_cell_into_a_stack()
    {
        // 16 rounds all at (0,0) is one stacked block, not sixteen cells (the ammo case)
        var ammo = Enumerable.Range(0, 16).Select(_ => Item("ItmAmmo9mm", 0, 0)).ToList();
        var r = InventoryGrid.Pack(4, 4, ammo);
        var block = Assert.Single(r.Items);
        Assert.Equal(16, block.Count);
        Assert.Equal((0, 0), (block.X, block.Y));
    }

    [Fact]
    public void Defaults_to_a_6x6_grid_when_no_dimensions()
    {
        var r = InventoryGrid.Pack(0, 0, [Item("A", 0, 0)]);
        Assert.Equal(6, r.Width);
        Assert.Equal(6, r.Height);
    }

    [Fact]
    public void A_multi_tile_item_reserves_its_whole_footprint()
    {
        var r = InventoryGrid.Pack(4, 4, [Item("Big", 0, 0, w: 2, h: 1), Item("Small", 0, 0)]);
        var big = r.Items.Single(p => p.Item.DefName == "Big");
        Assert.Equal((0, 0, 2, 1), (big.X, big.Y, big.W, big.H));
        AssertNoOverlaps(r);   // the 1x1 can't land on either of the 2x1's cells
    }

    [Fact]
    public void Grows_the_grid_when_items_dont_fit()
    {
        // two distinct 1x1 items into a 1x1 grid — the grid must grow so nothing is hidden
        var r = InventoryGrid.Pack(1, 1, [Item("A", 0, 0), Item("B", 0, 0)]);
        Assert.Equal(2, r.Items.Count);
        Assert.True(r.Height >= 2);
        AssertNoOverlaps(r);
    }

    [Fact]
    public void FirstFreeCell_finds_the_first_free_cell_row_major()
    {
        // a 2x1 grid with one item at (0,0): the first free 1x1 is (1,0)
        Assert.Equal((1, 0), InventoryGrid.FirstFreeCell(2, 1, [Item("A", 0, 0)], 1, 1));
    }

    [Fact]
    public void FirstFreeCell_is_null_when_the_declared_grid_is_full()
    {
        // both cells of a 2x1 grid taken — no room, and (unlike Pack) FirstFreeCell never grows the grid
        Assert.Null(InventoryGrid.FirstFreeCell(2, 1, [Item("A", 0, 0), Item("B", 1, 0)], 1, 1));
    }

    [Fact]
    public void FirstFreeCell_respects_a_multi_tile_footprint()
    {
        // a 2x2 grid with a 1x1 at (0,0): a 2x1 can't sit on row 0 (col 0 taken) but fits at (0,1)
        Assert.Equal((0, 1), InventoryGrid.FirstFreeCell(2, 2, [Item("A", 0, 0)], 2, 1));
    }

    [Fact]
    public void FirstFreeCell_rejects_an_item_wider_than_the_grid()
    {
        // a 4-wide item in a 3-wide grid fits nowhere. Clamping it to the grid width instead would report a cell
        // and let the add place something the container cannot hold.
        Assert.Null(InventoryGrid.FirstFreeCell(3, 5, [], 4, 1));
        Assert.Null(InventoryGrid.FirstFreeCell(3, 5, [], 1, 6));
    }

    [Fact]
    public void Pack_grows_the_grid_for_an_item_wider_than_it()
    {
        // an over-wide item is a data defect; show it whole rather than squash it into a space it cannot occupy
        var r = InventoryGrid.Pack(2, 2, [Item("Wide", 0, 0, w: 4, h: 1)]);
        Assert.True(r.Width >= 4);
        var wide = Assert.Single(r.Items);
        Assert.Equal(4, wide.W);
    }

    [Fact]
    public void Pack_keeps_differently_rotated_items_at_one_cell_apart()
    {
        // real saves park everything at (0,0). Two same-def items there in different orientations are two items,
        // not a stack of two: merging them would draw one under the other's footprint.
        var upright = Item("Missile", 0, 0, w: 1, h: 3);
        var flat = Item("Missile", 0, 0, w: 1, h: 3) with { StrID = "flat", GridRot = 90 };
        var r = InventoryGrid.Pack(3, 5, [upright, flat]);
        Assert.Equal(2, r.Items.Count);
        Assert.All(r.Items, p => Assert.Equal(1, p.Count));
        AssertNoOverlaps(r);
    }

    // ---- rotation-aware placement ----

    [Fact]
    public void FirstFreeCellRotated_prefers_the_upright_orientation()
    {
        var cell = InventoryGrid.FirstFreeCellRotated(3, 5, [], 1, 3, canRotate: true);
        Assert.Equal(new FreeCell(0, 0, 0), cell);   // upright while there is room for upright
    }

    [Fact]
    public void FirstFreeCellRotated_lays_an_item_on_its_side_when_upright_no_longer_fits()
    {
        // the Polaris decoy launcher: a 3x5 grid already holding three upright 1x3 missiles. The 3x2 band left
        // over takes no upright missile, but takes a flat one.
        var stored = new List<CargoItem>
        {
            Item("M", 0, 0, w: 1, h: 3),
            Item("M", 1, 0, w: 1, h: 3),
            Item("M", 2, 0, w: 1, h: 3),
        };
        var cell = InventoryGrid.FirstFreeCellRotated(3, 5, stored, 1, 3, canRotate: true);
        Assert.Equal(new FreeCell(0, 3, 90), cell);
    }

    [Fact]
    public void FirstFreeCellRotated_leaves_the_item_upright_when_rotation_is_refused()
    {
        var stored = new List<CargoItem>
        {
            Item("M", 0, 0, w: 1, h: 3),
            Item("M", 1, 0, w: 1, h: 3),
            Item("M", 2, 0, w: 1, h: 3),
        };
        // canRotate false is the sheet-item case (walls and floors never turn) — the container is simply full
        Assert.Null(InventoryGrid.FirstFreeCellRotated(3, 5, stored, 1, 3, canRotate: false));
        Assert.Null(InventoryGrid.FirstFreeCell(3, 5, stored, 1, 3));
    }

    [Fact]
    public void FirstFreeCellRotated_does_not_rotate_a_square_footprint()
    {
        var cell = InventoryGrid.FirstFreeCellRotated(2, 2, [], 2, 2, canRotate: true);
        Assert.Equal(0, cell!.Rot);
    }
}
