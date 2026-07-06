using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The save-edit cost model (<see cref="EditCost"/>) and the copy-folder naming (<see cref="SaveEdit.SuggestCopyDir"/>).
/// Both are pure/file-only and need no game install.
/// </summary>
public class EditCostTests
{
    private static PartDef Priced(string name, double price) => new(
        name, name, "MISC", "core",
        new ItemDef(name, "", false, null, 0, 1, ["L"], [], []),
        null, [], [], ["StatBasePrice"],
        new Dictionary<string, double> { ["StatBasePrice"] = price },
        new Dictionary<string, (double, double)>());

    private static Catalog CatOf(params PartDef[] parts)
    {
        var byName = new Dictionary<string, PartDef>();
        foreach (var p in parts) byName[p.DefName] = p;
        return new Catalog
        {
            Parts = parts,
            ByDefName = byName,
            Loots = new Dictionary<string, LootDef>(),
            Triggers = new Dictionary<string, CondTriggerDef>(),
            Warnings = [],
        };
    }

    [Fact]
    public void Cost_is_new_full_plus_moved_half_times_multiplier()
    {
        var cat = CatOf(Priced("A", 100), Priced("B", 200), Priced("M", 50));
        var doc = new ShipDocument(cat);
        new PlaceCommand(new Placement { DefName = "A", X = 0, Y = 0 }).Do(doc);                        // new 100
        new PlaceCommand(new Placement { DefName = "B", X = 1, Y = 0 }).Do(doc);                        // new 200
        new PlaceCommand(new Placement { DefName = "M", X = 5, Y = 5, OriginStrID = "m" }).Do(doc);     // moved 50
        new PlaceCommand(new Placement { DefName = "A", X = 9, Y = 9, OriginStrID = "k" }).Do(doc);     // kept (free)

        var origins = new Dictionary<string, OriginPart>
        {
            ["m"] = new(1, 1, 0, []),   // different pose -> moved
            ["k"] = new(9, 9, 0, []),   // same pose -> kept
            ["d"] = new(2, 2, 0, []),   // gone -> deleted (free)
        };
        var diff = ShipDiff.Compute(doc, origins);

        var b = EditCost.Compute(diff, cat, 2.0);
        Assert.Equal(2, b.NewParts);
        Assert.Equal(1, b.MovedParts);
        Assert.Equal(300, b.NewValue);
        Assert.Equal(50, b.MovedValue);
        // (300 full + 50×0.5) × 2 = 650
        Assert.Equal(650, b.Total, 3);

        // multiplier scales linearly; 0× is free
        Assert.Equal(0, EditCost.Compute(diff, cat, 0).Total, 3);
        Assert.Equal(325, EditCost.Compute(diff, cat, 1.0).Total, 3);
    }

    [Fact]
    public void Unpriced_and_deleted_parts_cost_nothing()
    {
        var cat = CatOf(Priced("A", 0));   // a def with no base price
        var doc = new ShipDocument(cat);
        new PlaceCommand(new Placement { DefName = "A", X = 0, Y = 0 }).Do(doc);   // new but price 0
        var diff = ShipDiff.Compute(doc, new Dictionary<string, OriginPart> { ["d"] = new(2, 2, 0, []) });

        var b = EditCost.Compute(diff, cat, 5.0);
        Assert.Equal(1, b.NewParts);
        Assert.Equal(0, b.Total, 3);
    }

    [Fact]
    public void ShipValue_reports_exact_build_cost()
    {
        var cat = CatOf(Priced("A", 100), Priced("B", 300));
        var doc = new ShipDocument(cat);
        new PlaceCommand(new Placement { DefName = "A", X = 0, Y = 0 }).Do(doc);
        new PlaceCommand(new Placement { DefName = "B", X = 1, Y = 0 }).Do(doc);

        var e = ShipValue.Estimate(doc, cat, []);
        Assert.Equal(400, e.BuildCost, 3);          // exact: Σ StatBasePrice
        // no floors/walls form a room in this synthetic doc, so the room-based ship value is 0
        Assert.Equal(0, e.ShipValue, 3);
        Assert.Equal(0, e.SellEstimate, 3);
        Assert.Equal(0, e.BuyEstimate, 3);
    }

    [Fact]
    public void SuggestCopyDir_strips_the_tag_and_numbers_on_clash()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ostraplan_name_{System.Guid.NewGuid():N}");
        var srcName = "My Save (Ostraplan)";           // already an Ostraplan copy
        var srcDir = Path.Combine(root, srcName);
        Directory.CreateDirectory(srcDir);
        try
        {
            var ctx = new SaveShipContext
            {
                Source = new SaveSourceRef(srcName, "REG"),
                ZipPath = Path.Combine(srcDir, srcName + ".zip"),
                ShipRecord = new JsonObject(),
                Origins = new Dictionary<string, OriginPart>(),
                ItemsById = new Dictionary<string, JsonNode>(),
                CosById = new Dictionary<string, JsonNode>(),
            };

            // "My Save (Ostraplan)" -> strip -> "My Save (Ostraplan)"; that folder exists -> "My Save (Ostraplan 2)"
            var suggested = SaveEdit.SuggestCopyDir(ctx);
            Assert.Equal("My Save (Ostraplan 2)", Path.GetFileName(suggested));
            Assert.False(Directory.Exists(suggested));   // always a fresh, non-colliding name
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
