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
    public void Adding_a_part_adds_one_item_and_no_co()
    {
        if (TestData.Game is not { } g) return;
        if (FirstImport(g.Env, g.Catalog) is not { } r) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);
        if (r.Doc.Bounds() is not { } b) return;

        var items0 = Count(r.Context.ShipRecord, "aItems");
        var cos0 = Count(r.Context.ShipRecord, "aCOs");
        // place inside the existing bounds so the grid doesn't blow up
        new PlaceCommand(new Placement { DefName = r.Doc.Placements[0].DefName, X = b.MinX, Y = b.MinY }).Do(r.Doc);

        var (ship, report) = SaveEdit.BuildInjectedShip(r.Doc, r.Context, g.Catalog, specs);

        Assert.Equal(1, report.Added);
        Assert.Equal(items0 + 1, Count(ship, "aItems"));   // one new item...
        Assert.Equal(cos0, Count(ship, "aCOs"));           // ...but no fabricated CO (the game defaults it)
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
