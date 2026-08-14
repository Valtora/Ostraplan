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

    /// <summary>
    /// A part is classed by the body it draws, not by its raw socket grid. The big cryogenic canisters are a 3×3
    /// machine ringed by two tiles of under-floor-only reservation, so they must swap with the plain 3×3 fixtures
    /// they visibly match — the bug behind "Find and Replace All offers three parts when it should offer twelve".
    /// </summary>
    [Fact]
    public void An_under_floor_apron_does_not_change_a_parts_swap_class()
    {
        var cat = new Fixtures()
            .Fixture("Tank", 3, 3)
            .Fixture("Canister", 3, 3, apron: 2)
            .Fixture("Bed", 2, 3)
            .Build();

        var tank = cat.Lookup("Tank")!;
        var canister = cat.Lookup("Canister")!;
        Assert.Equal((7, 7), (canister.Item.Width, canister.Item.Height));
        Assert.Equal((2, 2, 3, 3), cat.BodyBox(canister));   // body centred in the apron
        Assert.Equal((0, 0, 3, 3), cat.BodyBox(tank));       // no apron: body is the whole footprint

        var cls = cat.SwapClass(tank);
        Assert.Equal(cls, cat.SwapClass(canister));
        var targets = ReplaceOps.CompatibleTargets(cat, cls).Select(t => t.DefName).ToList();
        Assert.Equal(new[] { "Tank", "Canister" }, targets);   // and nothing of another size
    }

    /// <summary>
    /// The swap keeps the <b>body</b> on its tiles, so a part with an apron lands where the old one stood instead
    /// of jumping by the apron's width. Both directions, since the shift is signed.
    /// </summary>
    [Fact]
    public void Swapping_across_an_apron_keeps_the_body_where_it_was()
    {
        var cat = new Fixtures().Fixture("Tank", 3, 3).Fixture("Canister", 3, 3, apron: 2).Build();
        var doc = Fixtures.Doc(cat, Fixtures.P("Tank", 10, 10));
        var tank = doc.Placements[0];
        Assert.Equal((10, 10, 3, 3), doc.BodyBounds(tank));

        var swap = ReplaceOps.BuildSwap(doc, [tank], "Canister");
        Assert.NotNull(swap);
        swap!.Value.Cmd.Do(doc);
        var canister = swap.Value.New[0];
        Assert.Equal((8, 8), (canister.X, canister.Y));               // socket grid starts two tiles out...
        Assert.Equal((10, 10, 3, 3), doc.BodyBounds(canister));       // ...so the machine itself has not moved

        var back = ReplaceOps.BuildSwap(doc, [canister], "Tank");
        Assert.NotNull(back);
        back!.Value.Cmd.Do(doc);
        Assert.Equal((10, 10), (back.Value.New[0].X, back.Value.New[0].Y));
        Assert.Equal((10, 10, 3, 3), doc.BodyBounds(back.Value.New[0]));
    }

    /// <summary>The body offset is read at the placement's own rotation, so a rotated swap stays put too.</summary>
    [Fact]
    public void Swapping_a_rotated_part_keeps_the_body_where_it_was()
    {
        var cat = new Fixtures().Fixture("Pump", 2, 3).Fixture("Cryo", 2, 3, apron: 1).Build();
        var doc = Fixtures.Doc(cat, new Placement { DefName = "Pump", X = 4, Y = 4, Rot = 90 });
        var pump = doc.Placements[0];
        Assert.Equal((4, 4, 3, 2), doc.BodyBounds(pump));   // 2×3 turned on its side

        var swap = ReplaceOps.BuildSwap(doc, [pump], "Cryo");
        Assert.NotNull(swap);
        swap!.Value.Cmd.Do(doc);
        Assert.Equal(90, swap.Value.New[0].Rot);
        Assert.Equal((3, 3), (swap.Value.New[0].X, swap.Value.New[0].Y));
        Assert.Equal((4, 4, 3, 2), doc.BodyBounds(swap.Value.New[0]));
    }

    /// <summary>The real defs behind the report: the 7×7-socket canisters and the plain 3×3 fixtures are one class.</summary>
    [SkippableFact]
    public void The_big_canisters_swap_with_the_plain_3x3_fixtures()
    {
        var g = TestData.RequireGame();
        if (g.Catalog.Lookup("ItmCanisterLHe02") is not { } canister) return;
        Assert.Equal((7, 7), (canister.Item.Width, canister.Item.Height));
        Assert.Equal((2, 2, 3, 3), g.Catalog.BodyBox(canister));

        var doc = new ShipDocument(g.Catalog);
        var placed = Place(doc, canister.DefName, 20, 20);
        var cls = ReplaceOps.CommonClass(doc, [placed]);
        Assert.NotNull(cls);
        Assert.Equal((Catalog.LayerFixture, 3, 3), cls!.Value);

        var targets = ReplaceOps.CompatibleTargets(g.Catalog, cls.Value).Select(t => t.DefName).ToList();
        Assert.Contains("ItmCanisterLHe01", targets);          // the other canisters, as before
        Assert.Contains("ItmFusionMHDGenerator01", targets);   // and now the 3×3 machines it matches on the deck
        Assert.Contains("ItmSensorRadar02", targets);
        Assert.DoesNotContain(targets, t => t == "ItmFusionReactorCore01Off");   // 5×5 body: still a different class

        // the swap puts the generator's 3×3 body exactly where the canister's 3×3 body was
        var swap = ReplaceOps.BuildSwap(doc, [placed], "ItmFusionMHDGenerator01");
        Assert.NotNull(swap);
        swap!.Value.Cmd.Do(doc);
        Assert.Equal((22, 22), (swap.Value.New[0].X, swap.Value.New[0].Y));
        Assert.Equal((22, 22, 3, 3), doc.BodyBounds(swap.Value.New[0]));
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
