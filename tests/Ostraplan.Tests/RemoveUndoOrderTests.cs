using System.Linq;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// Undoing a deletion must put the part back <b>where it was in the document</b>, not on the end.
///
/// <para>Document order is not presentation. The build-order check sweeps the placement list in it
/// (<see cref="ProblemScan"/> orders by build rank with a stable sort, so within a rank the list decides), and
/// <see cref="ShipExport"/> writes <c>aItems</c> in it, which <c>DockCheck.FromDocument</c> and the device-link
/// indices both read back (see <see cref="ExportItemOrderTests"/>). So a part re-added at the end is a different
/// design from the one that was deleted, in the file as well as on screen.</para>
///
/// <para>Reported as a bartop with a stool in front of it: legal as built, deleted, and Ctrl+Z brought back a
/// blocking warning that had never been there. The bartop keeps its access tile clear, so it has to be built
/// before the stool, and the sweep never un-places anything — once the stool went down first, the bartop could
/// not be built in any order the sweep could reach (#66).</para>
/// </summary>
public class RemoveUndoOrderTests
{
    private const string B = "Blank";

    /// <summary>A 3x3 socket ring (the mask a 1x1 part is tested against) with <paramref name="cells"/> set.</summary>
    private static string[] Ring(params (int Idx, string Loot)[] cells)
    {
        var ring = Enumerable.Repeat(B, 9).ToArray();
        foreach (var (idx, loot) in cells) ring[idx] = loot;
        return ring;
    }

    // Bartop: keeps its own tile and the tile in FRONT of it (ring index 7, due south) clear of obstruction —
    // the access tile a customer stands on. Stool: an ordinary fixture that guards only its own tile.
    // The airlock is here because a ship without one is a Blocking problem that returns before the legality
    // sweep ever runs, so the scan would answer about the wrong thing. Geometry mirrors ProblemScanTests'.
    private static Catalog Cat() => new Fixtures()
        .Trig(ProblemScan.DocksysTrigger, reqs: ["IsDockSys", "IsInstalled"])
        .Part("Dock", w: 7, h: 2, category: "HULL", startingConds: ["IsDockSys", "IsInstalled"],
            mapPoints: new Dictionary<string, (double X, double Y)> { ["DockA"] = (0, 8), ["DockB"] = (0, 24) })
        .Loot("Obstruction", "IsObstruction")
        .Part("Bartop", tileConds: ["IsFixture", "IsObstruction"], category: "FURN",
            startingConds: ["IsInstalled"], forbids: Ring((4, "Obstruction"), (7, "Obstruction")))
        .Part("Stool", tileConds: ["IsFixture", "IsObstruction"], category: "FURN",
            startingConds: ["IsInstalled"], forbids: Ring((4, "Obstruction")))
        .Build();

    private static int BlockingCount(ShipDocument doc, Catalog cat) =>
        ProblemScan.Scan(doc, cat).Count(p => p.Severity == ProblemSeverity.Blocking);

    [Fact]
    public void Undoing_a_deletion_puts_the_part_back_at_its_own_index()
    {
        var cat = Cat();
        var doc = new ShipDocument(cat);
        var bartop = new Placement { DefName = "Bartop", X = 2, Y = 3 };
        var stool = new Placement { DefName = "Stool", X = 2, Y = 4 };
        new PlaceCommand(bartop).Do(doc);
        new PlaceCommand(stool).Do(doc);

        var cmd = new RemoveCommand([bartop]);
        cmd.Do(doc);
        cmd.Undo(doc);

        Assert.Equal([bartop, stool], doc.Placements);
    }

    [Fact]
    public void A_bartop_built_before_its_stool_survives_a_delete_and_undo()
    {
        var cat = Cat();
        var doc = new ShipDocument(cat);
        var bartop = new Placement { DefName = "Bartop", X = 2, Y = 3 };
        var stool = new Placement { DefName = "Stool", X = 2, Y = 4 };
        new PlaceCommand(new Placement { DefName = "Dock", X = 0, Y = 0 }).Do(doc);
        new PlaceCommand(bartop).Do(doc);
        new PlaceCommand(stool).Do(doc);

        // The guard against a vacuous test: built in this order the pair is legal, which is the whole premise.
        Assert.Equal(0, BlockingCount(doc, cat));

        var cmd = new RemoveCommand([bartop]);
        cmd.Do(doc);
        cmd.Undo(doc);

        Assert.Equal(0, BlockingCount(doc, cat));
    }

    [Fact]
    public void A_batch_deletion_restores_every_index()
    {
        var cat = Cat();
        var doc = new ShipDocument(cat);
        var parts = new[]
        {
            new Placement { DefName = "Stool", X = 0, Y = 0 },
            new Placement { DefName = "Stool", X = 1, Y = 0 },
            new Placement { DefName = "Stool", X = 2, Y = 0 },
            new Placement { DefName = "Stool", X = 3, Y = 0 },
        };
        foreach (var p in parts) new PlaceCommand(p).Do(doc);

        // The two in the middle, so the survivors sit on either side and both edges of the gap are exercised.
        var cmd = new RemoveCommand([parts[1], parts[2]]);
        cmd.Do(doc);
        Assert.Equal([parts[0], parts[3]], doc.Placements);

        cmd.Undo(doc);
        Assert.Equal(parts, doc.Placements);
    }

    [Fact]
    public void Redo_then_undo_lands_in_the_same_place_again()
    {
        var cat = Cat();
        var doc = new ShipDocument(cat);
        var parts = new[]
        {
            new Placement { DefName = "Stool", X = 0, Y = 0 },
            new Placement { DefName = "Stool", X = 1, Y = 0 },
            new Placement { DefName = "Stool", X = 2, Y = 0 },
        };
        foreach (var p in parts) new PlaceCommand(p).Do(doc);

        var stack = new CommandStack();
        stack.Push(doc, new RemoveCommand([parts[1]]));
        stack.Undo(doc);
        stack.Redo(doc);
        stack.Undo(doc);

        Assert.Equal(parts, doc.Placements);
    }

    [Fact]
    public void Undoing_a_deck_item_deletion_puts_it_back_at_its_own_index()
    {
        var cat = Cat();
        var doc = new ShipDocument(cat);
        var items = new[]
        {
            new LooseObject { DefName = "Stool", X = 0, Y = 0 },
            new LooseObject { DefName = "Stool", X = 1, Y = 0 },
            new LooseObject { DefName = "Stool", X = 2, Y = 0 },
        };
        foreach (var o in items) new PlaceLooseCommand(o).Do(doc);

        var cmd = new RemoveLooseCommand(items[1]);
        cmd.Do(doc);
        cmd.Undo(doc);

        Assert.Equal(items, doc.LooseObjects);
    }
}
