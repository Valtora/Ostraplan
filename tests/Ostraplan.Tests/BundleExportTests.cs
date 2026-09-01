using System.IO;
using System.Text.Json;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// A mod carrying more than one ship: what the folder ends up holding, the collisions that only exist once there
/// is more than one design in it, and what a re-export takes back out. Game-free, apart from the loot merge, which
/// needs pools to clone and is covered against real data in <see cref="BundleDeliveryTests"/>.
/// </summary>
public class BundleExportTests
{
    private static readonly IReadOnlyList<RoomSpecDef> NoSpecs = [];

    private static Catalog Cat() => new Fixtures().Floor("Floor").Build();

    private static BundleShip Ship(Catalog cat, string name, ShipDelivery? delivery = null, string? replaces = null) =>
        new(Fixtures.Doc(cat, Fixtures.P("Floor", 0, 0)), name, new ExportMetadata(), Delivery: delivery,
            ReplaceTarget: replaces);

    private static ShipPreview Art() => new([1, 2, 3], [new ShipPreviewRoom("Bunk", [4, 5, 6])]);

    private static BundleOptions Options(string dest, params BundleShip[] ships) =>
        new("My Pack", "Tester", "", "1.0.0", "1.0.0.13", dest, ships);

    private static void InTempDir(Action<string> body)
    {
        var dest = Path.Combine(Path.GetTempPath(), "OstraplanBundleTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dest);
        try { body(dest); }
        finally { if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true); }
    }

    private static IReadOnlyList<string> ShipNamesIn(string path) =>
        [.. JsonDocument.Parse(File.ReadAllText(path)).RootElement.EnumerateArray()
            .Select(e => e.GetProperty("strName").GetString()!)];

    // ---- what a bundle writes ----

    [Fact]
    public void Every_ship_lands_in_one_data_ships_file()
    {
        InTempDir(dest =>
        {
            var cat = Cat();
            var result = BundleExport.Write(cat, NoSpecs, Options(dest, Ship(cat, "Kestrel"), Ship(cat, "Harrier")));

            // one file, keyed per element by its own strName: that is how the game reads a data file
            Assert.EndsWith(Path.Combine("data", "ships", "My Pack.json"), result.ShipJsonPath);
            Assert.Equal(["Kestrel", "Harrier"], ShipNamesIn(result.ShipJsonPath));
            Assert.Single(Directory.EnumerateFiles(Path.Combine(result.ModDir, "data", "ships")));

            // the mod is one mod, whatever it carries
            var modInfo = JsonDocument.Parse(File.ReadAllText(result.ModInfoPath)).RootElement;
            Assert.Equal("My Pack", Assert.Single(modInfo.EnumerateArray().ToList()).GetProperty("strName").GetString());
            Assert.Equal(2, result.Ships.Count);
        });
    }

    /// <summary>Art is filed under the ship's own <c>strName</c> and nothing else, which is the only place the
    /// game looks for it, so two ships in one mod never contend for a folder.</summary>
    [Fact]
    public void Each_ship_gets_its_own_preview_folder()
    {
        InTempDir(dest =>
        {
            var cat = Cat();
            var ships = new[]
            {
                Ship(cat, "Kestrel") with { Preview = Art() },
                Ship(cat, "Harrier") with { Preview = Art() },
            };
            var result = BundleExport.Write(cat, NoSpecs, Options(dest, ships));

            foreach (var name in new[] { "Kestrel", "Harrier" })
            {
                var dir = Path.Combine(result.ModDir, "images", "ships", name);
                Assert.True(File.Exists(Path.Combine(dir, name + ".png")));
                Assert.True(File.Exists(Path.Combine(dir, "Bunk.png")));
            }
            Assert.All(result.Ships, s => Assert.Equal(2, s.PreviewCount));
        });
    }

    // ---- what a re-export takes back out ----

    [Fact]
    public void A_ship_dropped_from_the_bundle_loses_its_preview_folder()
    {
        InTempDir(dest =>
        {
            var cat = Cat();
            var first = BundleExport.Write(cat, NoSpecs, Options(dest,
                Ship(cat, "Kestrel") with { Preview = Art() },
                Ship(cat, "Harrier") with { Preview = Art() }));

            var dropped = Path.Combine(first.ModDir, "images", "ships", "Harrier");
            Assert.True(Directory.Exists(dropped));

            // the same mod, one ship lighter. Its own ships file names both, so nothing has to be remembered.
            var second = BundleExport.Write(cat, NoSpecs, Options(dest, Ship(cat, "Kestrel") with { Preview = Art() }));

            Assert.Equal(["Harrier"], second.RemovedArt);
            Assert.False(Directory.Exists(dropped));
            Assert.True(Directory.Exists(Path.Combine(second.ModDir, "images", "ships", "Kestrel")));
            Assert.Equal(["Kestrel"], ShipNamesIn(second.ShipJsonPath));
        });
    }

    /// <summary>A folder someone put there themselves cannot be told from one of ours by looking, so only a name
    /// the mod is known to have written is ever swept.</summary>
    [Fact]
    public void Art_the_mod_never_wrote_is_left_alone()
    {
        InTempDir(dest =>
        {
            var cat = Cat();
            var result = BundleExport.Write(cat, NoSpecs, Options(dest, Ship(cat, "Kestrel") with { Preview = Art() }));

            var theirs = Path.Combine(result.ModDir, "images", "ships", "SomeoneElsesShip");
            Directory.CreateDirectory(theirs);
            File.WriteAllBytes(Path.Combine(theirs, "SomeoneElsesShip.png"), [9]);

            var again = BundleExport.Write(cat, NoSpecs, Options(dest, Ship(cat, "Kestrel") with { Preview = Art() }));

            Assert.Empty(again.RemovedArt);
            Assert.True(File.Exists(Path.Combine(theirs, "SomeoneElsesShip.png")));
        });
    }

    // ---- a failure leaves the previous export alone ----

    /// <summary>
    /// Nothing reaches the mod folder until every design has been built and every file assembled, so a failure
    /// leaves the export that is already installed exactly as it was rather than half of a new one the game would
    /// load. A room thumbnail whose name cannot be a file name is the cheapest way to make the write throw.
    /// </summary>
    [Fact]
    public void A_write_that_fails_leaves_the_previous_export_untouched_and_no_staging_behind()
    {
        InTempDir(dest =>
        {
            var cat = Cat();
            var good = BundleExport.Write(cat, NoSpecs, Options(dest, Ship(cat, "Kestrel") with { Preview = Art() }));
            var before = File.ReadAllText(good.ShipJsonPath);

            var poisoned = new ShipPreview([1, 2, 3], [new ShipPreviewRoom("bad|name", [4, 5, 6])]);
            Assert.ThrowsAny<Exception>(() => BundleExport.Write(cat, NoSpecs, Options(dest,
                Ship(cat, "Kestrel") with { Preview = Art() },
                Ship(cat, "Harrier") with { Preview = poisoned })));

            Assert.Equal(before, File.ReadAllText(good.ShipJsonPath));   // still the one-ship export
            Assert.False(Directory.Exists(Path.Combine(good.ModDir, "images", "ships", "Harrier")));
            Assert.Empty(Directory.EnumerateDirectories(dest, "*.ostraplan-staging"));
        });
    }

    [Fact]
    public void A_first_export_that_fails_leaves_no_mod_folder_at_all()
    {
        InTempDir(dest =>
        {
            var cat = Cat();
            var poisoned = new ShipPreview([1, 2, 3], [new ShipPreviewRoom("bad|name", [4, 5, 6])]);

            Assert.ThrowsAny<Exception>(() => BundleExport.Write(cat, NoSpecs,
                Options(dest, Ship(cat, "Kestrel") with { Preview = poisoned })));

            Assert.Empty(Directory.EnumerateFileSystemEntries(dest));
        });
    }

    // ---- the collisions a second ship makes possible ----

    [Fact]
    public void Two_ships_of_one_name_are_refused_because_the_game_keys_everything_on_it()
    {
        var cat = Cat();
        var problems = BundleExport.Validate(Options("", Ship(cat, "Kestrel"), Ship(cat, "kestrel")));

        Assert.Contains(problems, p => p.Contains("Kestrel", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A design named after the ship another member replaces is the same collision wearing a different
    /// hat: both resolve to one <c>strName</c>.</summary>
    [Fact]
    public void A_ship_named_after_another_members_replacement_target_is_the_same_collision()
    {
        var cat = Cat();
        var problems = BundleExport.Validate(Options("",
            Ship(cat, "My Refit", replaces: "Vagabond+"), Ship(cat, "Vagabond+")));

        Assert.NotEmpty(problems);
    }

    [Fact]
    public void Two_ships_replacing_one_existing_ship_are_refused()
    {
        var cat = Cat();
        var problems = BundleExport.Validate(Options("",
            Ship(cat, "Refit A", replaces: "Vagabond+"), Ship(cat, "Refit B", replaces: "Vagabond+")));

        Assert.Contains(problems, p => p.Contains("both replace"));
    }

    /// <summary>A Special Offer pool is one pinned ship at weight 1, so unlike a kiosk's weighted stock there is
    /// no merge to make and a second claimant would simply overwrite the first.</summary>
    [Fact]
    public void Two_ships_in_one_Special_Offer_slot_are_refused()
    {
        var cat = Cat();
        var offer = ShipDelivery.None with { SpecialOfferPools = ["RandomShipBrokerSpecialOffer"] };
        var problems = BundleExport.Validate(Options("",
            Ship(cat, "Kestrel", offer), Ship(cat, "Harrier", offer)));

        Assert.Contains(problems, p => p.Contains("Special Offer"));
    }

    [Fact]
    public void Two_ships_in_different_Special_Offer_slots_are_fine()
    {
        var cat = Cat();
        var problems = BundleExport.Validate(Options("",
            Ship(cat, "Kestrel", ShipDelivery.None with { SpecialOfferPools = ["RandomShipBrokerSpecialOffer"] }),
            Ship(cat, "Harrier", ShipDelivery.None with { SpecialOfferPools = ["RandomShipBrokerSpecialOfferVORB"] })));

        Assert.Empty(problems);
    }

    [Fact]
    public void An_apartment_and_an_empty_design_are_refused()
    {
        var cat = Cat();
        var residence = Ship(cat, "Home");
        residence.Doc.Kind = DocumentKind.Residence;
        var empty = new BundleShip(new ShipDocument(cat), "Nothing", new ExportMetadata());

        var problems = BundleExport.Validate(Options("", residence, empty));

        Assert.Contains(problems, p => p.Contains("apartment"));
        Assert.Contains(problems, p => p.Contains("no parts"));
    }

    [Fact]
    public void A_mod_with_no_ships_in_it_is_refused() =>
        Assert.NotEmpty(BundleExport.Validate(Options("")));

    [Fact]
    public void A_bundle_that_would_be_refused_never_reaches_the_disk()
    {
        InTempDir(dest =>
        {
            var cat = Cat();
            Assert.Throws<ArgumentException>(() =>
                BundleExport.Write(cat, NoSpecs, Options(dest, Ship(cat, "Kestrel"), Ship(cat, "Kestrel"))));

            Assert.Empty(Directory.EnumerateFileSystemEntries(dest));
        });
    }
}
