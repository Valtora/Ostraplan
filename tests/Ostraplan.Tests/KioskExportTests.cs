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
}
