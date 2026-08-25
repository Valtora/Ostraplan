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

    /// <summary>
    /// A grant turns a template into a save record by giving every item a CO, and that is exactly what stops the
    /// game spawning a garment's pockets for itself. So the grant has to write them, or a suit added to a save
    /// this way arrives with no compartments — the same fault as writing them as loose cargo did.
    /// </summary>
    [Fact]
    public void A_garment_granted_into_a_save_keeps_its_pockets()
    {
        var cat = new Fixtures()
            .Floor().Wall()
            .ItemLoot("PocketLoot", ("Pocket", 2))
            .Part("Pocket", container: (1, 2), slotKeys: ["hipL", "hipR"])
            .Part("Coveralls", defaultLoot: "PocketLoot", slotsWeHave: ["hipL", "hipR"])
            .Build();
        var doc = new ShipDocument(cat);
        for (var x = 0; x < 4; x++)
            for (var y = 0; y < 4; y++)
                Fixtures.Place(doc, x is 0 or 3 || y is 0 or 3 ? "Wall" : "Floor", x, y);
        new PlaceLooseCommand(new LooseObject { DefName = "Coveralls", X = 1, Y = 1 }).Do(doc);

        var (ship, _) = SaveGrant.BuildShip(doc, cat, NoSpecs, "H-1234", Anchor(), Opts(), epoch: 500);

        var garment = Assert.Single(Items(ship), i => (string?)i["strName"] == "Coveralls");
        var pockets = Items(ship).Where(i => (string?)i["strName"] == "Pocket").ToList();
        Assert.Equal(2, pockets.Count);
        Assert.All(pockets, p => Assert.Equal((string)garment["strID"]!, (string?)p["strSlotParentID"]));
        // and each names the slot it sits in, or the game refuses to slot it
        var cos = Cos(ship).ToList();
        Assert.Equal(
            new[] { "hipL", "hipR" },
            pockets.Select(p => (string?)cos.Single(c => (string)c["strID"]! == (string)p["strID"]!)["strSlotName"])
                .Order().ToArray());
        Assert.All(Items(ship), i => Assert.Contains((string)i["strID"]!, cos.Select(c => (string)c["strID"]!)));
    }

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

    // ---- transferring a ship: the condition comes across rather than being re-rolled ----

    /// <summary>A source condition owner carrying <paramref name="damage"/> accumulated <c>StatDamage</c>.</summary>
    private static JsonNode SourceCo(string strID, double damage) => new JsonObject
    {
        ["strID"] = strID,
        ["aConds"] = new JsonArray("IsInstalled=1.0x1", $"StatDamage=1.0x{damage}"),
    };

    /// <summary>A bench-and-floor design whose benches remember the save items they were imported from.</summary>
    private static (Catalog Cat, ShipDocument Doc) WornPair()
    {
        var cat = new Fixtures()
            .Floor()
            .Part("Bench", tileConds: ["IsFixture", "IsObstruction"], startingConds: ["IsInstalled"],
                condValues: new Dictionary<string, double> { ["StatDamageMax"] = 100 })
            .Build();
        var doc = Fixtures.Doc(cat,
            new Placement { DefName = "Floor", X = 0, Y = 0 },
            new Placement { DefName = "Bench", X = 1, Y = 0, OriginStrID = "src-a" },
            new Placement { DefName = "Bench", X = 2, Y = 0, OriginStrID = "src-b" });
        return (cat, doc);
    }

    /// <summary>Every <c>StatDamage</c> amount on the COs of a given def, so a test can assert on the numbers
    /// rather than merely on damage being present.</summary>
    private static List<double> Damages(JsonObject ship, string def) =>
        [.. Cos(ship).Where(c => (string?)c["strCODef"] == def)
            .Select(c => (c["aConds"] as JsonArray)!
                .Select(v => (string?)v ?? "")
                .FirstOrDefault(s => s.StartsWith("StatDamage=", StringComparison.Ordinal)))
            .Select(s => s is null ? 0.0 : LootDef.CondAmount(s))];

    [Fact]
    public void A_transfer_carries_each_parts_real_damage_across()
    {
        var (cat, doc) = WornPair();
        var sourceCos = new Dictionary<string, JsonNode>
        {
            ["src-a"] = SourceCo("src-a", 40),
            ["src-b"] = SourceCo("src-b", 10),
        };

        var (ship, report) = SaveGrant.BuildShip(doc, cat, NoSpecs, "H-1234", Anchor(),
            new GrantOptions("Test Design", null, WearOptions.Pristine, 1234, sourceCos), epoch: 0);

        // the exact amounts, not merely "something is damaged": a transfer that re-rolled would still pass that
        Assert.Equal([10.0, 40.0], Damages(ship, "Bench").Order());
        // mean condition over installed parts is (0.60 + 0.90) / 2 = 0.75
        Assert.Equal(Rating.ConditionGrade(0.75), report.Rating.Condition);
    }

    /// <summary>Source conditions win outright. Rolling wear on top would mean the ship arrived in a condition that
    /// was neither the one it had nor the one the slider asked for.</summary>
    [Fact]
    public void A_transfer_ignores_the_wear_slider()
    {
        var (cat, doc) = WornPair();
        var sourceCos = new Dictionary<string, JsonNode>
        {
            ["src-a"] = SourceCo("src-a", 40),
            ["src-b"] = SourceCo("src-b", 10),
        };

        var (ship, _) = SaveGrant.BuildShip(doc, cat, NoSpecs, "H-1234", Anchor(),
            new GrantOptions("Test Design", null, new WearOptions(true, 0.5, Seed: 7), 1234, sourceCos), epoch: 0);

        Assert.Equal([10.0, 40.0], Damages(ship, "Bench").Order());
    }

    /// <summary>A part added after the import was never on the source ship, so it has nothing to inherit and
    /// arrives undamaged rather than picking up a neighbour's wear.</summary>
    [Fact]
    public void A_part_added_since_the_import_transfers_undamaged()
    {
        var cat = new Fixtures()
            .Floor()
            .Part("Bench", tileConds: ["IsFixture", "IsObstruction"], startingConds: ["IsInstalled"],
                condValues: new Dictionary<string, double> { ["StatDamageMax"] = 100 })
            .Build();
        var doc = Fixtures.Doc(cat,
            new Placement { DefName = "Floor", X = 0, Y = 0 },
            new Placement { DefName = "Bench", X = 1, Y = 0, OriginStrID = "src-a" },
            new Placement { DefName = "Bench", X = 2, Y = 0 });   // drawn in after the import

        var (ship, _) = SaveGrant.BuildShip(doc, cat, NoSpecs, "H-1234", Anchor(),
            new GrantOptions("Test Design", null, WearOptions.Pristine, 1234,
                new Dictionary<string, JsonNode> { ["src-a"] = SourceCo("src-a", 40) }), epoch: 0);

        Assert.Equal([0.0, 40.0], Damages(ship, "Bench").Order());
    }

    /// <summary>An undamaged source part carries no <c>StatDamage</c> cond at all, and must arrive that way rather
    /// than with a zero-damage cond (which would also strip its <c>IsPristine</c> resale flag).</summary>
    [Fact]
    public void An_undamaged_source_part_stays_pristine()
    {
        var (cat, doc) = WornPair();
        var sourceCos = new Dictionary<string, JsonNode>
        {
            ["src-a"] = new JsonObject { ["strID"] = "src-a", ["aConds"] = new JsonArray("IsPristine=1.0x1") },
            ["src-b"] = new JsonObject { ["strID"] = "src-b", ["aConds"] = new JsonArray("IsPristine=1.0x1") },
        };

        var (ship, report) = SaveGrant.BuildShip(doc, cat, NoSpecs, "H-1234", Anchor(),
            new GrantOptions("Test Design", null, WearOptions.Pristine, 1234, sourceCos), epoch: 0);

        Assert.DoesNotContain(Cos(ship), c => (c["aConds"] as JsonArray)!
            .Any(v => ((string?)v)?.StartsWith("StatDamage=", StringComparison.Ordinal) == true));
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
    public void Spawn_clears_an_anchor_bigger_than_the_whole_band_instead_of_stopping_at_its_outer_edge()
    {
        // An anchor wider than 5 km leaves no draw in the band that clears it, so the fallback has to stand off
        // past the hull. It used to fall back to the outer radius, which is not a clearance at all: 5 km is inside
        // a 5 km hull, and returning it is how a granted ship ended up intersecting the station it was granted at.
        var anchor = Anchor(sizeMetres: 5000);

        var (x, y, km) = SaveGrant.DrawSpawnPoint(anchor, new Random(3));

        var separation = Math.Sqrt(Math.Pow(x - anchor.PosX, 2) + Math.Pow(y - anchor.PosY, 2));
        Assert.True(separation > SaveGrant.MaxRadiusAu);
        Assert.True(km > 2 * 5.0);                 // both hulls cleared...
        Assert.True(km < SaveGrant.FerryRangeAu * SaveGrant.MetresPerAu / 1000.0);   // ...and still ferry-reachable
    }

    [Fact]
    public void A_stations_reported_size_is_a_constant_so_the_clearance_reads_its_grid_instead()
    {
        // Verified against a mature save: every station reads objSS.size exactly 1500, from an 11x13 apartment to
        // a 190x65 residential block, while ships get a hull-derived figure running to 2020. So a station three
        // times the size of another declares the same radius, and a guard that trusts it is measuring a constant.
        var small = Anchor(sizeMetres: 1500) with { Cols = 28, Rows = 18 };
        var huge = Anchor(sizeMetres: 1500) with { Cols = 190, Rows = 65 };

        Assert.Equal(small.SizeMetres, small.RadiusMetres);      // small enough that the reported figure still wins
        Assert.True(huge.RadiusMetres > 2 * huge.SizeMetres);    // the big one is not 1,500 of anything
        Assert.True(huge.RadiusMetres / SaveGrant.MetresPerAu > SaveGrant.MinRadiusAu);   // past the 3 km draw floor

        // and the spawn is pushed out past it rather than landing 3 km from the centre of a hull that is wider
        var (x, y, _) = SaveGrant.DrawSpawnPoint(huge, new Random(3), shipCols: 15, shipRows: 25);

        var separationAu = Math.Sqrt(Math.Pow(x - huge.PosX, 2) + Math.Pow(y - huge.PosY, 2));
        Assert.True(separationAu * SaveGrant.MetresPerAu > huge.RadiusMetres);
    }

    [Fact]
    public void An_ordinary_anchor_is_unaffected_by_the_grid_check()
    {
        // The correction must not move every grant: a hull the band already clears keeps the game's own draw.
        var anchor = Anchor(sizeMetres: 200) with { Cols = 15, Rows = 22 };

        var (_, _, km) = SaveGrant.DrawSpawnPoint(anchor, new Random(7), shipCols: 15, shipRows: 25);

        Assert.InRange(km, 3.0, 5.0);
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
