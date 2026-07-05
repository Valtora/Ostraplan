using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The P3 export Law gate: a design exported to the game's JsonShip shape must, when the game
/// re-parses it and recomputes rooms/rating on full load, reproduce exactly what Ostraplan
/// baked — otherwise the broker rating would visibly change on load. Proven by round-tripping
/// through the real loader path: <c>doc → ShipExport → ShipTemplate.Parse → ShipGrid.FromTemplate</c>
/// must rebuild the same tiles, the same room partition, and the same rating. No-ops without the install.
/// </summary>
public class ShipExportTests
{
    private static bool Ready((GameEnv, DataIndex, Catalog)? g) =>
        g is { } gg && gg.Item3.ByDefName.ContainsKey("ItmWall1x1") && gg.Item3.ByDefName.ContainsKey("ItmFloorGrate01");

    private static void Place(ShipDocument doc, string def, int x, int y, int rot = 0) =>
        new PlaceCommand(new Placement { DefName = def, X = x, Y = y, Rot = rot }).Do(doc);

    /// <summary>A 5×7 hull split by a full-width door into two floored compartments — walls, floor and a door.</summary>
    private static ShipDocument BuildDooredHull(Catalog catalog)
    {
        var doc = new ShipDocument(catalog);
        for (var x = 0; x < 5; x++) { Place(doc, "ItmWall1x1", x, 0); Place(doc, "ItmWall1x1", x, 6); }
        for (var y = 1; y <= 5; y++)
        {
            Place(doc, "ItmWall1x1", 0, y); Place(doc, "ItmWall1x1", 4, y);
            for (var x = 1; x < 4; x++) Place(doc, "ItmFloorGrate01", x, y);
        }
        if (catalog.ByDefName.ContainsKey("ItmDoor01Open")) Place(doc, "ItmDoor01Open", 0, 3);
        return doc;
    }

    /// <summary>Re-parse an exported ship string and rebuild the analysis grid + room partition through the loader.</summary>
    private static (ShipGrid Grid, RoomPartition Rooms, ShipTemplate Tmpl) Reload(
        string json, (GameEnv Env, DataIndex Index, Catalog Catalog) g, IReadOnlyList<RoomSpecDef> specs)
    {
        var tmpl = Assert.Single(ShipTemplate.ParseFile(json).ToList());
        var grid = ShipGrid.FromTemplate(tmpl, new PartResolver(g.Index), g.Catalog);
        var rooms = RoomBuilder.Build(grid);
        RoomCertifier.CertifyAll(rooms, specs, g.Catalog);
        return (grid, rooms, tmpl);
    }

    /// <summary>Every tile carries the same set of conditions in both grids (same dims, same parts, same coords).</summary>
    private static void AssertSameTiles(ShipGrid a, ShipGrid b)
    {
        Assert.Equal(a.NCols, b.NCols);
        Assert.Equal(a.NRows, b.NRows);
        for (var i = 0; i < a.TileCount; i++)
        {
            var ca = a.CondsAt(i)?.Keys.OrderBy(k => k, System.StringComparer.Ordinal) ?? Enumerable.Empty<string>();
            var cb = b.CondsAt(i)?.Keys.OrderBy(k => k, System.StringComparer.Ordinal) ?? Enumerable.Empty<string>();
            Assert.True(ca.SequenceEqual(cb),
                $"tile #{i} [{a.Col(i)},{a.Row(i)}] conds differ: [{string.Join("+", a.CondsAt(i)?.Keys ?? [])}] vs [{string.Join("+", b.CondsAt(i)?.Keys ?? [])}]");
        }
    }

    [Fact]
    public void Export_roundtrips_tiles_rooms_and_rating()
    {
        if (TestData.Game is not { } g || !Ready(g)) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);
        var doc = BuildDooredHull(g.Catalog);

        // the design as Ostraplan sees it
        var grid0 = ShipGrid.FromDocument(doc, g.Catalog);
        var rooms0 = RoomBuilder.Build(grid0);
        RoomCertifier.CertifyAll(rooms0, specs, g.Catalog);

        var (ship, rating, _) = ShipExport.Build(doc, g.Catalog, specs, "Roundtrip Test");
        var (grid1, rooms1, tmpl) = Reload(ShipExport.Serialize(ship), g, specs);

        // 1. parts + coordinates survive the reverse-mapping exactly
        AssertSameTiles(grid0, grid1);
        Assert.Equal(doc.Placements.Count, ship.AItems.Length);
        Assert.All(ship.AItems, it => Assert.False(string.IsNullOrEmpty(it.StrID)));
        Assert.Equal(ship.AItems.Length, ship.AItems.Select(i => i.StrID).Distinct().Count());   // fresh, unique

        // 2. the game's recompute of our baked rooms matches the bake (no visible rating change on load)
        Assert.Null(RoomParity.Compare(grid1, rooms1, tmpl, out _));
        Assert.Equal(rooms0.Rooms.Count(r => !r.Void), rooms1.Rooms.Count(r => !r.Void));

        // 3. the baked rating equals a fresh recompute from the reloaded grid
        var reRating = Rating.Calculate(grid1, rooms1, g.Catalog);
        Assert.Equal(rating.Display, reRating.Display);
        Assert.Equal(rating.Condition, ship.ARating[1]);
        Assert.Equal(rating.RoomCount, ship.ARating[2]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Export_roundtrips_a_rotated_multitile_fixture(int rot)
    {
        if (TestData.Game is not { } g || !Ready(g)) return;
        if (!g.Catalog.ByDefName.ContainsKey("ItmBed01Off")) return;   // a 3×5 non-sheet fixture
        var specs = RoomCertifier.LoadSpecs(g.Index);

        var doc = new ShipDocument(g.Catalog);
        Place(doc, "ItmBed01Off", 4, 4, rot);   // one rotated multi-tile part is enough to exercise centre+rotation inversion

        var grid0 = ShipGrid.FromDocument(doc, g.Catalog);
        var (ship, _, _) = ShipExport.Build(doc, g.Catalog, specs, $"Bed {rot}");
        var (grid1, _, _) = Reload(ShipExport.Serialize(ship), g, specs);

        AssertSameTiles(grid0, grid1);   // wrong centre/rotation inversion would land the footprint on different tiles
        var item = Assert.Single(ship.AItems);
        Assert.Equal(GridMath.Norm(-rot), (int)item.FRotation);   // fRotation is CCW
    }

    [Fact]
    public void Write_produces_a_mod_folder_and_never_touches_loading_order()
    {
        if (TestData.Game is not { } g || !Ready(g)) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);
        var doc = BuildDooredHull(g.Catalog);

        var dest = Path.Combine(Path.GetTempPath(), "OstraplanExportTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dest);
        try
        {
            var opts = new ExportOptions("My Test Ship", "Tester", "", "1.0.0",
                g.Env.InstalledVersion ?? GameEnv.VerifiedGameVersion, dest);
            var result = ShipExport.Write(doc, g.Catalog, specs, opts);

            Assert.True(File.Exists(result.ModInfoPath));
            Assert.True(File.Exists(result.ShipJsonPath));
            Assert.EndsWith(Path.Combine("data", "ships", "My Test Ship.json"), result.ShipJsonPath);

            // the ship file parses back as one valid ship with the right grid
            var tmpl = Assert.Single(ShipTemplate.ParseFile(File.ReadAllText(result.ShipJsonPath)).ToList());
            Assert.Equal(ShipGrid.FromDocument(doc, g.Catalog).NCols, tmpl.NCols);

            // mod_info carries the name; the Law: registration is single-owner, so NO loading_order.json is written
            Assert.Contains("\"strName\"", File.ReadAllText(result.ModInfoPath));
            Assert.Empty(Directory.EnumerateFiles(dest, "loading_order.json", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(dest, recursive: true);
        }
    }
}
