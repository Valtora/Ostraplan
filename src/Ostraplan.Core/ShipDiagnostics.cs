namespace Ostraplan.Core;

/// <summary>How a diagnostic row reads. <see cref="Neutral"/> is an informational row the game prints with no
/// colour tag at all (the rating code and the mass); <see cref="Good"/> and <see cref="Bad"/> are its own green
/// and red.</summary>
public enum DiagState { Neutral, Good, Bad }

/// <summary>
/// One row of the ship diagnostic. <see cref="Name"/> is verbatim from the game's <c>ShipStatus.aNames</c> so the
/// readout can be matched against the console line for line. <see cref="Note"/> is Ostraplan's addition: what is
/// missing and what to add, shown only where the row needs explaining.
/// </summary>
public sealed record DiagnosticRow(string Name, string Value, DiagState State, string? Note = null);

/// <summary>The full 16-row readout, in the game's order.</summary>
public sealed record ShipDiagnosticReport(IReadOnlyList<DiagnosticRow> Rows)
{
    /// <summary>Rows reading red — the checklist items a design still owes.</summary>
    public IReadOnlyList<DiagnosticRow> Faults => [.. Rows.Where(r => r.State == DiagState.Bad)];

    public int FaultCount => Rows.Count(r => r.State == DiagState.Bad);
}

/// <summary>
/// Port of the game's own ship diagnostic (verified 1.0.0.7): the <c>NavModDiagnostics</c> module's status page,
/// which is <c>ShipStatus.PrintStatus</c> filling sixteen fixed rows from <c>ShipStatus.aNames</c>. In game it is
/// reachable only by sitting at a nav console on a ship that already exists, so a planner has to recompute it —
/// and it is the one place the game itself enumerates the systems a working ship is expected to carry
/// (transponder, antenna, reactor and its two reactants, thrusters, distributor, reaction mass, backup power,
/// life support). That makes it the right basic ship checklist to answer from a design.
///
/// <para><b>Rows and thresholds are ported exactly</b>, including the ones that look arbitrary: He3 &gt; 100 kg,
/// D2O &gt; 1000 kg, RCS remass ≥ 200 kg, backup power ≥ 20 kWh, O2 stores &gt; 35 kg, and — the one people
/// misread — <b>more than one</b> switched-on RCS cluster, so a single thruster still reads red. Each is a
/// literal in the DLL and invisible to data diffing, so they can drift silently between patches; that is what
/// <see cref="ShipDiagnosticsThresholds"/> pins.</para>
///
/// <para><b>Four deliberate divergences</b>, all forced by the difference between a plan and a running ship, and
/// all surfaced in the report's own text rather than hidden:</para>
/// <list type="number">
///   <item><b>NAV STATION</b> is hardcoded <c>ONLINE</c> in game — you are reading the page off that very
///   console, so it cannot report its own absence. A design can very easily have no console at all, so the row
///   is answered as a real presence test (<c>TIsNavStationInstalled</c>). Without it, none of this page would be
///   readable in game.</item>
///   <item><b>TRANSPONDER</b> prints <c>Ship.strXPDR</c>, the registration ID the game assigns at spawn. A plan
///   has no registration, so an installed and switched-on transponder reads <c>INSTALLED</c> rather than a name
///   the planner would have to invent.</item>
///   <item><b>REACTOR</b> reads <c>ONLINE</c> only when the core's <c>StatPower</c> is non-zero, which the
///   fusion sim sets once the reactor is lit; no reactor def carries it. A planned (or freshly bought) reactor
///   is always installed unlit, so a literal port would report <c>OFFLINE</c> on every design ever made. The row
///   reports installation and says the console will read OFFLINE until it is lit — the same divergence, for the
///   same reason, that <see cref="Propulsion"/> makes reading <c>StatICVe</c> off the ignited core.</item>
///   <item><b>Quantities are as-spawned.</b> He3, D2O, reaction mass, backup power and O2 stores are summed from
///   what the placed parts spawn holding, which is exactly what a newly built or newly bought ship reads. They
///   are not a claim about a save in progress.</item>
/// </list>
///
/// <para>Everything else is answered from the same engines the rest of the planner uses:
/// <see cref="Rating"/> for the rating code, <see cref="Propulsion"/> for mass and reaction mass,
/// <see cref="PowerNetwork"/> for what the console's power inputs can actually see, and
/// <see cref="ShipValue.ScanO2Supply"/> for the life-support pumps.</para>
/// </summary>
public static class ShipDiagnostics
{
    // --- the game's own trigger names, so a modded part that satisfies them is counted like any other

    /// <summary>Installed, undamaged transponder (<c>Ship.ctXPDR</c>).</summary>
    public const string XpdrTrigger = "TIsXPDRInstalled";

    /// <summary>Installed transponder antenna, whatever its power state.</summary>
    public const string XpdrAntTrigger = "TIsXPDRAnt";

    /// <summary>Installed nav console — the station the diagnostic is read from.</summary>
    public const string NavStationTrigger = "TIsNavStationInstalled";

    /// <summary>A fusion reactor core, installed or not (the game re-tests <c>IsInstalled</c> itself).</summary>
    public const string ReactorTrigger = "TIsReactorIC";

    /// <summary>Installed helium-3 tank (<c>IsVesselHe3</c>) — NOT the same test <see cref="Propulsion"/> makes,
    /// which matches the tank by condowner name because the reactor's own fuel lookup does.</summary>
    public const string He3TankTrigger = "TIsCanisterLHe02Installed";

    /// <summary>Installed deuterium tank (<c>IsVesselH2</c>).</summary>
    public const string D2OTankTrigger = "TIsCanisterLH02Installed";

    /// <summary>Installed RCS cluster, whatever its power state.</summary>
    public const string RcsClusterTrigger = "TIsRCSClusterInstalled";

    /// <summary>Installed RCS distributor, whatever its power state.</summary>
    public const string RcsDistroTrigger = "TIsRCSDistroInstalled";

    /// <summary>Installed heater.</summary>
    public const string HeaterTrigger = "TIsHeater01Installed";

    /// <summary>Installed cooler.</summary>
    public const string CoolerTrigger = "TIsCooler01Installed";

    /// <summary>The switched-off marker every "…Off"/"…Dmg" form carries — what the game tests part by part
    /// rather than folding into the trigger.</summary>
    private const string OffCond = "IsOff";

    /// <summary>The sixteen row captions, verbatim from <c>ShipStatus.aNames</c>.</summary>
    public static readonly string[] Names =
    [
        "VESSEL RATING CODE:", "VESSEL MASS:", "TRANSPONDER:", "TRANSPONDER ANTENNA:", "NAV STATION:", "REACTOR:",
        "REACTOR HE3:", "REACTOR D2O:", "RCS THRUSTERS:", "RCS DISTRIBUTOR:", "RCS REMASS:", "BACKUP POWER:",
        "LIFE SUPPORT WORKING O2 PUMPS:", "LIFE SUPPORT O2 STORES:", "LIFE SUPPORT HEAT:", "LIFE SUPPORT COOL:",
    ];

    /// <summary>Run the whole diagnostic for a document. Room certification is needed only because the rating
    /// code counts certified compartments; everything else reads the grid directly.</summary>
    public static ShipDiagnosticReport Analyze(ShipDocument doc, Catalog catalog,
        IReadOnlyList<RoomSpecDef> specs, IProgress<(string Stage, double Frac)>? progress = null)
    {
        progress?.Report(("Building tile grid…", 0.15));
        var grid = ShipGrid.FromDocument(doc, catalog);

        progress?.Report(("Detecting rooms…", 0.35));
        var partition = RoomBuilder.Build(grid);

        progress?.Report(("Certifying rooms…", 0.60));
        RoomCertifier.CertifyAll(partition, specs, catalog);
        var rating = Rating.Calculate(grid, partition, catalog);

        progress?.Report(("Reading ship systems…", 0.85));
        var report = Build(doc, grid, catalog, rating, Propulsion.Estimate(doc, grid, catalog));

        progress?.Report(("Done", 1.0));
        return report;
    }

    /// <summary>
    /// The readout over an already-analysed ship, so a caller that has just run the rating and the propulsion
    /// scan does not pay for either twice.
    /// </summary>
    public static ShipDiagnosticReport Build(ShipDocument doc, ShipGrid grid, Catalog catalog,
        ShipRating rating, PropulsionEstimate propulsion)
    {
        var rows = new List<DiagnosticRow>(Names.Length);
        void Add(int i, string value, DiagState state, string? note = null) =>
            rows.Add(new DiagnosticRow(Names[i], value, state, note));

        // 0. VESSEL RATING CODE — Ship.GetRatingString(); "None" until the ship certifies anything.
        var hasRating = !string.IsNullOrEmpty(rating.Display);
        Add(0, hasRating ? rating.Display : "None", DiagState.Neutral,
            hasRating ? null : "No rating yet: the game rates a ship once it has a certified compartment.");

        // 1. VESSEL MASS — Ship.Mass, which walks TOP-LEVEL condowners with no IsInstalled filter, so loose deck
        // items weigh too. Deliberately not ShipRating.Mass, which counts installed parts only.
        Add(1, Kg0(propulsion.PartsMass + propulsion.LooseMass), DiagState.Neutral);

        // 2. TRANSPONDER — see the divergence note on the class: the game prints the registration ID.
        var xpdrs = Matching(grid, catalog, XpdrTrigger);
        var xpdrOn = xpdrs.Count(p => !p.Part.Has(OffCond));
        if (xpdrs.Count == 0)
            Add(2, "NOT FOUND", DiagState.Bad,
                "No transponder installed. Without one the ship broadcasts no identity, so ATC and other ships " +
                "cannot hail it and it reads as a derelict. Add one from the SENS tab.");
        else if (xpdrOn == 0)
            Add(2, "OFFLINE", DiagState.Bad,
                $"{Count(xpdrs.Count, "transponder")} installed but switched off, so no registration is broadcast.");
        else
            Add(2, "INSTALLED", DiagState.Good, "The console shows the ship's registration ID here; a design has " +
                                                "none until the game assigns one at spawn.");

        // 3. TRANSPONDER ANTENNA — on/total. Good on ANY antenna switched on.
        var ants = Matching(grid, catalog, XpdrAntTrigger);
        var antsOn = ants.Count(p => !p.Part.Has(OffCond));
        Add(3, $"{antsOn}/{ants.Count}", antsOn > 0 ? DiagState.Good : DiagState.Bad,
            antsOn > 0 ? null
            : ants.Count == 0
                ? "No transponder antenna. The transponder needs one to radiate: without it the ship is silent " +
                  "however many transponders it carries. Add one from the SENS tab."
                : $"{Count(ants.Count, "antenna", "antennae")} installed but switched off.");

        // 4. NAV STATION — a real presence test, not the game's hardcoded ONLINE (see the class note).
        var navs = Matching(grid, catalog, NavStationTrigger);
        Add(4, navs.Count > 0 ? "ONLINE" : "NOT FOUND", navs.Count > 0 ? DiagState.Good : DiagState.Bad,
            navs.Count > 0
                ? "The console always reports itself online, because this page is read at it. Ostraplan tests for " +
                  "one instead, since a design can have none."
                : "No nav console. The ship cannot be flown, and this whole diagnostic page is unreachable in " +
                  "game. Add one from the CTRL tab.");

        // 5. REACTOR — presence, not the game's lit/unlit test (see the class note).
        var cores = Matching(grid, catalog, ReactorTrigger).Where(p => p.Part.Has("IsInstalled")).ToList();
        Add(5, cores.Count > 0 ? "INSTALLED" : "NOT FOUND", cores.Count > 0 ? DiagState.Good : DiagState.Bad,
            cores.Count > 0
                ? "The console reads OFFLINE until the reactor is lit, and a planned reactor is always installed " +
                  "unlit. Run Ship Rating for whether its laser/feeder chain can actually fire."
                : "No fusion reactor core. The ship has no torch drive and no generated power, so it runs on " +
                  "battery alone. Deliberate on a small RCS-only hull; otherwise build one from the POWR tab.");

        // 6/7. REACTOR HE3 / D2O — the reactants, summed over the installed tanks the console counts.
        var he3 = SumCond(grid, catalog, He3TankTrigger, "StatSolidHe3");
        Add(6, Kg2(he3.Total), he3.Total > ShipDiagnosticsThresholds.He3Kg ? DiagState.Good : DiagState.Bad,
            he3.Total > ShipDiagnosticsThresholds.He3Kg ? null
            : he3.Count == 0
                ? $"No helium-3 tank aboard; the console wants more than {ShipDiagnosticsThresholds.He3Kg:0} kg. " +
                  "The torch burns He3 and deuterium together, so a tank of one without the other buys nothing."
                : $"{Count(he3.Count, "helium-3 tank")} aboard holding {Kg2(he3.Total)}, under the " +
                  $"{ShipDiagnosticsThresholds.He3Kg:0} kg the console wants.");

        var d2o = SumCond(grid, catalog, D2OTankTrigger, "StatLiqD2O");
        Add(7, Kg2(d2o.Total), d2o.Total > ShipDiagnosticsThresholds.D2OKg ? DiagState.Good : DiagState.Bad,
            d2o.Total > ShipDiagnosticsThresholds.D2OKg ? null
            : d2o.Count == 0
                ? $"No deuterium tank aboard; the console wants more than {ShipDiagnosticsThresholds.D2OKg:0} kg."
                : $"{Count(d2o.Count, "deuterium tank")} aboard holding {Kg2(d2o.Total)}, under the " +
                  $"{ShipDiagnosticsThresholds.D2OKg:0} kg the console wants.");

        // 8. RCS THRUSTERS — on/total, and the game wants MORE THAN ONE switched on: a single thruster can only
        // push, never turn, so one cluster reads red however healthy it is.
        var clusters = Matching(grid, catalog, RcsClusterTrigger);
        var clustersOn = clusters.Count(p => !p.Part.Has(OffCond));
        Add(8, $"{clustersOn}/{clusters.Count}",
            clustersOn > ShipDiagnosticsThresholds.MinRcsClustersOn ? DiagState.Good : DiagState.Bad,
            clustersOn > ShipDiagnosticsThresholds.MinRcsClustersOn ? null
            : clusters.Count == 0
                ? "No RCS thrusters. The ship cannot manoeuvre at all, and the Ship Rating's Maneuver slot reads O."
                : clustersOn == 0
                    ? $"{Count(clusters.Count, "RCS cluster")} installed but switched off."
                    : "Only one RCS cluster is on. The console wants more than one, because a single thruster can " +
                      "push but not turn the ship.");

        // 9. RCS DISTRIBUTOR — the game takes the first switched-on one it finds and stops.
        var distros = Matching(grid, catalog, RcsDistroTrigger);
        var distrosOn = distros.Count(p => !p.Part.Has(OffCond));
        Add(9, distros.Count == 0 ? "NOT FOUND" : distrosOn > 0 ? "ONLINE" : "OFFLINE",
            distrosOn > 0 ? DiagState.Good : DiagState.Bad,
            distrosOn > 0 ? null
            : distros.Count == 0
                ? "No RCS distributor. Nothing plumbs the tanks to the thrusters, so the ship has no reaction " +
                  "mass however many tanks it carries."
                : $"{Count(distros.Count, "RCS distributor")} installed but switched off.");

        // 10. RCS REMASS — Ship.GetRCSRemain: gas in containers sitting ON a switched-on distributor's GasInput
        // points. A canister in a rack feeds nothing, which is exactly what this row exists to catch.
        var remass = propulsion.RcsReactionMass;
        Add(10, Kg2(remass), remass >= ShipDiagnosticsThresholds.RcsRemassKg ? DiagState.Good : DiagState.Bad,
            remass >= ShipDiagnosticsThresholds.RcsRemassKg ? null
            : propulsion.RcsTankCount == 0
                ? "No tank sits on a distributor's gas input, so there is no reaction mass. A canister in a rack " +
                  "feeds nothing; it has to be on the input point itself."
                : $"{Kg2(remass)} plumbed in across {Count(propulsion.RcsTankCount, "feed position")}, under the " +
                  $"{ShipDiagnosticsThresholds.RcsRemassKg:0} kg the console wants.");

        // 11. BACKUP POWER — Powered.PowerConnected AT THE CONSOLE, so it is measured from the console's own
        // power inputs. No console, nothing to measure from.
        var navInputs = navs
            .SelectMany(nav => catalog.Lookup(nav.Part.DefName)?.PowerInputPoints ?? [], (nav, pt) => grid.MapPointTile(nav, pt))
            .Where(t => t >= 0)
            .ToHashSet();
        if (navs.Count == 0)
            Add(11, "NO CONSOLE", DiagState.Bad,
                "Backup power is read at the nav console's own power inputs, and there is no console to read it at.");
        else
        {
            var kWh = PowerNetwork.PowerConnectedTo(grid, catalog, navInputs);
            Add(11, Power(kWh), kWh >= ShipDiagnosticsThresholds.BackupPowerKWh ? DiagState.Good : DiagState.Bad,
                kWh >= ShipDiagnosticsThresholds.BackupPowerKWh ? null
                : navInputs.Count == 0
                    ? "The nav console declares no power input point, so nothing can feed it."
                    : $"Only {Power(kWh)} of charge reaches the console, under the " +
                      $"{ShipDiagnosticsThresholds.BackupPowerKWh:0} kWh the console wants. Run a POWR conduit " +
                      "from a battery to the console: a battery the network never reaches counts for nothing. " +
                      "Turn on PowerViz (P) to see which runs are live.");
        }

        // 12/13. LIFE SUPPORT O2 — the pumps, and the stores UNDER them. A hold full of O2 with no pump plumbed
        // to a can reads 0.00 kg here, in game too.
        var (pumps, fedPumps, o2Mass) = ShipValue.ScanO2Supply(grid, catalog);
        Add(12, $"{fedPumps}/{pumps}", fedPumps > 0 ? DiagState.Good : DiagState.Bad,
            fedPumps > 0 ? null
            : pumps == 0
                ? "No air pump installed, so nothing pressurises the ship. It also forfeits the ×3 O2 bonus on " +
                  "the ship's broker value. Add one from the HVAC tab."
                : $"{Count(pumps, "air pump")} installed but fed by nothing: an O2 RTA canister has to sit on the " +
                  "pump's gas-input tile, and hold O2.");

        Add(13, Kg2(o2Mass), o2Mass > ShipDiagnosticsThresholds.O2StoresKg ? DiagState.Good : DiagState.Bad,
            o2Mass > ShipDiagnosticsThresholds.O2StoresKg ? null
            : fedPumps == 0
                ? "Stores are measured in the canisters at the pumps' gas inputs, and no pump is fed — so this " +
                  "reads zero however much O2 is stowed elsewhere aboard."
                : $"{Kg2(o2Mass)} at the pumps, under the {ShipDiagnosticsThresholds.O2StoresKg:0} kg the console wants.");

        // 14/15. LIFE SUPPORT HEAT / COOL — first switched-on one wins, same shape as the distributor row.
        AddSwitchRow(14, HeaterTrigger, "heater",
            "No heater. Nothing warms the ship, so the crew freeze once out of the sun. Add one from the HVAC tab.");
        AddSwitchRow(15, CoolerTrigger, "cooler",
            "No cooler. Nothing sheds waste heat, so the ship cooks under load. Add one from the HVAC tab.");

        void AddSwitchRow(int i, string trigger, string noun, string missing)
        {
            var found = Matching(grid, catalog, trigger);
            var on = found.Count(p => !p.Part.Has(OffCond));
            Add(i, found.Count == 0 ? "NOT FOUND" : on > 0 ? "ONLINE" : "OFFLINE",
                on > 0 ? DiagState.Good : DiagState.Bad,
                on > 0 ? null : found.Count == 0 ? missing : $"{Count(found.Count, noun)} installed but switched off.");
        }

        return new ShipDiagnosticReport(rows);
    }

    // --- helpers

    /// <summary>Placed parts a trigger fires on — <c>Ship.GetICOs1</c> over the design.</summary>
    private static List<PlacedPart> Matching(ShipGrid grid, Catalog catalog, string trigger) =>
        [.. grid.Parts.Where(p => Fires(trigger, p, catalog))];

    /// <summary>Σ one cond over the parts a trigger fires on, with how many parts contributed.</summary>
    private static (int Count, double Total) SumCond(ShipGrid grid, Catalog catalog, string trigger, string cond)
    {
        var parts = Matching(grid, catalog, trigger);
        return (parts.Count, parts.Sum(p => p.Part.StartingCondValues.GetValueOrDefault(cond)));
    }

    private static bool Fires(string trigger, PlacedPart part, Catalog catalog) =>
        catalog.Triggers.TryGetValue(trigger, out var ct)
            ? CondEval.Triggered(ct, part.Part.CondSet, catalog)
            : part.Part.Has(trigger);

    private static string Kg0(double kg) => $"{kg:N0} kg";

    private static string Kg2(double kg) => $"{kg:N2} kg";

    /// <summary>The console's own power formatting: kWh, switching to GWh past a million.</summary>
    private static string Power(double kWh) => kWh >= 1_000_000 ? $"{kWh / 1_000_000:N0} GWh" : $"{kWh:N0} kWh";

    private static string Count(int n, string singular, string? plural = null) =>
        $"{n} {(n == 1 ? singular : plural ?? singular + "s")}";
}

/// <summary>
/// The pass/fail cutoffs <c>ShipStatus.PrintStatus</c> compiles in. They live in the DLL, not the game data, so a
/// patch can move them with nothing in <c>data/</c> to show for it — these constants and the test that pins them
/// are the only warning there would be. Re-verify with the rest of the checklist in
/// <c>docs/GAME-INTERNALS.md</c> after every game update.
/// </summary>
public static class ShipDiagnosticsThresholds
{
    /// <summary>He3 must exceed this to read green (<c>num &gt; 100.0</c>).</summary>
    public const double He3Kg = 100.0;

    /// <summary>D2O must exceed this to read green (<c>num &gt; 1000.0</c>).</summary>
    public const double D2OKg = 1000.0;

    /// <summary>RCS reaction mass must reach this to read green (<c>num &gt;= 200.0</c>).</summary>
    public const double RcsRemassKg = 200.0;

    /// <summary>Backup power must reach this to read green (<c>num &gt;= 20.0</c>).</summary>
    public const double BackupPowerKWh = 20.0;

    /// <summary>O2 stores must exceed this to read green (<c>num &gt; 35.0</c>).</summary>
    public const double O2StoresKg = 35.0;

    /// <summary>Switched-on RCS clusters must <b>exceed</b> this to read green (<c>num2 &gt; 1.0</c>) — so one
    /// cluster is a fault and two is the minimum, which is the row people most often misread.</summary>
    public const int MinRcsClustersOn = 1;
}
