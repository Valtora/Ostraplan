using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ostraplan.Core;
using Xunit;
using Xunit.Abstractions;

namespace Ostraplan.Tests;

/// <summary>
/// P3 template import: importing a game ship must place every part on the same tiles the game
/// stored it at (the forward of the export mapping), resolving the many non-buildable defs a real
/// ship uses, and dropping contained cargo (layout only). Proven against the game's own templates —
/// an import must reproduce the interior compartments the game baked — and by a closed export↔import
/// loop. No-ops without the install.
/// </summary>
public class ShipImportTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    private static IEnumerable<(string File, ShipTemplate Ship)> CoreShips(GameEnv env)
    {
        var dir = Path.Combine(env.CoreDataDir, "ships");
        if (!Directory.Exists(dir)) yield break;
        foreach (var path in Directory.EnumerateFiles(dir, "*.json"))
        {
            string text;
            try { text = File.ReadAllText(path); } catch { continue; }
            foreach (var ship in ShipTemplate.ParseFile(text))
                if (ship.Rooms.Count > 0) yield return (Path.GetFileName(path), ship);
        }
    }

    private static List<int> NonVoidRoomSizes(RoomPartition p) =>
        p.Rooms.Where(r => !r.Void).Select(r => r.TileCount).OrderBy(n => n).ToList();

    [Fact]
    public void Import_reproduces_a_core_ships_interior_compartments()
    {
        if (TestData.Game is not { } g) return;
        var resolver = new PartResolver(g.Index);

        var checkedShips = 0;
        foreach (var (file, ship) in CoreShips(g.Env))
        {
            if (ParityTests.RoomExclusions.ContainsKey(file)) continue;   // known non-reproducible rooms

            // the game's own partition (the parity-proven analysis path)
            var orig = RoomBuilder.Build(ShipGrid.FromTemplate(ship, resolver, g.Catalog));
            var origInterior = NonVoidRoomSizes(orig);
            if (origInterior.Count == 0) continue;   // only assert on ships that actually have sealed compartments

            // the imported document, re-analysed
            var result = TemplateImport.FromTemplate(ship, g.Catalog);
            var imp = RoomBuilder.Build(ShipGrid.FromDocument(result.Doc, g.Catalog));

            Assert.True(result.Skipped.Count == 0,
                $"{file}: {result.Skipped.Count} core defs failed to resolve, e.g. {string.Join(", ", result.Skipped.Take(4).Select(s => s.DefName))}");
            Assert.True(origInterior.SequenceEqual(NonVoidRoomSizes(imp)),
                $"{file} ({ship.Name}): imported interior compartments {string.Join(",", NonVoidRoomSizes(imp))} != game's {string.Join(",", origInterior)}");

            if (++checkedShips >= 12) break;   // a representative sweep — enough to prove the mapping, fast
        }

        _out.WriteLine($"import faithfulness verified on {checkedShips} core ships with interior compartments");
        Assert.True(checkedShips > 0, "no core ships with interior compartments were checked");
    }

    [Fact]
    public void Export_then_import_is_identity_up_to_translation()
    {
        if (TestData.Game is not { } g || !g.Catalog.ByDefName.ContainsKey("ItmWall1x1")
            || !g.Catalog.ByDefName.ContainsKey("ItmFloorGrate01")) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);

        var doc = new ShipDocument(g.Catalog);
        void P(string d, int x, int y, int r = 0) => new PlaceCommand(new Placement { DefName = d, X = x, Y = y, Rot = r }).Do(doc);
        for (var x = 0; x < 5; x++) { P("ItmWall1x1", x, 0); P("ItmWall1x1", x, 4); }
        for (var y = 1; y < 4; y++) { P("ItmWall1x1", 0, y); P("ItmWall1x1", 4, y); for (var x = 1; x < 4; x++) P("ItmFloorGrate01", x, y); }
        if (g.Catalog.ByDefName.ContainsKey("ItmBed01Off")) P("ItmBed01Off", 6, 1, 90);

        // doc → export → parse → import → doc'
        var (ship, _, _) = ShipExport.Build(doc, g.Catalog, specs, "Loop");
        var tmpl = ShipTemplate.ParseFile(ShipExport.Serialize(ship)).Single();
        var back = TemplateImport.FromTemplate(tmpl, g.Catalog);

        // same parts at the same tiles, up to a whole-ship translation (export re-anchors at vShipPos 0)
        Assert.Empty(back.Skipped);
        Assert.Equal(0, back.ContainedDropped);
        Assert.Equal(Normalize(doc), Normalize(back.Doc));
    }

    /// <summary>Placements as (def, x−minX, y−minY, rot), sorted — translation-invariant identity.</summary>
    private static List<(string, int, int, int)> Normalize(ShipDocument doc)
    {
        var b = doc.Bounds()!.Value;
        return doc.Placements
            .Select(p => (p.DefName, p.X - b.MinX, p.Y - b.MinY, p.Rot))
            .OrderBy(t => t).ToList();
    }

    [Fact]
    public void Unresolvable_defs_are_skipped_and_reported_while_the_rest_import()
    {
        if (TestData.Game is not { } g || !g.Catalog.ByDefName.ContainsKey("ItmWall1x1")) return;

        var tmpl = new ShipTemplate
        {
            Name = "Mixed", Designation = null, NCols = 10, NRows = 10, VShipPosX = 0, VShipPosY = 0,
            Items =
            [
                new TemplateItem("ItmWall1x1", 0, 0, 0, "a"),
                new TemplateItem("ItmWall1x1", 1, 0, 0, "b"),
                new TemplateItem("ItmDefinitelyNotARealDef_XYZ", 2, 0, 0, "c"),
                new TemplateItem("ItmWall1x1", 3, 0, 0, "d", Contained: true),   // a "contained" wall — dropped, not skipped
            ],
            Rooms = [], Rating = [],
        };

        var result = TemplateImport.FromTemplate(tmpl, g.Catalog);

        Assert.Equal(2, result.PartCount);                                  // the two loose walls
        Assert.Equal(1, result.ContainedDropped);                           // the contained one
        var skip = Assert.Single(result.Skipped);
        Assert.Equal("ItmDefinitelyNotARealDef_XYZ", skip.DefName);
        Assert.Equal(1, skip.Count);
    }
}
