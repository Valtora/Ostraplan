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
    public void RoomValueOf_is_parts_times_pristine_times_modifier_and_zero_for_void()
    {
        var item = new ItemDef("X", "x.png", false, null, 0, 1, [], [], []);
        var part = new ResolvedPart("X", "X", item, [], new Dictionary<string, double> { ["StatBasePrice"] = 200 },
            new Dictionary<string, (double, double)>());
        var placed = new PlacedPart(part, null, 0, 0, 0, 0, 0, 0);
        var mods = new Dictionary<string, double> { ["Wellness"] = 1.9 };

        var wellness = new RoomModel { RoomSpec = "Wellness" };
        wellness.Parts.Add(placed);
        Assert.Equal(200 * ShipValue.PristineMarkup * 1.9, ShipValue.RoomValueOf(wellness, mods));

        var blank = new RoomModel { RoomSpec = "Blank" };   // unknown/blank spec → modifier 1.0
        blank.Parts.Add(placed);
        Assert.Equal(200 * ShipValue.PristineMarkup, ShipValue.RoomValueOf(blank, mods));

        var voided = new RoomModel { Void = true, RoomSpec = "Wellness" };
        voided.Parts.Add(placed);
        Assert.Equal(0, ShipValue.RoomValueOf(voided, mods));   // open-to-space rooms are worth nothing
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
        var expectedTotal = rooms.Rooms.Sum(r => ShipValue.RoomValueOf(r, mods));
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
