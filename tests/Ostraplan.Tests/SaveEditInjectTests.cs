using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using Ostraplan.Core;
using Xunit;
using Xunit.Abstractions;

namespace Ostraplan.Tests;

/// <summary>
/// Save-edit Phase 2: the inject engine (<see cref="SaveEdit"/>) rebuilds the ship record from the diff and
/// writes it to a <b>copy</b> of the save. These run against the live install's newest player ship and write
/// only to a throwaway temp folder — the real saves are read, never written. No-op when there's no install.
/// </summary>
public class SaveEditInjectTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    private static SaveEditImportResult? FirstImport(GameEnv env, Catalog catalog)
    {
        foreach (var save in SaveImport.ListSaves(env))
        {
            try { return SaveEditImport.ImportForEditing(save, catalog); }
            catch { /* not a player-ship save */ }
        }
        return null;
    }

    private static int Count(JsonNode ship, string prop) => ((JsonArray)ship[prop]!).Count;

    private static string ReadShipEntry(string zipPath, string regId)
    {
        using var z = ZipFile.OpenRead(zipPath);
        using var r = new StreamReader(z.GetEntry($"ships/{regId}.json")!.Open());
        return r.ReadToEnd();
    }

    [Fact]
    public void Noop_inject_preserves_every_item_and_co()
    {
        if (TestData.Game is not { } g) return;
        if (FirstImport(g.Env, g.Catalog) is not { } r) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);

        var origItems = Count(r.Context.ShipRecord, "aItems");
        var origCOs = Count(r.Context.ShipRecord, "aCOs");

        var (ship, report) = SaveEdit.BuildInjectedShip(r.Doc, r.Context, g.Catalog, specs);

        Assert.Equal(origItems, Count(ship, "aItems"));   // nothing added or dropped
        Assert.Equal(origCOs, Count(ship, "aCOs"));
        Assert.Equal(0, report.Added);
        Assert.Equal(0, report.Moved);
        Assert.Equal(0, report.Deleted);
        Assert.Empty(report.CargoDropped);
        Assert.False(report.GridGrew);
        _out.WriteLine($"no-op: {report.Kept} kept, {origItems} items, {origCOs} COs, {report.NCols}x{report.NRows}, {report.Warnings.Count} warnings");
    }

    [Fact]
    public void Adding_a_part_adds_one_item_and_a_synthesized_pristine_co()
    {
        if (TestData.Game is not { } g) return;
        if (FirstImport(g.Env, g.Catalog) is not { } r) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);
        if (r.Doc.Bounds() is not { } b) return;

        var items0 = Count(r.Context.ShipRecord, "aItems");
        var cos0 = Count(r.Context.ShipRecord, "aCOs");
        var def = r.Doc.Placements[0].DefName;
        // place inside the existing bounds so the grid doesn't blow up
        new PlaceCommand(new Placement { DefName = def, X = b.MinX, Y = b.MinY }).Do(r.Doc);

        var (ship, report) = SaveEdit.BuildInjectedShip(r.Doc, r.Context, g.Catalog, specs);

        Assert.Equal(1, report.Added);
        Assert.Equal(items0 + 1, Count(ship, "aItems"));   // one new item...
        Assert.Equal(cos0 + 1, Count(ship, "aCOs"));       // ...AND a matching CO (a save load skips item-without-CO)

        // the synthesized CO is 1:1 with the new item by strID, def-typed, with DEFAULT conds (pristine on load)
        var newItem = ((JsonArray)ship["aItems"]!).Last()!.AsObject();
        var newId = (string?)newItem["strID"];
        var newCo = ((JsonArray)ship["aCOs"]!).Select(n => n!.AsObject()).First(c => (string?)c["strID"] == newId);
        Assert.Equal(def, (string?)newCo["strCODef"]);
        Assert.Contains("DEFAULT", ((JsonArray)newCo["aConds"]!).Select(n => (string?)n));
    }

    [Fact]
    public void Reskinning_a_part_gives_the_new_skin_a_co_so_it_is_not_skipped_on_load()
    {
        // regression for the catastrophic in-game failure: re-skinning drops identity (def change = new part),
        // so every re-skinned tile needs a synthesized CO or the game logs "missing save data" and skips it.
        if (TestData.Game is not { } g) return;
        if (FirstImport(g.Env, g.Catalog) is not { } r) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);

        var target = r.Doc.Placements.FirstOrDefault(p =>
            !r.Doc.IsLocked(p) && ReplaceOps.CommonClass(r.Doc, [p]) is { W: 1, H: 1 });
        if (target is null) return;
        var cls = ReplaceOps.CommonClass(r.Doc, [target])!.Value;
        if (ReplaceOps.CompatibleTargets(g.Catalog, cls).FirstOrDefault(d => d.DefName != target.DefName) is not { } other) return;

        var cos0 = Count(r.Context.ShipRecord, "aCOs");
        var swap = ReplaceOps.BuildSwap(r.Doc, [target], other.DefName);
        Assert.NotNull(swap);
        swap!.Value.Cmd.Do(r.Doc);

        var (ship, report) = SaveEdit.BuildInjectedShip(r.Doc, r.Context, g.Catalog, specs);

        Assert.Equal(1, report.Added);     // the re-skinned tile is a new part...
        Assert.Equal(1, report.Deleted);   // ...and its original is deleted
        Assert.Equal(cos0, Count(ship, "aCOs"));   // -1 original CO, +1 synthesized -> net unchanged
        Assert.Contains(((JsonArray)ship["aCOs"]!).Select(n => n!.AsObject()), c => (string?)c["strCODef"] == other.DefName);
    }

    [Fact]
    public void Rearranging_imported_structure_does_not_resurrect_construction_flags()
    {
        // the reported false positives: the game never re-validates existing structure, but moving a part used
        // to clear its given-ness, so rearranging an imported ship re-ran CheckFit on game-legal-but-not-
        // constructibly-ordered stacks (fixtures through walls, conduits on walls) and flagged "tile occupied".
        if (TestData.Game is not { } g) return;
        if (FirstImport(g.Env, g.Catalog) is not { } r) return;

        int Blocking() => ProblemScan.Scan(r.Doc, g.Catalog).Count(p => p.Severity == ProblemSeverity.Blocking);
        var before = Blocking();

        // "move" every imported part onto its own tile — zero displacement, but exercises the move path
        foreach (var p in r.Doc.Placements.Where(p => !r.Doc.IsLocked(p)).ToList())
            new MoveCommand([p], 0, 0).Do(r.Doc);

        Assert.Equal(before, Blocking());   // rearranging existing structure must not resurrect construction-time flags
    }

    [Fact]
    public void Moving_a_part_keeps_its_exact_condition_and_condition_owner()
    {
        // answers "does moving a wall keep it in the same condition?": a moved part keeps its strID, its item
        // entry (incl. aCondOverrides = wear/damage) verbatim bar the pose, and its whole CO (all live state).
        if (TestData.Game is not { } g) return;
        if (FirstImport(g.Env, g.Catalog) is not { } r) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);

        // prefer a part carrying wear/damage (aCondOverrides) so "condition preserved" is a real assertion
        var target = r.Doc.Placements.FirstOrDefault(p => !r.Doc.IsLocked(p)
                         && p.OriginStrID is { } id && r.Context.ItemsById[id]["aCondOverrides"] is JsonArray)
                     ?? r.Doc.Placements.FirstOrDefault(p => !r.Doc.IsLocked(p));
        if (target?.OriginStrID is not { } oid) return;

        var origItem = r.Context.ItemsById[oid].AsObject();
        var origOverrides = origItem["aCondOverrides"]?.ToJsonString();
        var origCoConds = r.Context.CosById[oid].AsObject()["aConds"]?.ToJsonString();
        var origFx = origItem["fX"]!.GetValue<double>();

        new MoveCommand([target], 3, 0).Do(r.Doc);
        var (ship, report) = SaveEdit.BuildInjectedShip(r.Doc, r.Context, g.Catalog, specs);

        Assert.Equal(1, report.Moved);
        // SAME item (strID survives), its condition overrides intact, only the pose changed
        var moved = ((JsonArray)ship["aItems"]!).Select(n => n!.AsObject()).Single(it => (string?)it["strID"] == oid);
        Assert.Equal(origOverrides, moved["aCondOverrides"]?.ToJsonString());
        Assert.NotEqual(origFx, moved["fX"]!.GetValue<double>());
        // and its condition owner (wear, power, gas, inventory, door state) is kept verbatim
        var co = ((JsonArray)ship["aCOs"]!).Select(n => n!.AsObject()).Single(c => (string?)c["strID"] == oid);
        Assert.Equal(origCoConds, co["aConds"]?.ToJsonString());
    }

    [Fact]
    public void Deleting_a_cargoless_part_drops_its_item_and_co()
    {
        if (TestData.Game is not { } g) return;
        if (FirstImport(g.Env, g.Catalog) is not { } r) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);

        var target = r.Doc.Placements.FirstOrDefault(p =>
            !r.Doc.IsLocked(p) && p.OriginStrID is { } id && r.Context.Origins[id].CargoIds.Count == 0);
        if (target is null) return;

        var items0 = Count(r.Context.ShipRecord, "aItems");
        var cos0 = Count(r.Context.ShipRecord, "aCOs");
        new RemoveCommand([target]).Do(r.Doc);

        var (ship, report) = SaveEdit.BuildInjectedShip(r.Doc, r.Context, g.Catalog, specs);

        Assert.Equal(1, report.Deleted);
        Assert.Empty(report.CargoDropped);
        Assert.Equal(items0 - 1, Count(ship, "aItems"));   // item gone...
        Assert.Equal(cos0 - 1, Count(ship, "aCOs"));       // ...and its 1:1 condition owner with it
    }

    [Fact]
    public void Injected_copy_has_a_co_for_every_item_after_reskin_and_add()
    {
        // The reported catastrophe, at the file level: re-skin a batch of tiles (def change -> new parts) and
        // add one, then confirm the WRITTEN save has a CO for every item — the invariant DataHandler.SpawnItems
        // enforces (an item without a CO is skipped on load).
        if (TestData.Game is not { } g) return;
        if (FirstImport(g.Env, g.Catalog) is not { } r) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);
        if (r.Doc.Bounds() is not { } b) return;

        var toReskin = r.Doc.Placements
            .Where(p => !r.Doc.IsLocked(p) && ReplaceOps.CommonClass(r.Doc, [p]) is { W: 1, H: 1 })
            .Take(20).ToList();
        foreach (var p in toReskin)
        {
            var cls = ReplaceOps.CommonClass(r.Doc, [p])!.Value;
            if (ReplaceOps.CompatibleTargets(g.Catalog, cls).FirstOrDefault(d => d.DefName != p.DefName) is { } alt)
                ReplaceOps.BuildSwap(r.Doc, [p], alt.DefName)?.Cmd.Do(r.Doc);
        }
        new PlaceCommand(new Placement { DefName = r.Doc.Placements[0].DefName, X = b.MinX, Y = b.MinY }).Do(r.Doc);

        var temp = Path.Combine(Path.GetTempPath(), $"ostraplan_inv_{Guid.NewGuid():N}");
        try
        {
            var (outDir, report) = SaveEdit.Inject(r.Doc, r.Context, g.Catalog, specs, temp, overwrite: true);
            Assert.True(report.Added >= 1);

            var name = Path.GetFileName(outDir);
            var top = JsonNode.Parse(ReadShipEntry(Path.Combine(outDir, name + ".zip"), r.Context.Source.RegId));
            var ship = top is JsonArray a
                ? a.OfType<JsonObject>().OrderByDescending(o => (o["aItems"] as JsonArray)?.Count ?? 0).First()
                : top!.AsObject();
            var coIds = ((JsonArray)ship["aCOs"]!).Select(c => (string?)c!["strID"]).Where(s => s is not null).ToHashSet();
            var orphans = ((JsonArray)ship["aItems"]!).Select(i => (string?)i!["strID"])
                .Where(id => id is not null && !coIds.Contains(id)).ToList();
            Assert.Empty(orphans);   // no item may lack a CO, or the game skips it on load
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public void WriteCopy_makes_a_loadable_copy_and_leaves_the_original_untouched()
    {
        if (TestData.Game is not { } g) return;
        if (FirstImport(g.Env, g.Catalog) is not { } r) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);

        var originalBefore = ReadShipEntry(r.Context.ZipPath, r.Context.Source.RegId);
        var temp = Path.Combine(Path.GetTempPath(), $"ostraplan_inject_{Guid.NewGuid():N}");
        try
        {
            var (outDir, _) = SaveEdit.Inject(r.Doc, r.Context, g.Catalog, specs, outputSaveDir: temp, overwrite: true);
            var name = Path.GetFileName(outDir);

            // the copy is a well-formed save: <folder>/<folder>.zip + a saveInfo naming the copy
            Assert.True(File.Exists(Path.Combine(outDir, name + ".zip")), "zip renamed to match the folder");
            Assert.Contains(name, File.ReadAllText(Path.Combine(outDir, "saveInfo.json")));

            // its spliced ship record parses and — this was a no-op inject — round-trips every item
            var injected = ReadShipEntry(Path.Combine(outDir, name + ".zip"), r.Context.Source.RegId);
            var shipNode = ShipTemplate.ParseFile(injected).OrderByDescending(s => s.Items.Count).FirstOrDefault();
            Assert.NotNull(shipNode);
            Assert.Equal(Count(r.Context.ShipRecord, "aItems"), shipNode!.Items.Count);

            // the ORIGINAL save is byte-for-byte what it was
            Assert.Equal(originalBefore, ReadShipEntry(r.Context.ZipPath, r.Context.Source.RegId));
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); }
    }
}
