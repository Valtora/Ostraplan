namespace Ostraplan.Core;

/// <summary>One device drawing steadily from a network, at the rate the game charges it.</summary>
/// <param name="Kw">The draw, in kW: the power-info's <c>fAmount</c> (kWh per game second) times 3600.</param>
public sealed record PowerLoad(Placement Placement, string Name, double Kw)
{
    /// <summary>True for a device the plan has in its Off form that nothing holds off once the ship loads. An Off
    /// form still ticks <c>Power</c> and still draws, and the first tick the network covers in full queues its
    /// <c>strIntPowerOn</c>, which switches it to the on form. Only <c>IsOverrideOff</c> or <c>IsSignalOff</c>
    /// keeps it dark.</summary>
    public bool OffInPlan { get; init; }

    /// <summary>True when the device has a bus knob the designer can turn to Off (its sensor panel), which is the
    /// remedy for <see cref="OffInPlan"/>. Without one, only a breaker box holds it off.</summary>
    public bool HasKnob { get; init; }

    /// <summary>True for a fusion core sitting in Battery Mode: its own draw plus the modules
    /// <c>FusionIC</c> bills the network for.</summary>
    public bool IsReactor { get; init; }
}

/// <summary>A battery on a network.</summary>
/// <param name="StoredKwh">The charge it spawns with: its def's <c>StatPower</c>, clamped to
/// <paramref name="CapacityKwh"/> the way <c>Powered.ResetCurrentToMaxPower</c> clamps a worn one.</param>
/// <param name="CapacityKwh"><c>StatPowerMax × condition</c>, which is <c>Powered.PowerStoredMax</c>.</param>
public sealed record PowerStore(Placement Placement, string Name, double StoredKwh, double CapacityKwh);

/// <summary>A generator feeding a network.</summary>
/// <param name="Kw">The supply it sustains, or null for one with no practical limit (a running fusion core).</param>
public sealed record PowerSupply(Placement Placement, string Name, double? Kw);

/// <summary>
/// One conduit network: every battery and generator whose floods connect, and every device that draws from them.
/// </summary>
public sealed record PowerNetworkBudget(
    int Number, IReadOnlyList<PowerStore> Batteries, IReadOnlyList<PowerLoad> Loads,
    IReadOnlyList<PowerSupply> Generators)
{
    public double LoadKw => Loads.Sum(l => l.Kw);

    public double StoredKwh => Batteries.Sum(b => b.StoredKwh);

    public double CapacityKwh => Batteries.Sum(b => b.CapacityKwh);

    /// <summary>True when a running fusion core feeds this network, which is as good as no limit.</summary>
    public bool Unlimited => Generators.Any(g => g.Kw is null);

    /// <summary>The supply of the generators that have a rate.</summary>
    public double GeneratorKw => Generators.Sum(g => g.Kw ?? 0);

    /// <summary>True when the generators cover the steady load, so the batteries do not drain.</summary>
    public bool Sustained => Unlimited || (GeneratorKw > 0 && GeneratorKw >= LoadKw);

    /// <summary>
    /// How long the batteries alone carry the steady load, in game hours: stored charge over load. Null when
    /// nothing draws. Zero when something draws and nothing is stored.
    ///
    /// <para>A division, not a run. <c>GatherPower</c> drains the fullest battery first each tick, so on one network
    /// the batteries empty together and the order never changes the total.</para>
    /// </summary>
    public double? HoursOnBatteries => LoadKw <= 0 ? null : StoredKwh / LoadKw;

    /// <summary>True when a running reactor with its MHD on recharges the batteries here.</summary>
    public bool RechargedByReactor { get; init; }
}

/// <summary>What a fusion core does when the ship loads, read off its form and its panel.</summary>
public enum ReactorState
{
    /// <summary>Off, with nothing set to start it. It draws nothing.</summary>
    Cold,

    /// <summary>Its bus knob is at BATT or CHRG without the ignition switch, so it enters Battery Mode on load and
    /// draws its starter power until someone lights it or the batteries run out.</summary>
    BatteryMode,

    /// <summary>Lit, with everything it needs to stay lit.</summary>
    Running,

    /// <summary>Authored lit, but missing something <c>FusionIC</c> needs, so it shuts down on the first tick.</summary>
    WillNotStayLit,
}

/// <summary>One fusion core: how it spawns, what starting it costs, and what it feeds once it runs.</summary>
public sealed record ReactorBudget(Placement Core, string Name, ReactorState State)
{
    /// <summary>Why <see cref="State"/> is what it is, when that is not obvious from the state alone.</summary>
    public string? Reason { get; init; }

    /// <summary>The network at the core's power input, which Battery Mode draws from. Null when the input is on no
    /// network with a source.</summary>
    public int? InputNetwork { get; init; }

    /// <summary>The network at the core's power output, which it feeds once it runs.</summary>
    public int? OutputNetwork { get; init; }

    /// <summary>The core's own Battery Mode draw, in kW (its <c>…Batt</c> form's <c>fAmount</c>).</summary>
    public double CoreKw { get; init; }

    /// <summary>Battery Mode with the modules ignition needs switched on: the core, the laser alignment, and every
    /// core pump, pellet feeder and fuel regulator, each billed at <see cref="PowerBudget.ModuleKw"/>.</summary>
    public double StartingKw { get; init; }

    /// <summary>How long the input network's batteries hold <see cref="StartingKw"/> on top of everything else
    /// already drawing there. Null when there is nothing stored on that network, so the core cannot be started.</summary>
    public double? StartingHours { get; init; }

    /// <summary>The same, with every field coil switched on as well: what a player who turns the coils on before
    /// ignition gets. Null on the same terms as <see cref="StartingHours"/>, and when there are no coils.</summary>
    public double? StartingHoursWithCoils { get; init; }

    public int Capacitors { get; init; }

    public int FieldCoils { get; init; }

    public int Mhds { get; init; }

    /// <summary>Whether the panel's MHD switch is on.</summary>
    public bool MhdSwitchOn { get; init; }

    /// <summary>The networks whose batteries the core recharges once it runs with its MHD on: those at its power
    /// input and its power output, which is where <c>Powered.Recharge</c> looks.</summary>
    public IReadOnlyList<int> RechargeNetworks { get; init; } = [];

    /// <summary>The one-off charge the capacitors take before ignition, in kWh. <c>FusionIC</c> bills
    /// <c>0.339 × rate × capacitors²</c> while it charges, so the cost goes with the square of the count.</summary>
    public double CapacitorKwh => PowerBudget.CapacitorKwhPerSquare * Capacitors * Capacitors;

    /// <summary>The capacitors' draw at the start of the charge, the highest it gets, in kW.</summary>
    public double CapacitorPeakKw => PowerBudget.CapacitorPeakKwPerSquare * Capacitors * Capacitors;

    public bool CanStart => StartingHours is > 0;

    /// <summary>True when an MHD sits on one of the core's module points, which is what lets it recharge.</summary>
    public bool CanRecharge => Mhds > 0;
}

/// <summary>Everything <see cref="PowerBudget.Measure"/> found.</summary>
/// <param name="Unconnected">Devices that would draw but whose input is on no network with a source.</param>
/// <param name="ChargingContainers">Charging lockers on the plan. What they hold is not counted.</param>
public sealed record PowerBudgetReport(
    IReadOnlyList<PowerNetworkBudget> Networks, IReadOnlyList<PowerLoad> Unconnected,
    IReadOnlyList<ReactorBudget> Reactors, int ChargingContainers)
{
    public static readonly PowerBudgetReport Empty = new([], [], [], 0);

    public bool IsEmpty => Networks.Count == 0 && Unconnected.Count == 0 && Reactors.Count == 0;
}

/// <summary>
/// The design's power budget: what every switched-on device draws, what the batteries hold, how long they carry
/// the load, and what the fusion core costs to start and gives back once it runs.
///
/// <para><b>Not a simulation.</b> Every figure is the game's own authored rate summed at the state the ship loads in,
/// and endurance is one division. Nothing is stepped forward: no battery runs out partway and drops its devices, no
/// sensor cycles a pump, no fuel burns down. That is the line docs/SCOPE.md draws, and the window says so.</para>
///
/// <para><b>What draws.</b> <c>Powered.Run</c> charges a device <c>fAmount × elapsed</c> about once a second while
/// <c>TIsReadyUsePower</c> fires: the per-second <c>Power</c> ticker grants <c>IsReadyUsePower</c>, and the trigger
/// forbids <c>IsOverrideOff</c>. A device that also carries <c>IsSignalOff</c> is shut down instead. Nothing else
/// matters: an idle pump waiting on its sensor costs the same as a running one, because <c>GasPump</c> and
/// <c>Heater</c> read the sensor only to decide whether gas or heat moves.</para>
///
/// <para><b>What keeps a device off</b>, as this design reaches the game: a def that starts with
/// <c>IsOverrideOff</c>; a sensor panel's bus knob at Off, which <c>GasPump.UpdateRemote</c> turns into
/// <c>IsOverrideOff</c>; and an Off form wired to a breaker box, which the export writes with status false so it
/// loads with <c>IsSignalOff</c>. An Off form with none of these comes on by itself (<see cref="PowerLoad.OffInPlan"/>).</para>
///
/// <para><b>Not counted:</b> a device whose power-info forbids external power (the fusion core's modules, which
/// <c>FusionIC</c> bills for instead), per-use draws through <c>UserPowerExt</c> (a weapon's shot, a lift rotor at
/// five or ten times its rate while manoeuvring), and the batteries inside charging lockers.</para>
///
/// <para>See docs/GAME-INTERNALS.md §13 for the port and the in-game checks behind it.</para>
/// </summary>
public static class PowerBudget
{
    /// <summary>Seconds in an hour: the game keeps charge in kWh and rates in kWh per game second.</summary>
    public const double SecondsPerHour = 3600;

    /// <summary>What <c>FusionIC.Run</c> bills per module for the core pump, the cryo pumps, the pellet feeders,
    /// the fuel regulators and the laser alignment, in kW (its <c>7.6e-5</c> kWh per second).</summary>
    public const double ModuleKw = 7.599999662488699E-05 * SecondsPerHour;

    /// <summary>What <c>FusionIC.Run</c> bills per field coil while the coils are switched on and the core is not yet
    /// lit, in kW: <c>5700 × dt / 3600</c> kWh per tick, doubled with both coils on. Once the core runs it pays for
    /// them itself.</summary>
    public const double FieldCoilKw = 5700;

    /// <summary>The capacitor charge, in kWh per capacitor squared: <c>0.339 × rate × n²</c> billed while the shared
    /// <c>StatICCapA</c> climbs from empty to the 0.996 where charging stops.</summary>
    public const double CapacitorKwhPerSquare = 0.33899998664855957 * 0.996;

    /// <summary>The capacitor draw at the start of the charge, where the rate is at its 0.1 per second ceiling, in kW
    /// per capacitor squared.</summary>
    public const double CapacitorPeakKwPerSquare = 0.33899998664855957 * 0.1 * SecondsPerHour;

    /// <summary>Seconds a running reactor takes to bring a battery from empty to 90%. <c>Powered.Recharge</c> adds a
    /// thousandth of each battery's missing charge per run, about once a game second at normal speed, whatever its
    /// size: <c>1000 × ln 10</c>.</summary>
    public static readonly double RechargeSeconds90 = 1000 * Math.Log(10);

    private const string ReactorCond = "IsReactorIC";

    public static PowerBudgetReport Measure(ShipDocument doc, ShipGrid grid, Catalog catalog)
    {
        var byId = new Dictionary<string, Placement>(StringComparer.Ordinal);
        foreach (var p in doc.Placements) byId[p.Id.ToString()] = p;
        Placement? PlacementOf(PlacedPart part) => part.StrID is { } id ? byId.GetValueOrDefault(id) : null;

        // Off forms wired on the breaker channel: the export writes their Electrical status false, which raises
        // IsSignalOff at load (ShipExport.ElectricalGpm). Wired on forms load live.
        var wired = new HashSet<Guid>();
        foreach (var (_, source, target) in DeviceLinks.Resolved(doc)) { wired.Add(source.Id); wired.Add(target.Id); }

        // The cores first: whether one is lit decides whether it is a source at all.
        var cores = new List<(PlacedPart Part, Placement Placement, PartDef Def, CoreScan Scan, ReactorState State, string? Reason)>();
        foreach (var part in grid.Parts)
        {
            if (!part.Part.Has(ReactorCond) || PlacementOf(part) is not { } placement || doc.Part(placement) is not { } def)
                continue;
            var scan = ScanCore(grid, part, catalog);
            var (state, reason) = CoreState(def, placement.Reactor ?? ReactorSettings.Default, scan);
            cores.Add((part, placement, def, scan, state, reason));
        }
        var coreState = cores.ToDictionary(c => c.Part, c => c.State);

        // Live sources. A lit core that will not stay lit is gone by the first tick, and its Off form is no source.
        var sources = PowerNetwork.SourceFloods(grid, catalog)
            .Where(s => !s.Def.StartingConds.Contains(ReactorCond)
                        || coreState.GetValueOrDefault(s.Part) == ReactorState.Running)
            .ToList();

        var roots = Enumerable.Range(0, sources.Count).ToArray();
        int Find(int i) { while (roots[i] != i) i = roots[i] = roots[roots[i]]; return i; }
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) roots[b] = a; }

        var sourcesOnTile = new Dictionary<int, List<int>>();
        for (var i = 0; i < sources.Count; i++)
            foreach (var t in sources[i].Tiles)
            {
                if (!sourcesOnTile.TryGetValue(t, out var list)) sourcesOnTile[t] = list = [];
                if (list.Count > 0) Union(list[0], i);
                list.Add(i);
            }

        // A source an input point lands on, or -1.
        int SourceAt(int tile) => tile >= 0 && sourcesOnTile.TryGetValue(tile, out var l) ? l[0] : -1;

        // Devices. One whose input points reach two networks draws from both, so it joins them.
        var loads = new List<(int Source, PowerLoad Load)>();
        var unconnected = new List<PowerLoad>();
        foreach (var part in grid.Parts)
        {
            if (part.Part.Has(ReactorCond) || PlacementOf(part) is not { } placement || doc.Part(placement) is not { } def)
                continue;
            if (Draw(doc, catalog, placement, def, wired) is not { } load) continue;
            var reached = def.PowerDrawPoints.Select(pt => SourceAt(grid.MapPointTile(part, pt)))
                .Where(s => s >= 0).Distinct().ToList();
            if (reached.Count == 0) { unconnected.Add(load); continue; }
            for (var i = 1; i < reached.Count; i++) Union(reached[0], reached[i]);
            loads.Add((reached[0], load));
        }

        // A core in Battery Mode is a load like any other, on the network at its power input.
        foreach (var core in cores.Where(c => c.State == ReactorState.BatteryMode))
        {
            var settings = (core.Placement.Reactor ?? ReactorSettings.Default).Clamped();
            var load = new PowerLoad(core.Placement, NameOf(core.Placement, core.Def),
                BatteryModeCoreKw(core.Def, catalog) + PanelModuleKw(settings, core.Scan)) { IsReactor = true };
            var reached = core.Def.PowerDrawPoints.Select(pt => SourceAt(grid.MapPointTile(core.Part, pt)))
                .FirstOrDefault(s => s >= 0, -1);
            if (reached < 0) unconnected.Add(load);
            else loads.Add((reached, load));
        }

        // Number the networks: most stored first, then busiest.
        var groups = Enumerable.Range(0, sources.Count).GroupBy(Find).ToList();
        var built = new List<(int Root, List<PowerStore> Batteries, List<PowerSupply> Generators, List<PowerLoad> Loads)>();
        foreach (var g in groups)
        {
            var batteries = new List<PowerStore>();
            var generators = new List<PowerSupply>();
            foreach (var i in g)
            {
                var (sp, sdef, _, _) = sources[i];
                if (PlacementOf(sp) is not { } placement) continue;
                if (sdef.StartingConds.Contains("IsPowerGen"))
                {
                    var supply = sdef.StartingConds.Contains(ReactorCond) ? (double?)null : catalog.TickerPowerSupplyKw(sdef);
                    if (supply is null or > 0) { generators.Add(new PowerSupply(placement, NameOf(placement, sdef), supply)); continue; }
                }
                // A battery, a charging locker, or a generator nothing refills: what it holds is all it gives.
                var condition = placement.Condition ?? 1.0;
                var max = sdef.StartingCondValues.GetValueOrDefault("StatPowerMax",
                    sdef.StartingCondValues.GetValueOrDefault("StatPower")) * condition;
                var stored = Math.Min(sdef.StartingCondValues.GetValueOrDefault("StatPower"), max);
                if (max > 0 || stored > 0)
                    batteries.Add(new PowerStore(placement, NameOf(placement, sdef), Math.Max(0, stored), max));
            }
            var groupLoads = loads.Where(l => Find(l.Source) == g.Key).Select(l => l.Load)
                .OrderByDescending(l => l.Kw).ToList();
            built.Add((g.Key, batteries, generators, groupLoads));
        }
        built = built
            .Where(b => b.Batteries.Count > 0 || b.Generators.Count > 0 || b.Loads.Count > 0)
            .OrderByDescending(b => b.Generators.Any(x => x.Kw is null))
            .ThenByDescending(b => b.Batteries.Sum(x => x.StoredKwh))
            .ThenByDescending(b => b.Loads.Sum(x => x.Kw))
            .ToList();
        var numberOf = new Dictionary<int, int>();
        for (var i = 0; i < built.Count; i++) numberOf[built[i].Root] = i + 1;
        int? NetworkAt(int tile) =>
            SourceAt(tile) is var s and >= 0 && numberOf.TryGetValue(Find(s), out var n) ? n : null;

        // The cores, against the numbered networks.
        var reactors = new List<ReactorBudget>();
        var recharged = new HashSet<int>();
        foreach (var core in cores)
        {
            var settings = (core.Placement.Reactor ?? ReactorSettings.Default).Clamped();
            var inputTiles = core.Def.PowerDrawPoints.Select(pt => grid.MapPointTile(core.Part, pt)).ToList();
            var outputTile = core.Def.PowerOutputPoint is { } o ? grid.MapPointTile(core.Part, o) : -1;
            var input = inputTiles.Select(NetworkAt).FirstOrDefault(n => n is not null);
            var output = NetworkAt(outputTile);
            var rechargeNets = inputTiles.Append(outputTile).Select(NetworkAt).OfType<int>().Distinct().Order().ToList();

            var coreKw = BatteryModeCoreKw(core.Def, catalog);
            var startingKw = coreKw + ModuleKw * (1 + core.Scan.CorePumps + core.Scan.Feeders + core.Scan.Regulators);
            double? startingHours = null, withCoils = null;
            if (input is { } n && built[n - 1] is var net && net.Batteries.Sum(b => b.StoredKwh) is var stored and > 0)
            {
                // Everything already drawing there, less this core's own Battery Mode line if it spawns in it.
                var others = net.Loads.Where(l => !ReferenceEquals(l.Placement, core.Placement)).Sum(l => l.Kw);
                startingHours = stored / (others + startingKw);
                if (core.Scan.Coils > 0)
                    withCoils = stored / (others + startingKw + FieldCoilKw * core.Scan.Coils);
            }

            if (core.State == ReactorState.Running && settings.Bus != ReactorPowerBus.Off && settings.Mhd
                && core.Scan.Mhds > 0)
                recharged.UnionWith(rechargeNets);

            reactors.Add(new ReactorBudget(core.Placement, NameOf(core.Placement, core.Def), core.State)
            {
                Reason = core.Reason,
                InputNetwork = input,
                OutputNetwork = output,
                CoreKw = coreKw,
                StartingKw = startingKw,
                StartingHours = startingHours,
                StartingHoursWithCoils = withCoils,
                Capacitors = core.Scan.Capacitors,
                FieldCoils = core.Scan.Coils,
                Mhds = core.Scan.Mhds,
                MhdSwitchOn = settings.Mhd,
                RechargeNetworks = rechargeNets,
            });
        }

        var networks = built.Select((b, i) => new PowerNetworkBudget(i + 1, b.Batteries, b.Loads, b.Generators)
        {
            RechargedByReactor = recharged.Contains(i + 1),
        }).ToList();

        var chargers = grid.Parts.Count(p => p.Part.Has("IsRechargingContainer"));
        return new PowerBudgetReport(networks, unconnected.OrderByDescending(l => l.Kw).ToList(), reactors, chargers);
    }

    /// <summary>
    /// What one device draws once the ship loads, or null when it draws nothing from the network: no armed draw,
    /// no ticker granting <c>IsReadyUsePower</c>, a power-info that forbids external power, or something holding
    /// it off.
    /// </summary>
    internal static PowerLoad? Draw(ShipDocument doc, Catalog catalog, Placement placement, PartDef def,
        IReadOnlySet<Guid> wired)
    {
        if (def.PowerInfo is not { Draws: true, AllowExtPower: true } info) return null;
        if (!catalog.TicksReadyToUsePower(def)) return null;
        if (def.StartingConds.Contains("IsOverrideOff")) return null;

        var knob = DevicePanels.SensorPanel(catalog, def) is not null;
        var settings = (placement.Device ?? DeviceSettings.Default).ClampTo(def);
        if (knob && settings.Bus == DeviceBusMode.Off) return null;

        var off = def.StartingConds.Contains("IsOff");
        if (off && wired.Contains(placement.Id)) return null;

        // Powered.Run swaps in fOverrideAmount while the override cond is set. The only one a design can set is
        // turbo, which GasPump.UpdateRemote grants from the panel's bTurbo.
        var overridden = info is { OverrideCond: { Length: > 0 } cond, OverrideAmount: > 0 }
            && (def.StartingConds.Contains(cond) || (cond == "IsTurboOn" && knob && settings.Turbo));
        var rate = overridden ? info.OverrideAmount : info.Amount;

        return new PowerLoad(placement, NameOf(placement, def), rate * SecondsPerHour)
        {
            OffInPlan = off,
            HasKnob = knob,
        };
    }

    private static string NameOf(Placement placement, PartDef def) =>
        placement.CustomName is { Length: > 0 } custom ? custom : def.Friendly;

    // ---- the fusion core

    /// <summary>The modules on a core's points, sorted the way <c>FusionIC.Run</c> sorts them, and the reactants
    /// aboard. Every module of a kind is counted whatever its form: <c>FusionIC</c> writes <c>StatPower</c> into
    /// each one it drives and the module switches itself to its on form on its next power tick.</summary>
    internal readonly record struct CoreScan(
        int Lasers, int Feeders, int Cryos, int Capacitors, int Regulators, int CorePumps, int Coils, int Mhds,
        double D2O, double He3);

    private static CoreScan ScanCore(ShipGrid grid, PlacedPart core, Catalog catalog)
    {
        int lasers = 0, feeders = 0, cryos = 0, caps = 0, regs = 0, pumps = 0, coils = 0, mhds = 0;
        foreach (var m in Propulsion.CoreModules(grid, core, catalog).Modules)
        {
            // FusionIC.AddModule tries the lists in this order and files a module in the first that matches.
            if (m.Part.Has("IsFusionLaserArray")) lasers++;
            else if (m.Part.Has("IsFusionPelletFeeder")) feeders++;
            else if (m.Part.Has("IsFusionCryoPump")) cryos++;
            else if (m.Part.Has("IsFusionCapacitor")) caps++;
            else if (m.Part.Has("IsFusionFuelRegulator")) regs++;
            else if (m.Part.Has("IsFusionCorePump")) pumps++;
            else if (m.Part.Has("IsFusionFieldCoils")) coils++;
            else if (m.Part.Has("IsFusionMHDGenerator")) mhds++;
        }

        double d2o = 0, he3 = 0;
        foreach (var p in grid.Parts)
        {
            if (p.Part.DefName == Propulsion.D2OTankDef) d2o += p.Part.StartingCondValues.GetValueOrDefault("StatLiqD2O");
            else if (p.Part.DefName == Propulsion.He3TankDef) he3 += p.Part.StartingCondValues.GetValueOrDefault("StatSolidHe3");
        }
        return new CoreScan(lasers, feeders, cryos, caps, regs, pumps, coils, mhds, d2o, he3);
    }

    /// <summary>
    /// How a core spawns. A lit form (<c>IsReadyFusion</c>, the <c>…Ignition</c> condowner) stays lit only while
    /// <c>FusionIC.Fusion</c> has a pellet rate, which needs the bus on, the pellet feed and fuel regulator switches
    /// on, a laser with a capacitor and a feeder with a regulator, and both reactants aboard. Short of any of that
    /// it shuts down on the first tick and drops to its Off form. An unlit form enters Battery Mode when its bus is
    /// on, unless the ignition switch is also set: with the capacitors empty, <c>FusionIC</c> reads that as a
    /// failed ignition and resets the whole panel.
    /// </summary>
    private static (ReactorState, string?) CoreState(PartDef def, ReactorSettings settings, CoreScan scan)
    {
        settings = settings.Clamped();
        var bus = settings.Bus != ReactorPowerBus.Off;

        if (def.StartingConds.Contains("IsReadyFusion"))
        {
            string? missing =
                !bus ? "its bus knob is at OFF"
                : !settings.PelletFeed ? "the pellet feed switch is off"
                : !settings.FuelRegulator ? "the fuel regulator switch is off"
                : scan.Lasers == 0 ? "it has no laser array"
                : scan.Capacitors == 0 ? "it has no capacitor"
                : scan.Feeders == 0 ? "it has no pellet feeder"
                : scan.Regulators == 0 ? "it has no fuel regulator"
                : scan.D2O <= 0 ? "there is no deuterium aboard"
                : scan.He3 <= 0 ? "there is no helium-3 aboard"
                : null;
            return missing is null ? (ReactorState.Running, null) : (ReactorState.WillNotStayLit, missing);
        }

        if (!bus) return (ReactorState.Cold, null);
        return settings.Ignition
            ? (ReactorState.Cold, "the ignition switch is set before the core is ready, so it resets on load")
            : (ReactorState.BatteryMode, null);
    }

    /// <summary>The core's own draw in Battery Mode, in kW. Read off its <c>…Batt</c> form, which is the one that
    /// sits drawing; an Off form with its bus on draws the same while it waits to switch.</summary>
    private static double BatteryModeCoreKw(PartDef def, Catalog catalog)
    {
        var stem = def.DefName;
        foreach (var suffix in new[] { "Ignition", "Batt", "Off" })
            if (stem.EndsWith(suffix, StringComparison.Ordinal)) { stem = stem[..^suffix.Length]; break; }
        var batt = catalog.Lookup(stem + "Batt")?.PowerInfo ?? def.PowerInfo;
        return (batt?.Amount ?? 0) * SecondsPerHour;
    }

    /// <summary>What <c>FusionIC.Run</c> bills the network for the switches the panel has on, before ignition.
    /// The capacitors' charge is left out: it is over in about twenty seconds.</summary>
    private static double PanelModuleKw(ReactorSettings s, CoreScan scan)
    {
        double kw = 0;
        if (s.Purge != ReactorCorePurge.Off) kw += ModuleKw * scan.CorePumps;
        if (s.Cryo) kw += ModuleKw * scan.Cryos;
        if (s.PelletFeed) kw += ModuleKw * scan.Feeders;
        if (s.FuelRegulator) kw += ModuleKw * scan.Regulators;
        if (s.LaserAlign) kw += ModuleKw;
        if (s.CoilForward || s.CoilRear) kw += FieldCoilKw * scan.Coils * (s.CoilForward && s.CoilRear ? 2 : 1);
        return kw;
    }
}
