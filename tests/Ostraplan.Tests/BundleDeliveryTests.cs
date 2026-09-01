using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The loot merge a bundle has to do and a single-ship export never had to: several ships sharing one pool.
///
/// <para>The game replaces data whole-object by <c>(type, strName)</c> with the last loaded winning
/// (GAME-INTERNALS §2), so two ships that each wrote a complete <c>RandomShipBrokerOKLG</c> would leave only the
/// second in the kiosk. These pin the merge that stops that. Needs the install, because a pool has to be cloned
/// from the effective data before it can be added to.</para>
/// </summary>
public class BundleDeliveryTests
{
    private static readonly IReadOnlyList<RoomSpecDef> NoSpecs = [];

    private static BundleShip Ship(Catalog cat, string name, ShipDelivery delivery) =>
        new(Fixtures.Doc(cat, Fixtures.P("Floor", 0, 0)), name, new ExportMetadata(), Delivery: delivery);

    /// <summary>The designs are synthetic (the merge does not care what a ship is made of) but the pools are the
    /// real ones, which is the whole point.</summary>
    private static Catalog SyntheticCatalog() => new Fixtures().Floor("Floor").Build();

    private static void InTempDir(Action<string> body)
    {
        var dest = Path.Combine(Path.GetTempPath(), "OstraplanBundleLoot_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dest);
        try { body(dest); }
        finally { if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true); }
    }

    private static IReadOnlyList<JsonNode> Objects(string path) =>
        [.. JsonNode.Parse(File.ReadAllText(path))!.AsArray().Select(n => n!)];

    private static JsonNode PoolNamed(string path, string strName) =>
        Assert.Single(Objects(path), o => o["strName"]!.GetValue<string>() == strName);

    private static IReadOnlyList<string> PickNames(JsonNode pool) =>
        [.. LootList.Parse(pool["aCOs"]!.AsArray()[0]!.GetValue<string>()).Select(e => e.Name)];

    [SkippableFact]
    public void Two_ships_in_one_kiosk_produce_one_pool_holding_both()
    {
        var g = TestData.RequireGame();
        InTempDir(dest =>
        {
            var cat = SyntheticCatalog();
            var kiosk = ShipDelivery.None with { BrokerPools = ["RandomShipBrokerOKLG"], BrokerWeight = 0.05 };
            var result = BundleExport.Write(cat, NoSpecs, new BundleOptions(
                "My Pack", "Tester", "", "1.0.0", "1.0.0.13", dest,
                [Ship(cat, "Kestrel", kiosk), Ship(cat, "Harrier", kiosk)]), g.Index);

            Assert.True(result.TouchedLootPools);
            var loot = Path.Combine(result.ModDir, "data", "loot", "loot.json");

            // ONE object for the pool. Two would leave only whichever the game loaded last.
            Assert.Single(Objects(loot), o => o["strName"]!.GetValue<string>() == "RandomShipBrokerOKLG");

            var names = PickNames(PoolNamed(loot, "RandomShipBrokerOKLG"));
            Assert.Contains("Kestrel", names);
            Assert.Contains("Harrier", names);
            Assert.True(names.Count > 2, "the kiosk's own stock should still be in the pool");
        });
    }

    [SkippableFact]
    public void Ships_in_different_pools_get_a_pool_object_each()
    {
        var g = TestData.RequireGame();
        InTempDir(dest =>
        {
            var cat = SyntheticCatalog();
            var result = BundleExport.Write(cat, NoSpecs, new BundleOptions(
                "My Pack", "Tester", "", "1.0.0", "1.0.0.13", dest,
                [
                    Ship(cat, "Kestrel", ShipDelivery.None with { BrokerPools = ["RandomShipBrokerOKLG"], BrokerWeight = 0.05 }),
                    Ship(cat, "Hulk", ShipDelivery.None with { DerelictPools = ["RandomDerelictSmall"], DerelictWeight = 0.1 }),
                ]), g.Index);

            var loot = Path.Combine(result.ModDir, "data", "loot", "loot.json");
            Assert.Contains("Kestrel", PickNames(PoolNamed(loot, "RandomShipBrokerOKLG")));
            Assert.Contains("Hulk", PickNames(PoolNamed(loot, "RandomDerelictSmall")));
            Assert.DoesNotContain("Hulk", PickNames(PoolNamed(loot, "RandomShipBrokerOKLG")));
        });
    }

    [SkippableFact]
    public void Two_starting_ships_share_the_one_pool_the_career_rolls()
    {
        var g = TestData.RequireGame();
        InTempDir(dest =>
        {
            var cat = SyntheticCatalog();
            var start = ShipDelivery.None with
            {
                StartingShip = true, StartingShipWeight = 0.16, StartingShipStation = "OKLG",
                StartingShipMortgage = 100000,
            };
            var result = BundleExport.Write(cat, NoSpecs, new BundleOptions(
                "My Pack", "Tester", "", "1.0.0", "1.0.0.13", dest,
                [Ship(cat, "Kestrel", start), Ship(cat, "Harrier", start)]), g.Index);

            var loot = Path.Combine(result.ModDir, "data", "loot", "loot.json");
            var events = PoolNamed(loot, StartingShipExport.ShipEventsPool);   // exactly one, or one ship is lost
            var names = PickNames(events);
            Assert.Contains("CGEncKestrelIntro", names);
            Assert.Contains("CGEncHarrierIntro", names);
            // whatever starts the install already offers are still offered: which they are depends on the game
            // version and on the mods loaded, so the assertion is that one of them survived, not which
            Assert.Contains(names, n => n is not ("CGEncKestrelIntro" or "CGEncHarrierIntro"));

            // each ship keeps its own chargen chain, and both are written
            foreach (var token in new[] { "Kestrel", "Harrier" })
            {
                Assert.Single(Objects(loot), o => o["strName"]!.GetValue<string>() == $"CGEnc{token}Reward");
                Assert.Contains(Objects(Path.Combine(result.ModDir, "data", "lifeevents", "lifeevents.json")),
                    o => o["strName"]!.GetValue<string>() == $"CGEnc{token}Take");
                Assert.Contains(Objects(Path.Combine(result.ModDir, "data", "interactions", "interactions.json")),
                    o => o["strName"]!.GetValue<string>() == $"CGEnc{token}Intro");
            }
        });
    }

    /// <summary>
    /// "Guaranteed start" pins the pool the career rolls. With one ship that means only that ship; with a bundle
    /// it can only mean this mod's ships, which is why the flag belongs to the mod rather than to a ship.
    /// </summary>
    [SkippableFact]
    public void A_guaranteed_start_pins_the_pool_to_the_bundles_own_ships()
    {
        var g = TestData.RequireGame();
        InTempDir(dest =>
        {
            var cat = SyntheticCatalog();
            var start = ShipDelivery.None with { StartingShip = true, StartingShipWeight = 0.5, StartingShipStation = "OKLG" };
            var result = BundleExport.Write(cat, NoSpecs, new BundleOptions(
                "My Pack", "Tester", "", "1.0.0", "1.0.0.13", dest,
                [Ship(cat, "Kestrel", start), Ship(cat, "Harrier", start)],
                ExclusiveStart: true), g.Index);

            var names = PickNames(PoolNamed(
                Path.Combine(result.ModDir, "data", "loot", "loot.json"), StartingShipExport.ShipEventsPool));

            Assert.Equal(["CGEncKestrelIntro", "CGEncHarrierIntro"], names);   // both of ours, and nothing else
        });
    }

    /// <summary>
    /// The file a pack writes has to come back through the loader as several ships, not one. It is one array with
    /// an element per ship, which is how the game reads every data file, and the round trip through
    /// <see cref="ShipTemplate.ParseFile"/> is the loader's own path.
    /// </summary>
    [SkippableFact]
    public void The_ships_file_a_pack_writes_parses_back_as_every_ship_in_it()
    {
        var g = TestData.RequireGame();
        InTempDir(dest =>
        {
            var cat = SyntheticCatalog();
            var kiosk = ShipDelivery.None with { BrokerPools = ["RandomShipBrokerOKLG"], BrokerWeight = 0.05 };
            var result = BundleExport.Write(cat, NoSpecs, new BundleOptions(
                "My Pack", "Tester", "", "1.0.0", "1.0.0.13", dest,
                [Ship(cat, "Kestrel", kiosk), Ship(cat, "Harrier", kiosk), Ship(cat, "Barge", kiosk)]), g.Index);

            var parsed = ShipTemplate.ParseFile(File.ReadAllText(result.ShipJsonPath)).ToList();

            Assert.Equal(["Kestrel", "Harrier", "Barge"], parsed.Select(t => t.Name));
            Assert.All(parsed, t => Assert.Equal(ShipExport.VariedNames, t.PublicName));
        });
    }

    [SkippableFact]
    public void A_single_ship_export_still_writes_what_it_always_did()
    {
        // The one-ship path now runs through the bundle writer. This is the guard that folding them together did
        // not change what a plain export produces.
        var g = TestData.RequireGame();
        InTempDir(dest =>
        {
            var cat = SyntheticCatalog();
            var doc = Fixtures.Doc(cat, Fixtures.P("Floor", 0, 0));
            var delivery = new ShipDelivery(["RandomShipBrokerOKLG"], 0.05, [], true, 0.16, "OKLG", 500000, "T", "D");
            var opts = new ExportOptions("Solo", "Tester", "", "1.0.0", "1.0.0.13", dest, "The Wanderer",
                Delivery: delivery);

            var result = ShipExport.Write(doc, cat, NoSpecs, opts, g.Index);

            Assert.EndsWith(Path.Combine("data", "ships", "Solo.json"), result.ShipJsonPath);
            var ship = Assert.Single(JsonDocument.Parse(File.ReadAllText(result.ShipJsonPath))
                .RootElement.EnumerateArray().ToList());
            Assert.Equal("Solo", ship.GetProperty("strName").GetString());
            Assert.Equal("The Wanderer", ship.GetProperty("publicName").GetString());

            Assert.True(result.TouchedLootPools);
            var loot = Path.Combine(result.ModDir, "data", "loot", "loot.json");
            Assert.Contains("Solo", PickNames(PoolNamed(loot, "RandomShipBrokerOKLG")));
            Assert.Contains("CGEncSoloIntro", PickNames(PoolNamed(loot, StartingShipExport.ShipEventsPool)));
        });
    }
}
