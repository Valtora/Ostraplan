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

    [SkippableFact]
    public void Common_class_is_null_when_layers_differ()
    {
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.ContainsKey(Wall)) return;
        if (OneByOne(g.Catalog, Catalog.LayerFloor) is not { } floor) return;

        var doc = new ShipDocument(g.Catalog);
        var w = Place(doc, Wall, 0, 0);
        var f = Place(doc, floor.DefName, 1, 0);

        Assert.Null(ReplaceOps.CommonClass(doc, [w, f]));         // wall + floor: no shared class
        Assert.NotNull(ReplaceOps.CommonClass(doc, [w]));         // a lone wall does
    }

    [SkippableFact]
    public void Compatible_targets_share_layer_and_footprint_only()
    {
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.ContainsKey(Wall)) return;
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

    [SkippableFact]
    public void Swap_preserves_position_and_is_one_step()
    {
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.ContainsKey(Wall)) return;
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

    [SkippableFact]
    public void Containers_are_never_replaceable_or_a_replace_target()
    {
        // a container's inventory grid + cargo don't survive a def-change, so it's excluded from Replace on both
        // sides: it can't be the source (no common class) and never appears as a target (no re-skin INTO a container).
        var g = TestData.RequireGame();
        var container = g.Catalog.Parts.FirstOrDefault(p => p.IsContainer)
            ?? new[] { "ItmRack1x4", "ItmStorageBin3x102", "ItmRack2x2C01", "ItmLocker01" }
                .Select(d => g.Catalog.Lookup(d)).FirstOrDefault(p => p?.IsContainer == true);
        if (container is null) return;

        var doc = new ShipDocument(g.Catalog);
        var c = Place(doc, container.DefName, 0, 0);
        Assert.Null(ReplaceOps.CommonClass(doc, [c]));   // can't be the SOURCE of a replace

        var cls = (g.Catalog.RenderLayer(container), container.Item.Width, container.Item.Height);
        Assert.DoesNotContain(ReplaceOps.CompatibleTargets(g.Catalog, cls), t => t.IsContainer);   // never a TARGET
    }

    [SkippableFact]
    public void Swap_carries_the_originals_given_ness()
    {
        // A same-layer, same-footprint swap keeps the tiles' structural role, so it must preserve
        // given-ness — else re-skinning imported (given) structure would re-validate a valid ship
        // the game never re-checks. Given -> given, authored -> authored.
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.ContainsKey(Wall)) return;
        var doc = new ShipDocument(g.Catalog);
        var given = new Placement { DefName = Wall, X = 0, Y = 0, IsGiven = true };
        new PlaceCommand(given).Do(doc);
        var authored = Place(doc, Wall, 1, 0);   // IsGiven defaults false

        var cls = ReplaceOps.CommonClass(doc, [given]);
        if (cls is null) return;
        var other = ReplaceOps.CompatibleTargets(g.Catalog, cls.Value).FirstOrDefault(t => t.DefName != Wall);
        if (other is null) return;

        var fromGiven = ReplaceOps.BuildSwap(doc, [given], other.DefName);
        Assert.NotNull(fromGiven);
        Assert.All(fromGiven!.Value.New, p => Assert.True(p.IsGiven));

        var fromAuthored = ReplaceOps.BuildSwap(doc, [authored], other.DefName);
        Assert.NotNull(fromAuthored);
        Assert.All(fromAuthored!.Value.New, p => Assert.False(p.IsGiven));
    }

    [Fact]
    public void Sole_def_requires_every_part_to_match_exactly()
    {
        var a = new Placement { DefName = Wall, X = 0, Y = 0 };
        var b = new Placement { DefName = Wall, X = 1, Y = 0 };
        var other = new Placement { DefName = "ItmFloor1x1", X = 2, Y = 0 };

        Assert.Equal(Wall, ReplaceOps.SoleDef([a]));
        Assert.Equal(Wall, ReplaceOps.SoleDef([a, b]));   // same class AND same def
        Assert.Null(ReplaceOps.SoleDef([a, other]));      // differing defs, even if same class
        Assert.Null(ReplaceOps.SoleDef([]));
    }

    [SkippableFact]
    public void Find_and_replace_locates_every_matching_placement_and_swaps_them_all()
    {
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.ContainsKey(Wall)) return;
        var doc = new ShipDocument(g.Catalog);
        var a = Place(doc, Wall, 0, 0);
        var b = Place(doc, Wall, 1, 0);
        var elsewhereFloor = OneByOne(g.Catalog, Catalog.LayerFloor);
        if (elsewhereFloor is not null) Place(doc, elsewhereFloor.DefName, 5, 5);   // a different def, must not match

        var found = ReplaceOps.FindAll(doc, Wall);
        Assert.Equal(2, found.Count);
        Assert.Contains(a, found);
        Assert.Contains(b, found);

        var cls = ReplaceOps.CommonClass(doc, [a]);
        if (cls is null) return;
        var other = ReplaceOps.CompatibleTargets(g.Catalog, cls.Value).FirstOrDefault(t => t.DefName != Wall);
        if (other is null) return;

        var swap = ReplaceOps.BuildSwap(doc, found, other.DefName);
        Assert.NotNull(swap);
        swap!.Value.Cmd.Do(doc);
        Assert.DoesNotContain(a, doc.Placements);
        Assert.DoesNotContain(b, doc.Placements);
        Assert.All(swap.Value.New, p => Assert.Equal(other.DefName, p.DefName));
    }

    [SkippableFact]
    public void Find_and_replace_finds_locked_matches_but_swap_skips_them()
    {
        var g = TestData.RequireGame();
        var docksys = Catalog.PrimaryDocksysDef;
        if (!g.Catalog.ByDefName.ContainsKey(docksys)) return;

        var doc = new ShipDocument(g.Catalog);
        var locked = Place(doc, docksys, 0, 0);
        Assert.True(doc.IsLocked(locked));

        var found = ReplaceOps.FindAll(doc, docksys);
        Assert.Contains(locked, found);   // located...

        var cls = ReplaceOps.CommonClass(doc, [locked]);
        if (cls is null) return;
        var other = ReplaceOps.CompatibleTargets(g.Catalog, cls.Value).FirstOrDefault(t => t.DefName != docksys);
        if (other is null) return;

        var swap = ReplaceOps.BuildSwap(doc, found, other.DefName);
        Assert.Null(swap);   // ...but never swapped: the only match is locked
    }
}
