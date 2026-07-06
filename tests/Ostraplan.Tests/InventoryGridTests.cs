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
}
