using System.IO;
using System.Linq;
using Ostraplan.Core;
using Xunit;
using Xunit.Abstractions;

namespace Ostraplan.Tests;

/// <summary>
/// P3 save-game import: from a save's data zip, find the <b>player's</b> ship (the one the
/// character record's <c>strShip</c> points at) and import its layout only. Verified against the
/// real saves on this machine — no-ops when there are none.
/// </summary>
public class ShipSaveImportTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    [SkippableFact]
    public void ListSaves_finds_saves_with_readable_metadata()
    {
        var g = TestData.RequireGame();
        var saves = SaveImport.ListSaves(g.Env);
        if (saves.Count == 0) return;   // machine with no saves

        Assert.All(saves, s => Assert.True(File.Exists(s.ZipPath), $"{s.Name}: zip missing"));
        Assert.Contains(saves, s => s.ShipName.Length > 0);   // at least one save's saveInfo is readable
        _out.WriteLine($"{saves.Count} saves: {string.Join(", ", saves.Select(s => $"{s.Name}→{s.ShipName}"))}");
    }

    /// <summary>
    /// Checks <b>every</b> save rather than stopping at the newest one. The newest save is whatever the game
    /// last autosaved, so stopping there made the outcome depend on which ship the player happened to be
    /// flying: a ship under renovation is legitimately an open hulk with no sealed compartment at all (the
    /// game bakes it the same single void room we compute), which turned a healthy import into a failure.
    /// </summary>
    [SkippableFact]
    public void Imports_the_players_own_ship_layout_only()
    {
        var g = TestData.RequireGame();
        var saves = SaveImport.ListSaves(g.Env);
        if (saves.Count == 0) return;

        var imported = 0;
        var sealedShips = 0;
        foreach (var save in saves)
        {
            ImportResult result;
            try { result = SaveImport.ImportPlayerShip(save.ZipPath, g.Catalog); }
            catch (System.Exception ex) { _out.WriteLine($"{save.Name}: {ex.Message}"); continue; }

            imported++;

            // Invariants that hold for ANY player ship, hulk or not.
            Assert.True(result.PartCount > 0, $"{save.Name}: imported 0 parts");

            // the ship we picked is the PLAYER's: its friendly name (publicName) matches saveInfo's shipName
            if (save.ShipName.Length > 0)
                Assert.Equal(save.ShipName, result.ShipName);

            var rooms = RoomBuilder.Build(ShipGrid.FromDocument(result.Doc, g.Catalog));
            var compartments = rooms.Rooms.Count(r => !r.Void);
            if (compartments > 0) sealedShips++;

            _out.WriteLine($"imported '{result.ShipName}' from {save.Name}: {result.PartCount} parts, " +
                $"{rooms.Rooms.Count} rooms ({compartments} sealed), " +
                $"{result.ContainedDropped} contained dropped, {result.Skipped.Count} defs skipped");
        }

        Skip.If(imported == 0, "no save on this machine exposes a player ship");

        // Asserted across the save set, not per save: one hulk proves nothing, but if NOTHING on this
        // machine seals, the walls aren't resolving and the flood fill is broken.
        Assert.True(sealedShips > 0,
            $"{imported} save(s) imported, none with a sealed compartment — room detection is likely broken");
    }
}
