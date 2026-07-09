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
