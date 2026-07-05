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

    [Fact]
    public void Counts_buildable_parts_by_def()
    {
        if (TestData.Game is not { } g || !g.Catalog.ByDefName.ContainsKey(Wall) || !g.Catalog.ByDefName.ContainsKey(Floor)) return;

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

    [Fact]
    public void Each_line_carries_the_parts_install_kit()
    {
        if (TestData.Game is not { } g || !g.Catalog.ByDefName.ContainsKey(Wall)) return;

        var doc = new ShipDocument(g.Catalog);
        Place(doc, Wall, 0, 0);

        var line = BillOfMaterials.ComputeAll(doc).Lines.Single();
        // the kit is the part's own install inputs — one uninstalled-wall entry
        Assert.NotEmpty(line.Kits);
        Assert.Equal(g.Catalog.ByDefName[Wall].Inputs, line.Kits);
    }

    [Fact]
    public void The_primary_airlock_needs_no_kit()
    {
        if (TestData.Game is not { } g || !g.Catalog.ByDefName.ContainsKey(Wall)) return;

        var doc = new ShipDocument(g.Catalog);
        Place(doc, Catalog.PrimaryDocksysDef, 0, 0);   // fixed airlock: no install job, no kit
        Place(doc, Wall, 5, 0);

        var bom = BillOfMaterials.ComputeAll(doc);
        Assert.Equal(1, bom.DistinctParts);                          // only the wall is buildable
        Assert.Equal(Wall, bom.Lines.Single().DefName);
        Assert.Equal(1, bom.NonBuildableCount);                      // the airlock
        Assert.Equal(2, bom.TotalParts);
    }

    [Fact]
    public void Selection_scopes_the_bill()
    {
        if (TestData.Game is not { } g || !g.Catalog.ByDefName.ContainsKey(Wall) || !g.Catalog.ByDefName.ContainsKey(Floor)) return;

        var doc = new ShipDocument(g.Catalog);
        var w1 = Place(doc, Wall, 0, 0);
        var w2 = Place(doc, Wall, 1, 0);
        Place(doc, Floor, 0, 1);

        var bom = BillOfMaterials.Compute(doc, [w1, w2]);            // just the two walls
        Assert.Equal(1, bom.DistinctParts);
        Assert.Equal(2, bom.BuildableCount);
        Assert.DoesNotContain(bom.Lines, l => l.DefName == Floor);
    }
}
