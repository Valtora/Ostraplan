using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The double-click magic-wand: 4-connected flood over same-def 1×1 tiles. Needs the game
/// data for real 1×1 defs, so these no-op on a machine without the install.
/// </summary>
public class FloodSelectTests
{
    private const string Wall = "ItmWall1x1";

    private static ShipDocument? Doc(out string other)
    {
        other = "";
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.ContainsKey(Wall)) return null;
        // any other 1×1 buildable def, to prove the flood stops at a different type
        var alt = g.Catalog.Parts.FirstOrDefault(p =>
            p.DefName != Wall && GridMath.Size(p.Item.Width, p.Item.Height, 0) == (1, 1));
        if (alt is null) return null;
        other = alt.DefName;
        return new ShipDocument(g.Catalog);
    }

    private static Placement Place(ShipDocument doc, string def, int x, int y)
    {
        var p = new Placement { DefName = def, X = x, Y = y };
        new PlaceCommand(p).Do(doc);
        return p;
    }

    [SkippableFact]
    public void Floods_a_connected_L_of_the_same_def()
    {
        if (Doc(out _) is not { } doc) return;

        // an L: (0,0)-(2,0) along x, then (2,1)-(2,2) down
        var seed = Place(doc, Wall, 0, 0);
        Place(doc, Wall, 1, 0);
        Place(doc, Wall, 2, 0);
        Place(doc, Wall, 2, 1);
        Place(doc, Wall, 2, 2);

        var region = FloodSelect.Collect(doc, seed);
        Assert.Equal(5, region.Count);
        Assert.Contains(seed, region);
    }

    [SkippableFact]
    public void Stops_at_a_diagonal_gap()
    {
        if (Doc(out _) is not { } doc) return;

        // (0,0) and (1,1) touch only diagonally — 4-connectivity does not bridge them
        var seed = Place(doc, Wall, 0, 0);
        Place(doc, Wall, 1, 1);

        var region = FloodSelect.Collect(doc, seed);
        Assert.Single(region);
        Assert.Same(seed, region[0]);
    }

    [SkippableFact]
    public void Does_not_cross_into_a_different_def()
    {
        if (Doc(out var other) is not { } doc) return;

        var seed = Place(doc, Wall, 0, 0);
        Place(doc, Wall, 1, 0);
        var barrier = Place(doc, other, 2, 0);   // adjacent but a different def
        Place(doc, Wall, 3, 0);                  // same def, but only reachable through the barrier

        var region = FloodSelect.Collect(doc, seed);
        Assert.Equal(2, region.Count);
        Assert.DoesNotContain(barrier, region);
    }

    [SkippableFact]
    public void A_multitile_seed_returns_just_itself()
    {
        var g = TestData.RequireGame();
        var multi = g.Catalog.Parts.FirstOrDefault(p => GridMath.Size(p.Item.Width, p.Item.Height, 0) is var (w, h) && (w > 1 || h > 1));
        if (multi is null) return;

        var doc = new ShipDocument(g.Catalog);
        var seed = Place(doc, multi.DefName, 0, 0);
        var region = FloodSelect.Collect(doc, seed);
        Assert.Single(region);
        Assert.Same(seed, region[0]);
    }
}
