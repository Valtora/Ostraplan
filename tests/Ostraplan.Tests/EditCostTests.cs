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

    /// <summary>Build a diff with two new parts (300 total) and one moved part (50).</summary>
    private static (ShipDiff Diff, Catalog Cat) AddedAndMoved()
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
        return (ShipDiff.Compute(doc, origins), cat);
    }

    [Fact]
    public void Added_and_moved_parts_are_priced_by_their_own_multipliers()
    {
        var (diff, cat) = AddedAndMoved();

        var b = EditCost.Compute(diff, cat, 2.0, 1.0);   // the defaults
        Assert.Equal(2, b.NewParts);
        Assert.Equal(1, b.MovedParts);
        Assert.Equal(300, b.NewValue);
        Assert.Equal(50, b.MovedValue);
        // 300 × 2 + 50 × 1 = 650, the same figure the old single-multiplier model gave at 2×
        Assert.Equal(650, b.Total, 3);

        // each multiplier scales its own side linearly, and only its own side
        Assert.Equal(350, EditCost.Compute(diff, cat, 1.0, 1.0).Total, 3);
        Assert.Equal(950, EditCost.Compute(diff, cat, 3.0, 1.0).Total, 3);
        Assert.Equal(750, EditCost.Compute(diff, cat, 2.0, 3.0).Total, 3);
        Assert.Equal(0, EditCost.Compute(diff, cat, 0, 0).Total, 3);
    }

    [Fact]
    public void Moving_can_be_made_free_without_making_adding_free()
    {
        // issue #19: a modular refit shifts a lot of parts without conjuring any, and shouldn't be priced
        // like a rebuild. 0× moved leaves only the added parts on the bill.
        var (diff, cat) = AddedAndMoved();

        Assert.Equal(600, EditCost.Compute(diff, cat, 2.0, 0).Total, 3);   // 300 × 2, the move is free
        Assert.Equal(50, EditCost.Compute(diff, cat, 0, 1.0).Total, 3);    // and the converse holds
    }

    [Fact]
    public void Total_rescales_a_breakdown_without_recomputing_the_diff()
    {
        // what the cost step does: cost once at 1×/1×, then follow the sliders
        var (diff, cat) = AddedAndMoved();
        var b = EditCost.Compute(diff, cat, 1.0, 1.0);

        Assert.Equal(EditCost.Compute(diff, cat, 2.0, 1.0).Total, EditCost.Total(b, 2.0, 1.0), 3);
        Assert.Equal(EditCost.Compute(diff, cat, 4.0, 0.5).Total, EditCost.Total(b, 4.0, 0.5), 3);
    }

    /// <summary>A save-imported fixture at (0,0) with a priced loose form, and the origins to diff it against.</summary>
    private static (ShipDocument Doc, Catalog Cat, Placement Part, Dictionary<string, OriginPart> Origins) Installed()
    {
        var cat = new Fixtures()
            .Part("Fix", basePrice: 400)
            .Part("FixLoose", basePrice: 400)
            .FormPair("Fix", "FixLoose")
            .Build();
        var doc = new ShipDocument(cat);
        var part = new Placement { DefName = "Fix", X = 0, Y = 0, OriginStrID = "s1" };
        new PlaceCommand(part).Do(doc);
        return (doc, cat, part, new Dictionary<string, OriginPart> { ["s1"] = new(0, 0, 0, []) });
    }

    [Fact]
    public void An_uninstalled_part_is_priced_as_a_move_not_as_new_material()
    {
        // The reported bug: uninstalling a fixture drops its save identity (the item record can't be reused under
        // a new def), so the loose form was billed at the full added-parts price while the origin vanished as a
        // free delete. The player already owns it — nothing was built.
        var (doc, cat, part, origins) = Installed();
        FormSwap.BuildSwap(doc, FormSwap.Loosenable(doc, [part]))!.Value.Cmd.Do(doc);

        var diff = ShipDiff.Compute(doc, origins);
        Assert.Equal(0, diff.NewCount);         // not new material...
        Assert.Equal(1, diff.ReformedCount);    // ...just a different state of something owned
        Assert.Equal(0, diff.DeletedCount);     // and the superseded origin isn't a deletion the user asked for
        Assert.Equal(1, diff.FreshItemCount);   // the write-back still authors a fresh item, unchanged

        var b = EditCost.Compute(diff, cat, 2.0, 1.0);
        Assert.Equal(0, b.NewParts);
        Assert.Equal(1, b.ReformedParts);
        Assert.Equal(400, b.ReformedValue, 3);
        Assert.Equal(400, b.Total, 3);          // 400 × 1.0× moved, NOT 400 × 2.0× added

        // it follows the moved slider, which is the whole point
        Assert.Equal(0, EditCost.Compute(diff, cat, 2.0, 0).Total, 3);
        Assert.Equal(800, EditCost.Compute(diff, cat, 2.0, 2.0).Total, 3);
        Assert.Equal(400, EditCost.Compute(diff, cat, 9.0, 1.0).Total, 3);   // the added slider doesn't touch it
    }

    [Fact]
    public void Uninstalling_and_re_installing_costs_nothing()
    {
        var (doc, cat, part, origins) = Installed();
        var loosened = FormSwap.BuildSwap(doc, FormSwap.Loosenable(doc, [part]))!.Value;
        loosened.Cmd.Do(doc);
        FormSwap.BuildSwap(doc, FormSwap.Installable(doc, [loosened.New[0]]))!.Value.Cmd.Do(doc);

        var diff = ShipDiff.Compute(doc, origins);
        Assert.Equal(1, diff.KeptCount);        // back to its own def at its own pose — the save item is reusable
        Assert.Equal(0, diff.ReformedCount);
        Assert.Equal(0, diff.FreshItemCount);
        Assert.Equal(0, EditCost.Compute(diff, cat, 2.0, 1.0).Total, 3);
    }

    [Fact]
    public void An_uninstalled_part_that_is_also_dragged_elsewhere_is_still_only_a_move()
    {
        // the issue's own case: parts shifted around a refit, not conjured
        var (doc, cat, part, origins) = Installed();
        var swap = FormSwap.BuildSwap(doc, FormSwap.Loosenable(doc, [part]))!.Value;
        swap.Cmd.Do(doc);
        new MoveCommand([swap.New[0]], 7, 9).Do(doc);

        var diff = ShipDiff.Compute(doc, origins);
        Assert.Equal(1, diff.ReformedCount);
        Assert.Equal(0, diff.NewCount);
        Assert.Equal(400, EditCost.Compute(diff, cat, 2.0, 1.0).Total, 3);
    }

    [Fact]
    public void A_re_skin_is_still_new_material()
    {
        // Replacing a part with a genuinely different part is not a state change, and stays on the added side.
        var (doc, cat, part, origins) = Installed();
        var replaced = ReplaceOps.BuildSwap(doc, [part], "FixLoose");
        Assert.NotNull(replaced);
        replaced!.Value.Cmd.Do(doc);

        var diff = ShipDiff.Compute(doc, origins);
        Assert.Equal(1, diff.NewCount);
        Assert.Equal(0, diff.ReformedCount);
        Assert.Equal(1, diff.DeletedCount);
        Assert.Equal(800, EditCost.Compute(diff, cat, 2.0, 1.0).Total, 3);   // 400 × 2.0× added
    }

    [Fact]
    public void Authored_cargo_counts_at_full_value_on_top_of_a_free_kept_container()
    {
        var cat = CatOf(Priced("Crate", 0), Priced("Ammo", 10));
        var doc = new ShipDocument(cat);
        var crate = new Placement { DefName = "Crate", X = 0, Y = 0, OriginStrID = "c" };
        new PlaceCommand(crate).Do(doc);
        // add three authored Ammo (non-stackable here -> three items, each full value)
        var cargo = CargoEdit.Add(crate.Cargo, null, (6, 6), cat.Lookup("Ammo")!, 3);
        Assert.NotNull(cargo);
        new SetCargoCommand(crate, crate.Cargo, cargo!).Do(doc);

        var diff = ShipDiff.Compute(doc, new Dictionary<string, OriginPart> { ["c"] = new(0, 0, 0, []) });   // kept
        var b = EditCost.Compute(diff, cat, 2.0, 1.0);

        Assert.Equal(0, b.NewParts);        // the container itself is kept (free)
        Assert.Equal(3, b.NewCargo);        // three authored items...
        Assert.Equal(30, b.CargoValue, 3);  // ...at full base value (3 × 10)
        Assert.Equal(60, b.Total, 3);       // 30 × 2× — cargo rides the added-parts multiplier

        // and the moved-parts multiplier doesn't touch it
        Assert.Equal(60, EditCost.Compute(diff, cat, 2.0, 5.0).Total, 3);

        // removing the cargo again makes it free, and originals never count
        new SetCargoCommand(crate, crate.Cargo, []).Do(doc);
        var diff2 = ShipDiff.Compute(doc, new Dictionary<string, OriginPart> { ["c"] = new(0, 0, 0, []) });
        Assert.Equal(0, EditCost.Compute(diff2, cat, 2.0, 1.0).Total, 3);
    }

    [Fact]
    public void Unpriced_and_deleted_parts_cost_nothing()
    {
        var cat = CatOf(Priced("A", 0));   // a def with no base price
        var doc = new ShipDocument(cat);
        new PlaceCommand(new Placement { DefName = "A", X = 0, Y = 0 }).Do(doc);   // new but price 0
        var diff = ShipDiff.Compute(doc, new Dictionary<string, OriginPart> { ["d"] = new(2, 2, 0, []) });

        var b = EditCost.Compute(diff, cat, 5.0, 5.0);
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

    [Fact]
    public void SuggestBackupDir_places_the_backup_in_the_saves_root_not_inside_the_save()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ostraplan_bak_{System.Guid.NewGuid():N}");
        var srcDir = Path.Combine(root, "My Save");
        Directory.CreateDirectory(srcDir);
        try
        {
            var ctx = Ctx("My Save", Path.Combine(srcDir, "My Save.zip"));
            var backup = SaveEdit.SuggestBackupDir(ctx);

            // the whole point of the change: the backup sits in the SAVES ROOT (the save's parent),
            // NOT inside the save folder — so deleting a broken save can't delete its backup.
            Assert.Equal(root, Path.GetDirectoryName(backup));
            Assert.NotEqual(srcDir, Path.GetDirectoryName(backup));
            Assert.Equal("My Save (backup)", Path.GetFileName(backup));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SuggestBackupDir_numbers_on_clash_and_never_stacks_the_tag()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ostraplan_bak2_{System.Guid.NewGuid():N}");
        var srcDir = Path.Combine(root, "My Save (backup)");   // backing up a backup shouldn't stack the tag
        Directory.CreateDirectory(srcDir);
        try
        {
            var ctx = Ctx("My Save (backup)", Path.Combine(srcDir, "My Save (backup).zip"));
            // strip -> "My Save"; "My Save (backup)" already exists (== srcDir) -> "My Save (backup 2)"
            var backup = SaveEdit.SuggestBackupDir(ctx);
            Assert.Equal("My Save (backup 2)", Path.GetFileName(backup));
            Assert.False(Directory.Exists(backup));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static SaveShipContext Ctx(string name, string zipPath) => new()
    {
        Source = new SaveSourceRef(name, "REG"),
        ZipPath = zipPath,
        ShipRecord = new JsonObject(),
        Origins = new Dictionary<string, OriginPart>(),
        ItemsById = new Dictionary<string, JsonNode>(),
        CosById = new Dictionary<string, JsonNode>(),
    };
}
