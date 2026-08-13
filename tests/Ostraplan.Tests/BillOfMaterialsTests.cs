using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>The bill of materials: counts of each buildable part's install kit, with non-buildable
/// structure tallied apart. Needs real defs, so these no-op without the install.</summary>
public class BillOfMaterialsTests
{
    private const string Wall = "ItmWall1x1";
    private const string Floor = "ItmFloorGrate01";

    private static Placement Place(ShipDocument doc, string def, int x, int y)
    {
        var p = new Placement { DefName = def, X = x, Y = y };
        new PlaceCommand(p).Do(doc);
        return p;
    }

    [SkippableFact]
    public void Counts_buildable_parts_by_def()
    {
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.ContainsKey(Wall) || !g.Catalog.ByDefName.ContainsKey(Floor)) return;

        var doc = new ShipDocument(g.Catalog);
        Place(doc, Wall, 0, 0);
        Place(doc, Wall, 1, 0);
        Place(doc, Wall, 2, 0);
        Place(doc, Floor, 0, 1);
        Place(doc, Floor, 1, 1);

        var bom = BillOfMaterials.ComputeAll(doc);

        Assert.Equal(2, bom.DistinctParts);
        Assert.Equal(5, bom.BuildableCount);
        Assert.Equal(0, bom.NonBuildableCount);
        Assert.Equal(3, bom.Lines.Single(l => l.DefName == Wall).Count);
        Assert.Equal(2, bom.Lines.Single(l => l.DefName == Floor).Count);
    }

    [SkippableFact]
    public void Each_line_carries_the_parts_install_kit()
    {
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.ContainsKey(Wall)) return;

        var doc = new ShipDocument(g.Catalog);
        Place(doc, Wall, 0, 0);

        var line = BillOfMaterials.ComputeAll(doc).Lines.Single();
        // the kit is the part's own install inputs — one uninstalled-wall entry
        Assert.NotEmpty(line.Kits);
        Assert.Equal(g.Catalog.ByDefName[Wall].Inputs, line.Kits);
    }

    [SkippableFact]
    public void The_primary_airlock_needs_no_kit()
    {
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.ContainsKey(Wall)) return;

        var doc = new ShipDocument(g.Catalog);
        Place(doc, Catalog.PrimaryDocksysDef, 0, 0);   // fixed airlock: no install job, no kit
        Place(doc, Wall, 5, 0);

        var bom = BillOfMaterials.ComputeAll(doc);
        Assert.Equal(1, bom.DistinctParts);                          // only the wall is buildable
        Assert.Equal(Wall, bom.Lines.Single().DefName);
        Assert.Equal(1, bom.NonBuildableCount);                      // the airlock
        Assert.Equal(2, bom.TotalParts);
    }

    [SkippableFact]
    public void Selection_scopes_the_bill()
    {
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.ContainsKey(Wall) || !g.Catalog.ByDefName.ContainsKey(Floor)) return;

        var doc = new ShipDocument(g.Catalog);
        var w1 = Place(doc, Wall, 0, 0);
        var w2 = Place(doc, Wall, 1, 0);
        Place(doc, Floor, 0, 1);

        var bom = BillOfMaterials.Compute(doc, [w1, w2]);            // just the two walls
        Assert.Equal(1, bom.DistinctParts);
        Assert.Equal(2, bom.BuildableCount);
        Assert.DoesNotContain(bom.Lines, l => l.DefName == Floor);
    }

    // ---- retrofit ----

    [SkippableFact]
    public void Retrofit_nets_each_part_type_in_both_directions()
    {
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.ContainsKey(Wall) || !g.Catalog.ByDefName.ContainsKey(Floor)) return;

        var ship = new ShipDocument(g.Catalog);          // 3 walls, 1 floor
        Place(ship, Wall, 0, 0);
        Place(ship, Wall, 1, 0);
        Place(ship, Wall, 2, 0);
        Place(ship, Floor, 0, 1);

        var design = new ShipDocument(g.Catalog);        // 1 wall, 3 floors
        Place(design, Wall, 0, 0);
        Place(design, Floor, 0, 1);
        Place(design, Floor, 1, 1);
        Place(design, Floor, 2, 1);

        var r = BillOfMaterials.Retrofit(
            BillOfMaterials.ComputeAll(ship), BillOfMaterials.ComputeAll(design), "Old Girl");

        Assert.Equal("Old Girl", r.FromShip);
        var walls = r.Lines.Single(l => l.DefName == Wall);
        Assert.Equal(3, walls.From);
        Assert.Equal(1, walls.To);
        Assert.Equal(2, walls.Recovered);                // two walls come off
        Assert.Equal(0, walls.Needed);

        var floors = r.Lines.Single(l => l.DefName == Floor);
        Assert.Equal(2, floors.Needed);                  // two floor kits to obtain
        Assert.Equal(0, floors.Recovered);

        Assert.Equal(2, r.NeededCount);
        Assert.Equal(2, r.RecoveredCount);
        Assert.Equal(1, r.AddedTypes);
        Assert.Equal(1, r.RemovedTypes);
        Assert.False(r.NoChange);
    }

    [SkippableFact]
    public void Retrofit_lists_a_part_type_present_on_only_one_side()
    {
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.ContainsKey(Wall) || !g.Catalog.ByDefName.ContainsKey(Floor)) return;

        var ship = new ShipDocument(g.Catalog);
        Place(ship, Wall, 0, 0);

        var design = new ShipDocument(g.Catalog);
        Place(design, Floor, 0, 0);

        var r = BillOfMaterials.Retrofit(
            BillOfMaterials.ComputeAll(ship), BillOfMaterials.ComputeAll(design), "Old Girl");

        Assert.Equal(2, r.Lines.Count);
        Assert.Equal(1, r.Lines.Single(l => l.DefName == Wall).Recovered);
        Assert.Equal(1, r.Lines.Single(l => l.DefName == Floor).Needed);
    }

    [SkippableFact]
    public void Retrofit_of_an_identical_layout_costs_nothing()
    {
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.ContainsKey(Wall)) return;

        var ship = new ShipDocument(g.Catalog);
        Place(ship, Wall, 0, 0);
        Place(ship, Wall, 1, 0);

        // same parts, different places: a move is labour, not material, so the bill nets to zero
        var design = new ShipDocument(g.Catalog);
        Place(design, Wall, 4, 4);
        Place(design, Wall, 5, 4);

        var r = BillOfMaterials.Retrofit(
            BillOfMaterials.ComputeAll(ship), BillOfMaterials.ComputeAll(design), "Old Girl");

        Assert.True(r.NoChange);
        Assert.Equal(1, r.UnchangedTypes);
        Assert.True(r.Lines.Single().Unchanged);
    }

    [SkippableFact]
    public void Retrofit_reports_non_buildable_structure_on_both_sides()
    {
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.ContainsKey(Wall)) return;

        var ship = new ShipDocument(g.Catalog);
        Place(ship, Catalog.PrimaryDocksysDef, 0, 0);

        var design = new ShipDocument(g.Catalog);
        Place(design, Wall, 0, 0);

        var r = BillOfMaterials.Retrofit(
            BillOfMaterials.ComputeAll(ship), BillOfMaterials.ComputeAll(design), "Old Girl");

        Assert.Equal(1, r.NonBuildableFrom);
        Assert.Equal(0, r.NonBuildableTo);
        Assert.Equal(1, r.NeededCount);        // the airlock never appears as a line — it has no kit
        Assert.Equal(Wall, r.Lines.Single().DefName);
    }
}
