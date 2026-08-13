using System.IO;
using System.Text.Json.Nodes;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// What an import brings in besides structure: container contents and items lying loose on the deck. Needs real
/// defs (the structure/loose split is the game's own <c>IsInstalled</c>), so these no-op without the install.
/// </summary>
public class ImportOptionsTests
{
    private const string Wall = "ItmWall1x1";      // installed structure
    private const string Crate = "ItmCrate01";     // a container, and not installed: it sits on the deck
    private const string Scrap = "ItmScrapSteel";  // loose cargo

    /// <summary>A ship with a wall, a crate on the deck, and a piece of scrap inside the crate.</summary>
    private const string ShipJsonText = """
        [{
          "strName": "Probe", "nCols": 6, "nRows": 6,
          "vShipPos": { "x": 0.0, "y": 0.0 },
          "aItems": [
            { "strName": "ItmWall1x1",    "fX": 0.0, "fY": 0.0, "fRotation": 0.0, "strID": "wall" },
            { "strName": "ItmCrate01",    "fX": 2.0, "fY": 0.0, "fRotation": 0.0, "strID": "crate" },
            { "strName": "ItmScrapSteel", "fX": 2.0, "fY": 0.0, "fRotation": 0.0, "strID": "inside", "strParentID": "crate" }
          ],
          "aCOs": [ { "strID": "inside", "strCODef": "ItmScrapSteel", "inventoryX": 1, "inventoryY": 2 } ]
        }]
        """;

    private static ImportResult Import(Catalog catalog, ImportOptions options)
    {
        var tmpl = ShipTemplate.ParseFile(ShipJsonText).Single();
        return TemplateImport.Build(tmpl, catalog, retainOrigin: false, options, ShipJson.Largest(ShipJsonText));
    }

    private static bool Ready(Catalog c) =>
        c.Lookup(Wall) is not null && c.Lookup(Crate) is not null && c.Lookup(Scrap) is not null;

    [SkippableFact]
    public void Everything_brings_in_contents_and_deck_items()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(Ready(g.Catalog), "this install lacks one of the probe defs");

        var r = Import(g.Catalog, ImportOptions.Everything);

        Assert.Single(r.Doc.Placements);                       // only the wall is structure
        Assert.Equal(Wall, r.Doc.Placements[0].DefName);
        Assert.Equal(Crate, Assert.Single(r.Doc.LooseObjects).DefName);   // the crate sits on the deck
        Assert.Equal(1, r.LooseKept);
        Assert.Equal(0, r.LooseDropped);
    }

    [SkippableFact]
    public void Layout_only_leaves_both_behind_and_says_so()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(Ready(g.Catalog), "this install lacks one of the probe defs");

        var r = Import(g.Catalog, ImportOptions.LayoutOnly);

        Assert.Single(r.Doc.Placements);
        Assert.Empty(r.Doc.LooseObjects);
        Assert.Equal(1, r.LooseDropped);
        Assert.Equal(1, r.ContainedDropped);
        Assert.Equal(0, r.ContainedKept);
    }

    [SkippableFact]
    public void A_deck_item_is_never_structure()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(Ready(g.Catalog), "this install lacks one of the probe defs");

        // The bug this fixes: a crate, a shirt or a piece of scrap used to import as a grid placement, which made
        // it a part — subject to the placement law and counted in the bill of materials.
        foreach (var options in new[] { ImportOptions.Everything, ImportOptions.LayoutOnly })
        {
            var r = Import(g.Catalog, options);
            Assert.DoesNotContain(r.Doc.Placements, p => p.DefName == Crate);
            Assert.Equal(0, BillOfMaterials.ComputeAll(r.Doc).TotalParts - 1);   // the wall, and nothing else
        }
    }

    [SkippableFact]
    public void Contents_ride_on_the_container_that_holds_them()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(Ready(g.Catalog), "this install lacks one of the probe defs");

        // The crate is a deck item, so put the contents on a container that IS structure to check the linkage.
        var text = ShipJsonText.Replace("\"ItmCrate01\"", "\"ItmRack1x101\"");
        Skip.IfNot(g.Catalog.Lookup("ItmRack1x101")?.StartingConds.Contains("IsInstalled") == true,
            "no installed locker in this install");

        var tmpl = ShipTemplate.ParseFile(text).Single();
        var r = TemplateImport.Build(tmpl, g.Catalog, retainOrigin: false, ImportOptions.Everything, ShipJson.Largest(text));

        var locker = r.Doc.Placements.Single(p => p.DefName == "ItmRack1x101");
        var held = Assert.Single(locker.Cargo);
        Assert.Equal(Scrap, held.DefName);
        Assert.Equal(1, held.GridX);                       // the grid position off the CO record
        Assert.Equal(2, held.GridY);
        Assert.Equal(1, r.ContainedKept);
        Assert.Equal(0, r.ContainedDropped);

        // no save behind this import, so the contents are the design's own and must persist in the .oplan
        Assert.True(r.Doc.IsCargoEdited(locker));
        Assert.True(held.Authored);
    }

    [SkippableFact]
    public void Imported_contents_survive_a_round_trip_through_the_oplan()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(g.Catalog.Lookup("ItmRack1x101")?.StartingConds.Contains("IsInstalled") == true,
            "no installed locker in this install");

        var text = ShipJsonText.Replace("\"ItmCrate01\"", "\"ItmRack1x101\"");
        var tmpl = ShipTemplate.ParseFile(text).Single();
        var r = TemplateImport.Build(tmpl, g.Catalog, retainOrigin: false, ImportOptions.Everything, ShipJson.Largest(text));

        var (rebuilt, _) = OplanFile.FromDocument(r.Doc, g.Index, new OplanMeta()).ToDocument(g.Catalog);

        var locker = rebuilt.Placements.Single(p => p.DefName == "ItmRack1x101");
        Assert.Equal(Scrap, Assert.Single(locker.Cargo).DefName);
    }

    [SkippableFact]
    public void A_ship_with_no_cargo_reports_nothing_either_way()
    {
        var g = TestData.RequireGame();
        if (g.Catalog.Lookup(Wall) is null) return;

        const string bare = """
            [{ "strName": "Bare", "nCols": 2, "nRows": 2, "vShipPos": { "x": 0.0, "y": 0.0 },
               "aItems": [ { "strName": "ItmWall1x1", "fX": 0.0, "fY": 0.0, "fRotation": 0.0, "strID": "w" } ] }]
            """;
        var tmpl = ShipTemplate.ParseFile(bare).Single();
        var r = TemplateImport.Build(tmpl, g.Catalog, retainOrigin: false, ImportOptions.Everything, ShipJson.Largest(bare));

        Assert.Equal(0, r.ContainedKept);
        Assert.Equal(0, r.ContainedDropped);
        Assert.Equal(0, r.LooseKept);
        Assert.Equal(0, r.LooseDropped);
    }

    [SkippableFact]
    public void The_save_edit_path_keeps_deck_items_as_placements_so_a_write_back_stays_lossless()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(Ready(g.Catalog), "this install lacks one of the probe defs");

        // Only a Placement carries an OriginStrID. If a deck item became a loose object here, the save's own item
        // would survive untouched (nothing marks it deleted) while a fresh copy was written beside it, so every
        // deck item would double on each round trip. SaveEditInjectTests pins the invariant; this pins the reason.
        var tmpl = ShipTemplate.ParseFile(ShipJsonText).Single();
        var r = TemplateImport.Build(tmpl, g.Catalog, retainOrigin: true, ImportOptions.Everything);

        Assert.Empty(r.Doc.LooseObjects);
        var crate = r.Doc.Placements.Single(p => p.DefName == Crate);
        Assert.Equal("crate", crate.OriginStrID);
    }

    [SkippableFact]
    public void A_deck_stack_imports_at_its_full_count()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(g.Catalog.Lookup(Wall) is not null && g.Catalog.Lookup(Scrap) is not null,
            "this install lacks a probe def");

        // A stack persists as a head plus same-def members parented to it. It used to import as ONE loose object
        // with the members reported "left behind" — and export 1 where the save had 3.
        const string text = """
            [{
              "strName": "Pile", "nCols": 4, "nRows": 4, "vShipPos": { "x": 0.0, "y": 0.0 },
              "aItems": [
                { "strName": "ItmWall1x1",    "fX": 0.0, "fY": 0.0, "fRotation": 0.0, "strID": "wall" },
                { "strName": "ItmScrapSteel", "fX": 2.0, "fY": 0.0, "fRotation": 0.0, "strID": "head" },
                { "strName": "ItmScrapSteel", "fX": 2.0, "fY": 0.0, "fRotation": 0.0, "strID": "m1", "strParentID": "head" },
                { "strName": "ItmScrapSteel", "fX": 2.0, "fY": 0.0, "fRotation": 0.0, "strID": "m2", "strParentID": "head" }
              ]
            }]
            """;
        var tmpl = ShipTemplate.ParseFile(text).Single();

        var kept = TemplateImport.Build(tmpl, g.Catalog, retainOrigin: false, ImportOptions.Everything, ShipJson.Largest(text));
        var pile = Assert.Single(kept.Doc.LooseObjects);
        Assert.Equal(3, pile.Quantity);
        Assert.Equal(3, kept.LooseKept);
        Assert.Equal(0, kept.ContainedDropped);   // the members are the stack, not cargo left behind

        var dropped = TemplateImport.Build(tmpl, g.Catalog, retainOrigin: false, ImportOptions.LayoutOnly, null);
        Assert.Equal(3, dropped.LooseDropped);
        Assert.Equal(0, dropped.ContainedDropped);
    }

    [SkippableFact]
    public void Crew_carried_gear_is_counted_as_crew_not_as_fetchable()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(g.Catalog.Lookup(Wall) is not null && g.Catalog.Lookup(Scrap) is not null,
            "this install lacks a probe def");

        // gear on a crew member parents to the crew CO's strID, which is not one of the ship's items — no import
        // option can ever fetch it, and the report must not point at the "Container contents" checkbox for it
        const string text = """
            [{
              "strName": "Crewed", "nCols": 4, "nRows": 4, "vShipPos": { "x": 0.0, "y": 0.0 },
              "aItems": [
                { "strName": "ItmWall1x1",    "fX": 0.0, "fY": 0.0, "fRotation": 0.0, "strID": "wall" },
                { "strName": "ItmScrapSteel", "fX": 0.0, "fY": 0.0, "fRotation": 0.0, "strID": "carried", "strSlotParentID": "some-crew-co" }
              ]
            }]
            """;
        var tmpl = ShipTemplate.ParseFile(text).Single();
        var r = TemplateImport.Build(tmpl, g.Catalog, retainOrigin: false, ImportOptions.Everything, ShipJson.Largest(text));

        Assert.Equal(1, r.ContainedDropped);
        Assert.Equal(1, r.CrewDropped);
        Assert.Equal(0, r.DeckDropped);
    }

    [SkippableFact]
    public void Deck_container_contents_are_counted_as_deck_not_as_fetchable()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(Ready(g.Catalog), "this install lacks one of the probe defs");

        // the crate sits on the deck, so its scrap can't come in even with both options on — the report words
        // that as the limitation it is, not as advice to turn on a checkbox that was already on
        var r = Import(g.Catalog, ImportOptions.Everything);

        Assert.Equal(1, r.ContainedDropped);
        Assert.Equal(1, r.DeckDropped);
        Assert.Equal(0, r.CrewDropped);
    }

    [SkippableFact]
    public void A_ship_file_with_a_trailing_comma_still_imports_its_cargo()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(g.Catalog.Lookup(Wall) is not null && g.Catalog.Lookup(Scrap) is not null
            && g.Catalog.Lookup("ItmRack1x101")?.StartingConds.Contains("IsInstalled") == true,
            "this install lacks a probe def");

        // hand-edited modded ships carry trailing commas; the template parser allows them, and the raw-JSON parse
        // behind container contents must allow the same or the structure imports while the cargo drops unexplained
        const string text = """
            [{
              "strName": "Handmade", "nCols": 4, "nRows": 4, "vShipPos": { "x": 0.0, "y": 0.0 },
              "aItems": [
                { "strName": "ItmRack1x101",  "fX": 0.0, "fY": 0.0, "fRotation": 0.0, "strID": "rack" },
                { "strName": "ItmScrapSteel", "fX": 0.0, "fY": 0.0, "fRotation": 0.0, "strID": "inside", "strParentID": "rack" },
              ],
            }]
            """;
        var tmpl = ShipTemplate.ParseFile(text).Single();
        var r = TemplateImport.Build(tmpl, g.Catalog, retainOrigin: false, ImportOptions.Everything, ShipJson.Largest(text));

        Assert.Equal(1, r.ContainedKept);
        Assert.Equal(0, r.ContainedDropped);
    }

    [SkippableFact]
    public void The_for_editing_tallies_report_what_was_actually_attached()
    {
        var g = TestData.RequireGame();

        // Build runs before the save-edit context hangs cargo on the placements, so its raw tallies call every
        // contained item dropped; the import must settle them from what was attached, or the report claims the
        // ship's whole inventory was left behind (the bug this pins).
        SaveEditImportResult? imp = null;
        foreach (var save in SaveImport.ListSaves(g.Env))
        {
            try { imp = SaveEditImport.ImportForEditing(save, g.Catalog); }
            catch { continue; }
            if (imp.Doc.Placements.Any(p => p.Cargo.Count > 0)) break;
        }
        Skip.If(imp is null, "no importable player-ship save on this machine");

        var attached = imp!.Doc.Placements.Sum(p => p.Cargo.Sum(c => c.SubtreeCount));
        Assert.Equal(attached, imp.Import.ContainedKept);
        Assert.Equal(0, imp.Import.LooseKept);     // that path never reclassifies deck items
        Assert.Equal(0, imp.Import.LooseDropped);
    }

    [SkippableFact]
    public void A_real_ship_template_imports_the_cargo_it_ships_with()
    {
        var g = TestData.RequireGame();
        var babak = TemplateImport.ListShipFiles(g.Index).FirstOrDefault(s => s.Name == "Babak");
        Skip.If(babak is null, "no Babak template in this install");

        var withCargo = TemplateImport.LoadFile(babak!.Path, g.Catalog, ImportOptions.Everything);
        var without = TemplateImport.LoadFile(babak.Path, g.Catalog, ImportOptions.LayoutOnly);

        // whatever the ship carries, the two paths must disagree only about the cargo, never about the structure
        Assert.Equal(without.Doc.Placements.Count, withCargo.Doc.Placements.Count);
        Assert.Equal(without.ContainedKept + without.ContainedDropped,
                     withCargo.ContainedKept + withCargo.ContainedDropped);
        Assert.Equal(0, withCargo.ContainedDropped);
        Assert.Equal(0, without.ContainedKept);
    }
}
