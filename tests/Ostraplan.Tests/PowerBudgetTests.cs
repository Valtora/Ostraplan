using System.Collections.Generic;
using System.Linq;
using Ostraplan.Core;
using Xunit;
using Xunit.Abstractions;

namespace Ostraplan.Tests;

/// <summary>
/// <see cref="PowerBudget"/>: what a design draws, what it stores, how long the batteries last, and what the fusion
/// core costs and gives back. The game-free cases pin each rule on a synthetic catalog carrying the stock rates; the
/// gated ones hold the port against the real power-infos and against stock ships whose reactor states are known.
/// </summary>
public class PowerBudgetTests
{
    private readonly ITestOutputHelper _out;

    public PowerBudgetTests(ITestOutputHelper output) => _out = output;

    // ---- a synthetic catalog at the stock rates

    private const double HeaterRate = 0.0011;          // Heater01: 3.96 kW
    private const double PumpRate = 7.6e-05;           // AirPump02: 273.6 W
    private const double PumpTurboRate = 0.00015;      // AirPump02 on turbo: 540 W
    private const double BatteryKwh = 80.96;           // ItmBattery02

    private static PowerInfoDef Draws(string name, double rate, bool external = true) =>
        new(name, ["PowerA"])
        {
            Amount = rate, UsePowerCT = "TIsReadyUsePower", AllowExtPower = external, PowerOnInteraction = "MSOn",
        };

    private static readonly string[] Power = [Catalog.PowerTicker];

    private static Fixtures Base()
    {
        var f = new Fixtures();
        f.Part("Batt", tileConds: ["IsPowerPath"], startingConds: ["IsInstalled", "IsPowerStorage"],
            category: "POWR", powerOutput: (0, 0),
            condValues: new Dictionary<string, double> { ["StatPower"] = BatteryKwh, ["StatPowerMax"] = BatteryKwh });
        f.Part("Cond", tileConds: ["IsPowerConduit", "IsPowerPath"], category: "POWR");
        f.Part("Heater", tileConds: ["IsPowerPath"], startingConds: ["IsInstalled"],
            powerInputs: [(0, 0)], powerInfo: Draws("Heater01", HeaterRate), tickers: Power);
        f.Part("HeaterOff", tileConds: ["IsPowerPath"], startingConds: ["IsInstalled", "IsOff"],
            powerInputs: [(0, 0)], powerInfo: Draws("Heater01", HeaterRate), tickers: Power);
        f.Part("Box", tileConds: ["IsPowerPath"], startingConds: ["IsInstalled"]);
        f.Part("Pump", tileConds: ["IsPowerPath"], startingConds: ["IsInstalled", "IsSignalable", "IsTurbo"],
            powerInputs: [(0, 0)], tickers: Power, gpm: [("Panel A", "AirPump")],
            powerInfo: Draws("AirPump02", PumpRate) with { OverrideCond = "IsTurboOn", OverrideAmount = PumpTurboRate });
        f.GpmTemplate("AirPump", "strGUIPrefab", "GUIAirPump", "strInput01", "", "nKnobBus", "1");
        f.Part("Untimed", tileConds: ["IsPowerPath"], startingConds: ["IsInstalled"],
            powerInputs: [(0, 0)], powerInfo: Draws("Untimed", HeaterRate));
        f.Part("Module", tileConds: ["IsPowerPath"], startingConds: ["IsInstalled"],
            powerInputs: [(0, 0)], powerInfo: Draws("Module", HeaterRate, external: false), tickers: Power);
        f.Part("Overridden", tileConds: ["IsPowerPath"], startingConds: ["IsInstalled", "IsOverrideOff"],
            powerInputs: [(0, 0)], powerInfo: Draws("Overridden", HeaterRate), tickers: Power);
        // Three tiles wide with an input point at each end, like a heater's PowerA and PowerB, and no power path of
        // its own, so its two ends can sit on two different runs.
        f.Part("Bridge", w: 3, startingConds: ["IsInstalled"],
            powerInputs: [(-16, 0), (16, 0)], powerInfo: Draws("Bridge", HeaterRate), tickers: Power);
        return f;
    }

    private static PowerBudgetReport Measure(Catalog cat, ShipDocument doc) =>
        PowerBudget.Measure(doc, ShipGrid.FromDocument(doc, cat), cat);

    // ---- what draws

    [Fact]
    public void A_heater_on_a_battery_lasts_stored_charge_over_its_draw()
    {
        var cat = Base().Build();
        var doc = Fixtures.Doc(cat, Fixtures.P("Batt", 0, 0), Fixtures.P("Cond", 1, 0), Fixtures.P("Heater", 2, 0));

        var net = Assert.Single(Measure(cat, doc).Networks);

        Assert.Equal(HeaterRate * 3600, net.LoadKw, 9);                // 3.96 kW
        Assert.Equal(BatteryKwh, net.StoredKwh, 9);
        Assert.Equal(BatteryKwh / (HeaterRate * 3600), net.HoursOnBatteries!.Value, 9);   // about 20.4 h
        Assert.False(net.Sustained);
    }

    [Fact]
    public void Painted_condition_scales_battery_capacity()
    {
        var cat = Base().Build();
        var doc = Fixtures.Doc(cat, Fixtures.P("Batt", 0, 0), Fixtures.P("Heater", 1, 0));
        doc.Placements.Single(p => p.DefName == "Batt").Condition = 0.5;

        var battery = Assert.Single(Assert.Single(Measure(cat, doc).Networks).Batteries);

        // Powered.PowerStoredMax is StatPowerMax times the damage state, and a full battery is clamped down to it.
        Assert.Equal(BatteryKwh / 2, battery.CapacityKwh, 9);
        Assert.Equal(BatteryKwh / 2, battery.StoredKwh, 9);
    }

    [Fact]
    public void A_bus_knob_at_off_holds_a_pump_off()
    {
        var cat = Base().Build();
        var doc = Fixtures.Doc(cat, Fixtures.P("Batt", 0, 0), Fixtures.P("Pump", 1, 0));
        var pump = doc.Placements.Single(p => p.DefName == "Pump");

        Assert.Equal(PumpRate * 3600, Assert.Single(Measure(cat, doc).Networks).LoadKw, 9);

        pump.Device = new DeviceSettings { Bus = DeviceBusMode.Off };
        Assert.Empty(Assert.Single(Measure(cat, doc).Networks).Loads);
    }

    [Fact]
    public void A_pump_waiting_on_its_sensor_still_draws()
    {
        // Auto is the default and the sensor decides only whether gas moves, never whether Powered draws.
        var cat = Base().Build();
        var doc = Fixtures.Doc(cat, Fixtures.P("Batt", 0, 0), Fixtures.P("Pump", 1, 0));

        var load = Assert.Single(Assert.Single(Measure(cat, doc).Networks).Loads);
        Assert.Equal(PumpRate * 3600, load.Kw, 9);
        Assert.True(load.HasKnob);
    }

    [Fact]
    public void Turbo_swaps_in_the_override_rate()
    {
        var cat = Base().Build();
        var doc = Fixtures.Doc(cat, Fixtures.P("Batt", 0, 0), Fixtures.P("Pump", 1, 0));
        doc.Placements.Single(p => p.DefName == "Pump").Device = new DeviceSettings { Turbo = true };

        Assert.Equal(PumpTurboRate * 3600, Assert.Single(Measure(cat, doc).Networks).LoadKw, 9);
    }

    [Fact]
    public void An_unwired_off_form_still_draws_and_says_so()
    {
        var cat = Base().Build();
        var doc = Fixtures.Doc(cat, Fixtures.P("Batt", 0, 0), Fixtures.P("HeaterOff", 1, 0));

        var load = Assert.Single(Assert.Single(Measure(cat, doc).Networks).Loads);
        Assert.Equal(HeaterRate * 3600, load.Kw, 9);
        Assert.True(load.OffInPlan);
        Assert.False(load.HasKnob);   // no bus knob, so a breaker box is the only way to hold it off
    }

    [Fact]
    public void A_wired_off_form_loads_with_its_signal_off()
    {
        var cat = Base().Build();
        var doc = Fixtures.Doc(cat, Fixtures.P("Batt", 0, 0), Fixtures.P("HeaterOff", 1, 0), Fixtures.P("Box", 2, 0));
        var heater = doc.Placements.Single(p => p.DefName == "HeaterOff");
        var box = doc.Placements.Single(p => p.DefName == "Box");
        new AddLinkCommand(new DeviceLink(box.Id, heater.Id)).Do(doc);

        Assert.Empty(Assert.Single(Measure(cat, doc).Networks).Loads);
    }

    [Theory]
    [InlineData("Untimed")]      // no ticker grants IsReadyUsePower, so TIsReadyUsePower never fires
    [InlineData("Module")]       // bAllowExtPower false: the fusion modules run on what FusionIC gives them
    [InlineData("Overridden")]   // starts with IsOverrideOff
    public void Some_devices_never_draw_from_the_network(string def)
    {
        var cat = Base().Build();
        var doc = Fixtures.Doc(cat, Fixtures.P("Batt", 0, 0), Fixtures.P(def, 1, 0));

        var r = Measure(cat, doc);
        Assert.Empty(Assert.Single(r.Networks).Loads);
        Assert.Empty(r.Unconnected);
    }

    // ---- networks

    [Fact]
    public void Separate_conduit_runs_are_separate_networks()
    {
        var cat = Base().Build();
        var doc = Fixtures.Doc(cat,
            Fixtures.P("Batt", 0, 0), Fixtures.P("Heater", 1, 0),
            Fixtures.P("Batt", 0, 5), Fixtures.P("Pump", 1, 5));

        var r = Measure(cat, doc);

        Assert.Equal(2, r.Networks.Count);
        Assert.Contains(r.Networks, n => System.Math.Abs(n.LoadKw - HeaterRate * 3600) < 1e-9);
        Assert.Contains(r.Networks, n => System.Math.Abs(n.LoadKw - PumpRate * 3600) < 1e-9);
    }

    [Fact]
    public void A_device_plugged_into_two_networks_joins_them()
    {
        // Powered.UsePower gathers from every source at every input point, so the two batteries pool for it.
        var cat = Base().Build();
        var doc = Fixtures.Doc(cat,
            Fixtures.P("Batt", 0, 0), Fixtures.P("Cond", 1, 0),      // run A, ending under the bridge's left plug
            Fixtures.P("Bridge", 1, 0),
            Fixtures.P("Cond", 3, 0), Fixtures.P("Batt", 4, 0));     // run B, ending under its right plug

        // Without the bridge the two runs are two networks; the gap at (2,0) carries no path.
        var apart = Fixtures.Doc(cat, Fixtures.P("Batt", 0, 0), Fixtures.P("Cond", 1, 0),
            Fixtures.P("Cond", 3, 0), Fixtures.P("Batt", 4, 0));
        Assert.Equal(2, Measure(cat, apart).Networks.Count);

        var net = Assert.Single(Measure(cat, doc).Networks);
        Assert.Equal(2, net.Batteries.Count);
        Assert.Equal(2 * BatteryKwh, net.StoredKwh, 9);
    }

    [Fact]
    public void A_device_on_no_powered_network_is_listed_apart()
    {
        var cat = Base().Build();
        var doc = Fixtures.Doc(cat, Fixtures.P("Batt", 0, 0), Fixtures.P("Heater", 5, 0));

        var r = Measure(cat, doc);

        Assert.Empty(Assert.Single(r.Networks).Loads);
        Assert.Equal(HeaterRate * 3600, Assert.Single(r.Unconnected).Kw, 9);
    }

    // ---- the PROBLEMS warning for devices with no power

    /// <summary>The base catalog plus a docking port, since the scan stops at "No docking port" without one.</summary>
    private static Catalog ScanCatalog() => Base()
        .Part("Dock", w: 7, h: 2, startingConds: ["IsDockSys", "IsInstalled"], category: "HULL",
            mapPoints: new Dictionary<string, (double, double)> { ["DockA"] = (0, 8), ["DockB"] = (0, 24) })
        .Trig(ProblemScan.DocksysTrigger, reqs: ["IsDockSys", "IsInstalled"])
        .Build();

    private static Problem? Unpowered(Catalog cat, ShipDocument doc) =>
        ProblemScan.Scan(doc, cat).SingleOrDefault(p => p.DismissKey == ProblemScan.UnpoweredAlertKey);

    [Fact]
    public void A_device_no_battery_reaches_is_a_warning_that_shows_it()
    {
        var cat = ScanCatalog();
        var doc = Fixtures.Doc(cat, Fixtures.P("Dock", 0, 10),
            Fixtures.P("Batt", 0, 0), Fixtures.P("Heater", 5, 0), Fixtures.P("Heater", 6, 0));

        var problem = Unpowered(cat, doc);

        Assert.NotNull(problem);
        Assert.Equal(ProblemSeverity.Warning, problem.Severity);
        Assert.Equal("2 devices have no power", problem.Title);
        Assert.Contains("Heater ×2", problem.Detail);
        Assert.Equal([(5, 0), (6, 0)], problem.Cells!.Order());
    }

    [Fact]
    public void A_connected_device_raises_no_warning()
    {
        var cat = ScanCatalog();
        var doc = Fixtures.Doc(cat, Fixtures.P("Dock", 0, 10),
            Fixtures.P("Batt", 0, 0), Fixtures.P("Cond", 1, 0), Fixtures.P("Heater", 2, 0));

        Assert.Null(Unpowered(cat, doc));
    }

    [Fact]
    public void A_device_switched_off_in_the_plan_raises_no_warning()
    {
        // With no power it stays off, which is what the plan says. The Power Budget still lists it.
        var cat = ScanCatalog();
        var doc = Fixtures.Doc(cat, Fixtures.P("Dock", 0, 10),
            Fixtures.P("Batt", 0, 0), Fixtures.P("HeaterOff", 5, 0));

        Assert.Null(Unpowered(cat, doc));
        Assert.Single(Measure(cat, doc).Unconnected);
    }

    // ---- the fusion core

    private const double CoreRate = 0.14;   // FusionReactorCore01Batt: 504 kW

    private static Catalog ReactorCatalog() => ReactorFixtures().Build();

    /// <summary>A 1×1 core with six module points in a row to its right, wired at its own tile, plus the
    /// modules and the two reactant tanks matched by name.</summary>
    private static Fixtures ReactorFixtures()
    {
        var f = Base();
        var points = new Dictionary<string, (double, double)>
        {
            ["Module01"] = (16, 0), ["Module02"] = (32, 0), ["Module03"] = (48, 0),
            ["Module04"] = (64, 0), ["Module05"] = (80, 0), ["Module06"] = (96, 0),
            ["PowerSource"] = (0, 0), ["PowerOutput"] = (0, 0),
        };
        f.GpmTemplate("ReactorIC", "strGUIPrefab", "GUIReactor", "knobBus", "0");
        foreach (var form in new[] { "Off", "Batt" })
            f.Part("Core" + form, tileConds: ["IsPowerPath"], startingConds: ["IsInstalled", "IsReactorIC", .. form == "Off" ? new[] { "IsOff" } : []],
                mapPoints: points, powerInputs: [(0, 0)], tickers: Power, gpm: [("Panel A", "ReactorIC")],
                powerInfo: Draws("Core" + form, CoreRate));
        f.Part("CoreIgnition", tileConds: ["IsPowerPath"],
            startingConds: ["IsInstalled", "IsReactorIC", "IsPowerGen", "IsReadyFusion"],
            mapPoints: points, powerInputs: [(0, 0)], powerOutput: (0, 0), gpm: [("Panel A", "ReactorIC")],
            powerInfo: new PowerInfoDef("CoreIgnition", ["PowerSource"]) { Amount = 3.4e7, RechargeCT = "TIsReadyRecharge" });

        f.Trig(Propulsion.FusionModuleTrigger, reqs: ["IsInstalled", "IsFusionCoreModule"]);
        foreach (var kind in new[] { "LaserArray", "Capacitor", "PelletFeeder", "FuelRegulator", "MHDGenerator", "FieldCoils" })
            f.Part(kind, startingConds: ["IsInstalled", "IsFusionCoreModule", "IsFusion" + kind]);
        f.Part(Propulsion.D2OTankDef, startingConds: ["IsInstalled"],
            condValues: new Dictionary<string, double> { ["StatLiqD2O"] = 1000 });
        f.Part(Propulsion.He3TankDef, startingConds: ["IsInstalled"],
            condValues: new Dictionary<string, double> { ["StatSolidHe3"] = 1000 });
        return f;
    }

    private static ShipDocument Reactor(Catalog cat, string coreForm, bool mhd = true) => Fixtures.Doc(cat,
    [
        Fixtures.P("Core" + coreForm, 0, 0), Fixtures.P("Batt", 0, 1), Fixtures.P("Heater", 1, 1),
        Fixtures.P("LaserArray", 1, 0), Fixtures.P("Capacitor", 2, 0), Fixtures.P("PelletFeeder", 3, 0),
        Fixtures.P("FuelRegulator", 4, 0), Fixtures.P("FieldCoils", 5, 0),
        .. mhd ? new[] { Fixtures.P("MHDGenerator", 6, 0) } : [],
        Fixtures.P(Propulsion.D2OTankDef, 0, 4), Fixtures.P(Propulsion.He3TankDef, 1, 4),
    ]);

    private static readonly ReactorSettings Lit = new()
    {
        Bus = ReactorPowerBus.Chrg, PelletFeed = true, FuelRegulator = true, Mhd = true, Ignition = true,
        LaserAlign = true, CoilForward = true,
    };

    [Fact]
    public void A_cold_core_draws_nothing_and_says_what_starting_it_costs()
    {
        var cat = ReactorCatalog();
        var doc = Reactor(cat, "Off");

        var r = Measure(cat, doc);
        var core = Assert.Single(r.Reactors);
        var net = Assert.Single(r.Networks);

        Assert.Equal(ReactorState.Cold, core.State);
        Assert.Equal(HeaterRate * 3600, net.LoadKw, 9);   // the heater, and not the core
        Assert.Equal(1, core.InputNetwork);
        Assert.Equal(CoreRate * 3600, core.CoreKw, 9);
        // The core, the laser alignment, one feeder and one regulator.
        Assert.Equal(CoreRate * 3600 + 3 * PowerBudget.ModuleKw, core.StartingKw, 9);
        Assert.Equal(BatteryKwh / (net.LoadKw + core.StartingKw), core.StartingHours!.Value, 9);
        Assert.Equal(BatteryKwh / (net.LoadKw + core.StartingKw + PowerBudget.FieldCoilKw), core.StartingHoursWithCoils!.Value, 9);
        Assert.True(core.CanRecharge);
        Assert.Equal([1], core.RechargeNetworks);
        Assert.Equal(PowerBudget.CapacitorKwhPerSquare, core.CapacitorKwh, 9);
    }

    [Fact]
    public void A_core_with_its_bus_on_and_no_ignition_spawns_in_battery_mode()
    {
        var cat = ReactorCatalog();
        var doc = Reactor(cat, "Off");
        doc.Placements.Single(p => p.DefName == "CoreOff").Reactor = new ReactorSettings { Bus = ReactorPowerBus.Batt };

        var r = Measure(cat, doc);

        Assert.Equal(ReactorState.BatteryMode, Assert.Single(r.Reactors).State);
        var core = Assert.Single(Assert.Single(r.Networks).Loads, l => l.IsReactor);
        Assert.Equal(CoreRate * 3600, core.Kw, 9);
    }

    [Fact]
    public void The_ignition_switch_on_a_cold_core_resets_it_rather_than_starting_it()
    {
        var cat = ReactorCatalog();
        var doc = Reactor(cat, "Off");
        doc.Placements.Single(p => p.DefName == "CoreOff").Reactor = Lit;

        var core = Assert.Single(Measure(cat, doc).Reactors);
        Assert.Equal(ReactorState.Cold, core.State);
        Assert.NotNull(core.Reason);
    }

    [Fact]
    public void A_lit_core_feeds_its_network_and_recharges_it_with_the_mhd_on()
    {
        var cat = ReactorCatalog();
        var doc = Reactor(cat, "Ignition");
        doc.Placements.Single(p => p.DefName == "CoreIgnition").Reactor = Lit;

        var r = Measure(cat, doc);

        Assert.Equal(ReactorState.Running, Assert.Single(r.Reactors).State);
        var net = Assert.Single(r.Networks);
        Assert.True(net.Unlimited);
        Assert.True(net.Sustained);
        Assert.True(net.RechargedByReactor);
    }

    [Fact]
    public void Without_an_mhd_a_running_core_powers_the_ship_but_never_recharges()
    {
        var cat = ReactorCatalog();
        var doc = Reactor(cat, "Ignition", mhd: false);
        doc.Placements.Single(p => p.DefName == "CoreIgnition").Reactor = Lit;

        var r = Measure(cat, doc);

        Assert.False(Assert.Single(r.Reactors).CanRecharge);
        var net = Assert.Single(r.Networks);
        Assert.True(net.Unlimited);
        Assert.False(net.RechargedByReactor);
    }

    [Fact]
    public void A_lit_core_with_its_bus_off_shuts_down_and_feeds_nothing()
    {
        var cat = ReactorCatalog();
        var doc = Reactor(cat, "Ignition");
        doc.Placements.Single(p => p.DefName == "CoreIgnition").Reactor = Lit with { Bus = ReactorPowerBus.Off };

        var r = Measure(cat, doc);

        Assert.Equal(ReactorState.WillNotStayLit, Assert.Single(r.Reactors).State);
        var net = Assert.Single(r.Networks);
        Assert.False(net.Unlimited);
        Assert.Equal(HeaterRate * 3600, net.LoadKw, 9);
    }

    /// <summary>The live scan reads a snapshot, so the snapshot has to carry the reactor's panel. Without it a lit
    /// core reads as bus-off, stops being a source, and everything it feeds is reported as having no power.</summary>
    [Fact]
    public void Devices_a_running_core_feeds_have_power_in_the_live_scan()
    {
        var cat = ReactorFixtures()
            .Part("Dock", w: 7, h: 2, startingConds: ["IsDockSys", "IsInstalled"], category: "HULL",
                mapPoints: new Dictionary<string, (double, double)> { ["DockA"] = (0, 8), ["DockB"] = (0, 24) })
            .Trig(ProblemScan.DocksysTrigger, reqs: ["IsDockSys", "IsInstalled"])
            .Build();
        var doc = Fixtures.Doc(cat,
            Fixtures.P("Dock", 0, 10),
            Fixtures.P("CoreIgnition", 0, 0), Fixtures.P("Heater", 0, 1),   // no battery: the core is the only source
            Fixtures.P("LaserArray", 1, 0), Fixtures.P("Capacitor", 2, 0), Fixtures.P("PelletFeeder", 3, 0),
            Fixtures.P("FuelRegulator", 4, 0),
            Fixtures.P(Propulsion.D2OTankDef, 0, 4), Fixtures.P(Propulsion.He3TankDef, 1, 4));
        var core = doc.Placements.Single(p => p.DefName == "CoreIgnition");
        core.Reactor = Lit;

        Assert.Null(Unpowered(cat, doc.Snapshot()));

        // Turn the bus off and the core goes out on load, so the heater really has no power.
        core.Reactor = Lit with { Bus = ReactorPowerBus.Off };
        Assert.NotNull(Unpowered(cat, doc.Snapshot()));

        // And emptying the helium-3 tank does the same.
        core.Reactor = Lit;
        doc.Placements.Single(p => p.DefName == Propulsion.He3TankDef).Fill =
            new Dictionary<string, double> { ["StatSolidHe3"] = 0 };
        Assert.NotNull(Unpowered(cat, doc.Snapshot()));
    }

    [Fact]
    public void A_lit_core_with_no_fuel_aboard_will_not_stay_lit()
    {
        var cat = ReactorCatalog();
        var doc = Reactor(cat, "Ignition");
        doc.Placements.Single(p => p.DefName == "CoreIgnition").Reactor = Lit;
        new RemoveCommand(doc.Placements.Where(p => p.DefName == Propulsion.He3TankDef).ToList()).Do(doc);

        Assert.Equal(ReactorState.WillNotStayLit, Assert.Single(Measure(cat, doc).Reactors).State);
    }

    // ---- against the game

    [SkippableFact]
    public void The_stock_rates_come_through_the_catalog()
    {
        var g = TestData.RequireGame();
        var c = g.Catalog;

        Assert.Equal(80.96, c.Lookup("ItmBattery02")!.StartingCondValues["StatPowerMax"], 6);
        Assert.Equal(3.96, c.Lookup("ItmHeater01")!.PowerInfo!.Amount * 3600, 6);
        Assert.True(c.TicksReadyToUsePower(c.Lookup("ItmHeater01")!));
        Assert.True(c.Lookup("ItmHeater01")!.PowerInfo!.AllowExtPower);
        Assert.Equal(504, c.Lookup("ItmFusionReactorCore01Batt")!.PowerInfo!.Amount * 3600, 6);
        Assert.False(c.Lookup("ItmFusionFieldCoils01")!.PowerInfo!.AllowExtPower);
        // The station uplink is refilled 1 kWh per 0.000278 h ticker period: just under 3.6 MW.
        Assert.Equal(1 / 0.000278, c.TickerPowerSupplyKw(c.Lookup("ItmReactorIC02IgnitionMini")!), 3);
        // The fusion modules hide their nubs, and still draw from the points they hide.
        var coils = c.Lookup("ItmFusionFieldCoils01")!;
        Assert.Empty(coils.PowerInputPoints);
        Assert.NotEmpty(coils.PowerDrawPoints);
    }

    [SkippableTheory]
    [InlineData("Halberd", ReactorState.Running, true)]
    [InlineData("Halberd Off", ReactorState.Cold, false)]
    public void Stock_ships_read_with_their_known_reactor_state(string ship, ReactorState state, bool recharging)
    {
        var g = TestData.RequireGame();
        var doc = TestData.Template(g, ship);

        var r = PowerBudget.Measure(doc, ShipGrid.FromDocument(doc, g.Catalog), g.Catalog);

        Assert.Equal(state, Assert.Single(r.Reactors).State);
        Assert.Equal(recharging, r.Networks.Any(n => n.RechargedByReactor));
        Assert.Equal(state == ReactorState.Running, r.Networks[0].Unlimited);
        Assert.True(r.Reactors[0].CanStart);
    }

    [SkippableFact]
    public void A_station_generator_carries_its_network()
    {
        var g = TestData.RequireGame();
        var doc = TestData.Template(g, "ATC 01");

        var r = PowerBudget.Measure(doc, ShipGrid.FromDocument(doc, g.Catalog), g.Catalog);

        var fed = r.Networks.Single(n => n.Generators.Count > 0);
        Assert.True(fed.Sustained);
        Assert.False(fed.Unlimited);
        Assert.True(fed.LoadKw > 0);
    }

    /// <summary>
    /// The no-power warning, through the snapshot the editor's scan reads, across the stock fleet. The Edelweiss is
    /// the case a thin snapshot gets wrong: its running core is the only source on the network its devices use, so
    /// losing the panel on the way to the scan would report all of them unpowered. The fleet bound holds the noise
    /// down: leaving out devices switched off in the plan is what takes this from 71 stock ships to none.
    /// </summary>
    [SkippableFact]
    public void Stock_ships_raise_the_no_power_warning_only_where_a_device_is_cut_off()
    {
        var g = TestData.RequireGame();

        var edelweiss = TestData.Template(g, "Edelweiss");
        Assert.DoesNotContain(ProblemScan.Scan(edelweiss.Snapshot(), g.Catalog),
            p => p.DismissKey == ProblemScan.UnpoweredAlertKey);

        var flagged = new List<string>();
        foreach (var file in TemplateImport.ListShipFiles(g.Index))
        {
            var doc = TemplateImport.LoadFile(file.Path, g.Catalog).Doc;
            if (ProblemScan.Scan(doc.Snapshot(), g.Catalog).Any(p => p.DismissKey == ProblemScan.UnpoweredAlertKey))
                flagged.Add(file.Name);
        }
        _out.WriteLine($"{flagged.Count} stock ships flagged: {string.Join(", ", flagged)}");
        Assert.True(flagged.Count <= 3, $"{flagged.Count} stock ships raise the no-power warning");
    }

    /// <summary>Every core template budgets without throwing, and the lit cores the stock ships author nearly all
    /// read as running: a template whose core is lit and stays lit in game is the common case, so a collapse here
    /// means the stays-lit test has stopped reading the panel or the modules.</summary>
    [SkippableFact]
    public void Core_ships_budget_and_their_lit_cores_read_as_running()
    {
        var g = TestData.RequireGame();
        int ships = 0, lit = 0, running = 0;
        foreach (var file in TemplateImport.ListShipFiles(g.Index))
        {
            var doc = TemplateImport.LoadFile(file.Path, g.Catalog).Doc;
            var r = PowerBudget.Measure(doc, ShipGrid.FromDocument(doc, g.Catalog), g.Catalog);
            ships++;
            foreach (var core in r.Reactors)
            {
                if (doc.Part(core.Core)?.StartingConds.Contains("IsReadyFusion") is not true) continue;
                lit++;
                if (core.State == ReactorState.Running) running++;
                else _out.WriteLine($"{file.Name}: lit core will not stay lit ({core.Reason})");
            }
        }
        _out.WriteLine($"{ships} ships, {running} of {lit} lit cores running");
        Skip.If(lit == 0, "no template carries a lit core");
        Assert.True(running >= lit * 0.9, $"only {running} of {lit} lit cores read as running");
    }
}
