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

        var (start, end) = MicrometeoroidStrike.GhostPath(doc, anchor, angle);

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
        var (start, end) = MicrometeoroidStrike.GhostPath(doc, anchor, 30);
        var len = Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2));

        // length = 2r where r is the half-diagonal of the padded grid (13x13 here).
        var r = Math.Sqrt(6.5 * 6.5 + 6.5 * 6.5);
        Assert.Equal(2 * r, len, 6);
    }

    [Fact]
    public void One_angle_per_ship_fires_nothing_at_all()
    {
        var cat = WallCat();
        // A square design puts that angle at exactly 45°: the ray's start lands ON the convergence point, and
        // Unity's normalized returns zero below its epsilon rather than a unit vector, so RaycastAll travels
        // nowhere. Reproduced rather than smoothed over, because inventing a direction would manufacture a strike
        // the game never delivers.
        var doc = Fixtures.Doc(cat, Fixtures.P("Wall", 0, 0), Fixtures.P("Wall", 10, 10));
        var anchor = MicrometeoroidStrike.AnchorFor(doc);

        var r = MicrometeoroidStrike.Fire(doc, anchor, 45, 750, new DamageState());

        Assert.True(r.Missed);
        Assert.Equal(r.Pool, r.PoolRemaining, 6);
        Assert.Equal(r.StartDoc, r.EndDoc);
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
        var r = MicrometeoroidStrike.Fire(doc, anchor, AngleHitting(doc, anchor, "Wall"), 750, state);

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
        var angle = AngleHitting(doc, anchor, "Wall");

        MicrometeoroidStrike.Fire(doc, anchor, angle, 750, state);
        Assert.Equal("WallDmg", state.CurrentDef(p));
        Assert.False(state.IsDestroyed(p));

        MicrometeoroidStrike.Fire(doc, anchor, angle, 750, state);
        Assert.True(state.IsDestroyed(p));
        Assert.Equal(0, state.Condition(p, cat), 6);

        // Gone means gone: a third strike finds nothing left to absorb.
        var third = MicrometeoroidStrike.Fire(doc, anchor, angle, 750, state);
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
        var anchor = MicrometeoroidStrike.AnchorFor(doc);

        var hitAny = false;
        for (var a = 0; a < 360; a++)
        {
            var r = MicrometeoroidStrike.Fire(doc, anchor, a, 750, new DamageState());
            Assert.DoesNotContain(r.Hits, h => h.FromDef == "Rock");
            hitAny |= r.Hits.Count > 0;
        }
        Assert.True(hitAny, "no angle reached the ship at all");
    }

    [Fact]
    public void The_condition_scale_measures_against_the_whole_chain()
    {
        var cat = WallCat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Wall", 0, 0));
        var p = doc.Placements[0];
        var state = new DamageState();

        Assert.Equal(1.0, state.Condition(p, cat), 6);

        var anchor = MicrometeoroidStrike.AnchorFor(doc);
        MicrometeoroidStrike.Fire(doc, anchor, AngleHitting(doc, anchor, "Wall"), 750, state);

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

        var anchor2 = MicrometeoroidStrike.AnchorFor(doc);
        MicrometeoroidStrike.Fire(doc, anchor2, AngleHitting(doc, anchor2, "Wall"), 750, state);

        // The placement still names the pristine def: a design carries no wear, and the .oplan must not gain any.
        Assert.Equal("Wall", doc.Placements[0].DefName);
        Assert.False(state.IsPristine);

        state.Clear();
        Assert.True(state.IsPristine);
        Assert.Equal(1.0, state.Condition(doc.Placements[0], cat), 6);
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
        var anchor = MicrometeoroidStrike.AnchorFor(doc);

        for (var angle = 0; angle < 360; angle += 15)
        {
            var r = MicrometeoroidStrike.Fire(doc, anchor, angle, 750, new DamageState());
            Assert.True(r.Hits.Count <= 1, $"angle {angle} hit the same part {r.Hits.Count} times");
        }
    }

    // ---- helpers ----

    /// <summary>The first angle whose ray reaches <paramref name="def"/>, searched in tenths of a degree.
    /// Angles are searched rather than guessed because the convergence point of an unexported design sits just
    /// OUTSIDE the hull, so most angles graze or miss it entirely — which is the behaviour under test elsewhere.</summary>
    private static double AngleHitting(ShipDocument doc, StrikeAnchor anchor, string def)
    {
        for (var i = 0; i < 3600; i++)
        {
            var a = i / 10.0;
            if (MicrometeoroidStrike.Fire(doc, anchor, a, 750, new DamageState()).Hits.Any(h => h.FromDef == def))
                return a;
        }
        throw new Xunit.Sdk.XunitException($"no angle reaches {def}");
    }


    private static double PointToSegment((double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        var len2 = dx * dx + dy * dy;
        if (len2 < 1e-12) return Math.Sqrt(Math.Pow(p.X - a.X, 2) + Math.Pow(p.Y - a.Y, 2));
        var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        return Math.Sqrt(Math.Pow(p.X - (a.X + t * dx), 2) + Math.Pow(p.Y - (a.Y + t * dy), 2));
    }
}
