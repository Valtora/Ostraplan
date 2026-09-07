using System.Linq;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// "Build last" (#67): a design saying which part goes up after the others, for a sequence the build-order sweep
/// cannot find on its own.
///
/// <para>The sweep looks for <i>some</i> order that builds every part legally, but it never un-places anything, so
/// it cannot reach an order that needs a part laid down after something already standing where that part wants
/// clear space. A rack under an overhead bin is the reported case. Document order is the tie-break within a build
/// rank, so moving the part through the list is the whole mechanism, and it costs nothing elsewhere: the
/// <c>.oplan</c> and the export already write that order (see <see cref="ExportItemOrderTests"/>).</para>
/// </summary>
public class BuildOrderTests
{
    private const string B = "Blank";

    private static string[] Ring(params (int Idx, string Loot)[] cells)
    {
        var ring = Enumerable.Repeat(B, 9).ToArray();
        foreach (var (idx, loot) in cells) ring[idx] = loot;
        return ring;
    }

    // The reported shape, reduced. "Bin" is an overhead fitting that wants the tile below it clear when it goes
    // up; "Rack" is what stands there. Build the bin first and both fit, build the rack first and the bin never
    // can. Both are rank-3 fittings, so document order is the only thing that separates them.
    private static Catalog Cat() => new Fixtures()
        .Trig(ProblemScan.DocksysTrigger, reqs: ["IsDockSys", "IsInstalled"])
        .Part("Dock", w: 7, h: 2, category: "HULL", startingConds: ["IsDockSys", "IsInstalled"],
            mapPoints: new Dictionary<string, (double X, double Y)> { ["DockA"] = (0, 8), ["DockB"] = (0, 24) })
        .Loot("Obstruction", "IsObstruction")
        .Part("Bin", tileConds: ["IsFixture", "IsObstruction"], category: "FURN",
            startingConds: ["IsInstalled"], forbids: Ring((4, "Obstruction"), (7, "Obstruction")))
        .Part("Rack", tileConds: ["IsFixture", "IsObstruction"], category: "FURN",
            startingConds: ["IsInstalled"], forbids: Ring((4, "Obstruction")))
        .Build();

    private static int BlockingCount(ShipDocument doc, Catalog cat) =>
        ProblemScan.Scan(doc, cat).Count(p => p.Severity == ProblemSeverity.Blocking);

    private static ShipDocument Ship(Catalog cat, out Placement bin, out Placement rack)
    {
        var doc = new ShipDocument(cat);
        bin = new Placement { DefName = "Bin", X = 2, Y = 3 };
        rack = new Placement { DefName = "Rack", X = 2, Y = 4 };
        new PlaceCommand(new Placement { DefName = "Dock", X = 0, Y = 0 }).Do(doc);
        // The rack first, which is the order someone actually laying out a hold would work in, and the one the
        // sweep cannot rescue.
        new PlaceCommand(rack).Do(doc);
        new PlaceCommand(bin).Do(doc);
        return doc;
    }

    [Fact]
    public void The_sweep_cannot_order_a_fitting_that_needs_the_tile_below_it_clear()
    {
        // The premise. Without this the test below proves nothing.
        var cat = Cat();
        var doc = Ship(cat, out _, out _);
        Assert.Equal(1, BlockingCount(doc, cat));
    }

    [Fact]
    public void Building_the_rack_last_clears_it()
    {
        var cat = Cat();
        var doc = Ship(cat, out _, out var rack);

        new BuildLastCommand([rack]).Do(doc);

        Assert.Equal(0, BlockingCount(doc, cat));
    }

    [Fact]
    public void Undo_puts_the_part_back_in_the_order_it_had()
    {
        var cat = Cat();
        var doc = Ship(cat, out _, out var rack);
        var before = doc.Placements.ToList();

        var cmd = new BuildLastCommand([rack]);
        cmd.Do(doc);
        Assert.NotEqual(before, doc.Placements);

        cmd.Undo(doc);
        Assert.Equal(before, doc.Placements);
        Assert.Equal(1, BlockingCount(doc, cat));   // and the problem comes back with it
    }

    [Fact]
    public void A_batch_keeps_the_relative_order_it_already_had()
    {
        var cat = Cat();
        var doc = new ShipDocument(cat);
        var racks = Enumerable.Range(0, 5)
            .Select(i => new Placement { DefName = "Rack", X = i, Y = 9 })
            .ToArray();
        foreach (var r in racks) new PlaceCommand(r).Do(doc);

        // Two from the middle, named out of order to prove the command sorts them rather than trusting the caller.
        var cmd = new BuildLastCommand([racks[3], racks[1]]);
        cmd.Do(doc);

        Assert.Equal([racks[0], racks[2], racks[4], racks[1], racks[3]], doc.Placements);
        cmd.Undo(doc);
        Assert.Equal(racks, doc.Placements);
    }

    [Fact]
    public void The_draw_order_does_not_follow_the_build_order()
    {
        // The two answer different questions. A rack told to go up last must not climb out from under whatever is
        // drawn over it to say so, so SetBuildIndex leaves the insertion order RenderKey sorts on alone.
        var cat = Cat();
        var doc = Ship(cat, out var bin, out var rack);
        var drawnBefore = doc.DrawOrder().Select(i => i.Id).ToList();

        new BuildLastCommand([rack]).Do(doc);

        Assert.Equal(drawnBefore, doc.DrawOrder().Select(i => i.Id));
        Assert.Equal(bin, doc.Placements[1]);          // the build order did move
        Assert.Equal(rack, doc.Placements[^1]);
    }

    /// <summary>
    /// The other half of #67. Force place lifts the check the <i>cursor</i> makes and nothing else, so a part put
    /// down against the law is still reported, and reported as Blocking rather than softened to a warning. That is
    /// what was asked for: the toggle stops the tool refusing a placement, it does not bless it.
    ///
    /// <para>Nothing here needs the toggle, which is the point. The App-side gate only decides whether a
    /// <see cref="PlaceCommand"/> is built at all; once one is, the document holds an ordinary illegal part and
    /// <see cref="ProblemScan"/> judges it exactly as it judges one that arrived by any other route.</para>
    /// </summary>
    [Fact]
    public void A_part_placed_against_the_law_is_still_a_blocking_problem()
    {
        var cat = Cat();
        var doc = new ShipDocument(cat);
        new PlaceCommand(new Placement { DefName = "Dock", X = 0, Y = 0 }).Do(doc);
        new PlaceCommand(new Placement { DefName = "Rack", X = 2, Y = 4 }).Do(doc);
        // Straight on top of the rack, which the rack's own forbid mask refuses outright. No build order saves
        // this one, unlike the bin above: it is genuinely unbuildable and has to keep saying so.
        new PlaceCommand(new Placement { DefName = "Rack", X = 2, Y = 4 }).Do(doc);

        var problems = ProblemScan.Scan(doc, cat);
        Assert.Contains(problems, p => p.Severity == ProblemSeverity.Blocking);
        // …and Build last must not be a way to make it go quiet.
        new BuildLastCommand([doc.Placements[^1]]).Do(doc);
        Assert.Contains(ProblemScan.Scan(doc, cat), p => p.Severity == ProblemSeverity.Blocking);
    }

    [Fact]
    public void A_redo_lands_in_the_same_place_as_the_first_run()
    {
        var cat = Cat();
        var doc = Ship(cat, out _, out var rack);
        var stack = new CommandStack();

        stack.Push(doc, new BuildLastCommand([rack]));
        var after = doc.Placements.ToList();
        stack.Undo(doc);
        stack.Redo(doc);

        Assert.Equal(after, doc.Placements);
        Assert.Equal(0, BlockingCount(doc, cat));
    }
}
