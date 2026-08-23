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
    public void A_soft_edge_stops_at_the_first_break_while_the_part_is_whole()
    {
        var cat = Cat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Wall", 0, 0), Fixtures.P("Wall", 0, 1), Fixtures.P("Wall", 0, 2));
        var state = new DamageState();
        var entry = WeaponImpact.EntryAlong(doc, (-3.0, 0.0), (12.0, 0.0))!;

        // Point defence: radius 1 gives three starts, soft edge 2 makes every one of them soft, so 20mm fire caps
        // each part at its first broken form however much damage it carries. That cap is on the PART, not on the
        // start, and lifts once the part is damaged: see the test below.
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
    public void A_drawn_line_is_an_aim_and_carries_on_past_where_the_drag_ended()
    {
        var cat = Cat();
        // Outer wall at x=0, then a long gap, then an inner wall far away at x=30. The drag stops at x=5, well
        // short of the inner wall.
        var doc = Fixtures.Doc(cat, Fixtures.P("Wall", 0, 0), Fixtures.P("Wall", 30, 0));
        var entry = WeaponImpact.EntryAlong(doc, (-3.0, 0.0), (5.0, 0.0))!;
        var state = new DamageState();
        var missile = Attack("Missile", ImpactType.Circular, 15, radius: 0, triggers: ["IsWall"]);

        WeaponImpact.Fire(doc, missile, entry, state);
        Assert.True(state.IsDestroyed(doc.Placements[0]));

        // The outer wall is gone, so the next thing along the heading is the inner wall thirty tiles further in.
        // The line sets a direction and nothing else: bounding the shot at the release point made the same shot
        // down the same line hit or miss according to how far someone happened to drag.
        var second = WeaponImpact.Fire(doc, missile, entry, state);

        Assert.False(second.Missed);
        Assert.Equal((30, 0), second.Centre);
        Assert.True(state.IsDestroyed(doc.Placements[1]));
    }

    [Fact]
    public void A_line_aimed_away_from_the_ship_still_misses()
    {
        // The backstop: an aim is unbounded, but only along itself. A heading that never meets the hull has to
        // terminate rather than run to the step cap looking for something.
        var cat = Cat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Wall", 0, 0));
        var missile = Attack("Missile", ImpactType.Circular, 15, radius: 0, triggers: ["IsWall"]);
        var entry = WeaponImpact.EntryAlong(doc, (-3.0, -8.0), (5.0, -8.0))!;

        Assert.True(WeaponImpact.Fire(doc, missile, entry, new DamageState()).Missed);
    }

    [Fact]
    public void A_tile_holding_a_wall_detonates_whatever_order_its_parts_are_in()
    {
        // A floor and a wall sharing one tile. The game looks at the first surviving part and breaks, so in game
        // this turns on which of the two the ship's item list happens to name first. That is not a property of the
        // design and nothing a designer can see, so this asks whether the TILE holds a trigger instead.
        // Deliberate deviation; see ImpactPoint and §26.
        var cat = Cat();
        var missile = Attack("Missile", ImpactType.Circular, 15, radius: 0, triggers: ["IsWall"]);

        foreach (var floorFirst in new[] { true, false })
        {
            var parts = floorFirst
                ? new[] { Fixtures.P("Floor", 0, 0), Fixtures.P("Wall", 0, 0), Fixtures.P("Wall", 3, 0) }
                : [Fixtures.P("Wall", 0, 0), Fixtures.P("Floor", 0, 0), Fixtures.P("Wall", 3, 0)];
            var doc = Fixtures.Doc(cat, parts);
            var entry = WeaponImpact.EntryAlong(doc, (-3.0, 0.0), (8.0, 0.0))!;

            var r = WeaponImpact.Fire(doc, missile, entry, new DamageState());

            // The near wall stops it either way, so two plans identical on screen give the same answer.
            Assert.Equal((0, 0), r.Centre);
        }
    }

    [Fact]
    public void A_tile_whose_trigger_is_spent_is_passed_over()
    {
        // The order-independence must not cost the walk-inward behaviour: a tile counts only while something on
        // it carrying a trigger cond still has capacity left. 40 is enough to spend the whole tile in one go (the
        // floor's 10 plus the wall's 30-deep chain), which is what makes the second shot move on.
        var cat = Cat();
        var doc = Fixtures.Doc(cat,
            Fixtures.P("Floor", 0, 0), Fixtures.P("Wall", 0, 0), Fixtures.P("Wall", 3, 0));
        var missile = Attack("Missile", ImpactType.Circular, 40, radius: 0, triggers: ["IsWall"]);
        var state = new DamageState();

        Assert.Equal((0, 0), WeaponImpact.Fire(doc, missile,
            WeaponImpact.EntryAlong(doc, (-3.0, 0.0), (8.0, 0.0))!, state).Centre);

        // The near wall is spent now. The floor beside it carries no trigger, so the shot moves on to x=3.
        var second = WeaponImpact.Fire(doc, missile,
            WeaponImpact.EntryAlong(doc, (-3.0, 0.0), (8.0, 0.0))!, state);
        Assert.Equal((3, 0), second.Centre);
    }

    // ---- what counts as still worth hitting ----

    /// <summary>
    /// Wall as above, plus a bin whose chain ends on <b>scrap</b>: a form the game still names but does not
    /// install. <see cref="Catalog.MaxHealth"/> counts scrap's own pool and stops there rather than following it
    /// on, so the bin is full at 35 while <see cref="DamageState.IsDestroyed"/> stays false. That is the shape
    /// that broke repeated fire, because a bin carries <c>IsRigid</c> and a missile triggers on that.
    /// </summary>
    private static Catalog CatWithScrap() => new Fixtures()
        .Part("Wall", startingConds: ["IsInstalled", "IsWall"],
              condValues: new Dictionary<string, double> { ["StatDamageMax"] = 10 })
        .Part("WallDmg", startingConds: ["IsInstalled", "IsWall", "IsDamaged"],
              condValues: new Dictionary<string, double> { ["StatDamageMax"] = 20 })
        .Part("Bin", startingConds: ["IsInstalled", "IsRigid"],
              condValues: new Dictionary<string, double> { ["StatDamageMax"] = 10 })
        .Part("BinDmg", startingConds: ["IsInstalled", "IsRigid", "IsDamaged"],
              condValues: new Dictionary<string, double> { ["StatDamageMax"] = 20 })
        .Part("Scrap", startingConds: ["IsRigid"],   // loose debris on the deck: NOT installed
              condValues: new Dictionary<string, double> { ["StatDamageMax"] = 5 })
        .Part("Trash", startingConds: [],
              condValues: new Dictionary<string, double> { ["StatDamageMax"] = 5 })
        .BreakPair("Wall", "WallDmg")
        .BreakPair("Bin", "BinDmg")
        .BreakPair("BinDmg", "Scrap")
        .BreakPair("Scrap", "Trash")
        .Build();

    [Fact]
    public void A_chain_that_ends_in_scrap_is_spent_without_ever_being_destroyed()
    {
        var cat = CatWithScrap();
        var doc = Fixtures.Doc(cat, Fixtures.P("Bin", 0, 0));
        var state = new DamageState();
        var entry = WeaponImpact.EntryAlong(doc, (-3.0, 0.0), (8.0, 0.0))!;

        // 10 + 20 + 5: the whole chain the game prices this bin against, MaxHealth stopping at scrap because
        // scrap is not installed.
        Assert.Equal(35, cat.MaxHealth("Bin"), 6);
        WeaponImpact.Fire(doc, Attack("MassDriver", ImpactType.Ray, 35), entry, state);

        var bin = doc.Placements[0];
        // It broke all the way through into a form the catalog still names, so it is NOT destroyed, and that is
        // exactly why destroyed is the wrong question to ask about it.
        Assert.False(state.IsDestroyed(bin));
        Assert.True(state.IsSpent(bin, cat));
        Assert.Equal(35, state.TotalDamage(bin, cat), 6);
    }

    [Fact]
    public void A_missile_detonates_past_a_part_that_is_spent_but_not_destroyed()
    {
        var cat = CatWithScrap();
        // The bin sits on the hull line with a wall three tiles further in. Both carry a trigger cond.
        var doc = Fixtures.Doc(cat, Fixtures.P("Bin", 0, 0), Fixtures.P("Wall", 3, 0));
        var state = new DamageState();
        var entry = WeaponImpact.EntryAlong(doc, (-3.0, 0.0), (8.0, 0.0))!;
        // Radius 0, so the blast is its centre alone, applied twice: 40 into a 35-point chain empties the bin.
        var missile = Attack("Missile", ImpactType.Circular, 20, radius: 0, triggers: ["IsWall", "IsRigid"]);

        var first = WeaponImpact.Fire(doc, missile, entry, state);
        Assert.Equal((0, 0), first.Centre);
        Assert.True(state.IsSpent(doc.Placements[0], cat));
        Assert.False(state.IsDestroyed(doc.Placements[0]));

        // The bin has nothing left to give, so the game walks straight past it. Asking whether it was DESTROYED
        // instead left a heap of scrap standing in for structure, and every later missile went off on the same
        // tile as the first however many were fired.
        var second = WeaponImpact.Fire(doc, missile, entry, state);

        Assert.Equal((3, 0), second.Centre);
        Assert.NotEqual(0.0, state.TotalDamage(doc.Placements[1], cat));
    }

    [Fact]
    public void A_part_with_no_damage_pool_does_not_set_a_missile_off()
    {
        var cat = new Fixtures()
            .Part("Statue", startingConds: ["IsInstalled", "IsRigid"])   // no StatDamageMax at all
            .Part("Wall", startingConds: ["IsInstalled", "IsWall"],
                  condValues: new Dictionary<string, double> { ["StatDamageMax"] = 10 })
            .Build();
        var doc = Fixtures.Doc(cat, Fixtures.P("Statue", 0, 0), Fixtures.P("Wall", 3, 0));
        var entry = WeaponImpact.EntryAlong(doc, (-3.0, 0.0), (8.0, 0.0))!;
        var missile = Attack("Missile", ImpactType.Circular, 20, radius: 0, triggers: ["IsWall", "IsRigid"]);

        var r = WeaponImpact.Fire(doc, missile, entry, new DamageState());

        // Max health zero satisfies the game's own "already at max health" skip on an untouched part, so a strike
        // passes through it as if it were not there.
        Assert.Equal((3, 0), r.Centre);
    }

    [Fact]
    public void A_soft_edge_finishes_a_part_something_already_cracked()
    {
        var cat = Cat();
        // Three walls abreast, centred on the line: radius 1 spreads the starts one tile either side of it, so
        // all three are under fire.
        var doc = Fixtures.Doc(cat, Fixtures.P("Wall", 0, -1), Fixtures.P("Wall", 0, 0), Fixtures.P("Wall", 0, 1));
        var state = new DamageState();
        var entry = WeaponImpact.EntryAlong(doc, (-3.0, 0.0), (12.0, 0.0))!;
        var pd = Attack("PointDefenceImpact", ImpactType.Point, damage: 500, radius: 1, soft: 2);

        // First burst: every wall is whole, so every one is capped at its first break and survives as WallDmg.
        WeaponImpact.Fire(doc, pd, entry, state);
        Assert.All(doc.Placements, p => Assert.False(state.IsDestroyed(p)));
        Assert.All(doc.Placements, p => Assert.True(state.IsDamaged(p, cat)));

        // Second burst on the same tiles: `damageOnly && !IsDamaged` no longer holds, so the cap comes off and
        // the same 20mm prices them against the whole chain. Point defence cannot take a hull from whole to gone
        // in one pass, but it does get through it, which is what makes firing repeatedly worth doing.
        WeaponImpact.Fire(doc, pd, entry, state);

        Assert.All(doc.Placements, p => Assert.True(state.IsDestroyed(p)));
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

    // ---- why a missile flies past a wall (§26) ----

    [Fact]
    public void The_impact_point_does_not_depend_on_a_tiles_part_order()
    {
        // What the game does: FindPointsOfImpact walks a cell's parts, skips the spent ones, and BREAKS after the
        // first it does not skip, whether or not that part matched a trigger. On a real hull that left 15% of
        // trigger-carrying tiles unable to stop a missile purely because a floor was named first in the ship's
        // item list (§26). Ostraplan asks about the tile, so the same plan always gives the same answer.
        var cat = Cat();
        var missile = Attack("M", ImpactType.Circular, 300, radius: 2, range: 30,
                             triggers: ["IsWall", "IsRigid", "IsPortal"]);

        var floorFirst = Fixtures.Doc(cat, Fixtures.P("Floor", 5, 0), Fixtures.P("Wall", 5, 0));
        var wallFirst = Fixtures.Doc(cat, Fixtures.P("Wall", 5, 0), Fixtures.P("Floor", 5, 0));

        var a = WeaponImpact.Fire(floorFirst, missile,
            WeaponImpact.EntryAlong(floorFirst, (0, 0), (12, 0))!, new DamageState());
        var b = WeaponImpact.Fire(wallFirst, missile,
            WeaponImpact.EntryAlong(wallFirst, (0, 0), (12, 0))!, new DamageState());

        Assert.Equal((5, 0), a.Centre);
        Assert.Equal(a.Centre, b.Centre);
    }

    [Fact]
    public void A_wall_alone_on_its_tile_always_stops_a_missile()
    {
        // The control for the test above: with nothing else on the tile there is no order to get wrong, which is
        // why exterior hull (usually wall-only) reads as "the only thing missiles detonate on".
        var cat = Cat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Wall", 5, 0));
        var missile = Attack("M", ImpactType.Circular, 300, radius: 2, range: 30,
                             triggers: ["IsWall", "IsRigid", "IsPortal"]);

        var r = WeaponImpact.Fire(doc, missile, WeaponImpact.EntryAlong(doc, (0, 0), (12, 0))!, new DamageState());

        Assert.Equal((5, 0), r.Centre);
    }

    [Fact]
    public void A_diagonal_path_steps_over_cells_the_line_crosses()
    {
        // The second reason a projectile can pass through a wall, and a separate one from the tile ordering above.
        // The game advances by one UNIT of the normalised direction and rounds
        // (`point += normalizedDirection; RoundToInt(point)`), which is point sampling rather than a grid
        // traversal: on a diagonal a single step can cross both a column and a row boundary at once, and the cell
        // between them is never looked at. Reproduced, so a plan agrees with the game rather than with geometry.
        var cat = Cat();
        // A solid diagonal wall line. Every cell of it is on the path; the sampling visits only some.
        var doc = Fixtures.Doc(cat, [.. Enumerable.Range(0, 12).Select(i => Fixtures.P("Wall", i, i))]);
        var missile = Attack("M", ImpactType.Circular, 300, radius: 0, range: 30,
                             triggers: ["IsWall", "IsRigid", "IsPortal"]);

        // A path that runs alongside the wall line at a shallow angle, so it crosses it without being parallel.
        var entry = WeaponImpact.EntryAlong(doc, (-4.0, 0.0), (11.0, 9.0))!;
        var walked = new List<(int X, int Y)>();
        double px = Math.Round(entry.DocX), py = Math.Round(entry.DocY);
        for (var i = 0; i < (int)Math.Ceiling(entry.Length) + 1; i++)
        {
            walked.Add(((int)Math.Round(px), (int)Math.Round(py)));
            px += entry.DirX;
            py += entry.DirY;
        }

        // At least one step moves diagonally, which is exactly a cell the line enters and the walk never samples.
        var jumps = walked.Zip(walked.Skip(1))
            .Count(p => Math.Abs(p.Second.X - p.First.X) == 1 && Math.Abs(p.Second.Y - p.First.Y) == 1);
        Assert.True(jumps > 0, "no diagonal step on this path, so it does not exercise the gap");
    }
}
