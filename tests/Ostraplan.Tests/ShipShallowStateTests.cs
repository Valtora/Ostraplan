using System.Collections.Generic;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The shallow-state block on an exported ship: what the game reads back verbatim when it spawns the template
/// without fully loading it (<c>Ship.InitShip</c>), and what the character-creation and broker spec sheets print.
///
/// <para>Every core template carries these; Ostraplan used to leave all of them at zero, which showed as
/// "Mass: 0 (kg) / RCS Count: 0" on the chargen panel and, worse, tripped the guard in <c>Ship.Maneuver</c> that
/// refuses RCS flight when either <c>fRCSCount</c> or <c>nRCSDistroCount</c> is zero.</para>
/// </summary>
public class ShipShallowStateTests
{
    private static bool Ready((GameEnv, DataIndex, Catalog)? g) =>
        g is { } gg && gg.Item3.ByDefName.ContainsKey("ItmWall1x1") && gg.Item3.ByDefName.ContainsKey("ItmFloorGrate01");

    private static ShipDocument BuildHull(Catalog catalog)
    {
        var doc = new ShipDocument(catalog);
        void Place(string def, int x, int y) =>
            new PlaceCommand(new Placement { DefName = def, X = x, Y = y }).Do(doc);

        for (var x = 0; x < 5; x++) { Place("ItmWall1x1", x, 0); Place("ItmWall1x1", x, 6); }
        for (var y = 1; y <= 5; y++)
        {
            Place("ItmWall1x1", 0, y); Place("ItmWall1x1", 4, y);
            for (var x = 1; x < 4; x++) Place("ItmFloorGrate01", x, y);
        }
        return doc;
    }

    [SkippableFact]
    public void Export_bakes_the_shallow_mass_and_propulsion_figures()
    {
        var g = TestData.RequireGame();
        if (!Ready(g)) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);
        var doc = BuildHull(g.Catalog);

        var prop = Propulsion.Estimate(doc, ShipGrid.FromDocument(doc, g.Catalog), g.Catalog);
        var (ship, _, _) = ShipExport.Build(doc, g.Catalog, specs, "Shallow Test");

        Assert.True(ship.FShallowMass > 0);   // a hull of walls and floor plates is never massless
        Assert.Equal(prop.PartsMass + prop.LooseMass, ship.FShallowMass, 6);
        Assert.Equal(prop.RcsThrust, ship.NRCSCount, 6);
        Assert.Equal(prop.RcsDistrosPresent, ship.NRCSDistroCount);
        Assert.Equal(prop.RcsReactionMass, ship.FShallowRCSRemass, 6);
        Assert.Equal(prop.RcsReactionMassMax, ship.FShallowRCSRemassMax, 6);
        Assert.Equal(prop.HasTorchFigures, ship.BFusionTorch);
        Assert.Equal(prop.PelletMax, ship.FFusionPelletMax, 6);
    }

    [SkippableFact]
    public void Shallow_mass_excludes_the_designs_planning_haul_figure()
    {
        var g = TestData.RequireGame();
        if (!Ready(g)) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);
        var doc = BuildHull(g.Catalog);

        var (bare, _, _) = ShipExport.Build(doc, g.Catalog, specs, "Shallow Test");

        // ExtraMassKg is "what I expect to haul", a planner input behind the propulsion report. The game's own
        // fShallowMass is the ship's mass alone (docked and cargo mass are added on top at read time), so telling
        // it the ship weighs a towed wreck more would under-report every acceleration the ship ever computes.
        doc.ExtraMassKg = 50_000;
        var laden = ShipExport.Build(doc, g.Catalog, specs, "Shallow Test").Ship;

        Assert.Equal(bare.FShallowMass, laden.FShallowMass, 6);
    }
}
