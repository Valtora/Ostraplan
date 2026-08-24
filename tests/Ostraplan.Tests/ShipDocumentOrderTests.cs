using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The render order is cached and <b>repaired</b> around an edit rather than sorted again, because a full sort
/// computes a key per drawable and a key is several dictionary lookups — on a large station that was most of what
/// a single click cost. The repair is only sound while each mutation says truthfully what it moved, and the
/// failure is quiet: parts draw in the wrong layer, on the designs least likely to be in a test.
///
/// <para>So every mutation here is held against <see cref="ShipDocument.RenderOrderFromScratch"/>, the answer a
/// full sort would have given. A mutation that starts moving something it does not declare fails here.</para>
/// </summary>
public class ShipDocumentOrderTests
{
    private static Catalog Cat() => new Fixtures()
        .Floor("Floor").Wall("Wall").Conduit("Conduit")
        .Container("Box", 4, 4)
        .Fixture("Bench")
        .Part("Widget", category: "MISC")
        .Part("Tall", w: 2, h: 2, category: "MISC")
        .Build();

    /// <summary>Enough of a ship for the order to have something to say: several z-scales, several footprints,
    /// and drawables sharing tiles.</summary>
    private static ShipDocument Ship(Catalog cat)
    {
        var doc = Fixtures.Doc(cat);
        for (var x = 0; x < 6; x++)
            for (var y = 0; y < 4; y++)
                new PlaceCommand(Fixtures.P("Floor", x, y)).Do(doc);
        new PlaceCommand(Fixtures.P("Wall", 0, 0)).Do(doc);
        new PlaceCommand(Fixtures.P("Wall", 1, 0)).Do(doc);
        new PlaceCommand(Fixtures.P("Conduit", 2, 0)).Do(doc);
        new PlaceCommand(Fixtures.P("Box", 3, 1)).Do(doc);
        new PlaceCommand(Fixtures.P("Bench", 4, 2)).Do(doc);
        new PlaceCommand(Fixtures.P("Tall", 1, 2)).Do(doc);
        new PlaceLooseCommand(new LooseObject { DefName = "Widget", X = 2, Y = 2 }).Do(doc);
        new PlaceLooseCommand(new LooseObject { DefName = "Widget", X = 5, Y = 3 }).Do(doc);
        return doc;
    }

    /// <summary>The cached (repaired) order and a sort from scratch, as the ids they hold in order.</summary>
    private static void AssertOrderIntact(ShipDocument doc) =>
        Assert.Equal(
            doc.RenderOrderFromScratch().Select(i => i.Id),
            doc.RenderOrder().Select(i => i.Id));

    /// <summary>Warm the cache, run the edit, then check. Without the first read there is nothing to repair and
    /// the test would only ever exercise the full sort.</summary>
    private static void Edit(Action<ShipDocument> edit)
    {
        var doc = Ship(Cat());
        _ = doc.RenderOrder();
        edit(doc);
        AssertOrderIntact(doc);
    }

    [Fact]
    public void After_placing_a_part() => Edit(d => new PlaceCommand(Fixtures.P("Wall", 5, 0)).Do(d));

    [Fact]
    public void After_placing_one_under_an_existing_part() =>
        Edit(d => new PlaceCommand(Fixtures.P("Conduit", 3, 1)).Do(d));

    [Fact]
    public void After_removing_a_part() => Edit(d => new RemoveCommand([d.Placements[10]]).Do(d));

    [Fact]
    public void After_removing_several_parts() =>
        Edit(d => new RemoveCommand([d.Placements[2], d.Placements[7], d.Placements[12]]).Do(d));

    [Fact]
    public void After_moving_a_part() => Edit(d => new MoveCommand([d.Placements[^1]], 0, 2).Do(d));

    /// <summary>Moving down the deck changes the body's bottom edge, which is a term in the key — so the part
    /// really does change place in the order rather than merely on screen.</summary>
    [Fact]
    public void After_moving_a_part_past_another()
    {
        var doc = Ship(Cat());
        _ = doc.RenderOrder();
        new MoveCommand([doc.Placements.Single(p => p.DefName == "Tall")], 3, 1).Do(doc);
        AssertOrderIntact(doc);
    }

    [Fact]
    public void After_rotating_a_part()
    {
        var doc = Ship(Cat());
        _ = doc.RenderOrder();
        new RotateCommand(doc, doc.Placements.Single(p => p.DefName == "Tall"), 90).Do(doc);
        AssertOrderIntact(doc);
    }

    [Fact]
    public void After_nudging_the_z_order()
    {
        var doc = Ship(Cat());
        _ = doc.RenderOrder();
        var bench = doc.Placements.Single(p => p.DefName == "Bench");
        new SetZOrderCommand([new ZOrder.BiasChange(new RenderItem(bench, null), 0, 5)], "raise").Do(doc);
        AssertOrderIntact(doc);
    }

    [Fact]
    public void After_nudging_a_deck_items_z_order()
    {
        var doc = Ship(Cat());
        var loose = doc.RenderOrder().First(i => i.IsLoose);
        new SetZOrderCommand([new ZOrder.BiasChange(loose, 0, -3)], "lower").Do(doc);
        AssertOrderIntact(doc);
    }

    [Fact]
    public void After_dropping_an_item_on_the_deck() =>
        Edit(d => new PlaceLooseCommand(new LooseObject { DefName = "Widget", X = 0, Y = 3 }).Do(d));

    /// <summary>The tile index is keyed by position, so dropping onto an occupied tile turns the object that was
    /// there out of the document with nothing else to show for it. The order has to lose it too, or it goes on
    /// drawing a thing that is not aboard.</summary>
    [Fact]
    public void After_dropping_an_item_onto_one_already_there()
    {
        var doc = Ship(Cat());
        var before = doc.RenderOrder().Count;
        new PlaceLooseCommand(new LooseObject { DefName = "Widget", X = 2, Y = 2 }).Do(doc);
        AssertOrderIntact(doc);
        Assert.Equal(before, doc.RenderOrder().Count);   // one in, one out
    }

    [Fact]
    public void After_picking_a_deck_item_up()
    {
        var doc = Ship(Cat());
        _ = doc.RenderOrder();
        new RemoveLooseCommand(doc.LooseAt(2, 2)!).Do(doc);
        AssertOrderIntact(doc);
    }

    [Fact]
    public void After_moving_several_deck_items_at_once()
    {
        var doc = Ship(Cat());
        _ = doc.RenderOrder();
        var a = doc.LooseAt(2, 2)!;
        var b = doc.LooseAt(5, 3)!;
        new SetLoosePosesCommand([(a, 0, 1, 0), (b, 1, 1, 90)]).Do(doc);
        AssertOrderIntact(doc);
    }

    [Fact]
    public void After_renaming() => Edit(d => new SetCustomNameCommand(d.Placements[0], null, "Deck plate").Do(d));

    [Fact]
    public void After_editing_cargo() =>
        Edit(d => new SetCargoCommand(d.Placements.Single(p => p.DefName == "Box"), [],
            [new CargoItem("c1", "Widget", null, false, [])]).Do(d));

    [Fact]
    public void After_painting_condition() =>
        Edit(d => new SetConditionCommand(d.Placements.First(p => p.DefName == "Wall"), null, 0.3).Do(d));

    [Fact]
    public void After_painting_a_zone() =>
        Edit(d => new CreateZoneCommand(
            new ShipZone { Name = "z", TileConds = [ShipZone.CondHaul], Tiles = [(0, 0), (1, 0)] }).Do(d));

    [Fact]
    public void After_wiring_two_devices() =>
        Edit(d => new AddLinkCommand(new DeviceLink(d.Placements[0].Id, d.Placements[1].Id)).Do(d));

    /// <summary>Undo has to leave the order exactly as a fresh sort would, or an edit and a Ctrl+Z would not be a
    /// round trip on screen.</summary>
    [Fact]
    public void After_an_edit_and_its_undo()
    {
        var doc = Ship(Cat());
        _ = doc.RenderOrder();
        var cmd = new MoveCommand([doc.Placements[^1]], 2, 1);
        cmd.Do(doc);
        _ = doc.RenderOrder();
        cmd.Undo(doc);
        AssertOrderIntact(doc);
    }

    /// <summary>A burst inside one batch: the notifications collapse into a single event, but the order still has
    /// to account for every one of them.</summary>
    [Fact]
    public void After_a_batch_of_edits()
    {
        var doc = Ship(Cat());
        _ = doc.RenderOrder();
        using (doc.SuspendChanged())
        {
            new PlaceCommand(Fixtures.P("Wall", 5, 1)).Do(doc);
            new RemoveCommand([doc.Placements[3]]).Do(doc);
            new MoveCommand([doc.Placements[^1]], 1, 1).Do(doc);
        }
        AssertOrderIntact(doc);
    }

    /// <summary>A long run of mixed edits, each one checked, over several seeds — so an error that only shows up
    /// once the order has been repaired hundreds of times, or only for a particular sequence, has somewhere to
    /// surface. This is what caught a deck item dropped onto an occupied tile leaving a ghost behind.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(20260824)]
    [InlineData(99991)]
    [InlineData(1234567)]
    public void After_a_long_run_of_mixed_edits(int seed)
    {
        var doc = Ship(Cat());
        _ = doc.RenderOrder();
        var rng = new Random(seed);
        for (var step = 0; step < 400; step++)
        {
            var parts = doc.Placements.Where(p => p.DefName != "Floor").ToList();
            switch (rng.Next(5))
            {
                case 0:
                    new PlaceCommand(Fixtures.P(rng.Next(2) == 0 ? "Wall" : "Conduit", rng.Next(8), rng.Next(6))).Do(doc);
                    break;
                case 1 when parts.Count > 0:
                    new RemoveCommand([parts[rng.Next(parts.Count)]]).Do(doc);
                    break;
                case 2 when parts.Count > 0:
                    new MoveCommand([parts[rng.Next(parts.Count)]], rng.Next(-2, 3), rng.Next(-2, 3)).Do(doc);
                    break;
                case 3 when parts.Count > 0:
                    new SetZOrderCommand(
                        [new ZOrder.BiasChange(new RenderItem(parts[rng.Next(parts.Count)], null), 0, rng.Next(-4, 5))],
                        "nudge").Do(doc);
                    break;
                default:
                    new PlaceLooseCommand(new LooseObject { DefName = "Widget", X = rng.Next(8), Y = rng.Next(6) }).Do(doc);
                    break;
            }
            AssertOrderIntact(doc);
        }
    }

    /// <summary>The version has to move whenever the order does, since the canvas keeps each drawable's extent
    /// against it and the document repairs the list in place — the identity of the list says nothing.</summary>
    [Fact]
    public void The_version_moves_when_the_order_does()
    {
        var doc = Ship(Cat());
        _ = doc.RenderOrder();
        var before = doc.RenderOrderVersion;

        new PlaceCommand(Fixtures.P("Wall", 5, 0)).Do(doc);
        _ = doc.RenderOrder();
        Assert.NotEqual(before, doc.RenderOrderVersion);
    }

    /// <summary>And must not move for a read that changed nothing, or the canvas would rebuild every extent on
    /// every frame.</summary>
    [Fact]
    public void The_version_holds_still_when_nothing_changed()
    {
        var doc = Ship(Cat());
        _ = doc.RenderOrder();
        var before = doc.RenderOrderVersion;
        _ = doc.RenderOrder();
        _ = doc.RenderOrder();
        Assert.Equal(before, doc.RenderOrderVersion);
    }
}
