using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The weapon solver (§26): the grid path every projectile in the game takes, as against the physics path a
/// micrometeoroid takes.
///
/// <para>The difference that matters most is the ceiling. This path prices a cell against the <b>whole</b> break
/// chain, so a missile can take a wall from whole to gone in one go; a micrometeoroid never can. Everything else
/// follows from that: the soft edge, which caps a cell at the first stage instead, and the doubled centre of a
/// blast.</para>
/// </summary>
public class WeaponImpactTests
{
    /// <summary>Wall: 10 to damage, 20 more to destroy, so 30 for the whole chain.</summary>
    private static Catalog Cat() => new Fixtures()
        .Part("Wall", startingConds: ["IsInstalled", "IsWall"],
              condValues: new Dictionary<string, double> { ["StatDamageMax"] = 10 })
        .Part("WallDmg", startingConds: ["IsInstalled", "IsWall", "IsDamaged"],
              condValues: new Dictionary<string, double> { ["StatDamageMax"] = 20 })
        .Part("Floor", startingConds: ["IsInstalled"],
              condValues: new Dictionary<string, double> { ["StatDamageMax"] = 10 })
        .BreakPair("Wall", "WallDmg")
        .Build();

    private static ShipAttackDef Attack(
        string name, ImpactType type, double damage, int radius = 0, int soft = 0,
        float range = 10, string[]? triggers = null) =>
        new(name, type, range, damage, radius, soft, 0.1, triggers ?? [], null);

    /// <summary>A row of walls along y = 0, x = 0..4.</summary>
    private static ShipDocument Row(Catalog cat) =>
        Fixtures.Doc(cat, Enumerable.Range(0, 5).Select(x => Fixtures.P("Wall", x, 0)).ToArray());

    // ---- entry geometry ----

    [Theory]
    [InlineData(1, 0, EntryEdge.Left)]     // travelling +x came through the left
    [InlineData(-1, 0, EntryEdge.Right)]
    [InlineData(0, 1, EntryEdge.Top)]      // +y is down in document coords
    [InlineData(0, -1, EntryEdge.Bottom)]
    public void The_entry_edge_follows_the_direction_of_travel(double dx, double dy, EntryEdge expected)
    {
        var cat = Cat();
        var doc = Row(cat);

        var entry = WeaponImpact.EntryAlong(doc, (0.0, 0.0), (dx * 5, dy * 5));

        Assert.NotNull(entry);
        // The edge is what the game spreads a multi-tile impact along, so it has to come from somewhere even
        // though the path itself is now free.
        Assert.Equal(expected, entry!.Edge);
        Assert.Equal(0, entry.DocX, 6);
        Assert.Equal(0, entry.DocY, 6);
    }

    [Fact]
    public void A_path_of_no_length_describes_nothing()
    {
        Assert.Null(WeaponImpact.EntryAlong(Row(Cat()), (2.0, 2.0), (2.0, 2.0)));
    }

    // ---- the ceiling ----

    [Fact]
    public void A_cell_is_priced_against_the_whole_break_chain()
    {
        var cat = Cat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Wall", 0, 0));
        var state = new DamageState();
        var entry = WeaponImpact.EntryAlong(doc, (-3.0, 0.0), (12.0, 0.0))!;

        // 30 into a wall whose chain is 30: gone in one, which the micrometeoroid path can never do because it
        // only ever reads the current form's pool.
        var r = WeaponImpact.Fire(doc, Attack("MassDriver", ImpactType.Ray, 30), entry, state);

        var hit = Assert.Single(r.Hits);
        Assert.Equal(30, hit.Absorbed, 6);
        Assert.Equal(2, hit.StagesBroken);
        Assert.True(hit.Destroyed);
        Assert.True(state.IsDestroyed(doc.Placements[0]));
        Assert.Equal(0, state.Condition(doc.Placements[0], cat), 6);
    }

    [Fact]
    public void A_soft_edge_can_damage_but_never_destroy()
    {
        var cat = Cat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Wall", 0, 0), Fixtures.P("Wall", 0, 1), Fixtures.P("Wall", 0, 2));
        var state = new DamageState();
        var entry = WeaponImpact.EntryAlong(doc, (-3.0, 0.0), (12.0, 0.0))!;

        // Point defence: radius 1 gives three starts, soft edge 2 makes every one of them soft, so 20mm fire caps
        // each part at its first broken form however much damage it carries.
        var pd = Attack("PointDefenceImpact", ImpactType.Point, damage: 500, radius: 1, soft: 2);
        var r = WeaponImpact.Fire(doc, pd, entry, state);

        Assert.NotEmpty(r.Hits);
        Assert.All(r.Hits, h => Assert.False(h.Destroyed));
        Assert.All(r.Hits, h => Assert.Equal(10, h.Absorbed, 6));   // capped at Health, not MaxHealth
        foreach (var p in doc.Placements) Assert.False(state.IsDestroyed(p));
    }

    // ---- patterns ----

    [Fact]
    public void A_ray_spends_range_on_occupied_cells_only()
    {
        var cat = Cat();
        // Five walls in a row with a two-tile gap in the middle; a range of 3 must still reach past the gap.
        var doc = Fixtures.Doc(cat,
            Fixtures.P("Wall", 0, 0), Fixtures.P("Wall", 1, 0),
            Fixtures.P("Wall", 6, 0), Fixtures.P("Wall", 7, 0));
        var entry = WeaponImpact.EntryAlong(doc, (-3.0, 0.0), (12.0, 0.0))!;

        var r = WeaponImpact.Fire(doc, Attack("MassDriver", ImpactType.Ray, 400, range: 4), entry, new DamageState());

        // All four are reached: empty space between them costs nothing against fMaxRange.
        Assert.Equal(4, r.Hits.Select(h => h.PlacementId).Distinct().Count());
    }

    [Fact]
    public void A_blast_damages_its_centre_twice()
    {
        var cat = Cat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Wall", 0, 0));
        var entry = WeaponImpact.EntryAlong(doc, (-3.0, 0.0), (12.0, 0.0))!;

        // The game seeds its cell list with the impact point and the square scan then adds it again at distance 0.
        // A wall whose whole chain is 30 therefore dies to a blast of 20 at the centre, because it lands twice.
        var r = WeaponImpact.Fire(doc, Attack("Missile", ImpactType.Circular, 20, radius: 2), entry, new DamageState());

        Assert.Equal(2, r.Cells.Count(c => c == (0, 0)));
        Assert.True(r.Hits.Sum(h => h.Absorbed) > 20, "the centre only took one application");
    }

    [Fact]
    public void A_missile_detonates_on_the_first_tile_carrying_a_trigger_cond()
    {
        var cat = Cat();
        // Floor first along the trajectory, then a wall. A missile triggers on IsWall, so it must fly over the
        // floor and detonate on the wall rather than at the first thing it touches.
        var doc = Fixtures.Doc(cat,
            Fixtures.P("Floor", 0, 0), Fixtures.P("Floor", 1, 0), Fixtures.P("Wall", 2, 0));
        var entry = WeaponImpact.EntryAlong(doc, (-3.0, 0.0), (12.0, 0.0))!;

        var missile = Attack("MissileAttack01", ImpactType.Circular, 12, radius: 1, triggers: ["IsWall"]);
        var r = WeaponImpact.Fire(doc, missile, entry, new DamageState());

        Assert.Contains(r.Cells, c => c == (2, 0));
        Assert.Contains(r.Hits, h => h.FromDef == "Wall");
    }

    // ---- punching through ----

    [Fact]
    public void A_second_shot_down_the_same_line_reaches_past_what_the_first_destroyed()
    {
        var cat = Cat();
        // Five walls abreast. The mass driver's range is two OCCUPIED cells, so on a pristine hull it can only
        // ever reach the first two — and firing again would be pointless if an emptied tile still cost range.
        var doc = Fixtures.Doc(cat, Enumerable.Range(0, 5).Select(x => Fixtures.P("Wall", x, 0)).ToArray());
        var entry = WeaponImpact.EntryAlong(doc, (-3.0, 0.0), (12.0, 0.0))!;
        var state = new DamageState();
        var driver = Attack("MassDriver", ImpactType.Ray, 60, range: 2);   // 60 = two walls' whole chain

        WeaponImpact.Fire(doc, driver, entry, state);
        Assert.Equal([true, true, false, false, false], doc.Placements.Select(state.IsDestroyed));

        // The hole the first shot made is now free to travel through, so the second reaches the next pair.
        WeaponImpact.Fire(doc, driver, entry, state);
        Assert.Equal([true, true, true, true, false], doc.Placements.Select(state.IsDestroyed));

        WeaponImpact.Fire(doc, driver, entry, state);
        Assert.Equal([true, true, true, true, true], doc.Placements.Select(state.IsDestroyed));
    }

    [Fact]
    public void A_missile_detonates_further_in_once_the_outer_hull_is_gone()
    {
        var cat = Cat();
        var doc = Fixtures.Doc(cat, Enumerable.Range(0, 4).Select(x => Fixtures.P("Wall", x, 0)).ToArray());
        var entry = WeaponImpact.EntryAlong(doc, (-3.0, 0.0), (12.0, 0.0))!;
        var state = new DamageState();
        // Radius 0, so the blast is its centre cell alone — which the game applies twice, giving 30 and exactly
        // destroying one wall. That makes the detonation point visible in what died.
        var missile = Attack("Missile", ImpactType.Circular, 15, radius: 0, triggers: ["IsWall"]);

        for (var shot = 0; shot < 4; shot++)
        {
            WeaponImpact.Fire(doc, missile, entry, state);
            // Each shot detonates on the outermost wall still standing, so the wall it kills walks inward.
            Assert.True(state.IsDestroyed(doc.Placements[shot]), $"shot {shot + 1} did not reach wall {shot}");
            for (var later = shot + 1; later < 4; later++)
                Assert.False(state.IsDestroyed(doc.Placements[later]), $"shot {shot + 1} reached too far");
        }
    }

    [Fact]
    public void A_strike_stops_at_the_end_of_the_line_it_was_drawn_along()
    {
        var cat = Cat();
        // Outer wall at x=0, then a long gap, then an inner wall far away at x=30. The drawn line stops at x=5,
        // well short of the inner wall.
        var doc = Fixtures.Doc(cat, Fixtures.P("Wall", 0, 0), Fixtures.P("Wall", 30, 0));
        var entry = WeaponImpact.EntryAlong(doc, (-3.0, 0.0), (5.0, 0.0))!;
        var state = new DamageState();
        var missile = Attack("Missile", ImpactType.Circular, 15, radius: 0, triggers: ["IsWall"]);

        WeaponImpact.Fire(doc, missile, entry, state);
        Assert.True(state.IsDestroyed(doc.Placements[0]));

        // The outer wall is now gone, so nothing along the drawn line can be detonated against. The missile must
        // MISS rather than fly on to the inner wall thirty tiles further in — which is what a walk bounded by the
        // grid instead of by the path does, and it puts the blast somewhere the user never aimed.
        var second = WeaponImpact.Fire(doc, missile, entry, state);

        Assert.True(second.Missed);
        Assert.False(state.IsDestroyed(doc.Placements[1]));
    }

    [Fact]
    public void Only_the_first_surviving_part_on_a_tile_decides_whether_it_detonates()
    {
        var cat = Cat();
        // A floor and a wall sharing one tile, floor first. The game's FindPointsOfImpact looks at the first
        // surviving part and breaks, so a missile flies over this tile rather than triggering on the buried wall.
        var doc = Fixtures.Doc(cat,
            Fixtures.P("Floor", 0, 0), Fixtures.P("Wall", 0, 0), Fixtures.P("Wall", 3, 0));
        var entry = WeaponImpact.EntryAlong(doc, (-3.0, 0.0), (8.0, 0.0))!;
        var missile = Attack("Missile", ImpactType.Circular, 15, radius: 0, triggers: ["IsWall"]);

        var r = WeaponImpact.Fire(doc, missile, entry, new DamageState());

        // It detonated on the clean wall at x=3, not on the one hiding under the floor at x=0.
        Assert.Contains(r.Cells, c => c == (3, 0));
        Assert.DoesNotContain(r.Cells, c => c == (0, 0));
    }

    // ---- the grid ----

    [Fact]
    public void A_loose_part_is_not_on_the_damage_grid_at_all()
    {
        var cat = new Fixtures()
            .Part("Wall", startingConds: ["IsInstalled", "IsWall"],
                  condValues: new Dictionary<string, double> { ["StatDamageMax"] = 10 })
            .Part("Crate", startingConds: [],   // not installed
                  condValues: new Dictionary<string, double> { ["StatDamageMax"] = 10 })
            .Build();
        var doc = Fixtures.Doc(cat, Fixtures.P("Crate", 0, 0), Fixtures.P("Wall", 1, 0));
        var entry = WeaponImpact.EntryAlong(doc, (-3.0, 0.0), (12.0, 0.0))!;

        var r = WeaponImpact.Fire(doc, Attack("MassDriver", ImpactType.Ray, 100), entry, new DamageState());

        // CreateShallowItemGrid keeps installed parts only, so cargo neither absorbs nor shields.
        Assert.DoesNotContain(r.Hits, h => h.FromDef == "Crate");
        Assert.Contains(r.Hits, h => h.FromDef == "Wall");
    }

    [SkippableFact]
    public void The_shipped_attacks_load_with_the_figures_the_game_declares()
    {
        var g = TestData.RequireGame();
        var attacks = g.Catalog.ShipAttacks;

        // The eight in data/attackmodes/shipAttacks. The coAttacks living in the same folder are a different
        // schema and must not appear here.
        Assert.Equal(600, attacks["MissileAttack01"].TotalDamage, 3);
        Assert.Equal(11, attacks["MissileAttack01"].Radius);
        Assert.Equal(ImpactType.Circular, attacks["MissileAttack01"].Type);
        Assert.True(attacks["MissileAttack01"].DetonatesOnContact);

        Assert.Equal(350, attacks["MassDriverAttack"].TotalDamage, 3);
        Assert.Equal(ImpactType.Ray, attacks["MassDriverAttack"].Type);
        Assert.Equal(10, attacks["MassDriverAttack"].MaxRange, 3);

        Assert.Equal(15, attacks["PointDefenseImpact"].TotalDamage, 3);
        Assert.Equal(ImpactType.Point, attacks["PointDefenseImpact"].Type);

        Assert.DoesNotContain("AModeMicrometeoroid", attacks.Keys);
    }
}
