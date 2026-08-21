using System;
using System.Collections.Generic;
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

    // ---- moving loose items as a group (#34): the pose is mutable, the tile index follows it, and one item per
    // tile is the only thing that can refuse a transform ----

    [SkippableFact]
    public void Moving_a_loose_item_keeps_its_identity_and_reindexes_the_tile()
    {
        var (doc, _) = FloorAt(0, 0);
        var stack = new CommandStack();
        var obj = new LooseObject { DefName = Cargo, X = 0, Y = 0 };
        new PlaceLooseCommand(obj).Do(doc);

        stack.Push(doc, new SetLoosePosesCommand([(obj, 3, 4, 90)]));

        Assert.Null(doc.LooseAt(0, 0));                       // the old tile is free again
        Assert.Same(obj, doc.LooseAt(3, 4));                  // the same object, not a copy — the selection survives
        Assert.Equal((3, 4, 90), (obj.X, obj.Y, obj.Rot));

        stack.Undo(doc);
        Assert.Same(obj, doc.LooseAt(0, 0));
        Assert.Equal((0, 0, 0), (obj.X, obj.Y, obj.Rot));
    }

    [SkippableFact]
    public void A_group_move_that_shuffles_within_itself_loses_nothing()
    {
        // two items in a row sliding one tile east: the first lands on the tile the second is still on, which is
        // exactly the case a move-one-at-a-time implementation drops an item in
        var (doc, _) = FloorAt(0, 0);
        var a = new LooseObject { DefName = Cargo, X = 0, Y = 0 };
        var b = new LooseObject { DefName = Cargo, X = 1, Y = 0 };
        new PlaceLooseCommand(a).Do(doc);
        new PlaceLooseCommand(b).Do(doc);

        var poses = LooseTransform.Poses(doc, [a, b], o => (o.X + 1, o.Y, o.Rot));
        Assert.NotNull(poses);   // a mover never blocks itself: b is vacating the tile a wants

        var stack = new CommandStack();
        stack.Push(doc, new SetLoosePosesCommand(poses!));

        Assert.Equal(2, doc.LooseObjects.Count);
        Assert.Same(a, doc.LooseAt(1, 0));
        Assert.Same(b, doc.LooseAt(2, 0));

        stack.Undo(doc);
        Assert.Equal(2, doc.LooseObjects.Count);
        Assert.Same(a, doc.LooseAt(0, 0));
        Assert.Same(b, doc.LooseAt(1, 0));
    }

    [SkippableFact]
    public void A_transform_onto_a_tile_something_else_holds_is_refused_whole()
    {
        var (doc, _) = FloorAt(0, 0);
        var moving = new LooseObject { DefName = Cargo, X = 0, Y = 0 };
        var sitting = new LooseObject { DefName = Cargo, X = 1, Y = 0 };
        new PlaceLooseCommand(moving).Do(doc);
        new PlaceLooseCommand(sitting).Do(doc);

        // the item sitting there is not part of the move, so it blocks it — and blocks all of it, rather than the
        // move going half through and stranding what could not land
        Assert.Null(LooseTransform.Poses(doc, [moving], o => (o.X + 1, o.Y, o.Rot)));
        Assert.Same(moving, doc.LooseAt(0, 0));   // nothing was touched by the asking

        // two movers landing on the same tile is refused for the same reason
        Assert.Null(LooseTransform.Poses(doc, [moving, sitting], _ => (5, 5, 0)));
    }

    [SkippableFact]
    public void Loose_free_at_exempts_the_movers()
    {
        var (doc, _) = FloorAt(0, 0);
        var obj = new LooseObject { DefName = Cargo, X = 2, Y = 2 };
        new PlaceLooseCommand(obj).Do(doc);

        Assert.False(doc.LooseFreeAt(2, 2));                          // occupied
        Assert.True(doc.LooseFreeAt(2, 2, new HashSet<Guid> { obj.Id }));   // occupied by something about to leave
        Assert.True(doc.LooseFreeAt(7, 7));                           // bare tile
    }

    [SkippableFact]
    public void Removing_every_loose_item_is_one_undo_step()
    {
        var (doc, _) = FloorAt(0, 0);
        for (var x = 0; x < 4; x++) new PlaceLooseCommand(new LooseObject { DefName = Cargo, X = x, Y = 0 }).Do(doc);
        Assert.Equal(4, doc.LooseObjects.Count);

        var stack = new CommandStack();
        stack.Push(doc, new CompositeCommand(
            doc.LooseObjects.ToList().Select(o => (IDocCommand)new RemoveLooseCommand(o)).ToList()));
        Assert.Empty(doc.LooseObjects);

        stack.Undo(doc);
        Assert.Equal(4, doc.LooseObjects.Count);   // one press puts the whole deck back
    }
}
