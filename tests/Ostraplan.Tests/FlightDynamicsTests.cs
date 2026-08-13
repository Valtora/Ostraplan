using Ostraplan.Core;
using Xunit;
using Xunit.Abstractions;

namespace Ostraplan.Tests;

/// <summary>
/// The atmospheric flight model: the bodies and atmosphere tables read out of <c>data/star_systems</c>, and the
/// port of the game's lift, drag and rotor maths. Everything here needs the real data, so it no-ops without the
/// install.
/// </summary>
public class FlightDynamicsTests(ITestOutputHelper output)
{
    private const string Rotor = "ItmHeavyLiftRotor01On";
    private const string AeroWall = "ItmWallAero1x1";
    private const string Wall = "ItmWall1x1";

    private static CelestialBody Body(DataIndex index, string name) =>
        Atmosphere.LoadBodies(index).Single(b => b.Name == name);

    // ---- the data layer ----

    [SkippableFact]
    public void Loads_every_body_that_declares_an_atmosphere()
    {
        var g = TestData.RequireGame();
        var bodies = Atmosphere.LoadBodies(g.Index);

        // stock 1.0.0.9: Venus, Earth, Mars, Titan and the four gas giants
        Assert.Contains(bodies, b => b.Name == "Venus");
        Assert.Contains(bodies, b => b.Name == "Earth");
        Assert.Contains(bodies, b => b.Name == "Titan");
        Assert.DoesNotContain(bodies, b => b.Name == "Luna");     // airless: never offered
        Assert.All(bodies, b => Assert.NotEmpty(b.Bands));
        Assert.All(bodies, b => Assert.True(b.MassKg > 0 && b.RadiusKm > 0));

        // the same body is authored in several test systems too; it must appear exactly once
        Assert.Equal(bodies.Select(b => b.Name).Distinct().Count(), bodies.Count);
        output.WriteLine(string.Join(", ", bodies.Select(b => $"{b.Name} ({b.Bands.Count} bands, " +
            $"{b.MaxAltitudeKm:0} km, {b.SurfaceGravity:0.00} m/s²)")));
    }

    [SkippableFact]
    public void Surface_gravity_matches_the_real_bodies()
    {
        var g = TestData.RequireGame();

        // The game's own G' × M / r². It reads about 2% under the real figures throughout, because its
        // gravitational constant is written 2E-44f and a float that small is subnormal, so it stores
        // 1.9618E-44. These are the game's numbers, not physics', and that is the point.
        Assert.Equal(8.43, Body(g.Index, "Venus").SurfaceGravity, 1);   // really 8.87
        Assert.Equal(9.66, Body(g.Index, "Earth").SurfaceGravity, 1);   // really 9.81
        Assert.Equal(3.66, Body(g.Index, "Mars").SurfaceGravity, 1);    // really 3.71
        Assert.Equal(1.34, Body(g.Index, "Titan").SurfaceGravity, 1);   // really 1.35
    }

    [SkippableFact]
    public void The_gravity_constant_is_the_subnormal_float_the_game_stores()
    {
        // Guards the one constant a well-meaning cleanup would "fix" and break every gravity figure by 2%.
        Assert.Equal(1.961817850054744E-44, Atmosphere.GravAccelConstant);
        Assert.NotEqual(2E-44, Atmosphere.GravAccelConstant);
        Assert.True(Atmosphere.GravAccelConstant < 2E-44);
    }

    [SkippableFact]
    public void Gravity_falls_off_with_the_square_of_the_distance()
    {
        var g = TestData.RequireGame();
        var venus = Body(g.Index, "Venus");

        // at one body radius up, gravity is a quarter of what it is at the surface
        Assert.Equal(venus.SurfaceGravity / 4, venus.GravityAt(venus.RadiusKm), 2);
    }

    [SkippableFact]
    public void Venus_cloud_layer_is_about_one_atmosphere_of_hot_CO2()
    {
        var g = TestData.RequireGame();
        var venus = Body(g.Index, "Venus");

        var at50 = venus.SampleAt(50);
        output.WriteLine($"Venus @50km: {at50.PressureKPa:0.0} kPa, {at50.DensityKgPerM3:0.000} kg/m³, {at50.TempK:0} K");

        Assert.InRange(at50.PressureKPa, 80, 120);          // the 48-52 km band, roughly sea-level pressure
        Assert.InRange(at50.TempK, 250, 320);               // and the famously survivable temperature
        Assert.InRange(at50.DensityKgPerM3, 1, 3);          // denser than Earth air, being CO2
        Assert.Equal("CO2", at50.Present.First().Key);      // overwhelmingly carbon dioxide
        Assert.True(at50.IsAtmosphere);
    }

    [SkippableFact]
    public void The_surface_is_far_worse_than_the_clouds()
    {
        var g = TestData.RequireGame();
        var venus = Body(g.Index, "Venus");

        Assert.True(venus.SampleAt(0).PressureKPa > venus.SampleAt(50).PressureKPa * 10);
        Assert.True(venus.SampleAt(0).TempK > 700);
    }

    [SkippableFact]
    public void Above_the_top_band_there_is_nothing()
    {
        var g = TestData.RequireGame();
        var venus = Body(g.Index, "Venus");

        var vacuum = venus.SampleAt(venus.MaxAltitudeKm + 1000);
        Assert.Equal(0, vacuum.PressureKPa);
        Assert.Equal(0, vacuum.DensityKgPerM3);
        Assert.False(vacuum.IsAtmosphere);
        Assert.Equal(AtmosphereSample.Void.TempK, vacuum.TempK, 5);
    }

    [SkippableFact]
    public void Pressure_falls_monotonically_with_altitude()
    {
        var g = TestData.RequireGame();
        var venus = Body(g.Index, "Venus");

        var last = double.MaxValue;
        for (var km = 0.0; km <= venus.MaxAltitudeKm; km += 5)
        {
            var p = venus.SampleAt(km).PressureKPa;
            Assert.True(p <= last + 1e-9, $"pressure rose at {km} km: {p} after {last}");
            last = p;
        }
    }

    // ---- measuring a design ----

    private static (ShipDocument Doc, ShipGrid Grid) Ship(Catalog catalog, params (string Def, int X, int Y)[] parts)
    {
        var doc = new ShipDocument(catalog);
        foreach (var (def, x, y) in parts)
            new PlaceCommand(new Placement { DefName = def, X = x, Y = y, IsGiven = true }).Do(doc);
        return (doc, ShipGrid.FromDocument(doc, catalog));
    }

    [SkippableFact]
    public void A_bare_hull_has_no_aero_and_no_rotors()
    {
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.ContainsKey(Wall)) return;

        var (doc, grid) = Ship(g.Catalog, (Wall, 0, 0), (Wall, 1, 0));
        var p = FlightDynamics.Measure(doc, grid, g.Catalog);

        Assert.Equal(1, p.AeroCoefficient);      // the game's base value, with nothing added
        Assert.Equal(0, p.AeroParts);
        Assert.False(p.HasRotors);
        Assert.False(p.HasAero);
        Assert.Contains(p.Notes, n => n.Contains("No rotor thrust"));
        Assert.Contains(p.Notes, n => n.Contains("No aerodynamic hull"));
    }

    [SkippableFact]
    public void Aero_hull_raises_the_coefficient_by_its_StatAeroLift()
    {
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.ContainsKey(AeroWall)) return;

        var lift = g.Catalog.ByDefName[AeroWall].StartingCondValues["StatAeroLift"];
        var (doc, grid) = Ship(g.Catalog, (AeroWall, 0, 0), (AeroWall, 1, 0), (AeroWall, 2, 0));
        var p = FlightDynamics.Measure(doc, grid, g.Catalog);

        Assert.Equal(1 + 3 * lift, p.AeroCoefficient, 6);
        Assert.Equal(3, p.AeroParts);
        Assert.True(p.HasAero);
    }

    [SkippableFact]
    public void A_rotor_contributes_thirty_times_its_rated_thrust()
    {
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.ContainsKey(Rotor)) return;

        var def = g.Catalog.ByDefName[Rotor].StartingCondValues;
        var (doc, grid) = Ship(g.Catalog, (Rotor, 0, 0));
        var p = FlightDynamics.Measure(doc, grid, g.Catalog);

        Assert.Equal(1, p.RotorsPresent);
        Assert.Equal(1, p.RotorsActive);
        Assert.Equal(def["StatThrustStrength"] * 30, p.RotorThrust, 6);
        Assert.Equal(def["StatThrustStrengthTurbo"] * 30, p.RotorThrustTurbo, 6);
        Assert.True(p.RotorThrustTurbo > p.RotorThrust);
        Assert.DoesNotContain(p.Notes, n => n.Contains("No rotor thrust"));
    }

    [SkippableFact]
    public void Aero_hull_cuts_frontal_drag_but_never_side_drag()
    {
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.ContainsKey(AeroWall) || !g.Catalog.ByDefName.ContainsKey(Wall)) return;

        var plain = FlightDynamics.Measure(
            Ship(g.Catalog, (Wall, 0, 0)).Doc, Ship(g.Catalog, (Wall, 0, 0)).Grid, g.Catalog);

        // enough aero hull to clear the game's max(1, aero / 100) divisor several times over
        var winged = Ship(g.Catalog, Enumerable.Range(0, 6).Select(i => (AeroWall, i, 0)).ToArray());
        var w = FlightDynamics.Measure(winged.Doc, winged.Grid, g.Catalog);

        Assert.True(w.AeroCoefficient / 100 > 1);
        Assert.True(w.DragAreaFront < w.DragAreaSide);
        Assert.Equal(plain.DragAreaFront, plain.DragAreaSide, 6);   // under 100 aero, the divisor is 1
    }

    // ---- flying it ----

    private static FlightPoint Fly(FlightProfile profile, CelestialBody body, double altitudeKm, double airspeed,
        double aoa = 0, double attitude = 0, double rcs = 0)
    {
        var air = body.SampleAt(altitudeKm);
        return new FlightPoint(profile, body.GravityAt(altitudeKm), air.DensityKgPerM3, air.PressureKPa, air.TempK,
            airspeed, aoa, attitude, rcs);
    }

    [SkippableFact]
    public void Rotors_give_nothing_in_vacuum_and_most_in_thick_air()
    {
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.ContainsKey(Rotor)) return;

        var (doc, grid) = Ship(g.Catalog, (Rotor, 0, 0));
        var p = FlightDynamics.Measure(doc, grid, g.Catalog);
        var venus = Body(g.Index, "Venus");

        var high = Fly(p, venus, venus.MaxAltitudeKm + 500, 0);
        Assert.Equal(0, high.RotorEfficiency);
        Assert.Equal(0, high.RotorThrustNewtons);

        var deep = Fly(p, venus, 0, 0);                     // ~9 MPa at the surface
        Assert.Equal(FlightDynamics.RotorEfficiencyMax, deep.RotorEfficiency);

        var clouds = Fly(p, venus, 50, 0);
        Assert.InRange(clouds.RotorEfficiency, 0.8, 1.5);
        output.WriteLine($"one rotor @Venus 50km: {clouds.RotorThrustNewtons / 1000:0} kN, "
            + $"{clouds.InG(clouds.RotorAccel):0.00} G on {p.Mass:0} kg");
    }

    [SkippableFact]
    public void Lift_falls_off_as_the_square_of_mass()
    {
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.ContainsKey(AeroWall)) return;

        var (doc, grid) = Ship(g.Catalog, (AeroWall, 0, 0));
        var p = FlightDynamics.Measure(doc, grid, g.Catalog);
        var venus = Body(g.Index, "Venus");

        var light = Fly(p.WithMass(10_000), venus, 50, 100);
        var heavy = Fly(p.WithMass(20_000), venus, 50, 100);

        // the game divides by mass twice, so doubling the mass quarters the lift
        Assert.Equal(light.LiftAccelRaw / 4, heavy.LiftAccelRaw, 9);
    }

    [SkippableFact]
    public void Lift_is_capped_at_ten_local_gravities()
    {
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.ContainsKey(AeroWall)) return;

        var (doc, grid) = Ship(g.Catalog, (AeroWall, 0, 0));
        var p = FlightDynamics.Measure(doc, grid, g.Catalog).WithMass(200);   // absurdly light and very winged
        var venus = Body(g.Index, "Venus");

        var fast = Fly(p, venus, 50, 400);
        Assert.True(fast.LiftCapped);
        Assert.Equal(fast.Gravity * 10, fast.LiftAccel, 6);
    }

    [SkippableFact]
    public void Lift_dies_broadside_and_nose_up()
    {
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.ContainsKey(AeroWall)) return;

        var (doc, grid) = Ship(g.Catalog, (AeroWall, 0, 0));
        var p = FlightDynamics.Measure(doc, grid, g.Catalog).WithMass(50_000);
        var venus = Body(g.Index, "Venus");

        var level = Fly(p, venus, 50, 120).LiftAccelRaw;
        Assert.True(level > 0);
        // cos(90°) is not exactly zero — the game's degrees-to-radians literal is a float approximation, so 90°
        // lands about 6e-9 off the right angle. Lift collapses to a millionth of the level-flight figure rather
        // than to a hard zero, which is the same as gone.
        Assert.True(Fly(p, venus, 50, 120, aoa: 90).LiftAccelRaw < level * 1e-6);
        Assert.True(Fly(p, venus, 50, 120, attitude: 90).LiftAccelRaw < level * 1e-6);
    }

    [SkippableFact]
    public void Drag_is_worst_broadside()
    {
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.ContainsKey(AeroWall)) return;

        var winged = Ship(g.Catalog, Enumerable.Range(0, 6).Select(i => (AeroWall, i, 0)).ToArray());
        var p = FlightDynamics.Measure(winged.Doc, winged.Grid, g.Catalog).WithMass(50_000);
        var venus = Body(g.Index, "Venus");

        var nose = Fly(p, venus, 50, 150);
        var side = Fly(p, venus, 50, 150, aoa: 90);
        var tail = Fly(p, venus, 50, 150, aoa: 180);

        Assert.True(side.DragAccel > nose.DragAccel);
        Assert.Equal(nose.DragAccel, tail.DragAccel, 6);   // sin(180°) = 0, so tail-on reads as nose-on
        Assert.Equal(0, Fly(p, venus, 50, 0).DragAccel);   // stationary in the air: no drag
    }

    [SkippableFact]
    public void Hover_airspeed_is_the_speed_at_which_lift_matches_gravity()
    {
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.ContainsKey(AeroWall)) return;

        var winged = Ship(g.Catalog, Enumerable.Range(0, 8).Select(i => (AeroWall, i, 0)).ToArray());
        var p = FlightDynamics.Measure(winged.Doc, winged.Grid, g.Catalog).WithMass(30_000);
        var venus = Body(g.Index, "Venus");

        var v = Fly(p, venus, 50, 0).HoverAirspeed;
        Assert.NotNull(v);
        output.WriteLine($"hover airspeed: {v:0} m/s");

        // flown at exactly that speed, lift equals local gravity
        var atHover = Fly(p, venus, 50, v!.Value);
        Assert.Equal(atHover.Gravity, atHover.LiftAccelRaw, 6);
    }

    [SkippableFact]
    public void In_vacuum_nothing_holds_the_ship_up()
    {
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.ContainsKey(AeroWall) || !g.Catalog.ByDefName.ContainsKey(Rotor)) return;

        var (doc, grid) = Ship(g.Catalog, (Rotor, 0, 0), (AeroWall, 4, 0));
        var p = FlightDynamics.Measure(doc, grid, g.Catalog);
        var venus = Body(g.Index, "Venus");

        var space = Fly(p, venus, venus.MaxAltitudeKm + 1000, 300);
        Assert.False(space.InAtmosphere);
        Assert.Equal(0, space.LiftAccel);
        Assert.Equal(0, space.DragAccel);
        Assert.Equal(0, space.RotorAccel);
        Assert.False(space.Holds);
    }

    [SkippableFact]
    public void RCS_counts_towards_holding_altitude()
    {
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.ContainsKey(Wall)) return;

        var (doc, grid) = Ship(g.Catalog, (Wall, 0, 0));
        var p = FlightDynamics.Measure(doc, grid, g.Catalog).WithMass(10_000);
        var venus = Body(g.Index, "Venus");

        var unpowered = Fly(p, venus, 50, 0);
        var withRcs = Fly(p, venus, 50, 0, rcs: 10_000 * venus.GravityAt(50));

        Assert.False(unpowered.Holds);
        Assert.True(withRcs.Holds);
        Assert.Equal(1, withRcs.SupportRatio, 6);
    }
}
