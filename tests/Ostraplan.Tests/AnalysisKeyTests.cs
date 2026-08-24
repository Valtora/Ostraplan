using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// <see cref="ShipDocument.AnalysisKey"/> is what lets the editor skip re-running the whole off-thread analysis
/// when nothing it reads has changed. It only earns that if it moves for every edit the analysis can see and
/// stays put for every edit it cannot: a key that misses an edit leaves a stale Law report, stale rooms and a
/// stale walk map on screen with nothing to say they are stale.
/// </summary>
public class AnalysisKeyTests
{
    private static Catalog Cat() => new Fixtures()
        .Floor("Floor").Wall("Wall").Door("Door")
        .Container("Box", 4, 4)
        .Part("Widget", category: "MISC")
        .Part("Gadget", category: "MISC")
        .Build();

    private static ShipDocument Ship(Catalog cat) => Fixtures.Doc(cat,
        Fixtures.P("Floor", 0, 0), Fixtures.P("Floor", 1, 0), Fixtures.P("Wall", 0, 1),
        Fixtures.P("Box", 2, 0), Fixtures.P("Widget", 1, 1));

    private static ShipZone Zone(params string[] conds) =>
        new() { Name = "z", TileConds = [.. conds], Tiles = [(0, 0), (1, 0)] };

    // ---- edits the analysis can see ----

    [Fact]
    public void Placing_a_part_moves_it()
    {
        var doc = Ship(Cat());
        var before = doc.AnalysisKey();
        new PlaceCommand(Fixtures.P("Floor", 5, 5)).Do(doc);
        Assert.NotEqual(before, doc.AnalysisKey());
    }

    [Fact]
    public void Removing_a_part_moves_it()
    {
        var doc = Ship(Cat());
        var before = doc.AnalysisKey();
        new RemoveCommand([doc.Placements[0]]).Do(doc);
        Assert.NotEqual(before, doc.AnalysisKey());
    }

    [Fact]
    public void Moving_a_part_moves_it()
    {
        var doc = Ship(Cat());
        var before = doc.AnalysisKey();
        new MoveCommand([doc.Placements[4]], 3, 3).Do(doc);
        Assert.NotEqual(before, doc.AnalysisKey());
    }

    [Fact]
    public void Rotating_a_part_moves_it()
    {
        var doc = Ship(Cat());
        var before = doc.AnalysisKey();
        new RotateCommand(doc, doc.Placements[4], 90).Do(doc);
        Assert.NotEqual(before, doc.AnalysisKey());
    }

    /// <summary>Two designs holding the same parts in a different order are not the same design: which of two
    /// primary ports bounds construction is settled by registration order.</summary>
    [Fact]
    public void The_order_parts_were_registered_in_counts()
    {
        var cat = Cat();
        var a = Fixtures.Doc(cat, Fixtures.P("Widget", 0, 0), Fixtures.P("Gadget", 1, 0));
        var b = Fixtures.Doc(cat, Fixtures.P("Gadget", 1, 0), Fixtures.P("Widget", 0, 0));
        Assert.NotEqual(a.AnalysisKey(), b.AnalysisKey());
    }

    /// <summary>A forbid zone is folded in even though the scan is handed a snapshot that carries no zones, so
    /// teaching it to read them cannot leave this reporting "unchanged".</summary>
    [Fact]
    public void Painting_a_forbid_zone_moves_it()
    {
        var doc = Ship(Cat());
        var before = doc.AnalysisKey();
        new CreateZoneCommand(Zone(ShipZone.CondForbid)).Do(doc);
        Assert.NotEqual(before, doc.AnalysisKey());

        var painted = doc.AnalysisKey();
        new SetZoneTilesCommand(doc.Zones[0], doc.Zones[0].Tiles, [(0, 0), (1, 0), (2, 0)]).Do(doc);
        Assert.NotEqual(painted, doc.AnalysisKey());
    }

    // ---- edits it cannot ----

    [Fact]
    public void Renaming_a_part_leaves_it()
    {
        var doc = Ship(Cat());
        var before = doc.AnalysisKey();
        new SetCustomNameCommand(doc.Placements[3], null, "Spares").Do(doc);
        Assert.Equal(before, doc.AnalysisKey());
    }

    [Fact]
    public void Filling_a_container_leaves_it()
    {
        var doc = Ship(Cat());
        var before = doc.AnalysisKey();
        var box = doc.Placements[3];
        new SetCargoCommand(box, box.Cargo, [new CargoItem("cargo-1", "Widget", null, false, [])]).Do(doc);
        Assert.Equal(before, doc.AnalysisKey());
    }

    [Fact]
    public void Nudging_the_z_order_leaves_it()
    {
        var doc = Ship(Cat());
        var before = doc.AnalysisKey();
        new SetZOrderCommand([new ZOrder.BiasChange(new RenderItem(doc.Placements[4], null), 0, 1)], "raise").Do(doc);
        Assert.Equal(before, doc.AnalysisKey());
    }

    [Fact]
    public void Painting_condition_leaves_it()
    {
        var doc = Ship(Cat());
        var before = doc.AnalysisKey();
        new SetConditionCommand(doc.Placements[2], null, 0.4).Do(doc);
        Assert.Equal(before, doc.AnalysisKey());
    }

    [Fact]
    public void Dropping_an_item_on_the_deck_leaves_it()
    {
        var doc = Ship(Cat());
        var before = doc.AnalysisKey();
        new PlaceLooseCommand(new LooseObject { DefName = "Widget", X = 1, Y = 0 }).Do(doc);
        Assert.Equal(before, doc.AnalysisKey());
    }

    [Fact]
    public void Wiring_two_devices_leaves_it()
    {
        var doc = Ship(Cat());
        var before = doc.AnalysisKey();
        new AddLinkCommand(new DeviceLink(doc.Placements[4].Id, doc.Placements[3].Id)).Do(doc);
        Assert.Equal(before, doc.AnalysisKey());
    }

    /// <summary>Haul and Barter zones are display-and-export state; only Forbid feeds the walk analysis.</summary>
    [Fact]
    public void Painting_a_haul_zone_leaves_it()
    {
        var doc = Ship(Cat());
        var before = doc.AnalysisKey();
        new CreateZoneCommand(Zone(ShipZone.CondHaul)).Do(doc);
        Assert.Equal(before, doc.AnalysisKey());
    }

    /// <summary>Undo has to put the key back where it was, or the editor would skip the re-analysis of a design
    /// it has just changed back.</summary>
    [Fact]
    public void Undo_puts_it_back()
    {
        var doc = Ship(Cat());
        var before = doc.AnalysisKey();
        var move = new MoveCommand([doc.Placements[4]], 3, 3);
        move.Do(doc);
        move.Undo(doc);
        Assert.Equal(before, doc.AnalysisKey());
    }

    /// <summary>A snapshot is what the analysis actually reads, so it must key the same as the design it came
    /// from — otherwise the skip would be comparing against something the scan never saw.</summary>
    [Fact]
    public void A_snapshot_keys_the_same_as_its_source()
    {
        var doc = Ship(Cat());
        Assert.Equal(doc.AnalysisKey(), doc.Snapshot().AnalysisKey());
    }
}
