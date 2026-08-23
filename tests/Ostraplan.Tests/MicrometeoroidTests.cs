using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The micrometeoroid solver (§26): the ray, the pool, and what one strike costs a part.
///
/// <para>Nearly all of it is game-free, because the geometry and the arithmetic are the port and the game data only
/// supplies the numbers. The figures pinned here come off the 1.0.0.11 decompile: 55 environmental damage, a
/// multiplier of closing speed over 750 m/s floored at 0.5, and a ray that aims at world origin rather than at the
/// ship.</para>
/// </summary>
public class MicrometeoroidTests
{
    /// <summary>A wall that breaks once and then is gone: 10 to damage, 20 more to destroy.</summary>
    private static Catalog WallCat() => new Fixtures()
        .Part("Wall", startingConds: ["IsInstalled", "IsWall"],
              condValues: new Dictionary<string, double> { ["StatDamageMax"] = 10 })
        .Part("WallDmg", startingConds: ["IsInstalled", "IsWall", "IsDamaged"],
              condValues: new Dictionary<string, double> { ["StatDamageMax"] = 20 })
        .Part("Rock", startingConds: ["IsInstalled"])   // no pool at all
        .BreakPair("Wall", "WallDmg")
        .Build();

    // ---- the strength model ----

    [Theory]
    [InlineData(0, 0.5)]          // matched velocity still takes half-strength strikes
    [InlineData(375, 0.5)]        // still under the floor
    [InlineData(750, 1.0)]        // one ATC speed limit
    [InlineData(7500, 10.0)]
    public void The_multiplier_is_closing_speed_over_the_atc_limit_with_a_floor(double speed, double expected)
    {
        Assert.Equal(expected, MicrometeoroidStrike.MultiplierFor(speed), 6);
    }

    [Fact]
    public void The_offered_speed_range_is_the_one_the_game_can_actually_deliver()
    {
        // The bottom of the range is where the floor takes over. Below it every speed is the same strike, so a
        // slider running to zero would be offering positions the game cannot tell apart.
        Assert.Equal(MicrometeoroidStrike.MultiplierFor(0),
                     MicrometeoroidStrike.MultiplierFor(MicrometeoroidStrike.MinClosingSpeedMs), 6);
        Assert.True(MicrometeoroidStrike.MultiplierFor(MicrometeoroidStrike.MinClosingSpeedMs + 1)
                    > MicrometeoroidStrike.MultiplierFor(MicrometeoroidStrike.MinClosingSpeedMs));

        // The spawn site that reaches normal play always passes fMult: 1f, so this is the one speed a ship is
        // exposed to wherever it is, and it has to sit inside the offered range.
        Assert.Equal(1.0, MicrometeoroidStrike.MultiplierFor(MicrometeoroidStrike.StandardSpeedMs), 6);
        Assert.InRange(MicrometeoroidStrike.StandardSpeedMs,
                       MicrometeoroidStrike.MinClosingSpeedMs, MicrometeoroidStrike.MaxClosingSpeedMs);
    }

    [Fact]
    public void Damage_and_speed_are_the_same_control_read_two_ways()
    {
        // The window works in damage and the solver in velocity, so the two have to agree exactly at the default
        // or the figure on screen is not the figure being fired.
        Assert.Equal(55.0, MicrometeoroidStrike.StandardDamage, 6);
        Assert.Equal(MicrometeoroidStrike.StandardSpeedMs,
                     MicrometeoroidStrike.SpeedForDamage(MicrometeoroidStrike.StandardDamage), 6);

        // Round-trip across the whole offered range.
        foreach (var damage in new[] { MicrometeoroidStrike.MinDamage, 100.0, 300.0, 560.0 })
            Assert.Equal(damage, MicrometeoroidStrike.WorstCasePool(MicrometeoroidStrike.SpeedForDamage(damage)), 6);

        // Under the floor is not a weaker strike, it is one the game does not have: the multiplier stops moving,
        // so asking for less than the minimum gets the minimum.
        Assert.Equal(MicrometeoroidStrike.MinClosingSpeedMs, MicrometeoroidStrike.SpeedForDamage(1), 6);
        Assert.Equal(MicrometeoroidStrike.MaxClosingSpeedMs, MicrometeoroidStrike.SpeedForDamage(99999), 6);
    }

    [Fact]
    public void The_worst_case_pool_is_fifty_five_times_the_multiplier()
    {
        // The roll is pinned to 1: in game it is Rand(0,1,Mid), so this is the ceiling rather than the expectation.
        Assert.Equal(55.0, MicrometeoroidStrike.WorstCasePool(750), 6);
        Assert.Equal(27.5, MicrometeoroidStrike.WorstCasePool(0), 6);
        Assert.Equal(550.0, MicrometeoroidStrike.WorstCasePool(7500), 6);
    }

    // ---- the anchor ----

    [Fact]
    public void An_unexported_design_converges_one_tile_off_its_top_left_corner()
    {
        var cat = WallCat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Wall", 5, 7), Fixtures.P("Wall", 9, 11));

        var anchor = MicrometeoroidStrike.AnchorFor(doc);

        // Both write paths anchor a fresh ship at the export grid origin, which is the bounding box minus its
        // one-tile pad — so the convergence point is always just outside the hull for a ship Ostraplan makes.
        Assert.Equal(StrikeFrame.AsExported, anchor.Frame);
        Assert.Equal(4, anchor.DocX, 6);
        Assert.Equal(6, anchor.DocY, 6);
    }

    [Fact]
    public void An_imported_ship_keeps_the_anchor_it_arrived_with()
    {
        var cat = WallCat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Wall", 5, 7));

        var anchor = MicrometeoroidStrike.AnchorFor(doc, (12, 3));

        Assert.Equal(StrikeFrame.AsImported, anchor.Frame);
        Assert.Equal(12, anchor.DocX, 6);
        Assert.Equal(3, anchor.DocY, 6);
    }

    // ---- the ray ----

    [Theory]
    [InlineData(0)]
    [InlineData(37)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(271)]
    [InlineData(359)]
    public void Every_ray_passes_through_the_anchor(double angle)
    {
        var cat = WallCat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Wall", 3, 3), Fixtures.P("Wall", 8, 5));
        var anchor = MicrometeoroidStrike.AnchorFor(doc);

        var (start, end) = MicrometeoroidStrike.GameRayFor(doc, anchor, angle);

        // This is the aim bug, and it is the whole reason the origin is not a free parameter: the game normalises
        // the START position rather than the offset from the ship, so every ray it fires runs through world origin.
        var dist = PointToSegment((anchor.DocX, anchor.DocY), start, end);
        Assert.True(dist < 1e-6, $"angle {angle} passed {dist:F4} tiles from the anchor");
    }

    [Fact]
    public void The_ray_starts_outside_the_ship_and_is_long_enough_to_cross_it()
    {
        var cat = WallCat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Wall", 0, 0), Fixtures.P("Wall", 10, 10));
        var anchor = MicrometeoroidStrike.AnchorFor(doc);

        // 45° is the degenerate angle for a square ship, which fires nothing — see the test below. Any other
        // angle gives the full ray.
        var (start, end) = MicrometeoroidStrike.GameRayFor(doc, anchor, 30);
        var len = Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2));

        // length = 2r where r is the half-diagonal of the padded grid (13x13 here).
        var r = Math.Sqrt(6.5 * 6.5 + 6.5 * 6.5);
        Assert.Equal(2 * r, len, 6);
    }

    [Fact]
    public void One_angle_per_ship_is_a_ray_the_game_never_fires()
    {
        var cat = WallCat();
        // A square design puts that angle at exactly 45°: the ray's start lands ON the convergence point, and
        // Unity's normalized returns zero below its epsilon rather than a unit vector, so RaycastAll travels
        // nowhere. It is a fact about the game's own aiming, which is why it survives the move to drawn paths —
        // the reference overlay must not draw a ray the game would not fire.
        var doc = Fixtures.Doc(cat, Fixtures.P("Wall", 0, 0), Fixtures.P("Wall", 10, 10));
        var anchor = MicrometeoroidStrike.AnchorFor(doc);

        var (start, end) = MicrometeoroidStrike.GameRayFor(doc, anchor, 45);

        Assert.Equal(start, end);
    }

    [Fact]
    public void A_drawn_path_may_go_anywhere_the_game_could_not()
    {
        var cat = WallCat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Wall", 20, 20));
        var anchor = MicrometeoroidStrike.AnchorFor(doc);

        // This wall is far from the convergence point and no real micrometeoroid would ever reach it at this
        // heading. Drawing through it still resolves, because the question "what would a hit here cost" is worth
        // answering even when the game cannot ask it.
        var r = MicrometeoroidStrike.Fire(doc, (18.0, 20.0), (22.0, 20.0), 750, new DamageState());

        Assert.False(r.Missed);
        Assert.Equal(10, r.Delivered, 6);
        // The reference ray is still available and still runs through the anchor, which is what tells the user
        // this was a hypothetical.
        var (gs, ge) = MicrometeoroidStrike.GameRayFor(doc, anchor, 30);
        Assert.True(PointToSegment((anchor.DocX, anchor.DocY), gs, ge) < 1e-6);
    }

    // ---- what it hits ----

    [Fact]
    public void A_part_absorbs_only_its_current_forms_pool_and_breaks_one_stage()
    {
        var cat = WallCat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Wall", 0, 0));
        var anchor = MicrometeoroidStrike.AnchorFor(doc);
        var state = new DamageState();

        // A pool of 55 against a wall whose whole chain is 30. The physics hit list is built before anything
        // breaks, so the wall absorbs its own 10, becomes WallDmg, and the ray does NOT come back for the other 20.
        var path = Through(doc.Placements[0]);
        var r = MicrometeoroidStrike.Fire(doc, path.Start, path.End, 750, state);

        var hit = Assert.Single(r.Hits);
        Assert.Equal("Wall", hit.FromDef);
        Assert.Equal(10, hit.Absorbed, 6);
        Assert.True(hit.Broke);
        Assert.Equal("WallDmg", hit.ToDef);
        Assert.Equal("WallDmg", state.CurrentDef(doc.Placements[0]));
        Assert.Equal(45, r.PoolRemaining, 6);   // 55 − 10, the rest passes out the far side
    }

    [Fact]
    public void Firing_again_drives_the_part_through_its_chain()
    {
        var cat = WallCat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Wall", 0, 0));
        var anchor = MicrometeoroidStrike.AnchorFor(doc);
        var state = new DamageState();
        var p = doc.Placements[0];
        var path = Through(p);

        MicrometeoroidStrike.Fire(doc, path.Start, path.End, 750, state);
        Assert.Equal("WallDmg", state.CurrentDef(p));
        Assert.False(state.IsDestroyed(p));

        MicrometeoroidStrike.Fire(doc, path.Start, path.End, 750, state);
        Assert.True(state.IsDestroyed(p));
        Assert.Equal(0, state.Condition(p, cat), 6);

        // Gone means gone: a third strike finds nothing left to absorb.
        var third = MicrometeoroidStrike.Fire(doc, path.Start, path.End, 750, state);
        Assert.Empty(third.Hits);
    }

    [Fact]
    public void A_part_with_no_damage_pool_absorbs_nothing_and_the_ray_carries_on()
    {
        var cat = WallCat();
        // Rock has no StatDamageMax at all. Whatever angle is fired, it must never appear as a hit and must never
        // consume pool: the game's DmgLeft <= 0 branch continues past it rather than stopping the ray.
        var doc = Fixtures.Doc(cat,
            Fixtures.P("Rock", 0, 0), Fixtures.P("Rock", 1, 0), Fixtures.P("Rock", 2, 0),
            Fixtures.P("Wall", 0, 1), Fixtures.P("Wall", 1, 1), Fixtures.P("Wall", 2, 1));
        var hitAny = false;
        for (var y = 0; y < 2; y++)
        {
            var r = MicrometeoroidStrike.Fire(doc, (-2.0, y), (5.0, y), 750, new DamageState());
            Assert.DoesNotContain(r.Hits, h => h.FromDef == "Rock");
            hitAny |= r.Hits.Count > 0;
        }
        Assert.True(hitAny, "no path reached the ship at all");
    }

    [Fact]
    public void The_condition_scale_measures_against_the_whole_chain()
    {
        var cat = WallCat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Wall", 0, 0));
        var p = doc.Placements[0];
        var state = new DamageState();

        Assert.Equal(1.0, state.Condition(p, cat), 6);

        var path = Through(p);
        MicrometeoroidStrike.Fire(doc, path.Start, path.End, 750, state);

        // 10 of 30 absorbed. Measuring against the current form's pool instead would read this as full health
        // again the instant the wall broke, which is exactly the misleading answer the overlay must not give.
        Assert.Equal(1 - 10.0 / 30.0, state.Condition(p, cat), 6);
    }

    [Fact]
    public void Damage_never_reaches_the_document()
    {
        var cat = WallCat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Wall", 0, 0));
        var state = new DamageState();

        var path = Through(doc.Placements[0]);
        MicrometeoroidStrike.Fire(doc, path.Start, path.End, 750, state);

        // The placement still names the pristine def: a design carries no wear, and the .oplan must not gain any.
        Assert.Equal("Wall", doc.Placements[0].DefName);
        Assert.False(state.IsPristine);

        state.Clear();
        Assert.True(state.IsPristine);
        Assert.Equal(1.0, state.Condition(doc.Placements[0], cat), 6);
    }

    // ---- punching through ----

    [Fact]
    public void Strikes_down_the_same_line_eat_their_way_inward()
    {
        var cat = WallCat();
        // Four bulkheads abreast, each 10 to crack and 20 more to finish. A strike carries 55, so the first one
        // reaches all four and cracks them — a micrometeoroid can only ever advance a part one stage — and the
        // second finishes as many as its pool covers. This is the "how many hits to reach the middle" question.
        var doc = Fixtures.Doc(cat, Enumerable.Range(0, 4).Select(x => Fixtures.P("Wall", x, 0)).ToArray());
        var state = new DamageState();
        ((double X, double Y) S, (double X, double Y) E) path = ((-3.0, 0.0), (10.0, 0.0));

        var first = MicrometeoroidStrike.Fire(doc, path.S, path.E, 750, state);
        Assert.Equal(4, first.Hits.Count);
        Assert.All(first.Hits, h => Assert.True(h.Broke));
        Assert.All(doc.Placements, p => Assert.False(state.IsDestroyed(p)));
        Assert.All(doc.Placements, p => Assert.Equal("WallDmg", state.CurrentDef(p)));

        // Keep firing the same line. Each pass destroys what it can, and the ones already gone neither absorb nor
        // shield, so the damage reaches further in every time rather than stalling on the outer skin.
        var shots = 1;
        while (doc.Placements.Any(p => !state.IsDestroyed(p)) && shots < 20)
        {
            MicrometeoroidStrike.Fire(doc, path.S, path.E, 750, state);
            shots++;
        }

        Assert.All(doc.Placements, p => Assert.True(state.IsDestroyed(p)));
        // 4 walls x 30 = 120 damage against 55 a strike, so it cannot be done in fewer than three.
        Assert.Equal(3, shots);
    }

    [Fact]
    public void A_hole_lets_the_next_strike_past_without_spending_anything_on_it()
    {
        var cat = WallCat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Wall", 0, 0), Fixtures.P("Wall", 5, 0));
        var state = new DamageState();
        var outer = doc.Placements[0];

        // Drive the near wall to nothing, then fire: the strike must arrive at the far wall with its pool
        // untouched, because there is no longer anything at the near tile to soak it. Spent directly rather than
        // by firing a short line at it, since a drawn line is an aim and would carry on into the far wall too.
        state.Apply(outer, "Wall", 10, cat);
        state.Apply(outer, "WallDmg", 20, cat);
        Assert.True(state.IsDestroyed(outer));

        var through = MicrometeoroidStrike.Fire(doc, (-3.0, 0.0), (10.0, 0.0), 750, state);

        var hit = Assert.Single(through.Hits);
        Assert.Equal(doc.Placements[1].Id, hit.PlacementId);
        Assert.Equal(10, through.Delivered, 6);   // the far wall's own pool, and nothing lost on the way
    }

    // ---- the collider ----

    [SkippableFact]
    public void A_multi_tile_part_absorbs_once_however_many_tiles_the_ray_crosses()
    {
        var g = TestData.RequireGame();
        var cat = g.Catalog;

        // A raycast returns one hit per collider, so a big part is one entry in the list no matter how much of it
        // the ray passes through. Anything that walked tiles instead would charge it several times over.
        var big = cat.Parts.FirstOrDefault(p =>
            p.Item.Width >= 3 && p.Item.Height >= 3 && cat.IsDestructable(p.DefName)
            && p.StartingConds.Contains("IsInstalled"));
        Skip.If(big is null, "no 3x3 destructable part in this install");

        var doc = Fixtures.Doc(cat, new Placement { DefName = big!.DefName, X = 0, Y = 0 });

        // Sweep paths right across it in both axes and diagonally: a raycast returns one hit per collider, so a
        // part several tiles wide is charged once however much of it the line crosses.
        for (var i = -1; i <= 3; i++)
        {
            foreach (var (s, e) in new[]
                     {
                         ((-4.0, (double)i), (6.0, (double)i)),
                         (((double)i, -4.0), ((double)i, 6.0)),
                         ((-4.0, (double)i - 4), (6.0, (double)i + 6)),
                     })
            {
                var r = MicrometeoroidStrike.Fire(doc, s, e, 750, new DamageState());
                Assert.True(r.Hits.Count <= 1, $"path {s}->{e} hit the same part {r.Hits.Count} times");
            }
        }
    }

    // ---- the collider follows the form ----

    /// <summary>
    /// A part that has broken presents its NEW form's sprite to the next ray, because the game replaces the object
    /// outright: <c>CondOwner.ModeSwitch</c> swaps in a fresh <c>CondOwner</c> carrying its own <c>Item</c>, and
    /// <c>Item.ResetTransforms</c> scales that item's quad by its own <c>vScale</c>.
    ///
    /// <para>Game-gated because the effect needs two defs whose sprites differ, and a synthetic fixture has no
    /// texture on disk so every part in one is 1×1. <c>ItmCanisterLHe02</c> is the clearest case in stock data:
    /// a 3×3 tank that breaks straight into <c>ItmScrapAluminum</c>, 1×1.</para>
    /// </summary>
    [SkippableFact]
    public void A_broken_part_shields_with_its_new_form_not_its_old_one()
    {
        var g = TestData.RequireGame();
        var doc = Fixtures.Doc(g.Catalog, Fixtures.P("ItmCanisterLHe02", 10, 10));
        Skip.IfNot(doc.Placements.Count == 1, "ItmCanisterLHe02 not in this install");
        var tank = doc.Placements[0];

        // Footprint 7×7 anchored at (10,10) puts the transform, and so the collider centre, on (13,13). The 3×3
        // sprite reaches to 14.5 and the 1×1 scrap only to 13.5, so y = 14 grazes the tank and clears the scrap.
        const double grazing = 14.0;

        var pristine = MicrometeoroidStrike.Fire(doc, (0.0, grazing), (30.0, grazing), 7700, new DamageState());
        Assert.Single(pristine.Hits);

        var broken = new DamageState();
        broken.Apply(tank, "ItmCanisterLHe02", g.Catalog.Health("ItmCanisterLHe02"), g.Catalog);
        Assert.Equal("ItmScrapAluminum", broken.CurrentDef(tank));

        // Same line, same ship, but the tank is a heap of scrap now and the ray goes over it. Reading the
        // placement's original def instead left the wreck shielding the compartment behind it at full size.
        var after = MicrometeoroidStrike.Fire(doc, (0.0, grazing), (30.0, grazing), 7700, broken);
        Assert.Empty(after.Hits);

        // Straight through the middle still finds it, so this is the collider shrinking rather than the part
        // dropping out of the raycast altogether.
        var centred = MicrometeoroidStrike.Fire(doc, (0.0, 13.0), (30.0, 13.0), 7700, broken);
        Assert.Single(centred.Hits);
    }

    // ---- the canvas frame ----

    [Fact]
    public void A_path_drawn_across_a_tile_hits_the_part_standing_on_that_tile()
    {
        // The canvas reports a continuous position in its own CORNER frame, where tile (x, y) covers [x, x+1), so
        // anywhere from y=3.0 to y=4.0 is inside the row the user can see their line crossing. The solver reads an
        // integer as a tile CENTRE. Handing a canvas point straight over aimed half a tile up and to the left of
        // the drawn line, which put the answer on the wrong row for the lower half of every tile.
        var cat = WallCat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Wall", 2, 3), Fixtures.P("Wall", 2, 4));
        var wall3 = doc.Placements[0];

        var start = TileFrame.CornerToCentre((0.0, 3.9));
        var end = TileFrame.CornerToCentre((6.0, 3.9));
        var r = MicrometeoroidStrike.Fire(doc, start, end, 750, new DamageState());

        var hit = Assert.Single(r.Hits);
        Assert.Equal(wall3.Id, hit.PlacementId);

        // The same line unconverted lands on the row below, which is the bug this guards.
        var raw = MicrometeoroidStrike.Fire(doc, (0.0, 3.9), (6.0, 3.9), 750, new DamageState());
        Assert.Equal(doc.Placements[1].Id, Assert.Single(raw.Hits).PlacementId);
    }

    [Fact]
    public void The_two_tile_frames_round_trip_and_agree_on_which_cell_a_point_is_in()
    {
        (double X, double Y) middleOfTile = (2.5, 3.5);
        Assert.Equal((2, 3), TileFrame.CellOf(middleOfTile));
        // The middle of a tile in the corner frame is the tile's own index in the centre frame, which is the whole
        // of the difference between them.
        Assert.Equal((2.0, 3.0), TileFrame.CornerToCentre(middleOfTile));
        Assert.Equal(middleOfTile, TileFrame.CentreToCorner(TileFrame.CornerToCentre(middleOfTile)));

        // A point anywhere inside a tile stays inside it, which is what makes the conversion safe to apply to a
        // drag position rather than only to a snapped one.
        Assert.Equal((14, 1), TileFrame.CellOf((14.8, 1.6)));
        Assert.Equal((14, 1), TileFrame.CellOf(TileFrame.CentreToCorner(TileFrame.CornerToCentre((14.8, 1.6)))));
    }

    // ---- helpers ----

    /// <summary>A straight path across a placement, from one tile before it to one tile after — the line a user
    /// would drag through the part they want to test.</summary>
    private static ((double X, double Y) Start, (double X, double Y) End) Through(Placement p) =>
        ((p.X - 2.0, p.Y), (p.X + 2.0, p.Y));


    private static double PointToSegment((double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        var len2 = dx * dx + dy * dy;
        if (len2 < 1e-12) return Math.Sqrt(Math.Pow(p.X - a.X, 2) + Math.Pow(p.Y - a.Y, 2));
        var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        return Math.Sqrt(Math.Pow(p.X - (a.X + t * dx), 2) + Math.Pow(p.Y - (a.Y + t * dy), 2));
    }

    [Fact]
    public void The_ceiling_comes_from_the_data_because_the_code_has_none()
    {
        // StarSystem.SpawnMicroMeteoroid passes fMult straight through and the only clamp on the path is the 0.5
        // floor, so how hard a strike can be is decided by the fastest band that declares a chance at all.
        var noBands = new List<CelestialBody>
        {
            new("Sol", "Ceres", RadiusKm: 470, MassKg: 9.4e20, Bands: []),
        };
        // Nothing declares one, so the atmosphere site cannot fire and the standard strike is the only strike.
        Assert.Equal(MicrometeoroidStrike.StandardSpeedMs, MicrometeoroidStrike.FastestClosingSpeed(noBands), 6);
        Assert.Equal(MicrometeoroidStrike.StandardDamage, MicrometeoroidStrike.MaxDamageFor(noBands), 6);

        // A band that declares one raises the ceiling; a faster (lower) band raises it further. A band with a zero
        // chance never fires, so it must not count however fast a ship would be moving through it.
        var earthish = new List<CelestialBody>
        {
            new("Sol", "Earth", RadiusKm: 6371, MassKg: 5.97e24, Bands:
            [
                new AtmosphereBand("Low", 6771, 250, new Dictionary<string, double>(), MicrometeoroidChance: 0.1),
                new AtmosphereBand("High", 7600, 250, new Dictionary<string, double>(), MicrometeoroidChance: 0.01),
                new AtmosphereBand("Silent", 6400, 250, new Dictionary<string, double>(), MicrometeoroidChance: 0),
            ]),
        };
        var fastest = MicrometeoroidStrike.FastestClosingSpeed(earthish);

        // Circular orbit at the lowest band that can actually fire, which is a real orbital velocity rather than a
        // round number: near 7.7 km/s for a shell 400 km up.
        Assert.InRange(fastest, 7600, 7750);
        Assert.True(fastest > MicrometeoroidStrike.StandardSpeedMs);
        // The silent band sits lower still, so counting it would have produced a faster figure than this.
        Assert.True(fastest < 7900);
    }

    [Fact]
    public void A_drawn_line_is_an_aim_and_reaches_past_where_the_drag_ended()
    {
        // The drag sets a start and a heading. Ending the ray where the mouse came up made the answer turn on how
        // far someone happened to pull, so the same strike down the same line reached a part or did not according
        // to a gesture rather than to the hull.
        var cat = WallCat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Wall", 0, 0), Fixtures.P("Wall", 20, 0));

        // Released two tiles in, twenty short of the far wall.
        var r = MicrometeoroidStrike.Fire(doc, (-3.0, 0.0), (-1.0, 0.0), 7700, new DamageState());

        Assert.Equal(2, r.Hits.Count);
        Assert.Equal(doc.Placements[0].Id, r.Hits[0].PlacementId);   // nearest first, as ever
        Assert.Equal(doc.Placements[1].Id, r.Hits[1].PlacementId);
        // The drag is still reported as drawn, so the readout says where the line was put.
        Assert.Equal((-1.0, 0.0), r.EndDoc);
    }

    [Fact]
    public void A_drag_that_never_moved_still_describes_no_strike()
    {
        // An aim needs a heading. A click without a drag has none, and must not be turned into one.
        var cat = WallCat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Wall", 0, 0));

        Assert.True(MicrometeoroidStrike.Fire(doc, (0.0, 0.0), (0.0, 0.0), 750, new DamageState()).Missed);
    }
}
