namespace Ostraplan.Core;

/// <summary>
/// A design's propulsion performance: what it can pull on RCS and on the torch drive, how much
/// reaction mass and reactant it carries, and (when a figure reads zero) ready-to-show lines saying
/// which link in the chain is missing. Every derived figure is a computed property over the raw
/// counts, so the UI can re-read them after <see cref="WithExtraMass"/> without re-analysing the ship.
/// </summary>
/// <param name="PartsMass">Σ <c>StatMass</c> over the placed parts — the game's own ship-mass walk
/// (<c>Ship.Mass</c> → <c>GetCondAmount("StatMass", bAllowDocked: false)</c>), which visits <b>top-level</b>
/// condowners with no <c>IsInstalled</c> filter. Deliberately not <see cref="ShipRating.Mass"/>, which counts
/// installed parts only (see <see cref="Propulsion"/>).</param>
/// <param name="LooseMass">Σ <c>StatMass</c> over loose items lying on the deck. They are top-level
/// condowners too, so the game weighs them; Ostraplan keeps them off the analysis grid, hence the separate sum.</param>
/// <param name="ExtraMass">Additional mass the design is expected to haul (a towed ship, a hold full of
/// salvage). The game's own docked-mass path: <c>Ship.RCSAccelMax</c> divides by this ship's mass plus every
/// docked ship's. User-supplied, persisted with the design.</param>
/// <param name="RcsThrust">Σ <c>StatThrustStrength</c> over installed, switched-on RCS clusters
/// (<c>TIsRCSClusterAudioEmitter</c>) — the game's <c>fRCSCount</c>, and the same figure behind the Maneuver grade.</param>
/// <param name="RcsClustersPresent">Installed RCS clusters ignoring their power state, so an all-Off design
/// can be told apart from one with no thrusters at all.</param>
/// <param name="RcsDistrosOn">Installed, switched-on RCS distributors (<c>TIsRCSDistroInstalledOn</c>) — the
/// parts whose <c>GasInput</c> points define what is actually plumbed in.</param>
/// <param name="RcsDistrosPresent">Installed RCS distributors ignoring their power state.</param>
/// <param name="RcsTankCount">Feed positions matched: how many (distributor, GasInput point, container) hits the
/// scan found. Not a distinct-tank count — the game does not de-duplicate (see <see cref="Propulsion"/>).</param>
/// <param name="RcsReactionMass">The gas mass those containers hold now — <c>Ship.GetRCSRemain</c>.</param>
/// <param name="RcsReactionMassMax">What they would hold brim-full of N2 — <c>Ship.GetRCSMax</c>.</param>
/// <param name="FusionVe">The reactor's exhaust velocity <c>StatICVe</c> in m/s, read from its ignited
/// counterpart (the only form that carries it).</param>
/// <param name="PelletMax">The reactor's <c>StatICPellMax</c>: <c>2 × min(feed chain, laser chain)</c>.
/// Scales both torch thrust and reactant burn rate; zero means the reactor cannot fire.</param>
/// <param name="ReactantD2O">Σ <c>StatLiqD2O</c> over the ship's <c>ItmCanisterLH02</c> tanks.</param>
/// <param name="ReactantHe3">Σ <c>StatSolidHe3</c> over the ship's <c>ItmCanisterLHe02</c> tanks.</param>
public sealed record PropulsionEstimate(
    double PartsMass, double LooseMass, double ExtraMass,
    double RcsThrust, int RcsClustersPresent,
    int RcsDistrosOn, int RcsDistrosPresent, int RcsTankCount,
    double RcsReactionMass, double RcsReactionMassMax,
    bool HasReactor, double FusionVe, double PelletMax,
    int Lasers, int Capacitors, int Feeders, int Regulators,
    double ReactantD2O, double ReactantHe3,
    IReadOnlyList<string> RcsNotes, IReadOnlyList<string> TorchNotes)
{
    /// <summary>The mass every figure below divides by: placed parts + loose deck items + whatever the
    /// design is told it will haul.</summary>
    public double Mass => PartsMass + LooseMass + ExtraMass;

    /// <summary>The same estimate with a different haul mass. Pure arithmetic — no re-scan — so the report
    /// can follow a slider without re-analysing the ship.</summary>
    public PropulsionEstimate WithExtraMass(double kg) => this with { ExtraMass = Math.Max(0, kg) };

    /// <summary>Total RCS thrust in newtons. Each unit of <see cref="RcsThrust"/> is ~57.3 kN
    /// (the game's <c>100 × 0.728 × 5.26077e-9</c> AU/s² per unit mass, converted out of AU).</summary>
    public double RcsThrustNewtons => Propulsion.RcsAccelScale * (Propulsion.RcsMassFlow * RcsThrust)
                                      * Propulsion.RcsAccelConst / Propulsion.AuPerMetre;

    /// <summary>Peak RCS acceleration in G — <c>Ship.RCSAccelMax</c> rendered the way the nav console
    /// renders an acceleration (<c>/ 6.684587e-12 / 9.81</c>).</summary>
    public double RcsAccelG => Mass > 0 ? RcsThrustNewtons / Mass / Propulsion.StandardGravity : 0;

    /// <summary>Delta-v available on the reaction mass aboard, m/s — <c>Ship.DeltaVRemainingRCS</c>.
    /// The thruster count cancels out of the game's expression, so delta-v is set purely by reaction
    /// mass over ship mass: more thrusters buy acceleration, never range.</summary>
    public double RcsDeltaV => DeltaVFor(RcsReactionMass);

    /// <summary>Delta-v with every feed tank brim-full of N2 — <c>Ship.DeltaVMaxRCS</c>.</summary>
    public double RcsDeltaVFull => DeltaVFor(RcsReactionMassMax);

    private double DeltaVFor(double reactionMass) =>
        RcsThrust > 0 && Mass > 0
            ? RcsThrustNewtons / Mass * reactionMass / Propulsion.RcsMassFlow / RcsThrust
            : 0;

    /// <summary>Peak torch thrust in newtons — the game's <c>Ship.fFusionThrustMax</c>, which
    /// <c>FusionIC.Fusion</c> recomputes every tick from the reactor's Ve and pellet ceiling.</summary>
    public double TorchThrustNewtons => Propulsion.FusionThrustMax(FusionVe, PelletMax);

    /// <summary>Peak torch acceleration in G, at full cycle and a reactor at its ideal core temperature
    /// — <c>GetMaxTorchThrust(1.0) / 6.684587e-12 / 9.81</c>, the nav console's course-plot readout with the
    /// limiter wide open.</summary>
    public double TorchAccelG => Mass > 0 ? TorchThrustNewtons / Mass / Propulsion.StandardGravity : 0;

    /// <summary>Reactant burn rate at full flow, kg/s across both reactants.</summary>
    public double ReactantMassFlow => Propulsion.FusionMassFlow(FusionVe, PelletMax);

    /// <summary>Seconds of full-flow burn left, whichever reactant runs out first — the quantity behind the
    /// console's reactant clock (<c>Ship.fShallowFusionRemain</c>).</summary>
    public double ReactantSeconds
    {
        get
        {
            var flow = ReactantMassFlow;
            if (flow <= 0) return 0;
            return Math.Min(ReactantD2O / (flow * Propulsion.ReactantShareD2O),
                            ReactantHe3 / (flow * Propulsion.ReactantShareHe3));
        }
    }

    /// <summary>Hours of full-flow burn left. Burning at a lower cycle lasts proportionally longer;
    /// the console shows this same full-flow figure.</summary>
    public double ReactantHours => ReactantSeconds / 3600.0;

    /// <summary>Which reactant runs dry first, or null when the torch cannot fire at all.</summary>
    public string? LimitingReactant
    {
        get
        {
            var flow = ReactantMassFlow;
            if (flow <= 0) return null;
            return ReactantD2O / Propulsion.ReactantShareD2O <= ReactantHe3 / Propulsion.ReactantShareHe3
                ? "deuterium" : "helium-3";
        }
    }

    public bool HasRcsFigures => RcsThrust > 0;
    public bool HasTorchFigures => PelletMax > 0 && FusionVe > 0;
}

/// <summary>
/// Ports the game's propulsion maths (verified 1.0.0.7): the RCS acceleration and delta-v the nav
/// console's Reserves module shows, and the torch acceleration and reactant clock its Course Plot and
/// Torch Drive modules show. None of it is surfaced anywhere but that one console in game, which is why
/// a planner has to recompute it.
///
/// <para><b>RCS</b> (<c>Ship.RCSAccelMax</c> / <c>DeltaVRemainingRCS</c>). Thrust is
/// <c>100 × (0.728 × fRCSCount) × 5.26077e-9</c> AU/s² per unit mass, where <c>fRCSCount</c> sums
/// <c>StatThrustStrength</c> over installed, switched-on clusters. Delta-v divides that by
/// <c>0.728 × fRCSCount</c> again, so the count cancels and delta-v reduces to a fixed exhaust velocity
/// (~78.7 km/s) times reaction mass over ship mass. Gas mass never enters <c>StatMass</c>, so burning
/// reaction mass does not lighten the ship: the game's delta-v is linear, not a rocket equation.</para>
///
/// <para><b>Reaction mass</b> (<c>Ship.GetRCSRemain</c> / <c>GetRCSMax</c>) is not "the N2 aboard". It is the
/// gas in containers found at an installed, switched-on RCS <b>distributor</b>'s <c>GasInput</c> map points.
/// A canister in a rack feeds nothing. Any airtight container qualifies (<c>TIsRCSValidInput</c> requires
/// <c>IsAirtight</c> and forbids <c>IsHuman</c>/<c>IsSystem</c>) and <i>all</i> its gases count by mass, so an
/// O2 tank is reaction mass too; capacity, however, is always priced as if the tank were full of N2.</para>
///
/// <para><b>Torch</b> (<c>FusionIC.Fusion</c> + <c>Ship.GetMaxTorchThrust</c>). Thrust and burn rate both scale
/// with <c>StatICPellMax</c> = <c>2 × min(min(feeders, 2×regulators), min(lasers, 2×capacitors))</c>, computed
/// over the modules sitting on the reactor core's twelve <c>Module01..12</c> map points. So a laser without a
/// capacitor, or a feeder without a fuel regulator, contributes nothing: this is the "have I enough
/// laser-feeder pairs" question, answered before the ship is built.</para>
///
/// <para><b>Two deliberate divergences from what the console prints, both documented in GAME-INTERNALS §20.</b>
/// First, <c>NavModReserves</c> applies the docked-mass ratio a second time on top of an acceleration that
/// already includes it, under-reading delta-v by <c>(M/M_total)²</c> under tow; this follows
/// <c>Ship.DeltaVRemainingRCS</c>, the value the autopilot and AI actually plan against, so
/// <see cref="PropulsionEstimate.ExtraMass"/> scales once. Second, the torch figures read <c>StatICVe</c> from
/// the reactor's <b>ignited</b> counterpart, because the installable core is the Off form and only
/// <c>…Ignition</c> carries the cond — a literal read would report zero thrust for every design ever planned.
/// The RCS side needs no such help: <see cref="Catalog"/> already builds the switched-on form of clusters and
/// distributors, so an all-zero RCS reading means the design really is dead in the water.</para>
/// </summary>
public static class Propulsion
{
    // --- ported constants. Values suffixed 'f' in the game are float literals whose widened double form is
    // what the game's own double expressions use (0.728f widens to 0.7279999852180481, which is exactly the
    // divisor DeltaVRemainingRCS spells out), so they are declared from float literals here to match bit for bit.

    /// <summary>AU per metre. The nav console divides an AU-space acceleration by this to print metres.</summary>
    public const double AuPerMetre = 6.6845869117759804E-12;

    /// <summary>Standard gravity, as the console's G readouts use it.</summary>
    public const double StandardGravity = 9.81;

    /// <summary>Reaction mass per unit thrust per second (<c>0.728f</c>).</summary>
    public const double RcsMassFlow = 0.728f;

    /// <summary>RCS acceleration coefficient (<c>5.26077E-09f</c> AU/s²).</summary>
    public const double RcsAccelConst = 5.26077E-09f;

    /// <summary>The bare <c>100f</c> factor in front of the RCS acceleration expression.</summary>
    public const double RcsAccelScale = 100f;

    /// <summary>Nominal fusion exhaust velocity, <c>FusionIC.FUSION_VE</c>. The reactor's own
    /// <c>StatICVe</c> is divided by this to get the ratio both fusion expressions scale by.</summary>
    public const double FusionVeNominal = 70500000f;

    /// <summary>Thrust coefficient in <c>FusionIC.Fusion</c> (0.35e12 W × the 0.95 thrust-mode fraction).</summary>
    public const double FusionThrustConst = 332499980926.5137;

    /// <summary>Mass-flow coefficient in <c>FusionIC.Fusion</c> (2 × 0.35e12 W).</summary>
    public const double FusionMassFlowConst = 699999988079.071;

    /// <summary>The game's unexplained <c>FUSION_FUDGE</c> scalar, applied to both thrust and mass flow.</summary>
    public const double FusionFudge = 393.06358381502895;

    /// <summary>Deuterium's share of the reactant mass flow, <c>FusionIC.aReactantAmounts[0]</c>.</summary>
    public const double ReactantShareD2O = 0.667f;

    /// <summary>Helium-3's share of the reactant mass flow, <c>FusionIC.aReactantAmounts[1]</c>.</summary>
    public const double ReactantShareHe3 = 1f;

    // --- the game's own trigger names, so a modded part that satisfies them is counted like any other

    /// <summary>Installed, switched-on RCS cluster — what <c>fRCSCount</c> sums.</summary>
    public const string RcsClusterOnTrigger = "TIsRCSClusterAudioEmitter";

    /// <summary>Installed RCS cluster regardless of power state, for telling "none" from "all off".</summary>
    public const string RcsClusterTrigger = "TIsRCSClusterInstalled";

    /// <summary>Installed, switched-on RCS distributor — whose GasInput points define the feed.</summary>
    public const string RcsDistroOnTrigger = "TIsRCSDistroInstalledOn";

    /// <summary>Installed RCS distributor regardless of power state.</summary>
    public const string RcsDistroTrigger = "TIsRCSDistroInstalled";

    /// <summary>A container the RCS feed will accept: airtight, not a person, not a system object.</summary>
    public const string RcsFeedTrigger = "TIsRCSValidInput";

    /// <summary>The core the ship treats as its reactor (<c>Ship.Reactor</c> = first <c>aCores</c> entry).</summary>
    public const string ReactorTrigger = "TIsReactorICNAVUsable";

    /// <summary>A candidate fusion module: installed, undamaged, and not structure.</summary>
    public const string FusionModuleTrigger = "TIsFusionModule";

    /// <summary>The reactant tanks, matched by condowner name exactly as <c>CODicts.GetTriggeredCOListByType</c>
    /// does. A modded or reskinned tank under any other name holds no reactant as far as the reactor is
    /// concerned, however much <c>StatLiqD2O</c> it carries.</summary>
    public const string D2OTankDef = "ItmCanisterLH02";

    /// <inheritdoc cref="D2OTankDef"/>
    public const string He3TankDef = "ItmCanisterLHe02";

    /// <summary>Peak torch thrust in newtons for a reactor with exhaust velocity <paramref name="ve"/> and
    /// pellet ceiling <paramref name="pelletMax"/> — <c>Ship.fFusionThrustMax</c> as <c>FusionIC</c> sets it.</summary>
    public static double FusionThrustMax(double ve, double pelletMax) =>
        FusionThrustConst * (ve / FusionVeNominal) / FusionVeNominal * pelletMax * FusionFudge;

    /// <summary>Reactant mass flow in kg/s at full pellet rate — the <c>num8</c> divisor behind the
    /// console's reactant clock.</summary>
    public static double FusionMassFlow(double ve, double pelletMax) =>
        FusionMassFlowConst * (ve / FusionVeNominal) / FusionVeNominal / FusionVeNominal * FusionFudge * pelletMax;

    /// <summary>
    /// Measure a design's propulsion. <paramref name="grid"/> supplies the placed parts and the map-point
    /// geometry; <paramref name="doc"/> supplies the loose deck items and the design's haul mass, neither of
    /// which reaches the analysis grid.
    /// </summary>
    public static PropulsionEstimate Estimate(ShipDocument doc, ShipGrid grid, Catalog catalog)
    {
        var partsMass = 0.0;
        foreach (var p in grid.Parts)
            partsMass += p.Part.StartingCondValues.GetValueOrDefault("StatMass");

        var looseMass = 0.0;
        foreach (var loose in doc.LooseObjects)
            if (catalog.Lookup(loose.DefName) is { } def)
                looseMass += def.StartingCondValues.GetValueOrDefault("StatMass") * Math.Max(1, loose.Quantity);

        var (rcsThrust, clustersOn, clustersPresent) = MeasureThrusters(grid, catalog);
        var feed = MeasureFeed(grid, catalog);
        var torch = MeasureTorch(grid, catalog);

        return new PropulsionEstimate(
            partsMass, looseMass, doc.ExtraMassKg,
            rcsThrust, clustersPresent,
            feed.DistrosOn, feed.DistrosPresent, feed.TankCount, feed.ReactionMass, feed.ReactionMassMax,
            torch.HasReactor, torch.Ve, torch.PelletMax,
            torch.Lasers, torch.Capacitors, torch.Feeders, torch.Regulators,
            torch.ReactantD2O, torch.ReactantHe3,
            RcsNotes(rcsThrust, clustersOn, clustersPresent, feed),
            TorchNotes(torch));
    }

    // --- RCS

    private static (double Thrust, int On, int Present) MeasureThrusters(ShipGrid grid, Catalog catalog)
    {
        double thrust = 0;
        int on = 0, present = 0;
        foreach (var p in grid.Parts)
        {
            if (Fires(RcsClusterTrigger, p, catalog)) present++;
            if (!Fires(RcsClusterOnTrigger, p, catalog)) continue;
            on++;
            // Ship.AddICO: StatThrustStrength when the cluster declares one, else a flat 1.
            thrust += p.Part.StartingCondValues.TryGetValue("StatThrustStrength", out var t) ? t : 1.0;
        }
        return (thrust, on, present);
    }

    private readonly record struct FeedScan(
        int DistrosOn, int DistrosPresent, int TankCount, double ReactionMass, double ReactionMassMax);

    /// <summary>
    /// Walk every switched-on distributor's <c>GasInput</c> points and total what is plumbed in, exactly as
    /// <c>Ship.GetRCSRemain</c> / <c>GetRCSMax</c> do.
    ///
    /// <para>Two faithfulness notes. The game finds the container by <b>raycast</b>
    /// (<c>GetCOsAtWorldCoords1</c>), which a headless planner cannot reproduce; footprint coverage is the
    /// closest stand-in and is exact for the 1×1 RTAs that actually feed an RCS system (the same approximation
    /// <see cref="ShipValue.CountO2Pumps"/> makes). And the game de-duplicates nothing: a tank spanning two
    /// GasInput points, or shared between two distributors, is counted once per hit. That double count is in
    /// <c>GetRCSRemain</c> itself, which the flight model reads, so it is reproduced rather than corrected.</para>
    /// </summary>
    private static FeedScan MeasureFeed(ShipGrid grid, Catalog catalog)
    {
        var byTile = FootprintIndex(grid);
        int distrosOn = 0, distrosPresent = 0, tanks = 0;
        double remain = 0, max = 0;

        foreach (var distro in grid.Parts)
        {
            if (Fires(RcsDistroTrigger, distro, catalog)) distrosPresent++;
            if (!Fires(RcsDistroOnTrigger, distro, catalog)) continue;
            distrosOn++;

            foreach (var (key, px) in distro.Part.MapPoints)
            {
                if (!key.Contains("GasInput", StringComparison.Ordinal)) continue;
                var tile = grid.MapPointTile(distro, px);
                if (tile < 0 || !byTile.TryGetValue(tile, out var here)) continue;

                foreach (var tank in here)
                {
                    if (ReferenceEquals(tank, distro) || !Fires(RcsFeedTrigger, tank, catalog)) continue;
                    tanks++;
                    remain += GasMass(tank.Part);
                    max += FullN2Mass(tank.Part);
                }
            }
        }
        return new FeedScan(distrosOn, distrosPresent, tanks, remain, max);
    }

    /// <summary>Every gas the container starts with, by mass — <c>GasContainer.Mass</c>. All species count as
    /// reaction mass, which is how a Katydid runs its RCS off O2.</summary>
    private static double GasMass(ResolvedPart part)
    {
        double mass = 0;
        foreach (var (cond, mols) in part.StartingCondValues)
        {
            if (!cond.StartsWith("StatGasMol", StringComparison.Ordinal) || cond == "StatGasMolTotal") continue;
            mass += ShipValue.MolarMass(cond["StatGasMol".Length..]) * mols;
        }
        return mass;
    }

    /// <summary>The container's capacity priced as N2 — <c>Ship.GetRCSMax</c>:
    /// <c>StatGasPressureMax × StatVolume / 293 / 0.008314</c> mol of N2. The game assumes an N2 refill
    /// whatever the tank currently holds, which is what a fuel kiosk sells.</summary>
    private static double FullN2Mass(ResolvedPart part)
    {
        var mols = part.StartingCondValues.GetValueOrDefault("StatGasPressureMax")
                   * part.StartingCondValues.GetValueOrDefault("StatVolume")
                   / 293.0 / 0.008314000442624092;
        return ShipValue.MolarMass("N2") * mols;
    }

    // --- torch

    private readonly record struct TorchScan(
        bool HasReactor, string? CoreDef, double Ve, double PelletMax,
        int Lasers, int Capacitors, int Feeders, int Regulators, int ModulePoints,
        double ReactantD2O, double ReactantHe3);

    private static TorchScan MeasureTorch(ShipGrid grid, Catalog catalog)
    {
        double d2o = 0, he3 = 0;
        foreach (var p in grid.Parts)
        {
            // CODicts.GetTriggeredCOListByType keys on the condowner name, so the match is exact.
            if (p.Part.DefName == D2OTankDef) d2o += p.Part.StartingCondValues.GetValueOrDefault("StatLiqD2O");
            else if (p.Part.DefName == He3TankDef) he3 += p.Part.StartingCondValues.GetValueOrDefault("StatSolidHe3");
        }

        // Ship.Reactor is aCores[0]: the first installed fusion core, in placement order.
        var core = grid.Parts.FirstOrDefault(p => Fires(ReactorTrigger, p, catalog));
        if (core is null) return new TorchScan(false, null, 0, 0, 0, 0, 0, 0, 0, d2o, he3);

        var byTile = FootprintIndex(grid);
        var modules = new List<PlacedPart>();
        var points = 0;
        // FusionIC.Init walks Module01..Module32, stopping at the first name the core does not declare.
        for (var i = 1; i < 33; i++)
        {
            if (!core.Part.MapPoints.TryGetValue($"Module{(i < 10 ? "0" : "")}{i}", out var px)) break;
            points++;
            var tile = grid.MapPointTile(core, px);
            if (tile < 0 || !byTile.TryGetValue(tile, out var here)) continue;
            foreach (var m in here)
                if (!ReferenceEquals(m, core) && Fires(FusionModuleTrigger, m, catalog) && !modules.Contains(m))
                    modules.Add(m);
        }

        // FusionIC classifies each module by the first IsFusion* cond it carries, in this order, and skips a
        // module that is switched off — except capacitors, which it counts by list length whatever their state.
        int lasers = 0, feeders = 0, regulators = 0, capacitors = 0;
        foreach (var m in modules)
        {
            if (m.Part.Has("IsFusionLaserArray")) { if (!m.Part.Has("IsOff")) lasers++; }
            else if (m.Part.Has("IsFusionPelletFeeder")) { if (!m.Part.Has("IsOff")) feeders++; }
            else if (m.Part.Has("IsFusionCapacitor")) capacitors++;
            else if (m.Part.Has("IsFusionFuelRegulator")) { if (!m.Part.Has("IsOff")) regulators++; }
        }

        // StatICPellMax: each chain is clamped by its enabler (a laser needs a capacitor, a feeder needs a
        // regulator, each enabler carrying two), and the weaker chain sets the ceiling.
        var laserChain = Math.Min(lasers, capacitors * 2);
        var feedChain = Math.Min(feeders, regulators * 2);
        var pelletMax = Math.Min(laserChain, feedChain) * 2.0;

        return new TorchScan(true, core.Part.DefName, IgnitedVe(core.Part, catalog), pelletMax,
            lasers, capacitors, feeders, regulators, points, d2o, he3);
    }

    /// <summary>
    /// The core's <c>StatICVe</c>. Only the ignited condowner carries it (the installable form is
    /// <c>…Off</c> and the bare <c>…On</c> item has no condowner at all — see
    /// <c>Catalog.PreferPoweredState</c>), so a placed core is resolved through to its <c>…Ignition</c>
    /// counterpart. Reporting the design's potential is the whole point: a planned ship's reactor is always
    /// unlit.
    /// </summary>
    private static double IgnitedVe(ResolvedPart core, Catalog catalog)
    {
        if (core.StartingCondValues.GetValueOrDefault("StatICVe") is var own && own > 0) return own;

        var stem = core.DefName;
        foreach (var suffix in new[] { "Ignition", "Batt", "Off" })
            if (stem.EndsWith(suffix, StringComparison.Ordinal)) { stem = stem[..^suffix.Length]; break; }

        return catalog.Lookup(stem + "Ignition")?.StartingCondValues.GetValueOrDefault("StatICVe") ?? 0;
    }

    // --- diagnosis

    private static IReadOnlyList<string> RcsNotes(double thrust, int on, int present, FeedScan feed)
    {
        var notes = new List<string>();
        if (thrust <= 0)
            notes.Add(present > 0
                ? $"No RCS thrust: {present} RCS cluster{S(present)} installed but switched off."
                : "No RCS thrust: no RCS clusters installed.");
        else if (on < present)
            notes.Add($"{present - on} of {present} RCS cluster{S(present)} switched off and contributing nothing.");

        if (feed.ReactionMass <= 0)
        {
            if (feed.DistrosPresent == 0)
                notes.Add("No reaction mass: no RCS distributor installed, so no tank can feed the thrusters.");
            else if (feed.DistrosOn == 0)
                notes.Add($"No reaction mass: {feed.DistrosPresent} RCS distributor{S(feed.DistrosPresent)} installed but switched off.");
            else if (feed.TankCount == 0)
                notes.Add("No reaction mass: no airtight tank sits on a distributor's gas input. A canister in a rack feeds nothing.");
            else
                notes.Add($"No reaction mass: {feed.TankCount} tank{S(feed.TankCount)} plumbed in, all empty.");
        }
        return notes;
    }

    private static IReadOnlyList<string> TorchNotes(TorchScan t)
    {
        var notes = new List<string>();
        if (!t.HasReactor) return notes;   // no reactor is a design choice, not a fault

        if (t.PelletMax <= 0)
        {
            var missing = new List<string>();
            if (t.Lasers == 0) missing.Add("laser array");
            if (t.Capacitors == 0) missing.Add("capacitor");
            if (t.Feeders == 0) missing.Add("pellet feeder");
            if (t.Regulators == 0) missing.Add("fuel regulator");
            notes.Add(missing.Count > 0
                ? $"Torch cannot fire: the reactor has no {string.Join(", no ", missing)} on its {t.ModulePoints} module points."
                : "Torch cannot fire: the reactor's modules are all switched off.");
        }
        else
        {
            var laserChain = Math.Min(t.Lasers, t.Capacitors * 2);
            var feedChain = Math.Min(t.Feeders, t.Regulators * 2);
            if (t.Lasers > t.Capacitors * 2)
                notes.Add($"{t.Lasers - t.Capacitors * 2} laser array{S(t.Lasers - t.Capacitors * 2)} idle: {t.Capacitors} capacitor{S(t.Capacitors)} drives at most {t.Capacitors * 2}.");
            if (t.Feeders > t.Regulators * 2)
                notes.Add($"{t.Feeders - t.Regulators * 2} pellet feeder{S(t.Feeders - t.Regulators * 2)} idle: {t.Regulators} fuel regulator{S(t.Regulators)} drives at most {t.Regulators * 2}.");
            if (laserChain != feedChain)
                notes.Add(laserChain < feedChain
                    ? $"Thrust is capped by the laser side ({laserChain} against {feedChain} on the feed side)."
                    : $"Thrust is capped by the feed side ({feedChain} against {laserChain} on the laser side).");
        }

        if (t.Ve <= 0)
            notes.Add($"No exhaust velocity for '{t.CoreDef}': the core declares no StatICVe, even ignited.");
        if (t.ReactantD2O <= 0)
            notes.Add($"No deuterium aboard: the torch burns it from {D2OTankDef} tanks only.");
        if (t.ReactantHe3 <= 0)
            notes.Add($"No helium-3 aboard: the torch burns it from {He3TankDef} tanks only (the smaller LHe01 tank is the cryo feed and holds none).");
        return notes;
    }

    private static string S(int n) => n == 1 ? "" : "s";

    // --- shared helpers

    /// <summary>Tile index → the parts whose <b>socket footprint</b> covers it. The planner's stand-in for the
    /// game's collider raycast at a map point.</summary>
    private static Dictionary<int, List<PlacedPart>> FootprintIndex(ShipGrid grid)
    {
        var byTile = new Dictionary<int, List<PlacedPart>>();
        foreach (var p in grid.Parts)
        {
            var (w, h) = GridMath.Size(p.Part.Item.Width, p.Part.Item.Height, p.Rot);
            for (var dy = 0; dy < h; dy++)
            for (var dx = 0; dx < w; dx++)
            {
                var col = p.TopLeftCol + dx;
                var row = p.TopLeftRow + dy;
                if (!grid.InBounds(col, row)) continue;
                var idx = grid.Index(col, row);
                if (!byTile.TryGetValue(idx, out var list)) byTile[idx] = list = [];
                list.Add(p);
            }
        }
        return byTile;
    }

    private static bool Fires(string trigger, PlacedPart part, Catalog catalog) =>
        catalog.Triggers.TryGetValue(trigger, out var ct)
            ? CondEval.Triggered(ct, part.Part.CondSet, catalog)
            : part.Part.Has(trigger);
}
