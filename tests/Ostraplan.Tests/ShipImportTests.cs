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

    [SkippableFact]
    public void Import_reproduces_a_core_ships_interior_compartments()
    {
        var g = TestData.RequireGame();
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

    [SkippableFact]
    public void Export_then_import_is_identity_up_to_translation()
    {
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.ContainsKey("ItmWall1x1")
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

    [SkippableFact]
    public void Unresolvable_defs_are_skipped_and_reported_while_the_rest_import()
    {
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.ContainsKey("ItmWall1x1")) return;

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

    [SkippableFact]
    public void Imported_structure_is_given_and_a_valid_ship_flags_no_placement_law_problems()
    {
        // A real ship stacks parts (fixtures on floors, thrusters through walls) that the game
        // built incrementally and never re-validates. Imported parts are "given" and exempt from
        // the placement-law scan, so a valid ship must surface no socket / airlock false positives.
        var g = TestData.RequireGame();

        var checkedShips = 0;
        foreach (var (file, ship) in CoreShips(g.Env))
        {
            if (ParityTests.RoomExclusions.ContainsKey(file)) continue;
            var r = TemplateImport.FromTemplate(ship, g.Catalog);
            if (r.PartCount < 50) continue;   // a real ship with real stacking

            Assert.All(r.Doc.Placements, p => Assert.True(p.IsGiven, $"{file}: an imported part isn't marked given"));

            var falsePositives = ProblemScan.Scan(r.Doc, g.Catalog)
                .Where(p => p.Title.Contains("occupied") || p.Title.Contains("blocked by") || p.Title.Contains("beyond the airlock"))
                .ToList();
            Assert.True(falsePositives.Count == 0,
                $"{file}: {falsePositives.Count} placement-law false positive(s) on a valid ship: {string.Join("; ", falsePositives.Select(p => p.Title))}");

            if (++checkedShips >= 10) break;
        }
        Assert.True(checkedShips > 0, "no core ships were checked");
    }

    [SkippableFact]
    public void System_objects_are_filtered_on_import()
    {
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.ContainsKey("ItmWall1x1")) return;
        if (g.Catalog.Lookup("SysLootSpawner") is null) return;   // needs the spawner def in this install

        var tmpl = new ShipTemplate
        {
            Name = "Sys", Designation = null, NCols = 10, NRows = 10, VShipPosX = 0, VShipPosY = 0,
            Items =
            [
                new TemplateItem("ItmWall1x1", 0, 0, 0, "a"),
                new TemplateItem("SysLootSpawner", 1, 0, 0, "b"),   // IsSystem — a runtime loot spawner, not structure
            ],
            Rooms = [], Rating = [],
        };

        var r = TemplateImport.FromTemplate(tmpl, g.Catalog);
        Assert.Equal(1, r.SystemDropped);   // the spawner
        Assert.Equal(1, r.PartCount);       // just the wall
        Assert.All(r.Doc.Placements, p => Assert.True(p.IsGiven));
    }

    [SkippableFact]
    public void Importing_then_exporting_a_core_ship_stays_a_valid_spawnable()
    {
        // The P3 acceptance path end-to-end on real data: a real ship → import → export as a mod →
        // re-parse. The exported template's baked aRooms must equal the game's recompute (no rating
        // drift on load) and preserve the ship's compartments through the round-trip.
        var g = TestData.RequireGame();
        var specs = RoomCertifier.LoadSpecs(g.Index);
        var resolver = new PartResolver(g.Index);

        var checkedShips = 0;
        foreach (var (file, ship) in CoreShips(g.Env))
        {
            if (ParityTests.RoomExclusions.ContainsKey(file)) continue;
            var import = TemplateImport.FromTemplate(ship, g.Catalog);
            var importInterior = RoomBuilder.Build(ShipGrid.FromDocument(import.Doc, g.Catalog))
                .Rooms.Count(r => !r.Void);
            if (importInterior == 0) continue;

            var (exported, _, _) = ShipExport.Build(import.Doc, g.Catalog, specs, ship.Name);
            var tmpl = Assert.Single(ShipTemplate.ParseFile(ShipExport.Serialize(exported)).ToList());
            var grid = ShipGrid.FromTemplate(tmpl, resolver, g.Catalog);
            var rooms = RoomBuilder.Build(grid);
            RoomCertifier.CertifyAll(rooms, specs, g.Catalog);

            Assert.Null(RoomParity.Compare(grid, rooms, tmpl, out _));                    // recompute == baked (no drift)
            Assert.Equal(importInterior, rooms.Rooms.Count(r => !r.Void));                // compartments survive the round-trip
            Assert.All(tmpl.Items, it => Assert.False(string.IsNullOrEmpty(it.StrID)));   // fresh instance ids

            if (++checkedShips >= 6) break;
        }
        Assert.True(checkedShips > 0, "no core ships with interior compartments were checked");
    }

    [SkippableFact]
    public void A_core_ships_empty_nav_console_is_stocked_on_import_and_survives_export()
    {
        // Real data, because this is where the game's own shape matters: a core template keeps its console's
        // modules in a SysLootSpawner (dropped on import as a system object), so every console arrives empty and
        // has to be stocked here — the same state a pre-1.0 ship arrives in. See NavConsole.
        var g = TestData.RequireGame();
        if (g.Catalog.Lookup("ItmStationNav") is not { } consoleDef || !NavConsole.IsConsole(consoleDef)) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);

        var expected = NavConsole.StandardModules.OrderBy(x => x, System.StringComparer.Ordinal).ToList();
        foreach (var entry in TemplateImport.ListShipFiles(g.Index))
        {
            ImportResult r;
            try { r = TemplateImport.LoadFile(entry.Path, g.Catalog); } catch { continue; }
            var consoles = r.Doc.Placements.Where(p => NavConsole.IsConsole(g.Catalog.Lookup(p.DefName))).ToList();
            if (consoles.Count == 0) continue;

            Assert.Equal(consoles.Count, r.NavConsolesStocked);
            Assert.Equal(consoles.Count * expected.Count, r.NavModulesInstalled);
            // the modules are the LOOSE contents; the console's slotted data chip came from the ship and stays
            Assert.All(consoles, c => Assert.Equal(
                expected, c.Cargo.Where(x => !x.Slotted).Select(x => x.DefName)
                    .OrderBy(x => x, System.StringComparer.Ordinal).ToList()));
            Assert.All(consoles, c => Assert.Contains(c.Cargo, x => x.Slotted));

            // and they reach the exported template, each parented to its own console
            var (exported, _, _) = ShipExport.Build(r.Doc, g.Catalog, specs, "NavStockTest");
            var consoleIds = exported.AItems
                .Where(i => consoles.Any(c => c.DefName == i.StrName)).Select(i => i.StrID).ToHashSet();
            var byParent = exported.AItems
                .Where(i => i.StrParentID is { } pid && consoleIds.Contains(pid))
                .GroupBy(i => i.StrParentID!);
            Assert.Equal(consoles.Count, byParent.Count());
            Assert.All(byParent, grp => Assert.Equal(
                expected, grp.Select(i => i.StrName).OrderBy(x => x, System.StringComparer.Ordinal).ToList()));

            // each console also carries the screen arrangement for what it holds: a NavModConfig panel with an
            // entry per module, empty for the two the stock layout has no room for (NavConsole.Arrange)
            foreach (var item in exported.AItems.Where(i => consoleIds.Contains(i.StrID)))
            {
                var panel = Assert.Single(item.AGPMSettings ?? [], p => p.StrName == "NavModConfig");
                var flat = panel.DictGUIPropMap.Select(x => x as string).ToList();
                Assert.Equal(expected.Count * 2, flat.Count);
                Assert.Equal(2, flat.Where((_, i) => i % 2 == 1).Count(v => v is { Length: 0 }));
            }
            return;
        }
        Assert.Fail("no ship template with a nav console was found");
    }
}
