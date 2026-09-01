using System.IO;
using System.Text.Json;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The <c>.oplanmod</c> document: what it round-trips, what it deliberately does not carry, and how it finds the
/// designs it points at.
/// </summary>
public class BundleFileTests
{
    private static void InTempDir(Action<string> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "OstraplanBundleFile_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try { body(dir); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static BundleFile Sample() => new()
    {
        Mod = new BundleModMeta { Name = "My Pack", Author = "Valtora", Version = "1.2.0", ExclusiveStart = true },
        Ships =
        [
            new BundleEntry
            {
                Path = "Kestrel.oplan",
                NameOverride = "Kestrel Mk2",
                Replaces = null,
                Wear = new BundleWear { On = true, Target = 0.88 },
                Delivery = new DeliveryPlan
                {
                    BrokerPools = ["RandomShipBrokerOKLG"], BrokerWeight = 0.05,
                    StartingShip = true, StartStation = "BCER", StartMortgage = 250000,
                },
            },
        ],
        LastWritten = ["Kestrel"],
    };

    [Fact]
    public void A_pack_round_trips_through_the_file()
    {
        InTempDir(dir =>
        {
            var path = Path.Combine(dir, "pack.oplanmod");
            Sample().Save(path);
            var read = BundleFile.Load(path);

            Assert.Equal("My Pack", read.Mod.Name);
            Assert.Equal("1.2.0", read.Mod.Version);
            Assert.True(read.Mod.ExclusiveStart);
            Assert.Equal(["Kestrel"], read.LastWritten);

            var ship = Assert.Single(read.Ships);
            Assert.Equal("Kestrel.oplan", ship.Path);
            Assert.Equal("Kestrel Mk2", ship.NameOverride);
            Assert.True(ship.Wear.On);
            Assert.Equal(0.88, ship.Wear.Target, 6);
            Assert.Equal(["RandomShipBrokerOKLG"], ship.Delivery.BrokerPools);
            Assert.Equal(0.05, ship.Delivery.BrokerWeight!.Value, 6);
            Assert.True(ship.Delivery.StartingShip);
            Assert.Equal("BCER", ship.Delivery.StartStation);
            Assert.Equal(250000, ship.Delivery.StartMortgage, 6);
        });
    }

    /// <summary>A pack written by a later build has to survive an older one, the same way an <c>.oplan</c> does:
    /// what this build does not understand is kept and written back rather than dropped.</summary>
    [Fact]
    public void Fields_this_build_does_not_know_survive_a_round_trip()
    {
        InTempDir(dir =>
        {
            var path = Path.Combine(dir, "pack.oplanmod");
            File.WriteAllText(path, """
                {
                  "formatVersion": 1,
                  "mod": { "name": "Future Pack", "somethingNew": 42 },
                  "ships": [ { "path": "a.oplan", "aRouteFromLater": true } ],
                  "topLevelNovelty": "kept"
                }
                """);

            var read = BundleFile.Load(path);
            read.Save(path);

            var json = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
            Assert.Equal("kept", json.GetProperty("topLevelNovelty").GetString());
            Assert.Equal(42, json.GetProperty("mod").GetProperty("somethingNew").GetInt32());
            Assert.True(json.GetProperty("ships")[0].GetProperty("aRouteFromLater").GetBoolean());
        });
    }

    [Fact]
    public void A_file_from_a_later_format_is_refused_rather_than_half_read()
    {
        InTempDir(dir =>
        {
            var path = Path.Combine(dir, "pack.oplanmod");
            File.WriteAllText(path, """{ "formatVersion": 99, "ships": [] }""");

            Assert.Throws<InvalidDataException>(() => BundleFile.Load(path));
        });
    }

    /// <summary>A pack and its designs in one folder can be moved or shared whole, which only works if the paths
    /// in it are relative to the pack.</summary>
    [Fact]
    public void A_design_beside_the_pack_is_stored_relative_and_found_again()
    {
        InTempDir(dir =>
        {
            var bundle = Path.Combine(dir, "pack.oplanmod");
            var design = Path.Combine(dir, "ships", "Kestrel.oplan");

            var stored = BundleFile.StoreDesignPath(bundle, design);
            Assert.False(Path.IsPathRooted(stored));
            Assert.Equal(Path.Combine("ships", "Kestrel.oplan"), stored);
            Assert.Equal(design, BundleFile.ResolveDesignPath(bundle, stored));

            // and a pack that has been moved with its designs still finds them
            var moved = Path.Combine(dir, "elsewhere", "pack.oplanmod");
            Assert.Equal(Path.Combine(dir, "elsewhere", "ships", "Kestrel.oplan"),
                BundleFile.ResolveDesignPath(moved, stored));
        });
    }

    [Fact]
    public void An_absolute_path_is_kept_as_one()
    {
        InTempDir(dir =>
        {
            var bundle = Path.Combine(dir, "pack.oplanmod");
            var elsewhere = Path.Combine(Path.GetTempPath(), "SomewhereElse", "Kestrel.oplan");

            Assert.Equal(elsewhere, BundleFile.ResolveDesignPath(bundle, elsewhere));
        });
    }

    /// <summary>Where the mod is written is a fact about this machine, not about the pack, so it is not in here.
    /// A shared pack must carry no folder of yours, the same rule the <c>.oplan</c> follows.</summary>
    [Fact]
    public void The_pack_carries_no_paths_of_your_own()
    {
        InTempDir(dir =>
        {
            var path = Path.Combine(dir, "pack.oplanmod");
            Sample().Save(path);

            var json = File.ReadAllText(path);
            Assert.DoesNotContain("Mods", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("stagedIntoMods", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ostrasort", json, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>A derived answer is not a stored one. Writing it would put a second copy of something the routes
    /// already say into a file people hand-edit, where the two could disagree.</summary>
    [Fact]
    public void Nothing_derived_from_the_routes_is_written_beside_them()
    {
        InTempDir(dir =>
        {
            var path = Path.Combine(dir, "pack.oplanmod");
            Sample().Save(path);

            var json = File.ReadAllText(path);
            Assert.DoesNotContain("anyRoute", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("derelictOnly", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("startWeight", json, StringComparison.OrdinalIgnoreCase);
        });
    }
}
