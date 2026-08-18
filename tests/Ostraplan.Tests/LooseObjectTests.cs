using System.Linq;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>The Items palette: loose cargo dropped on a ship's floor (or into a container). Placement law, the
/// undo/redo commands, the free-standing export, and the .oplan round-trip. Install-gated (needs real floor/cargo
/// defs and their tile conditions).</summary>
public class LooseObjectTests
{
    private const string Floor = "ItmFloorGrate01";
    private const string Cargo = "ItmScrapAluminum";     // loose cargo
    private const string Container = "ItmBackpack01";     // a container with a fit filter

    private static (ShipDocument Doc, Catalog Cat) FloorAt(int x, int y)
    {
        var g = TestData.RequireGame();
        var doc = new ShipDocument(g.Catalog);
        new PlaceCommand(new Placement { DefName = Floor, X = x, Y = y }).Do(doc);
        return (doc, g.Catalog);
    }

    [SkippableFact]
    public void Item_may_rest_on_a_floor_tile_but_not_off_ship_or_on_a_taken_tile()
    {
        var (doc, _) = FloorAt(2, 2);

        Assert.True(LoosePlacement.CanRestOnFloor(doc, 2, 2));    // on the floor
        Assert.False(LoosePlacement.CanRestOnFloor(doc, 9, 9));   // empty space, no floor

        new PlaceLooseCommand(new LooseObject { DefName = Cargo, X = 2, Y = 2 }).Do(doc);
        Assert.False(LoosePlacement.CanRestOnFloor(doc, 2, 2));   // one per tile — now taken
        Assert.NotNull(doc.LooseAt(2, 2));
    }

    [SkippableFact]
    public void Place_and_remove_loose_are_reversible()
    {
        var (doc, _) = FloorAt(0, 0);
        var stack = new CommandStack();

        var obj = new LooseObject { DefName = Cargo, X = 0, Y = 0 };
        stack.Push(doc, new PlaceLooseCommand(obj));
        Assert.Single(doc.LooseObjects);

        stack.Undo(doc);
        Assert.Empty(doc.LooseObjects);
        stack.Redo(doc);
        Assert.Single(doc.LooseObjects);

        stack.Push(doc, new RemoveLooseCommand(doc.LooseAt(0, 0)!));
        Assert.Empty(doc.LooseObjects);
        stack.Undo(doc);
        Assert.Single(doc.LooseObjects);   // remove undone → back on its tile
    }

    [SkippableFact]
    public void An_open_container_under_the_cursor_takes_the_item()
    {
        var g = TestData.RequireGame();
        Skip.If(g.Catalog.Lookup(Container) is null || g.Catalog.Lookup(Cargo) is null, "defs not in this build");
        var doc = new ShipDocument(g.Catalog);
        // a backpack sitting on a floor tile
        new PlaceCommand(new Placement { DefName = Floor, X = 3, Y = 3 }).Do(doc);
        new PlaceCommand(new Placement { DefName = Container, X = 3, Y = 3 }).Do(doc);

        var container = LoosePlacement.AcceptingContainerAt(doc, g.Catalog, 3, 3, g.Catalog.Lookup(Cargo)!);
        Assert.NotNull(container);
        Assert.Equal(Container, container!.DefName);
    }

    [SkippableFact]
    public void Export_emits_a_loose_item_as_a_free_standing_top_level_item()
    {
        var g = TestData.RequireGame();
        Skip.If(g.Catalog.Lookup(Cargo) is null, "cargo def not in this build");
        var specs = RoomCertifier.LoadSpecs(g.Index);
        var (doc, _) = FloorAt(5, 5);
        new PlaceLooseCommand(new LooseObject { DefName = Cargo, X = 5, Y = 5 }).Do(doc);

        var (ship, _, _) = ShipExport.Build(doc, g.Catalog, specs, "Loose Export");

        var emitted = ship.AItems.Where(i => i.StrName == Cargo).ToList();
        Assert.Single(emitted);
        Assert.Null(emitted[0].StrParentID);        // free-standing, not inside a container
        Assert.Null(emitted[0].StrSlotParentID);
    }

    [SkippableFact]
    public void Oplan_round_trips_loose_objects_including_quantity()
    {
        var g = TestData.RequireGame();
        Skip.If(g.Catalog.Lookup(Cargo) is null, "cargo def not in this build");
        var (doc, _) = FloorAt(1, 1);
        new PlaceLooseCommand(new LooseObject { DefName = Cargo, X = 1, Y = 1, Rot = 90, Quantity = 4 }).Do(doc);

        var file = OplanFile.FromDocument(doc, g.Index, new OplanMeta());
        Assert.Single(file.LooseObjects);

        var (reopened, missing) = file.ToDocument(g.Catalog);
        Assert.Empty(missing);
        var lo = Assert.Single(reopened.LooseObjects);
        Assert.Equal(Cargo, lo.DefName);
        Assert.Equal((1, 1), (lo.X, lo.Y));
        Assert.Equal(90, lo.Rot);
        Assert.Equal(4, lo.Quantity);
    }

    [SkippableFact]
    public void Change_quantity_is_reversible_in_place()
    {
        var (doc, _) = FloorAt(0, 0);
        var stack = new CommandStack();
        var obj = new LooseObject { DefName = Cargo, X = 0, Y = 0, Quantity = 1 };
        stack.Push(doc, new PlaceLooseCommand(obj));

        stack.Push(doc, new SetLooseQuantityCommand(obj, 1, 5));
        Assert.Equal(5, doc.LooseAt(0, 0)!.Quantity);
        stack.Undo(doc);
        Assert.Equal(1, doc.LooseAt(0, 0)!.Quantity);   // same object, quantity restored
    }

    // ---- deck items that hold things ----

    /// <summary>A deck item that can hold things offers an inventory: a real container, or a garment/suit/backpack
    /// that stores in its own pockets. A severed limb declares slots too, but they are wound sockets.</summary>
    [SkippableFact]
    public void What_can_hold_cargo_takes_containers_and_wearables_but_not_anatomy()
    {
        var g = TestData.RequireGame();
        Skip.If(g.Catalog.Lookup("OutfitEVA01") is null || g.Catalog.Lookup(Container) is null,
            "this install lacks a probe def");

        bool CanHold(string def) => Ostraplan.Core.Cargo.CanHoldCargo(g.Catalog.Lookup(def), g.Catalog);

        Assert.True(CanHold(Container));            // a backpack: a grid AND pockets
        Assert.True(CanHold("OutfitEVA01"));        // an EVA suit: no grid at all, four compartments
        Assert.False(CanHold(Cargo));               // a lump of scrap holds nothing
        if (g.Catalog.Lookup("BodyarmUpperLA") is not null)
            Assert.False(CanHold("BodyarmUpperLA"));   // wound sockets are anatomy, not storage
    }

    /// <summary>
    /// An EVA suit dropped on the deck arrives with its four compartments, so "View contents" has something to
    /// show and the save write has something to record. Seeding happens on the way into the document, so every
    /// route in (a palette drop, an import, an .oplan load, a redo) gets it.
    /// </summary>
    [SkippableFact]
    public void A_suit_dropped_on_the_deck_arrives_with_its_compartments()
    {
        var (doc, cat) = FloorAt(1, 1);
        Skip.If(cat.Lookup("OutfitEVA01") is null, "OutfitEVA01 not in this install");

        var suit = new LooseObject { DefName = "OutfitEVA01", X = 1, Y = 1 };
        new PlaceLooseCommand(suit).Do(doc);

        Assert.Equal(4, suit.Cargo.Count);
        Assert.All(suit.Cargo, c => Assert.True(c.Slotted && c.Intrinsic));
        Assert.Equal(4, suit.Cargo.Select(c => c.SlotName).Distinct().Count());

        // and it is idempotent: a redo re-adds the same object and must not double them
        new PlaceLooseCommand(suit).Do(doc);
        Assert.Equal(4, suit.Cargo.Count);
    }

    [SkippableFact]
    public void Deck_cargo_is_editable_and_survives_an_oplan_round_trip()
    {
        var g = TestData.RequireGame();
        var (doc, cat) = FloorAt(3, 3);
        var pack = new LooseObject { DefName = Container, X = 3, Y = 3 };
        new PlaceLooseCommand(pack).Do(doc);
        var grid = cat.Lookup(Container)!.ContainerGrid!.Value;
        var filled = CargoEdit.Add(pack.Cargo, null, grid, cat.Lookup(Cargo)!, 1, cat);
        Skip.If(filled is null, "the backpack would not take the probe item");

        var stack = new CommandStack();
        stack.Push(doc, new SetLooseCargoCommand(pack, pack.Cargo, filled!));
        Assert.Contains(pack.Cargo, c => c.DefName == Cargo);

        stack.Undo(doc);
        Assert.DoesNotContain(pack.Cargo, c => c.DefName == Cargo);
        stack.Redo(doc);
        Assert.Contains(pack.Cargo, c => c.DefName == Cargo);

        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ostraplan-test-{System.Guid.NewGuid():N}.oplan");
        try
        {
            OplanFile.FromDocument(doc, g.Index, new OplanMeta()).Save(tmp);
            var (back, _) = OplanFile.Load(tmp).ToDocument(cat);
            var reopened = Assert.Single(back.LooseObjects);
            Assert.Contains(reopened.Cargo, c => c.DefName == Cargo);
            Assert.Equal(pack.Cargo.Count, reopened.Cargo.Count);   // pouches included, and not doubled by the seed
        }
        finally { System.IO.File.Delete(tmp); }
    }

    /// <summary>A crate on the deck takes a dropped item the same way an installed one does.</summary>
    [SkippableFact]
    public void A_deck_container_accepts_a_dropped_item()
    {
        var (doc, cat) = FloorAt(4, 4);
        var pack = new LooseObject { DefName = Container, X = 4, Y = 4 };
        new PlaceLooseCommand(pack).Do(doc);
        var item = cat.Lookup(Cargo)!;

        Assert.Equal(pack, LoosePlacement.AcceptingLooseAt(doc, cat, 4, 4, item));
        Assert.Null(LoosePlacement.AcceptingLooseAt(doc, cat, 9, 9, item));   // nothing there
        // an item that holds nothing never takes a drop
        new PlaceLooseCommand(new LooseObject { DefName = Cargo, X = 4, Y = 5 }).Do(doc);
        Assert.Null(LoosePlacement.AcceptingLooseAt(doc, cat, 4, 5, item));
    }

    [SkippableFact]
    public void A_stack_exports_as_a_head_plus_members_with_astack()
    {
        var g = TestData.RequireGame();
        var stackable = g.Catalog.LooseItems.FirstOrDefault(p => p.StackLimit > 1 && p.SpriteAbs is not null);
        Skip.If(stackable is null, "no stackable loose item in this build");
        var specs = RoomCertifier.LoadSpecs(g.Index);
        var doc = new ShipDocument(g.Catalog);
        new PlaceCommand(new Placement { DefName = Floor, X = 5, Y = 5 }).Do(doc);
        var qty = System.Math.Min(3, stackable!.StackLimit);
        new PlaceLooseCommand(new LooseObject { DefName = stackable.DefName, X = 5, Y = 5, Quantity = qty }).Do(doc);

        var (ship, _, _) = ShipExport.Build(doc, g.Catalog, specs, "Stack Export");

        var emitted = ship.AItems.Where(i => i.StrName == stackable.DefName).ToList();
        Assert.Equal(qty, emitted.Count);                       // one head + (qty-1) members
        var head = Assert.Single(emitted, i => i.StrParentID is null);
        var members = emitted.Where(i => i.StrParentID == head.StrID).ToList();
        Assert.Equal(qty - 1, members.Count);
        Assert.All(members, m => Assert.True(m.BForceLoad == true));   // members keep their strIDs so the stack rebuilds
        var co = Assert.Single(ship.ACOs!, c => c.StrID == head.StrID);
        Assert.Equal(qty - 1, co.AStack!.Length);              // head lists its members
    }
}
