using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ostraplan.Core;
using Xunit;
using Xunit.Abstractions;

namespace Ostraplan.Tests;

/// <summary>
/// Save-edit Phase 2: the inject engine (<see cref="SaveEdit"/>) rebuilds the ship record from the diff and
/// writes it to a <b>copy</b> of the save. These run against the live install's newest player ship and write
/// only to a throwaway temp folder — the real saves are read, never written. No-op when there's no install.
/// </summary>
public class SaveEditInjectTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    private static SaveEditImportResult? FirstImport(GameEnv env, Catalog catalog)
    {
        foreach (var save in SaveImport.ListSaves(env))
        {
            try { return SaveEditImport.ImportForEditing(save, catalog); }
            catch { /* not a player-ship save */ }
        }
        return null;
    }

    private static int Count(JsonNode ship, string prop) => ((JsonArray)ship[prop]!).Count;

    private static string ReadShipEntry(string zipPath, string regId)
    {
        using var z = ZipFile.OpenRead(zipPath);
        using var r = new StreamReader(z.GetEntry($"ships/{regId}.json")!.Open());
        return r.ReadToEnd();
    }

    [Fact]
    public void Noop_inject_preserves_every_item_and_co()
    {
        if (TestData.Game is not { } g) return;
        if (FirstImport(g.Env, g.Catalog) is not { } r) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);

        var origItems = Count(r.Context.ShipRecord, "aItems");
        var origCOs = Count(r.Context.ShipRecord, "aCOs");

        var (ship, report) = SaveEdit.BuildInjectedShip(r.Doc, r.Context, g.Catalog, specs);

        Assert.Equal(origItems, Count(ship, "aItems"));   // nothing added or dropped
        Assert.Equal(origCOs, Count(ship, "aCOs"));
        Assert.Equal(0, report.Added);
        Assert.Equal(0, report.Moved);
        Assert.Equal(0, report.Deleted);
        Assert.Empty(report.CargoDropped);
        Assert.False(report.GridGrew);
        _out.WriteLine($"no-op: {report.Kept} kept, {origItems} items, {origCOs} COs, {report.NCols}x{report.NRows}, {report.Warnings.Count} warnings");
    }

    [Fact]
    public void Adding_a_part_adds_one_item_and_a_synthesized_pristine_co()
    {
        if (TestData.Game is not { } g) return;
        if (FirstImport(g.Env, g.Catalog) is not { } r) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);
        if (r.Doc.Bounds() is not { } b) return;

        var items0 = Count(r.Context.ShipRecord, "aItems");
        var cos0 = Count(r.Context.ShipRecord, "aCOs");
        var def = r.Doc.Placements[0].DefName;
        // place inside the existing bounds so the grid doesn't blow up
        new PlaceCommand(new Placement { DefName = def, X = b.MinX, Y = b.MinY }).Do(r.Doc);

        var (ship, report) = SaveEdit.BuildInjectedShip(r.Doc, r.Context, g.Catalog, specs);

        Assert.Equal(1, report.Added);
        Assert.Equal(items0 + 1, Count(ship, "aItems"));   // one new item...
        Assert.Equal(cos0 + 1, Count(ship, "aCOs"));       // ...AND a matching CO (a save load skips item-without-CO)

        // the synthesized CO is 1:1 with the new item by strID, def-typed, with DEFAULT conds (pristine on load)
        var newItem = ((JsonArray)ship["aItems"]!).Last()!.AsObject();
        var newId = (string?)newItem["strID"];
        var newCo = ((JsonArray)ship["aCOs"]!).Select(n => n!.AsObject()).First(c => (string?)c["strID"] == newId);
        Assert.Equal(def, (string?)newCo["strCODef"]);
        Assert.Contains("DEFAULT", ((JsonArray)newCo["aConds"]!).Select(n => (string?)n));
    }

    [Fact]
    public void Reskinning_a_part_gives_the_new_skin_a_co_so_it_is_not_skipped_on_load()
    {
        // regression for the catastrophic in-game failure: re-skinning drops identity (def change = new part),
        // so every re-skinned tile needs a synthesized CO or the game logs "missing save data" and skips it.
        if (TestData.Game is not { } g) return;
        if (FirstImport(g.Env, g.Catalog) is not { } r) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);

        var target = r.Doc.Placements.FirstOrDefault(p =>
            !r.Doc.IsLocked(p) && ReplaceOps.CommonClass(r.Doc, [p]) is { W: 1, H: 1 });
        if (target is null) return;
        var cls = ReplaceOps.CommonClass(r.Doc, [target])!.Value;
        if (ReplaceOps.CompatibleTargets(g.Catalog, cls).FirstOrDefault(d => d.DefName != target.DefName) is not { } other) return;

        var cos0 = Count(r.Context.ShipRecord, "aCOs");
        var swap = ReplaceOps.BuildSwap(r.Doc, [target], other.DefName);
        Assert.NotNull(swap);
        swap!.Value.Cmd.Do(r.Doc);

        var (ship, report) = SaveEdit.BuildInjectedShip(r.Doc, r.Context, g.Catalog, specs);

        Assert.Equal(1, report.Added);     // the re-skinned tile is a new part...
        Assert.Equal(1, report.Deleted);   // ...and its original is deleted
        Assert.Equal(cos0, Count(ship, "aCOs"));   // -1 original CO, +1 synthesized -> net unchanged
        Assert.Contains(((JsonArray)ship["aCOs"]!).Select(n => n!.AsObject()), c => (string?)c["strCODef"] == other.DefName);
    }

    [Fact]
    public void Moving_an_imported_part_clears_immunity_and_the_law_reapplies()
    {
        // an unmoved imported part is immune (the game never re-validates existing structure), but MOVING it is
        // an authoring act — given-ness clears and the placement law re-applies. Dragged into open space, a part
        // that needs support (has socket reqs) must now flag; the imported ship flagged nothing before.
        if (TestData.Game is not { } g) return;
        if (FirstImport(g.Env, g.Catalog) is not { } r) return;

        // a non-locked imported part that requires structure around it — deep empty space fails its socket reqs
        var target = r.Doc.Placements.FirstOrDefault(p => !r.Doc.IsLocked(p)
            && r.Doc.Part(p) is { } d && d.Item.SocketReqs.Any(s => s.Length > 0 && s != "Blank"));
        if (target is null) return;

        int Blocking() => ProblemScan.Scan(r.Doc, g.Catalog).Count(p => p.Severity == ProblemSeverity.Blocking);
        Assert.True(target.IsGiven);        // imported -> immune while unmoved
        var before = Blocking();

        new MoveCommand([target], 500, 500).Do(r.Doc);   // drag it far into open space

        Assert.False(target.IsGiven);       // the move re-authored it...
        Assert.NotNull(target.OriginStrID); // ...but its save identity is kept (a move, not a delete+new)
        Assert.True(Blocking() > before);   // ...and the law now flags the unsupported part
    }

    [Fact]
    public void Moving_a_part_keeps_its_exact_condition_and_condition_owner()
    {
        // answers "does moving a wall keep it in the same condition?": a moved part keeps its strID, its item
        // entry (incl. aCondOverrides = wear/damage) verbatim bar the pose, and its whole CO (all live state).
        if (TestData.Game is not { } g) return;
        if (FirstImport(g.Env, g.Catalog) is not { } r) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);

        // prefer a part carrying wear/damage (aCondOverrides) so "condition preserved" is a real assertion
        var target = r.Doc.Placements.FirstOrDefault(p => !r.Doc.IsLocked(p)
                         && p.OriginStrID is { } id && r.Context.ItemsById[id]["aCondOverrides"] is JsonArray)
                     ?? r.Doc.Placements.FirstOrDefault(p => !r.Doc.IsLocked(p));
        if (target?.OriginStrID is not { } oid) return;

        var origItem = r.Context.ItemsById[oid].AsObject();
        var origOverrides = origItem["aCondOverrides"]?.ToJsonString();
        var origCoConds = r.Context.CosById[oid].AsObject()["aConds"]?.ToJsonString();
        var origFx = origItem["fX"]!.GetValue<double>();

        new MoveCommand([target], 3, 0).Do(r.Doc);
        var (ship, report) = SaveEdit.BuildInjectedShip(r.Doc, r.Context, g.Catalog, specs);

        Assert.Equal(1, report.Moved);
        // SAME item (strID survives), its condition overrides intact, only the pose changed
        var moved = ((JsonArray)ship["aItems"]!).Select(n => n!.AsObject()).Single(it => (string?)it["strID"] == oid);
        Assert.Equal(origOverrides, moved["aCondOverrides"]?.ToJsonString());
        Assert.NotEqual(origFx, moved["fX"]!.GetValue<double>());
        // and its condition owner (wear, power, gas, inventory, door state) is kept verbatim
        var co = ((JsonArray)ship["aCOs"]!).Select(n => n!.AsObject()).Single(c => (string?)c["strID"] == oid);
        Assert.Equal(origCoConds, co["aConds"]?.ToJsonString());
    }

    [Fact]
    public void Deleting_a_cargoless_part_drops_its_item_and_co()
    {
        if (TestData.Game is not { } g) return;
        if (FirstImport(g.Env, g.Catalog) is not { } r) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);

        var target = r.Doc.Placements.FirstOrDefault(p =>
            !r.Doc.IsLocked(p) && p.OriginStrID is { } id && r.Context.Origins[id].CargoIds.Count == 0);
        if (target is null) return;

        var items0 = Count(r.Context.ShipRecord, "aItems");
        var cos0 = Count(r.Context.ShipRecord, "aCOs");
        new RemoveCommand([target]).Do(r.Doc);

        var (ship, report) = SaveEdit.BuildInjectedShip(r.Doc, r.Context, g.Catalog, specs);

        Assert.Equal(1, report.Deleted);
        Assert.Empty(report.CargoDropped);
        Assert.Equal(items0 - 1, Count(ship, "aItems"));   // item gone...
        Assert.Equal(cos0 - 1, Count(ship, "aCOs"));       // ...and its 1:1 condition owner with it
    }

    [Fact]
    public void Injected_copy_has_a_co_for_every_item_after_reskin_and_add()
    {
        // The reported catastrophe, at the file level: re-skin a batch of tiles (def change -> new parts) and
        // add one, then confirm the WRITTEN save has a CO for every item — the invariant DataHandler.SpawnItems
        // enforces (an item without a CO is skipped on load).
        if (TestData.Game is not { } g) return;
        if (FirstImport(g.Env, g.Catalog) is not { } r) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);
        if (r.Doc.Bounds() is not { } b) return;

        var toReskin = r.Doc.Placements
            .Where(p => !r.Doc.IsLocked(p) && ReplaceOps.CommonClass(r.Doc, [p]) is { W: 1, H: 1 })
            .Take(20).ToList();
        foreach (var p in toReskin)
        {
            var cls = ReplaceOps.CommonClass(r.Doc, [p])!.Value;
            if (ReplaceOps.CompatibleTargets(g.Catalog, cls).FirstOrDefault(d => d.DefName != p.DefName) is { } alt)
                ReplaceOps.BuildSwap(r.Doc, [p], alt.DefName)?.Cmd.Do(r.Doc);
        }
        new PlaceCommand(new Placement { DefName = r.Doc.Placements[0].DefName, X = b.MinX, Y = b.MinY }).Do(r.Doc);

        var temp = Path.Combine(Path.GetTempPath(), $"ostraplan_inv_{Guid.NewGuid():N}");
        try
        {
            var (outDir, report) = SaveEdit.Inject(r.Doc, r.Context, g.Catalog, specs, temp, overwrite: true);
            Assert.True(report.Added >= 1);

            var name = Path.GetFileName(outDir);
            var top = JsonNode.Parse(ReadShipEntry(Path.Combine(outDir, name + ".zip"), r.Context.Source.RegId));
            var ship = top is JsonArray a
                ? a.OfType<JsonObject>().OrderByDescending(o => (o["aItems"] as JsonArray)?.Count ?? 0).First()
                : top!.AsObject();
            var coIds = ((JsonArray)ship["aCOs"]!).Select(c => (string?)c!["strID"]).Where(s => s is not null).ToHashSet();
            var orphans = ((JsonArray)ship["aItems"]!).Select(i => (string?)i!["strID"])
                .Where(id => id is not null && !coIds.Contains(id)).ToList();
            Assert.Empty(orphans);   // no item may lack a CO, or the game skips it on load
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public void Adding_a_powered_device_bakes_its_power_ticker_so_it_can_draw_power()
    {
        // the reported dead-device bug: an injected device with no Power ticker never gets IsReadyUsePower, so it
        // can't draw power even once conduits reach it. A synthesized CO must carry the def's Power ticker.
        if (TestData.Game is not { } g) return;
        if (FirstImport(g.Env, g.Catalog) is not { } r) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);
        if (r.Doc.Bounds() is not { } b) return;

        var device = g.Catalog.Parts.FirstOrDefault(p => p.TickerNames.Contains("Power") && g.Catalog.TickersFor(p).Count > 0);
        if (device is null) return;

        new PlaceCommand(new Placement { DefName = device.DefName, X = b.MinX, Y = b.MinY }).Do(r.Doc);
        var (ship, report) = SaveEdit.BuildInjectedShip(r.Doc, r.Context, g.Catalog, specs);

        var newCo = ((JsonArray)ship["aCOs"]!).Select(n => n!.AsObject()).Last(c => (string?)c["strCODef"] == device.DefName);
        var tickers = newCo["aTickers"] as JsonArray;
        Assert.NotNull(tickers);
        var power = tickers!.Select(n => n!.AsObject()).FirstOrDefault(t => (string?)t["strName"] == "Power");
        Assert.NotNull(power);                      // the Power ticker is present...
        Assert.NotNull(power!["fEpochStart"]);      // ...stamped with a start epoch so it fires on load
        Assert.True(report.PowerFixed >= 0);        // self-heal ran over the kept devices too (no crash)
    }

    [Fact]
    public void Injected_ship_is_armed_to_refill_atmosphere_and_marked_pristine()
    {
        // the edit regenerates aRooms, orphaning per-room gas -> the ship would spawn airless. bPrefill makes the
        // game refill it on load; marking the hull pristine (DMGStatus New) keeps that fill safe on a Used/Damaged
        // ship (else bPrefill fires the break-in/damage path).
        if (TestData.Game is not { } g) return;
        if (FirstImport(g.Env, g.Catalog) is not { } r) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);

        var (ship, report) = SaveEdit.BuildInjectedShip(r.Doc, r.Context, g.Catalog, specs);

        Assert.True(report.AtmosphereFilled);
        Assert.True((bool)ship["bPrefill"]!);                 // the game runs PreFillRooms on load...
        Assert.Equal(0, ship["DMGStatus"]!.GetValue<int>());  // ...and the hull is marked pristine so it's safe
    }

    [Fact]
    public void GpmSettingsFor_expands_the_def_pairs_against_the_templates()
    {
        var electrical = JsonDocument.Parse("[\"status\",\"true\"]").RootElement.Clone();
        var panel = JsonDocument.Parse("[\"strGUIPrefab\",\"GUIAirPump\"]").RootElement.Clone();
        var cat = new Catalog
        {
            Parts = [],
            ByDefName = new Dictionary<string, PartDef>(),
            Loots = new Dictionary<string, LootDef>(),
            Triggers = new Dictionary<string, CondTriggerDef>(),
            Warnings = [],
            GpmTemplates = new Dictionary<string, JsonElement> { ["Electrical"] = electrical, ["AirPump"] = panel },
        };
        var part = new PartDef("Dev", "Dev", "MISC", "core",
            new ItemDef("Dev", "", false, null, 0, 1, ["L"], [], []), null, [], [], [],
            new Dictionary<string, double>(), new Dictionary<string, (double, double)>())
        {
            Gpm = [("Panel A", "AirPump"), ("Electrical", "Electrical")],
        };

        var settings = cat.GpmSettingsFor(part);
        Assert.Equal(["Panel A", "Electrical"], settings.Select(s => s.Instance));
        Assert.Empty(cat.GpmSettingsFor(part with { Gpm = [("X", "NoSuchTemplate")] }));   // unknown template -> nothing
    }

    [Fact]
    public void Adding_a_powered_device_bakes_its_gpm_so_it_wires_on_load()
    {
        if (TestData.Game is not { } g) return;
        if (FirstImport(g.Env, g.Catalog) is not { } r) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);
        if (r.Doc.Bounds() is not { } b) return;
        if (g.Catalog.Lookup("ItmAirPump03OnG") is not { } pumpDef) return;

        // the def resolves its GUI-prop-maps (control panel + the Electrical power map) and the templates load
        Assert.Contains(pumpDef.Gpm, t => t.Template == "Electrical");
        Assert.Contains(g.Catalog.GpmSettingsFor(pumpDef), s => s.Instance == "Electrical");

        new PlaceCommand(new Placement { DefName = "ItmAirPump03OnG", X = b.MinX, Y = b.MinY }).Do(r.Doc);
        var (ship, report) = SaveEdit.BuildInjectedShip(r.Doc, r.Context, g.Catalog, specs);
        Assert.Equal(1, report.Added);

        // the new pump (appended last) carries an aGPMSettings including the Electrical power map
        var pump = ((JsonArray)ship["aItems"]!).Select(n => n!.AsObject()).Last(it => (string?)it["strName"] == "ItmAirPump03OnG");
        var gpm = pump["aGPMSettings"] as JsonArray;
        Assert.NotNull(gpm);
        Assert.Contains(gpm!.Select(n => n!.AsObject()), o => (string?)o["strName"] == "Electrical");
    }

    [Fact]
    public void Charging_an_edit_deducts_statusd_on_the_player_co_and_saveinfo_money()
    {
        // the cost deduction: rewrite the player CO's StatUSD (authoritative balance) and mirror saveInfo.money
        // into the written copy; the player CO lives on their own ship, so it's the file we already splice.
        if (TestData.Game is not { } g) return;
        if (FirstImport(g.Env, g.Catalog) is not { } r) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);
        if (SaveEdit.CurrentBalance(r.Context) is not { } balance) return;   // save has no player balance -> skip
        if (r.Context.PlayerCoId is not { } coId) return;

        const double cost = 100.0;
        var charge = new EditCharge(coId, cost, balance - cost);

        var temp = Path.Combine(Path.GetTempPath(), $"ostraplan_charge_{Guid.NewGuid():N}");
        try
        {
            var (outDir, report) = SaveEdit.Inject(r.Doc, r.Context, g.Catalog, specs, temp, overwrite: true, charge: charge);
            Assert.Equal(cost, report.Charged);
            Assert.Equal(balance - cost, report.ResultingBalance!.Value, 2);

            // saveInfo.money mirrors the new balance
            var name = Path.GetFileName(outDir);
            var saveInfo = JsonNode.Parse(File.ReadAllText(Path.Combine(outDir, "saveInfo.json")));
            var money = (saveInfo is JsonArray a ? a[0] : saveInfo)!.AsObject()["money"]!.GetValue<double>();
            Assert.Equal(balance - cost, money, 2);

            // the player CO's StatUSD in the written ship record is the new balance
            var top = JsonNode.Parse(ReadShipEntry(Path.Combine(outDir, name + ".zip"), r.Context.Source.RegId));
            var ship = top is JsonArray sa
                ? sa.OfType<JsonObject>().OrderByDescending(o => (o["aItems"] as JsonArray)?.Count ?? 0).First()
                : top!.AsObject();
            var playerCo = ((JsonArray)ship["aCOs"]!).Select(n => n!.AsObject()).First(c => (string?)c["strID"] == coId);
            var statUsd = ((JsonArray)playerCo["aConds"]!)
                .Select(n => (string?)n).Where(s => s is not null && s.StartsWith("StatUSD="))
                .Sum(s => ParseAmount(s!));
            Assert.Equal(balance - cost, statUsd, 2);
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); }
    }

    // "StatUSD=1.0x5651.7" -> 5651.7 (magnitude after the 'x')
    private static double ParseAmount(string cond)
    {
        var x = cond.IndexOf('x');
        return x >= 0 && double.TryParse(cond[(x + 1)..], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    [Fact]
    public void WriteCopy_makes_a_loadable_copy_and_leaves_the_original_untouched()
    {
        if (TestData.Game is not { } g) return;
        if (FirstImport(g.Env, g.Catalog) is not { } r) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);

        var originalBefore = ReadShipEntry(r.Context.ZipPath, r.Context.Source.RegId);
        var temp = Path.Combine(Path.GetTempPath(), $"ostraplan_inject_{Guid.NewGuid():N}");
        try
        {
            var (outDir, _) = SaveEdit.Inject(r.Doc, r.Context, g.Catalog, specs, outputSaveDir: temp, overwrite: true);
            var name = Path.GetFileName(outDir);

            // the copy is a well-formed save: <folder>/<folder>.zip + a saveInfo naming the copy
            Assert.True(File.Exists(Path.Combine(outDir, name + ".zip")), "zip renamed to match the folder");
            Assert.Contains(name, File.ReadAllText(Path.Combine(outDir, "saveInfo.json")));

            // its spliced ship record parses and — this was a no-op inject — round-trips every item
            var injected = ReadShipEntry(Path.Combine(outDir, name + ".zip"), r.Context.Source.RegId);
            var shipNode = ShipTemplate.ParseFile(injected).OrderByDescending(s => s.Items.Count).FirstOrDefault();
            Assert.NotNull(shipNode);
            Assert.Equal(Count(r.Context.ShipRecord, "aItems"), shipNode!.Items.Count);

            // the ORIGINAL save is byte-for-byte what it was
            Assert.Equal(originalBefore, ReadShipEntry(r.Context.ZipPath, r.Context.Source.RegId));
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); }
    }
}
