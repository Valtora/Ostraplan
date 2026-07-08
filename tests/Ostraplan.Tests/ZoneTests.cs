using System.IO;
using System.Text.Json.Nodes;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The zone round-trip: painted <c>aZones</c> must survive import → (edit) → export / save-edit with their tile
/// indices re-projected into whatever grid frame the parts use — the fix for zones being dropped on export and
/// silently relocated on save-edit. All game-free (a synthetic catalog + hand-built records).
/// </summary>
public class ZoneTests
{
    private static readonly IReadOnlyList<RoomSpecDef> NoSpecs = [];

    // ---- ZoneGeometry: the one place indices convert ----

    [Fact]
    public void Index_and_doc_tile_round_trip()
    {
        Assert.Equal((1, 1), ZoneGeometry.IndexToDoc(5, 4));       // 5 = col 1, row 1 in a 4-wide grid
        Assert.Equal(5, ZoneGeometry.DocToIndex(1, 1, 0, 0, 4, 4));
        Assert.Equal(4, ZoneGeometry.DocToIndex(0, 0, -1, -1, 3, 3));   // origin (-1,-1): (0,0) → grid (1,1) → 4
    }

    [Fact]
    public void DocToIndex_drops_tiles_outside_the_frame()
    {
        Assert.Equal(-1, ZoneGeometry.DocToIndex(4, 0, 0, 0, 4, 4));    // col past the right edge
        Assert.Equal(-1, ZoneGeometry.DocToIndex(-1, 0, 0, 0, 4, 4));   // col past the left edge
        Assert.Equal(-1, ZoneGeometry.DocToIndex(0, 4, 0, 0, 4, 4));    // row past the bottom edge
    }

    // ---- parse + import ----

    private const string ShipWithZone = """
    [{ "strName":"Zoned", "nCols":4, "nRows":4, "vShipPos":{"x":0,"y":0},
       "aItems":[{"strName":"Floor","fX":0,"fY":0,"fRotation":0,"strID":"i1"}],
       "aZones":[{"strName":"engineering","aTiles":[0,5],"aTileConds":["IsZoneForbid"],
                  "categoryConds":["TriggerX"],"strPersonSpec":"ZonePlayer","strTargetPSpec":"ZoneCrew",
                  "bTriggerOnOwner":true,"zoneColor":{"r":0.25,"g":0.5,"b":0.75,"a":1}}] }]
    """;

    [Fact]
    public void Parse_reads_the_aZones_array()
    {
        var tmpl = Assert.Single(ShipTemplate.ParseFile(ShipWithZone).ToList());
        var z = Assert.Single(tmpl.Zones);
        Assert.Equal("engineering", z.Name);
        Assert.Equal([0, 5], z.Tiles);
        Assert.Equal(["IsZoneForbid"], z.TileConds);
        Assert.Equal(["TriggerX"], z.CategoryConds);
        Assert.Equal("ZonePlayer", z.PersonSpec);
        Assert.Equal("ZoneCrew", z.TargetPSpec);
        Assert.True(z.TriggerOnOwner);
        Assert.Equal(new ZoneColor(0.25, 0.5, 0.75, 1), z.Color);
    }

    [Fact]
    public void Import_converts_zone_indices_to_document_tiles()
    {
        var cat = new Fixtures().Floor("Floor").Build();
        var tmpl = Assert.Single(ShipTemplate.ParseFile(ShipWithZone).ToList());

        var doc = TemplateImport.FromTemplate(tmpl, cat).Doc;

        var z = Assert.Single(doc.Zones);
        // nCols = 4: index 0 → (0,0), index 5 → (1,1)
        Assert.Equal(new HashSet<(int, int)> { (0, 0), (1, 1) }, z.Tiles);
        Assert.True(z.IsForbid);
        Assert.Equal("engineering", z.Name);
        Assert.Equal("ZoneCrew", z.TargetPSpec);
    }

    // ---- export ----

    [Fact]
    public void Export_reprojects_zone_tiles_and_drops_the_out_of_frame_ones()
    {
        // a ship on a 10-wide grid, one floor at (0,0), a zone covering (0,0) and a far tile (9,9)
        const string json = """
        [{ "strName":"Far", "nCols":10, "nRows":10, "vShipPos":{"x":0,"y":0},
           "aItems":[{"strName":"Floor","fX":0,"fY":0,"fRotation":0,"strID":"i1"}],
           "aZones":[{"strName":"z","aTiles":[0,99],"aTileConds":["IsZoneStockpile"],
                      "zoneColor":{"r":0.1,"g":0.2,"b":0.3,"a":1}}] }]
        """;
        var cat = new Fixtures().Floor("Floor").Build();
        var tmpl = Assert.Single(ShipTemplate.ParseFile(json).ToList());
        var doc = TemplateImport.FromTemplate(tmpl, cat).Doc;   // zone tiles: (0,0) and (9,9)

        var (ship, _, _) = ShipExport.Build(doc, cat, NoSpecs, "Far");

        var z = Assert.Single(ship.AZones);
        // export frames to the parts' bounds (0,0) + 1 pad → 3×3 grid: (0,0) is in, (9,9) is dropped.
        Assert.Single(z.ATiles);
        Assert.Equal("z", z.StrName);
        Assert.Equal(["IsZoneStockpile"], z.ATileConds);
        Assert.Equal(0.2, z.ZoneColor.G);
        // the surviving index decodes (against the export grid) to the floor's own cell
        var idx = z.ATiles[0];
        Assert.InRange(idx, 0, ship.NCols * ship.NRows - 1);
        Assert.Equal(ship.VShipPos.X + idx % ship.NCols, ship.AItems[0].FX);
        Assert.Equal(ship.VShipPos.Y - idx / ship.NCols, ship.AItems[0].FY);
    }

    [Fact]
    public void Export_makes_zone_names_unique()
    {
        var cat = new Fixtures().Floor("Floor").Build();
        var doc = Fixtures.Doc(cat, Fixtures.P("Floor", 0, 0));
        new CreateZoneCommand(new ShipZone { Name = "dup", TileConds = { ShipZone.CondForbid }, Tiles = { (0, 0) } }).Do(doc);
        new CreateZoneCommand(new ShipZone { Name = "dup", TileConds = { ShipZone.CondForbid }, Tiles = { (0, 0) } }).Do(doc);

        var (ship, _, _) = ShipExport.Build(doc, cat, NoSpecs, "Dups");

        Assert.Equal(2, ship.AZones.Length);
        Assert.Equal(2, ship.AZones.Select(z => z.StrName).Distinct().Count());
    }

    // ---- save-edit (the relocation fix) ----

    private static JsonObject Item(string id, string name, double fx, double fy) => new()
    {
        ["strID"] = id, ["strName"] = name, ["fX"] = fx, ["fY"] = fy, ["fRotation"] = 0.0,
    };

    private static SaveShipContext Context(JsonArray items, JsonArray cos, Dictionary<string, OriginPart> origins,
        int nCols = 6, int nRows = 6, double vx = 100, double vy = 200)
    {
        var ship = new JsonObject
        {
            ["strName"] = "Test", ["strRegID"] = "H-ABC",
            ["nCols"] = (double)nCols, ["nRows"] = (double)nRows,
            ["vShipPos"] = new JsonObject { ["x"] = vx, ["y"] = vy },
            ["aItems"] = items, ["aCOs"] = cos, ["aCrew"] = new JsonArray(),
            // a stale verbatim aZones the inject must IGNORE (rebuild from the document instead)
            ["aZones"] = new JsonArray(new JsonObject { ["strName"] = "stale", ["aTiles"] = new JsonArray(0) }),
        };
        return new SaveShipContext
        {
            Source = new SaveSourceRef("TestSave", "H-ABC"),
            ZipPath = @"C:\dummy\TestSave\TestSave.zip",
            ShipRecord = ship,
            Origins = origins,
            ItemsById = items.Select(n => n!.AsObject()).ToDictionary(o => (string)o["strID"]!, o => (JsonNode)o),
            CosById = cos.Select(n => n!.AsObject()).ToDictionary(o => (string)o["strID"]!, o => (JsonNode)o),
            Epoch = 0,
        };
    }

    private static JsonArray Zones(JsonObject ship) => (JsonArray)ship["aZones"]!;

    [Fact]
    public void SaveEdit_rebuilds_zones_from_the_document_not_the_stale_verbatim_copy()
    {
        var cat = new Fixtures().Floor("Floor").Build();
        var ctx = Context(
            new JsonArray(Item("a", "Floor", 100, 200)),
            new JsonArray(new JsonObject { ["strID"] = "a", ["strCODef"] = "Floor", ["bAlive"] = true, ["aConds"] = new JsonArray("DEFAULT") }),
            new() { ["a"] = new OriginPart(0, 0, 0, []) });
        var doc = Fixtures.Doc(cat, new Placement { DefName = "Floor", X = 0, Y = 0, OriginStrID = "a" });
        new CreateZoneCommand(new ShipZone { Name = "haul", TileConds = { ShipZone.CondHaul }, Tiles = { (0, 0) } }).Do(doc);

        var (ship, report) = SaveEdit.BuildInjectedShip(doc, ctx, cat, NoSpecs);

        Assert.False(report.GridGrew);
        var z = Assert.Single(Zones(ship));                       // the doc's one zone, not the stale two-name copy
        Assert.Equal("haul", (string)z!["strName"]!);
        Assert.Equal("H-ABC", (string)z["strRegID"]!);           // stamped to the edited ship
        var tiles = (JsonArray)z["aTiles"]!;
        Assert.Equal(0, (int)tiles.Single()!);                    // (0,0) in an unchanged 6-wide frame → index 0
    }

    [Fact]
    public void SaveEdit_reprojects_zone_tiles_when_the_grid_grows()
    {
        var cat = new Fixtures().Floor("Floor").Build();
        var ctx = Context(
            new JsonArray(Item("a", "Floor", 100, 200)),
            new JsonArray(new JsonObject { ["strID"] = "a", ["strCODef"] = "Floor", ["bAlive"] = true, ["aConds"] = new JsonArray("DEFAULT") }),
            new() { ["a"] = new OriginPart(0, 0, 0, []) });
        var doc = Fixtures.Doc(cat,
            new Placement { DefName = "Floor", X = 0, Y = 0, OriginStrID = "a" },
            new Placement { DefName = "Floor", X = -2, Y = -3 });   // NEW part up-and-left → grid grows, origin shifts
        new CreateZoneCommand(new ShipZone { Name = "haul", TileConds = { ShipZone.CondHaul }, Tiles = { (0, 0) } }).Do(doc);

        var (ship, report) = SaveEdit.BuildInjectedShip(doc, ctx, cat, NoSpecs);

        Assert.True(report.GridGrew);
        var z = Assert.Single(Zones(ship));
        var idx = (int)((JsonArray)z!["aTiles"]!).Single()!;
        // the zone tile (doc (0,0) = world (100,200)) must decode, in the NEW frame, back to that same world cell —
        // a verbatim copy (old index 0) would land somewhere else entirely.
        var nCols = (int)ship["nCols"]!;
        var vs = (JsonObject)ship["vShipPos"]!;
        Assert.Equal(100.0, (double)vs["x"]! + idx % nCols);
        Assert.Equal(200.0, (double)vs["y"]! - idx / nCols);
    }

    // ---- .oplan persistence ----

    [Fact]
    public void Oplan_round_trips_a_zone()
    {
        var cat = new Fixtures().Floor("Floor").Build();
        var file = new OplanFile
        {
            Zones =
            [
                new OplanZone
                {
                    Name = "cargo", Color = [0.2, 0.7, 0.6, 1], TileConds = ["IsZoneStockpile", "IsZoneBarter"],
                    CategoryConds = ["TriggerX"], PersonSpec = "ZonePlayer", TargetPSpec = "ZoneCaptainAndCrew",
                    TriggerOnOwner = false, Tiles = [[0, 0], [3, -2]],
                },
            ],
        };
        var path = Path.Combine(Path.GetTempPath(), "ostraplan_zone_" + Guid.NewGuid().ToString("N")[..8] + ".oplan");
        try
        {
            file.Save(path);
            var (doc, _) = OplanFile.Load(path).ToDocument(cat);

            var z = Assert.Single(doc.Zones);
            Assert.Equal("cargo", z.Name);
            Assert.Equal(new ZoneColor(0.2, 0.7, 0.6, 1), z.Color);
            Assert.True(z.IsHaul);
            Assert.True(z.IsBarter);
            Assert.Equal(["TriggerX"], z.CategoryConds);
            Assert.Equal("ZoneCaptainAndCrew", z.TargetPSpec);
            Assert.Equal(new HashSet<(int, int)> { (0, 0), (3, -2) }, z.Tiles);
        }
        finally { File.Delete(path); }
    }
}
