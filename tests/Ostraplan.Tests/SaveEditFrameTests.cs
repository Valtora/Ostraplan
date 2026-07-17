using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Ostraplan.Core;
using Xunit;
using Xunit.Abstractions;

namespace Ostraplan.Tests;

/// <summary>
/// The two save-corrupting inject bugs behind the "ghost rooms skew my zones" report, both verified against
/// the decompiled game (0.15.1.6):
///
/// <list type="number">
/// <item><b>The grid frame.</b> A loading ship does not trust the saved <c>nCols</c>/<c>vShipPos</c> (those feed
/// only the shallow, unloaded view — <c>Ship</c>'s <c>LoadState > Loaded.Shallow ? nCols : json.nCols</c>). It
/// REBUILDS the tilemap: <c>Ship.UpdateTiles</c> pads by (−1,+1) tiles around every spawned item
/// (<c>TileUtils.PadTilemap</c>), so the loaded grid is always the part bounding box plus a one-tile margin.
/// Every <c>aRooms</c>/<c>aZones</c> entry is a flat <c>col + row*nCols</c> index, so writing them in a frame of
/// a different width makes each one decode to the wrong tile, the error compounding by a column per row: rooms
/// bind to the wrong tiles and zones skew.</item>
///
/// <item><b>Room identity.</b> A room is backed by a <c>Compartment</c> CO and saved under that CO's strID
/// (<c>Room.GetJSONSave</c>: <c>jsonRoom.strID = coRoom.strID</c>). <c>Ship.CreateRooms</c> re-binds each saved
/// room with <c>GetCOByID(strID)</c> and consumes the hit via <c>RemoveCO</c>. Rebuilding <c>aRooms</c> with
/// fresh strIDs while keeping the original Compartments leaves every one of them unbound — the ghost rooms.</item>
/// </list>
/// </summary>
public class SaveEditFrameTests(ITestOutputHelper output)
{
    private static readonly IReadOnlyList<RoomSpecDef> NoSpecs = [];

    private static JsonObject Item(string id, string name, double fx, double fy) => new()
    {
        ["strID"] = id, ["strName"] = name, ["fX"] = fx, ["fY"] = fy, ["fRotation"] = 0.0,
    };

    private static JsonObject Co(string id, string def, params string[] conds) => new()
    {
        ["strID"] = id, ["strCODef"] = def, ["bAlive"] = true,
        ["aConds"] = new JsonArray([.. (conds.Length > 0 ? conds : ["DEFAULT"]).Select(c => (JsonNode)c!)]),
    };

    private static SaveShipContext Context(JsonArray items, JsonArray cos, Dictionary<string, OriginPart> origins,
        int nCols, int nRows, JsonArray? rooms = null, double vx = 100, double vy = 200) =>
        new()
        {
            Source = new SaveSourceRef("TestSave", "H-ABC"),
            ZipPath = @"C:\dummy\TestSave\TestSave.zip",
            ShipRecord = new JsonObject
            {
                ["strName"] = "Test", ["strRegID"] = "H-ABC",
                ["nCols"] = (double)nCols, ["nRows"] = (double)nRows,
                ["vShipPos"] = new JsonObject { ["x"] = vx, ["y"] = vy },
                ["aItems"] = items, ["aCOs"] = cos, ["aCrew"] = new JsonArray(),
                ["aRooms"] = rooms ?? new JsonArray(),
            },
            Origins = origins,
            ItemsById = items.Select(n => n!.AsObject()).ToDictionary(o => (string)o["strID"]!, o => (JsonNode)o),
            CosById = cos.Select(n => n!.AsObject()).ToDictionary(o => (string)o["strID"]!, o => (JsonNode)o),
            Epoch = 0,
        };

    // ---- 1. the grid frame ----

    /// <summary>
    /// The reporter's exact scenario, reduced. A large tank's SOCKET footprint (its under-floor storage ring) is
    /// far wider than its visible body, so sliding one to the hull edge pushes the document bounding box a column
    /// past the old grid origin while no item tile actually goes there. The frame must still carry the game's
    /// one-tile margin: hugging that bounding box exactly (the old behaviour) wrote a 48-wide frame where the
    /// game rebuilt a different one, and every room/zone index decoded against the wrong stride.
    /// </summary>
    [Fact]
    public void A_part_reaching_the_old_edge_keeps_the_games_one_tile_margin()
    {
        var cat = new Fixtures()
            .Floor("Floor")
            .Part("Tank", w: 7, h: 7, tileConds: ["IsSubTile"])
            .Build();
        // a 3x3 ship (floor inset by 1) — the frame a game load would produce
        var ctx = Context(
            new JsonArray(Item("a", "Floor", 101, 199)),
            new JsonArray(Co("a", "Floor")),
            new() { ["a"] = new OriginPart(1, 1, 0, []) }, nCols: 3, nRows: 3);
        var doc = Fixtures.Doc(cat,
            new Placement { DefName = "Floor", X = 1, Y = 1, OriginStrID = "a" },
            new Placement { DefName = "Tank", X = -1, Y = -1 });   // ring reaches a column left of the origin

        var (ship, report) = SaveEdit.BuildInjectedShip(doc, ctx, cat, NoSpecs);

        // bounds are (-1,-1)..(5,5): the frame must be that PLUS a margin, i.e. (-2,-2)..(6,6) = 9x9 at vx 98
        Assert.True(report.GridReframed);
        Assert.Equal((9, 9), (report.NCols, report.NRows));
        Assert.Equal(98.0, (double)((JsonObject)ship["vShipPos"]!)["x"]!);
        Assert.Equal(202.0, (double)((JsonObject)ship["vShipPos"]!)["y"]!);
    }

    /// <summary>The frame tracks the content: deleting the outermost part tightens the game's rebuilt grid, so
    /// the inject must shrink with it rather than preserving a stale, too-wide frame.</summary>
    [Fact]
    public void The_frame_shrinks_when_the_outermost_part_is_deleted()
    {
        var cat = new Fixtures().Floor("Floor").Build();
        var ctx = Context(
            new JsonArray(Item("a", "Floor", 101, 199), Item("b", "Floor", 105, 199)),
            new JsonArray(Co("a", "Floor"), Co("b", "Floor")),
            new() { ["a"] = new OriginPart(1, 1, 0, []), ["b"] = new OriginPart(5, 1, 0, []) },
            nCols: 7, nRows: 3);
        var doc = Fixtures.Doc(cat, new Placement { DefName = "Floor", X = 1, Y = 1, OriginStrID = "a" });   // b deleted

        var (ship, report) = SaveEdit.BuildInjectedShip(doc, ctx, cat, NoSpecs);

        Assert.True(report.GridReframed);
        Assert.Equal((3, 3), (report.NCols, report.NRows));   // bbox (1,1)..(1,1) + margin
        Assert.Equal(3, (int)ship["nCols"]!);
    }

    /// <summary>
    /// The Law gate for the frame, on real data — every player ship in every local save.
    ///
    /// <para>Two claims. First, the frame we compute insets the content by exactly one tile on all four edges:
    /// the rule, restated against real ships (the old code hugged the bounding box, i.e. inset 0, whenever a
    /// part reached the edge — that was the bug). Second, the game's OWN baked frame never insets by less than
    /// that, and where its frame is itself tight we reproduce it exactly, origin and stride included. That
    /// second half is the real evidence: it is the game's own output agreeing with the port.</para>
    ///
    /// <para>A baked frame can be LOOSER than tight because the live tilemap only ever grows —
    /// <c>TileUtils.PadTilemap</c> pads and never shrinks, so deconstructing an outermost part in play leaves a
    /// stale empty rank behind. A reload rebuilds it tight, which is exactly the frame we write, so tightening
    /// is correct rather than a mismatch. It is also harmless on its own: an index is <c>col + row*nCols</c>,
    /// so a stale trailing ROW shifts nothing (only the origin and the stride can).</para>
    /// </summary>
    [SkippableFact]
    public void A_noop_inject_frames_every_real_ship_the_way_the_game_rebuilds_it()
    {
        var g = TestData.RequireGame();
        var specs = RoomCertifier.LoadSpecs(g.Index);
        var checkedAny = false;

        foreach (var save in SaveImport.ListSaves(g.Env))
        {
            SaveEditImportResult r;
            try { r = SaveEditImport.ImportForEditing(save, g.Catalog); }
            catch { continue; }
            if (r.Doc.Bounds() is not { } b) continue;

            var rec = r.Context.ShipRecord;
            var cols0 = (int)(double)rec["nCols"]!;
            var rows0 = (int)(double)rec["nRows"]!;
            var vx0 = (double)((JsonObject)rec["vShipPos"]!)["x"]!;
            var vy0 = (double)((JsonObject)rec["vShipPos"]!)["y"]!;

            var (ship, report) = SaveEdit.BuildInjectedShip(r.Doc, r.Context, g.Catalog, specs);
            var vx = (double)((JsonObject)ship["vShipPos"]!)["x"]!;
            var vy = (double)((JsonObject)ship["vShipPos"]!)["y"]!;
            var minC = (int)Math.Round(vx - vx0);   // our frame's origin, back in document coords
            var minR = (int)Math.Round(vy0 - vy);

            var bakedInset = (L: b.MinX, R: cols0 - 1 - b.MaxX, T: b.MinY, B: rows0 - 1 - b.MaxY);
            output.WriteLine($"{save.Name}/{r.Context.Source.RegId}: game {cols0}x{rows0} inset {bakedInset} -> ours {report.NCols}x{report.NRows}");

            // ours insets by exactly the game's one-tile margin, all four edges
            Assert.Equal(1, b.MinX - minC);
            Assert.Equal(1, b.MinY - minR);
            Assert.Equal(1, minC + report.NCols - 1 - b.MaxX);
            Assert.Equal(1, minR + report.NRows - 1 - b.MaxY);

            // the game's own frame never insets by LESS than the margin
            Assert.True(bakedInset.L >= 1 && bakedInset.R >= 1 && bakedInset.T >= 1 && bakedInset.B >= 1,
                $"baked frame insets content by less than the margin: {bakedInset}");

            // where the baked frame is tight (not stale from in-play growth), we must match it exactly
            if (bakedInset is { L: 1, R: 1, T: 1, B: 1 })
            {
                Assert.Equal((cols0, rows0), (report.NCols, report.NRows));
                Assert.Equal((vx0, vy0), (vx, vy));
                Assert.False(report.GridReframed);
            }
            checkedAny = true;
        }
        Skip.IfNot(checkedAny, "no player-ship saves on this machine");
    }

    // ---- 2. room identity (ghost rooms) ----

    /// <summary>
    /// Rooms are rebuilt with fresh strIDs, so every original room CO must be dropped. Left behind, each one is
    /// an IsRoom CO no rebuilt room claims: Ship.CreateRooms' GetCOByID misses, it mints a replacement
    /// ("Generating new room with old ID"), and the original never reaches its RemoveCO — a ghost room.
    /// </summary>
    [Fact]
    public void The_original_room_cos_are_dropped_so_none_is_left_unbound()
    {
        var cat = new Fixtures().Floor("Floor").Build();
        var ctx = Context(
            new JsonArray(
                Item("a", "Floor", 101, 199),
                Item("room1", "Compartment", 100, 200),
                Item("room2", "Compartment", 100, 200)),
            new JsonArray(
                Co("a", "Floor"),
                Co("room1", "Compartment", "IsRoom=1.0x1"),
                Co("room2", "Compartment", "IsRoom=1.0x1")),
            new() { ["a"] = new OriginPart(1, 1, 0, []) }, nCols: 3, nRows: 3,
            rooms: new JsonArray(
                new JsonObject { ["strID"] = "room1", ["bVoid"] = false, ["aTiles"] = new JsonArray(4) },
                new JsonObject { ["strID"] = "room2", ["bVoid"] = true, ["aTiles"] = new JsonArray(0) }));
        var doc = Fixtures.Doc(cat, new Placement { DefName = "Floor", X = 1, Y = 1, OriginStrID = "a" });

        var (ship, _) = SaveEdit.BuildInjectedShip(doc, ctx, cat, NoSpecs);

        var itemIds = ((JsonArray)ship["aItems"]!).Select(n => (string)n!["strID"]!).ToList();
        var coIds = ((JsonArray)ship["aCOs"]!).Select(n => (string)n!["strID"]!).ToList();
        Assert.DoesNotContain("room1", itemIds);
        Assert.DoesNotContain("room2", itemIds);
        Assert.DoesNotContain("room1", coIds);
        Assert.DoesNotContain("room2", coIds);
        Assert.Contains("a", itemIds);   // the real part is untouched

        // and nothing that survived is a room: no Compartment, no IsRoom
        Assert.DoesNotContain(((JsonArray)ship["aCOs"]!).Select(n => (string?)n!["strCODef"]), d => d == "Compartment");

        // every rebuilt room's strID is fresh — it must match no surviving CO, so the game mints its own
        foreach (var room in (JsonArray)ship["aRooms"]!)
            Assert.DoesNotContain((string)room!["strID"]!, coIds);
    }

    /// <summary>A Compartment orphaned by an earlier ghost-room episode (present as a CO, named by no aRooms
    /// entry) is swept up too, rather than being preserved for the life of the save.</summary>
    [Fact]
    public void An_already_orphaned_compartment_is_swept_up_rather_than_preserved()
    {
        var cat = new Fixtures().Floor("Floor").Build();
        var ctx = Context(
            new JsonArray(Item("a", "Floor", 101, 199), Item("ghost", "Compartment", 100, 200)),
            new JsonArray(Co("a", "Floor"), Co("ghost", "Compartment", "IsRoom=1.0x1")),
            new() { ["a"] = new OriginPart(1, 1, 0, []) }, nCols: 3, nRows: 3,
            rooms: new JsonArray());   // aRooms does not mention it — exactly a pre-existing ghost
        var doc = Fixtures.Doc(cat, new Placement { DefName = "Floor", X = 1, Y = 1, OriginStrID = "a" });

        var (ship, _) = SaveEdit.BuildInjectedShip(doc, ctx, cat, NoSpecs);

        Assert.DoesNotContain("ghost", ((JsonArray)ship["aItems"]!).Select(n => (string)n!["strID"]!));
        Assert.DoesNotContain("ghost", ((JsonArray)ship["aCOs"]!).Select(n => (string)n!["strID"]!));
    }

    // ---- 3. the dimensions string ----

    /// <summary>The game parses '.' decimals, so the dimensions string must not pick up a comma-decimal
    /// locale ("15,36m x 11,20m" was written on the reporter's machine).</summary>
    [Fact]
    public void Dimensions_use_invariant_decimals_on_a_comma_decimal_locale()
    {
        var prior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");   // decimal comma
            var cat = new Fixtures().Floor("Floor").Build();
            var ctx = Context(
                new JsonArray(Item("a", "Floor", 101, 199)),
                new JsonArray(Co("a", "Floor")),
                new() { ["a"] = new OriginPart(1, 1, 0, []) }, nCols: 3, nRows: 3);
            var doc = Fixtures.Doc(cat, new Placement { DefName = "Floor", X = 1, Y = 1, OriginStrID = "a" });

            var (ship, _) = SaveEdit.BuildInjectedShip(doc, ctx, cat, NoSpecs);

            var dims = (string)ship["dimensions"]!;
            Assert.DoesNotContain(",", dims);
            Assert.Equal("0.96m x 0.96m", dims);   // 3 tiles * 0.32 m
        }
        finally { CultureInfo.CurrentCulture = prior; }
    }
}
