using System.Text.Json;

namespace Ostraplan.Core;

/// <summary>
/// One authored atmospheric layer of a body, straight out of <c>data/star_systems</c>'s
/// <c>aAtmosphericValues</c> (<c>JsonAtmosphere</c>).
/// </summary>
/// <param name="Name">The band's own <c>strName</c>, e.g. <c>Venus_Troposphere 48-52km</c>.</param>
/// <param name="CeilingKm"><c>fMaxAltitude</c>: the band's top, measured from the body's <b>centre</b>, not from
/// its surface. Venus's 48-52 km band has a ceiling of 6104, which is its 6052 km radius plus 52.</param>
/// <param name="TempK"><c>fTemp</c> in kelvin. Zero means the band is unauthored and weighs nothing.</param>
/// <param name="Gases">Partial pressure in kPa per gas name, only for the gases the band declares.</param>
/// <param name="MicrometeoroidChance"><c>fMicrometeoroidChance</c>: the per-update odds that a ship in this band
/// is struck (§26). Zero on all but a handful of bands, and this is the <b>only</b> spawn site whose strength
/// varies, so a band declaring it is a band where a strike can be harder than the standard one.</param>
public sealed record AtmosphereBand(string Name, double CeilingKm, double TempK,
    IReadOnlyDictionary<string, double> Gases, double MicrometeoroidChance = 0)
{
    /// <summary>Total pressure, kPa — <c>JsonAtmosphere.GetTotalKPA</c>.</summary>
    public double PressureKPa => Gases.Values.Sum();
}

/// <summary>
/// The atmosphere at one point: partial pressures by gas and a temperature, interpolated between authored bands.
/// </summary>
public sealed record AtmosphereSample(IReadOnlyDictionary<string, double> Gases, double TempK)
{
    /// <summary>Vacuum, as the game models it: no gas, and the cosmic microwave background at 2.72548 K
    /// (<c>BodyOrbit._voidAtmo</c>).</summary>
    public static readonly AtmosphereSample Void = new(new Dictionary<string, double>(StringComparer.Ordinal), 2.72548);

    /// <summary>Total pressure, kPa — <c>JsonAtmosphere.GetTotalKPA</c>.</summary>
    public double PressureKPa => Gases.Values.Sum();

    /// <summary>Mass density in kg/m³ — <c>GasContainer.GetGasDensity(JsonAtmosphere)</c>: each gas contributes
    /// <c>P · M / (R · T)</c> with the game's own gas constant and molar masses. A gas the game's table does not
    /// know weighs nothing, exactly as it does aboard ship (see <see cref="ShipValue.MolarMass"/>).</summary>
    public double DensityKgPerM3 =>
        TempK == 0 ? 0 : Gases.Sum(g => g.Value * ShipValue.MolarMass(g.Key) / Atmosphere.GasConstant / TempK);

    /// <summary>Whether the game would call this "in atmosphere" at all (<c>BodyOrbit.AtmoKPaThreshold</c>).</summary>
    public bool IsAtmosphere => PressureKPa > Atmosphere.AtmoKPaThreshold;

    /// <summary>The gases actually present, heaviest partial pressure first.</summary>
    public IEnumerable<KeyValuePair<string, double>> Present =>
        Gases.Where(g => g.Value > 0).OrderByDescending(g => g.Value);
}

/// <summary>
/// A body a ship can fly at: its radius and mass (for gravity) and its authored atmosphere table. Read from
/// <c>data/star_systems</c>, so a mod that adds a body, or retunes Venus, is picked up like any other data.
/// </summary>
public sealed record CelestialBody(
    string SystemName, string Name, double RadiusKm, double MassKg, IReadOnlyList<AtmosphereBand> Bands)
{
    /// <summary>The top of the authored atmosphere, measured from the body's centre. Above it the game returns
    /// vacuum outright.</summary>
    public double CeilingKm => Bands.Count > 0 ? Bands[^1].CeilingKm : RadiusKm;

    /// <summary>The highest altitude above the surface that still has authored air.</summary>
    public double MaxAltitudeKm => Math.Max(0, CeilingKm - RadiusKm);

    /// <summary>Local gravitational acceleration in m/s² at an altitude above the surface — the game's
    /// <c>StarSystem.GetGravAccelScalar</c> (<c>G' · M / r²</c> in AU/s²) rendered in metres the way every
    /// acceleration readout in the game renders one.</summary>
    public double GravityAt(double altitudeKm)
    {
        var distanceAu = (RadiusKm + Math.Max(0, altitudeKm)) / Atmosphere.KmPerAu;
        if (distanceAu <= 0) return 0;
        return Atmosphere.GravAccelConstant * MassKg / (distanceAu * distanceAu) / Propulsion.AuPerMetre;
    }

    /// <summary>Surface gravity, in m/s².</summary>
    public double SurfaceGravity => GravityAt(0);

    /// <summary>
    /// The atmosphere at an altitude above the surface — <c>BodyOrbit.GetAtmosphereAtDistance</c>.
    ///
    /// <para>The interpolation is the game's, oddities included: within the band that contains the point, every
    /// value is lerped from <b>that</b> band towards the one <b>above</b> it, across the span from the previous
    /// band's ceiling (or the body's radius, for the lowest band) to this band's own ceiling. So a band's authored
    /// figures are what you get at its floor, and its neighbour's are what you get at its ceiling. Above the last
    /// authored band the game returns vacuum with no fade.</para>
    /// </summary>
    public AtmosphereSample SampleAt(double altitudeKm)
    {
        if (Bands.Count == 0) return AtmosphereSample.Void;

        var distanceKm = RadiusKm + Math.Max(0, altitudeKm);
        for (var i = 0; i < Bands.Count; i++)
        {
            if (distanceKm > Bands[i].CeilingKm) continue;

            var floor = i == 0 ? RadiusKm : Bands[i - 1].CeilingKm;
            var t = Atmosphere.InverseLerp(floor, Bands[i].CeilingKm, distanceKm);
            var here = Bands[i];
            var above = i == Bands.Count - 1 ? null : Bands[i + 1];

            var gases = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var gas in Atmosphere.GasNames)
            {
                var a = here.Gases.GetValueOrDefault(gas);
                var b = above?.Gases.GetValueOrDefault(gas) ?? 0;   // vacuum above the top band
                var v = a + (b - a) * t;
                if (v != 0) gases[gas] = v;
            }
            var temp = here.TempK + ((above?.TempK ?? AtmosphereSample.Void.TempK) - here.TempK) * t;
            return new AtmosphereSample(gases, temp);
        }
        return AtmosphereSample.Void;
    }
}

/// <summary>
/// Reads the game's own bodies and atmosphere tables out of <c>data/star_systems</c>, and holds the constants the
/// atmospheric maths needs. Data only: nothing here knows about a ship.
///
/// <para>Only bodies that actually declare an atmosphere are returned, since a body with no air answers no
/// question a flight report could ask. On a stock 1.0.0.9 install that is Venus, Earth, Mars, Titan, Jupiter,
/// Saturn, Uranus and Neptune.</para>
/// </summary>
public static class Atmosphere
{
    /// <summary>The gas species the game's atmosphere record carries, in <c>JsonAtmosphere</c> field order.
    /// A modded band naming anything else is ignored, exactly as the game ignores it.</summary>
    public static readonly string[] GasNames =
        ["CO2", "CH4", "NH3", "N2", "H2SO4", "O2", "H2O", "H2", "He2", "CO", "Smoke"];

    /// <summary>The game's gas constant, kJ/(mol·K) — the divisor in <c>GasContainer.GetGasDensity</c>.</summary>
    public const double GasConstant = 0.008314000442624092;

    /// <summary>
    /// Gravitational constant in the game's AU units (<c>StarSystem.fGravAccelConstant</c>). Declared from the
    /// float literal the game uses, so the widened value matches bit for bit.
    ///
    /// <para>That matters more than usual here: <c>2E-44f</c> is <b>subnormal</b> as a float, where the spacing
    /// between representable values is 1.4×10⁻⁴⁵, so it actually stores 1.9618×10⁻⁴⁴ — about 2% under its written
    /// value. Every gravity in the game is that 2% light as a result (Earth reads 9.66 m/s² rather than 9.81), and
    /// a port that "cleaned this up" to a literal 2e-44 would disagree with the game everywhere.</para>
    /// </summary>
    public const double GravAccelConstant = 2E-44f;

    /// <summary>Kilometres per AU, as <c>BodyOrbit</c> converts a body radius and an atmosphere altitude. The nav
    /// console's acceleration path uses 149597870 instead (see <see cref="Propulsion.AuPerMetre"/>); the two differ
    /// by one part in 7.5×10⁷, far below anything a report can show.</summary>
    public const double KmPerAu = 149597872.0;

    /// <summary>Total pressure above which the game calls a point "in atmosphere" (<c>BodyOrbit.AtmoKPaThreshold</c>).</summary>
    public const double AtmoKPaThreshold = 0.05f;

    /// <summary><c>Mathf.InverseLerp</c>: clamped to [0,1], and 0 when the ends coincide.</summary>
    internal static double InverseLerp(double a, double b, double v) =>
        a == b ? 0 : Math.Clamp((v - a) / (b - a), 0, 1);

    /// <summary>
    /// Every body with an authored atmosphere, name-sorted.
    ///
    /// <para>The same body appears in more than one authored system (the one a new game starts in, plus the
    /// developers' test environments), and the copies disagree about the body itself, so entries are
    /// de-duplicated by name and the pick is the copy from the <b>fullest</b> system. See the ordering below for
    /// why that is the discriminator and not the richest atmosphere table.</para>
    /// </summary>
    public static IReadOnlyList<CelestialBody> LoadBodies(DataIndex index)
    {
        // Paired with the number of bodies in the system each was authored in, which is what tells the system the
        // player actually flies in from a two-body test rig. See the ordering at the end.
        var found = new List<(CelestialBody Body, int SystemSize)>();
        foreach (var (systemName, (el, _)) in index.Type("star_systems"))
        {
            if (!el.TryGetProperty("aSpawnBodies", out var bodies) || bodies.ValueKind != JsonValueKind.Array)
                continue;

            var systemSize = bodies.GetArrayLength();
            foreach (var b in bodies.EnumerateArray())
            {
                if (b.ValueKind != JsonValueKind.Object) continue;
                var bands = ReadBands(b);
                if (bands.Count == 0) continue;                        // no air, nothing to report

                var name = Json.Str(b, "strName");
                if (string.IsNullOrWhiteSpace(name)) continue;
                var radius = Json.Dbl(b, "fRadiusKM");
                var mass = Json.Dbl(b, "fMassKG");
                if (radius <= 0 || mass <= 0) continue;                // can't place an altitude or a gravity on it

                found.Add((new CelestialBody(systemName, name, radius, mass, bands), systemSize));
            }
        }

        // The copies of a body disagree about the body, not only about its air: stock 1.0.0.13 gives Venus the real
        // 4.87e24 kg in the system a new game starts in and 4.7e24 kg in three test rigs, which is 8.73 m/s2 of
        // surface gravity against 8.43. Every figure downstream (gravity, lift, drag, whether a design holds
        // altitude) rides on which copy is taken, so take the one from the fullest system: the solar system is 55
        // bodies where a rig is two or three, and it is the only one a player is ever in. Band count breaks a tie
        // between equally complete systems, and the name settles it after that, so the answer never depends on
        // file enumeration order.
        //
        // This used to lead on band count with the system name as the tie-break. All four Venuses carry ten bands,
        // so that landed on the right one purely because "NewGame" sorts ahead of "OKLG_AND_VENUS": renaming the
        // system would have moved every atmospheric figure onto a test rig's numbers with nothing to show for it.
        return found
            .GroupBy(f => f.Body.Name, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(f => f.SystemSize)
                          .ThenByDescending(f => f.Body.Bands.Count)
                          .ThenBy(f => f.Body.SystemName, StringComparer.Ordinal).First().Body)
            .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<AtmosphereBand> ReadBands(JsonElement body)
    {
        if (!body.TryGetProperty("aAtmosphericValues", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];

        var bands = new List<AtmosphereBand>();
        foreach (var a in arr.EnumerateArray())
        {
            if (a.ValueKind != JsonValueKind.Object) continue;
            var gases = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var gas in GasNames)
            {
                var kpa = Json.Dbl(a, "f" + gas);
                if (kpa != 0) gases[gas] = kpa;
            }
            bands.Add(new AtmosphereBand(
                Json.Str(a, "strName") ?? "", Json.Dbl(a, "fMaxAltitude"), Json.Dbl(a, "fTemp"), gases,
                Json.Dbl(a, "fMicrometeoroidChance")));
        }
        // The game walks the array in file order and takes the first band whose ceiling the point is under, so a
        // table authored out of order would read wrong. Sorting is the one liberty taken, and it is a no-op on
        // every authored table there is.
        return bands.OrderBy(b => b.CeilingKm).ToList();
    }
}
