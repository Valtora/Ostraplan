using System.Collections.Generic;
using System.Linq;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The loose placement law: what the cursor may lay on the deck, judged over an item's whole <b>footprint</b>,
/// and what it deliberately leaves alone in a design that arrived rather than being authored here.
///
/// <para>Game-free. The shapes and masks here are the ones the real data carries — a loose def paints
/// <c>IsItemTile</c> across its footprint and forbids <c>IsFixture</c> / <c>IsObstruction</c> / <c>IsItemTile</c>
/// over the same cells — so what is exercised is the rule rather than any particular item.</para>
/// </summary>
public class LoosePlacementTests
{
    private const string Floor = "Floor";
    private const string Bunk = "Bunk";
    private const string Antenna = "Antenna";     // 1x4, the shape that surfaced the fault
    private const string Ration = "Ration";       // 1x1
    private const string Ghost = "Ghost";         // a def with no masks at all (a cooverlay-only item)

    /// <summary>The per-cell forbid mask a loose def carries: the loot on every footprint cell of the (W+2)x(H+2)
    /// ring, "Blank" on the border. This is the shape <c>Item.CheckFit</c> reads.</summary>
    private static string[] FootprintMask(int w, int h, string loot)
    {
        var mask = new string[(w + 2) * (h + 2)];
        for (var r = 0; r < h + 2; r++)
            for (var c = 0; c < w + 2; c++)
                mask[r * (w + 2) + c] = r > 0 && r <= h && c > 0 && c <= w ? loot : "Blank";
        return mask;
    }

    private static Catalog Cat() => new Fixtures()
        .Floor(Floor)
        // TILItemForbids, verbatim: what the game refuses to put a loose item on top of
        .Loot("ItemForbids", "IsFixture", "IsObstruction", "IsItemTile")
        .Part(Bunk, w: 1, h: 2, tileConds: ["IsFixture", "IsObstruction"], category: "FURN")
        .Part(Antenna, w: 1, h: 4, tileConds: ["IsItemTile"], forbids: FootprintMask(1, 4, "ItemForbids"))
        .Part(Ration, tileConds: ["IsItemTile"], forbids: FootprintMask(1, 1, "ItemForbids"))
        .Part(Ghost)
        .Build();

    /// <summary>A design with a floored rectangle, which is the deck everything below is laid onto.</summary>
    private static ShipDocument Deck(int w = 8, int h = 8)
    {
        var doc = new ShipDocument(Cat());
        using (doc.SuspendChanged())
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    new PlaceCommand(new Placement { DefName = Floor, X = x, Y = y }).Do(doc);
        return doc;
    }

    private static LooseObject Drop(ShipDocument doc, string def, int x, int y, int rot = 0)
    {
        var o = new LooseObject { DefName = def, X = x, Y = y, Rot = rot };
        new PlaceLooseCommand(o).Do(doc);
        return o;
    }

    private static PartDef Def(ShipDocument doc, string name) => doc.Catalog.Lookup(name)!;

    // ---- the footprint is the unit ----

    [Fact]
    public void A_deck_item_holds_every_tile_of_its_footprint()
    {
        var doc = Deck();
        var antenna = Drop(doc, Antenna, 2, 2);

        Assert.Same(antenna, doc.LooseAt(2, 2));   // the anchor
        Assert.Same(antenna, doc.LooseAt(2, 5));   // three tiles down, still the same item
        Assert.Null(doc.LooseAt(2, 6));            // one past the end
        Assert.Single(doc.LooseObjects);           // covering four tiles is still one item
    }

    [Fact]
    public void Rotating_a_deck_item_moves_which_tiles_it_holds()
    {
        var doc = Deck();
        var antenna = Drop(doc, Antenna, 2, 2);
        new SetLoosePosesCommand([(antenna, 2, 2, 90)]).Do(doc);

        Assert.Null(doc.LooseAt(2, 5));            // the tiles it used to reach down onto are free
        Assert.Same(antenna, doc.LooseAt(5, 2));   // and it reaches east instead
    }

    [Fact]
    public void An_item_may_not_be_laid_across_one_that_is_already_there()
    {
        var doc = Deck();
        Drop(doc, Antenna, 2, 2);   // covers (2,2)..(2,5)

        // Anchored two tiles below the first one's anchor, so the anchors differ and only the footprints collide.
        // That is the case an anchor-only law allowed, and it put two antennae half through each other.
        var fit = LoosePlacement.Check(doc, Def(doc, Antenna), 2, 4, 0);

        Assert.False(fit.Ok);
        Assert.Equal("another deck item is already there", fit.Reason);
        Assert.Equal([(2, 4), (2, 5)], fit.FailedCells.OrderBy(c => c.Y).ToList());
    }

    [Fact]
    public void An_item_may_not_be_laid_over_a_fixture()
    {
        // The game's own rule: every loose def forbids IsFixture / IsObstruction over its footprint, so a crew
        // cannot drop a thing onto a bunk. The bunk is 1x2 at (4,4), and the antenna reaches it from three tiles up.
        var doc = Deck();
        new PlaceCommand(new Placement { DefName = Bunk, X = 4, Y = 4 }).Do(doc);

        var fit = LoosePlacement.Check(doc, Def(doc, Antenna), 4, 1, 0);   // (4,1)..(4,4)

        Assert.False(fit.Ok);
        Assert.Equal("tile is already occupied", fit.Reason);
        Assert.Contains((4, 4), fit.FailedCells);

        Assert.True(LoosePlacement.Check(doc, Def(doc, Antenna), 4, 0, 0).Ok);   // (4,0)..(4,3), stops short of it
    }

    [Fact]
    public void Facing_decides_whether_a_pose_fits()
    {
        // A row of bunks down the column at x=1. Upright, a 1x4 antenna anchored beside them runs straight into
        // one; turned, it lies across the open deck. Under the old anchor-only law both answered the same, which
        // is the other half of "regardless of rotation".
        var doc = Deck();
        new PlaceCommand(new Placement { DefName = Bunk, X = 1, Y = 3 }).Do(doc);

        Assert.False(LoosePlacement.Check(doc, Def(doc, Antenna), 1, 0, 0).Ok);   // (1,0)..(1,3) meets the bunk
        Assert.True(LoosePlacement.Check(doc, Def(doc, Antenna), 1, 0, 90).Ok);   // (1,0)..(4,0) is clear
    }

    [Fact]
    public void A_def_with_no_masks_still_gets_one_item_per_tile()
    {
        // Plenty of items are known to the game only as a cooverlay, so they carry no socket masks and the law
        // would have nothing to say about them. One per tile is Ostraplan's own invariant and holds regardless.
        var doc = Deck();
        Drop(doc, Ghost, 1, 1);

        Assert.False(LoosePlacement.Check(doc, Def(doc, Ghost), 1, 1, 0).Ok);
        Assert.True(LoosePlacement.Check(doc, Def(doc, Ghost), 2, 1, 0).Ok);
    }

    // ---- re-testing an item that is already down ----

    [Fact]
    public void An_item_being_moved_does_not_fail_against_where_it_currently_is()
    {
        var doc = Deck();
        var antenna = Drop(doc, Antenna, 2, 2);

        // Both the tile index and the deck condition layer have to exempt it, or nudging an item one tile is
        // refused by the copy of itself it is standing on.
        Assert.True(LoosePlacement.Check(doc, Def(doc, Antenna), 2, 3, 0, self: antenna).Ok);
        Assert.True(LoosePlacement.Check(doc, Def(doc, Antenna), 2, 2, 90, self: antenna).Ok);
        Assert.False(LoosePlacement.Check(doc, Def(doc, Antenna), 2, 3, 0).Ok);   // without the exemption it is blocked
    }

    [Fact]
    public void Taking_an_item_away_releases_its_tiles_and_its_conditions()
    {
        var doc = Deck();
        var antenna = Drop(doc, Antenna, 2, 2);
        new RemoveLooseCommand(antenna).Do(doc);

        Assert.Null(doc.LooseAt(2, 5));
        Assert.Null(doc.LooseConds.At(2, 5));   // the IsItemTile it painted is gone with it
        Assert.True(LoosePlacement.Check(doc, Def(doc, Antenna), 2, 2, 0).Ok);
    }

    [Fact]
    public void Filing_the_same_item_twice_does_not_count_its_conditions_twice()
    {
        // The mirror of RemoveLoose's stale-undo guard. Without it a repeated place leaves IsItemTile at 2 on every
        // tile the item covers, one remove takes it to 1, and the tiles stay blocked with nothing on them.
        var doc = Deck();
        var antenna = new LooseObject { DefName = Antenna, X = 2, Y = 2 };
        var place = new PlaceLooseCommand(antenna);
        place.Do(doc);
        place.Do(doc);

        Assert.Single(doc.LooseObjects);
        new RemoveLooseCommand(antenna).Do(doc);

        Assert.Empty(doc.LooseObjects);
        Assert.Null(doc.LooseConds.At(2, 2));
        Assert.True(LoosePlacement.Check(doc, Def(doc, Antenna), 2, 2, 0).Ok);
    }

    [Fact]
    public void Undoing_a_move_puts_the_condition_layer_back_where_it_was()
    {
        var doc = Deck();
        var antenna = Drop(doc, Antenna, 2, 2);
        var stack = new CommandStack();

        stack.Push(doc, new SetLoosePosesCommand([(antenna, 5, 0, 0)]));
        Assert.Null(doc.LooseConds.At(2, 5));
        Assert.NotNull(doc.LooseConds.At(5, 3));

        stack.Undo(doc);
        Assert.NotNull(doc.LooseConds.At(2, 5));
        Assert.Null(doc.LooseConds.At(5, 3));
    }

    // ---- group transforms ----

    [Fact]
    public void A_group_move_is_refused_when_a_footprint_would_land_on_another_item()
    {
        var doc = Deck(w: 12, h: 12);
        var moving = Drop(doc, Antenna, 1, 1);    // (1,1)..(1,4)
        Drop(doc, Antenna, 1, 6);                 // (1,6)..(1,9)

        // Sliding the first one down three tiles puts its bottom cell on the second's top cell. Neither anchor
        // collides, so an anchor-only test waved it through and the two ended up overlapping by one tile.
        Assert.Null(LooseTransform.Poses(doc, [moving], o => (o.X, o.Y + 3, o.Rot)));
        Assert.NotNull(LooseTransform.Poses(doc, [moving], o => (o.X, o.Y + 1, o.Rot)));
    }

    [Fact]
    public void A_group_move_still_lets_a_cluster_slide_through_itself()
    {
        var doc = Deck(w: 12, h: 12);
        var a = Drop(doc, Antenna, 1, 0);   // (1,0)..(1,3)
        var b = Drop(doc, Antenna, 1, 4);   // (1,4)..(1,7)

        // b vacates the tile a wants, and the movers are exempt from each other's origins — so a column of items
        // packed end to end still shifts as one, footprints and all.
        var poses = LooseTransform.Poses(doc, [a, b], o => (o.X, o.Y + 1, o.Rot));
        Assert.NotNull(poses);
        new SetLoosePosesCommand(poses!).Do(doc);

        Assert.Same(a, doc.LooseAt(1, 1));
        Assert.Same(b, doc.LooseAt(1, 8));
        Assert.Equal(2, doc.LooseObjects.Count);
    }

    // ---- what arrives is left alone ----

    [Fact]
    public void A_design_may_carry_poses_the_cursor_would_not_author()
    {
        // The game's own ships do: Babak writes fifteen separate ItmPillAntibiotic01 objects at one position with
        // no aStack, and Ship.SpawnItems places a template's deck cargo with no fit check at all. So an import
        // brings in what it brings in — nothing is refused on the way through, and nothing is dropped by the tile
        // index to make one-per-tile true. Before it held a list per tile, fourteen of those fifteen pills were
        // lost on the way in.
        var doc = Deck();
        var first = Drop(doc, Ration, 1, 1);
        var second = Drop(doc, Ration, 1, 1);
        var across = Drop(doc, Antenna, 1, 1);

        Assert.Equal(3, doc.LooseObjects.Count);
        Assert.Equal([first, second, across], doc.LooseStackAt(1, 1));
        Assert.Same(across, doc.LooseAt(1, 1));            // the last one laid answers a hit test
        Assert.Same(across, doc.LooseAt(1, 4));            // and it still holds the rest of its own footprint
    }

    [Fact]
    public void An_item_lying_over_nothing_at_all_is_allowed()
    {
        // There is no floor requirement, because the game has none and its content leans on that: Station_Ground
        // lies regolith on a station exterior and Station_MTRS_Nuked strews 254 pieces of scrap over unfloored
        // wreckage. A rule that demanded deck underneath would refuse to author either.
        var doc = Deck(w: 4, h: 4);

        Assert.True(LoosePlacement.Check(doc, Def(doc, Ration), 20, 20, 0).Ok);
        Assert.True(LoosePlacement.Check(doc, Def(doc, Antenna), 1, 2, 0).Ok);   // (1,2)..(1,5), half off the deck
    }

    // ---- what deliberately still works ----

    [Fact]
    public void Structure_is_never_refused_for_standing_on_a_deck_item()
    {
        // The deck layer is kept out of Conds on purpose, so a wall built over a crate places. In game the crate
        // would be in the way; in a planner the ship is built before it is dressed, and rooms, airtightness and
        // the rating must not start reading what is lying on the floor.
        var doc = Deck();
        Drop(doc, Ration, 3, 3);

        Assert.True(CheckFit.Check(doc, Def(doc, Bunk), 3, 3, 0).Ok);
    }

    [Fact]
    public void The_deck_condition_layer_is_not_in_the_structural_one()
    {
        var doc = Deck();
        Drop(doc, Antenna, 2, 2);

        Assert.NotNull(doc.LooseConds.At(2, 2));
        Assert.DoesNotContain("IsItemTile", doc.Conds.At(2, 2) ?? new Dictionary<string, double>());
    }
}
