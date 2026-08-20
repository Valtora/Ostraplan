using System.Text.Json;
using System.Text.Json.Nodes;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The game's own object rename, read off an imported ship and written back out (see <see cref="Rename"/>). The
/// normalisation and JSON-shape tests need no install; the round trips through the catalog do.
/// </summary>
public class RenameTests
{
    private const string Rack = "ItmRack01";
    private const string Wall = "ItmWall1x1";

    // ---- normalisation ----

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("\t\n", null)]
    [InlineData("spare tools", "spare tools")]
    [InlineData("  spare tools  ", "spare tools")]
    public void An_empty_name_is_no_name(string? input, string? expected) =>
        Assert.Equal(expected, Rename.Clean(input));

    [Fact]
    public void A_very_long_name_is_cut_rather_than_stored_whole()
    {
        var cleaned = Rename.Clean(new string('x', Rename.MaxLength + 50));
        Assert.Equal(Rename.MaxLength, cleaned!.Length);
    }

    // ---- reading the game's shape ----

    [Fact]
    public void Reads_the_rename_panel_off_an_item()
    {
        // the exact shape a core ship carries (the Babak Refit's "Pressurization SB" electrical box)
        var item = JsonDocument.Parse("""
            {
              "strName": "ItmAirPump01Off",
              "aGPMSettings": [
                { "strName": "Electrical", "dictGUIPropMap": ["status", "true"] },
                { "strName": "Rename", "dictGUIPropMap": ["strName", "Pressurization SB"] }
              ]
            }
            """).RootElement;

        Assert.Equal("Pressurization SB", Rename.FromItem(item));
    }

    [Fact]
    public void An_item_with_no_rename_panel_has_no_name()
    {
        var item = JsonDocument.Parse("""
            { "strName": "ItmWall1x1", "aGPMSettings": [ { "strName": "Electrical", "dictGUIPropMap": ["status", "true"] } ] }
            """).RootElement;

        Assert.Null(Rename.FromItem(item));
        Assert.Null(Rename.FromItem(JsonDocument.Parse("""{ "strName": "ItmWall1x1" }""").RootElement));
    }

    [Fact]
    public void An_imported_name_is_carried_verbatim_not_normalised()
    {
        // The game caps neither length nor whitespace (CondOwner.Rename stores anything non-empty untouched), so a
        // name read off a ship must come back exactly — normalising here would make a no-op write-back rewrite the
        // player's own data. The 64-char cap belongs to Ostraplan's rename box alone.
        var longName = new string('x', Rename.MaxLength + 20);
        var item = JsonDocument.Parse($$"""
            { "aGPMSettings": [ { "strName": "Rename", "dictGUIPropMap": ["strName", "  padded  "] },
                                { "strName": "Rename", "dictGUIPropMap": ["strName", "{{longName}}"] } ] }
            """).RootElement;

        // last panel wins, as the game's per-key merge reads it (Ship.CreatePart)
        Assert.Equal(longName, Rename.FromItem(item));

        var padded = JsonDocument.Parse("""
            { "aGPMSettings": [ { "strName": "Rename", "dictGUIPropMap": ["strName", "  padded  "] } ] }
            """).RootElement;
        Assert.Equal("  padded  ", Rename.FromItem(padded));
    }

    [Fact]
    public void A_verbatim_name_round_trips_through_the_save_writer_unchanged()
    {
        var longName = new string('n', Rename.MaxLength + 20);
        var item = JsonNode.Parse("""{ "strName": "ItmRack01" }""")!.AsObject();

        SaveEdit.ApplyRename(item, longName);

        Assert.Equal(longName, Rename.FromItem(JsonDocument.Parse(item.ToJsonString()).RootElement));
    }

    [Fact]
    public void A_panel_with_a_non_string_name_is_ignored_rather_than_throwing()
    {
        // a malformed panel whose strName is a number must not crash the write-back
        var item = JsonNode.Parse("""
            { "aGPMSettings": [ { "strName": 7, "dictGUIPropMap": ["x", "y"] } ] }
            """)!.AsObject();

        SaveEdit.ApplyRename(item, "named");

        Assert.Equal("named", Rename.FromItem(JsonDocument.Parse(item.ToJsonString()).RootElement));
        Assert.Equal(2, item["aGPMSettings"]!.AsArray().Count);   // the malformed panel was left alone
    }

    [Fact]
    public void A_malformed_panel_is_skipped_rather_than_read_off_by_one()
    {
        // an odd-length flat map has no value for its last key; reading pairs past the end would throw or lie
        var item = JsonDocument.Parse("""
            { "aGPMSettings": [ { "strName": "Rename", "dictGUIPropMap": ["strName"] } ] }
            """).RootElement;

        Assert.Null(Rename.FromItem(item));
    }

    // ---- writing the game's shape ----

    [Fact]
    public void Applying_a_name_to_a_save_item_replaces_rather_than_appends()
    {
        // the kept/moved path: the item is the save's own and already carries the name it had in game
        var item = JsonNode.Parse("""
            {
              "strName": "ItmRack01",
              "aGPMSettings": [
                { "strName": "Electrical", "dictGUIPropMap": ["status", "true"] },
                { "strName": "Rename", "dictGUIPropMap": ["strName", "old name"] }
              ]
            }
            """)!.AsObject();

        SaveEdit.ApplyRename(item, "spare tool storage");

        var panels = item["aGPMSettings"]!.AsArray();
        Assert.Equal(2, panels.Count);                                  // not three: the old one was replaced
        Assert.Single(panels, p => (string?)p!["strName"] == Rename.Panel);
        Assert.Equal("spare tool storage", Rename.FromItem(JsonDocument.Parse(item.ToJsonString()).RootElement));
        Assert.Contains(panels, p => (string?)p!["strName"] == "Electrical");   // and the other panel survived
    }

    [Fact]
    public void Clearing_a_name_removes_the_panel_and_leaves_no_empty_array()
    {
        var item = JsonNode.Parse("""
            { "strName": "ItmRack01", "aGPMSettings": [ { "strName": "Rename", "dictGUIPropMap": ["strName", "old"] } ] }
            """)!.AsObject();

        SaveEdit.ApplyRename(item, null);

        Assert.False(item.ContainsKey("aGPMSettings"));
    }

    [Fact]
    public void Clearing_a_name_keeps_the_other_panels()
    {
        var item = JsonNode.Parse("""
            {
              "aGPMSettings": [
                { "strName": "Electrical", "dictGUIPropMap": ["status", "true"] },
                { "strName": "Rename", "dictGUIPropMap": ["strName", "old"] }
              ]
            }
            """)!.AsObject();

        SaveEdit.ApplyRename(item, "");

        var panels = item["aGPMSettings"]!.AsArray();
        Assert.Single(panels);
        Assert.Equal("Electrical", (string?)panels[0]!["strName"]);
    }

    [Fact]
    public void An_unnamed_item_is_left_untouched()
    {
        var item = JsonNode.Parse("""{ "strName": "ItmWall1x1" }""")!.AsObject();
        SaveEdit.ApplyRename(item, null);
        Assert.False(item.ContainsKey("aGPMSettings"));
    }

    // ---- against the real catalog ----

    [SkippableFact]
    public void Anything_placed_can_be_renamed_the_way_the_game_allows()
    {
        var g = TestData.RequireGame();

        Assert.False(Rename.CanRename(null));                                // an unresolved def is not a part
        Assert.True(Rename.CanRename(g.Catalog.Lookup("ItmAirPump01Off")));  // a device: has GPM panels

        // None of these is a container or carries a panel, and each was unnameable until 0.93.1 (#32). The
        // secondary airlock is the one that was reported; the rest are the same hole seen from other angles.
        foreach (var def in new[] { Wall, "ItmDockSys03Closed", Catalog.PrimaryDocksysDef, "ItmRTAO2", "ItmTable02" })
            if (g.Catalog.Lookup(def) is { } part)
                Assert.True(Rename.CanRename(part), def);

        if (g.Catalog.Lookup(Rack) is { } rack) Assert.True(Rename.CanRename(rack));  // a container
    }

    [SkippableFact]
    public void A_name_survives_a_state_change()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(g.Catalog.Lookup("ItmTransponder01Off") is not null, "no transponder in this install");

        var doc = new ShipDocument(g.Catalog);
        var p = new Placement { DefName = "ItmTransponder01Off", X = 0, Y = 0, CustomName = "port beacon" };
        new PlaceCommand(p).Do(doc);

        // switching a device on does not change what it is called
        var switched = p.Restate(g.Catalog.PowerToggle(p.DefName)!, p.Rot);
        Assert.Equal("port beacon", switched.CustomName);
    }

    [SkippableFact]
    public void A_name_round_trips_through_the_oplan()
    {
        var g = TestData.RequireGame();
        if (g.Catalog.Lookup(Wall) is null) return;

        var doc = new ShipDocument(g.Catalog);
        var named = new Placement { DefName = Wall, X = 0, Y = 0, CustomName = "spare tool storage" };
        var plain = new Placement { DefName = Wall, X = 1, Y = 0 };
        new PlaceCommand(named).Do(doc);
        new PlaceCommand(plain).Do(doc);

        var (rebuilt, _) = OplanFile.FromDocument(doc, g.Index, new OplanMeta()).ToDocument(g.Catalog);

        Assert.Equal("spare tool storage", rebuilt.Placements[0].CustomName);
        Assert.Null(rebuilt.Placements[1].CustomName);
    }

    [SkippableFact]
    public void A_name_authored_in_game_survives_an_import()
    {
        var g = TestData.RequireGame();

        // parse a ship the way an import does, with the panel the game writes
        var ship = ShipTemplate.ParseFile("""
            [{
              "strName": "TestShip", "nCols": 4, "nRows": 4,
              "vShipPos": { "x": 0.0, "y": 0.0 },
              "aItems": [
                { "strName": "ItmWall1x1", "fX": 0.0, "fY": 0.0, "fRotation": 0.0, "strID": "a",
                  "aGPMSettings": [ { "strName": "Rename", "dictGUIPropMap": ["strName", "spare reactor parts"] } ] },
                { "strName": "ItmWall1x1", "fX": 1.0, "fY": 0.0, "fRotation": 0.0, "strID": "b" }
              ]
            }]
            """).Single();

        Assert.Equal("spare reactor parts", ship.Items[0].CustomName);
        Assert.Null(ship.Items[1].CustomName);

        // and reaches the document the import builds
        var imported = TemplateImport.FromTemplate(ship, g.Catalog).Doc;
        Assert.Contains(imported.Placements, p => p.CustomName == "spare reactor parts");
    }

    [SkippableFact]
    public void A_name_on_the_primary_airlock_is_exported_like_any_other()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(g.Catalog.Lookup(Catalog.PrimaryDocksysDef) is not null, "no primary airlock in this install");

        // The one part a design locks (ShipDocument.IsLocked). The lock is about geometry: it cannot be moved or
        // deleted, but the game renames it like anything else, so a name given here has to travel (#32).
        var doc = new ShipDocument(g.Catalog);
        var port = new Placement { DefName = Catalog.PrimaryDocksysDef, X = 0, Y = 0, CustomName = "bow airlock" };
        new PlaceCommand(port).Do(doc);
        Assert.True(doc.IsLocked(port));

        var (ship, _, _) = ShipExport.Build(doc, g.Catalog, [], "Tug");
        var panel = Assert.Single(Assert.Single(ship.AItems).AGPMSettings!, s => s.StrName == Rename.Panel);
        Assert.Equal(new object?[] { Rename.NameKey, "bow airlock" }, panel.DictGUIPropMap);
    }
}
