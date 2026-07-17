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

    /// <summary>The first save whose imported player ship matches <paramref name="pred"/> — so a cargo test lands
    /// on a save that actually has cargo (the first player-ship save may not). Null if none across all saves.</summary>
    private static SaveEditImportResult? ImportWhere(GameEnv env, Catalog catalog, Func<ShipDocument, bool> pred)
    {
        foreach (var save in SaveImport.ListSaves(env))
        {
            SaveEditImportResult imp;
            try { imp = SaveEditImport.ImportForEditing(save, catalog); }
            catch { continue; }
            if (pred(imp.Doc)) return imp;
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

    /// <summary>The source ship's room strIDs — its <c>Compartment</c> COs, which an inject always replaces
    /// (see <c>SaveEdit.RoomCoIds</c>). Everything else must survive a no-op verbatim.</summary>
    private static HashSet<string> RoomIds(JsonNode ship) =>
        ((JsonArray)ship["aRooms"]!).Select(r => (string)r!["strID"]!).ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> IdsOf(JsonNode ship, string prop) =>
        ((JsonArray)ship[prop]!).Select(n => (string?)n!["strID"]).Where(s => s is not null).ToHashSet(StringComparer.Ordinal)!;

    /// <summary>The source ship's item/CO counts MINUS its room COs — the baseline every inject starts from,
    /// because rooms are always rebuilt with fresh strIDs and the originals dropped. Tests below express their
    /// expectations as a delta off this (+1 for an added part, −1 for a deleted one).</summary>
    private static (int Items, int COs) Baseline(JsonNode rec)
    {
        var rooms = RoomIds(rec).Count;
        return (Count(rec, "aItems") - rooms, Count(rec, "aCOs") - rooms);
    }

    [SkippableFact]
    public void Noop_inject_preserves_every_item_and_co_except_the_rooms_it_rebuilds()
    {
        var g = TestData.RequireGame();
        if (FirstImport(g.Env, g.Catalog) is not { } r) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);

        var rec = r.Context.ShipRecord;
        var origItems = Count(rec, "aItems");
        var origCOs = Count(rec, "aCOs");
        var rooms = RoomIds(rec);
        Skip.If(rooms.Count == 0, "this save's ship has no rooms to exercise");

        var (ship, report) = SaveEdit.BuildInjectedShip(r.Doc, r.Context, g.Catalog, specs);

        // the ONLY entries an inject removes are the room COs: rooms are rebuilt with fresh strIDs, so the
        // originals must go or they linger as unbound IsRoom COs (ghost rooms)
        Assert.Equal(origItems - rooms.Count, Count(ship, "aItems"));
        Assert.Equal(origCOs - rooms.Count, Count(ship, "aCOs"));
        Assert.Empty(IdsOf(ship, "aItems").Intersect(rooms));
        Assert.Empty(IdsOf(ship, "aCOs").Intersect(rooms));
        // every non-room original survives untouched
        Assert.Empty(IdsOf(rec, "aItems").Except(rooms).Except(IdsOf(ship, "aItems")));
        Assert.Empty(IdsOf(rec, "aCOs").Except(rooms).Except(IdsOf(ship, "aCOs")));

        Assert.Equal(0, report.Added);
        Assert.Equal(0, report.Moved);
        Assert.Equal(0, report.Deleted);
        Assert.Empty(report.CargoDropped);
        _out.WriteLine($"no-op: {report.Kept} kept, {origItems} items, {origCOs} COs, {rooms.Count} rooms rebuilt, {report.NCols}x{report.NRows}, reframed={report.GridReframed}, {report.Warnings.Count} warnings");
    }

    [SkippableFact]
    public void Adding_a_part_adds_one_item_and_a_synthesized_pristine_co()
    {
        var g = TestData.RequireGame();
        if (FirstImport(g.Env, g.Catalog) is not { } r) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);
        if (r.Doc.Bounds() is not { } b) return;

        var (items0, cos0) = Baseline(r.Context.ShipRecord);
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

    [SkippableFact]
    public void Reskinning_a_part_gives_the_new_skin_a_co_so_it_is_not_skipped_on_load()
    {
        // regression for the catastrophic in-game failure: re-skinning drops identity (def change = new part),
        // so every re-skinned tile needs a synthesized CO or the game logs "missing save data" and skips it.
        var g = TestData.RequireGame();
        if (FirstImport(g.Env, g.Catalog) is not { } r) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);

        var target = r.Doc.Placements.FirstOrDefault(p =>
            !r.Doc.IsLocked(p) && ReplaceOps.CommonClass(r.Doc, [p]) is { W: 1, H: 1 });
        if (target is null) return;
        var cls = ReplaceOps.CommonClass(r.Doc, [target])!.Value;
        if (ReplaceOps.CompatibleTargets(g.Catalog, cls).FirstOrDefault(d => d.DefName != target.DefName) is not { } other) return;

        var (_, cos0) = Baseline(r.Context.ShipRecord);
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
        // An imported (given) part is immune — the game never re-validates existing structure — but MOVING it is an
        // authoring act: given-ness clears and the placement law re-applies, while its save identity (OriginStrID)
        // is kept (a move, not a delete+new). Deterministic on a synthetic ship, so the invariant isn't gated on
        // whatever ship happens to be in the newest save (which made this flaky); the live-save import that produces
        // the given+OriginStrID tags is covered by the other tests here. The fixture needs a sealed floor beneath
        // it, so once dragged into open space it must flag; a docking port keeps ProblemScan past its no-port
        // early-return so the legality pass actually runs.
        const string B = "Blank";
        var cat = new Fixtures()
            .Trig(ProblemScan.DocksysTrigger, ["IsDockSys", "IsInstalled"])
            .Part("Dock", startingConds: ["IsDockSys", "IsInstalled"], category: "HULL")
            .Floor("Floor")
            .Loot("ReqFloor", "IsFloor", "IsFloorSealed")
            .Part("MyFixture", tileConds: ["IsFixture", "IsObstruction"],
                reqs: [B, B, B, B, "ReqFloor", B, B, B, B], category: "FURN")   // needs a sealed floor on its own (centre) cell
            .Build();

        var doc = new ShipDocument(cat);
        new PlaceCommand(new Placement { DefName = "Dock", X = 0, Y = 8 }).Do(doc);    // a port -> ProblemScan runs the legality pass
        new PlaceCommand(new Placement { DefName = "Floor", X = 0, Y = 0 }).Do(doc);
        var fixture = new Placement { DefName = "MyFixture", X = 0, Y = 0, OriginStrID = "sid-fixture", IsGiven = true };
        new PlaceCommand(fixture).Do(doc);   // a valid, imported (given) fixture resting on its floor

        bool FixtureFlagged() => ProblemScan.Scan(doc, cat)
            .Any(p => p.Severity == ProblemSeverity.Blocking && p.Detail.Contains("MyFixture"));

        Assert.True(fixture.IsGiven);     // imported -> immune while unmoved
        Assert.False(FixtureFlagged());   // ...so the law does not flag it

        new MoveCommand([fixture], 40, 40).Do(doc);   // drag it off its floor into open space

        Assert.False(fixture.IsGiven);                      // the move re-authored it...
        Assert.Equal("sid-fixture", fixture.OriginStrID);   // ...but kept its save identity (a move, not delete+new)...
        Assert.True(FixtureFlagged());                      // ...and the law now flags the unsupported fixture
    }

    [SkippableFact]
    public void Moving_a_part_keeps_its_exact_condition_and_condition_owner()
    {
        // answers "does moving a wall keep it in the same condition?": a moved part keeps its strID, its item
        // entry (incl. aCondOverrides = wear/damage) verbatim bar the pose, and its whole CO (all live state).
        var g = TestData.RequireGame();
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

    [SkippableFact]
    public void Deleting_a_cargoless_part_drops_its_item_and_co()
    {
        var g = TestData.RequireGame();
        if (FirstImport(g.Env, g.Catalog) is not { } r) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);

        var target = r.Doc.Placements.FirstOrDefault(p =>
            !r.Doc.IsLocked(p) && p.OriginStrID is { } id && r.Context.Origins[id].CargoIds.Count == 0);
        if (target is null) return;

        var (items0, cos0) = Baseline(r.Context.ShipRecord);
        new RemoveCommand([target]).Do(r.Doc);

        var (ship, report) = SaveEdit.BuildInjectedShip(r.Doc, r.Context, g.Catalog, specs);

        Assert.Equal(1, report.Deleted);
        Assert.Empty(report.CargoDropped);
        Assert.Equal(items0 - 1, Count(ship, "aItems"));   // item gone...
        Assert.Equal(cos0 - 1, Count(ship, "aCOs"));       // ...and its 1:1 condition owner with it
    }

    [SkippableFact]
    public void Reskinning_a_container_preserves_and_reparents_its_cargo()
    {
        // the inject's cargo SAFETY NET: the UI no longer lets you re-skin a container (ReplaceOps excludes them),
        // but if a def-change ever carries cargo the inject must keep it — re-parented onto the new part, nothing
        // reported lost — rather than dropping it. This drives that defensive path directly (bypassing the UI guard).
        var g = TestData.RequireGame();
        var specs = RoomCertifier.LoadSpecs(g.Index);
        if (ImportWhere(g.Env, g.Catalog, d => d.Placements.Any(p => !d.IsLocked(p) && p.Cargo.Count > 0)) is not { } r) return;

        var target = r.Doc.Placements.First(p => !r.Doc.IsLocked(p) && p.Cargo.Count > 0);
        var cargoIds = target.Cargo.SelectMany(c => c.SubtreeIds()).ToHashSet();
        var topLevel = target.Cargo.Select(c => c.StrID).ToList();
        var oldId = target.OriginStrID;

        // exactly what ReplaceOps.BuildSwap produces: remove the container, place a fresh part carrying its cargo
        var repl = new Placement { DefName = target.DefName, X = target.X, Y = target.Y, Rot = target.Rot, Cargo = target.Cargo };
        new RemoveCommand([target]).Do(r.Doc);
        new PlaceCommand(repl).Do(r.Doc);

        var (ship, report) = SaveEdit.BuildInjectedShip(r.Doc, r.Context, g.Catalog, specs);

        Assert.Empty(report.CargoDropped);   // a re-skin loses no cargo
        var itemsById = ((JsonArray)ship["aItems"]!).Select(n => n!.AsObject())
            .Where(o => (string?)o["strID"] is not null).ToDictionary(o => (string)o["strID"]!);
        foreach (var cid in cargoIds) Assert.True(itemsById.ContainsKey(cid), $"cargo {cid} was dropped");

        // every top-level item is re-parented onto the NEW container (a fresh id), not the deleted original
        string? Parent(JsonObject o) => (string?)o["strParentID"] ?? (string?)o["strSlotParentID"];
        var newParent = Parent(itemsById[topLevel[0]]);
        Assert.NotNull(newParent);
        Assert.NotEqual(oldId, newParent);
        Assert.True(itemsById.ContainsKey(newParent!));
        Assert.Equal(target.DefName, (string?)itemsById[newParent!]["strName"]);
        foreach (var tl in topLevel) Assert.Equal(newParent, Parent(itemsById[tl]));
    }

    [SkippableFact]
    public void Deleting_a_container_reports_and_drops_its_cargo()
    {
        // the other side of the fix: a genuine delete (not a re-skin) still reports the lost cargo and drops it.
        var g = TestData.RequireGame();
        var specs = RoomCertifier.LoadSpecs(g.Index);
        if (ImportWhere(g.Env, g.Catalog, d => d.Placements.Any(p => !d.IsLocked(p) && p.Cargo.Count > 0)) is not { } r) return;

        var target = r.Doc.Placements.First(p => !r.Doc.IsLocked(p) && p.Cargo.Count > 0);
        var cargoIds = r.Context.Origins[target.OriginStrID!].CargoIds;

        new RemoveCommand([target]).Do(r.Doc);
        var (ship, report) = SaveEdit.BuildInjectedShip(r.Doc, r.Context, g.Catalog, specs);

        Assert.Equal(1, report.Deleted);
        Assert.Single(report.CargoDropped);   // reported...
        var itemIds = ((JsonArray)ship["aItems"]!).Select(n => (string?)n!["strID"]).ToHashSet();
        foreach (var cid in cargoIds) Assert.DoesNotContain(cid, itemIds);   // ...and actually gone
    }

    [SkippableFact]
    public void Reskinning_a_nav_console_keeps_its_modules_and_adds_no_generic_set()
    {
        // nav auto-inject is empty-only: a console that already carries modules (kept, or transferred through a
        // re-skin) keeps exactly those — it must NOT also receive the generic 14-module set.
        var g = TestData.RequireGame();
        var specs = RoomCertifier.LoadSpecs(g.Index);
        if (ImportWhere(g.Env, g.Catalog, d => d.Placements.Any(p =>
                !d.IsLocked(p) && NavConsole.IsConsole(d.Part(p)) && p.Cargo.Count > 0)) is not { } r) return;

        var console = r.Doc.Placements.First(p => !r.Doc.IsLocked(p) && NavConsole.IsConsole(r.Doc.Part(p)) && p.Cargo.Count > 0);
        var moduleIds = console.Cargo.SelectMany(c => c.SubtreeIds()).ToHashSet();
        var topCount = console.Cargo.Count;   // top-level modules on the console

        var repl = new Placement { DefName = console.DefName, X = console.X, Y = console.Y, Rot = console.Rot, Cargo = console.Cargo };
        new RemoveCommand([console]).Do(r.Doc);
        new PlaceCommand(repl).Do(r.Doc);

        var (ship, _) = SaveEdit.BuildInjectedShip(r.Doc, r.Context, g.Catalog, specs);
        var items = ((JsonArray)ship["aItems"]!).Select(n => n!.AsObject()).ToList();

        var itemIds = items.Select(o => (string?)o["strID"]).ToHashSet();
        foreach (var mid in moduleIds) Assert.Contains(mid, itemIds);   // real modules preserved

        // the new console has exactly the transferred top-level modules parented to it — no generic set appended
        string? Parent(JsonObject o) => (string?)o["strParentID"] ?? (string?)o["strSlotParentID"];
        var newConsoleId = Parent(items.First(o => (string?)o["strID"] == console.Cargo[0].StrID));
        Assert.NotNull(newConsoleId);
        var childCount = items.Count(o => Parent(o) == newConsoleId);
        Assert.Equal(topCount, childCount);
    }

    [SkippableFact]
    public void Injected_copy_has_a_co_for_every_item_after_reskin_and_add()
    {
        // The reported catastrophe, at the file level: re-skin a batch of tiles (def change -> new parts) and
        // add one, then confirm the WRITTEN save has a CO for every item — the invariant DataHandler.SpawnItems
        // enforces (an item without a CO is skipped on load).
        var g = TestData.RequireGame();
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

    [SkippableFact]
    public void Adding_a_powered_device_bakes_its_power_ticker_so_it_can_draw_power()
    {
        // the reported dead-device bug: an injected device with no Power ticker never gets IsReadyUsePower, so it
        // can't draw power even once conduits reach it. A synthesized CO must carry the def's Power ticker.
        var g = TestData.RequireGame();
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

    [SkippableFact]
    public void Injected_ship_is_armed_to_refill_atmosphere_and_marked_pristine()
    {
        // the edit regenerates aRooms, orphaning per-room gas -> the ship would spawn airless. bPrefill makes the
        // game refill it on load; marking the hull pristine (DMGStatus New) keeps that fill safe on a Used/Damaged
        // ship (else bPrefill fires the break-in/damage path).
        var g = TestData.RequireGame();
        if (FirstImport(g.Env, g.Catalog) is not { } r) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);

        var (ship, report) = SaveEdit.BuildInjectedShip(r.Doc, r.Context, g.Catalog, specs);

        Assert.True(report.AtmosphereFilled);
        Assert.True((bool)ship["bPrefill"]!);                 // the game runs PreFillRooms on load...
        Assert.Equal(0, ship["DMGStatus"]!.GetValue<int>());  // ...and the hull is marked pristine so it's safe
    }

    [SkippableFact]
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

    [SkippableFact]
    public void Adding_a_powered_device_bakes_its_gpm_so_it_wires_on_load()
    {
        var g = TestData.RequireGame();
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

    [SkippableFact]
    public void Charging_an_edit_deducts_statusd_on_the_player_co_and_saveinfo_money()
    {
        // the cost deduction: rewrite the player CO's StatUSD (authoritative balance) and mirror saveInfo.money
        // into the written copy; the player CO lives on their own ship, so it's the file we already splice.
        var g = TestData.RequireGame();
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

    [SkippableFact]
    public void WriteCopy_makes_a_loadable_copy_and_leaves_the_original_untouched()
    {
        var g = TestData.RequireGame();
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

            // its spliced ship record parses and — this was a no-op inject — round-trips every item bar the
            // room COs it rebuilds
            var injected = ReadShipEntry(Path.Combine(outDir, name + ".zip"), r.Context.Source.RegId);
            var shipNode = ShipTemplate.ParseFile(injected).OrderByDescending(s => s.Items.Count).FirstOrDefault();
            Assert.NotNull(shipNode);
            Assert.Equal(Baseline(r.Context.ShipRecord).Items, shipNode!.Items.Count);

            // the ORIGINAL save is byte-for-byte what it was
            Assert.Equal(originalBefore, ReadShipEntry(r.Context.ZipPath, r.Context.Source.RegId));
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); }
    }

    [SkippableFact]
    public void Adding_a_nav_console_installs_the_standard_module_set_with_cos()
    {
        // a newly-added nav console spawns as an empty frame — the inject must add the standard module set as
        // contained children, each with a synthesized CO (a save load skips any item lacking one).
        var g = TestData.RequireGame();
        if (FirstImport(g.Env, g.Catalog) is not { } r) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);
        if (r.Doc.Bounds() is not { } b) return;
        if (g.Catalog.Lookup("ItmStationNav") is not { } consoleDef || !NavConsole.IsConsole(consoleDef)) return;

        var (items0, cos0) = Baseline(r.Context.ShipRecord);
        var n = NavConsole.StandardModules.Count;

        new PlaceCommand(new Placement { DefName = "ItmStationNav", X = b.MinX, Y = b.MinY }).Do(r.Doc);
        var (ship, report) = SaveEdit.BuildInjectedShip(r.Doc, r.Context, g.Catalog, specs);

        Assert.Equal(1, report.Added);                       // one placement (the console); its modules ride along
        Assert.Equal(items0 + 1 + n, Count(ship, "aItems")); // console + N modules
        Assert.Equal(cos0 + 1 + n, Count(ship, "aCOs"));     // each gets a CO (DataHandler.SpawnItems skips CO-less items)

        var items = ((JsonArray)ship["aItems"]!).Select(x => x!.AsObject()).ToList();
        var consoleId = (string?)items.Last(it => (string?)it["strName"] == "ItmStationNav")["strID"];
        var modules = items.Where(it => (string?)it["strParentID"] == consoleId).ToList();
        Assert.Equal(NavConsole.StandardModules.OrderBy(x => x),
                     modules.Select(m => (string?)m["strName"]).OrderBy(x => x));   // the full set, parented to the console

        var coIds = ((JsonArray)ship["aCOs"]!).Select(c => (string?)c!["strID"]).ToHashSet();
        Assert.All(modules, m => Assert.Contains((string?)m["strID"], coIds));      // every module has its 1:1 CO
    }

    [SkippableFact]
    public void Imported_placements_carry_their_container_cargo()
    {
        // save-import now attaches each container's contents tree to its placement (Placement.Cargo). The trees
        // must cover exactly the subtree the context recorded, with unique ids that resolve to real save items.
        var g = TestData.RequireGame();
        if (FirstImport(g.Env, g.Catalog) is not { } r) return;

        var cargoIds = r.Doc.Placements.SelectMany(p => p.Cargo.SelectMany(c => c.SubtreeIds())).ToList();

        var originCargoCount = r.Context.Origins.Values.Sum(o => o.CargoIds.Count);
        Assert.Equal(originCargoCount, cargoIds.Count);                       // covers exactly the recorded subtree
        Assert.Equal(cargoIds.Count, cargoIds.Distinct().Count());           // each contained item appears once
        Assert.All(cargoIds, id => Assert.True(r.Context.ItemsById.ContainsKey(id)));   // and is a real save item

        // if this ship has a nav console, its modules are attached as cargo — the container we were losing
        var console = r.Doc.Placements.FirstOrDefault(p => p.DefName == "ItmStationNav" && p.Cargo.Count > 0);
        if (console is not null)
            Assert.Contains(console.Cargo, c => c.DefName.StartsWith("ItmNavMod", System.StringComparison.Ordinal));
    }

    [SkippableFact]
    public void Adding_cargo_to_a_kept_container_writes_a_new_item_and_co_and_drops_nothing()
    {
        // Phase 4: an item added to a container's contents becomes a fresh authored item, parented to the
        // container, with a synthesized pristine CO — and no original cargo is dropped or warned about.
        var g = TestData.RequireGame();
        var specs = RoomCertifier.LoadSpecs(g.Index);
        if (ImportWhere(g.Env, g.Catalog, d => d.Placements.Any(p =>
                !d.IsLocked(p) && d.Part(p)?.ContainerGrid is not null)) is not { } r) return;

        // find a container with room that accepts a sprite-bearing item, and add one
        Placement? container = null;
        PartDef? itemDef = null;
        IReadOnlyList<CargoItem>? updated = null;
        foreach (var p in r.Doc.Placements.Where(p => !r.Doc.IsLocked(p) && r.Doc.Part(p)?.ContainerGrid is not null))
        {
            var def = r.Doc.Part(p)!;
            var item = ContainerFilter.AcceptedBy(g.Catalog, def).FirstOrDefault(i => i.SpriteAbs is not null);
            if (item is null) continue;
            if (CargoEdit.Add(p.Cargo, null, def.ContainerGrid!.Value, item, 1) is not { } u) continue;
            container = p; itemDef = item; updated = u; break;
        }
        if (container is null) return;   // no container with room found in this save

        var (items0, cos0) = Baseline(r.Context.ShipRecord);
        new SetCargoCommand(container, container.Cargo, updated!).Do(r.Doc);

        var (ship, report) = SaveEdit.BuildInjectedShip(r.Doc, r.Context, g.Catalog, specs);

        Assert.Empty(report.CargoDropped);                 // adding drops nothing
        Assert.Equal(items0 + 1, Count(ship, "aItems"));   // +1 authored item...
        Assert.Equal(cos0 + 1, Count(ship, "aCOs"));       // ...and its synthesized CO

        var containerId = container.OriginStrID!;
        var authored = ((JsonArray)ship["aItems"]!).Select(n => n!.AsObject())
            .Where(o => (string?)o["strParentID"] == containerId && !r.Context.ItemsById.ContainsKey((string)o["strID"]!)).ToList();
        var it = Assert.Single(authored);                  // exactly one new item, parented to the container
        Assert.Equal(itemDef!.DefName, (string?)it["strName"]);
        var co = ((JsonArray)ship["aCOs"]!).Select(n => n!.AsObject()).First(c => (string?)c["strID"] == (string?)it["strID"]);
        Assert.Contains("DEFAULT", ((JsonArray)co["aConds"]!).Select(n => (string?)n));   // pristine on load
    }

    [SkippableFact]
    public void Removing_cargo_from_a_kept_container_drops_the_item_without_a_loss_warning()
    {
        // removing an item from a KEPT container is an intended edit: the item + its 1:1 CO are dropped, and
        // NOTHING is reported as lost (a content edit must not fire the deleted-container "cargo will be gone" warning).
        var g = TestData.RequireGame();
        var specs = RoomCertifier.LoadSpecs(g.Index);
        if (ImportWhere(g.Env, g.Catalog, d => d.Placements.Any(p => !d.IsLocked(p)
                && p.Cargo.Any(c => !c.IsStack && c.Children.Count == 0 && !c.Slotted))) is not { } r) return;

        var container = r.Doc.Placements.First(p => !r.Doc.IsLocked(p)
            && p.Cargo.Any(c => !c.IsStack && c.Children.Count == 0 && !c.Slotted));
        var victim = container.Cargo.First(c => !c.IsStack && c.Children.Count == 0 && !c.Slotted);
        if (!r.Context.CosById.ContainsKey(victim.StrID)) return;   // expect the 1:1 CO

        var (items0, cos0) = Baseline(r.Context.ShipRecord);
        new SetCargoCommand(container, container.Cargo, CargoEdit.RemoveWhole(container.Cargo, victim.StrID)).Do(r.Doc);

        var (ship, report) = SaveEdit.BuildInjectedShip(r.Doc, r.Context, g.Catalog, specs);

        Assert.Empty(report.CargoDropped);                 // an intended edit, not a warned loss
        Assert.Equal(items0 - 1, Count(ship, "aItems"));   // the item is gone...
        Assert.Equal(cos0 - 1, Count(ship, "aCOs"));       // ...with its condition owner
        var ids = ((JsonArray)ship["aItems"]!).Select(n => (string?)n!["strID"]).ToHashSet();
        Assert.DoesNotContain(victim.StrID, ids);
    }

    [SkippableFact]
    public void Oplan_persists_and_restores_an_edited_containers_cargo_snapshot()
    {
        // authored cargo survives .oplan save + reopen (full snapshot): FromDocument stores it for the edited
        // container, ToDocument restores it and re-marks the part edited so a re-save persists it again.
        var g = TestData.RequireGame();
        var containerDef = g.Catalog.Parts.FirstOrDefault(p => p.ContainerGrid is not null
            && ContainerFilter.AcceptedBy(g.Catalog, p).Any(i => i.SpriteAbs is not null));
        if (containerDef is null) return;
        var itemDef = ContainerFilter.AcceptedBy(g.Catalog, containerDef).First(i => i.SpriteAbs is not null);

        var doc = new ShipDocument(g.Catalog);
        var p = new Placement { DefName = containerDef.DefName };
        new PlaceCommand(p).Do(doc);
        if (CargoEdit.Add(p.Cargo, null, containerDef.ContainerGrid!.Value, itemDef, 3) is not { } added) return;
        new SetCargoCommand(p, p.Cargo, added).Do(doc);
        Assert.True(doc.IsCargoEdited(p));

        var file = OplanFile.FromDocument(doc, g.Index, new OplanMeta());
        Assert.NotNull(Assert.Single(file.Parts).Cargo);   // snapshot persisted for the edited container

        var (doc2, missing) = file.ToDocument(g.Catalog);
        Assert.Empty(missing);
        var p2 = Assert.Single(doc2.Placements);
        Assert.True(doc2.IsCargoEdited(p2));                          // restored + re-marked edited
        Assert.Equal(added.Count, p2.Cargo.Count);                   // same shape...
        Assert.Equal(added.Sum(c => c.Stack), p2.Cargo.Sum(c => c.Stack));   // ...same total quantity
        Assert.All(p2.Cargo, c => Assert.True(c.Authored));
    }

    [SkippableFact]
    public void Oplan_persists_no_cargo_snapshot_for_an_unedited_container()
    {
        var g = TestData.RequireGame();
        var containerDef = g.Catalog.Parts.FirstOrDefault(p => p.ContainerGrid is not null);
        if (containerDef is null) return;
        var doc = new ShipDocument(g.Catalog);
        new PlaceCommand(new Placement { DefName = containerDef.DefName }).Do(doc);
        var file = OplanFile.FromDocument(doc, g.Index, new OplanMeta());
        Assert.Null(Assert.Single(file.Parts).Cargo);   // un-edited container -> cargo re-read from the save, not stored
    }

    [SkippableFact]
    public void Moving_original_cargo_writes_its_new_grid_position_to_the_co()
    {
        // rearranging an original item persists: its CO's inventoryX/inventoryY are overwritten to the new cell.
        var g = TestData.RequireGame();
        var specs = RoomCertifier.LoadSpecs(g.Index);
        if (ImportWhere(g.Env, g.Catalog, d => d.Placements.Any(p => !d.IsLocked(p)
                && (d.Part(p)?.ContainerGrid ?? (0, 0)).Item1 >= 2
                && p.Cargo.Any(c => !c.IsStack && !c.Slotted && c.Children.Count == 0))) is not { } r) return;

        var container = r.Doc.Placements.First(p => !r.Doc.IsLocked(p)
            && (r.Doc.Part(p)?.ContainerGrid ?? (0, 0)).Item1 >= 2
            && p.Cargo.Any(c => !c.IsStack && !c.Slotted && c.Children.Count == 0));
        var grid = r.Doc.Part(container)!.ContainerGrid!.Value;
        var item = container.Cargo.First(c => !c.IsStack && !c.Slotted && c.Children.Count == 0);
        if (!r.Context.CosById.ContainsKey(item.StrID)) return;

        var tx = grid.W - 1;   // a distinct column (grid is ≥2 wide)
        if (CargoEdit.Move(container.Cargo, item.StrID, null, grid, tx, 0) is not { } moved) return;   // too full to relocate
        new SetCargoCommand(container, container.Cargo, moved).Do(r.Doc);

        var (ship, report) = SaveEdit.BuildInjectedShip(r.Doc, r.Context, g.Catalog, specs);
        Assert.Empty(report.CargoDropped);
        var ids = ((JsonArray)ship["aItems"]!).Select(n => (string?)n!["strID"]).ToHashSet();
        Assert.Contains(item.StrID, ids);   // still present (moved, not dropped)
        var co = ((JsonArray)ship["aCOs"]!).Select(n => n!.AsObject()).First(c => (string?)c["strID"] == item.StrID);
        Assert.Equal(tx, co["inventoryX"]!.GetValue<int>());
        Assert.Equal(0, co["inventoryY"]!.GetValue<int>());
    }

    [SkippableFact]
    public void Rotating_original_cargo_writes_its_rotation_to_the_item()
    {
        // rotating an original item persists to its aItems fRotation (the field the game reads inventory rotation from).
        var g = TestData.RequireGame();
        var specs = RoomCertifier.LoadSpecs(g.Index);
        if (ImportWhere(g.Env, g.Catalog, d => d.Placements.Any(p => !d.IsLocked(p)
                && d.Part(p)?.ContainerGrid is not null
                && p.Cargo.Any(c => !c.IsStack && !c.Slotted))) is not { } r) return;

        var container = r.Doc.Placements.First(p => !r.Doc.IsLocked(p)
            && r.Doc.Part(p)?.ContainerGrid is not null
            && p.Cargo.Any(c => !c.IsStack && !c.Slotted));
        var grid = r.Doc.Part(container)!.ContainerGrid!.Value;
        var item = container.Cargo.First(c => !c.IsStack && !c.Slotted);
        if (item.GridRot != 0) return;   // start from unrotated so a 90° write is observable

        if (CargoEdit.Rotate(container.Cargo, item.StrID, null, grid) is not { } rotated) return;
        new SetCargoCommand(container, container.Cargo, rotated).Do(r.Doc);

        var (ship, _) = SaveEdit.BuildInjectedShip(r.Doc, r.Context, g.Catalog, specs);
        var it = ((JsonArray)ship["aItems"]!).Select(n => n!.AsObject()).First(o => (string?)o["strID"] == item.StrID);
        Assert.Equal(90.0, it["fRotation"]!.GetValue<double>());
    }

    [SkippableFact]
    public void Oplan_round_trips_an_authored_items_rotation()
    {
        var g = TestData.RequireGame();
        var containerDef = g.Catalog.Parts.FirstOrDefault(p => p.ContainerGrid is not null
            && ContainerFilter.AcceptedBy(g.Catalog, p).Any(i => i.SpriteAbs is not null));
        if (containerDef is null) return;
        var itemDef = ContainerFilter.AcceptedBy(g.Catalog, containerDef).First(i => i.SpriteAbs is not null);
        var grid = containerDef.ContainerGrid!.Value;

        var doc = new ShipDocument(g.Catalog);
        var p = new Placement { DefName = containerDef.DefName };
        new PlaceCommand(p).Do(doc);
        if (CargoEdit.Add(p.Cargo, null, grid, itemDef, 1) is not { } added) return;
        var itemId = added[0].StrID;
        if (CargoEdit.Rotate(added, itemId, null, grid) is not { } rotated) return;
        new SetCargoCommand(p, p.Cargo, rotated).Do(doc);

        var file = OplanFile.FromDocument(doc, g.Index, new OplanMeta());
        var (doc2, _) = file.ToDocument(g.Catalog);
        var restored = doc2.Placements.Single().Cargo.Single();
        Assert.Equal(90, restored.GridRot);   // rotation survives save + reopen
    }
}
