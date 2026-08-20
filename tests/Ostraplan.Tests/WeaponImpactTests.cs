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
    [InlineData(0, EntryEdge.Left)]      // travelling +x enters from the left
    [InlineData(180, EntryEdge.Right)]
    [InlineData(90, EntryEdge.Top)]      // +y is down in document coords
    [InlineData(270, EntryEdge.Bottom)]
    public void The_entry_point_is_on_the_bounding_box(double angle, EntryEdge expected)
    {
        var cat = Cat();
        var doc = Row(cat);

        var entry = WeaponImpact.EntryFor(doc, angle);

        Assert.NotNull(entry);
        Assert.Equal(expected, entry!.Edge);
        // The box is the bounds plus the one-tile pad, and the aim point must lie on it: the game's
        // FindIntersection cannot express an impact starting anywhere else.
        var onBox = Math.Abs(entry.DocX - (-1)) < 1e-9 || Math.Abs(entry.DocX - 5) < 1e-9
                 || Math.Abs(entry.DocY - (-1)) < 1e-9 || Math.Abs(entry.DocY - 1) < 1e-9;
        Assert.True(onBox, $"entry ({entry.DocX}, {entry.DocY}) is not on the box");
    }

    [Fact]
    public void An_empty_design_has_nowhere_to_be_hit()
    {
        Assert.Null(WeaponImpact.EntryFor(Fixtures.Doc(Cat()), 0));
    }

    // ---- the ceiling ----

    [Fact]
    public void A_cell_is_priced_against_the_whole_break_chain()
    {
        var cat = Cat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Wall", 0, 0));
        var state = new DamageState();
        var entry = WeaponImpact.EntryFor(doc, 0)!;

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
        var entry = WeaponImpact.EntryFor(doc, 0)!;

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
        var entry = WeaponImpact.EntryFor(doc, 0)!;

        var r = WeaponImpact.Fire(doc, Attack("MassDriver", ImpactType.Ray, 400, range: 4), entry, new DamageState());

        // All four are reached: empty space between them costs nothing against fMaxRange.
        Assert.Equal(4, r.Hits.Select(h => h.PlacementId).Distinct().Count());
    }

    [Fact]
    public void A_blast_damages_its_centre_twice()
    {
        var cat = Cat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Wall", 0, 0));
        var entry = WeaponImpact.EntryFor(doc, 0)!;

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
        var entry = WeaponImpact.EntryFor(doc, 0)!;

        var missile = Attack("MissileAttack01", ImpactType.Circular, 12, radius: 1, triggers: ["IsWall"]);
        var r = WeaponImpact.Fire(doc, missile, entry, new DamageState());

        Assert.Contains(r.Cells, c => c == (2, 0));
        Assert.Contains(r.Hits, h => h.FromDef == "Wall");
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
        var entry = WeaponImpact.EntryFor(doc, 0)!;

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
