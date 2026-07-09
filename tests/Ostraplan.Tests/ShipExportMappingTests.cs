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

    [Fact]
    public void Build_falls_back_to_the_ship_name_when_no_public_name_is_given()
    {
        // Build is the mechanical writer: an empty meta.PublicName falls back to the ship name (the caller,
        // ShipExport.Write, resolves the richer "$TEMPLATE"/replacement policy via ResolvePublicName).
        var fx = new Fixtures().Floor("Floor");
        var cat = fx.Build();
        var doc = Fixtures.Doc(cat, Fixtures.P("Floor", 0, 0));

        var (blank, _, _) = ShipExport.Build(doc, cat, NoSpecs, "T", meta: new ExportMetadata(""));
        Assert.Equal("T", blank.PublicName);

        var (given, _, _) = ShipExport.Build(doc, cat, NoSpecs, "T", meta: new ExportMetadata("Charon"));
        Assert.Equal("Charon", given.PublicName);   // a real name is written verbatim
    }

    [Theory]
    // custom, fallback, isReplace -> expected
    [InlineData("Charon", "MyShip", false, "Charon")]       // a real typed name always wins
    [InlineData("Charon", "MyShip", true, "Charon")]        // …even when replacing
    [InlineData("", "MyShip", false, "MyShip")]             // new ship, blank -> the design name (stable identity)
    [InlineData("  ", "MyShip", false, "MyShip")]           // whitespace counts as blank
    [InlineData("", "MyShip", true, "$TEMPLATE")]           // replacement, blank -> vanilla varied naming
    [InlineData("$TEMPLATE", "MyShip", false, "MyShip")]    // the literal sentinel is never a real name (new)
    [InlineData("$TEMPLATE", "MyShip", true, "$TEMPLATE")]  // …and maps to vanilla naming when replacing
    public void ResolvePublicName_covers_new_and_replacement_naming(string custom, string fallback, bool isReplace, string expected)
    {
        Assert.Equal(expected, ShipExport.ResolvePublicName(custom, fallback, isReplace));
    }

    [Theory]
    // typed mod name, ship name, replace target -> expected mod name
    [InlineData("My Mod", "MyShip", null, "My Mod")]                              // a typed name always wins
    [InlineData("My Mod", "MyShip", "Sundancer", "My Mod")]                       // …even when replacing
    [InlineData("", "MyShip", null, "MyShip")]                                    // new ship, blank -> the ship name
    [InlineData("  ", "MyShip", null, "MyShip")]                                  // whitespace counts as blank
    [InlineData("", "MyShip", "Sundancer", "Sundancer - Replaced via Ostraplan")] // replacement, blank -> distinct default
    [InlineData("", "Sundancer", "Sundancer", "Sundancer - Replaced via Ostraplan")] // even when the ship shares the name
    public void ResolveModName_defaults_a_replacement_to_a_distinct_name(string modName, string shipName, string? replaceTarget, string expected)
    {
        Assert.Equal(expected, ShipExport.ResolveModName(modName, shipName, replaceTarget));
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
