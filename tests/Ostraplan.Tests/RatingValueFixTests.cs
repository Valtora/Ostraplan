using System.Collections.Generic;
using System.Linq;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The two 0.7.0 rating/value fixes: the export now bakes the game's parts-based <c>roomValue</c> (not the
/// physical volume, which read as near-worthless at a broker), and RCS thrusters are built in their ON state so
/// the maneuver rating counts them and an exported ship flies without a manual power-on.
/// </summary>
public class RatingValueFixTests
{
    // ---- roomValue = parts value (game-free) ----

    [Fact]
    public void RoomValueOf_is_parts_times_modifier_including_void_rooms_and_no_pristine_markup()
    {
        var cat = new Fixtures().Build();
        var item = new ItemDef("X", "x.png", false, null, 0, 1, [], [], []);
        var part = new ResolvedPart("X", "X", item, [], new Dictionary<string, double> { ["StatBasePrice"] = 200 },
            new Dictionary<string, (double, double)>());
        var placed = new PlacedPart(part, null, 0, 0, 0, 0, 0, 0);
        var mods = new Dictionary<string, double> { ["Wellness"] = 1.9 };

        // NO ×1.25: IsPristine is a runtime cond only derelict break-in and trader stock ever
        // grant — a spawned/exported ship's parts never carry it, so the flat markup Ostraplan
        // used to apply overshot real resale quotes by up to 25%
        var wellness = new RoomModel { RoomSpec = "Wellness" };
        wellness.Parts.Add(placed);
        Assert.Equal(200 * 1.9, ShipValue.RoomValueOf(wellness, mods, cat));

        var blank = new RoomModel { RoomSpec = "Blank" };   // unknown/blank spec → modifier 1.0
        blank.Parts.Add(placed);
        Assert.Equal(200, ShipValue.RoomValueOf(blank, mods, cat));

        // void rooms count too — neither CalculateRoomValue nor GetShipValue filters bVoid, and
        // 192 baked core void rooms carry non-zero roomValue (engines/exterior gear); zeroing
        // them undercut any ship with parts outside sealed rooms
        var voided = new RoomModel { Void = true, RoomSpec = "Blank" };
        voided.Parts.Add(placed);
        Assert.Equal(200, ShipValue.RoomValueOf(voided, mods, cat));
    }

    [SkippableFact]
    public void Part_value_includes_the_gas_a_def_starts_with()
    {
        var g = TestData.RequireGame();
        var rta = g.Catalog.Lookup("ItmRTAO2");
        Skip.If(rta is null, "RTA def not present");

        // GetBasePrice adds GetTotalGasValue: an O2 RTA spawns full — 13,373 mol × 0.0319988 kg/mol
        // × the data-driven $13.2/kg ≈ $5,648 of O2 on top of its $410 shell
        var resolved = new ResolvedPart(rta!.DefName, rta.Friendly, rta.Item,
            rta.StartingConds, rta.StartingCondValues, rta.MapPoints);
        var value = ShipValue.PartValue(resolved, g.Catalog);

        var shell = rta.StartingCondValues.GetValueOrDefault("StatBasePrice");
        var mols = rta.StartingCondValues.GetValueOrDefault("StatGasMolO2");
        var gas = g.Catalog.GasPrices.GetValueOrDefault("O2") * 0.0319988 * mols;
        Assert.True(gas > 1000, $"expected a full can's O2 to be worth real money, got {gas:N0}");
        Assert.Equal(shell + gas, value, 3);
    }

    [SkippableFact]
    public void Export_bakes_the_parts_based_room_value_not_the_volume()
    {
        var g = TestData.RequireGame();
        var specs = RoomCertifier.LoadSpecs(g.Index);

        // a real template has fixtures in certified rooms, so its rooms carry a real (large) parts value
        var files = TemplateImport.ListShipFiles(g.Index);
        var entry = files.FirstOrDefault(f => f.Name == "Vector3") ?? files.FirstOrDefault();
        Skip.If(entry is null, "no ship templates available");
        var doc = TemplateImport.LoadFile(entry!.Path, g.Catalog).Doc;

        var (ship, _, _) = ShipExport.Build(doc, g.Catalog, specs, "Value Export");

        var grid = ShipGrid.FromDocument(doc, g.Catalog);
        var rooms = RoomBuilder.Build(grid);
        RoomCertifier.CertifyAll(rooms, specs, g.Catalog);
        var mods = specs.ToDictionary(s => s.Name, s => s.ValueModifier, System.StringComparer.Ordinal);

        var bakedTotal = ship.ARooms.Sum(r => r.RoomValue);
        var expectedTotal = rooms.Rooms.Sum(r => ShipValue.RoomValueOf(r, mods, g.Catalog));
        var volumeTotal = rooms.Rooms.Where(r => !r.Void).Sum(r => r.Volume);

        Assert.Equal(expectedTotal, bakedTotal, 3);        // export baked exactly the parts value...
        Assert.True(expectedTotal > volumeTotal,           // ...which for a real ship dwarfs the old volume figure
            $"parts value {expectedTotal:N0} should exceed the volume {volumeTotal:N2} the export used to bake");
    }

    // ---- RCS built ON (install-gated) ----

    [SkippableFact]
    public void Rcs_thrusters_are_built_in_the_on_state()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(g.Catalog.Index is not null, "needs the real installables");

        // the palette entry is the On variant (counts toward maneuver), not the as-installed Off variant
        Assert.Contains(g.Catalog.Parts, p => p.DefName == "ItmRCSCluster01");
        Assert.DoesNotContain(g.Catalog.Parts, p => p.DefName == "ItmRCSCluster01Off");

        // and the built variant actually fires the rating's RCS trigger, where the Off one is forbidden by IsOff
        Assert.True(g.Catalog.Triggers.TryGetValue(Rating.RcsTrigger, out var trig));
        var on = g.Catalog.Lookup("ItmRCSCluster01");
        var off = g.Catalog.Lookup("ItmRCSCluster01Off");
        Skip.If(on is null || off is null, "RCS defs not present in this build");
        Assert.True(CondEval.Triggered(trig!, on!.StartingConds, g.Catalog));    // On → counted
        Assert.False(CondEval.Triggered(trig!, off!.StartingConds, g.Catalog));  // Off → not counted
    }

    [SkippableTheory]
    // Non-RCS powered fixtures also build in their operational state, resolved via the game's non-uniform On naming:
    [InlineData("ItmCooler01Off", "ItmCooler01On")]        // "…On"
    [InlineData("ItmAtmoScrubber01Off", "ItmAtmoScrubber01")] // drop the suffix
    [InlineData("ItmHeater01Off", "ItmHeater01")]          // drop the suffix (base is the powered state)
    [InlineData("ItmBed01Off", "ItmBed01")]                // furniture too — the base bed, not the "off" one
    public void Powered_fixtures_are_built_in_their_on_state(string offDef, string onDef)
    {
        var g = TestData.RequireGame();
        Skip.IfNot(g.Catalog.Index is not null, "needs the real installables");
        Skip.If(g.Catalog.Lookup(offDef) is null || g.Catalog.Lookup(onDef) is null, "defs not present in this build");

        Assert.Contains(g.Catalog.Parts, p => p.DefName == onDef);          // the On/base variant is the palette entry
        Assert.DoesNotContain(g.Catalog.Parts, p => p.DefName == offDef);   // never the as-installed Off variant
        Assert.DoesNotContain("IsOff", g.Catalog.Lookup(onDef)!.StartingConds);
        Assert.Contains("IsOff", g.Catalog.Lookup(offDef)!.StartingConds);
    }

    [SkippableTheory]
    // Devices whose only On states are ambiguous (colour/alert modes, a startup sequence, an Open/Closed vent) are
    // left exactly as the game installs them, never guessed into a wrong default.
    [InlineData("ItmVent02Off")]           // Open/Closed, not On
    [InlineData("ItmTransponder01Off")]    // only "…OnR" exists (a colour state)
    public void Ambiguous_devices_are_left_as_installed(string offDef)
    {
        var g = TestData.RequireGame();
        Skip.IfNot(g.Catalog.Index is not null, "needs the real installables");
        Skip.If(g.Catalog.Lookup(offDef) is null, "def not present in this build");

        Assert.Contains(g.Catalog.Parts, p => p.DefName == offDef);   // still built as installed
    }

    [SkippableFact]
    public void A_design_with_rcs_gets_a_real_maneuver_grade_not_O()
    {
        var g = TestData.RequireGame();
        var rcs = g.Catalog.Parts.FirstOrDefault(p => p.DefName == "ItmRCSCluster01");
        Skip.If(rcs is null, "RCS thruster not in the palette");

        // one On thruster on the grid: fRCSCount > 0, so the maneuver slot is a graded value, never "O"
        var doc = new ShipDocument(g.Catalog);
        new PlaceCommand(new Placement { DefName = rcs!.DefName, X = 5, Y = 5 }).Do(doc);
        var grid = ShipGrid.FromDocument(doc, g.Catalog);
        var rooms = RoomBuilder.Build(grid);
        var rating = Rating.Calculate(grid, rooms, g.Catalog);

        Assert.NotEqual("O", rating.Maneuver);
    }
}
