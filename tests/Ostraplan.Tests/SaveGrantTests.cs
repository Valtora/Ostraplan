using System.Text.Json.Nodes;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// <see cref="SaveGrant.BuildShip"/> exercised game-free: the template-to-save-shape conversion (a condition
/// owner for every item, GPM panels baked without losing device wiring, per-part wear, the pristine + prefill
/// pairing), the spawn draw ported from the game's own "bought a ship with nowhere to dock" path, and RegID
/// minting. No file I/O and no install — the writer's end-to-end test against a real save is separate.
/// </summary>
public class SaveGrantTests
{
    private static readonly IReadOnlyList<RoomSpecDef> NoSpecs = [];

    /// <summary>An anchor at a plausible in-system position with a station-sized collision radius (1500 m, what
    /// every core station reads).</summary>
    private static GrantAnchor Anchor(int sizeMetres = 1500) =>
        new(1.05316490080142, -1.55757630566879, 1.6e-07, 1.8e-08, "OKLG", true, sizeMetres);

    private static GrantOptions Opts(WearOptions? wear = null, int? seed = 1234) =>
        new("Test Design", new ExportMetadata("Rustbucket"), wear, seed);

    private static IEnumerable<JsonObject> Items(JsonObject ship) =>
        (ship["aItems"] as JsonArray ?? []).Select(n => n!.AsObject());

    private static IEnumerable<JsonObject> Cos(JsonObject ship) =>
        (ship["aCOs"] as JsonArray ?? []).Select(n => n!.AsObject());

    /// <summary>A small sealed room: enough parts that rooms, rating and the CO pass all have something to chew on.</summary>
    private static (Catalog Cat, ShipDocument Doc) Room()
    {
        var cat = new Fixtures().Floor().Wall().Build();
        var doc = new ShipDocument(cat);
        for (var x = 0; x < 4; x++)
            for (var y = 0; y < 4; y++)
            {
                var edge = x is 0 or 3 || y is 0 or 3;
                Fixtures.Place(doc, edge ? "Wall" : "Floor", x, y);
            }
        return (cat, doc);
    }

    // ---- save shape ----

    [Fact]
    public void Every_item_gets_a_condition_owner()
    {
        var (cat, doc) = Room();

        var (ship, report) = SaveGrant.BuildShip(doc, cat, NoSpecs, "H-1234", Anchor(), Opts(), epoch: 500);

        // THE invariant a save load enforces: DataHandler.SpawnItems skips any item whose strID is absent from
        // dictCOSaves, so a missing CO silently deletes that part from the ship.
        var coIds = Cos(ship).Select(c => (string)c["strID"]!).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(coIds);
        foreach (var item in Items(ship))
            Assert.Contains((string)item["strID"]!, coIds);
        Assert.Equal(16, report.ItemCount);
    }

    [Fact]
    public void Synthesized_cos_repopulate_the_defs_conds_and_name_the_ship()
    {
        var (cat, doc) = Room();

        var (ship, _) = SaveGrant.BuildShip(doc, cat, NoSpecs, "H-1234", Anchor(), Opts(), epoch: 500);

        var co = Cos(ship).First();
        Assert.Equal("DEFAULT", (string)(co["aConds"] as JsonArray)![0]!);   // CondOwner.SetData rebuilds from the def
        Assert.Equal("H-1234", (string)co["strRegIDLast"]!);
        Assert.True((bool)co["bAlive"]!);
    }

    [Fact]
    public void Identity_follows_the_games_own_save_shape()
    {
        var (cat, doc) = Room();

        var (ship, report) = SaveGrant.BuildShip(doc, cat, NoSpecs, "H-1234", Anchor(), Opts(), epoch: 500);

        // a save's own ships name themselves by their registration (verified against a real save)
        Assert.Equal("H-1234", (string)ship["strName"]!);
        Assert.Equal("H-1234", (string)ship["strRegID"]!);
        Assert.Equal("H-1234", (string)ship["strXPDR"]!);
        Assert.Equal("Rustbucket", (string)ship["publicName"]!);
        Assert.Equal("Rustbucket", report.PublicName);
        // left as the sentinel on purpose: Ship.InitShip re-rolls it from the TXTShipOrigin loot on any load
        Assert.Equal("$TEMPLATE", (string)ship["origin"]!);
    }

    [Fact]
    public void Public_name_falls_back_to_the_design_name()
    {
        var (cat, doc) = Room();
        var opts = new GrantOptions("Test Design", Meta: null, Wear: null, PlacementSeed: 1);

        var (ship, report) = SaveGrant.BuildShip(doc, cat, NoSpecs, "H-1234", Anchor(), opts, epoch: 0);

        Assert.Equal("Test Design", (string)ship["publicName"]!);
        Assert.Equal("Test Design", report.PublicName);
    }

    [Fact]
    public void Hull_is_marked_pristine_and_armed_to_refill_its_atmosphere()
    {
        var (cat, doc) = Room();

        var (ship, _) = SaveGrant.BuildShip(doc, cat, NoSpecs, "H-1234", Anchor(), Opts(), epoch: 0);

        // freshly generated aRooms have no gas containers, so without bPrefill the ship comes up in vacuum;
        // DMGStatus New is what stops that same pass also running the break-in damage roll.
        Assert.True((bool)ship["bPrefill"]!);
        Assert.Equal(0, (int)ship["DMGStatus"]!);
        Assert.False((bool)ship["bBreakInUsed"]!);
    }

    // ---- GPM ----

    [Fact]
    public void Device_panels_are_baked_from_the_def()
    {
        var cat = new Fixtures()
            .Floor()
            .GpmTemplate("PanelTemplate", "status", "true")
            .Part("Pump", tileConds: ["IsFixture", "IsObstruction"], startingConds: ["IsInstalled"],
                gpm: [("Panel A", "PanelTemplate")])
            .Build();
        var doc = Fixtures.Doc(cat,
            new Placement { DefName = "Floor", X = 0, Y = 0 },
            new Placement { DefName = "Pump", X = 1, Y = 0 });

        var (ship, _) = SaveGrant.BuildShip(doc, cat, NoSpecs, "H-1234", Anchor(), Opts(), epoch: 0);

        var pump = Items(ship).Single(i => (string)i["strName"]! == "Pump");
        var panels = pump["aGPMSettings"] as JsonArray;
        Assert.NotNull(panels);
        Assert.Equal("Panel A", (string)panels!.Single()!["strName"]!);
    }

    [Fact]
    public void Baking_panels_never_drops_the_wiring_already_on_a_device()
    {
        // A device wired in the design carries an Electrical panel holding its signal connections. The def
        // declares an Electrical panel too (an empty one), so a naive "write the def's panels" would erase them.
        var cat = new Fixtures()
            .Floor()
            .GpmTemplate("ElectricalTemplate", "status", "true", "outputConnections", "")
            .GpmTemplate("PanelTemplate", "status", "true")
            .Part("Sensor", tileConds: ["IsFixture", "IsObstruction"],
                startingConds: ["IsInstalled", "IsSignalable"],
                gpm: [("Electrical", "ElectricalTemplate"), ("Panel A", "PanelTemplate")])
            .Build();

        var doc = new ShipDocument(cat);
        Fixtures.Place(doc, "Floor", 0, 0);
        var source = Fixtures.Place(doc, "Sensor", 1, 0);
        var target = Fixtures.Place(doc, "Sensor", 2, 0);
        new AddLinkCommand(new DeviceLink(source.Id, target.Id)).Do(doc);

        var (ship, _) = SaveGrant.BuildShip(doc, cat, NoSpecs, "H-1234", Anchor(), Opts(), epoch: 0);

        var wired = Items(ship)
            .Where(i => i["aGPMSettings"] is JsonArray)
            .Select(i => (Item: i, Electrical: (i["aGPMSettings"] as JsonArray)!
                .FirstOrDefault(p => (string?)p!["strName"] == "Electrical")))
            .Where(x => x.Electrical is not null)
            .ToList();
        Assert.Equal(2, wired.Count);

        // the connection survived, and the def's other panel was added alongside rather than instead
        var flat = wired.Select(w => string.Join("|", (w.Electrical!["dictGUIPropMap"] as JsonArray)!.Select(v => (string?)v ?? "")));
        Assert.Contains(flat, f => f.Contains("#0#true#"));
        foreach (var w in wired)
            Assert.Contains((w.Item["aGPMSettings"] as JsonArray)!, p => (string?)p!["strName"] == "Panel A");
    }

    // ---- wear ----

    [Fact]
    public void Wear_damages_installed_parts_and_grades_the_rating()
    {
        var cat = new Fixtures()
            .Floor()
            .Part("Bench", tileConds: ["IsFixture", "IsObstruction"], startingConds: ["IsInstalled"],
                condValues: new Dictionary<string, double> { ["StatDamageMax"] = 100 })
            .Build();
        var doc = Fixtures.Doc(cat,
            new Placement { DefName = "Floor", X = 0, Y = 0 },
            new Placement { DefName = "Bench", X = 1, Y = 0 },
            new Placement { DefName = "Bench", X = 2, Y = 0 });

        var wear = new WearOptions(true, WearModel.VanillaUsedCondition, Seed: 7);
        var (ship, report) = SaveGrant.BuildShip(doc, cat, NoSpecs, "H-1234", Anchor(), Opts(wear), epoch: 0);

        // wear rides on the CO's aConds, which is where a save load reads a part's condition from (NOT on the
        // item's aCondOverrides, which is the template-spawn mechanism)
        var damaged = Cos(ship)
            .Where(c => (string?)c["strCODef"] == "Bench")
            .Count(c => (c["aConds"] as JsonArray)!.Any(v => ((string?)v)?.StartsWith("StatDamage=") == true));
        Assert.Equal(2, damaged);
        Assert.Contains(report.Rating.Condition, new[] { "A", "B", "C", "D", "E" });
        Assert.Equal(report.Rating.Condition, (string)(ship["aRating"] as JsonArray)![1]!);
    }

    [Fact]
    public void Rating_is_stamped_with_the_saves_own_clock()
    {
        var (cat, doc) = Room();

        var (ship, _) = SaveGrant.BuildShip(doc, cat, NoSpecs, "H-1234", Anchor(), Opts(), epoch: 65628140837.4364);

        // slot 0 is when the ship was last rated; a mod export has no clock and writes "0", a grant does
        Assert.Equal("65628140837.4364", (string)(ship["aRating"] as JsonArray)![0]!);
    }

    [Fact]
    public void Wear_off_leaves_the_ship_pristine()
    {
        var cat = new Fixtures()
            .Floor()
            .Part("Bench", tileConds: ["IsFixture", "IsObstruction"], startingConds: ["IsInstalled"],
                condValues: new Dictionary<string, double> { ["StatDamageMax"] = 100 })
            .Build();
        var doc = Fixtures.Doc(cat,
            new Placement { DefName = "Floor", X = 0, Y = 0 },
            new Placement { DefName = "Bench", X = 1, Y = 0 });

        var (ship, report) = SaveGrant.BuildShip(doc, cat, NoSpecs, "H-1234", Anchor(), Opts(WearOptions.Pristine), epoch: 0);

        Assert.DoesNotContain(Cos(ship), c => (c["aConds"] as JsonArray)!.Any(v => ((string?)v)?.StartsWith("StatDamage=") == true));
        Assert.Equal("A", report.Rating.Condition);
    }

    // ---- placement ----

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(99)]
    [InlineData(12345)]
    public void Spawn_lands_in_the_games_own_three_to_five_kilometre_band(int seed)
    {
        var anchor = Anchor();

        var (x, y, distanceKm) = SaveGrant.DrawSpawnPoint(anchor, new Random(seed));

        var dx = x - anchor.PosX;
        var dy = y - anchor.PosY;
        var au = Math.Sqrt(dx * dx + dy * dy);
        Assert.InRange(au, SaveGrant.MinRadiusAu, SaveGrant.MaxRadiusAu);
        Assert.InRange(distanceKm, 3.0, 5.0);
    }

    [Fact]
    public void Spawn_stays_far_inside_the_ferrys_range()
    {
        // GUIPDAFerry.ShowRequest drops a destination beyond this, which is the whole point of parking the ship
        // next door rather than anywhere in the system.
        Assert.True(SaveGrant.MaxRadiusAu * 5 < SaveGrant.FerryRangeAu);
    }

    [Fact]
    public void Spawn_clears_an_implausibly_large_anchor_rather_than_landing_inside_it()
    {
        // 5,000 m radius is larger than anything in core data (a station reads 1,500), so every draw in the band
        // is inside it and the fallback has to take over.
        var anchor = Anchor(sizeMetres: 5000);

        var (x, y, _) = SaveGrant.DrawSpawnPoint(anchor, new Random(3));

        var dx = x - anchor.PosX;
        var dy = y - anchor.PosY;
        Assert.Equal(SaveGrant.MaxRadiusAu, Math.Sqrt(dx * dx + dy * dy), 12);
    }

    [Fact]
    public void Situ_inherits_the_anchors_reference_body_and_velocity_but_is_not_a_station()
    {
        var (cat, doc) = Room();
        var anchor = Anchor();

        var (ship, _) = SaveGrant.BuildShip(doc, cat, NoSpecs, "H-1234", anchor, Opts(), epoch: 0);

        var ss = (ship["objSS"] as JsonObject)!;
        Assert.Equal("OKLG", (string)ss["boPORShip"]!);
        Assert.Equal(anchor.VelX, (double)ss["vVelX"]!);
        Assert.True((bool)ss["bBOLocked"]!);
        // bIsBO is what makes a ship a station, and it is also what CondOwner.ClaimShip refuses to claim
        Assert.False((bool)ss["bIsBO"]!);
        Assert.False((bool)ss["bGrounded"]!);
        // Ship.InitShip recomputes the collision radius from the floor plan on load, so we leave it alone
        Assert.Equal(0, (int)ss["size"]!);
    }

    [Fact]
    public void Situ_carries_a_path_history_or_the_whole_simulation_dies()
    {
        // Regression, and the nastiest failure this feature has had. ShipSitu(JsonShipSitu) does NOT chain to the
        // constructor that calls InitPath(), so aPathRecent is built only when aPathRecentX is present in the
        // save. StarSystem.UpdateShip then finishes with an unguarded objSS.aPathRecent.Count, so a ship without
        // one throws every frame — and the exception escapes StarSystem.Update, freezing the entire sim (the
        // player stops moving and every stat runs red), not merely the granted ship.
        var (cat, doc) = Room();

        var (ship, _) = SaveGrant.BuildShip(doc, cat, NoSpecs, "H-1234", Anchor(), Opts(), epoch: 65628140837.4364);

        var ss = (ship["objSS"] as JsonObject)!;
        var t = ss["aPathRecentT"] as JsonArray;
        var px = ss["aPathRecentX"] as JsonArray;
        var py = ss["aPathRecentY"] as JsonArray;
        Assert.NotNull(t);
        Assert.NotNull(px);
        Assert.NotNull(py);
        // all three, and the same length: the loader reads aPathRecentT.Length while gating on aPathRecentX
        Assert.Single(t!);
        Assert.Equal(t!.Count, px!.Count);
        Assert.Equal(t.Count, py!.Count);
        Assert.Equal(65628140837.4364, (double)t[0]!);
        Assert.Equal((double)ss["vPosx"]!, (double)px[0]!);
        Assert.Equal((double)ss["vPosy"]!, (double)py[0]!);
    }

    [Fact]
    public void Placement_seed_makes_the_spawn_reproducible()
    {
        var (cat, doc) = Room();

        var (a, _) = SaveGrant.BuildShip(doc, cat, NoSpecs, "H-1", Anchor(), Opts(seed: 42), epoch: 0);
        var (b, _) = SaveGrant.BuildShip(doc, cat, NoSpecs, "H-2", Anchor(), Opts(seed: 42), epoch: 0);

        Assert.Equal((double)(a["objSS"] as JsonObject)!["vPosx"]!, (double)(b["objSS"] as JsonObject)!["vPosx"]!);
    }

    // ---- registration ----

    [Fact]
    public void Minted_reg_id_avoids_every_registration_the_save_uses()
    {
        var taken = new HashSet<string>(StringComparer.Ordinal) { "B-A1R", "OKLG", "H-0B0" };

        for (var i = 0; i < 50; i++)
        {
            var reg = SaveGrant.MintRegId(taken, likeRegId: "B-A1R");
            Assert.DoesNotContain(reg, taken);
            Assert.StartsWith("B-", reg);   // the origin loot keys off the first letter, so match the neighbourhood
            taken.Add(reg);
        }
    }

    [Fact]
    public void Minted_reg_id_falls_back_to_a_sane_prefix()
    {
        Assert.StartsWith("H-", SaveGrant.MintRegId([], likeRegId: null));
        Assert.StartsWith("H-", SaveGrant.MintRegId([], likeRegId: "1234"));
    }
}
