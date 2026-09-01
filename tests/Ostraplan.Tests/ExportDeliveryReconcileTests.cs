using System.IO;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// What an export leaves on disk for a delivery that has been <b>taken away</b>, and how it knows which names in
/// the game's loot pools are its own to remove. Game-free: the sweep is file work, and the ownership rules are
/// arithmetic over names.
///
/// <para>Both exist for the same defect. A mod's delivery files are written by the mod and read whole, and the
/// pools they contain are cloned from the <i>effective</i> data — which, once the mod is registered, already
/// carries the mod's own last write. So an export that stopped writing a route, or renamed the ship, used to
/// leave the game still able to roll a ship the mod no longer contained.</para>
/// </summary>
public class ExportDeliveryReconcileTests
{
    private static readonly IReadOnlyList<RoomSpecDef> NoSpecs = [];

    private static ShipDocument OnePartDesign(out Catalog catalog)
    {
        catalog = new Fixtures().Floor("Floor").Build();
        return Fixtures.Doc(catalog, Fixtures.P("Floor", 0, 0));
    }

    private static string PlantFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void A_route_taken_away_deletes_the_files_the_previous_export_wrote()
    {
        var dest = Path.Combine(Path.GetTempPath(), "OstraplanSweepTest_" + Guid.NewGuid().ToString("N")[..8]);
        var modDir = Path.Combine(dest, "My Ship");
        try
        {
            // what an export WITH a kiosk and a Shipbreaker start left here last time
            var loot = PlantFile(Path.Combine(modDir, "data", "loot", "loot.json"), "[]");
            var life = PlantFile(Path.Combine(modDir, "data", "lifeevents", "lifeevents.json"), "[]");
            var inter = PlantFile(Path.Combine(modDir, "data", "interactions", "interactions.json"), "[]");
            // and something of the user's own, which is not this export's to touch
            var theirs = PlantFile(Path.Combine(modDir, "README.txt"), "mine");

            var doc = OnePartDesign(out var catalog);
            var opts = new ExportOptions("My Ship", "Tester", "", "1.0.0", "1.0.0.13", dest, "");
            var result = ShipExport.Write(doc, catalog, NoSpecs, opts);

            Assert.False(result.TouchedLootPools);
            Assert.False(File.Exists(loot));
            Assert.False(File.Exists(life));
            Assert.False(File.Exists(inter));
            // the folders go with them: an empty data/loot is not a state any export produces
            Assert.False(Directory.Exists(Path.GetDirectoryName(loot)!));
            Assert.False(Directory.Exists(Path.GetDirectoryName(life)!));

            Assert.True(File.Exists(theirs));
            Assert.True(File.Exists(result.ShipJsonPath));
        }
        finally
        {
            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
        }
    }

    [Fact]
    public void An_export_with_no_delivery_files_to_sweep_writes_the_mod_as_before()
    {
        var dest = Path.Combine(Path.GetTempPath(), "OstraplanSweepTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dest);
        try
        {
            var doc = OnePartDesign(out var catalog);
            var opts = new ExportOptions("My Ship", "Tester", "", "1.0.0", "1.0.0.13", dest, "");
            var result = ShipExport.Write(doc, catalog, NoSpecs, opts);

            Assert.True(File.Exists(result.ShipJsonPath));
            Assert.True(File.Exists(result.ModInfoPath));
            Assert.False(Directory.Exists(Path.Combine(result.ModDir, "data", "loot")));
        }
        finally
        {
            Directory.Delete(dest, recursive: true);
        }
    }

    // ---- which names the export may take back out ----

    [Fact]
    public void The_ship_being_written_and_the_one_it_replaces_on_disk_are_both_owned()
    {
        // renamed since the last export: the old name is still in the pools, under this mod's hand
        var owned = ShipExport.OwnedShipNames(["Kestrel"], "Kestrel Mk2", replaceTarget: null);

        Assert.Contains("Kestrel", owned);
        Assert.Contains("Kestrel Mk2", owned);
        Assert.Equal(2, owned.Count);
    }

    /// <summary>
    /// A replacement's <c>strName</c> is a core ship's, which core's own pools legitimately list. Stripping it
    /// would take a vanilla ship out of the kiosks of anyone who installed the mod.
    /// </summary>
    [Fact]
    public void A_replacement_owns_neither_the_ship_it_replaces_nor_that_name_from_a_previous_export()
    {
        Assert.Empty(ShipExport.OwnedShipNames([], "Vagabond+", replaceTarget: "Vagabond+"));
        Assert.Empty(ShipExport.OwnedShipNames(["Vagabond+"], "Vagabond+", replaceTarget: "Vagabond+"));

        // a mod that used to add "Kestrel" and now replaces "Vagabond+" still owns the name it invented
        var owned = ShipExport.OwnedShipNames(["Kestrel"], "Vagabond+", replaceTarget: "Vagabond+");
        Assert.Equal(["Kestrel"], owned);
    }

    [Fact]
    public void Ship_names_are_read_back_from_the_file_the_export_is_about_to_overwrite()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OstraplanNamesTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "ships.json");

            Assert.Empty(ShipExport.ReadShipNames(path));   // no file: nothing is known, which is not an error

            File.WriteAllText(path, """[{"strName":"Kestrel"},{"strName":"Harrier"}]""");
            Assert.Equal(["Kestrel", "Harrier"], ShipExport.ReadShipNames(path));

            File.WriteAllText(path, "not json at all");
            Assert.Empty(ShipExport.ReadShipNames(path));   // unreadable tells us nothing, same as absent
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
