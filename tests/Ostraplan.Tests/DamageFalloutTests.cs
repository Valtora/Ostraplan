using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// What a strike costs the <b>ship</b>, as against its parts.
///
/// <para>The case that matters most here is the one the whole thing was rebuilt for: a design that was already
/// leaking must not have its own faults reported as the strike's doing. The old set-difference over
/// <see cref="ProblemScan"/> could not tell them apart, because an airtightness warning puts the breach count in
/// its own title and merges every leak point into one cell list, so any new breach changed the whole string and
/// the whole aggregate read as new.</para>
/// </summary>
public class DamageFalloutTests
{
    private const double WallPool = 10;

    private static Catalog Cat() => new Fixtures()
        .Floor()
        // A destructible wall: nothing to break into, so filling its pool takes it off the tile.
        .Part("Wall", tileConds: ["IsWall", "IsObstruction"], startingConds: ["IsInstalled", "IsWall"],
              category: "HULL", condValues: new Dictionary<string, double> { ["StatDamageMax"] = WallPool })
        // A wall that breaks into a DAMAGED wall, which is still installed and still carries IsWall — the game's
        // own ItmWall1x1 → ItmWall1x1Dmg.
        .Part("SoftWall", tileConds: ["IsWall", "IsObstruction"], startingConds: ["IsInstalled", "IsWall"],
              category: "HULL", condValues: new Dictionary<string, double> { ["StatDamageMax"] = WallPool })
        .Part("SoftWallDmg", tileConds: ["IsWall", "IsObstruction"],
              startingConds: ["IsInstalled", "IsWall", "IsDamaged"], category: "HULL",
              condValues: new Dictionary<string, double> { ["StatDamageMax"] = 20 })
        .Part("Console", tileConds: ["IsFixture", "IsObstruction"],
              startingConds: ["IsInstalled", "TIsNavStationInstalled"], category: "CTRL",
              condValues: new Dictionary<string, double> { ["StatDamageMax"] = WallPool })
        .BreakPair("SoftWall", "SoftWallDmg")
        .Build();

    /// <summary>
    /// A sealed three-tile compartment at (1..3, 1), walled all round, plus a strip of bare floor out at x=7 that
    /// is open to space and always was. <paramref name="wall"/> names the def used for the ring, so a test can
    /// choose whether the hull breaks into nothing or into a damaged wall.
    /// </summary>
    private static ShipDocument Ship(Catalog cat, string wall = "Wall")
    {
        var parts = new List<Placement>();
        for (var x = 0; x <= 4; x++)
        {
            parts.Add(Fixtures.P(wall, x, 0));
            parts.Add(Fixtures.P(wall, x, 2));
        }
        parts.Add(Fixtures.P(wall, 0, 1));
        parts.Add(Fixtures.P(wall, 4, 1));
        for (var x = 1; x <= 3; x++) parts.Add(Fixtures.P("Floor", x, 1));

        // The design's own pre-existing fault: floor nobody ever walled in.
        parts.Add(Fixtures.P("Floor", 7, 1));
        parts.Add(Fixtures.P("Floor", 8, 1));

        return Fixtures.Doc(cat, [.. parts]);
    }

    private static Placement WallAt(ShipDocument doc, int x, int y) =>
        doc.Placements.First(p => p.X == x && p.Y == y && p.DefName != "Floor");

    [Fact]
    public void An_undamaged_ship_has_lost_nothing()
    {
        var cat = Cat();
        var doc = Ship(cat);
        var baseline = DamageFallout.Baseline(doc, cat)!;

        Assert.True(DamageFallout.Compare(new DamageState().Project(doc), cat, baseline).IsEmpty);
    }

    [Fact]
    public void A_compartment_that_loses_a_wall_is_reported_as_open_to_space()
    {
        var cat = Cat();
        var doc = Ship(cat);
        var baseline = DamageFallout.Baseline(doc, cat)!;

        var state = new DamageState();
        state.Apply(WallAt(doc, 2, 0), "Wall", WallPool, cat);

        var air = DamageFallout.Compare(state.Project(doc), cat, baseline)
            .Consequences.Where(c => c.Kind == FalloutKind.Air).ToList();

        // Exactly one: the compartment that was sealed and is not any more. The strip at x=7 was already open
        // before the hit and is the design's problem, not the strike's.
        var one = Assert.Single(air);
        Assert.Equal(3, one.Cells.Count);
        Assert.Contains((2, 1), one.Cells);
    }

    [Fact]
    public void A_compartment_that_was_already_open_is_never_reported()
    {
        var cat = Cat();
        var doc = Ship(cat);
        var baseline = DamageFallout.Baseline(doc, cat)!;

        // Only the pre-existing open floor is in the baseline as a void room, so it is not a sealed compartment
        // and cannot be lost. Firing anywhere must not surface it.
        var state = new DamageState();
        state.Apply(WallAt(doc, 2, 0), "Wall", WallPool, cat);

        var report = DamageFallout.Compare(state.Project(doc), cat, baseline);

        Assert.DoesNotContain(report.Consequences, c => c.Cells.Contains((7, 1)) || c.Cells.Contains((8, 1)));
    }

    [Fact]
    public void Firing_again_does_not_re_report_what_the_first_shot_cost()
    {
        var cat = Cat();
        var doc = Ship(cat);
        var baseline = DamageFallout.Baseline(doc, cat)!;

        var state = new DamageState();
        state.Apply(WallAt(doc, 2, 0), "Wall", WallPool, cat);
        var first = DamageFallout.Compare(state.Project(doc), cat, baseline);

        // A second wall gone from the same compartment. It is still one compartment and still one consequence:
        // the count of what is broken is not part of what identifies it, which is what the old title-and-cells
        // key got wrong.
        state.Apply(WallAt(doc, 3, 0), "Wall", WallPool, cat);
        var second = DamageFallout.Compare(state.Project(doc), cat, baseline);

        Assert.Equal(
            first.Consequences.Count(c => c.Kind == FalloutKind.Air),
            second.Consequences.Count(c => c.Kind == FalloutKind.Air));
    }

    [Fact]
    public void A_wall_that_breaks_into_a_damaged_wall_still_holds_air()
    {
        var cat = Cat();
        var doc = Ship(cat, "SoftWall");
        var baseline = DamageFallout.Baseline(doc, cat)!;

        // It broke, and what stands there is a different def — but a damaged wall is still IsInstalled and still
        // carries IsWall, exactly as ItmWall1x1Dmg does in the game's own data (it keeps IsCheckRoom too, and
        // loses only half its StatGasPressureMax). So the compartment is intact and nothing is reported.
        var state = new DamageState();
        var (broke, to, gone) = state.Apply(WallAt(doc, 2, 0), "SoftWall", WallPool, cat);

        Assert.True(broke);
        Assert.Equal("SoftWallDmg", to);
        Assert.False(gone);
        Assert.DoesNotContain(
            DamageFallout.Compare(state.Project(doc), cat, baseline).Consequences, c => c.Kind == FalloutKind.Air);
    }

    [Fact]
    public void A_compartment_inside_a_named_zone_is_reported_by_that_name()
    {
        var cat = Cat();
        var doc = Ship(cat);
        var zone = new ShipZone { Name = "sick bay" };
        for (var x = 1; x <= 3; x++) zone.Tiles.Add((x, 1));
        doc.AddZone(zone);

        var baseline = DamageFallout.Baseline(doc, cat)!;
        var state = new DamageState();
        state.Apply(WallAt(doc, 2, 0), "Wall", WallPool, cat);

        var air = Assert.Single(
            DamageFallout.Compare(state.Project(doc), cat, baseline).Consequences, c => c.Kind == FalloutKind.Air);
        Assert.Contains("sick bay", air.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void A_system_that_stops_working_is_reported_in_the_console_s_own_words()
    {
        var cat = Cat();
        var doc = Ship(cat);
        doc.Add(Fixtures.P("Console", 2, 1));

        var baseline = DamageFallout.Baseline(doc, cat)!;
        var console = doc.Placements.First(p => p.DefName == "Console");

        var state = new DamageState();
        state.Apply(console, "Console", WallPool, cat);

        var systems = DamageFallout.Compare(state.Project(doc), cat, baseline)
            .Consequences.Where(c => c.Kind == FalloutKind.System).ToList();

        // NAV STATION went from ONLINE to NOT FOUND. Every other row was already failing on a hull this bare, so
        // only the one that actually changed is reported.
        var row = Assert.Single(systems);
        Assert.Contains("NAV STATION", row.Title, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two hulls have to agree on what a part <b>is</b>, and only a projection carries that across.
    ///
    /// <para>A <see cref="ShipDocument.Snapshot"/> mints a fresh id for every placement, which is right for what
    /// it is (an independent document) and fatal here: every device would read as a different device on the two
    /// sides, so nothing would ever match and no power or reach loss could be reported at all. Nothing else in the
    /// report would look wrong, which is what makes it worth pinning.</para>
    /// </summary>
    [Fact]
    public void A_projection_keeps_each_part_s_identity_and_a_snapshot_does_not()
    {
        var cat = Cat();
        var doc = Ship(cat);

        var projected = new DamageState().Project(doc);
        Assert.Equal(
            doc.Placements.Select(p => p.Id).ToHashSet(),
            projected.Placements.Select(p => p.Id).ToHashSet());

        Assert.Empty(doc.Snapshot().Placements.Select(p => p.Id).Intersect(doc.Placements.Select(p => p.Id)));
    }

    [Fact]
    public void A_device_cut_off_from_its_generator_is_reported_as_losing_power()
    {
        var cat = new Fixtures()
            .Part("Gen", tileConds: ["IsPowerPath"], startingConds: ["IsPowerGen", "IsInstalled"],
                  category: "POWR", powerOutput: (0, 0))
            .Part("Cond", tileConds: ["IsPowerConduit", "IsPowerPath"], category: "POWR",
                  condValues: new Dictionary<string, double> { ["StatDamageMax"] = WallPool })
            .Part("Dev", tileConds: ["IsPowerPath"], startingConds: ["IsInstalled"], category: "FURN",
                  powerInputs: [(0, 0)])
            .Build();
        var doc = Fixtures.Doc(cat,
            Fixtures.P("Gen", 0, 0), Fixtures.P("Cond", 1, 0), Fixtures.P("Dev", 2, 0));

        var baseline = DamageFallout.Baseline(doc, cat)!;
        var state = new DamageState();
        // The one length of conduit between the generator and the device.
        state.Apply(doc.Placements[1], "Cond", WallPool, cat);

        var power = Assert.Single(
            DamageFallout.Compare(state.Project(doc), cat, baseline).Consequences,
            c => c.Kind == FalloutKind.Power);
        Assert.Contains("lost power", power.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void A_part_that_is_only_chipped_costs_the_ship_nothing()
    {
        var cat = Cat();
        var doc = Ship(cat);
        var baseline = DamageFallout.Baseline(doc, cat)!;

        var state = new DamageState();
        state.Apply(WallAt(doc, 2, 0), "Wall", WallPool - 1, cat);   // damage, but the wall is still a wall

        Assert.True(DamageFallout.Compare(state.Project(doc), cat, baseline).IsEmpty);
    }
}
