using System.IO;
using System.Linq;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// Validates the ship-template loader's coordinate model against baked ground truth,
/// isolated from rooms/rotation: walls are 1×1, so a correct centre→top-left→tile
/// translation makes the solid-wall tiles exactly the complement of the stored
/// room-tile union (walls belong to no room). All tests no-op without the install.
/// </summary>
public class TemplateLoaderTests
{
    private static ShipTemplate? LoadShip(string file, out (GameEnv Env, DataIndex Index, Catalog Catalog)? game)
    {
        game = TestData.Game;
        if (game is not { } g) return null;
        var path = Path.Combine(g.Env.CoreDataDir, "ships", file);
        return File.Exists(path)
            ? ShipTemplate.ParseFile(File.ReadAllText(path)).FirstOrDefault(t => t.Rooms.Count > 0)
            : null;
    }

    [Fact]
    public void Babak_grid_dims_and_part_count()
    {
        if (LoadShip("Babak.json", out var game) is not { } tmpl || game is not { } g) return;
        Assert.Equal(37, tmpl.NCols);
        Assert.Equal(95, tmpl.NRows);

        var grid = ShipGrid.FromTemplate(tmpl, new PartResolver(g.Index), g.Catalog);
        Assert.True(grid.Parts.Count > 4000, $"only {grid.Parts.Count} of {tmpl.Items.Count} items resolved");
    }

    [Fact]
    public void Babak_solid_walls_are_the_room_complement()
    {
        if (LoadShip("Babak.json", out var game) is not { } tmpl || game is not { } g) return;
        var warnings = new List<string>();
        var grid = ShipGrid.FromTemplate(tmpl, new PartResolver(g.Index), g.Catalog, warnings);
        Assert.True(warnings.Count == 0,
            $"{warnings.Count} unresolved items, e.g.: {string.Join(" | ", warnings.Take(6))}");

        var storedRoomTiles = tmpl.Rooms.SelectMany(r => r.TileIndices).ToHashSet();
        var nonRoom = Enumerable.Range(0, grid.TileCount).Where(i => !storedRoomTiles.Contains(i)).ToHashSet();
        var solidWalls = Enumerable.Range(0, grid.TileCount)
            .Where(i => grid.Has(i, "IsWall") && !grid.Has(i, "IsPortal")).ToHashSet();

        // tiles the game excludes from every room but I don't see as solid walls
        var missing = nonRoom.Where(i => !solidWalls.Contains(i)).ToList();
        string Describe(int i)
        {
            var c = grid.CondsAt(i);
            var conds = c is null ? "(empty)" : string.Join("+", c.Keys);
            return $"[{grid.Col(i)},{grid.Row(i)}]#{i} {conds}";
        }
        Assert.True(missing.Count == 0,
            $"solid walls {solidWalls.Count} vs non-room {nonRoom.Count}; {missing.Count} non-room tiles are not solid walls: "
            + string.Join("  ", missing.Take(12).Select(Describe)));

        // and no stored room tile should be a solid wall
        var wallsInRooms = storedRoomTiles.Where(i => solidWalls.Contains(i)).ToList();
        Assert.True(wallsInRooms.Count == 0,
            $"{wallsInRooms.Count} stored room tiles are solid walls: " + string.Join("  ", wallsInRooms.Take(12).Select(Describe)));
    }
}
