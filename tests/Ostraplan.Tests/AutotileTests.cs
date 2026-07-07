using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// Autotile connectivity honours each sprite-sheet trigger's <c>bAND</c>. Conduits
/// (<c>TIsConduitSprite</c>, an OR of IsPowerConduit/Switch/Jack) must connect to a conduit
/// neighbour and render as a run; before the bAND fix they required all three conds at once
/// (never true) and every conduit rendered as an isolated junction. No-ops without the install.
/// </summary>
public class AutotileTests
{
    [SkippableFact]
    public void Conduits_connect_by_OR_and_autotile_as_a_run()
    {
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.ContainsKey("ItmConduit01")) return;

        var doc = new ShipDocument(g.Catalog);
        void P(int x, int y) => new PlaceCommand(new Placement { DefName = "ItmConduit01", X = x, Y = y }).Do(doc);

        P(0, 0);   // lone conduit — no neighbours
        Assert.Equal(0, Autotile.MaskAt(doc.Conds, "TIsConduitSprite", 0, 0));

        P(5, 0); P(6, 0); P(7, 0);   // a horizontal run
        Assert.Equal(Autotile.Mask(false, true, true, false), Autotile.MaskAt(doc.Conds, "TIsConduitSprite", 6, 0));   // W+E (through-run)
        Assert.Equal(Autotile.Mask(false, false, true, false), Autotile.MaskAt(doc.Conds, "TIsConduitSprite", 5, 0));  // E end
        Assert.NotEqual(0, Autotile.MaskAt(doc.Conds, "TIsConduitSprite", 7, 0));                                      // W end (connected, not isolated)
    }

    [SkippableFact]
    public void Walls_still_connect_by_single_AND_req()
    {
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.ContainsKey("ItmWall1x1")) return;

        var doc = new ShipDocument(g.Catalog);
        void P(int x, int y) => new PlaceCommand(new Placement { DefName = "ItmWall1x1", X = x, Y = y }).Do(doc);
        P(0, 0); P(1, 0);

        Assert.Equal(Autotile.Mask(false, false, true, false), Autotile.MaskAt(doc.Conds, "TIsWall", 0, 0));   // E neighbour
        Assert.Equal(Autotile.Mask(false, true, false, false), Autotile.MaskAt(doc.Conds, "TIsWall", 1, 0));   // W neighbour
    }
}
