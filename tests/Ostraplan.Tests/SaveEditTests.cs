using System.IO;
using System.Linq;
using Ostraplan.Core;
using Xunit;
using Xunit.Abstractions;

namespace Ostraplan.Tests;

/// <summary>
/// Save-edit Phase 1: importing the player's ship <b>for editing</b> retains its identity
/// (<see cref="Placement.OriginStrID"/>) + a <see cref="SaveShipContext"/>, and the structural
/// <see cref="ShipDiff"/> classifies edits as kept / moved / new / deleted. The classification and the
/// identity invariants are pure unit tests; the end-to-end import + diff runs against a real save (and
/// no-ops on a machine without one). <b>Nothing here writes to any save.</b>
/// </summary>
public class SaveEditTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    /// <summary>An empty catalog: defs won't resolve, but placements still hold their pose + identity —
    /// enough to exercise the pure diff/identity logic without the game.</summary>
    private static Catalog EmptyCat() => new()
    {
        Parts = [],
        ByDefName = new Dictionary<string, PartDef>(),
        Loots = new Dictionary<string, LootDef>(),
        Triggers = new Dictionary<string, CondTriggerDef>(),
        Warnings = [],
    };

    // ---- pure logic (no install needed) ----

    [Fact]
    public void Diff_classifies_kept_moved_new_deleted()
    {
        var doc = new ShipDocument(EmptyCat());
        new PlaceCommand(new Placement { DefName = "X", X = 0, Y = 0, OriginStrID = "keep" }).Do(doc);
        new PlaceCommand(new Placement { DefName = "X", X = 5, Y = 5, OriginStrID = "move" }).Do(doc);   // origin says (1,1)
        new PlaceCommand(new Placement { DefName = "X", X = 9, Y = 9 }).Do(doc);                         // no origin -> new

        var origins = new Dictionary<string, OriginPart>
        {
            ["keep"] = new(0, 0, 0, []),
            ["move"] = new(1, 1, 0, []),
            ["gone"] = new(2, 2, 0, []),   // present at import, absent now -> deleted
        };

        var diff = ShipDiff.Compute(doc, origins);
        Assert.Equal(1, diff.KeptCount);
        Assert.Equal(1, diff.MovedCount);
        Assert.Equal(1, diff.NewCount);
        Assert.Equal(1, diff.DeletedCount);
        Assert.Equal("gone", Assert.Single(diff.OfKind(PartChangeKind.Deleted)).OriginStrID);
    }

    [Fact]
    public void OriginStrID_and_given_ness_both_survive_move_and_rotate()
    {
        var doc = new ShipDocument(EmptyCat());
        var p = new Placement { DefName = "X", X = 0, Y = 0, IsGiven = true, OriginStrID = "id1" };
        new PlaceCommand(p).Do(doc);

        new MoveCommand([p], 2, 3).Do(doc);
        Assert.Equal("id1", p.OriginStrID);   // identity kept across a move...
        Assert.True(p.IsGiven);               // ...and so is given-ness (the game never re-checks existing structure)

        new RotateCommand(doc, p, 90).Do(doc);
        Assert.Equal("id1", p.OriginStrID);   // and both survive a rotate (SetPose)
        Assert.True(p.IsGiven);
    }

    [Fact]
    public void Snapshot_carries_origin()
    {
        var doc = new ShipDocument(EmptyCat());
        new PlaceCommand(new Placement { DefName = "X", X = 0, Y = 0, OriginStrID = "id1" }).Do(doc);
        Assert.Contains(doc.Snapshot().Placements, p => p.OriginStrID == "id1");
    }

    // ---- end-to-end against a real save ----

    /// <summary>The first save that yields the player's ship, imported for editing, or null.</summary>
    private static SaveEditImportResult? FirstImport(GameEnv env, Catalog catalog)
    {
        foreach (var save in SaveImport.ListSaves(env))
        {
            try { return SaveEditImport.ImportForEditing(save, catalog); }
            catch { /* not a player-ship save — keep looking */ }
        }
        return null;
    }

    [Fact]
    public void Import_for_editing_builds_full_context_with_1to1_identity()
    {
        if (TestData.Game is not { } g) return;
        if (FirstImport(g.Env, g.Catalog) is not { } r) return;

        // every placed part is tagged, and its id is an origin
        Assert.All(r.Doc.Placements, p => Assert.NotNull(p.OriginStrID));
        Assert.Equal(r.Doc.Placements.Count, r.Context.Origins.Count);
        Assert.All(r.Doc.Placements, p => Assert.True(r.Context.Origins.ContainsKey(p.OriginStrID!)));

        // 1:1 identity: every structural id has both an item entry and a CO entry
        Assert.All(r.Context.Origins.Keys, id =>
        {
            Assert.True(r.Context.ItemsById.ContainsKey(id), $"{id}: no item entry");
            Assert.True(r.Context.CosById.ContainsKey(id), $"{id}: no CO entry");
        });

        // the full context retains more than just the structural parts (contained cargo lives here too)
        Assert.True(r.Context.ItemsById.Count >= r.Context.Origins.Count);
        Assert.True(r.Context.CosById.Count >= r.Context.ItemsById.Count);   // +crew/loot-spawner COs
        Assert.NotNull(r.Doc.SourceSave);

        _out.WriteLine($"{r.Import.ShipName}: {r.Doc.Placements.Count} structural, " +
            $"{r.Context.ItemsById.Count} items, {r.Context.CosById.Count} COs, source={r.Doc.SourceSave}");
    }

    [Fact]
    public void Noop_diff_is_all_kept()
    {
        if (TestData.Game is not { } g) return;
        if (FirstImport(g.Env, g.Catalog) is not { } r) return;

        var diff = ShipDiff.Compute(r.Doc, r.Context);
        Assert.Equal(r.Doc.Placements.Count, diff.KeptCount);
        Assert.Equal(0, diff.MovedCount);
        Assert.Equal(0, diff.NewCount);
        Assert.Equal(0, diff.DeletedCount);
    }

    [Fact]
    public void Moving_one_part_yields_one_moved()
    {
        if (TestData.Game is not { } g) return;
        if (FirstImport(g.Env, g.Catalog) is not { } r) return;

        var count = r.Doc.Placements.Count;
        var target = r.Doc.Placements.First(p => !r.Doc.IsLocked(p));
        new MoveCommand([target], 1, 0).Do(r.Doc);

        var diff = ShipDiff.Compute(r.Doc, r.Context);
        Assert.Equal(1, diff.MovedCount);
        Assert.Equal(count - 1, diff.KeptCount);
        Assert.Equal(0, diff.NewCount);
        Assert.Equal(0, diff.DeletedCount);
    }

    [Fact]
    public void Deleting_one_part_yields_one_deleted()
    {
        if (TestData.Game is not { } g) return;
        if (FirstImport(g.Env, g.Catalog) is not { } r) return;

        var count = r.Doc.Placements.Count;
        var target = r.Doc.Placements.First(p => !r.Doc.IsLocked(p));
        new RemoveCommand([target]).Do(r.Doc);

        var diff = ShipDiff.Compute(r.Doc, r.Context);
        Assert.Equal(1, diff.DeletedCount);
        Assert.Equal(count - 1, diff.KeptCount);
        Assert.Equal(0, diff.MovedCount);
        Assert.Equal(0, diff.NewCount);
    }

    [Fact]
    public void Adding_one_part_yields_one_new()
    {
        if (TestData.Game is not { } g) return;
        if (FirstImport(g.Env, g.Catalog) is not { } r) return;

        var count = r.Doc.Placements.Count;
        var def = r.Doc.Placements[0].DefName;   // a def known to resolve
        new PlaceCommand(new Placement { DefName = def, X = 9999, Y = 9999 }).Do(r.Doc);   // far from the ship, no origin

        var diff = ShipDiff.Compute(r.Doc, r.Context);
        Assert.Equal(1, diff.NewCount);
        Assert.Equal(count, diff.KeptCount);
        Assert.Equal(0, diff.MovedCount);
        Assert.Equal(0, diff.DeletedCount);
    }

    [Fact]
    public void Replace_drops_origin_identity_but_keeps_given()
    {
        if (TestData.Game is not { } g || !g.Catalog.ByDefName.ContainsKey("ItmWall1x1")) return;

        var doc = new ShipDocument(g.Catalog);
        var p = new Placement { DefName = "ItmWall1x1", X = 0, Y = 0, IsGiven = true, OriginStrID = "id1" };
        new PlaceCommand(p).Do(doc);

        if (ReplaceOps.CommonClass(doc, [p]) is not { } cls) return;
        if (ReplaceOps.CompatibleTargets(g.Catalog, cls).FirstOrDefault(d => d.DefName != "ItmWall1x1") is not { } other) return;

        var swap = ReplaceOps.BuildSwap(doc, [p], other.DefName);
        Assert.NotNull(swap);
        swap!.Value.Cmd.Do(doc);

        Assert.All(swap.Value.New, np => Assert.Null(np.OriginStrID));   // def change = new part -> identity dropped
        Assert.All(swap.Value.New, np => Assert.True(np.IsGiven));       // but given-ness still inherited
    }

    [Fact]
    public void Oplan_roundtrips_origin_and_source()
    {
        if (TestData.Game is not { } g) return;
        if (g.Catalog.Parts.FirstOrDefault()?.DefName is not { } def) return;

        var doc = new ShipDocument(g.Catalog);
        new PlaceCommand(new Placement { DefName = def, X = 2, Y = 3, IsGiven = true, OriginStrID = "orig-1" }).Do(doc);
        doc.SourceSave = new SaveSourceRef("mod save", "J-P3HF");

        var path = Path.Combine(Path.GetTempPath(), $"oplan_{Guid.NewGuid():N}.oplan");
        try
        {
            OplanFile.FromDocument(doc, g.Index, new OplanMeta { Name = "roundtrip" }).Save(path);
            var (doc2, missing) = OplanFile.Load(path).ToDocument(g.Catalog);

            Assert.Empty(missing);
            var p2 = Assert.Single(doc2.Placements);
            Assert.Equal("orig-1", p2.OriginStrID);
            Assert.True(p2.IsGiven);
            Assert.Equal(new SaveSourceRef("mod save", "J-P3HF"), doc2.SourceSave);
        }
        finally { File.Delete(path); }
    }
}
