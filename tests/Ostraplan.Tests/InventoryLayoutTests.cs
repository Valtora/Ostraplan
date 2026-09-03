using System.Collections.Generic;
using System.Linq;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// Which slotted contents get a grid drawn with their host, and where it sits — the port of
/// <c>GUIInventory.SpawnInventoryWindow</c> (see <see cref="InventoryLayout"/>). Install-free except for the two
/// that hold the geometry against the real backpack defs.
/// </summary>
public class InventoryLayoutTests
{
    /// <summary>A backpack that spawns its own pouches, laid out the way <c>ItmBackpack01</c> is: a 4×4 grid at
    /// <c>self</c> {5,0} with four 1×1 pouches in a row 68 units below it.</summary>
    private static Catalog Backpack() => new Fixtures()
        .Part("PocketPouchSmall01", container: (1, 1), slotKeys:
            ["pocket_pouchSm01", "pocket_pouchSm02", "pocket_pouchSm03", "pocket_pouchSm04"])
        .Part("ItmBackpack01", container: (4, 4),
            slotsWeHave: ["pocket_pouchSm01", "pocket_pouchSm02", "pocket_pouchSm03", "pocket_pouchSm04"],
            slotLayout: new Dictionary<string, (double X, double Y)>
            {
                ["self"] = (5, 0),
                ["pocket_pouchSm01"] = (0, -68),
                ["pocket_pouchSm02"] = (20, -68),
                ["pocket_pouchSm03"] = (40, -68),
                ["pocket_pouchSm04"] = (60, -68),
            })
        .Build();

    private static CargoItem Pouch(string id, string slot) =>
        new(id, "PocketPouchSmall01", "Pouch", Slotted: true, []) { SlotName = slot };

    [Fact]
    public void A_backpacks_pouches_are_drawn_with_it_in_a_row_under_its_grid()
    {
        var cat = Backpack();
        var contents = new List<CargoItem>
        {
            Pouch("p1", "pocket_pouchSm01"),
            Pouch("p2", "pocket_pouchSm02"),
            Pouch("p3", "pocket_pouchSm03"),
            Pouch("p4", "pocket_pouchSm04"),
        };

        var figure = InventoryLayout.Compose(cat, "ItmBackpack01", hostId: null, contents);

        Assert.Equal((4, 4), figure.Grid);
        // self {5,0} shifts the host's own grid a fraction of a cell right, and nothing else moves with it.
        Assert.Equal(5 / 16.0, figure.SelfOffset.X, 6);
        Assert.Equal(0, figure.SelfOffset.Y, 6);

        Assert.Equal(4, figure.Children.Count);
        Assert.All(figure.Children, p => Assert.Equal((1, 1), p.Grid));

        // x 0/20/40/60 is 1.25 cells apart, so four 1×1 pouches sit in a row with a quarter-cell gap.
        Assert.Equal([0, 1.25, 2.5, 3.75], figure.Children.Select(p => p.Offset!.Value.X));
        // y -68 is 4.25 cells DOWN (the game's +y is up), clearing a 4-tall grid by a quarter of a cell.
        Assert.All(figure.Children, p => Assert.Equal(4.25, p.Offset!.Value.Y, 6));
    }

    [Fact]
    public void A_pouchs_own_contents_come_through_as_a_nested_figure()
    {
        var cat = new Fixtures()
            .Part("PocketPouchSmall01", container: (1, 1), slotKeys: ["pocket_pouchSm01"],
                slotsWeHave: ["pocket_tiny"],
                slotLayout: new Dictionary<string, (double X, double Y)> { ["pocket_tiny"] = (16, 0) })
            .Part("PocketTiny", container: (1, 1), slotKeys: ["pocket_tiny"])
            .Part("ItmBackpack01", container: (4, 4), slotsWeHave: ["pocket_pouchSm01"],
                slotLayout: new Dictionary<string, (double X, double Y)> { ["pocket_pouchSm01"] = (0, -68) })
            .Build();

        var inner = new CargoItem("t1", "PocketTiny", "Tiny", Slotted: true, []) { SlotName = "pocket_tiny" };
        var pouch = new CargoItem("p1", "PocketPouchSmall01", "Pouch", Slotted: true, [inner])
        {
            SlotName = "pocket_pouchSm01",
        };

        var figure = InventoryLayout.Compose(cat, "ItmBackpack01", hostId: null, [pouch]);

        var p = Assert.Single(figure.Children);
        var t = Assert.Single(p.Children);
        Assert.Equal("t1", t.ContainerId);
        Assert.Equal(1.0, t.Offset!.Value.X, 6);   // 16 units is exactly one cell
        // Flatten reaches every level, which is what the view registers as drop targets.
        Assert.Equal(["ItmBackpack01", "PocketPouchSmall01", "PocketTiny"], figure.Flatten().Select(x => x.DefName));
    }

    [Fact]
    public void A_child_with_no_position_on_its_host_is_left_unpinned()
    {
        // No dictSlotsLayout at all: the game gives that child an ordinary titled window beside the parent
        // rather than pinning it, so the panel comes through with no offset for the view to flow instead.
        var cat = new Fixtures()
            .Part("NavModule", container: (2, 2), slotKeys: ["module01"])
            .Part("NavConsole", container: (3, 3), slotsWeHave: ["module01"])
            .Build();

        var module = new CargoItem("m1", "NavModule", "Module", Slotted: true, []) { SlotName = "module01" };

        var figure = InventoryLayout.Compose(cat, "NavConsole", hostId: null, [module]);

        var p = Assert.Single(figure.Children);
        Assert.Null(p.Offset);
        Assert.Equal((2, 2), p.Grid);
    }

    [Fact]
    public void A_slotted_item_that_holds_nothing_is_not_drawn_as_a_grid()
    {
        // The game's last clause: a child gets a window only if it has a container or a slot layout of its own.
        // A battery in a suit's power slot has neither, so it stays a slot cell.
        var cat = new Fixtures()
            .Part("ItmBattery01", slotKeys: ["power01"])
            .Part("ItmSuit", container: (2, 2), slotsWeHave: ["power01"],
                slotLayout: new Dictionary<string, (double X, double Y)> { ["power01"] = (0, -32) })
            .Build();

        var battery = new CargoItem("b1", "ItmBattery01", "Battery", Slotted: true, []) { SlotName = "power01" };

        Assert.Empty(InventoryLayout.Compose(cat, "ItmSuit", hostId: null, [battery]).Children);
    }

    [Fact]
    public void A_hidden_slot_and_a_concealed_or_locked_item_stay_shut()
    {
        var cat = new Fixtures()
            .Slot("secret", hide: true)
            .Part("ItmStash", container: (1, 1), slotKeys: ["secret", "open01", "open02"])
            .Part("ItmLockbox", container: (1, 1), startingConds: ["IsLocked"], slotKeys: ["open01"])
            .Part("ItmConcealed", container: (1, 1), startingConds: ["IsHiddenInv"], slotKeys: ["open02"])
            .Part("ItmCoat", container: (2, 2), slotsWeHave: ["secret", "open01", "open02"])
            .Build();

        var coat = _catalog(cat, "ItmCoat");
        Assert.False(InventoryLayout.ShowsWithHost(cat, coat, _catalog(cat, "ItmStash"), "secret"));
        Assert.False(InventoryLayout.ShowsWithHost(cat, coat, _catalog(cat, "ItmLockbox"), "open01"));
        Assert.False(InventoryLayout.ShowsWithHost(cat, coat, _catalog(cat, "ItmConcealed"), "open02"));
        // The same stash in a slot that is not hidden does open, so it is the slot being refused and not the item.
        Assert.True(InventoryLayout.ShowsWithHost(cat, coat, _catalog(cat, "ItmStash"), "open01"));
    }

    [Fact]
    public void A_stack_is_never_drawn_as_a_container_of_itself()
    {
        // A stack's children are copies of the item, not cargo. Composing one as a container would draw a pouch
        // holding four of itself.
        var cat = Backpack();
        var member = new CargoItem("m", "PocketPouchSmall01", "Pouch", Slotted: true, []);
        var stack = new CargoItem("s", "PocketPouchSmall01", "Pouch", Slotted: true, [member])
        {
            SlotName = "pocket_pouchSm01",
            Stack = 2,
            IsStack = true,
        };

        Assert.Empty(InventoryLayout.Compose(cat, "ItmBackpack01", hostId: null, [stack]).Children);
    }

    [SkippableFact]
    public void The_real_backpacks_lay_their_pouches_clear_of_their_own_grid()
    {
        var g = TestData.RequireGame();

        foreach (var name in new[] { "ItmBackpack01", "ItmBackpack03" })
        {
            var def = g.Catalog.Lookup(name);
            Skip.If(def is null, name + " is not in this install");

            var grid = def!.ContainerGrid!.Value;
            var pouches = def.SlotsWeHave
                .Where(s => def.SlotLayout.ContainsKey(s))
                .Select(s => InventoryLayout.ToCells(def.SlotLayout[s]))
                .ToList();
            Assert.NotEmpty(pouches);

            // Every pouch clears the host's own grid, either below it or off to its side. That is the whole point
            // of the offsets: laid out any other way the pouch row would sit on top of the 4×4.
            var self = InventoryLayout.ToCells(def.SlotLayout.GetValueOrDefault("self"));
            Assert.All(pouches, p => Assert.True(
                p.Y >= self.Y + grid.H || p.X >= self.X + grid.W,
                $"{name}: a pouch at {p} overlaps a {grid.W}x{grid.H} grid at {self}"));
        }
    }

    [SkippableFact]
    public void A_real_backpacks_pouches_are_drawn_with_it()
    {
        var g = TestData.RequireGame();
        var pack = g.Catalog.Lookup("ItmBackpack01");
        var pouch = g.Catalog.Lookup("PocketPouchSmall01");
        Skip.If(pack is null || pouch is null, "the stock backpack is not in this install");

        // The pouches the pack spawns with are exactly the children the view has to draw.
        var intrinsic = CargoEdit.IntrinsicContentsOf(pack!, g.Catalog);
        Assert.Equal(4, intrinsic.Count);

        var figure = InventoryLayout.Compose(g.Catalog, "ItmBackpack01", hostId: null, intrinsic);
        Assert.Equal(4, figure.Children.Count);
        Assert.All(figure.Children, p => Assert.NotNull(p.Offset));
        Assert.All(figure.Children, p => Assert.Equal((1, 1), p.Grid));
    }

    private static PartDef? _catalog(Catalog cat, string name) => cat.Lookup(name);
}
