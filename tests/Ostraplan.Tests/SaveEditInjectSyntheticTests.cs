using System.Text.Json.Nodes;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// <see cref="SaveEdit.BuildInjectedShip"/> exercised game-free with a hand-built <see cref="SaveShipContext"/>
/// and ship record — the write-back logic that had the most in-game E2E bugs. Covers the four diff outcomes
/// (kept verbatim / moved / new-with-synthesized-CO / deleted), the CO-per-item invariant a save load enforces,
/// nav-module injection, grid growth, cargo-loss reporting, and the atmosphere refill. No file I/O, no install.
/// The end-to-end version against a real save stays in the game-gated <c>SaveEditInjectTests</c>.
/// </summary>
public class SaveEditInjectSyntheticTests
{
    private static readonly IReadOnlyList<RoomSpecDef> NoSpecs = [];

    // ---- synthetic save-record builders ----

    private static JsonObject Item(string id, string name, double fx, double fy, double frot = 0) => new()
    {
        ["strID"] = id, ["strName"] = name, ["fX"] = fx, ["fY"] = fy, ["fRotation"] = frot,
    };

    private static JsonObject Co(string id, string def) => new()
    {
        ["strID"] = id, ["strCODef"] = def, ["bAlive"] = true, ["aConds"] = new JsonArray("DEFAULT"),
    };

    /// <summary>
    /// A synthetic source ship. The default frame (4×3, content at doc (1,1)–(2,1)) is one the GAME could
    /// actually produce: it rebuilds the tilemap as the part bounding box plus a one-tile margin, so content is
    /// always inset by exactly 1. Only the tests that assert on <see cref="InjectReport.GridReframed"/> need
    /// that fidelity — the rest ignore the frame, so their fixtures may sit anywhere and simply get reframed.
    /// </summary>
    private static SaveShipContext Context(JsonArray items, JsonArray cos, Dictionary<string, OriginPart> origins,
        int nCols = 4, int nRows = 3, double vx = 100, double vy = 200)
    {
        var ship = new JsonObject
        {
            ["strName"] = "Test", ["strRegID"] = "H-ABC",
            // stored as doubles: a real save's numbers parse as JsonElement (double-readable), whereas a
            // CLR-int JsonValue fails TryGetValue<double>, which is how SaveEdit reads nCols/nRows.
            ["nCols"] = (double)nCols, ["nRows"] = (double)nRows,
            ["vShipPos"] = new JsonObject { ["x"] = vx, ["y"] = vy },
            ["aItems"] = items, ["aCOs"] = cos, ["aCrew"] = new JsonArray(),
        };
        var itemsById = items.Select(n => n!.AsObject()).ToDictionary(o => (string)o["strID"]!, o => (JsonNode)o);
        var cosById = cos.Select(n => n!.AsObject()).ToDictionary(o => (string)o["strID"]!, o => (JsonNode)o);
        return new SaveShipContext
        {
            Source = new SaveSourceRef("TestSave", "H-ABC"),
            ZipPath = @"C:\dummy\TestSave\TestSave.zip",   // never opened by BuildInjectedShip (pure)
            ShipRecord = ship,
            Origins = origins,
            ItemsById = itemsById,
            CosById = cosById,
            Epoch = 0,
        };
    }

    private static IEnumerable<JsonObject> Items(JsonObject ship) => ((JsonArray)ship["aItems"]!).Select(n => n!.AsObject());
    private static List<string> ItemIds(JsonObject ship) => Items(ship).Select(o => (string)o["strID"]!).ToList();
    private static List<string> CoIds(JsonObject ship) => ((JsonArray)ship["aCOs"]!).Select(n => (string)n!["strID"]!).ToList();

    // ---- tests ----

    [Fact]
    public void A_no_op_edit_keeps_every_part_verbatim_and_fills_atmosphere()
    {
        // the fixture's frame is the one the game would rebuild (content inset by 1 in a 4×3 grid), so a no-op
        // edit must leave it alone — see Context
        var cat = new Fixtures().Floor("Floor").Build();
        var ctx = Context(
            new JsonArray(Item("a", "Floor", 101, 199), Item("b", "Floor", 102, 199)),
            new JsonArray(Co("a", "Floor"), Co("b", "Floor")),
            new() { ["a"] = new OriginPart(1, 1, 0, []), ["b"] = new OriginPart(2, 1, 0, []) });
        var doc = Fixtures.Doc(cat,
            new Placement { DefName = "Floor", X = 1, Y = 1, OriginStrID = "a" },
            new Placement { DefName = "Floor", X = 2, Y = 1, OriginStrID = "b" });

        var (ship, report) = SaveEdit.BuildInjectedShip(doc, ctx, cat, NoSpecs);

        Assert.Equal((2, 0, 0, 0), (report.Kept, report.Moved, report.Added, report.Deleted));
        Assert.Equal(new[] { "a", "b" }, ItemIds(ship).OrderBy(x => x).ToArray());
        Assert.Equal(102.0, (double)Items(ship).Single(i => (string)i["strID"]! == "b")["fX"]!);   // world coords verbatim
        Assert.False(report.GridReframed);
        Assert.Equal((4, 3), (report.NCols, report.NRows));   // unchanged frame

        // the CO-per-item invariant a save load enforces (or the game skips the item)
        Assert.All(ItemIds(ship), id => Assert.Contains(id, CoIds(ship)));
        // regenerated rooms leave the ship in vacuum → it must be armed to refill on load
        Assert.True(report.AtmosphereFilled);
        Assert.True((bool)ship["bPrefill"]!);
        Assert.Equal(0, (int)ship["DMGStatus"]!);
    }

    [Fact]
    public void A_new_part_gets_a_synthesized_pristine_CO()
    {
        // loading a save (unlike a template) skips any item with no CO — a new part MUST carry a fresh one
        var cat = new Fixtures().Floor("Floor").Build();
        var ctx = Context(
            new JsonArray(Item("a", "Floor", 100, 200)),
            new JsonArray(Co("a", "Floor")),
            new() { ["a"] = new OriginPart(0, 0, 0, []) });
        var doc = Fixtures.Doc(cat,
            new Placement { DefName = "Floor", X = 0, Y = 0, OriginStrID = "a" },
            new Placement { DefName = "Floor", X = 1, Y = 0 });   // new, no origin

        var (ship, report) = SaveEdit.BuildInjectedShip(doc, ctx, cat, NoSpecs);

        Assert.Equal(1, report.Added);
        var newId = ItemIds(ship).Single(id => id != "a");
        Assert.Contains(newId, CoIds(ship));   // synthesized CO present, keyed to the item
        var newCo = ((JsonArray)ship["aCOs"]!).Select(n => n!.AsObject()).Single(o => (string)o["strID"]! == newId);
        Assert.Contains("DEFAULT", ((JsonArray)newCo["aConds"]!).Select(x => (string)x!));   // repopulates def conds on load
    }

    [Fact]
    public void A_deleted_part_drops_its_item_and_its_CO()
    {
        var cat = new Fixtures().Floor("Floor").Build();
        var ctx = Context(
            new JsonArray(Item("a", "Floor", 100, 200), Item("b", "Floor", 101, 200)),
            new JsonArray(Co("a", "Floor"), Co("b", "Floor")),
            new() { ["a"] = new OriginPart(0, 0, 0, []), ["b"] = new OriginPart(1, 0, 0, []) });
        var doc = Fixtures.Doc(cat, new Placement { DefName = "Floor", X = 0, Y = 0, OriginStrID = "a" });   // b gone

        var (ship, report) = SaveEdit.BuildInjectedShip(doc, ctx, cat, NoSpecs);

        Assert.Equal(1, report.Deleted);
        Assert.DoesNotContain("b", ItemIds(ship));
        Assert.DoesNotContain("b", CoIds(ship));
        Assert.Contains("a", ItemIds(ship));
    }

    [Fact]
    public void A_moved_part_is_repositioned_in_the_ships_world_frame()
    {
        var cat = new Fixtures().Floor("Floor").Build();
        var ctx = Context(
            new JsonArray(Item("a", "Floor", 100, 200), Item("b", "Floor", 101, 200)),
            new JsonArray(Co("a", "Floor"), Co("b", "Floor")),
            new() { ["a"] = new OriginPart(0, 0, 0, []), ["b"] = new OriginPart(1, 0, 0, []) });
        var doc = Fixtures.Doc(cat,
            new Placement { DefName = "Floor", X = 0, Y = 0, OriginStrID = "a" },
            new Placement { DefName = "Floor", X = 2, Y = 0, OriginStrID = "b" });   // moved 1,0 → 2,0

        var (ship, report) = SaveEdit.BuildInjectedShip(doc, ctx, cat, NoSpecs);

        Assert.Equal(1, report.Moved);
        // doc tile 2 → world x = 2 + (1/2 − 0.5) + vShipPos.x(100) = 102
        Assert.Equal(102.0, (double)Items(ship).Single(i => (string)i["strID"]! == "b")["fX"]!);
    }

    [Fact]
    public void A_new_empty_nav_console_is_injected_with_the_standard_modules()
    {
        var cat = new Fixtures().Part("Nav", startingConds: ["IsNavStation"]).Build();
        var ctx = Context(new JsonArray(), new JsonArray(), new());   // empty original ship
        var doc = Fixtures.Doc(cat, new Placement { DefName = "Nav", X = 0, Y = 0 });   // a new console, no cargo

        var (ship, _) = SaveEdit.BuildInjectedShip(doc, ctx, cat, NoSpecs);

        var console = Items(ship).Single(i => (string)i["strName"]! == "Nav");
        var modules = Items(ship).Where(i => (string?)i["strParentID"] == (string)console["strID"]!).ToList();
        Assert.Equal(NavConsole.StandardModules.Count, modules.Count);
        // every injected module also carries its own CO (no item may load CO-less)
        Assert.All(ItemIds(ship), id => Assert.Contains(id, CoIds(ship)));
    }

    [Fact]
    public void The_grid_grows_to_fit_a_new_part_placed_outside_the_original()
    {
        var cat = new Fixtures().Floor("Floor").Build();
        var ctx = Context(
            new JsonArray(Item("a", "Floor", 100, 200)),
            new JsonArray(Co("a", "Floor")),
            new() { ["a"] = new OriginPart(0, 0, 0, []) }, nCols: 6, nRows: 6);
        var doc = Fixtures.Doc(cat,
            new Placement { DefName = "Floor", X = 0, Y = 0, OriginStrID = "a" },
            new Placement { DefName = "Floor", X = 20, Y = 20 });   // far outside the 6×6

        var (ship, report) = SaveEdit.BuildInjectedShip(doc, ctx, cat, NoSpecs);

        Assert.True(report.GridReframed);
        Assert.True(report.NCols >= 21 && report.NRows >= 21);
        Assert.Equal(report.NCols, (int)ship["nCols"]!);
        Assert.Equal(report.NRows, (int)ship["nRows"]!);
    }

    [Fact]
    public void Deleting_a_container_reports_and_drops_its_cargo()
    {
        var cat = new Fixtures().Floor("Floor").Container("Box").Build();
        var ctx = Context(
            new JsonArray(Item("a", "Floor", 100, 200), Item("b", "Box", 101, 200), Item("c1", "ItmThing", 101, 200)),
            new JsonArray(Co("a", "Floor"), Co("b", "Box"), Co("c1", "ItmThing")),
            new() { ["a"] = new OriginPart(0, 0, 0, []), ["b"] = new OriginPart(1, 0, 0, ["c1"]) });
        var doc = Fixtures.Doc(cat, new Placement { DefName = "Floor", X = 0, Y = 0, OriginStrID = "a" });   // delete the box

        var (ship, report) = SaveEdit.BuildInjectedShip(doc, ctx, cat, NoSpecs);

        Assert.Equal(1, report.Deleted);
        var loss = Assert.Single(report.CargoDropped);
        Assert.Equal("b", loss.ContainerStrID);
        Assert.Contains("ItmThing", loss.Items);
        Assert.DoesNotContain("c1", ItemIds(ship));   // the contained item is gone with its container
    }

    [Fact]
    public void A_dangling_reference_would_abort_the_inject()
    {
        // sanity: the hard integrity guard fires. An item parented to a missing strID must throw, never write.
        var cat = new Fixtures().Floor("Floor").Build();
        var ctx = Context(
            new JsonArray(
                Item("a", "Floor", 100, 200),
                new JsonObject { ["strID"] = "orphan", ["strName"] = "X", ["fX"] = 100, ["fY"] = 200, ["strParentID"] = "ghost" }),
            new JsonArray(Co("a", "Floor"), Co("orphan", "X")),
            new() { ["a"] = new OriginPart(0, 0, 0, []) });
        var doc = Fixtures.Doc(cat, new Placement { DefName = "Floor", X = 0, Y = 0, OriginStrID = "a" });

        Assert.Throws<System.IO.InvalidDataException>(() => SaveEdit.BuildInjectedShip(doc, ctx, cat, NoSpecs));
    }

    [Fact]
    public void A_loose_floor_item_is_injected_as_a_free_standing_item_with_a_CO()
    {
        // the ITEMS palette: a loose item dropped on a floor must reach the save (its own top-level item + a CO,
        // or the game skips it on load). A single item is one entry.
        var cat = new Fixtures().Floor("Floor").Part("Widget").Build();
        var ctx = Context(
            new JsonArray(Item("a", "Floor", 100, 200)),
            new JsonArray(Co("a", "Floor")),
            new() { ["a"] = new OriginPart(0, 0, 0, []) });
        var doc = Fixtures.Doc(cat, new Placement { DefName = "Floor", X = 0, Y = 0, OriginStrID = "a" });
        new PlaceLooseCommand(new LooseObject { DefName = "Widget", X = 0, Y = 0 }).Do(doc);

        var (ship, _) = SaveEdit.BuildInjectedShip(doc, ctx, cat, NoSpecs);

        var widget = Assert.Single(Items(ship), i => (string)i["strName"]! == "Widget");
        Assert.Null(widget["strParentID"]);                         // free-standing, not inside a container
        Assert.Contains((string)widget["strID"]!, CoIds(ship));      // has a CO (or a save load skips it)
    }

    [Fact]
    public void A_loose_stack_is_injected_as_a_head_plus_members_with_astack()
    {
        var cat = new Fixtures().Floor("Floor").Part("Ammo", stackLimit: 10).Build();
        var ctx = Context(
            new JsonArray(Item("a", "Floor", 100, 200)),
            new JsonArray(Co("a", "Floor")),
            new() { ["a"] = new OriginPart(0, 0, 0, []) });
        var doc = Fixtures.Doc(cat, new Placement { DefName = "Floor", X = 0, Y = 0, OriginStrID = "a" });
        new PlaceLooseCommand(new LooseObject { DefName = "Ammo", X = 0, Y = 0, Quantity = 3 }).Do(doc);

        var (ship, _) = SaveEdit.BuildInjectedShip(doc, ctx, cat, NoSpecs);

        var ammo = Items(ship).Where(i => (string)i["strName"]! == "Ammo").ToList();
        Assert.Equal(3, ammo.Count);                                            // head + 2 members
        var head = Assert.Single(ammo, i => i["strParentID"] is null);
        Assert.Equal(2, ammo.Count(i => (string?)i["strParentID"] == (string)head["strID"]!));
        Assert.All(ammo, i => Assert.Contains((string)i["strID"]!, CoIds(ship)));   // every copy carries a CO
        var headCo = ((JsonArray)ship["aCOs"]!).Select(n => n!.AsObject()).Single(o => (string)o["strID"]! == (string)head["strID"]!);
        Assert.Equal(2, ((JsonArray)headCo["aStack"]!).Count);                  // head lists its members
    }
}
