using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ostraplan.Core;
using Xunit;
using Xunit.Abstractions;

namespace Ostraplan.Tests;

/// <summary>
/// The grant writer: the textual <c>dictShipOwners</c> insert (unit-tested against both shapes the game writes),
/// and the end-to-end grant against the live install's newest save. The real save is read, never written — every
/// write goes to a throwaway temp folder. No-op when there's no install.
/// </summary>
public class SaveGrantWriteTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    // ---- the ownership registry insert ----

    private static string Insert(string record, string regId = "H-NEW", string owner = "Ark Valtor")
    {
        using var reader = new StringReader(record);
        using var writer = new StringWriter();
        Assert.True(SaveGrant.InsertShipOwner(reader, writer, regId, owner));
        return writer.ToString();
    }

    /// <summary>Parse the (alternating key/value) registry back out of a session record. A real record is
    /// wrapped in a one-element array, like every other file the game writes; the fixtures below are bare
    /// objects, so unwrap either.</summary>
    private static Dictionary<string, string> Owners(string record)
    {
        var root = JsonNode.Parse(record)!;
        var obj = root is JsonArray a ? a[0]! : root;
        var arr = obj["objSystem"]!["dictShipOwners"]!.AsArray()
            .Select(n => (string)n!).ToList();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < arr.Count; i += 2) map[arr[i]] = arr[i + 1];
        return map;
    }

    [Fact]
    public void Owner_insert_adds_a_pair_to_a_populated_registry()
    {
        // the shape the game actually writes: the array opens on the key's line, one entry per line after it
        var record = """
        {
          "strName" : "Ark Valtor",
          "strShip" : "B-A1R",
          "objSystem"   : {
            "dfEpoch" : 65628140837.4364,
            "dictShipOwners"  : [
              "B-A1R",
              "Ark Valtor",
              "OKLG",
              "OKLGCiv"
            ],
            "aComps"          : []
          }
        }
        """;

        var result = Insert(record);

        var owners = Owners(result);
        Assert.Equal("Ark Valtor", owners["H-NEW"]);
        Assert.Equal("Ark Valtor", owners["B-A1R"]);   // existing pairs survive, unshuffled
        Assert.Equal("OKLGCiv", owners["OKLG"]);
        Assert.Equal(3, owners.Count);
    }

    [Fact]
    public void Owner_insert_handles_an_empty_registry_written_inline()
    {
        var record = """
        {
          "strShip" : "B-A1R",
          "objSystem"   : {
            "dictShipOwners"  : [],
            "aComps"          : []
          }
        }
        """;

        var result = Insert(record);

        Assert.Equal(new Dictionary<string, string> { ["H-NEW"] = "Ark Valtor" }, Owners(result));
    }

    [Fact]
    public void Owner_insert_leaves_the_rest_of_the_record_byte_identical()
    {
        // the point of doing this textually: adding two strings must not reformat a 60 MB record
        var record = """
        {
          "strShip" : "B-A1R",
          "fTotalGameSec" : 451009.6,
          "objSystem"   : {
            "dfEpoch" : 65628140837.4364,
            "dictShips"       : [
              "B-A1R"
            ],
            "dictShipOwners"  : [
              "B-A1R",
              "Ark Valtor"
            ]
          }
        }
        """;

        var result = Insert(record);

        foreach (var line in record.Split('\n'))
            Assert.Contains(line.TrimEnd('\r'), result.Split('\n').Select(l => l.TrimEnd('\r')));
        Assert.Equal(record.Split('\n').Length + 2, result.Split('\n').Length - 1);   // exactly two lines added
    }

    [Fact]
    public void Owner_insert_reports_a_record_with_no_registry_rather_than_corrupting_it()
    {
        // "dictShips" contains "dictShip" but must not be mistaken for the owner registry
        var record = """
        {
          "strShip" : "B-A1R",
          "objSystem"   : {
            "dictShips" : [ "B-A1R" ]
          }
        }
        """;

        using var reader = new StringReader(record);
        using var writer = new StringWriter();
        Assert.False(SaveGrant.InsertShipOwner(reader, writer, "H-NEW", "Ark Valtor"));

        Assert.Equal(
            record.ReplaceLineEndings("\n").TrimEnd('\n'),
            writer.ToString().ReplaceLineEndings("\n").TrimEnd('\n'));
    }

    // ---- end to end, against the live install ----

    /// <summary>The newest save that can actually take a grant, with its context.</summary>
    private static (SaveEntry Save, GrantContext Ctx)? FirstGrantable(GameEnv env)
    {
        foreach (var save in SaveImport.ListSaves(env))
        {
            try { return (save, SaveGrant.ReadContext(save)); }
            catch { /* no player record / player not aboard — try the next */ }
        }
        return null;
    }

    /// <summary>A minimal but real design: a sealed 4×4 room of core walls and floor.</summary>
    private static ShipDocument Design(Catalog catalog)
    {
        var doc = new ShipDocument(catalog);
        for (var x = 0; x < 4; x++)
            for (var y = 0; y < 4; y++)
            {
                var edge = x is 0 or 3 || y is 0 or 3;
                new PlaceCommand(new Placement { DefName = edge ? "ItmWall1x1" : "ItmFloor1x1", X = x, Y = y }).Do(doc);
            }
        return doc;
    }

    private static string ReadEntry(string zipPath, string entry)
    {
        using var z = ZipFile.OpenRead(zipPath);
        using var r = new StreamReader(z.GetEntry(entry)!.Open());
        return r.ReadToEnd();
    }

    [SkippableFact]
    public void Grant_writes_a_copy_that_owns_a_new_reachable_ship()
    {
        var g = TestData.RequireGame();
        Skip.If(FirstGrantable(g.Env) is null, "no local save the player can be granted a ship in");
        var (save, ctx) = FirstGrantable(g.Env)!.Value;
        var specs = RoomCertifier.LoadSpecs(g.Index);

        var outDir = Path.Combine(Path.GetTempPath(), "OstraplanGrantTest-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (dir, report) = SaveGrant.Grant(
                ctx, Design(g.Catalog), g.Catalog, specs,
                new GrantOptions("Test Grant", new ExportMetadata("Test Grant"), WearOptions.Vanilla, PlacementSeed: 5),
                price: 0, outputSaveDir: outDir);
            sw.Stop();

            var zip = Directory.EnumerateFiles(dir, "*.zip").Single();

            // 1. the ship is there, and every item carries the CO a save load demands
            var shipFile = ReadEntry(zip, SaveZip.ShipEntry(report.RegId));
            var ship = JsonNode.Parse(shipFile)!.AsArray()[0]!;
            var coIds = ship["aCOs"]!.AsArray().Select(c => (string)c!["strID"]!).ToHashSet(StringComparer.Ordinal);
            foreach (var item in ship["aItems"]!.AsArray())
                Assert.Contains((string)item!["strID"]!, coIds);

            // 2. it is owned — in BOTH places, or it is either unreachable or unusable by your own crew
            var session = ReadEntry(zip, ctx.SessionEntryName);
            var owners = Owners(session);
            Assert.Equal(ctx.PlayerCoId, owners[report.RegId]);

            var playerShip = JsonNode.Parse(ReadEntry(zip, SaveZip.ShipEntry(ctx.PlayerShipRegId)))!;
            var record = playerShip is JsonArray arr
                ? arr.OfType<JsonObject>().OrderByDescending(o => (o["aItems"] as JsonArray)?.Count ?? 0).First()
                : playerShip.AsObject();
            var playerCo = record["aCOs"]!.AsArray().Single(c => (string?)c!["strID"] == ctx.PlayerCoId)!;
            Assert.Contains(report.RegId, playerCo["aMyShips"]!.AsArray().Select(n => (string)n!));

            // 3. it is parked within the band the ferry will fly to
            var ss = ship["objSS"]!;
            var dx = (double)ss["vPosx"]! - ctx.Anchor.PosX;
            var dy = (double)ss["vPosy"]! - ctx.Anchor.PosY;
            var au = Math.Sqrt(dx * dx + dy * dy);
            Assert.InRange(au, SaveGrant.MinRadiusAu, SaveGrant.MaxRadiusAu);
            Assert.True(au < SaveGrant.FerryRangeAu);

            // 4. the original save is untouched
            Assert.DoesNotContain(report.RegId, ReadEntry(save.ZipPath, ctx.SessionEntryName));

            _out.WriteLine(
                $"granted {report.RegId} ({report.PublicName}) into a copy of \"{save.Name}\": " +
                $"{report.ItemCount} items, {report.RoomCount} rooms, rating {report.Rating.Display}, " +
                $"{report.DistanceKm:0.00} km out, {sw.ElapsedMilliseconds} ms, " +
                $"{new FileInfo(zip).Length / 1_000_000.0:0} MB archive");
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        }
    }

    [SkippableFact]
    public void Grant_deducts_the_price_from_the_players_balance()
    {
        var g = TestData.RequireGame();
        Skip.If(FirstGrantable(g.Env) is null, "no local save the player can be granted a ship in");
        var (_, ctx) = FirstGrantable(g.Env)!.Value;
        Skip.If(ctx.Balance <= 0, "this save's player has no credits to deduct from");
        var specs = RoomCertifier.LoadSpecs(g.Index);

        var outDir = Path.Combine(Path.GetTempPath(), "OstraplanGrantTest-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var price = Math.Round(ctx.Balance / 2);
            var (dir, report) = SaveGrant.Grant(
                ctx, Design(g.Catalog), g.Catalog, specs, new GrantOptions("Priced"), price, outDir);

            Assert.Equal(price, report.Charged);
            Assert.Equal(ctx.Balance - price, report.ResultingBalance);

            // the authoritative balance is the player CO's StatUSD; saveInfo.money is the mirror the menu shows
            var zip = Directory.EnumerateFiles(dir, "*.zip").Single();
            var playerShip = JsonNode.Parse(ReadEntry(zip, SaveZip.ShipEntry(ctx.PlayerShipRegId)))!;
            var record = playerShip is JsonArray arr
                ? arr.OfType<JsonObject>().OrderByDescending(o => (o["aItems"] as JsonArray)?.Count ?? 0).First()
                : playerShip.AsObject();
            var playerCo = record["aCOs"]!.AsArray().Single(c => (string?)c!["strID"] == ctx.PlayerCoId)!;
            var usd = playerCo["aConds"]!.AsArray()
                .Select(n => (string)n!)
                .Where(s => s.StartsWith("StatUSD=", StringComparison.Ordinal))
                .Sum(LootDef.CondAmount);
            Assert.Equal(ctx.Balance - price, usd, 2);

            var info = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "saveInfo.json")))!;
            var infoObj = info is JsonArray a2 ? a2[0]!.AsObject() : info.AsObject();
            Assert.Equal(ctx.Balance - price, (double)infoObj["money"]!, 2);
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        }
    }

    [SkippableFact]
    public void Grant_refuses_a_price_the_player_cannot_afford()
    {
        var g = TestData.RequireGame();
        Skip.If(FirstGrantable(g.Env) is null, "no local save the player can be granted a ship in");
        var (_, ctx) = FirstGrantable(g.Env)!.Value;
        var specs = RoomCertifier.LoadSpecs(g.Index);

        Assert.Throws<InvalidDataException>(() => SaveGrant.Grant(
            ctx, Design(g.Catalog), g.Catalog, specs, new GrantOptions("Too dear"), ctx.Balance + 1));
    }

    // ---- the build/write split ----

    /// <summary>
    /// The property the export wizard's Review step rests on: a ship built once and written later lands in the
    /// save <b>byte for byte</b> as it was built. Rebuilding at commit instead would not do, because
    /// <see cref="SaveGrant.MintRegId"/> draws from <see cref="Guid"/> and cannot be seeded, so the registration
    /// shown on Review would not be the one written.
    /// </summary>
    [SkippableFact]
    public void A_ship_built_up_front_is_written_exactly_as_it_was_built()
    {
        var g = TestData.RequireGame();
        Skip.If(FirstGrantable(g.Env) is null, "no local save the player can be granted a ship in");
        var (_, ctx) = FirstGrantable(g.Env)!.Value;
        var specs = RoomCertifier.LoadSpecs(g.Index);

        var outDir = Path.Combine(Path.GetTempPath(), "OstraplanGrantTest-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // --- what Review does ---
            var regId = SaveGrant.MintRegId(ctx.ExistingRegIds, ctx.PlayerShipRegId);
            var opts = new GrantOptions("Split Grant", new ExportMetadata("Split Grant"),
                WearOptions.Vanilla with { Seed = 1234 }, PlacementSeed: 5);
            var (built, report) = SaveGrant.BuildShip(
                Design(g.Catalog), g.Catalog, specs, regId, ctx.Anchor, opts, ctx.Epoch);
            var reviewed = built.ToJsonString();

            // --- what Commit does, later, with no rebuild ---
            var (dir, written) = SaveGrant.WriteGrant(ctx, regId, built, report, price: 0, outputSaveDir: outDir);

            Assert.Equal(regId, written.RegId);   // the registration Review displayed is the one in the save
            var zip = Directory.EnumerateFiles(dir, "*.zip").Single();
            var onDisk = JsonNode.Parse(ReadEntry(zip, SaveZip.ShipEntry(regId)))!.AsArray()[0]!;
            Assert.Equal(reviewed, onDisk.ToJsonString());
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        }
    }

    [SkippableFact]
    public void WriteGrant_refuses_a_price_the_player_cannot_afford()
    {
        var g = TestData.RequireGame();
        Skip.If(FirstGrantable(g.Env) is null, "no local save the player can be granted a ship in");
        var (_, ctx) = FirstGrantable(g.Env)!.Value;
        var specs = RoomCertifier.LoadSpecs(g.Index);

        var regId = SaveGrant.MintRegId(ctx.ExistingRegIds, ctx.PlayerShipRegId);
        var (ship, report) = SaveGrant.BuildShip(
            Design(g.Catalog), g.Catalog, specs, regId, ctx.Anchor, new GrantOptions("Too dear"), ctx.Epoch);

        // the check has to live on the write half too: the wizard never calls Grant
        Assert.Throws<InvalidDataException>(() => SaveGrant.WriteGrant(ctx, regId, ship, report, ctx.Balance + 1));
    }
}
