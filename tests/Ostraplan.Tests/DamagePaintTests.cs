using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The Damage Brush's rules (<see cref="Paint"/>, <see cref="ConditionBrush"/>) and the round trip that carries a
/// painted condition through the <c>.oplan</c>. Game-free: the brush is an authoring tool, and what it writes is
/// checked against the export elsewhere.
/// </summary>
public class DamagePaintTests
{
    private static Catalog Cat() => new Fixtures()
        .Part("Wall", startingConds: ["IsInstalled", "IsWall"], condValues: new Dictionary<string, double> { ["StatDamageMax"] = 30 })
        .Part("WallDmg", startingConds: ["IsInstalled", "IsWall", "IsDamaged"], condValues: new Dictionary<string, double> { ["StatDamageMax"] = 15 })
        .Part("Reactor", startingConds: ["IsInstalled", "IsSystem"], condValues: new Dictionary<string, double> { ["StatDamageMax"] = 99 })
        .Part("Strut", startingConds: ["IsInstalled"])
        .Part("Crate", startingConds: [], condValues: new Dictionary<string, double> { ["StatDamageMax"] = 10 })
        .BreakPair("Wall", "WallDmg")
        .Build();

    // ---- the brush ----

    [Fact]
    public void A_fixed_brush_paints_one_value_and_never_rolls()
    {
        var b = ConditionBrush.Fixed(0.55);
        Assert.True(b.IsFixed);

        var rng = new Random(1);
        for (var i = 0; i < 50; i++) Assert.Equal(0.55, b.Roll(rng), 9);
    }

    [Fact]
    public void A_range_brush_stays_inside_its_bounds_and_actually_varies()
    {
        // The point of the range is a compartment where some things held up and others did not, so it has to
        // genuinely spread rather than cluster on one end.
        var b = ConditionBrush.Range(0.3, 0.8);
        var rng = new Random(7);

        var seen = new List<double>();
        for (var i = 0; i < 400; i++)
        {
            var v = b.Roll(rng);
            Assert.InRange(v, 0.3, 0.8);
            seen.Add(v);
        }
        Assert.True(seen.Max() - seen.Min() > 0.4, "the range brush barely varied");
        Assert.False(b.IsFixed);
    }

    [Fact]
    public void A_range_reads_the_same_whichever_way_round_it_is_given()
    {
        // The UI has two boxes and nothing stops a user typing the larger one first.
        Assert.Equal(ConditionBrush.Range(0.8, 0.3), ConditionBrush.Range(0.3, 0.8));
    }

    [Fact]
    public void Out_of_range_input_is_clamped_rather_than_trusted()
    {
        Assert.Equal(1.0, ConditionBrush.Fixed(4.0).Low, 9);
        Assert.Equal(0.0, ConditionBrush.Fixed(-2.0).High, 9);
        Assert.Equal(0.5, Paint.Clamp(0.5));
        Assert.Null(Paint.Clamp(null));
        Assert.Equal(1.0, Paint.Clamp(9.0));
    }

    // ---- what may be painted ----

    [Fact]
    public void The_brush_skips_exactly_what_the_games_own_wear_pass_skips()
    {
        var cat = Cat();

        Assert.True(Paint.CanWear(cat.Lookup("Wall")));
        Assert.False(Paint.CanWear(cat.Lookup("Reactor")));   // IsSystem
        Assert.False(Paint.CanWear(cat.Lookup("Strut")));     // no StatDamageMax
        Assert.False(Paint.CanWear(cat.Lookup("Crate")));     // not installed: that is the loose test's job
        Assert.False(Paint.CanWear(null));
    }

    [Fact]
    public void A_loose_item_needs_a_pool_but_not_an_installed_flag()
    {
        // A deck item is by definition not IsInstalled, so applying the placed test to it would leave every
        // crate and canister unpaintable — which is most of what makes a compartment read as lived-in.
        var cat = Cat();
        Assert.True(Paint.CanWearLoose(cat.Lookup("Crate")));
        Assert.False(Paint.CanWearLoose(cat.Lookup("Strut")));
        Assert.False(Paint.CanWearLoose(cat.Lookup("Reactor")));
    }

    // ---- zero condition breaks the part ----

    [Fact]
    public void Painting_zero_breaks_the_part_into_its_damaged_def()
    {
        // The game does not let a condition owner rest at a full pool: DestCheck.DamageCheck fires the break and
        // mode-switches the part. A design that stored "Wall at 0%" would claim a state the game cannot hold.
        var cat = Cat();
        var r = Paint.Resolve("Wall", 0.0, cat);

        Assert.NotNull(r);
        Assert.Equal("WallDmg", r!.Value.Def);
        Assert.Null(r.Value.Condition);   // the broken form starts its own life whole
    }

    [Fact]
    public void Painting_above_zero_leaves_the_def_alone()
    {
        var cat = Cat();
        var r = Paint.Resolve("Wall", 0.35, Cat());

        Assert.NotNull(r);
        Assert.Equal("Wall", r!.Value.Def);
        Assert.Equal(0.35, r.Value.Condition!.Value, 9);
    }

    [Fact]
    public void A_part_that_breaks_into_nothing_stays_put_at_zero()
    {
        // WallDmg has no break form of its own. A design has no way to place an absence, so the honest answer is
        // the lowest condition the part can actually hold rather than deleting it behind the user's back.
        var cat = Cat();
        var r = Paint.Resolve("WallDmg", 0.0, cat);

        Assert.NotNull(r);
        Assert.Equal("WallDmg", r!.Value.Def);
        Assert.Equal(0.0, r.Value.Condition!.Value, 9);
    }

    [Fact]
    public void An_unpaintable_part_resolves_to_nothing()
    {
        var cat = Cat();
        Assert.Null(Paint.Resolve("Reactor", 0.5, cat));
        Assert.Null(Paint.Resolve("Strut", 0.5, cat));
    }

    // ---- the commands ----

    [Fact]
    public void Painting_and_clearing_are_one_undo_step_each()
    {
        var doc = new ShipDocument(Cat());
        var wall = new Placement { DefName = "Wall", X = 2, Y = 3 };
        doc.Add(wall);

        var cmd = new SetConditionCommand(wall, wall.Condition, 0.4);
        cmd.Do(doc);
        Assert.Equal(0.4, wall.Condition!.Value, 9);

        cmd.Undo(doc);
        Assert.Null(wall.Condition);
    }

    [Fact]
    public void The_command_clamps_what_it_is_given()
    {
        var doc = new ShipDocument(Cat());
        var wall = new Placement { DefName = "Wall", X = 0, Y = 0 };
        doc.Add(wall);

        new SetConditionCommand(wall, null, 5.0).Do(doc);
        Assert.Equal(1.0, wall.Condition!.Value, 9);
    }

    [Fact]
    public void A_loose_item_paints_the_same_way()
    {
        var doc = new ShipDocument(Cat());
        var crate = new LooseObject { DefName = "Crate", X = 1, Y = 1 };
        doc.AddLoose(crate);

        var cmd = new SetLooseConditionCommand(crate, null, 0.25);
        cmd.Do(doc);
        Assert.Equal(0.25, crate.Condition!.Value, 9);
        cmd.Undo(doc);
        Assert.Null(crate.Condition);
    }

    // ---- it survives a state swap, and is dropped by a copy ----

    [Fact]
    public void A_state_swap_carries_the_painted_condition()
    {
        // Uninstalling a battered pump does not mend it, the same reasoning that carries Fill and CustomName
        // through Restate.
        var wall = new Placement { DefName = "Wall", X = 4, Y = 4, Condition = 0.42 };
        var swapped = wall.Restate("WallDmg", 0);
        Assert.Equal(0.42, swapped.Condition!.Value, 9);
    }

    // ---- the .oplan round trip ----

    [Fact]
    public void A_painted_condition_is_read_back_and_an_unpainted_one_stays_absent()
    {
        var cat = Cat();
        var tmp = Path.Combine(Path.GetTempPath(), $"ostraplan-test-{Guid.NewGuid():N}.oplan");
        try
        {
            new OplanFile
            {
                Parts =
                [
                    new OplanPart { Def = "Wall", X = 1, Y = 1, Cond = 0.62 },
                    new OplanPart { Def = "Wall", X = 2, Y = 1 },   // unpainted, and must stay that way
                ],
                LooseObjects = [new OplanLoose { Def = "Crate", X = 3, Y = 1, Qty = 1, Cond = 0.18 }],
            }.Save(tmp);

            var (back, missing) = OplanFile.Load(tmp).ToDocument(cat);

            Assert.Empty(missing);
            Assert.Equal(0.62, back.Placements[0].Condition!.Value, 6);
            Assert.Null(back.Placements[1].Condition);
            Assert.Equal(0.18, back.LooseObjects.First().Condition!.Value, 6);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void An_unpainted_design_writes_no_condition_field_at_all()
    {
        // The field has to stay absent for the overwhelming majority of parts, or every .oplan in existence grows
        // a line per part for a feature almost no design uses.
        var tmp = Path.Combine(Path.GetTempPath(), $"ostraplan-test-{Guid.NewGuid():N}.oplan");
        try
        {
            new OplanFile
            {
                Parts = [new OplanPart { Def = "Wall", X = 1, Y = 1 }],
                LooseObjects = [new OplanLoose { Def = "Crate", X = 3, Y = 1, Qty = 1 }],
            }.Save(tmp);

            Assert.DoesNotContain("\"cond\"", File.ReadAllText(tmp), StringComparison.Ordinal);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void A_hand_edited_condition_outside_the_range_is_clamped_on_load()
    {
        // The .oplan is meant to be human-readable and people do edit it. A condition above 1 would drive the
        // wear shader and the export's StatDamage past the pool the part actually has.
        var cat = Cat();
        var (back, _) = new OplanFile
        {
            Parts = [new OplanPart { Def = "Wall", X = 1, Y = 1, Cond = 7.5 }],
            LooseObjects = [new OplanLoose { Def = "Crate", X = 2, Y = 1, Qty = 1, Cond = -3 }],
        }.ToDocument(cat);

        Assert.Equal(1.0, back.Placements[0].Condition!.Value, 9);
        Assert.Equal(0.0, back.LooseObjects.First().Condition!.Value, 9);
    }

    [SkippableFact]
    public void A_painted_design_round_trips_through_a_real_oplan()
    {
        // The write direction needs a DataIndex for the mod manifest, so it is game-gated. What it proves that
        // the game-free half cannot is that FromDocument actually emits the field.
        var g = TestData.RequireGame();
        var doc = new ShipDocument(g.Catalog);
        doc.Add(new Placement { DefName = "ItmWall1x1", X = 1, Y = 1, Condition = 0.44 });
        doc.Add(new Placement { DefName = "ItmWall1x1", X = 2, Y = 1 });

        var (back, missing) = OplanFile.FromDocument(doc, g.Index, new OplanMeta()).ToDocument(g.Catalog);

        Assert.Empty(missing);
        Assert.Equal(0.44, back.Placements[0].Condition!.Value, 6);
        Assert.Null(back.Placements[1].Condition);
    }
}
