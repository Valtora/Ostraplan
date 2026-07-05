using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>"Replace with…" compatibility (same layer + footprint) and the remove+place swap.
/// Needs real defs, so these no-op without the install.</summary>
public class ReplaceOpsTests
{
    private const string Wall = "ItmWall1x1";

    private static PartDef? OneByOne(Catalog cat, int layer) =>
        cat.Parts.FirstOrDefault(p => cat.RenderLayer(p) == layer && p.Item is { Width: 1, Height: 1 });

    private static Placement Place(ShipDocument doc, string def, int x, int y)
    {
        var p = new Placement { DefName = def, X = x, Y = y };
        new PlaceCommand(p).Do(doc);
        return p;
    }

    [Fact]
    public void Common_class_is_null_when_layers_differ()
    {
        if (TestData.Game is not { } g || !g.Catalog.ByDefName.ContainsKey(Wall)) return;
        if (OneByOne(g.Catalog, Catalog.LayerFloor) is not { } floor) return;

        var doc = new ShipDocument(g.Catalog);
        var w = Place(doc, Wall, 0, 0);
        var f = Place(doc, floor.DefName, 1, 0);

        Assert.Null(ReplaceOps.CommonClass(doc, [w, f]));         // wall + floor: no shared class
        Assert.NotNull(ReplaceOps.CommonClass(doc, [w]));         // a lone wall does
    }

    [Fact]
    public void Compatible_targets_share_layer_and_footprint_only()
    {
        if (TestData.Game is not { } g || !g.Catalog.ByDefName.ContainsKey(Wall)) return;
        if (OneByOne(g.Catalog, Catalog.LayerFloor) is not { } floor) return;

        var doc = new ShipDocument(g.Catalog);
        var f = Place(doc, floor.DefName, 0, 0);
        var cls = ReplaceOps.CommonClass(doc, [f]);
        Assert.NotNull(cls);

        var targets = ReplaceOps.CompatibleTargets(g.Catalog, cls!.Value);
        Assert.NotEmpty(targets);
        // every candidate is a floor of the same 1×1 footprint — never a wall, never a fixture
        Assert.All(targets, t =>
        {
            Assert.Equal(Catalog.LayerFloor, g.Catalog.RenderLayer(t));
            Assert.Equal((1, 1), (t.Item.Width, t.Item.Height));
        });
        Assert.DoesNotContain(targets, t => t.DefName == Wall);
    }

    [Fact]
    public void Swap_preserves_position_and_is_one_step()
    {
        if (TestData.Game is not { } g || !g.Catalog.ByDefName.ContainsKey(Wall)) return;
        var doc = new ShipDocument(g.Catalog);
        var a = Place(doc, Wall, 0, 0);
        var b = Place(doc, Wall, 1, 0);

        var cls = ReplaceOps.CommonClass(doc, [a, b]);
        if (cls is null) return;
        // a different buildable wall of the same class to swap to
        var other = ReplaceOps.CompatibleTargets(g.Catalog, cls.Value).FirstOrDefault(t => t.DefName != Wall);
        if (other is null) return;

        var swap = ReplaceOps.BuildSwap(doc, [a, b], other.DefName);
        Assert.NotNull(swap);

        var changed = 0;
        doc.Changed += () => changed++;
        swap!.Value.Cmd.Do(doc);

        Assert.Equal(1, changed);                                  // one coalesced notification
        Assert.Equal(2, swap.Value.New.Count);
        Assert.DoesNotContain(a, doc.Placements);
        Assert.All(swap.Value.New, p => Assert.Equal(other.DefName, p.DefName));
        var coords = swap.Value.New.Select(p => (p.X, p.Y)).OrderBy(c => c.X).ToList();
        Assert.Equal((0, 0), coords[0]);
        Assert.Equal((1, 0), coords[1]);
    }
}
