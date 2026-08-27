using System.Collections.Generic;
using System.Linq;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The order an export emits <c>aItems</c> in (#45). The game's <c>FindPointsOfImpact</c> judges a tile on its
/// FIRST part with health left and then breaks whether or not that part matched a trigger cond, so a wall sharing
/// a tile with a floor stops a missile only when the wall comes first. Ostraplan's own damage model asks about the
/// tile rather than its first part, and the export is what makes that answer true of the ship it writes.
/// </summary>
public class ExportTriggerOrderTests
{
    private static readonly IReadOnlyList<RoomSpecDef> NoSpecs = [];

    private static Catalog Cat() => new Fixtures()
        .Floor()
        .Wall()
        .Door()
        .Conduit()
        .ShipAttack("MissileAttack03", triggerConds: ["IsWall", "IsRigid", "IsPortal"])
        .Build();

    private static string[] EmittedOrder(ShipDocument doc, Catalog cat) =>
        ShipExport.Build(doc, cat, NoSpecs, "Test").Ship.AItems.Select(i => i.StrName).ToArray();

    [Fact]
    public void A_wall_laid_after_its_floor_is_still_emitted_first()
    {
        // The reported build order: deck the floors, then wall over them. In document order the floor leads, so
        // in game the missile would examine the floor, match nothing, break, and sail over a tile with a wall.
        var cat = Cat();
        var doc = Fixtures.Doc(cat);
        Fixtures.Place(doc, "Floor", 0, 0);
        Fixtures.Place(doc, "Wall", 0, 0);

        Assert.Equal(["Floor", "Wall"], doc.Placements.Select(p => p.DefName));
        Assert.Equal(["Wall", "Floor"], EmittedOrder(doc, cat));
    }

    [Fact]
    public void Every_shared_tile_puts_its_trigger_before_its_non_triggers()
    {
        // The guarantee stated over a whole deck rather than one tile: wherever a trigger part and a plain one
        // share a tile, the trigger is the earlier of the two in aItems.
        var cat = Cat();
        var doc = Fixtures.Doc(cat);
        for (var x = 0; x < 6; x++)
        {
            Fixtures.Place(doc, "Floor", x, 0);
            Fixtures.Place(doc, "Conduit", x, 0);
            if (x is 0 or 5) Fixtures.Place(doc, "Wall", x, 0);
        }

        var order = EmittedOrder(doc, cat);
        var triggers = order.Select((n, i) => (n, i)).Where(t => t.n == "Wall").Select(t => t.i);
        var plain = order.Select((n, i) => (n, i)).Where(t => t.n != "Wall").Select(t => t.i);
        Assert.True(triggers.Max() < plain.Min());
    }

    [Fact]
    public void A_door_counts_as_a_trigger_because_it_carries_IsPortal()
    {
        // IsPortal is one of the three the missiles declare, so a door in a wall run stops one exactly as the
        // wall does, and has to lead its own tile for the same reason.
        var cat = Cat();
        var doc = Fixtures.Doc(cat);
        Fixtures.Place(doc, "Floor", 1, 0);
        Fixtures.Place(doc, "Door", 1, 0);

        Assert.Equal(["Door", "Floor"], EmittedOrder(doc, cat));
    }

    [Fact]
    public void Parts_that_do_not_share_a_tile_keep_their_own_relative_order()
    {
        // The partition is stable, which is what leaves everything that reads emission order alone (docking-port
        // registration above all, see ProblemScan.BoundingPort).
        var cat = Cat();
        var doc = Fixtures.Doc(cat);
        Fixtures.Place(doc, "Wall", 0, 0);
        Fixtures.Place(doc, "Wall", 1, 0);
        Fixtures.Place(doc, "Wall", 2, 0);

        var order = EmittedOrder(doc, cat);
        Assert.Equal(["Wall", "Wall", "Wall"], order);

        // left to right as they were laid, whatever frame the export picks (it pads the grid by a tile)
        var xs = ShipExport.Build(doc, cat, NoSpecs, "Test").Ship.AItems.Select(i => i.FX).ToArray();
        Assert.Equal(xs.OrderBy(x => x), xs);
        Assert.Equal(3, xs.Distinct().Count());
    }

    [Fact]
    public void A_catalogue_with_no_attacks_leaves_the_order_untouched()
    {
        // Nothing declares a trigger, so there is no ordering to enforce and the document order stands. Keeps a
        // hand-built catalogue behaving like a real one rather than reordering on an empty rule.
        var cat = new Fixtures().Floor().Wall().Build();
        var doc = Fixtures.Doc(cat);
        Fixtures.Place(doc, "Floor", 0, 0);
        Fixtures.Place(doc, "Wall", 0, 0);

        Assert.Equal(["Floor", "Wall"], EmittedOrder(doc, cat));
    }
}
