using System.Linq;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The order an export emits <c>aItems</c> in: document order, structure first and the deck items after all of it.
///
/// <para>Two things read that order back, so it is a guarantee rather than an accident. Device links are stored as
/// indices into <c>aItems</c>, and <c>DockCheck.FromDocument</c> replays the same order because an item whose cell
/// is already taken is dropped from the docking grid entirely.</para>
///
/// <para>It was briefly not document order. Up to game 1.0.0.16 a missile judged a tile on its first listed part
/// alone, so <c>ShipExport.TriggerFirst</c> emitted every trigger-carrying part ahead of every other one to keep a
/// wall laid over a floor able to stop one (#45). Game 1.0.0.17 tests the whole stack and the partition came back
/// out (§26).</para>
/// </summary>
public class ExportItemOrderTests
{
    private static Catalog Cat() => new Fixtures()
        .Floor()
        .Wall()
        .Conduit()
        .ShipAttack("MissileAttack03", triggerConds: ["IsWall", "IsRigid", "IsPortal"])
        .Build();

    [Fact]
    public void Parts_are_emitted_in_the_order_they_were_laid_down()
    {
        // The build order that used to be rearranged: deck the floor, then wall over it.
        var cat = Cat();
        var doc = Fixtures.Doc(cat);
        Fixtures.Place(doc, "Floor", 0, 0);
        Fixtures.Place(doc, "Wall", 0, 0);
        Fixtures.Place(doc, "Conduit", 0, 0);

        var emitted = ShipExport.Build(doc, cat, [], "Test").Ship.AItems.Select(i => i.StrName);

        Assert.Equal(doc.Placements.Select(p => p.DefName), emitted);
    }
}
