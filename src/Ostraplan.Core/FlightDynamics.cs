namespace Ostraplan.Core;

/// <summary>
/// What the design itself brings to atmospheric flight, measured once and independent of where it flies: its mass,
/// its aerodynamic coefficient, its lift rotors, and the frontal/side areas the drag model derives from its grid.
/// </summary>
/// <param name="Mass">The mass every acceleration divides by — the same walk
/// <see cref="PropulsionEstimate.Mass"/> makes (placed parts + loose deck items + declared dead weight), because
/// the game's flight model divides by <c>Ship.Mass</c> exactly as its propulsion does.</param>
/// <param name="AeroCoefficient"><c>Ship.fAeroCoefficient</c>: 1, plus <c>StatAeroLift</c> summed over every
/// installed ship-special part that declares it. Aero hull carries 100 per 1×1 and 200 per slant, so a bare ship
/// sits at 1 and a winged one in the thousands.</param>
/// <param name="AeroParts">How many placed parts contributed to <see cref="AeroCoefficient"/>.</param>
/// <param name="RotorThrust">Σ <c>StatThrustStrength × 30</c> over installed, switched-on heavy lift rotors —
/// <c>Ship.LiftRotorsThrustStrength</c> via <c>Rotor.ThrustStrength</c>, in kN.</param>
/// <param name="RotorThrustTurbo">The same sum taken from <c>StatThrustStrengthTurbo</c>: what those rotors give
/// with turbo engaged, which is a switch at the console rather than anything the layout decides.</param>
/// <param name="RotorsActive">Installed rotors that are switched on — what the thrust sums over.</param>
/// <param name="RotorsPresent">Installed rotors regardless of power state, so an all-off design can be told apart
/// from one with no rotors at all.</param>
/// <param name="NCols">The analysis grid's width in tiles — half of the game's drag size term.</param>
/// <param name="NRows">The analysis grid's height in tiles.</param>
/// <param name="Notes">Ready-to-show lines naming whichever link in the chain is missing.</param>
public sealed record FlightProfile(
    double Mass, double AeroCoefficient, int AeroParts,
    double RotorThrust, double RotorThrustTurbo, int RotorsActive, int RotorsPresent,
    int NCols, int NRows, IReadOnlyList<string> Notes)
{
    /// <summary>The characteristic size the drag model uses: <c>(nCols + nRows) × 0.32 / 2</c>, the mean of the
    /// ship's two grid dimensions in metres.</summary>
    public double SizeMetres => (NCols + NRows) * FlightDynamics.TileMetres / 2.0;

    /// <summary>The size-driven multiplier behind both drag areas: <c>Lerp(3, 15, (size − 3) / 50)</c>, clamped.
    /// A bigger ship is punished more than linearly, which is the whole of the game's shape model.</summary>
    public double DragScale =>
        FlightDynamics.Lerp(FlightDynamics.DragScaleMin, FlightDynamics.DragScaleMax,
            (SizeMetres - FlightDynamics.DragScaleOffset) / FlightDynamics.DragScaleSpan);

    /// <summary>Effective area presented nose-on, m². Aero hull cuts it: the game divides by
    /// <c>max(1, aeroCoefficient / 100)</c>, so the first hundred points of <c>StatAeroLift</c> buy nothing and
    /// every hundred after that divides the frontal drag.</summary>
    public double DragAreaFront => DragAreaSide / Math.Max(1.0, AeroCoefficient / 100.0);

    /// <summary>Effective area presented side-on, m². Aero hull does <b>not</b> reduce this: broadside, a wing is
    /// just more ship in the airflow.</summary>
    public double DragAreaSide => SizeMetres * DragScale;

    /// <summary>The same profile with a different haul mass, matching <see cref="PropulsionEstimate.WithExtraMass"/>
    /// so both reports answer to one number. Pure arithmetic: no re-scan.</summary>
    public FlightProfile WithMass(double kg) => this with { Mass = Math.Max(0, kg) };

    public bool HasRotors => RotorThrust > 0;
    public bool HasAero => AeroCoefficient > 1;
}

/// <summary>
/// The design flown at one point: an atmosphere, a local gravity, an airspeed and an attitude. Every figure is a
/// computed property over the inputs, so a UI can follow a slider without re-measuring the ship.
/// </summary>
/// <param name="Profile">What the design brings.</param>
/// <param name="Gravity">Local gravitational acceleration, m/s².</param>
/// <param name="Density">Atmospheric mass density, kg/m³.</param>
/// <param name="PressureKPa">Ambient pressure, kPa — what rotor efficiency is read off.</param>
/// <param name="TempK">Ambient temperature, K. Carried for display; the density already accounts for it.</param>
/// <param name="Airspeed">Speed relative to the air, m/s. The game measures this against the body's own velocity,
/// so it is airspeed rather than orbital speed.</param>
/// <param name="AngleOfAttackDeg">Angle between the ship's facing and its motion through the air, 0–180. 0 is
/// nose-on, 90 is broadside.</param>
/// <param name="AttitudeDeg">Angle between the ship's facing and the local horizontal, 0–180. It is the second
/// cosine in the game's lift expression: a ship pointed straight up its own gravity vector makes no lift.</param>
/// <param name="RcsThrustNewtons">Peak RCS thrust from <see cref="PropulsionEstimate.RcsThrustNewtons"/>. The
/// game's MIXED engine mode adds RCS to rotor thrust, so a report that leaves it out understates what a design can
/// actually hold.</param>
public sealed record FlightPoint(
    FlightProfile Profile,
    double Gravity, double Density, double PressureKPa, double TempK,
    double Airspeed, double AngleOfAttackDeg, double AttitudeDeg,
    double RcsThrustNewtons)
{
    // ---- rotors ----

    /// <summary>How much of a rotor's rated thrust the air here supports — <c>Ship.CurrentRotorEfficiency</c>:
    /// ambient pressure over 100 kPa, capped at 1.5. Rotors in vacuum give nothing, and thick air gives half as
    /// much again as sea level.</summary>
    public double RotorEfficiency =>
        Math.Clamp(PressureKPa / FlightDynamics.RotorEfficiencyPressure, 0, FlightDynamics.RotorEfficiencyMax);

    /// <summary>Rotor thrust actually available here, in newtons.</summary>
    public double RotorThrustNewtons => Profile.RotorThrust * RotorEfficiency * FlightDynamics.NewtonsPerKn;

    /// <summary>The same with turbo engaged at the console.</summary>
    public double RotorThrustTurboNewtons => Profile.RotorThrustTurbo * RotorEfficiency * FlightDynamics.NewtonsPerKn;

    /// <summary>Acceleration from the rotors alone, m/s², at full stick.</summary>
    public double RotorAccel => Profile.Mass > 0 ? RotorThrustNewtons / Profile.Mass : 0;

    /// <summary>Acceleration from the rotors with turbo engaged, m/s².</summary>
    public double RotorAccelTurbo => Profile.Mass > 0 ? RotorThrustTurboNewtons / Profile.Mass : 0;

    // ---- aerodynamic lift ----

    /// <summary>
    /// Lift acceleration in m/s², before the cap — <c>ShipSitu.CalculateLiftDrag</c>:
    /// <c>0.5 ρ v² · (aero / mass) · cos(AoA) · cos(attitude) / mass</c>.
    ///
    /// <para>The mass appears <b>twice</b>, and that is the game's own expression rather than a slip in the port:
    /// the coefficient it forms is <c>fAeroCoefficient / Mass</c>, and the force it produces is then divided by
    /// mass again to make an acceleration. So doubling a design's mass quarters its lift, which is the single most
    /// important thing to know when designing around it.</para>
    /// </summary>
    public double LiftAccelRaw =>
        Profile.Mass > 0
            ? Math.Abs(0.5 * Density * Airspeed * Airspeed * (Profile.AeroCoefficient / Profile.Mass)
                       * Math.Cos(AngleOfAttackDeg * FlightDynamics.DegToRad)
                       * Math.Cos(AttitudeDeg * FlightDynamics.DegToRad)) / Profile.Mass
            : 0;

    /// <summary>Lift acceleration as the game applies it, m/s². Capped at ten times local gravity, which is the
    /// game's own ceiling and the reason a very light, very winged design stops gaining from more speed.</summary>
    public double LiftAccel => Math.Min(Gravity * FlightDynamics.LiftAccelCapG, LiftAccelRaw);

    /// <summary>True when the cap above is what is limiting lift.</summary>
    public bool LiftCapped => LiftAccelRaw > Gravity * FlightDynamics.LiftAccelCapG && LiftAccelRaw > 0;

    /// <summary>
    /// The airspeed at which aerodynamic lift alone would equal local gravity, m/s, at this attitude. Null when it
    /// never can: no air, no aero hull, or a nose-on angle that cancels the lift term outright.
    /// </summary>
    public double? HoverAirspeed
    {
        get
        {
            var k = 0.5 * Density * (Profile.AeroCoefficient / Profile.Mass)
                    * Math.Abs(Math.Cos(AngleOfAttackDeg * FlightDynamics.DegToRad))
                    * Math.Abs(Math.Cos(AttitudeDeg * FlightDynamics.DegToRad)) / Profile.Mass;
            return Profile.Mass > 0 && k > 0 && Gravity > 0 ? Math.Sqrt(Gravity / k) : null;
        }
    }

    // ---- drag ----

    /// <summary>The effective area in the airflow at this angle of attack — the game lerps from the frontal area
    /// to the side area by <c>sin(AoA)</c>, so broadside is the worst of it and nose-on or tail-on the best.</summary>
    public double DragArea =>
        FlightDynamics.Lerp(Profile.DragAreaFront, Profile.DragAreaSide,
            Math.Sin(AngleOfAttackDeg * FlightDynamics.DegToRad));

    /// <summary>Drag deceleration in m/s², before the cap: <c>0.5 ρ v² · area / mass</c>.</summary>
    public double DragAccelRaw =>
        Profile.Mass > 0 ? 0.5 * Density * Airspeed * Airspeed * DragArea / Profile.Mass : 0;

    /// <summary>Drag deceleration as the game applies it, m/s² — clamped to 2000, a little over 200 g.</summary>
    public double DragAccel => Math.Clamp(DragAccelRaw, 0, FlightDynamics.DragAccelCap);

    /// <summary>True when the clamp above is what is limiting drag: past this point the design is being held
    /// together by a hard limit in the flight model rather than by anything about the ship.</summary>
    public bool DragCapped => DragAccelRaw > FlightDynamics.DragAccelCap;

    // ---- the balance ----

    /// <summary>Acceleration from RCS alone, m/s². The game's MIXED mode fires RCS alongside the rotors.</summary>
    public double RcsAccel => Profile.Mass > 0 ? RcsThrustNewtons / Profile.Mass : 0;

    /// <summary>Everything working against gravity, m/s²: aerodynamic lift, the rotors at full stick, and RCS,
    /// assuming thrust is pointed straight up. Lift is always anti-gravity by construction in the game's model;
    /// the two thrust terms are only anti-gravity if you point them there.</summary>
    public double UpwardAccel => LiftAccel + RotorAccel + RcsAccel;

    /// <summary>Upward acceleration over local gravity. 1 is holding altitude; below 1 the design sinks.</summary>
    public double SupportRatio => Gravity > 0 ? UpwardAccel / Gravity : 0;

    /// <summary>Can it hold altitude here, with everything pointed the right way?</summary>
    public bool Holds => SupportRatio >= 1;

    /// <summary>Is there enough air here for any of this to mean anything (<c>BodyOrbit.AtmoKPaThreshold</c>)?</summary>
    public bool InAtmosphere => PressureKPa > Atmosphere.AtmoKPaThreshold;

    /// <summary>Accelerations in G, the unit every readout in the game uses.</summary>
    public double InG(double accel) => accel / Propulsion.StandardGravity;
}

/// <summary>
/// Ports the game's atmospheric flight model (verified 1.0.0.9): the lift and drag the nav console's Flight
/// Dynamics module shows, and the rotor thrust that <c>Ship.Maneuver</c> adds to RCS once a design is in air.
/// Like <see cref="Propulsion"/>, none of it is surfaced anywhere but a running ship, so a planner has to
/// recompute it.
///
/// <para><b>Lift</b> (<c>Ship.CalculateLiftDrag</c> → <c>ShipSitu.CalculateLiftDrag</c>).
/// <c>0.5 ρ v² · (fAeroCoefficient / Mass) · cos(AoA) · cos(attitude)</c>, divided by mass again to make an
/// acceleration and capped at ten local gravities. <c>fAeroCoefficient</c> is 1 plus every installed ship-special
/// part's <c>StatAeroLift</c>, which is where aero hull earns its place. The double division by mass is the
/// game's, and it is why lift falls off as the square of mass rather than linearly.</para>
///
/// <para><b>Drag</b>. The ship's grid gives a size, <c>(nCols + nRows) × 0.32 / 2</c>; that size scaled by
/// <c>Lerp(3, 15, (size − 3) / 50)</c> gives the side area, and the frontal area is that divided by
/// <c>max(1, aero / 100)</c>. The angle of attack lerps between the two by <c>sin(AoA)</c>. Deceleration is
/// <c>0.5 ρ v² · area / mass</c>, clamped to 2000 m/s².</para>
///
/// <para><b>Rotors</b> (<c>Ship.LiftRotorsThrustStrength</c> + <c>Rotor.ThrustStrength</c> + <c>Ship.Maneuver</c>).
/// Each installed, switched-on heavy lift rotor contributes <c>StatThrustStrength × 30</c> kN, and the total is
/// scaled by <c>Ship.CurrentRotorEfficiency</c> — ambient pressure over 100 kPa, capped at 1.5 — so a rotor gives
/// nothing in vacuum and half as much again in Venus's deep cloud layer. Turbo swaps in
/// <c>StatThrustStrengthTurbo</c> and is a console switch, not a layout decision, so it is reported as potential.</para>
///
/// <para><b>Two things the model deliberately leaves out.</b> The rotor efficiency the game reads is the pressure
/// of the ship's own Void room, which its atmosphere sync sets to the ambient figure, so reading it from the
/// atmosphere directly is the same number by a shorter route. And the ship's own radius is added to its distance
/// from the body before the atmosphere is sampled; on a ship that is tens of metres and a band is kilometres
/// thick, so it cannot move a figure and is dropped.</para>
/// </summary>
public static class FlightDynamics
{
    // --- ported constants. Values suffixed 'f' in the game are float literals whose widened double form is what
    // the game's own double expressions use, so they are declared from float literals here to match.

    /// <summary>Metres per grid tile in the drag size term (<c>Ship.CalculateLiftDrag</c>'s <c>0.32</c>).</summary>
    public const double TileMetres = 0.32;

    /// <summary>Lower end of the size-driven drag multiplier.</summary>
    public const double DragScaleMin = 3f;

    /// <summary>Upper end of the size-driven drag multiplier.</summary>
    public const double DragScaleMax = 15f;

    /// <summary>Size at which the drag multiplier starts climbing off its floor.</summary>
    public const double DragScaleOffset = 3.0;

    /// <summary>Size span the drag multiplier climbs over.</summary>
    public const double DragScaleSpan = 50.0;

    /// <summary>Lift acceleration is capped at this many local gravities.</summary>
    public const double LiftAccelCapG = 10f;

    /// <summary>Drag deceleration is clamped here, m/s².</summary>
    public const double DragAccelCap = 2000.0;

    /// <summary>What <c>Rotor.ThrustStrength</c> multiplies a rotor's declared strength by.</summary>
    public const double RotorThrustScale = 30.0;

    /// <summary>Ambient pressure at which a rotor gives its rated thrust, kPa.</summary>
    public const double RotorEfficiencyPressure = 100f;

    /// <summary>Ceiling on rotor efficiency, however thick the air.</summary>
    public const double RotorEfficiencyMax = 1.5f;

    /// <summary>Newtons per kN: the rotor thrust term reaches an acceleration through the game's AU conversion,
    /// which works out to exactly this.</summary>
    public const double NewtonsPerKn = 1000.0;

    /// <summary>The game's own degrees-to-radians literal.</summary>
    public const double DegToRad = 0.01745329238474369;

    // --- the game's own trigger and condition names, so a modded part that satisfies them counts like any other

    /// <summary>Installed, switched-on heavy lift rotor — what <c>aActiveHeavyLiftRotors</c> collects.</summary>
    public const string RotorOnTrigger = "TIsHeavyLiftRotorNotOff";

    /// <summary>Installed heavy lift rotor regardless of power state, for telling "none" from "all off".</summary>
    public const string RotorCond = "IsHeavyLiftRotor";

    /// <summary>A rotor's rated thrust, and the turbo figure it swaps to when turbo is engaged.</summary>
    public const string RotorThrustCond = "StatThrustStrength";

    /// <inheritdoc cref="RotorThrustCond"/>
    public const string RotorThrustTurboCond = "StatThrustStrengthTurbo";

    /// <summary>The per-part contribution to <c>Ship.fAeroCoefficient</c>.</summary>
    public const string AeroLiftCond = "StatAeroLift";

    /// <summary>The gate every one of those blocks sits behind in <c>Ship.AddICO</c>: a part that is not a ship
    /// special item never reaches the rotor or aero branches at all.</summary>
    public const string ShipSpecialCond = "IsShipSpecialItem";

    /// <summary><c>Mathf.Lerp</c>: clamped to the endpoints.</summary>
    internal static double Lerp(double a, double b, double t) => a + (b - a) * Math.Clamp(t, 0, 1);

    /// <summary>
    /// Measure a design's flight profile. <paramref name="grid"/> supplies the placed parts and the grid
    /// dimensions the drag model needs; <paramref name="doc"/> supplies the loose deck items and the declared
    /// dead weight, neither of which reaches the analysis grid.
    /// </summary>
    public static FlightProfile Measure(ShipDocument doc, ShipGrid grid, Catalog catalog)
    {
        var partsMass = 0.0;
        foreach (var p in grid.Parts)
            partsMass += p.Part.StartingCondValues.GetValueOrDefault("StatMass");

        var looseMass = 0.0;
        foreach (var loose in doc.LooseObjects)
            if (catalog.Lookup(loose.DefName) is { } def)
                looseMass += def.StartingCondValues.GetValueOrDefault("StatMass") * Math.Max(1, loose.Quantity);

        // Ship.fAeroCoefficient starts at 1 and accumulates StatAeroLift over installed ship-special parts.
        var aero = 1.0;
        var aeroParts = 0;
        double thrust = 0, thrustTurbo = 0;
        int rotorsOn = 0, rotorsPresent = 0;

        foreach (var p in grid.Parts)
        {
            var conds = p.Part.StartingCondValues;
            var special = p.Part.Has(ShipSpecialCond);

            if (special && conds.TryGetValue(AeroLiftCond, out var lift) && lift != 0)
            {
                aero += lift;
                aeroParts++;
            }

            if (!special || !p.Part.Has(RotorCond)) continue;
            rotorsPresent++;
            if (!Propulsion.Fires(RotorOnTrigger, p, catalog)) continue;
            rotorsOn++;
            thrust += conds.GetValueOrDefault(RotorThrustCond) * RotorThrustScale;
            // no fallback to the base stat: Rotor.ThrustStrength reads StatThrustStrengthTurbo outright when
            // turbo is on, so a (modded) rotor that doesn't declare it genuinely gives nothing in turbo
            thrustTurbo += conds.GetValueOrDefault(RotorThrustTurboCond) * RotorThrustScale;
        }

        return new FlightProfile(
            partsMass + looseMass + doc.ExtraMassKg, aero, aeroParts,
            thrust, thrustTurbo, rotorsOn, rotorsPresent,
            grid.NCols, grid.NRows,
            Notes(aero, aeroParts, thrust, rotorsOn, rotorsPresent));
    }

    private static IReadOnlyList<string> Notes(double aero, int aeroParts, double thrust, int on, int present)
    {
        var notes = new List<string>();

        if (thrust <= 0)
            notes.Add(present > 0
                ? $"No rotor thrust: {present} heavy lift rotor{S(present)} installed but switched off."
                : "No rotor thrust: no heavy lift rotors installed. Without them a design can only fly on wings "
                  + "and RCS.");
        else if (on < present)
            notes.Add($"{present - on} of {present} heavy lift rotor{S(present)} switched off and contributing nothing.");

        if (aeroParts == 0)
            notes.Add("No aerodynamic hull: nothing on this design declares StatAeroLift, so it makes almost no "
                      + "lift and presents its full side area to the airflow whichever way it points.");
        else if (aero < 100)
            notes.Add($"Aero coefficient {aero:0} is under 100, so it does not divide frontal drag at all: the "
                      + "game's divisor is max(1, aero / 100).");

        return notes;
    }

    private static string S(int n) => n == 1 ? "" : "s";
}
