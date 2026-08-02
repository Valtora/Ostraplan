using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ostraplan.Core;
using Xunit;
using Xunit.Abstractions;

namespace Ostraplan.Tests;

/// <summary>
/// The propulsion port (<see cref="Propulsion"/>): the RCS acceleration / delta-v and torch acceleration /
/// reactant clock the game only ever shows on a nav console. Nothing in the ship data bakes these figures, so
/// there is no parity corpus to check against; correctness rests on (a) pinning every constant lifted from the
/// decompile, and (b) fixture ships whose module and tank counts are known by construction.
/// <para>The arithmetic tests build a <see cref="PropulsionEstimate"/> directly and need no game install.
/// The scan tests place real parts and skip without one.</para>
/// </summary>
public class PropulsionTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    private const string Cluster = "ItmRCSCluster01";        // operational form the palette builds
    private const string Distro = "ItmRCSDistro01";           // 3×3 socket grid, GasInput on its 4 cardinal neighbours
    private const string TankN2 = "ItmRTAN2";                 // 1×1, spawns full at 13,373 mol
    private const string CoreOff = "ItmFusionReactorCore01Off";
    private const string Laser = "ItmFusionLaserArray01";     // 1×3
    private const string Capacitor = "ItmCapacitor01";        // 2×5
    private const string Feeder = "ItmFusionPelletFeeder01";  // 1×3
    private const string Regulator = "ItmFusionFuelRegulator01"; // 3×5

    /// <summary>A bare estimate carrying only the fields the arithmetic uses, so the derived figures can be
    /// checked without a ship.</summary>
    private static PropulsionEstimate Est(
        double partsMass = 0, double rcsThrust = 0, double reactionMass = 0, double reactionMassMax = 0,
        double ve = 0, double pelletMax = 0, double d2o = 0, double he3 = 0, double extra = 0) =>
        new(partsMass, 0, extra, rcsThrust, 0, 0, 0, 0, reactionMass, reactionMassMax,
            ve > 0, ve, pelletMax, 0, 0, 0, 0, d2o, he3, [], []);

    // ---- constants ----

    /// <summary>
    /// Every magic number lifted out of the decompile, pinned. These are compiled into the DLL and invisible to
    /// data diffing, so they can drift between game patches with nothing to warn us: this test is the warning.
    /// The float-suffixed game literals are pinned in their <b>widened</b> form, because that is what the game's
    /// own double expressions consume (<c>DeltaVRemainingRCS</c> spells the divisor out as 0.7279999852180481,
    /// which is exactly <c>(double)0.728f</c>).
    /// </summary>
    [Fact]
    public void Ported_constants_are_pinned()
    {
        Assert.Equal(6.6845869117759804E-12, Propulsion.AuPerMetre);
        Assert.Equal(9.81, Propulsion.StandardGravity);
        Assert.Equal(0.7279999852180481, Propulsion.RcsMassFlow);          // 0.728f widened
        Assert.Equal(100.0, Propulsion.RcsAccelScale);
        Assert.Equal(70500000.0, Propulsion.FusionVeNominal);              // FusionIC.FUSION_VE
        Assert.Equal(332499980926.5137, Propulsion.FusionThrustConst);     // 0.35e12 W × 0.95 thrust-mode
        Assert.Equal(699999988079.071, Propulsion.FusionMassFlowConst);    // 2 × 0.35e12 W
        Assert.Equal(393.06358381502895, Propulsion.FusionFudge);          // FUSION_FUDGE
        Assert.Equal((double)0.667f, Propulsion.ReactantShareD2O);        // aReactantAmounts[0]
        Assert.Equal(1.0, Propulsion.ReactantShareHe3);
        Assert.Equal((double)5.26077E-09f, Propulsion.RcsAccelConst);
    }

    /// <summary>
    /// The collapsed forms of those constants, pinned separately so a change to any one of them shows up as a
    /// figure a person can sanity-check: one unit of RCS thrust strength is ~57.3 kN, RCS reaction mass leaves
    /// at ~78.7 km/s, and a nominal reactor makes ~1.85 MN per unit of pellet ceiling.
    /// </summary>
    [Fact]
    public void Collapsed_forms_are_pinned()
    {
        Assert.Equal(57293.6003858369, Est(rcsThrust: 1).RcsThrustNewtons, 6);

        // delta-v = exhaust velocity × reaction mass / mass; the thruster count cancels out entirely
        Assert.Equal(78700.00212798976, Est(partsMass: 1, rcsThrust: 1, reactionMass: 1).RcsDeltaV, 6);

        Assert.Equal(1853810.4130695637, Propulsion.FusionThrustMax(Propulsion.FusionVeNominal, 1), 6);
        Assert.Equal(0.055358282578308375, Propulsion.FusionMassFlow(Propulsion.FusionVeNominal, 1), 12);
    }

    // ---- RCS arithmetic ----

    /// <summary>
    /// Delta-v depends only on reaction mass over ship mass. It is the single most counter-intuitive thing
    /// about the game's model and the report says so out loud, so it is pinned: bolting on thrusters buys
    /// acceleration and no range at all.
    /// </summary>
    [Fact]
    public void Delta_v_is_independent_of_the_thruster_count()
    {
        var few = Est(partsMass: 50_000, rcsThrust: 2, reactionMass: 500);
        var many = Est(partsMass: 50_000, rcsThrust: 20, reactionMass: 500);

        Assert.Equal(few.RcsDeltaV, many.RcsDeltaV, 6);
        Assert.Equal(10 * few.RcsAccelG, many.RcsAccelG, 6);   // ...while acceleration scales exactly with count
        _out.WriteLine($"2 clusters: {few.RcsAccelG:0.000} G · 20 clusters: {many.RcsAccelG:0.000} G · both {few.RcsDeltaV:0.0} m/s");
    }

    /// <summary>Extra haul mass scales acceleration and delta-v <b>once</b>. The nav console's Reserves module
    /// applies the docked ratio a second time on top of an acceleration that already carries it; this follows
    /// <c>Ship.DeltaVRemainingRCS</c>, which is what the autopilot plans against.</summary>
    [Fact]
    public void Extra_mass_scales_the_figures_once()
    {
        var solo = Est(partsMass: 40_000, rcsThrust: 4, reactionMass: 800);
        var towing = solo.WithExtraMass(40_000);   // exactly doubles the mass

        Assert.Equal(80_000, towing.Mass);
        Assert.Equal(solo.RcsAccelG / 2, towing.RcsAccelG, 9);
        Assert.Equal(solo.RcsDeltaV / 2, towing.RcsDeltaV, 9);   // once, not (M/Mtotal)² = a quarter
        Assert.Equal(solo.TorchAccelG / 2, towing.TorchAccelG, 9);
    }

    /// <summary>A negative or unparseable haul mass is clamped away rather than producing a nonsense figure,
    /// and the estimate is otherwise untouched.</summary>
    [Fact]
    public void Extra_mass_never_goes_negative()
    {
        var e = Est(partsMass: 1000, rcsThrust: 1, reactionMass: 10);
        Assert.Equal(0, e.WithExtraMass(-5000).ExtraMass);
        Assert.Equal(e.RcsAccelG, e.WithExtraMass(-5000).RcsAccelG, 9);
    }

    /// <summary>No thrusters means no figures at all, not a division by zero. The game's expression divides by
    /// <c>fRCSCount</c>, so an unguarded port yields NaN on any design without RCS.</summary>
    [Fact]
    public void No_thrusters_yields_zero_not_nan()
    {
        var e = Est(partsMass: 10_000, rcsThrust: 0, reactionMass: 500);
        Assert.False(e.HasRcsFigures);
        Assert.Equal(0, e.RcsAccelG);
        Assert.Equal(0, e.RcsDeltaV);
        Assert.False(double.IsNaN(e.RcsDeltaV));
    }

    // ---- torch arithmetic ----

    /// <summary>The reactant clock runs on whichever reactant empties first, at full flow. A tank of deuterium
    /// with no helium-3 buys nothing.</summary>
    [Fact]
    public void Reactant_clock_uses_the_limiting_reactant()
    {
        var ve = Propulsion.FusionVeNominal;
        var flow = Propulsion.FusionMassFlow(ve, 4);

        var he3Short = Est(partsMass: 100_000, ve: ve, pelletMax: 4, d2o: 44_722.8, he3: 5_216);
        Assert.Equal("helium-3", he3Short.LimitingReactant);
        Assert.Equal(5_216 / flow / 3600.0, he3Short.ReactantHours, 6);

        var d2oShort = Est(partsMass: 100_000, ve: ve, pelletMax: 4, d2o: 100, he3: 5_216);
        Assert.Equal("deuterium", d2oShort.LimitingReactant);
        Assert.Equal(100 / (flow * Propulsion.ReactantShareD2O) / 3600.0, d2oShort.ReactantHours, 6);

        var dry = Est(partsMass: 100_000, ve: ve, pelletMax: 4, d2o: 44_722.8, he3: 0);
        Assert.Equal(0, dry.ReactantHours);
    }

    /// <summary>A reactor that cannot reach a pellet rate makes no thrust and burns nothing, and must not
    /// divide by its own zero.</summary>
    [Fact]
    public void Zero_pellet_max_yields_zero_not_nan()
    {
        var e = Est(partsMass: 100_000, ve: Propulsion.FusionVeNominal, pelletMax: 0, d2o: 44_722.8, he3: 5_216);
        Assert.False(e.HasTorchFigures);
        Assert.Equal(0, e.TorchAccelG);
        Assert.Equal(0, e.ReactantHours);
        Assert.Null(e.LimitingReactant);
    }

    // ---- fixture ships (need the game's real defs) ----

    /// <summary>
    /// The RCS feed is a plumbing question, not an inventory one: reaction mass counts only what sits on an
    /// installed distributor's <c>GasInput</c> map points. A tank one tile further out feeds nothing, which is
    /// exactly the mistake the report exists to catch.
    /// </summary>
    [SkippableFact]
    public void Reaction_mass_counts_only_tanks_on_a_distributor_gas_input()
    {
        var g = TestData.RequireGame();
        RequireDefs(g.Catalog, Distro, TankN2, Cluster);

        // Distro01 is a 3×3 socket grid whose four GasInput points sit on the cardinal neighbours of its
        // centre tile — cells its own socket mask leaves Blank, so a 1×1 tank legally occupies one.
        var doc = new ShipDocument(g.Catalog);
        Place(doc, Cluster, 30, 30);
        Place(doc, Distro, 20, 20);          // footprint (20,20)–(22,22), centre tile (21,21)
        var unplumbed = Measure(doc, g.Catalog);
        Assert.Equal(0, unplumbed.RcsReactionMass);
        Assert.Contains(unplumbed.RcsNotes, n => n.Contains("gas input"));

        Place(doc, TankN2, 21, 20);          // GasInput01, one tile north of the centre
        var one = Measure(doc, g.Catalog);
        var perTank = one.RcsReactionMass;
        _out.WriteLine($"one plumbed {TankN2}: {perTank:0.0} kg of {one.RcsReactionMassMax:0.0} kg");
        Assert.True(perTank > 300, $"a full N2 RTA should carry ~375 kg of N2, got {perTank:0.0}");
        Assert.Equal(1, one.RcsTankCount);
        Assert.Empty(one.RcsNotes);

        // the other three cardinal points feed too, and the total is linear in them
        Place(doc, TankN2, 21, 22);
        Place(doc, TankN2, 22, 21);
        Place(doc, TankN2, 20, 21);
        var four = Measure(doc, g.Catalog);
        Assert.Equal(4, four.RcsTankCount);
        Assert.Equal(perTank * 4, four.RcsReactionMass, 3);

        // ...whereas a tank parked out of reach of any gas input contributes nothing
        var stowed = new ShipDocument(g.Catalog);
        Place(stowed, Cluster, 30, 30);
        Place(stowed, Distro, 20, 20);
        Place(stowed, TankN2, 26, 26);
        var away = Measure(stowed, g.Catalog);
        Assert.Equal(0, away.RcsReactionMass);
        Assert.Equal(0, away.RcsTankCount);
    }

    /// <summary>An RTA spawns essentially brim-full, so a freshly designed ship's remaining and maximum
    /// reaction mass agree to within a rounding of the authored fill. The two are computed by completely
    /// different routes (summed <c>StatGasMol*</c> against <c>StatGasPressureMax × StatVolume</c> priced as N2),
    /// so their agreement is a real cross-check on both. They are close but not identical: the def authors
    /// 13,373 mol against a computed capacity of ~13,376, which is why this is a tolerance and not an equality.</summary>
    [SkippableFact]
    public void A_fresh_n2_tank_reads_full()
    {
        var g = TestData.RequireGame();
        RequireDefs(g.Catalog, Distro, TankN2, Cluster);

        var doc = new ShipDocument(g.Catalog);
        Place(doc, Cluster, 30, 30);
        Place(doc, Distro, 20, 20);
        Place(doc, TankN2, 21, 20);

        var p = Measure(doc, g.Catalog);
        _out.WriteLine($"remain {p.RcsReactionMass:0.00} kg · max {p.RcsReactionMassMax:0.00} kg");
        Assert.True(p.RcsReactionMass > 0 && p.RcsReactionMassMax > 0);
        Assert.InRange(p.RcsReactionMass / p.RcsReactionMassMax, 0.995, 1.0);
        Assert.InRange(p.RcsDeltaV / p.RcsDeltaVFull, 0.995, 1.0);
    }

    /// <summary>
    /// The pellet ceiling is the whole point of issue #16: a laser is dead weight without a capacitor to drive
    /// it, a feeder is dead weight without a fuel regulator, and the weaker of the two chains caps thrust. Built
    /// up one module at a time so each clamp is visible.
    /// </summary>
    [SkippableFact]
    public void Pellet_max_pairs_lasers_with_capacitors_and_feeders_with_regulators()
    {
        var g = TestData.RequireGame();
        RequireDefs(g.Catalog, CoreOff, Laser, Capacitor, Feeder, Regulator);

        // A 5×5 core at (10,10): its twelve Module points are the non-corner cells of its own footprint, so a
        // module attaches by overlapping one. Module01–03 run along the top row at (11,10)–(13,10);
        // Module04–06 down the right at (14,11)–(14,13); Module07–09 along the bottom; Module10–12 down the left.
        var doc = new ShipDocument(g.Catalog);
        Place(doc, CoreOff, 10, 10);

        var bare = Measure(doc, g.Catalog);
        Assert.True(bare.HasReactor);
        Assert.Equal(0, bare.PelletMax);
        Assert.Contains(bare.TorchNotes, n => n.Contains("laser array") && n.Contains("capacitor"));

        Place(doc, Laser, 11, 8);        // 1×3 reaching up into Module01
        Place(doc, Feeder, 13, 8);       // 1×3 reaching up into Module03
        var noDrivers = Measure(doc, g.Catalog);
        Assert.Equal(1, noDrivers.Lasers);
        Assert.Equal(1, noDrivers.Feeders);
        Assert.Equal(0, noDrivers.PelletMax);   // nothing drives them yet

        Place(doc, Capacitor, 14, 11);   // 2×5 over Module04–06
        Place(doc, Regulator, 10, 14);   // 3×5 over Module07–08
        var paired = Measure(doc, g.Catalog);
        Assert.Equal(1, paired.Capacitors);
        Assert.Equal(1, paired.Regulators);
        Assert.Equal(2, paired.PelletMax);      // 2 × min(min(1,2), min(1,2))
        Assert.True(paired.TorchAccelG > 0);

        // a second laser with no second feeder is capped by the feed side, so thrust does not move
        Place(doc, Laser, 12, 8);        // Module02
        var laserHeavy = Measure(doc, g.Catalog);
        Assert.Equal(2, laserHeavy.Lasers);
        Assert.Equal(2, laserHeavy.PelletMax);
        Assert.Equal(paired.TorchThrustNewtons, laserHeavy.TorchThrustNewtons, 3);
        Assert.Contains(laserHeavy.TorchNotes, n => n.Contains("capped by the feed side"));

        // balancing the feed side finally lifts it
        Place(doc, Feeder, 10, 11);      // 1×3 down Module10–12
        var balanced = Measure(doc, g.Catalog);
        Assert.Equal(2, balanced.Feeders);
        Assert.Equal(4, balanced.PelletMax);
        Assert.Equal(2 * paired.TorchThrustNewtons, balanced.TorchThrustNewtons, 3);
        _out.WriteLine($"pellet max {balanced.PelletMax} · {balanced.TorchThrustNewtons / 1000:0} kN · {balanced.TorchAccelG:0.00} G");
    }

    /// <summary>
    /// The installable core is the <c>…Off</c> form and only the ignited condowner carries <c>StatICVe</c>, so
    /// a literal read would report zero thrust for every design ever planned. The resolution through to
    /// <c>…Ignition</c> is what makes the torch figures mean anything.
    /// </summary>
    [SkippableFact]
    public void Exhaust_velocity_resolves_through_to_the_ignited_core()
    {
        var g = TestData.RequireGame();
        RequireDefs(g.Catalog, CoreOff);

        Assert.Equal(0, g.Catalog.ByDefName[CoreOff].StartingCondValues.GetValueOrDefault("StatICVe"));

        var doc = new ShipDocument(g.Catalog);
        Place(doc, CoreOff, 10, 10);
        var p = Measure(doc, g.Catalog);

        _out.WriteLine($"{CoreOff} resolved Ve = {p.FusionVe:#,0} m/s");
        Assert.Equal(70_500_000, p.FusionVe, 0);
    }

    /// <summary>The RCS thrust the propulsion figures divide by must be the same number the Maneuver grade is
    /// computed from, or the report contradicts itself two lines apart.</summary>
    [SkippableFact]
    public void Rcs_thrust_agrees_with_the_maneuver_input()
    {
        var g = TestData.RequireGame();
        RequireDefs(g.Catalog, Cluster);

        var doc = new ShipDocument(g.Catalog);
        Place(doc, Cluster, 10, 10);
        Place(doc, Cluster, 14, 10);
        Place(doc, Cluster, 18, 10);

        var grid = ShipGrid.FromDocument(doc, g.Catalog);
        var rating = Rating.Calculate(grid, RoomBuilder.Build(grid), g.Catalog);
        var p = Propulsion.Estimate(doc, grid, g.Catalog);

        Assert.Equal(rating.RcsThrust, p.RcsThrust, 9);
        Assert.Equal(3, p.RcsClustersPresent);
    }

    /// <summary>
    /// Propulsion mass is the game's own walk (<c>Ship.Mass</c>): every top-level condowner, with no
    /// <c>IsInstalled</c> filter, plus the loose items lying on the deck. Deliberately not
    /// <see cref="ShipRating.Mass"/>, which counts installed parts only — the report shows both and explains
    /// the difference, so the divergence is pinned rather than left to drift.
    /// </summary>
    [SkippableFact]
    public void Mass_counts_placed_parts_plus_loose_deck_items()
    {
        var g = TestData.RequireGame();
        var loose = g.Catalog.LooseItems.FirstOrDefault(p => p.StartingCondValues.GetValueOrDefault("StatMass") > 0);
        Skip.If(loose is null, "no loose item with mass in the catalog");
        RequireDefs(g.Catalog, Cluster);

        var doc = new ShipDocument(g.Catalog);
        Place(doc, Cluster, 10, 10);
        var structureOnly = Measure(doc, g.Catalog);
        Assert.Equal(0, structureOnly.LooseMass);

        new PlaceLooseCommand(new LooseObject { DefName = loose!.DefName, X = 20, Y = 20, Quantity = 3 }).Do(doc);
        var withDeck = Measure(doc, g.Catalog);

        var each = loose.StartingCondValues.GetValueOrDefault("StatMass");
        _out.WriteLine($"{loose.DefName} {each} kg × 3 · parts {withDeck.PartsMass:0.0} kg · loose {withDeck.LooseMass:0.0} kg");
        Assert.Equal(each * 3, withDeck.LooseMass, 3);
        Assert.Equal(structureOnly.PartsMass, withDeck.PartsMass, 3);
        Assert.Equal(withDeck.PartsMass + withDeck.LooseMass, withDeck.Mass, 3);
    }

    /// <summary>The haul mass is a design property, so it must survive a save and reopen.</summary>
    [SkippableFact]
    public void Extra_mass_round_trips_through_the_oplan()
    {
        var g = TestData.RequireGame();
        RequireDefs(g.Catalog, Cluster);

        var doc = new ShipDocument(g.Catalog) { ExtraMassKg = 42_500 };
        Place(doc, Cluster, 10, 10);

        var path = Path.Combine(Path.GetTempPath(), $"ostraplan-propulsion-{Guid.NewGuid():N}.oplan");
        try
        {
            OplanFile.FromDocument(doc, g.Index, new OplanMeta()).Save(path);
            var (reopened, missing) = OplanFile.Load(path).ToDocument(g.Catalog);
            Assert.Empty(missing);
            Assert.Equal(42_500, reopened.ExtraMassKg);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }

        // a design that hauls nothing writes no field at all, so an untouched .oplan does not grow
        var plain = new ShipDocument(g.Catalog);
        Place(plain, Cluster, 10, 10);
        Assert.Null(OplanFile.FromDocument(plain, g.Index, new OplanMeta()).ExtraMassKg);
    }

    /// <summary>
    /// The feed geometry against real ships rather than ones this test laid out. The map-point walk is the
    /// part most likely to be quietly wrong (it stands in for a physics raycast the planner cannot run), so it
    /// is exercised on the shipped fleet: a good number of core ships must resolve a real, non-zero RCS feed.
    /// </summary>
    [SkippableFact]
    public void Core_ships_resolve_a_real_rcs_feed()
    {
        var g = TestData.RequireGame();
        var resolver = new PartResolver(g.Index);

        int withDistro = 0, withFeed = 0;
        var examples = new List<string>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(g.Env.CoreDataDir, "ships"), "*.json"))
        foreach (var ship in ShipTemplate.ParseFile(File.ReadAllText(path)))
        {
            var grid = ShipGrid.FromTemplate(ship, resolver, g.Catalog);
            var doc = new ShipDocument(g.Catalog);   // templates carry no loose items or haul mass
            var p = Propulsion.Estimate(doc, grid, g.Catalog);
            if (p.RcsDistrosPresent == 0) continue;
            withDistro++;
            if (p.RcsReactionMass <= 0) continue;
            withFeed++;
            if (examples.Count < 5)
                examples.Add($"{ship.Name}: {p.RcsReactionMass:0} kg over {p.RcsTankCount} feeds, {p.RcsThrust:0} thrust");
        }

        foreach (var e in examples) _out.WriteLine(e);
        _out.WriteLine($"{withFeed} of {withDistro} core ships with a distributor resolve a fed RCS system");
        Skip.If(withDistro == 0, "no core ship carries an RCS distributor");
        // 108 of 111 at game 0.15.1.6. The bound sits at 90% so a handful of odd hulls (an unfuelled pod, a
        // wreck) can vary without noise, while a geometry regression — which would collapse this to near zero —
        // fails loudly.
        Assert.True(withFeed >= withDistro * 0.9,
            $"only {withFeed} of {withDistro} ships resolved a feed — the GasInput map-point walk has probably broken");
    }

    // ---- helpers ----

    private static void RequireDefs(Catalog catalog, params string[] defs)
    {
        foreach (var d in defs)
            Skip.IfNot(catalog.ByDefName.ContainsKey(d), $"'{d}' is not in the catalog");
    }

    private static void Place(ShipDocument doc, string def, int x, int y) =>
        new PlaceCommand(new Placement { DefName = def, X = x, Y = y }).Do(doc);

    private static PropulsionEstimate Measure(ShipDocument doc, Catalog catalog) =>
        Propulsion.Estimate(doc, ShipGrid.FromDocument(doc, catalog), catalog);
}
