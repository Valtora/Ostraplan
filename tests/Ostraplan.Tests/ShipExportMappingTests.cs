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
    public void A_loose_item_loads_back_onto_the_tile_it_was_dropped_on()
    {
        // Regression (#20): a LooseObject carries DOCUMENT coords while the file is written in GRID coords, so
        // exporting one verbatim displaced every loose item by the grid origin (the bbox minus the one-tile pad).
        // Bounds deliberately start away from (1,1) so the origin is non-zero on BOTH axes — a design anchored at
        // (1,1) hides the bug, and one anchored at (0,0) hides only half of it.
        var fx = new Fixtures().Floor("Floor").Part("Junk", stackLimit: 4);
        var cat = fx.Build();
        var doc = Fixtures.Doc(cat,
            Fixtures.P("Floor", -1, 0), Fixtures.P("Floor", 0, 0), Fixtures.P("Floor", 0, 1));
        new PlaceLooseCommand(new LooseObject { DefName = "Junk", X = 0, Y = 1 }).Do(doc);
        var grid = ShipGrid.FromDocument(doc, cat);

        var (ship, _, _) = ShipExport.Build(doc, cat, NoSpecs, "T");

        var floor = Assert.Single(ship.AItems, i => i.StrName == "Floor" && i.FY == -2);   // the (0,1) floor
        var loose = Assert.Single(ship.AItems, i => i.StrName == "Junk");
        Assert.Equal((floor.FX, floor.FY), (loose.FX, loose.FY));   // sitting ON its floor, not beside it

        var def = cat.ByDefName["Junk"];
        var recovered = ShipGrid.TemplateTile(loose.FX, loose.FY, loose.FRotation, def.Item.Width, def.Item.Height, 0, 0);
        Assert.Equal((0 - (int)grid.VShipPosX, 1 - (int)grid.VShipPosY, 0), recovered);
    }

    [Fact]
    public void A_loose_stack_keeps_every_member_on_the_head_s_tile()
    {
        var fx = new Fixtures().Floor("Floor").Part("Junk", stackLimit: 4);
        var cat = fx.Build();
        var doc = Fixtures.Doc(cat, Fixtures.P("Floor", 3, 2));
        new PlaceLooseCommand(new LooseObject { DefName = "Junk", X = 3, Y = 2, Quantity = 3 }).Do(doc);

        var (ship, _, _) = ShipExport.Build(doc, cat, NoSpecs, "T");

        var floor = Assert.Single(ship.AItems, i => i.StrName == "Floor");
        var emitted = ship.AItems.Where(i => i.StrName == "Junk").ToList();
        Assert.Equal(3, emitted.Count);
        Assert.All(emitted, i => Assert.Equal((floor.FX, floor.FY), (i.FX, i.FY)));
    }

    [Fact]
    public void The_declared_grid_is_the_frame_the_game_rebuilds_around_the_top_level_items()
    {
        // Ship.UpdateTiles pads a one-tile margin (TileUtils.PadTilemap, Vector2(-1,1)) around every TOP-LEVEL
        // item as it spawns; a contained or slotted item is attached to its parent and never reaches the tilemap.
        // So the grid the game comes up with is the top-level footprint bbox plus one tile, and every baked
        // aRooms/aZones entry is a flat col + row·nCols index against THAT grid. An item written outside the
        // declared frame widens the rebuilt one and skews every stored index (rooms bind to the wrong tiles,
        // zones shift by a column per row) — which is what a document-coord loose item used to do (#20).
        var fx = new Fixtures().Floor("Floor").Fixture("Box", 2, 1).Part("Junk", stackLimit: 4);
        var cat = fx.Build();
        var doc = Fixtures.Doc(cat,
            Fixtures.P("Floor", -3, -2), Fixtures.P("Floor", -2, -2), Fixtures.P("Floor", -2, -1),
            Fixtures.P("Box", -3, -1, 90));
        new PlaceLooseCommand(new LooseObject { DefName = "Junk", X = -3, Y = -2 }).Do(doc);   // a corner tile

        var (ship, _, _) = ShipExport.Build(doc, cat, NoSpecs, "T");

        int minC = int.MaxValue, minR = int.MaxValue, maxC = int.MinValue, maxR = int.MinValue;
        foreach (var it in ship.AItems.Where(i => i.StrParentID is null && i.StrSlotParentID is null))
        {
            var def = cat.ByDefName[it.StrName];
            var (col, row, rot) = ShipGrid.TemplateTile(it.FX, it.FY, it.FRotation, def.Item.Width, def.Item.Height, 0, 0);
            var (w, h) = GridMath.Size(def.Item.Width, def.Item.Height, rot);
            minC = Math.Min(minC, col); maxC = Math.Max(maxC, col + w - 1);
            minR = Math.Min(minR, row); maxR = Math.Max(maxR, row + h - 1);
        }

        // the margin is intact on all four edges: nothing sits on or past the declared edge
        Assert.Equal((1, 1), (minC, minR));
        Assert.Equal((ship.NCols - 2, ship.NRows - 2), (maxC, maxR));
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
    public void Build_never_names_a_ship_after_the_thing_it_was_handed_as_a_strName()
    {
        // Build is the mechanical writer, and the strName it is handed is an internal key: the design's file name
        // on a mod export, the registration on a save grant. Neither is a name for a hull, and falling back to it
        // is what put "fCargoTug" on a player's nav display. Handed nothing, it writes the sentinel that asks the
        // game to name the ship; the callers resolve the richer per-destination policy via ResolvePublicName.
        var fx = new Fixtures().Floor("Floor");
        var cat = fx.Build();
        var doc = Fixtures.Doc(cat, Fixtures.P("Floor", 0, 0));

        var (blank, _, _) = ShipExport.Build(doc, cat, NoSpecs, "T", meta: new ExportMetadata(""));
        Assert.Equal(ShipExport.VariedNames, blank.PublicName);

        var (none, _, _) = ShipExport.Build(doc, cat, NoSpecs, "T");
        Assert.Equal(ShipExport.VariedNames, none.PublicName);   // no metadata at all, same answer

        var (given, _, _) = ShipExport.Build(doc, cat, NoSpecs, "T", meta: new ExportMetadata("Charon"));
        Assert.Equal("Charon", given.PublicName);   // a real name is written verbatim
    }

    [Theory]
    // custom, whenBlank -> expected
    [InlineData("Charon", "$TEMPLATE", "Charon")]           // a real typed name always wins
    [InlineData("Charon", "MyShip", "Charon")]              // …whatever blank would have meant
    [InlineData("", "$TEMPLATE", "$TEMPLATE")]              // mod export, blank -> the game's varied names
    [InlineData("  ", "$TEMPLATE", "$TEMPLATE")]            // whitespace counts as blank
    [InlineData("", "MyShip", "MyShip")]                    // save grant, blank -> the design name
    [InlineData("$TEMPLATE", "MyShip", "MyShip")]           // the literal sentinel is never a real name
    [InlineData(null, "$TEMPLATE", "$TEMPLATE")]            // and neither is nothing at all
    public void ResolvePublicName_honours_a_typed_name_and_defers_blank_to_the_caller(
        string? custom, string whenBlank, string expected)
    {
        Assert.Equal(expected, ShipExport.ResolvePublicName(custom, whenBlank));
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

        // the flavour fields go out blank, but the display name does not: blank is the game's cue to roll a random
        // name on every single load, so an unnamed ship is written as the sentinel instead
        Assert.Equal(ShipExport.VariedNames, ship.PublicName);
        Assert.Equal("", ship.Make);
        Assert.Equal("", ship.Model);
    }
}
