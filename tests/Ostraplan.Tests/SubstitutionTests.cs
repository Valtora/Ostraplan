using System.Linq;
using System.Text.Json.Nodes;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// <see cref="Substitution"/> — standing a real part in for one whose mod isn't loaded.
///
/// <para>The danger being defended against: an unresolved item is skipped by the import (no def ⇒ no footprint,
/// no tile conds) but PRESERVED by the write-back, so it is invisible to the rooms/grid/rating engines while still
/// sitting in the save. A stand-in makes it visible again and replaces it outright, so what Ostraplan models is
/// what lands in the save. These cover the identity trick that makes that work without any extra persisted state:
/// a stand-in is exactly a placement whose origin id names a save item that isn't a modelled original.</para>
/// </summary>
public class SubstitutionTests
{
    private const string Modded = "ItmWallFromSomeMod";

    private static JsonObject Item(string id, string name, double fx, double fy, string? parent = null)
    {
        var o = new JsonObject { ["strID"] = id, ["strName"] = name, ["fX"] = fx, ["fY"] = fy, ["fRotation"] = 0.0 };
        if (parent is not null) o["strParentID"] = parent;
        return o;
    }

    private static JsonObject Co(string id, string def) => new()
    {
        ["strID"] = id, ["strCODef"] = def, ["bAlive"] = true, ["aConds"] = new JsonArray("DEFAULT"),
    };

    /// <summary>A ship whose save holds a normal floor plus an item from a mod we don't have. The floor is a
    /// modelled origin; the modded item is not (the import skipped it), which is exactly the real situation.</summary>
    private static SaveShipContext Context(JsonArray? extraItems = null, JsonArray? extraCos = null)
    {
        var items = new JsonArray(Item("a", "F", 101, 199), Item("mod1", Modded, 103, 199));
        var cos = new JsonArray(Co("a", "F"), Co("mod1", Modded));
        foreach (var n in extraItems ?? []) items.Add(n!.DeepClone());
        foreach (var n in extraCos ?? []) cos.Add(n!.DeepClone());

        return new SaveShipContext
        {
            Source = new SaveSourceRef("TestSave", "H-ABC"),
            ZipPath = @"C:\dummy\TestSave\TestSave.zip",
            ShipRecord = new JsonObject
            {
                ["strName"] = "Test", ["strRegID"] = "H-ABC",
                ["nCols"] = 6.0, ["nRows"] = 3.0,
                ["vShipPos"] = new JsonObject { ["x"] = 100.0, ["y"] = 200.0 },
                ["aItems"] = items, ["aCOs"] = cos, ["aCrew"] = new JsonArray(), ["aRooms"] = new JsonArray(),
            },
            Origins = new Dictionary<string, OriginPart> { ["a"] = new(1, 1, 0, []) },   // only the floor is modelled
            ItemsById = items.Select(n => n!.AsObject()).ToDictionary(o => (string)o["strID"]!, o => (JsonNode)o),
            CosById = cos.Select(n => n!.AsObject()).ToDictionary(o => (string)o["strID"]!, o => (JsonNode)o),
            Epoch = 0,
        };
    }

    private static Catalog Cat() => new Fixtures().Floor("F").Wall("W").Build();

    private static ShipDocument DocWithFloor(Catalog cat) =>
        Fixtures.Doc(cat, new Placement { DefName = "F", X = 1, Y = 1, OriginStrID = "a" });

    // ---- detection ----

    [Fact]
    public void An_item_whose_def_is_not_loaded_is_reported_as_outstanding()
    {
        var cat = Cat();
        var ctx = Context();

        var outstanding = Assert.Single(Substitution.Outstanding(DocWithFloor(cat), ctx, cat));
        Assert.Equal("mod1", outstanding.StrID);
        Assert.Equal(Modded, outstanding.DefName);
        Assert.Equal((103.0, 199.0), (outstanding.FX, outstanding.FY));   // the raw world pose, not a tile
    }

    [Fact]
    public void A_modelled_part_is_never_outstanding()
    {
        var cat = Cat();
        Assert.DoesNotContain(Substitution.Outstanding(DocWithFloor(cat), Context(), cat), u => u.StrID == "a");
    }

    [Fact]
    public void Contained_cargo_with_an_unloaded_def_is_not_outstanding()
    {
        // cargo is never grid structure, so it can't skew rooms or the frame — only top-level items can
        var cat = Cat();
        var ctx = Context(new JsonArray(Item("c1", "SomeModdedGun", 101, 199, parent: "a")));

        Assert.DoesNotContain(Substitution.Outstanding(DocWithFloor(cat), ctx, cat), u => u.StrID == "c1");
    }

    // ---- the stand-in ----

    [Fact]
    public void A_stand_in_lands_on_the_original_tile_and_stops_it_being_outstanding()
    {
        var cat = Cat();
        var ctx = Context();
        var doc = DocWithFloor(cat);
        var item = Substitution.Outstanding(doc, ctx, cat).Single();

        var standIn = Substitution.StandIn(item, "W", cat, ctx);
        // world 103 with vShipPos.x 100 and a 1x1 footprint -> doc col 3
        Assert.Equal((3, 1), (standIn.X, standIn.Y));
        Assert.Equal("mod1", standIn.OriginStrID);   // tagged with the item it replaces
        Assert.True(standIn.IsGiven);                // pre-existing structure, not new construction to re-validate

        new PlaceCommand(standIn).Do(doc);
        Assert.Empty(Substitution.Outstanding(doc, ctx, cat));               // covered now
        Assert.Equal(["mod1"], Substitution.StandInOriginIds(doc, ctx));
    }

    /// <summary>The persistence trick: a stand-in needs no stored state, because "origin id names a save item that
    /// isn't a modelled original" is only ever true of a stand-in. So it survives an .oplan round-trip, and
    /// deleting it reverts cleanly to leaving the modded item alone.</summary>
    [Fact]
    public void Deleting_the_stand_in_reverts_to_leaving_the_modded_item_alone()
    {
        var cat = Cat();
        var ctx = Context();
        var doc = DocWithFloor(cat);
        var standIn = Substitution.StandIn(Substitution.Outstanding(doc, ctx, cat).Single(), "W", cat, ctx);
        new PlaceCommand(standIn).Do(doc);

        new RemoveCommand([standIn]).Do(doc);

        Assert.Empty(Substitution.StandInOriginIds(doc, ctx));
        Assert.Single(Substitution.Outstanding(doc, ctx, cat));   // outstanding again
    }

    [Fact]
    public void A_moved_stand_in_still_replaces_its_original()
    {
        var cat = Cat();
        var ctx = Context();
        var doc = DocWithFloor(cat);
        var standIn = Substitution.StandIn(Substitution.Outstanding(doc, ctx, cat).Single(), "W", cat, ctx);
        new PlaceCommand(standIn).Do(doc);

        new MoveCommand([standIn], 1, 0).Do(doc);   // origin ids survive a move

        Assert.Equal(["mod1"], Substitution.StandInOriginIds(doc, ctx));
    }

    // ---- the write-back ----

    [Fact]
    public void The_inject_drops_the_replaced_item_and_writes_the_stand_in()
    {
        var cat = Cat();
        var ctx = Context();
        var doc = DocWithFloor(cat);
        new PlaceCommand(Substitution.StandIn(Substitution.Outstanding(doc, ctx, cat).Single(), "W", cat, ctx)).Do(doc);

        var (ship, report) = SaveEdit.BuildInjectedShip(doc, ctx, cat, []);

        var items = ((JsonArray)ship["aItems"]!).Select(n => n!.AsObject()).ToList();
        Assert.DoesNotContain(items, i => (string?)i["strID"] == "mod1");          // the original is gone...
        Assert.DoesNotContain(items, i => (string?)i["strName"] == Modded);
        Assert.DoesNotContain(((JsonArray)ship["aCOs"]!), n => (string?)n!["strID"] == "mod1");   // ...with its CO
        Assert.Contains(items, i => (string?)i["strName"] == "W");                 // the stand-in landed
        Assert.Equal(1, report.Added);                                             // classified as a fresh part

        // and the stand-in carries a CO, or a save load would skip it
        var standInId = (string)items.First(i => (string?)i["strName"] == "W")["strID"]!;
        Assert.Contains(((JsonArray)ship["aCOs"]!), n => (string?)n!["strID"] == standInId);
    }

    [Fact]
    public void Without_a_stand_in_the_unresolved_item_is_preserved_verbatim()
    {
        // leaving it alone is non-destructive: the item and its CO ride through untouched
        var cat = Cat();
        var ctx = Context();

        var (ship, _) = SaveEdit.BuildInjectedShip(DocWithFloor(cat), ctx, cat, []);

        Assert.Contains(((JsonArray)ship["aItems"]!), n => (string?)n!["strID"] == "mod1");
        Assert.Contains(((JsonArray)ship["aCOs"]!), n => (string?)n!["strID"] == "mod1");
    }

    [Fact]
    public void Replacing_a_container_takes_its_contents_and_reports_the_loss()
    {
        // a substituted part is replaced outright, so anything inside it goes too — that must not be silent
        var cat = Cat();
        var ctx = Context(
            new JsonArray(Item("c1", "SomeGun", 103, 199, parent: "mod1")),
            new JsonArray(Co("c1", "SomeGun")));
        var doc = DocWithFloor(cat);
        new PlaceCommand(Substitution.StandIn(Substitution.Outstanding(doc, ctx, cat).Single(), "W", cat, ctx)).Do(doc);

        var (ship, report) = SaveEdit.BuildInjectedShip(doc, ctx, cat, []);

        Assert.DoesNotContain(((JsonArray)ship["aItems"]!), n => (string?)n!["strID"] == "c1");
        var loss = Assert.Single(report.CargoDropped);
        Assert.Contains("SomeGun", loss.Items);
    }
}
