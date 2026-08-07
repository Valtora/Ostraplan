using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The preview art an exported mod ships in <c>images/ships/</c>. The game's character-creation ship panel
/// (<c>GUIChargenCareer</c>) loads exactly <c>images/ships/&lt;strName&gt;/&lt;strName&gt;.png</c> and has no
/// fallback — a miss draws the red missing-texture X — so the file names here are load-bearing and keyed on the
/// ship's <c>strName</c>, never on the mod folder. Rendering needs the app's sprite atlas, so these tests hand
/// <see cref="ShipExport.Write"/> stand-in bytes and check only the filing.
/// </summary>
public class ShipPreviewExportTests
{
    private static bool Ready((GameEnv, DataIndex, Catalog)? g) =>
        g is { } gg && gg.Item3.ByDefName.ContainsKey("ItmWall1x1") && gg.Item3.ByDefName.ContainsKey("ItmFloorGrate01");

    private static ShipDocument BuildHull(Catalog catalog)
    {
        var doc = new ShipDocument(catalog);
        void Place(string def, int x, int y) =>
            new PlaceCommand(new Placement { DefName = def, X = x, Y = y }).Do(doc);

        for (var x = 0; x < 5; x++) { Place("ItmWall1x1", x, 0); Place("ItmWall1x1", x, 6); }
        for (var y = 1; y <= 5; y++)
        {
            Place("ItmWall1x1", 0, y); Place("ItmWall1x1", 4, y);
            for (var x = 1; x < 4; x++) Place("ItmFloorGrate01", x, y);
        }
        return doc;
    }

    private static byte[] Bytes(params byte[] b) => b;

    private static ExportOptions Opts(GameEnv env, string dest, string shipName, ShipPreview? preview,
        string? replaceTarget = null, string modName = "") =>
        new(shipName, "Tester", "", "1.0.0", env.InstalledVersion ?? GameEnv.VerifiedGameVersion, dest, shipName,
            ReplaceTarget: replaceTarget, ModName: modName, Preview: preview);

    private static string TempDest(string tag)
    {
        var dest = Path.Combine(Path.GetTempPath(), tag + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dest);
        return dest;
    }

    [SkippableFact]
    public void Preview_lands_under_the_ship_strName_not_the_mod_folder()
    {
        var g = TestData.RequireGame();
        if (!Ready(g)) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);
        var doc = BuildHull(g.Catalog);

        var dest = TempDest("OstraplanPreviewTest");
        try
        {
            var preview = new ShipPreview(Bytes(1, 2, 3),
                [new ShipPreviewRoom("BridgeRoom", Bytes(4)), new ShipPreviewRoom("BridgeRoom_1", Bytes(5))]);
            // a mod name deliberately unlike the ship name: the image folder must follow the SHIP, because that is
            // the only name the game ever looks the art up under
            var result = ShipExport.Write(doc, g.Catalog, specs, Opts(g.Env, dest, "My Test Ship", preview,
                modName: "Some Other Mod Name"));

            var dir = Path.Combine(result.ModDir, "images", "ships", "My Test Ship");
            Assert.True(File.Exists(Path.Combine(dir, "My Test Ship.png")));
            Assert.True(File.Exists(Path.Combine(dir, "BridgeRoom.png")));
            Assert.True(File.Exists(Path.Combine(dir, "BridgeRoom_1.png")));
            Assert.Equal(3, result.PreviewCount);
            Assert.Equal(Bytes(1, 2, 3), File.ReadAllBytes(Path.Combine(dir, "My Test Ship.png")));
            Assert.EndsWith("Some Other Mod Name", result.ModDir);
        }
        finally { Directory.Delete(dest, recursive: true); }
    }

    [SkippableFact]
    public void Replacement_preview_takes_the_replaced_ships_name()
    {
        var g = TestData.RequireGame();
        if (!Ready(g)) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);
        var doc = BuildHull(g.Catalog);

        var dest = TempDest("OstraplanPreviewReplaceTest");
        try
        {
            // a replacement is keyed to the target's strName everywhere, so its art has to override the target's
            // art too — otherwise the game keeps showing the original ship's picture for the new design
            var result = ShipExport.Write(doc, g.Catalog, specs,
                Opts(g.Env, dest, "My Test Ship", new ShipPreview(Bytes(9), []), replaceTarget: "SalvagePod"));

            Assert.True(File.Exists(Path.Combine(result.ModDir, "images", "ships", "SalvagePod", "SalvagePod.png")));
            Assert.False(Directory.Exists(Path.Combine(result.ModDir, "images", "ships", "My Test Ship")));
        }
        finally { Directory.Delete(dest, recursive: true); }
    }

    [SkippableFact]
    public void Re_export_sweeps_room_thumbnails_the_redesign_no_longer_has()
    {
        var g = TestData.RequireGame();
        if (!Ready(g)) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);
        var doc = BuildHull(g.Catalog);

        var dest = TempDest("OstraplanPreviewSweepTest");
        try
        {
            var first = new ShipPreview(Bytes(1), [new ShipPreviewRoom("Engineering", Bytes(2))]);
            ShipExport.Write(doc, g.Catalog, specs, Opts(g.Env, dest, "My Test Ship", first));

            var second = new ShipPreview(Bytes(3), [new ShipPreviewRoom("BridgeRoom", Bytes(4))]);
            var result = ShipExport.Write(doc, g.Catalog, specs, Opts(g.Env, dest, "My Test Ship", second));

            // the broker loads the whole folder, so a thumbnail of a room the ship no longer has would still show
            var dir = Path.Combine(result.ModDir, "images", "ships", "My Test Ship");
            Assert.False(File.Exists(Path.Combine(dir, "Engineering.png")));
            Assert.True(File.Exists(Path.Combine(dir, "BridgeRoom.png")));
            Assert.Equal(2, result.PreviewCount);
        }
        finally { Directory.Delete(dest, recursive: true); }
    }

    [SkippableFact]
    public void A_room_thumbnail_never_overwrites_the_ship_image()
    {
        var g = TestData.RequireGame();
        if (!Ready(g)) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);
        var doc = BuildHull(g.Catalog);

        var dest = TempDest("OstraplanPreviewClashTest");
        try
        {
            // a ship named after a room spec: the two file stems collide, and the ship image is the one chargen
            // asks for by name, so it must win
            var preview = new ShipPreview(Bytes(1), [new ShipPreviewRoom("Engineering", Bytes(2))]);
            var result = ShipExport.Write(doc, g.Catalog, specs, Opts(g.Env, dest, "Engineering", preview));

            var dir = Path.Combine(result.ModDir, "images", "ships", "Engineering");
            Assert.Equal(Bytes(1), File.ReadAllBytes(Path.Combine(dir, "Engineering.png")));
            Assert.Equal(1, result.PreviewCount);
            Assert.Contains(result.Warnings, w => w.Contains("collides", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(dest, recursive: true); }
    }

    [SkippableFact]
    public void A_ship_name_that_cannot_be_a_folder_gets_no_art_and_says_so()
    {
        var g = TestData.RequireGame();
        if (!Ready(g)) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);
        var doc = BuildHull(g.Catalog);

        var dest = TempDest("OstraplanPreviewBadNameTest");
        try
        {
            // sanitising the folder name is not an option: the game builds the lookup path from the strName
            // verbatim, so a "cleaned" folder is one the game will never open
            var result = ShipExport.Write(doc, g.Catalog, specs,
                Opts(g.Env, dest, "Ship: Mk?II", new ShipPreview(Bytes(1), []), modName: "Bad Name Mod"));

            Assert.Equal(0, result.PreviewCount);
            Assert.False(Directory.Exists(Path.Combine(result.ModDir, "images")));
            Assert.Contains(result.Warnings, w => w.Contains("preview art", StringComparison.OrdinalIgnoreCase));
            Assert.True(File.Exists(result.ShipJsonPath));   // the ship itself still exports
        }
        finally { Directory.Delete(dest, recursive: true); }
    }

    [SkippableFact]
    public void No_preview_supplied_writes_no_image_folder()
    {
        var g = TestData.RequireGame();
        if (!Ready(g)) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);
        var doc = BuildHull(g.Catalog);

        var dest = TempDest("OstraplanPreviewNoneTest");
        try
        {
            var result = ShipExport.Write(doc, g.Catalog, specs, Opts(g.Env, dest, "My Test Ship", null));

            Assert.Equal(0, result.PreviewCount);
            Assert.False(Directory.Exists(Path.Combine(result.ModDir, "images")));
        }
        finally { Directory.Delete(dest, recursive: true); }
    }
}
