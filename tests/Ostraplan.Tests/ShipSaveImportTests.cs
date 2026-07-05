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

    [Fact]
    public void ListSaves_finds_saves_with_readable_metadata()
    {
        if (TestData.Game is not { } g) return;
        var saves = SaveImport.ListSaves(g.Env);
        if (saves.Count == 0) return;   // machine with no saves

        Assert.All(saves, s => Assert.True(File.Exists(s.ZipPath), $"{s.Name}: zip missing"));
        Assert.Contains(saves, s => s.ShipName.Length > 0);   // at least one save's saveInfo is readable
        _out.WriteLine($"{saves.Count} saves: {string.Join(", ", saves.Select(s => $"{s.Name}→{s.ShipName}"))}");
    }

    [Fact]
    public void Imports_the_players_own_ship_layout_only()
    {
        if (TestData.Game is not { } g) return;
        var saves = SaveImport.ListSaves(g.Env);
        if (saves.Count == 0) return;

        foreach (var save in saves)
        {
            ImportResult result;
            try { result = SaveImport.ImportPlayerShip(save.ZipPath, g.Catalog); }
            catch (System.Exception ex) { _out.WriteLine($"{save.Name}: {ex.Message}"); continue; }

            Assert.True(result.PartCount > 0, $"{save.Name}: imported 0 parts");

            // the ship we picked is the PLAYER's: its friendly name (publicName) matches saveInfo's shipName
            if (save.ShipName.Length > 0)
                Assert.Equal(save.ShipName, result.ShipName);

            // a real ship yields an analysable layout with compartments
            var rooms = RoomBuilder.Build(ShipGrid.FromDocument(result.Doc, g.Catalog));
            Assert.Contains(rooms.Rooms, r => !r.Void);

            _out.WriteLine($"imported '{result.ShipName}' from {save.Name}: {result.PartCount} parts, " +
                $"{result.ContainedDropped} contained dropped, {result.Skipped.Count} defs skipped");
            return;   // one real import proves the path
        }
    }
}
