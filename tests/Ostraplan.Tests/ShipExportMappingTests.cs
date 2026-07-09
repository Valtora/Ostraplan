using System.Text.Json;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// <see cref="ShipExport.Build"/> and its serializers, game-free: the JSON shape the game's loader requires
/// (a top-level array — the recent <c>mod_info.json</c> bug), the coordinate/rotation inverse (an exported part
/// must load back onto its own tile), and the nav-console module injection. Real-corpus export parity stays in
/// the game-gated <c>ShipExportTests</c>.
/// </summary>
public class ShipExportMappingTests
{
    private static readonly IReadOnlyList<RoomSpecDef> NoSpecs = [];

    [Fact]
    public void Mod_info_serializes_as_a_one_element_top_level_array()
    {
        // The game's DataHandler wants an ARRAY like every core data file; a bare object parses to an empty
        // collection and logs "Missing mod_info.json" + "Error loading file". Guard for that exact regression.
        var json = ShipExport.SerializeModInfo(new ModInfo { StrName = "My Ship", StrAuthor = "me" });

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(1, doc.RootElement.GetArrayLength());
        Assert.Equal("My Ship", doc.RootElement[0].GetProperty("strName").GetString());
    }

    [Fact]
    public void Ship_serializes_as_a_one_element_top_level_array()
    {
        var json = ShipExport.Serialize(new ExportedShip { StrName = "My Ship" });

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(1, doc.RootElement.GetArrayLength());
        Assert.Equal("My Ship", doc.RootElement[0].GetProperty("strName").GetString());
    }

    [Fact]
    public void Export_anchors_at_the_origin_the_coordinate_inverse_assumes()
    {
        var fx = new Fixtures().Floor("Floor");
        var cat = fx.Build();
        var doc = Fixtures.Doc(cat, Fixtures.P("Floor", 0, 0));

        var (ship, _, _) = ShipExport.Build(doc, cat, NoSpecs, "T");

        Assert.Equal(0, ship.VShipPos.X);
        Assert.Equal(0, ship.VShipPos.Y);
        Assert.Equal("Floor", Assert.Single(ship.AItems).StrName);
    }

    [Fact]
    public void Every_exported_part_loads_back_onto_its_own_tile_and_rotation()
    {
        var fx = new Fixtures().Floor("Floor").Fixture("Box", 2, 1);
        var cat = fx.Build();
        var doc = Fixtures.Doc(cat,
            Fixtures.P("Floor", 0, 0), Fixtures.P("Floor", 1, 0), Fixtures.P("Box", 4, 4, 90));
        var grid = ShipGrid.FromDocument(doc, cat);

        var (ship, _, _) = ShipExport.Build(doc, cat, NoSpecs, "T");

        // no cargo/nav here, so exported items line up 1:1 with grid parts, in order
        Assert.Equal(grid.Parts.Count, ship.AItems.Length);
        for (var i = 0; i < grid.Parts.Count; i++)
        {
            var gp = grid.Parts[i];
            var it = ship.AItems[i];
            var def = cat.ByDefName[it.StrName];
            var recovered = ShipGrid.TemplateTile(it.FX, it.FY, it.FRotation, def.Item.Width, def.Item.Height, 0, 0);
            Assert.Equal((gp.TopLeftCol, gp.TopLeftRow, gp.Rot), recovered);
        }
    }

    [Fact]
    public void An_empty_nav_console_gets_the_standard_module_set_parented_to_it()
    {
        var fx = new Fixtures().Part("Nav", startingConds: ["IsNavStation"]);
        var cat = fx.Build();
        var doc = Fixtures.Doc(cat, Fixtures.P("Nav", 0, 0));

        var (ship, _, _) = ShipExport.Build(doc, cat, NoSpecs, "T");

        var console = Assert.Single(ship.AItems, i => i.StrName == "Nav");
        var modules = ship.AItems.Where(i => i.StrParentID == console.StrID).ToList();
        Assert.Equal(NavConsole.StandardModules.Count, modules.Count);
        Assert.All(modules, m => Assert.Contains(m.StrName, NavConsole.StandardModules));
        Assert.All(modules, m => Assert.False(string.IsNullOrEmpty(m.StrID)));   // each module has its own id
    }

    [Fact]
    public void Spawn_position_is_never_exactly_zero()
    {
        // objSS at exact (0,0) around "Sol" is Sol's own coordinate origin: the kiosk/Special-Offer/starting-ship
        // spawn path does not reposition it like template import does, so a literal (0,0) spawns inside the star.
        var fx = new Fixtures().Floor("Floor");
        var cat = fx.Build();
        var doc = Fixtures.Doc(cat, Fixtures.P("Floor", 0, 0));

        var (ship, _, _) = ShipExport.Build(doc, cat, NoSpecs, "T");

        Assert.Equal("Sol", ship.ObjSS.BoPORShip);
        Assert.False(ship.ObjSS.VPosx == 0 && ship.ObjSS.VPosy == 0);
    }

    [Fact]
    public void Metadata_flows_through_when_provided()
    {
        var fx = new Fixtures().Floor("Floor");
        var cat = fx.Build();
        var doc = Fixtures.Doc(cat, Fixtures.P("Floor", 0, 0));
        var meta = new ExportMetadata("Vagabond+", "Ryokka", "TS-20b", "2079", "Salvage Tug", "A hodgepodge of parts.");

        var (ship, _, _) = ShipExport.Build(doc, cat, NoSpecs, "T", meta: meta);

        Assert.Equal("Vagabond+", ship.PublicName);
        Assert.Equal("Ryokka", ship.Make);
        Assert.Equal("TS-20b", ship.Model);
        Assert.Equal("2079", ship.Year);
        Assert.Equal("Salvage Tug", ship.Designation);
        Assert.Equal("A hodgepodge of parts.", ship.Description);
    }

    [Theory]
    [InlineData("")]
    [InlineData("$TEMPLATE")]
    public void PublicName_falls_back_to_ship_name_when_blank_or_TEMPLATE(string publicName)
    {
        // The game only keeps a custom publicName when the on-disk value isn't null/""/"$TEMPLATE" (verified
        // against decompiled Ship.InitShip) — otherwise it re-rolls a random name on every spawn. Guard the same
        // way at export time so a blank/mistaken "$TEMPLATE" dialog value can't silently reintroduce that bug.
        var fx = new Fixtures().Floor("Floor");
        var cat = fx.Build();
        var doc = Fixtures.Doc(cat, Fixtures.P("Floor", 0, 0));
        var meta = new ExportMetadata(publicName);

        var (ship, _, _) = ShipExport.Build(doc, cat, NoSpecs, "T", meta: meta);

        Assert.Equal("T", ship.PublicName);
    }

    [Fact]
    public void Metadata_defaults_to_blank_when_omitted()
    {
        var fx = new Fixtures().Floor("Floor");
        var cat = fx.Build();
        var doc = Fixtures.Doc(cat, Fixtures.P("Floor", 0, 0));

        var (ship, _, _) = ShipExport.Build(doc, cat, NoSpecs, "T");

        Assert.Equal("T", ship.PublicName);   // falls back to the ship name, not "$TEMPLATE"
        Assert.Equal("", ship.Make);
        Assert.Equal("", ship.Model);
    }
}
