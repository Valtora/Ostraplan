using System.Text.Json.Nodes;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The loot-pool format parser and the broker/Special-Offer/starting-ship fragment builders, game-free. The
/// weighted <c>aCOs</c> string is the game's own format (<c>Name=WeightxCount</c>, <c>|</c>-delimited); a broker
/// pool is a single-element array whose one string holds the whole weighted set, so adding a ship must append to
/// that string (not add a second array element, which would roll a second ship). These tests pin that contract.
/// </summary>
public class KioskExportTests
{
    // ---- LootList ----

    [Fact]
    public void Parse_reads_name_weight_and_count()
    {
        var entries = LootList.Parse("Babak=0.017x1|ShuttleSmall=0.034x1|Bulk Lifter=0.044x1");
        Assert.Equal(3, entries.Count);
        Assert.Equal("Babak", entries[0].Name);
        Assert.Equal(0.017, entries[0].Weight, 6);
        Assert.Equal("1", entries[0].Count);
        Assert.Equal("Bulk Lifter", entries[2].Name);   // names with spaces survive
    }

    [Fact]
    public void Parse_keeps_a_range_count_as_a_string()
    {
        var entries = LootList.Parse("RandomShipBrokerOKLG=1.0x3-10");
        Assert.Equal("3-10", Assert.Single(entries).Count);
    }

    [Fact]
    public void Append_adds_to_the_existing_string_and_skips_duplicates()
    {
        var appended = LootList.Append("A=0.02x1|B=0.03x1", "MyShip", 0.05);
        Assert.Equal("A=0.02x1|B=0.03x1|MyShip=0.05x1", appended);

        // already present: no-op (append must not double it)
        Assert.Equal(appended, LootList.Append(appended, "MyShip", 0.9));
    }

    [Fact]
    public void Append_to_empty_returns_just_the_entry()
    {
        Assert.Equal("MyShip=0.05x1", LootList.Append("", "MyShip", 0.05));
    }

    [Fact]
    public void FormatEntry_uses_invariant_culture_decimals()
    {
        Assert.Equal("MyShip=0.017x1", LootList.FormatEntry("MyShip", 0.017));
        Assert.Equal("MyShip=1x1", LootList.FormatEntry("MyShip", 1.0));   // trailing zeros trimmed
    }

    [Fact]
    public void AverageWeight_of_a_pool_is_the_mean_of_its_entries()
    {
        Assert.Equal(0.03, LootList.AverageWeight("A=0.02x1|B=0.04x1"), 6);
        Assert.Equal(0.05, LootList.AverageWeight(""), 6);   // empty pool falls back to 0.05
    }

    // ---- KioskExport pool mutation ----

    private static JsonObject Pool(string name, string aCOs) => new()
    {
        ["strName"] = name,
        ["aCOs"] = new JsonArray(aCOs),
        ["aLoots"] = new JsonArray(),
        ["strType"] = "ship",
    };

    [Fact]
    public void AppendShipToPool_preserves_existing_ships_and_adds_one_option()
    {
        var pool = Pool("RandomShipBrokerOKLG", "Babak=0.017x1|ShuttleSmall=0.034x1");
        KioskExport.AppendShipToPool(pool, "Vagabond+", 0.05);

        // still ONE array element (a second element would make the game roll two ships)
        var aCOs = pool["aCOs"]!.AsArray();
        Assert.Single(aCOs);
        var names = LootList.Parse(aCOs[0]!.GetValue<string>()).Select(e => e.Name).ToList();
        Assert.Equal(["Babak", "ShuttleSmall", "Vagabond+"], names);
    }

    [Fact]
    public void PinShipToPool_replaces_the_whole_pick_with_one_ship()
    {
        var pool = Pool("RandomShipBrokerSpecialOffer", "SalvageCustom2=1.0x1");
        KioskExport.PinShipToPool(pool, "Vagabond+");

        var aCOs = pool["aCOs"]!.AsArray();
        Assert.Equal("Vagabond+=1x1", Assert.Single(aCOs)!.GetValue<string>());
    }

    // ---- StartingShipExport ----

    [Fact]
    public void Token_keeps_only_letters_and_digits_and_is_never_empty()
    {
        Assert.Equal("Vagabond2", StartingShipExport.Token("Vagabond+ 2"));
        Assert.Equal("Ship", StartingShipExport.Token("!!!"));   // fallback
    }

    [Fact]
    public void StartingShip_build_produces_the_reward_grant_and_the_take_chain()
    {
        var events = Pool(StartingShipExport.ShipEventsPool, "CGEncShipSalvagePodIntro=0.16x1");
        var frags = StartingShipExport.Build(events, "Vagabond+", 0.16, "OKLG", 500000, "A ship.", "A listing.");

        // reward loot: names the ship template by its strName, strType "ship"
        var reward = Assert.Single(frags.LootObjects, o => o["strName"]!.GetValue<string>() == "CGEncVagabondReward");
        Assert.Equal("ship", reward["strType"]!.GetValue<string>());
        Assert.Equal("Vagabond+=1x1", reward["aCOs"]!.AsArray()[0]!.GetValue<string>());

        // the shipbreaker events pool gained our intro as a weighted option, keeping the core one
        var evOverride = Assert.Single(frags.LootObjects, o => o["strName"]!.GetValue<string>() == StartingShipExport.ShipEventsPool);
        var evNames = LootList.Parse(evOverride["aCOs"]!.AsArray()[0]!.GetValue<string>()).Select(e => e.Name).ToList();
        Assert.Contains("CGEncShipSalvagePodIntro", evNames);
        Assert.Contains("CGEncVagabondIntro", evNames);

        // the Take lifeevent grants via strShipRewards → the reward pool, and carries the mortgage
        var takeEvent = Assert.Single(frags.Lifeevents, o => o["strName"]!.GetValue<string>() == "CGEncVagabondTake");
        Assert.Equal("CGEncVagabondReward", takeEvent["strShipRewards"]!.GetValue<string>());
        Assert.Equal("OKLG", takeEvent["strStartATC"]!.GetValue<string>());
        Assert.Equal(500000, takeEvent["fShipMortgage"]!.GetValue<double>());
        Assert.True(takeEvent["bShipOwned"]!.GetValue<bool>());

        // the intro interaction offers the core "keep looking" branch + our Take
        var introInteraction = Assert.Single(frags.Interactions, o => o["strName"]!.GetValue<string>() == "CGEncVagabondIntro");
        var choices = introInteraction["aInverse"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal([StartingShipExport.ContinueInteraction, "CGEncVagabondTake"], choices);

        // the Take interaction grants the standard shipbreaker starting gear
        var takeInteraction = Assert.Single(frags.Interactions, o => o["strName"]!.GetValue<string>() == "CGEncVagabondTake");
        Assert.Equal("addus," + StartingShipExport.StarterLoadout,
            takeInteraction["aLootItms"]!.AsArray()[0]!.GetValue<string>());
    }

    [Fact]
    public void StartingShip_exclusive_pins_the_pool_to_only_this_ship()
    {
        var events = Pool(StartingShipExport.ShipEventsPool, "CGEncShipSalvagePodIntro=0.16x1|CGEncShipSalvagePod2Intro=0.16x1");
        var frags = StartingShipExport.Build(events, "Vagabond+", 0.16, "OKLG", 500000, "A ship.", "A listing.", exclusive: true);

        // the events pool now holds ONLY our intro at weight 1 — the vanilla pods are dropped (guaranteed start)
        var evOverride = Assert.Single(frags.LootObjects, o => o["strName"]!.GetValue<string>() == StartingShipExport.ShipEventsPool);
        var entries = LootList.Parse(evOverride["aCOs"]!.AsArray()[0]!.GetValue<string>()).ToList();
        var only = Assert.Single(entries);
        Assert.Equal("CGEncVagabondIntro", only.Name);
        Assert.Equal(1.0, only.Weight, 6);
    }

    // ---- derelict rings ----

    /// <summary>
    /// A derelict ring pool is the same kind of weighted <c>aCOs</c> pick a broker kiosk is, which is why the
    /// override machinery is shared. <c>star_system.json</c>'s <c>aSpawnDerelictRings</c> names one per ring, and
    /// the spawner is what marks the ship derelict: no core ship template carries a damaged state of its own.
    /// </summary>
    [SkippableFact]
    public void A_derelict_pool_takes_the_same_override_a_kiosk_does()
    {
        var g = TestData.RequireGame();

        var pool = KioskExport.DerelictPoolOverride(g.Index, "RandomDerelictSmall", "My Wreck", 0.1);

        var entries = LootList.Parse(pool["aCOs"]!.AsArray()[0]!.GetValue<string>()).ToList();
        Assert.Contains(entries, e => e.Name == "My Wreck" && Math.Abs(e.Weight - 0.1) < 1e-6);
        Assert.Contains(entries, e => e.Name == "Katydid");   // the stock wrecks survive
        Assert.Equal("ship", pool["strType"]!.GetValue<string>());
    }

    /// <summary>
    /// Venus is a composer, not a leaf: <c>RandomDerelictVenus</c> carries an empty <c>aCOs</c> and delegates
    /// through <c>aLoots</c> to <c>RandomScavShipVNCA</c> (0.85) and <c>RandomScavShip</c> (0.15). Writing to the
    /// composer would be writing at the wrong level, so the offered pool is the VNCA leaf.
    /// </summary>
    [SkippableFact]
    public void The_Venus_derelict_target_is_the_leaf_pool_not_the_composer()
    {
        var g = TestData.RequireGame();

        Assert.Contains(KioskExport.DerelictPools, p => p.Pool == "RandomScavShipVNCA");
        Assert.DoesNotContain(KioskExport.DerelictPools, p => p.Pool == "RandomDerelictVenus");

        var composer = g.Index.Type("loot")["RandomDerelictVenus"].El;
        Assert.Empty(composer.GetProperty("aCOs").EnumerateArray());
        Assert.NotEmpty(composer.GetProperty("aLoots").EnumerateArray());
    }

    [SkippableFact]
    public void Every_offered_derelict_pool_exists_in_the_game_data()
    {
        var g = TestData.RequireGame();

        foreach (var (pool, _) in KioskExport.DerelictPools)
            Assert.True(g.Index.Type("loot").ContainsKey(pool), $"{pool} is missing from the loot data");
    }

    /// <summary>The bands overlap heavily, so the suggestion is a nearest fit rather than a claim about which
    /// size a hull "is". It only has to be stable, and sane at the extremes.</summary>
    [Theory]
    [InlineData(50, "RandomDerelictSmall")]
    [InlineData(250, "RandomDerelictSmall")]
    [InlineData(400, "RandomDerelictMedium")]
    [InlineData(9000, "RandomDerelictBig")]
    public void The_suggested_band_is_the_nearest_by_median(int parts, string expected) =>
        Assert.Equal(expected, KioskExport.SuggestDerelictBand(parts));

    [Fact]
    public void A_delivery_with_only_a_derelict_field_still_counts_as_obtainable()
    {
        var delivery = ShipDelivery.None with { DerelictPools = ["RandomDerelictBig"] };

        Assert.True(delivery.IsObtainable);
        Assert.True(delivery.TouchesLoot);   // so the Ostrasort conflict patch still runs
        Assert.False(ShipDelivery.None.IsObtainable);
    }

    // ---- kiosk discovery ----

    /// <summary>
    /// The broker list is read out of the loaded loot table, not hardcoded. It used to be a fixed five, which was
    /// the whole set in 0.15.1.6; game 1.0 opened the system and there are thirteen, so a hardcoded list hid most
    /// of the game's kiosks from the export dialog. This pins the discovery, not a count, so the next station the
    /// game adds is picked up rather than breaking the test.
    /// </summary>
    [SkippableFact]
    public void Broker_pools_are_discovered_from_the_loaded_data()
    {
        var g = TestData.RequireGame();
        var pools = KioskExport.BrokerPoolsIn(g.Index);
        if (pools.Count == 0) return;   // no loot data → data skip

        var names = pools.Select(p => p.Pool).ToList();
        Assert.All(names, n => Assert.StartsWith("RandomShipBroker", n));
        Assert.DoesNotContain(names, n => n.StartsWith("RandomShipBrokerSpecialOffer", System.StringComparison.Ordinal));
        Assert.Contains("RandomShipBrokerOKLG", names);
        Assert.Equal("RandomShipBrokerOKLG", names[0]);   // the starting station leads the list
        Assert.Equal(names.Count, names.Distinct().Count());

        // every discovered pool is a real, overridable ship pool
        foreach (var name in names) Assert.True(g.Index.Type("loot").ContainsKey(name));

        // and the label carries the station code, glossed where the world data names it
        Assert.Equal("OKLG (K-Legrange)", pools[0].Label);
    }

    [SkippableFact]
    public void Special_offer_pools_are_discovered_and_kept_out_of_the_broker_list()
    {
        var g = TestData.RequireGame();
        var special = KioskExport.SpecialOfferPoolsIn(g.Index);
        if (special.Count == 0) return;   // no loot data → data skip

        var names = special.Select(p => p.Pool).ToList();
        Assert.All(names, n => Assert.StartsWith("RandomShipBrokerSpecialOffer", n));
        Assert.Equal("RandomShipBrokerSpecialOffer", names[0]);   // the bare default pool leads
        Assert.Equal("OKLG / default", special[0].Label);
        Assert.Empty(names.Intersect(KioskExport.BrokerPoolsIn(g.Index).Select(p => p.Pool)));
    }

    /// <summary>An ATC code the world data does not name still gets an entry, because a station Ostraplan has
    /// never heard of is exactly the case the old hardcoded list got wrong.</summary>
    [Fact]
    public void An_unknown_station_code_labels_as_itself() =>
        Assert.Equal("ZZZZ", KioskExport.StationLabel("ZZZZ"));
}
