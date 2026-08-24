using System;
using System.Collections.Generic;
using System.Linq;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// A Damage Brush stroke as the model runs it (<see cref="DamageStroke"/>) — one tile at a time, the way the
/// window feeds it as the mouse crosses the plan. <see cref="DamagePaintTests"/> covers the rules a single object
/// is painted by; this covers what a whole stroke does to a document, which is where a part that stops being
/// itself mid-stroke lives.
/// </summary>
public class DamageStrokeTests
{
    // A three-stage break chain, so a stroke that walked one stage too far would show it: Hull breaks into
    // HullDmg, which breaks into HullWreck, which breaks into nothing.
    private static Catalog Cat() => new Fixtures()
        .Part("Wall", startingConds: ["IsInstalled", "IsWall"], condValues: new Dictionary<string, double> { ["StatDamageMax"] = 30 })
        .Part("WallDmg", startingConds: ["IsInstalled", "IsWall"], condValues: new Dictionary<string, double> { ["StatDamageMax"] = 15 })
        .Part("Floor", startingConds: ["IsInstalled"], condValues: new Dictionary<string, double> { ["StatDamageMax"] = 20 })
        .Part("Hull", w: 2, startingConds: ["IsInstalled"], condValues: new Dictionary<string, double> { ["StatDamageMax"] = 40 })
        .Part("HullDmg", w: 2, startingConds: ["IsInstalled"], condValues: new Dictionary<string, double> { ["StatDamageMax"] = 20 })
        .Part("HullWreck", w: 2, startingConds: ["IsInstalled"], condValues: new Dictionary<string, double> { ["StatDamageMax"] = 10 })
        .Part("Strut", startingConds: ["IsInstalled"])
        .Part("Crate", startingConds: [], condValues: new Dictionary<string, double> { ["StatDamageMax"] = 10 })
        .BreakPair("Wall", "WallDmg")
        .BreakPair("Hull", "HullDmg")
        .BreakPair("HullDmg", "HullWreck")
        .Build();

    private static DamageStroke Stroke(ShipDocument doc) => new(doc, new Random(1));

    private static ConditionBrush Destroy => ConditionBrush.Fixed(0.0);

    // ---- the reported crash ----

    [Fact]
    public void Destroying_a_part_does_not_trip_the_tile_index_it_is_standing_in()
    {
        // Reported from the wild: painting an item down to nothing threw "Collection was modified; enumeration
        // operation may not execute". Breaking a part removes it and places its damaged form, and both halves
        // edit the same tile list the stroke was walking, so the crash was every break rather than an odd one.
        var doc = new ShipDocument(Cat());
        doc.Add(new Placement { DefName = "Wall", X = 1, Y = 1 });

        var (painted, skipped) = Stroke(doc).PaintTile(1, 1, Destroy, includeLoose: true);

        Assert.Equal(1, painted);
        Assert.Equal(0, skipped);
        Assert.Equal("WallDmg", Assert.Single(doc.Placements).DefName);
    }

    [Fact]
    public void A_whole_stack_on_the_tile_is_painted_even_when_one_of_them_breaks()
    {
        // The floor under the wall must still take its roll: the break interrupted the walk before the fix, so
        // whatever lay under the part that broke was left untouched.
        var doc = new ShipDocument(Cat());
        doc.Add(new Placement { DefName = "Floor", X = 0, Y = 0 });
        doc.Add(new Placement { DefName = "Wall", X = 0, Y = 0 });

        var (painted, _) = Stroke(doc).PaintTile(0, 0, Destroy, includeLoose: false);

        Assert.Equal(2, painted);
        Assert.Contains(doc.Placements, p => p.DefName == "WallDmg");
        Assert.Equal(0.0, doc.Placements.Single(p => p.DefName == "Floor").Condition!.Value, 9);
    }

    // ---- what the broken form arrives as ----

    [Fact]
    public void The_broken_form_starts_its_own_life_rather_than_inheriting_the_wear_that_broke_it()
    {
        // Paint.Resolve hands back a null condition with the broken def and means it: the new part has its own
        // (smaller) damage pool, and a restate carries the old painted condition across, which is right for an
        // uninstall and wrong for a break.
        var doc = new ShipDocument(Cat());
        doc.Add(new Placement { DefName = "Wall", X = 2, Y = 2, Condition = 0.4 });

        Stroke(doc).PaintTile(2, 2, Destroy, includeLoose: false);

        var broken = Assert.Single(doc.Placements);
        Assert.Equal("WallDmg", broken.DefName);
        Assert.Null(broken.Condition);
    }

    [Fact]
    public void A_part_that_breaks_into_nothing_is_left_at_zero_rather_than_removed()
    {
        var doc = new ShipDocument(Cat());
        doc.Add(new Placement { DefName = "WallDmg", X = 0, Y = 0 });

        Stroke(doc).PaintTile(0, 0, Destroy, includeLoose: false);

        var still = Assert.Single(doc.Placements);
        Assert.Equal("WallDmg", still.DefName);
        Assert.Equal(0.0, still.Condition!.Value, 9);
    }

    // ---- once per object, not once per tile ----

    [Fact]
    public void A_part_wider_than_a_tile_takes_one_roll_however_many_of_its_tiles_the_stroke_crosses()
    {
        // A 2-wide hull crossed end to end used to be rolled twice, so a stroke set to destroy walked it two
        // stages down its break chain in one pass — Hull to HullDmg on the first tile, HullDmg to HullWreck on
        // the second. One drag, one break.
        var doc = new ShipDocument(Cat());
        doc.Add(new Placement { DefName = "Hull", X = 0, Y = 0 });

        var stroke = Stroke(doc);
        stroke.PaintTile(0, 0, Destroy, includeLoose: false);
        Assert.NotEmpty(doc.PlacementsAt(1, 0));   // the second tile really is the same part's other half
        var (painted, skipped) = stroke.PaintTile(1, 0, Destroy, includeLoose: false);

        Assert.Equal("HullDmg", Assert.Single(doc.Placements).DefName);
        Assert.Equal(0, painted);    // the second tile reached the same object, so it is neither painted
        Assert.Equal(0, skipped);    // nor reported as something that could not take wear
    }

    [Fact]
    public void The_next_stroke_rolls_the_same_part_again()
    {
        // Once per object is a property of a stroke, not of the part: a second drag over the same wreck is the
        // user asking for it again.
        var doc = new ShipDocument(Cat());
        doc.Add(new Placement { DefName = "Hull", X = 0, Y = 0 });

        var stroke = Stroke(doc);
        stroke.PaintTile(0, 0, Destroy, includeLoose: false);
        stroke.Reset();
        stroke.PaintTile(0, 0, Destroy, includeLoose: false);

        Assert.Equal("HullWreck", Assert.Single(doc.Placements).DefName);
    }

    // ---- the stroke is one undo step ----

    [Fact]
    public void Undoing_the_stroke_backwards_puts_the_destroyed_part_back_exactly_as_it_was()
    {
        var doc = new ShipDocument(Cat());
        doc.Add(new Placement { DefName = "Wall", X = 1, Y = 1, Condition = 0.4 });

        var stroke = Stroke(doc);
        stroke.PaintTile(1, 1, Destroy, includeLoose: false);

        var batch = new CompositeCommand(stroke.Commands.ToList());
        batch.Undo(doc);

        var back = Assert.Single(doc.Placements);
        Assert.Equal("Wall", back.DefName);
        Assert.Equal(0.4, back.Condition!.Value, 9);
    }

    [Fact]
    public void A_stroke_that_changed_nothing_records_no_commands()
    {
        // A part with no damage pool cannot be painted at all, and a no-op must not land on the undo stack: a
        // Ctrl+Z after a stroke that did nothing would otherwise undo the edit before it.
        var doc = new ShipDocument(Cat());
        doc.Add(new Placement { DefName = "Strut", X = 0, Y = 0 });

        var stroke = Stroke(doc);
        var (painted, skipped) = stroke.PaintTile(0, 0, ConditionBrush.Fixed(0.5), includeLoose: false);

        Assert.Equal(0, painted);
        Assert.Equal(1, skipped);
        Assert.Empty(stroke.Commands);
    }

    // ---- the area brush (Shift + drag) ----

    [Fact]
    public void An_area_paints_every_tile_of_its_rectangle()
    {
        var doc = new ShipDocument(Cat());
        for (var y = 0; y < 3; y++)
            for (var x = 0; x < 3; x++)
                doc.Add(new Placement { DefName = "Floor", X = x, Y = y });
        doc.Add(new Placement { DefName = "Floor", X = 5, Y = 5 });   // outside the box, and must stay untouched

        var (painted, skipped) = Stroke(doc).PaintArea(0, 0, 2, 2, ConditionBrush.Fixed(0.5), includeLoose: false);

        Assert.Equal(9, painted);
        Assert.Equal(0, skipped);
        Assert.Equal(9, doc.Placements.Count(p => p.Condition is { } c && Math.Abs(c - 0.5) < 1e-9));
        Assert.Null(doc.Placements.Single(p => p.X == 5).Condition);
    }

    [Fact]
    public void An_area_reads_the_same_whichever_corner_the_drag_started_from()
    {
        var doc = new ShipDocument(Cat());
        for (var x = 0; x < 3; x++) doc.Add(new Placement { DefName = "Floor", X = x, Y = 0 });

        var (painted, _) = Stroke(doc).PaintArea(2, 0, 0, 0, ConditionBrush.Fixed(0.5), includeLoose: false);

        Assert.Equal(3, painted);
    }

    [Fact]
    public void An_area_is_one_stroke_and_so_one_undo_step()
    {
        // The whole box has to come back on a single Ctrl+Z, including the parts it destroyed.
        var doc = new ShipDocument(Cat());
        doc.Add(new Placement { DefName = "Wall", X = 0, Y = 0 });
        doc.Add(new Placement { DefName = "Floor", X = 1, Y = 0 });

        var stroke = Stroke(doc);
        stroke.PaintArea(0, 0, 1, 0, Destroy, includeLoose: false);
        Assert.Equal(["Floor", "WallDmg"], doc.Placements.Select(p => p.DefName).Order());

        new CompositeCommand(stroke.Commands.ToList()).Undo(doc);

        Assert.Equal(["Floor", "Wall"], doc.Placements.Select(p => p.DefName).Order());
        Assert.All(doc.Placements, p => Assert.Null(p.Condition));
    }

    [Fact]
    public void A_part_straddling_the_edge_of_the_area_is_still_rolled_once()
    {
        var doc = new ShipDocument(Cat());
        doc.Add(new Placement { DefName = "Hull", X = 0, Y = 0 });   // 2 wide: tiles (0,0) and (1,0)

        Stroke(doc).PaintArea(0, 0, 4, 4, Destroy, includeLoose: false);

        Assert.Equal("HullDmg", Assert.Single(doc.Placements).DefName);
    }

    [Fact]
    public void An_area_far_bigger_than_the_ship_paints_the_ship_and_stops()
    {
        // A drag at low zoom bounds a rectangle mostly made of vacuum. Walking all of it would be millions of
        // lookups for the same result, so the box is clipped to what the design occupies.
        var doc = new ShipDocument(Cat());
        doc.Add(new Placement { DefName = "Floor", X = 0, Y = 0 });
        doc.AddLoose(new LooseObject { DefName = "Crate", X = 40, Y = 40 });   // clipping must still reach the deck items

        var (painted, _) = Stroke(doc).PaintArea(-100_000, -100_000, 100_000, 100_000, ConditionBrush.Fixed(0.5), includeLoose: true);

        Assert.Equal(2, painted);
        Assert.Equal(0.5, Assert.Single(doc.LooseObjects).Condition!.Value, 9);
    }

    [Fact]
    public void An_area_on_an_empty_design_does_nothing()
    {
        var stroke = Stroke(new ShipDocument(Cat()));

        Assert.Equal((0, 0), stroke.PaintArea(0, 0, 9, 9, Destroy, includeLoose: true));
        Assert.Empty(stroke.Commands);
    }

    [Fact]
    public void The_totals_run_across_the_whole_stroke_and_reset_with_it()
    {
        var doc = new ShipDocument(Cat());
        doc.Add(new Placement { DefName = "Floor", X = 0, Y = 0 });
        doc.Add(new Placement { DefName = "Floor", X = 1, Y = 0 });
        doc.Add(new Placement { DefName = "Strut", X = 2, Y = 0 });   // no damage pool: reached, not painted

        var stroke = Stroke(doc);
        stroke.PaintTile(0, 0, ConditionBrush.Fixed(0.5), includeLoose: false);
        stroke.PaintTile(1, 0, ConditionBrush.Fixed(0.5), includeLoose: false);
        stroke.PaintTile(2, 0, ConditionBrush.Fixed(0.5), includeLoose: false);

        Assert.Equal((2, 1), stroke.Totals);

        stroke.Reset();
        Assert.Equal((0, 0), stroke.Totals);
    }

    // ---- deck items ----

    [Fact]
    public void A_loose_item_is_painted_with_the_tile_and_never_broken_off_the_deck()
    {
        var doc = new ShipDocument(Cat());
        doc.AddLoose(new LooseObject { DefName = "Crate", X = 3, Y = 3 });

        var (painted, _) = Stroke(doc).PaintTile(3, 3, Destroy, includeLoose: true);

        Assert.Equal(1, painted);
        var crate = Assert.Single(doc.LooseObjects);
        Assert.Equal(0.0, crate.Condition!.Value, 9);   // floored, not deleted: a brush is not a way to remove things
    }

    [Fact]
    public void The_loose_switch_leaves_the_deck_alone_when_it_is_off()
    {
        var doc = new ShipDocument(Cat());
        doc.AddLoose(new LooseObject { DefName = "Crate", X = 3, Y = 3 });

        var (painted, skipped) = Stroke(doc).PaintTile(3, 3, Destroy, includeLoose: false);

        Assert.Equal(0, painted);
        Assert.Equal(0, skipped);
        Assert.Null(Assert.Single(doc.LooseObjects).Condition);
    }
}
