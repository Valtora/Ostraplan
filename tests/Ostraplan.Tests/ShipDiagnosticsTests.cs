using System;
using System.Collections.Generic;
using System.Linq;
using Ostraplan.Core;
using Xunit;
using Xunit.Abstractions;

namespace Ostraplan.Tests;

/// <summary>
/// The ship-diagnostic port (<see cref="ShipDiagnostics"/>): the sixteen rows the game's own nav-console
/// Diagnostics module prints, answered from a design. Nothing in the ship data bakes these, so correctness rests
/// on (a) pinning the row captions and the cutoffs lifted from the decompile, and (b) synthetic ships whose every
/// system is present or absent by construction. A handful of cases place the real parts to guard the map-point
/// and power-network geometry the readout leans on.
/// </summary>
public class ShipDiagnosticsTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    // ---- pinned surface ----

    /// <summary>
    /// The captions, verbatim and in order from <c>ShipStatus.aNames</c>. The whole point of the feature is that a
    /// player can hold this next to the console, so a caption drifting from the game's is a real defect.
    /// </summary>
    [Fact]
    public void Row_captions_match_the_games_own()
    {
        Assert.Equal(16, ShipDiagnostics.Names.Length);
        Assert.Equal(
        [
            "VESSEL RATING CODE:", "VESSEL MASS:", "TRANSPONDER:", "TRANSPONDER ANTENNA:", "NAV STATION:",
            "REACTOR:", "REACTOR HE3:", "REACTOR D2O:", "RCS THRUSTERS:", "RCS DISTRIBUTOR:", "RCS REMASS:",
            "BACKUP POWER:", "LIFE SUPPORT WORKING O2 PUMPS:", "LIFE SUPPORT O2 STORES:", "LIFE SUPPORT HEAT:",
            "LIFE SUPPORT COOL:",
        ], ShipDiagnostics.Names);
    }

    /// <summary>
    /// The pass/fail cutoffs, which are literals inside <c>ShipStatus.PrintStatus</c> and so invisible to data
    /// diffing: a game patch can move any of them with nothing in <c>data/</c> to show for it, and this test is
    /// the only warning there would be.
    /// </summary>
    [Fact]
    public void Ported_thresholds_are_pinned()
    {
        Assert.Equal(100.0, ShipDiagnosticsThresholds.He3Kg);
        Assert.Equal(1000.0, ShipDiagnosticsThresholds.D2OKg);
        Assert.Equal(200.0, ShipDiagnosticsThresholds.RcsRemassKg);
        Assert.Equal(20.0, ShipDiagnosticsThresholds.BackupPowerKWh);
        Assert.Equal(35.0, ShipDiagnosticsThresholds.O2StoresKg);
        Assert.Equal(1, ShipDiagnosticsThresholds.MinRcsClustersOn);
    }

    // ---- synthetic ships ----

    /// <summary>A hull with nothing but floor fails every system row, and neither of the two informational rows
    /// (the rating code and the mass) is ever counted as a fault.</summary>
    [Fact]
    public void A_bare_hull_fails_every_system_row()
    {
        var cat = DiagCatalog();
        var report = Run(cat, Fixtures.P("Floor", 0, 0));

        Assert.Equal(16, report.Rows.Count);
        Assert.Equal(14, report.FaultCount);   // rows 0 and 1 are informational, the other fourteen are systems
        Assert.All(report.Rows.Skip(2), r => Assert.Equal(DiagState.Bad, r.State));
        Assert.All(report.Rows.Take(2), r => Assert.Equal(DiagState.Neutral, r.State));

        // and every fault says what is missing, since a checklist with no fix is just a complaint
        Assert.All(report.Faults, r => Assert.False(string.IsNullOrWhiteSpace(r.Note)));
        Assert.Equal("NOT FOUND", Row(report, "TRANSPONDER:").Value);
        Assert.Equal("0/0", Row(report, "TRANSPONDER ANTENNA:").Value);
        Assert.Equal("NO CONSOLE", Row(report, "BACKUP POWER:").Value);
    }

    /// <summary>A fully kitted ship reads clean, which is the state a design is aiming at.</summary>
    [Fact]
    public void A_fully_kitted_ship_reads_all_green()
    {
        var cat = DiagCatalog();
        var report = Run(cat, [.. KittedShip()]);

        foreach (var r in report.Rows) _out.WriteLine($"{r.Name,-32}{r.Value}  [{r.State}]");
        Assert.Equal(0, report.FaultCount);
        Assert.All(report.Rows.Skip(2), r => Assert.Equal(DiagState.Good, r.State));
    }

    /// <summary>
    /// A system that is installed but switched off reads OFFLINE, not NOT FOUND. The distinction is the whole
    /// value of the row: "you forgot to build one" and "you built one and left it off" want different fixes.
    /// </summary>
    [Fact]
    public void An_installed_but_switched_off_system_reads_offline()
    {
        var cat = DiagCatalog();
        var report = Run(cat, Fixtures.P("Floor", 0, 0), Fixtures.P("XpdrOff", 1, 0),
            Fixtures.P("HeaterOff", 2, 0), Fixtures.P("AntennaOff", 3, 0));

        var xpdr = Row(report, "TRANSPONDER:");
        Assert.Equal("OFFLINE", xpdr.Value);
        Assert.Equal(DiagState.Bad, xpdr.State);
        Assert.Contains("switched off", xpdr.Note);

        Assert.Equal("OFFLINE", Row(report, "LIFE SUPPORT HEAT:").Value);
        Assert.Equal("0/1", Row(report, "TRANSPONDER ANTENNA:").Value);   // counted, but none radiating
    }

    /// <summary>
    /// The row people misread: the game wants <b>more than one</b> switched-on RCS cluster, so a single healthy
    /// thruster still reads red. One thruster can push but not turn the ship.
    /// </summary>
    [Fact]
    public void One_rcs_cluster_is_a_fault_and_two_is_not()
    {
        var cat = DiagCatalog();

        var one = Row(Run(cat, Fixtures.P("Floor", 0, 0), Fixtures.P("Cluster", 1, 0)), "RCS THRUSTERS:");
        Assert.Equal("1/1", one.Value);
        Assert.Equal(DiagState.Bad, one.State);
        Assert.Contains("Only one RCS cluster", one.Note);

        var two = Row(Run(cat, Fixtures.P("Floor", 0, 0), Fixtures.P("Cluster", 1, 0), Fixtures.P("Cluster", 2, 0)),
            "RCS THRUSTERS:");
        Assert.Equal("2/2", two.Value);
        Assert.Equal(DiagState.Good, two.State);
        Assert.Null(two.Note);
    }

    /// <summary>
    /// The reactant cutoffs are strict inequalities in the game (<c>&gt; 100</c>, <c>&gt; 1000</c>), so a tank
    /// holding exactly the threshold still reads red. Off-by-one here would tell a player their ship is fuelled
    /// when the console will say it is not.
    /// </summary>
    [Theory]
    [InlineData(100.0, DiagState.Bad)]
    [InlineData(100.01, DiagState.Good)]
    public void He3_must_exceed_the_threshold_not_merely_meet_it(double kg, DiagState expected)
    {
        var cat = DiagCatalog(he3: kg);
        Assert.Equal(expected, Row(Run(cat, Fixtures.P("Floor", 0, 0), Fixtures.P("He3Tank", 1, 0)), "REACTOR HE3:").State);
    }

    [Theory]
    [InlineData(1000.0, DiagState.Bad)]
    [InlineData(1000.01, DiagState.Good)]
    public void D2o_must_exceed_the_threshold_not_merely_meet_it(double kg, DiagState expected)
    {
        var cat = DiagCatalog(d2o: kg);
        Assert.Equal(expected, Row(Run(cat, Fixtures.P("Floor", 0, 0), Fixtures.P("D2OTank", 1, 0)), "REACTOR D2O:").State);
    }

    /// <summary>
    /// Backup power is read at the nav console's own power inputs, so a battery the conduit network never
    /// reaches counts for nothing — exactly the mistake the row exists to catch. Ported from
    /// <c>Powered.PowerConnected</c> via <see cref="PowerNetwork.PowerConnectedTo"/>.
    /// </summary>
    [Fact]
    public void Backup_power_counts_only_batteries_the_console_can_reach()
    {
        var cat = DiagCatalog();

        // console at (0,0), whose power input lands one tile south at (0,1); a battery two conduits along from it
        var wired = Run(cat, Fixtures.P("Floor", 0, 0), Fixtures.P("Nav", 0, 0),
            Fixtures.P("Cond", 0, 1), Fixtures.P("Cond", 1, 1), Fixtures.P("Battery", 2, 1));
        var live = Row(wired, "BACKUP POWER:");
        Assert.Equal(DiagState.Good, live.State);
        Assert.Equal("81 kWh", live.Value);

        // the same battery, off the end of the run: nothing reaches the console
        var stranded = Run(cat, Fixtures.P("Floor", 0, 0), Fixtures.P("Nav", 0, 0),
            Fixtures.P("Cond", 0, 1), Fixtures.P("Cond", 1, 1), Fixtures.P("Battery", 8, 8));
        var dead = Row(stranded, "BACKUP POWER:");
        Assert.Equal(DiagState.Bad, dead.State);
        Assert.Equal("0 kWh", dead.Value);
        Assert.Contains("conduit", dead.Note);
    }

    /// <summary>
    /// O2 stores are measured in the canisters at a pump's gas input, not across the ship — the game's own rule
    /// (<c>ShipStatus.GetO2UnderPump</c>). A hold full of oxygen with no pump plumbed to a can reads zero, and
    /// saying so is the difference between a useful warning and a baffling one.
    /// </summary>
    [Fact]
    public void O2_stores_are_measured_under_the_pumps_not_across_the_ship()
    {
        var cat = DiagCatalog();

        // a canister with plenty of O2, but no pump anywhere
        var stowed = Run(cat, Fixtures.P("Floor", 0, 0), Fixtures.P("RtaO2", 5, 5));
        Assert.Equal("0/0", Row(stowed, "LIFE SUPPORT WORKING O2 PUMPS:").Value);
        Assert.Equal(DiagState.Bad, Row(stowed, "LIFE SUPPORT O2 STORES:").State);
        Assert.Contains("stowed elsewhere", Row(stowed, "LIFE SUPPORT O2 STORES:").Note);

        // a pump whose gas input lands on empty floor is installed but fed by nothing
        var dry = Run(cat, Fixtures.P("Floor", 0, 0), Fixtures.P("Pump", 2, 2), Fixtures.P("RtaO2", 8, 8));
        var dryPumps = Row(dry, "LIFE SUPPORT WORKING O2 PUMPS:");
        Assert.Equal("0/1", dryPumps.Value);
        Assert.Contains("gas-input tile", dryPumps.Note);

        // the can on the pump's gas input (one tile north, from the "GasInput" map point) feeds it
        var fed = Run(cat, Fixtures.P("Floor", 0, 0), Fixtures.P("Pump", 2, 2), Fixtures.P("RtaO2", 2, 1));
        Assert.Equal("1/1", Row(fed, "LIFE SUPPORT WORKING O2 PUMPS:").Value);
        var stores = Row(fed, "LIFE SUPPORT O2 STORES:");
        Assert.Equal(DiagState.Good, stores.State);
        _out.WriteLine($"fed pump stores: {stores.Value}");
    }

    /// <summary>
    /// The three deliberate divergences from a literal port are stated in the row itself, not buried: a planner
    /// that quietly answers a different question than the console is worse than one that answers none.
    /// </summary>
    [Fact]
    public void The_divergent_rows_say_so_in_their_own_note()
    {
        var cat = DiagCatalog();
        var report = Run(cat, [.. KittedShip()]);

        Assert.Contains("registration", Row(report, "TRANSPONDER:").Note);
        Assert.Contains("reports itself online", Row(report, "NAV STATION:").Note);
        Assert.Contains("until the reactor is lit", Row(report, "REACTOR:").Note);
    }

    // ---- real parts ----

    /// <summary>
    /// The battery-to-console power path against the game's real defs, which is where the map-point geometry can
    /// be quietly wrong: the console's input point and the battery's output point are both authored offsets, not
    /// tile centres, and the synthetic case cannot catch a mistake in resolving them.
    /// </summary>
    [SkippableFact]
    public void A_real_battery_wired_to_a_real_console_reads_its_charge()
    {
        var g = TestData.RequireGame();
        RequireDefs(g.Catalog, "ItmStationNav", "ItmBattery02", "ItmConduit00");

        var doc = new ShipDocument(g.Catalog);
        Place(doc, "ItmStationNav", 10, 10);
        Place(doc, "ItmBattery02", 16, 16);

        // The console's power input (PowerA) and the battery's output are authored pixel offsets, not the parts'
        // own tiles, and resolving them is exactly what this test guards — so read where they land rather than
        // assuming, and lay conduit over the rectangle spanning the two. Anything less would guard nothing.
        var probe = ShipGrid.FromDocument(doc, g.Catalog);
        var (inX, inY) = EndpointTile(probe, g.Catalog, "ItmStationNav", d => d.PowerInputPoints[0]);
        var (outX, outY) = EndpointTile(probe, g.Catalog, "ItmBattery02", d => d.PowerOutputPoint!.Value);
        _out.WriteLine($"console input lands on ({inX},{inY}) · battery output on ({outX},{outY})");

        for (var x = Math.Min(inX, outX); x <= Math.Max(inX, outX); x++)
        for (var y = Math.Min(inY, outY); y <= Math.Max(inY, outY); y++)
            Place(doc, "ItmConduit00", x, y);

        var report = Analyze(doc, g.Catalog);
        var power = Row(report, "BACKUP POWER:");
        _out.WriteLine($"real console + ItmBattery02: {power.Value} [{power.State}]");
        Assert.Equal(DiagState.Good, power.State);
        Assert.Equal("81 kWh", power.Value);   // the def authors StatPower 80.96 kWh
        Assert.Equal(DiagState.Good, Row(report, "NAV STATION:").State);

        // and with the conduit gone the same two parts read nothing, so the pass above is the network and not
        // merely the presence of a battery
        var unwired = new ShipDocument(g.Catalog);
        Place(unwired, "ItmStationNav", 10, 10);
        Place(unwired, "ItmBattery02", 16, 16);
        Assert.Equal("0 kWh", Row(Analyze(unwired, g.Catalog), "BACKUP POWER:").Value);
    }

    /// <summary>The document tile one of a part's authored power map points resolves to.</summary>
    private static (int X, int Y) EndpointTile(ShipGrid grid, Catalog catalog, string defName,
        Func<PartDef, (double X, double Y)> point)
    {
        var part = grid.Parts.Single(p => p.Part.DefName == defName);
        var tile = grid.MapPointTile(part, point(catalog.ByDefName[defName]));
        Assert.True(tile >= 0, $"{defName}'s power map point fell off the grid");
        return grid.GridToDoc(tile);
    }

    /// <summary>
    /// The O2 rows against the real air pump and O2 RTA. The pump's <c>GasInput</c> point is an authored pixel
    /// offset, and only the running (<c>OnG</c>) form even declares one, so this is the case that proves the
    /// planner and the game agree about what "a fed pump" means.
    /// </summary>
    [SkippableFact]
    public void A_real_air_pump_fed_by_a_real_o2_rta_reads_its_stores()
    {
        var g = TestData.RequireGame();
        // Found by trigger rather than by name: the palette builds the pump's running (…OnG) form via
        // Catalog.PreferPoweredState, so the buildable def's name is a detail of that mapping, not of this test.
        var pump = g.Catalog.Parts.FirstOrDefault(p => Fires(g.Catalog, ShipValue.PumpTrigger, p)
            && p.MapPoints.Keys.Any(k => k.Contains("GasInput")));
        var can = g.Catalog.Parts.FirstOrDefault(p => Fires(g.Catalog, ShipValue.O2CanTrigger, p)
            && p.StartingCondValues.GetValueOrDefault("StatGasMolO2") > 1000);
        Skip.If(pump is null, "no buildable air pump with a gas input in the catalog");
        Skip.If(can is null, "no buildable O2 RTA with oxygen in the catalog");
        _out.WriteLine($"pump {pump!.DefName} · can {can!.DefName}");

        var doc = new ShipDocument(g.Catalog);
        Place(doc, pump.DefName, 10, 10);
        var unfed = Analyze(doc, g.Catalog);
        Assert.Equal("0/1", Row(unfed, "LIFE SUPPORT WORKING O2 PUMPS:").Value);

        // The pump's GasInput is an authored pixel offset; read where it lands rather than assuming.
        var probe = ShipGrid.FromDocument(doc, g.Catalog);
        var placed = probe.Parts.Single();
        var gasInput = placed.Part.MapPoints.First(p => p.Key.Contains("GasInput")).Value;
        var (gx, gy) = probe.GridToDoc(probe.MapPointTile(placed, gasInput));
        _out.WriteLine($"{pump.DefName} at (10,10): gas input lands on ({gx},{gy})");

        Place(doc, can.DefName, gx, gy);
        var fed = Analyze(doc, g.Catalog);
        var pumps = Row(fed, "LIFE SUPPORT WORKING O2 PUMPS:");
        var stores = Row(fed, "LIFE SUPPORT O2 STORES:");
        _out.WriteLine($"{pumps.Value} pumps · {stores.Value}");
        Assert.Equal("1/1", pumps.Value);
        Assert.Equal(DiagState.Good, pumps.State);
        Assert.Equal(DiagState.Good, stores.State);

        // the mass is the can's own oxygen at the game's molar mass, not a figure this test invented
        var expected = ShipValue.MolarMass("O2") * can.StartingCondValues["StatGasMolO2"];
        Assert.Equal($"{expected:N2} kg", stores.Value);
        Assert.True(expected > ShipDiagnosticsThresholds.O2StoresKg);
    }

    private static bool Fires(Catalog catalog, string trigger, PartDef part) =>
        catalog.Triggers.TryGetValue(trigger, out var ct)
        && CondEval.Triggered(ct, [.. part.StartingConds], catalog);

    // ---- helpers ----

    private static DiagnosticRow Row(ShipDiagnosticReport report, string name) =>
        report.Rows.Single(r => r.Name == name);

    private static ShipDiagnosticReport Run(Catalog cat, params Placement[] placements) =>
        Analyze(Fixtures.Doc(cat, placements), cat);

    private static ShipDiagnosticReport Analyze(ShipDocument doc, Catalog cat)
    {
        var grid = ShipGrid.FromDocument(doc, cat);
        var partition = RoomBuilder.Build(grid);
        return ShipDiagnostics.Build(doc, grid, cat, Rating.Calculate(grid, partition, cat),
            Propulsion.Estimate(doc, grid, cat));
    }

    private static void RequireDefs(Catalog catalog, params string[] defs)
    {
        foreach (var d in defs)
            Skip.IfNot(catalog.ByDefName.ContainsKey(d), $"'{d}' is not in the catalog");
    }

    private static void Place(ShipDocument doc, string def, int x, int y) =>
        new PlaceCommand(new Placement { DefName = def, X = x, Y = y }).Do(doc);

    /// <summary>One of every system the diagnostic looks for, laid out so each feed lands where it must.</summary>
    private static IEnumerable<Placement> KittedShip() =>
    [
        Fixtures.P("Floor", 0, 0),
        Fixtures.P("Xpdr", 1, 0), Fixtures.P("Antenna", 2, 0),
        Fixtures.P("Nav", 0, 0), Fixtures.P("Core", 4, 0),
        Fixtures.P("He3Tank", 5, 0), Fixtures.P("D2OTank", 6, 0),
        Fixtures.P("Cluster", 7, 0), Fixtures.P("Cluster", 8, 0),
        Fixtures.P("Distro", 4, 4), Fixtures.P("RtaN2", 4, 3),   // tank on the distributor's GasInput point
        Fixtures.P("Cond", 0, 1), Fixtures.P("Battery", 1, 1),   // conduit run onto the console's input tile
        Fixtures.P("Pump", 2, 6), Fixtures.P("RtaO2", 2, 5),     // can on the pump's GasInput point
        Fixtures.P("Heater", 6, 6), Fixtures.P("Cooler", 7, 6),
    ];

    /// <summary>
    /// A game-free catalog carrying the game's real diagnostic triggers and one part satisfying each, so the
    /// readout is exercised through <see cref="CondEval"/> exactly as it is on real data. Gas-input and
    /// power-output map points use the game's own +y-up pixel offsets (16 px = one tile), so "one tile north"
    /// means the same thing here as it does in <c>data/condowners</c>.
    /// </summary>
    private static Catalog DiagCatalog(double he3 = 5000, double d2o = 40_000)
    {
        var f = new Fixtures();
        f.Floor();

        f.Trig(ShipDiagnostics.XpdrTrigger, ["IsTransponder", "IsInstalled"], ["IsDamaged"]);
        f.Trig(ShipDiagnostics.XpdrAntTrigger, ["IsAntennaXPDR", "IsInstalled"]);
        f.Trig(ShipDiagnostics.NavStationTrigger, ["IsNavStation", "IsInstalled"], ["IsDamaged"]);
        f.Trig(ShipDiagnostics.ReactorTrigger, ["IsReactorIC"]);
        f.Trig(ShipDiagnostics.He3TankTrigger, ["IsVesselHe3", "IsInstalled"]);
        f.Trig(ShipDiagnostics.D2OTankTrigger, ["IsVesselH2", "IsInstalled"]);
        f.Trig(ShipDiagnostics.RcsClusterTrigger, ["IsRCSCluster", "IsInstalled"]);
        f.Trig(ShipDiagnostics.RcsDistroTrigger, ["IsRCSReg", "IsInstalled"]);
        f.Trig(ShipDiagnostics.HeaterTrigger, ["IsHeater", "IsInstalled"]);
        f.Trig(ShipDiagnostics.CoolerTrigger, ["IsCooler", "IsInstalled"]);
        f.Trig(ShipValue.PumpTrigger, ["IsAirPump", "IsInstalled"]);
        f.Trig(ShipValue.O2CanTrigger, ["IsVesselO2", "IsRTA", "IsInstalled"]);
        f.Trig(Propulsion.RcsDistroOnTrigger, ["IsRCSReg", "IsInstalled"], ["IsOff"]);
        f.Trig(Propulsion.RcsFeedTrigger, ["IsAirtight"], ["IsHuman", "IsSystem"]);

        f.Part("Xpdr", startingConds: ["IsTransponder", "IsInstalled"], category: "SENS");
        f.Part("XpdrOff", startingConds: ["IsTransponder", "IsInstalled", "IsOff"], category: "SENS");
        f.Part("Antenna", startingConds: ["IsAntennaXPDR", "IsInstalled"], category: "SENS");
        f.Part("AntennaOff", startingConds: ["IsAntennaXPDR", "IsInstalled", "IsOff"], category: "SENS");
        f.Part("Nav", startingConds: ["IsNavStation", "IsInstalled"], category: "CTRL",
            tileConds: ["IsPowerPath"], powerInputs: [(0, -16)]);   // PowerA, one tile south
        f.Part("Core", startingConds: ["IsReactorIC", "IsInstalled"], category: "POWR");
        f.Part("He3Tank", startingConds: ["IsVesselHe3", "IsInstalled"], category: "POWR",
            condValues: new Dictionary<string, double> { ["StatSolidHe3"] = he3 });
        f.Part("D2OTank", startingConds: ["IsVesselH2", "IsInstalled"], category: "POWR",
            condValues: new Dictionary<string, double> { ["StatLiqD2O"] = d2o });
        f.Part("Cluster", startingConds: ["IsRCSCluster", "IsInstalled"], category: "HULL");
        f.Part("Distro", startingConds: ["IsRCSReg", "IsInstalled"], category: "HVAC",
            mapPoints: new Dictionary<string, (double X, double Y)> { ["GasInput01"] = (0, 16) });
        f.Part("Heater", startingConds: ["IsHeater", "IsInstalled"], category: "HVAC");
        f.Part("HeaterOff", startingConds: ["IsHeater", "IsInstalled", "IsOff"], category: "HVAC");
        f.Part("Cooler", startingConds: ["IsCooler", "IsInstalled"], category: "HVAC");
        f.Part("Pump", startingConds: ["IsAirPump", "IsInstalled"], category: "HVAC",
            mapPoints: new Dictionary<string, (double X, double Y)> { ["GasInput"] = (0, 16) });

        // An RTA is airtight, so it is valid RCS feed as well as an O2 store — the game makes the same part do
        // both jobs, which is how a Katydid runs its thrusters off oxygen.
        f.Part("RtaO2", startingConds: ["IsVesselO2", "IsRTA", "IsInstalled", "IsAirtight"], category: "HVAC",
            condValues: new Dictionary<string, double> { ["StatGasMolO2"] = 13_373 });
        f.Part("RtaN2", startingConds: ["IsRTA", "IsInstalled", "IsAirtight"], category: "HVAC",
            condValues: new Dictionary<string, double> { ["StatGasMolN2"] = 13_373 });

        f.Part("Cond", tileConds: ["IsPowerConduit", "IsPowerPath"], category: "POWR");
        f.Part("Battery", tileConds: ["IsPowerPath"], startingConds: ["IsPowerStorage", "IsInstalled"],
            category: "POWR", powerOutput: (0, 0),
            condValues: new Dictionary<string, double> { ["StatPower"] = 80.96 });

        return f.Build();
    }
}
