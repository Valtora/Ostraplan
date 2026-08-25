using System.IO;
using System.Linq;
using System.Text.Json;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// Device signal connections, both of the game's channels: the <c>Electrical</c> GPM breaker graph and the sensor
/// a device follows through its own panel's <c>strInput01</c>. Covers the validity rules (which come from the defs'
/// declared panels, not from a hardcoded list), undoable add/remove, the one-sensor-per-device rule, the
/// <c>.oplan</c> index-pair round-trip and the migration of legacy single-channel files, and — against the live
/// install — what an export actually bakes onto each item.
/// </summary>
public class DeviceLinkTests
{
    // The two panel templates that matter, verbatim in shape from data/guipropmaps: a breaker box admits anything
    // installed and signalable, an air pump admits any alarm and takes exactly one. Built as the game's flat
    // alternating key/value array, since that is what the resolver has to cope with.
    private static readonly string[] BreakerPanel =
        ["strGUIPrefab", "GUIBreaker", "strValidCOTrigger01", "TIsSignalOpen", "strInput1", "", "strInput2", ""];
    private static readonly string[] PumpPanel =
        ["strGUIPrefab", "GUIAirPump", "strValidCOTrigger01", "TIsAlarm2", "strCondMonitor01", "IsReadyPumpAir", "strInput01", ""];
    private static readonly string[] ThermoSinkPanel =
        ["strGUIPrefab", "GUIAirPump", "strValidCOTrigger01", "TIsAlarmTemp", "strCondMonitor01", "DcGasTemp01", "strInput01", ""];

    private static JsonElement Flat(string[] keyValues) =>
        JsonDocument.Parse(JsonSerializer.Serialize(keyValues)).RootElement.Clone();

    private static CondTriggerDef Trig(string name, params string[] reqs) => new(name, reqs, [], false);

    private static Catalog Fake(params PartDef[] parts) => new()
    {
        Parts = parts,
        ByDefName = parts.ToDictionary(p => p.DefName),
        Loots = new Dictionary<string, LootDef>(),
        Triggers = new Dictionary<string, CondTriggerDef>
        {
            ["TIsSignalOpen"] = Trig("TIsSignalOpen", "IsSignalable", "IsInstalled"),
            ["TIsAlarm2"] = Trig("TIsAlarm2", "IsAlarm2"),
            ["TIsAlarmTemp"] = Trig("TIsAlarmTemp", "IsAlarmTemp"),
        },
        GpmTemplates = new Dictionary<string, JsonElement>
        {
            ["ElectricalBox01"] = Flat(BreakerPanel),
            ["AirPump"] = Flat(PumpPanel),
            ["Heater"] = Flat(ThermoSinkPanel),
        },
        Warnings = [],
    };

    /// <summary>A 1×1 part with the given starting conds, and optionally a declared control panel.</summary>
    private static PartDef MakePart(string name, string[] conds, string? panel = null) => new(
        name, name, "SENS", "core",
        new ItemDef(name, "", false, null, 0, 1, ["L"], [], []),
        null, [], [], conds, new Dictionary<string, double>(), new Dictionary<string, (double, double)>())
    {
        Gpm = panel is null ? [] : [("Panel A", panel)],
    };

    /// <summary>A plain installed signalable device — a valid breaker TARGET, but nothing's source.</summary>
    private static PartDef Device(string name) => MakePart(name, ["IsSignalable", "IsInstalled"]);

    /// <summary>A signal box: the only thing that can source a breaker connection.</summary>
    private static PartDef Breaker(string name) =>
        MakePart(name, ["IsSignalable", "IsInstalled", "IsElectricalBox"], "ElectricalBox01");

    /// <summary>An alarm: satisfies <c>TIsAlarm2</c>, so it can drive a pump.</summary>
    private static PartDef Alarm(string name) => MakePart(name, ["IsSignalable", "IsInstalled", "IsAlarm2"]);

    /// <summary>A pump: takes one sensor, and only an alarm.</summary>
    private static PartDef Pump(string name, params string[] extraConds) =>
        MakePart(name, ["IsSignalable", "IsInstalled", .. extraConds], "AirPump");

    private static Placement Place(ShipDocument doc, string def, int x, int y)
    {
        var p = new Placement { DefName = def, X = x, Y = y };
        new PlaceCommand(p).Do(doc);
        return p;
    }

    // ---- the breaker channel ----

    [Fact]
    public void Only_a_breaker_box_can_source_an_electrical_connection()
    {
        // The rule the game applies: GUIBreaker.SetInput is the one thing that calls Electrical.SetUpConnection,
        // so a part with no breaker panel can be driven but can never drive. Ostraplan used to let any signalable
        // part source one, which is how alarm→pump links were authorable on the channel that ignores them.
        var cat = Fake(Breaker("Box"), Alarm("Alarm"), Pump("Pump"), Device("Light"));
        var doc = new ShipDocument(cat);
        var box = Place(doc, "Box", 0, 0);
        var alarm = Place(doc, "Alarm", 2, 0);
        var pump = Place(doc, "Pump", 4, 0);
        var light = Place(doc, "Light", 6, 0);

        Assert.True(DeviceLinks.CanSource(doc, box));
        Assert.False(DeviceLinks.CanSource(doc, alarm));
        Assert.False(DeviceLinks.CanSource(doc, light));

        Assert.True(DeviceLinks.CanConnect(doc, box, light));
        Assert.True(DeviceLinks.CanConnect(doc, box, pump));
        Assert.False(DeviceLinks.CanConnect(doc, box, box));      // self
        Assert.False(DeviceLinks.CanConnect(doc, alarm, pump));   // the wrong channel entirely
    }

    [Fact]
    public void Breaker_target_rule_comes_from_the_breakers_own_panel_trigger()
    {
        // TIsSignalOpen = IsSignalable + IsInstalled, read off the box's panel rather than hardcoded, so a def
        // missing either is refused.
        var cat = Fake(Breaker("Box"), Device("Light"), MakePart("Wall", ["IsWall"]),
            MakePart("LooseLight", ["IsSignalable"]));   // signalable but NOT installed
        var doc = new ShipDocument(cat);
        var box = Place(doc, "Box", 0, 0);
        var light = Place(doc, "Light", 2, 0);
        var wall = Place(doc, "Wall", 4, 0);
        var loose = Place(doc, "LooseLight", 6, 0);

        Assert.True(DeviceLinks.CanConnect(doc, box, light));
        Assert.False(DeviceLinks.CanConnect(doc, box, wall));    // not signalable
        Assert.False(DeviceLinks.CanConnect(doc, box, loose));   // not installed
    }

    [Fact]
    public void AddLink_and_RemoveLink_are_undoable_and_dedup()
    {
        var cat = Fake(Breaker("Box"), Device("B"));
        var doc = new ShipDocument(cat);
        var stack = new CommandStack();
        var a = Place(doc, "Box", 0, 0);
        var b = Place(doc, "B", 2, 0);
        var link = new DeviceLink(a.Id, b.Id);

        stack.Push(doc, new AddLinkCommand(link));
        Assert.Single(doc.Links);
        Assert.False(DeviceLinks.CanConnect(doc, a, b));   // already linked → not addable again

        new AddLinkCommand(link).Do(doc);              // exact duplicate is a no-op
        Assert.Single(doc.Links);

        stack.Undo(doc);
        Assert.Empty(doc.Links);
        stack.Redo(doc);
        Assert.Single(doc.Links);

        stack.Push(doc, new RemoveLinkCommand(link));
        Assert.Empty(doc.Links);
    }

    // ---- the sensor channel ----

    [Fact]
    public void A_device_takes_only_the_sensor_its_own_panel_admits()
    {
        var cat = Fake(Alarm("Alarm"), MakePart("Thermostat", ["IsSignalable", "IsInstalled", "IsAlarmTemp"]),
            Pump("Pump"), MakePart("Heater", ["IsSignalable", "IsInstalled"], "Heater"), Device("Light"));
        var doc = new ShipDocument(cat);
        var alarm = Place(doc, "Alarm", 0, 0);
        var thermo = Place(doc, "Thermostat", 2, 0);
        var pump = Place(doc, "Pump", 4, 0);
        var heater = Place(doc, "Heater", 6, 0);
        var light = Place(doc, "Light", 8, 0);

        Assert.True(SensorLinks.CanDrive(doc, alarm, pump));       // TIsAlarm2 admits any alarm
        Assert.False(SensorLinks.CanDrive(doc, thermo, pump));     // the thermostat is not an IsAlarm2
        Assert.True(SensorLinks.CanDrive(doc, thermo, heater));    // TIsAlarmTemp admits only the thermostat
        Assert.False(SensorLinks.CanDrive(doc, alarm, heater));
        Assert.False(SensorLinks.CanDrive(doc, alarm, light));     // a light takes no sensor at all
        Assert.False(SensorLinks.CanDrive(doc, pump, pump));       // self
    }

    [Fact]
    public void Pointing_a_device_at_a_second_sensor_displaces_the_first_in_one_undo_step()
    {
        // A device has a single strInput01, so re-pointing is a replacement. Undo has to put the old sensor back,
        // not merely remove the new one, or undoing a re-point would leave the device following nothing.
        var cat = Fake(Alarm("O2"), Alarm("N2"), Pump("Pump"));
        var doc = new ShipDocument(cat);
        var stack = new CommandStack();
        var o2 = Place(doc, "O2", 0, 0);
        var n2 = Place(doc, "N2", 2, 0);
        var pump = Place(doc, "Pump", 4, 0);

        var first = new SensorLink(o2.Id, pump.Id);
        stack.Push(doc, new AddSensorLinkCommand(first, SensorLinks.Replacing(doc, first)));
        Assert.Equal(o2.Id, Assert.Single(doc.SensorLinks).Source);

        var second = new SensorLink(n2.Id, pump.Id);
        stack.Push(doc, new AddSensorLinkCommand(second, SensorLinks.Replacing(doc, second)));
        Assert.Equal(n2.Id, Assert.Single(doc.SensorLinks).Source);

        stack.Undo(doc);
        Assert.Equal(o2.Id, Assert.Single(doc.SensorLinks).Source);   // back to the FIRST sensor, not to none
        stack.Redo(doc);
        Assert.Equal(n2.Id, Assert.Single(doc.SensorLinks).Source);
    }

    [Fact]
    public void Device_settings_are_clamped_to_the_modes_the_def_declares()
    {
        // GasPump.UpdateRemote grants IsTurboOn from bTurbo without checking, while the rate multiplier it then
        // reads off IsTurbo is zero on a def that does not declare it — so an ungated flag stops the pump rather
        // than doing nothing. The clamp is what keeps that off an exported ship.
        var cat = Fake(Pump("Plain"), Pump("Fancy", "IsReverse"));
        var plain = cat.ByDefName["Plain"];
        var fancy = cat.ByDefName["Fancy"];
        var all = new DeviceSettings { Bus = DeviceBusMode.On, Turbo = true, Reverse = true, Slow = true };

        Assert.Equal(new DeviceSettings { Bus = DeviceBusMode.On }, all.ClampTo(plain));
        Assert.Equal(new DeviceSettings { Bus = DeviceBusMode.On, Reverse = true }, all.ClampTo(fancy));
        Assert.True(DeviceSettings.Default.IsDefault);
        Assert.Null(DeviceSettings.Default.OrNull());
    }

    // ---- persistence ----

    [SkippableFact]
    public void Both_channels_survive_an_oplan_round_trip_by_part_index()
    {
        var g = TestData.RequireGame();   // FromDocument needs a real DataIndex (versions + mods manifest)
        var cat = Fake(Breaker("Box"), Alarm("Alarm"), Pump("Pump"), Device("Light"));
        var doc = new ShipDocument(cat);
        var box = Place(doc, "Box", 0, 0);
        var alarm = Place(doc, "Alarm", 2, 0);
        var pump = Place(doc, "Pump", 4, 0);
        Place(doc, "Light", 6, 0);
        new AddLinkCommand(new DeviceLink(box.Id, pump.Id)).Do(doc);
        new AddSensorLinkCommand(new SensorLink(alarm.Id, pump.Id), null).Do(doc);
        new SetDeviceSettingsCommand(pump, null, new DeviceSettings { Bus = DeviceBusMode.On }).Do(doc);

        var tmp = Path.Combine(Path.GetTempPath(), $"ostraplan-link-{System.Guid.NewGuid():N}.oplan");
        try
        {
            OplanFile.FromDocument(doc, g.Index, new OplanMeta()).Save(tmp);
            var (doc2, missing) = OplanFile.Load(tmp).ToDocument(cat);

            Assert.Empty(missing);
            var breaker = Assert.Single(doc2.Links);
            Assert.Equal("Box", doc2.ById(breaker.Source)?.DefName);
            Assert.Equal("Pump", doc2.ById(breaker.Target)?.DefName);

            var sensor = Assert.Single(doc2.SensorLinks);
            Assert.Equal("Alarm", doc2.ById(sensor.Source)?.DefName);
            Assert.Equal("Pump", doc2.ById(sensor.Target)?.DefName);

            Assert.Equal(DeviceBusMode.On, doc2.ById(sensor.Target)!.Device!.Bus);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void A_legacy_alarm_to_pump_link_migrates_to_the_sensor_channel_on_load()
    {
        // Files written before the channels were told apart put everything in "links", including alarm→pump pairs
        // that only ever worked as sensor links. Those move to the channel that works. Anything else is kept
        // verbatim, even where the current rules would not let the user draw it: loading a design must not delete
        // what is in it, and an unrecognised pair is inert rather than harmful.
        var file = new OplanFile
        {
            Parts =
            [
                new OplanPart { Def = "Alarm", X = 0, Y = 0 },
                new OplanPart { Def = "Pump", X = 2, Y = 0 },
                new OplanPart { Def = "Box", X = 4, Y = 0 },
                new OplanPart { Def = "Light", X = 6, Y = 0 },
            ],
            Links =
            [
                new OplanLink { Src = 0, Tgt = 1 },   // alarm → pump: belongs on the sensor channel
                new OplanLink { Src = 2, Tgt = 3 },   // box → light: a real breaker link, stays put
                new OplanLink { Src = 0, Tgt = 3 },   // alarm → light: fits neither, but is kept rather than lost
            ],
        };
        var cat = Fake(Alarm("Alarm"), Pump("Pump"), Breaker("Box"), Device("Light"));
        var (doc, missing) = file.ToDocument(cat);

        Assert.Empty(missing);
        var sensor = Assert.Single(doc.SensorLinks);
        Assert.Equal("Alarm", doc.ById(sensor.Source)?.DefName);
        Assert.Equal("Pump", doc.ById(sensor.Target)?.DefName);

        var breakers = doc.Links
            .Select(l => $"{doc.ById(l.Source)?.DefName} → {doc.ById(l.Target)?.DefName}")
            .OrderBy(s => s, System.StringComparer.Ordinal)
            .ToList();
        Assert.Equal(["Alarm → Light", "Box → Light"], breakers);
    }

    [Fact]
    public void Link_to_a_dropped_missing_part_is_skipped_without_corrupting_others()
    {
        // Author a file whose part index 1 is a missing def; a link 0→2 must still resolve to the right parts
        // after the drop shifts nothing (indices are original), and a link touching the dropped part vanishes.
        var file = new OplanFile
        {
            Parts =
            [
                new OplanPart { Def = "Box", X = 0, Y = 0 },
                new OplanPart { Def = "GONE", X = 2, Y = 0 },   // def not in the catalog → dropped
                new OplanPart { Def = "C", X = 4, Y = 0 },
            ],
            Links =
            [
                new OplanLink { Src = 0, Tgt = 2 },   // Box → C: both survive
                new OplanLink { Src = 0, Tgt = 1 },   // Box → GONE: dropped
            ],
        };
        var cat = Fake(Breaker("Box"), Device("C"));
        var (doc, missing) = file.ToDocument(cat);

        Assert.Single(missing);
        var link = Assert.Single(doc.Links);
        Assert.Equal("Box", doc.ById(link.Source)?.DefName);
        Assert.Equal("C", doc.ById(link.Target)?.DefName);
    }

    // ---- export, against the live install ----

    [SkippableFact]
    public void Export_bakes_input_and_output_connections_with_the_right_signal_type_per_side()
    {
        var g = TestData.RequireGame();
        var box = g.Catalog.Parts.FirstOrDefault(p => DevicePanels.BreakerPanel(g.Catalog, p) is not null);
        Skip.If(box is null, "no breaker-box part in this install");
        var sink = g.Catalog.Parts.First(p => p.DefName != box!.DefName && p.IsSignalable
                                              && p.StartingConds.Contains("IsInstalled"));

        var doc = new ShipDocument(g.Catalog);
        var src = new Placement { DefName = box!.DefName, X = 0, Y = 0 };
        var tgt = new Placement { DefName = sink.DefName, X = 6, Y = 0 };
        new PlaceCommand(src).Do(doc);
        new PlaceCommand(tgt).Do(doc);
        new AddLinkCommand(new DeviceLink(src.Id, tgt.Id)).Do(doc);

        var (items, dest) = Export(doc, g);
        try
        {
            var srcItem = ItemFor(items, box.DefName);
            var tgtItem = ItemFor(items, sink.DefName);
            var srcId = srcItem.GetProperty("strID").GetString()!;
            var tgtId = tgtItem.GetProperty("strID").GetString()!;

            // The driving side carries SignalType.None (0) and the driven side SignalType.On (2). Anything else on
            // the driven side leaves it holding an unsignalled input, which raises IsSignalOff and shuts it down.
            Assert.Equal($"{tgtId}#0#true#", PanelKey(srcItem, "Electrical", "outputConnections"));
            Assert.Equal($"{srcId}#2#true#", PanelKey(tgtItem, "Electrical", "inputConnections"));
            Assert.Equal("", PanelKey(srcItem, "Electrical", "inputConnections"));

            // The canonical eight keys, and none of the three the panel used to invent.
            var keys = PanelKeys(srcItem, "Electrical");
            Assert.Equal(
                ["status", "inputConnections", "outputConnections", "signalQueue", "sendQueue", "override", "delay", "gate"],
                keys);
        }
        finally { Directory.Delete(dest, recursive: true); }
    }

    [SkippableFact]
    public void Export_writes_the_sensor_onto_the_driven_devices_own_panel()
    {
        var g = TestData.RequireGame();
        // A real device that takes a sensor, and a real alarm its panel admits.
        var sink = g.Catalog.Parts.FirstOrDefault(p => DevicePanels.SensorPanel(g.Catalog, p) is not null);
        Skip.If(sink is null, "no sensor-driven device in this install");
        var panel = DevicePanels.SensorPanel(g.Catalog, sink!)!;
        var sensor = g.Catalog.Parts.FirstOrDefault(p => p.DefName != sink!.DefName
            && p.StartingConds.Contains("IsInstalled")
            && DevicePanels.Satisfies(g.Catalog, p, panel.ValidSourceTrigger));
        Skip.If(sensor is null, "no sensor this device admits in this install");

        var doc = new ShipDocument(g.Catalog);
        var alarm = new Placement { DefName = sensor!.DefName, X = 0, Y = 0 };
        var device = new Placement { DefName = sink!.DefName, X = 8, Y = 0 };
        new PlaceCommand(alarm).Do(doc);
        new PlaceCommand(device).Do(doc);
        new AddSensorLinkCommand(new SensorLink(alarm.Id, device.Id), null).Do(doc);

        var (items, dest) = Export(doc, g);
        try
        {
            var alarmItem = ItemFor(items, sensor.DefName);
            var deviceItem = ItemFor(items, sink.DefName);

            // The link lives on the DRIVEN device's own panel, naming the alarm's export id — this is what
            // GasPump/Heater read. Nothing goes onto the Electrical panel for it.
            Assert.Equal(alarmItem.GetProperty("strID").GetString(),
                PanelKey(deviceItem, panel.Instance, "strInput01"));
            Assert.Equal("1", PanelKey(deviceItem, panel.Instance, "nKnobBus"));   // auto: follow the sensor
            Assert.DoesNotContain("Electrical", PanelNames(deviceItem));

            // Only the authored keys are written; the template constants come from the def at spawn.
            Assert.DoesNotContain("strValidCOTrigger01", PanelKeys(deviceItem, panel.Instance));
            Assert.DoesNotContain("strCondMonitor01", PanelKeys(deviceItem, panel.Instance));

            // An untouched, unwired device is left alone entirely.
            Assert.DoesNotContain(panel.Instance, PanelNames(alarmItem));
        }
        finally { Directory.Delete(dest, recursive: true); }
    }

    [SkippableFact]
    public void A_stock_ship_keeps_its_sensor_wiring_through_an_import_and_re_export()
    {
        // The regression that matters most: the game's own ships carry 1,780 of these links, and a round trip used
        // to drop every one. Compares the re-exported panels against the original ship's, by def and count.
        var g = TestData.RequireGame();
        var ships = TemplateImport.ListShipFiles(g.Index);
        var candidate = ships
            .Select(s => s.Path)
            .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p) == "Katydid");
        Skip.If(candidate is null, "Katydid.json not present in this install");

        var imported = TemplateImport.LoadFile(candidate!, g.Catalog);
        var doc = imported.Doc;
        Assert.NotEmpty(doc.SensorLinks);   // the stock Katydid wires two pumps to their alarms

        // What the ship itself says, as "<sensor def> → <device def>" pairs. Defs rather than ids, because an
        // export mints fresh ids; the relationships are what has to survive.
        var original = SensorPairsInFile(candidate!);
        Assert.NotEmpty(original);

        var (items, dest) = Export(doc, g);
        try
        {
            var byId = items.EnumerateArray().ToDictionary(
                it => it.GetProperty("strID").GetString()!, it => it.GetProperty("strName").GetString()!);
            var written = new List<string>();
            foreach (var it in items.EnumerateArray())
                foreach (var panel in PanelNames(it))
                    if (PanelKey(it, panel, "strInput01") is { Length: > 0 } sensorId && byId.TryGetValue(sensorId, out var sensorDef))
                        written.Add($"{sensorDef} → {it.GetProperty("strName").GetString()}");

            Assert.Equal(original.OrderBy(s => s, StringComparer.Ordinal), written.OrderBy(s => s, StringComparer.Ordinal));
        }
        finally { Directory.Delete(dest, recursive: true); }
    }

    /// <summary>The sensor wiring a ship file itself carries, as "&lt;sensor def&gt; → &lt;device def&gt;" pairs.
    /// A device pointing at its own id is the game's way of writing "no sensor", so it is not a pair.</summary>
    private static List<string> SensorPairsInFile(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var ship = json.RootElement.EnumerateArray()
            .Where(e => e.TryGetProperty("aItems", out _))
            .OrderByDescending(e => e.GetProperty("aItems").GetArrayLength())
            .First();
        var items = ship.GetProperty("aItems");
        var byId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var it in items.EnumerateArray())
            if (it.TryGetProperty("strID", out var id) && id.GetString() is { } sid)
                byId[sid] = it.GetProperty("strName").GetString() ?? "";

        var pairs = new List<string>();
        foreach (var it in items.EnumerateArray())
        {
            var self = it.TryGetProperty("strID", out var sid2) ? sid2.GetString() : null;
            foreach (var panel in PanelNames(it))
                if (PanelKey(it, panel, "strInput01") is { Length: > 0 } v && v != self && byId.TryGetValue(v, out var sensorDef))
                    pairs.Add($"{sensorDef} → {it.GetProperty("strName").GetString()}");
        }
        return pairs;
    }

    // ---- export helpers ----

    private static (JsonElement Items, string Dest) Export(
        ShipDocument doc, (GameEnv Env, DataIndex Index, Catalog Catalog) g)
    {
        var specs = RoomCertifier.LoadSpecs(g.Index);
        var dest = Path.Combine(Path.GetTempPath(), "OstraplanLinkExport_" + System.Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dest);
        var opts = new ExportOptions("LinkTest", "Tester", "", "1.0.0",
            g.Env.InstalledVersion ?? GameEnv.VerifiedGameVersion, dest, "LinkTest");
        var result = ShipExport.Write(doc, g.Catalog, specs, opts, g.Index);
        var json = JsonDocument.Parse(File.ReadAllText(result.ShipJsonPath));
        return (json.RootElement[0].GetProperty("aItems").Clone(), dest);
    }

    private static JsonElement ItemFor(JsonElement items, string defName) =>
        items.EnumerateArray().First(it => it.GetProperty("strName").GetString() == defName);

    private static IEnumerable<string> PanelNames(JsonElement item) =>
        item.TryGetProperty("aGPMSettings", out var gpms)
            ? gpms.EnumerateArray().Select(g => g.GetProperty("strName").GetString() ?? "")
            : [];

    private static List<string> PanelKeys(JsonElement item, string panel)
    {
        if (!item.TryGetProperty("aGPMSettings", out var gpms)) return [];
        foreach (var g in gpms.EnumerateArray())
            if (g.GetProperty("strName").GetString() == panel)
            {
                var flat = g.GetProperty("dictGUIPropMap").EnumerateArray().Select(e => e.GetString()).ToList();
                return [.. flat.Where((_, i) => i % 2 == 0).Select(s => s ?? "")];
            }
        return [];
    }

    private static string PanelKey(JsonElement item, string panel, string key)
    {
        if (!item.TryGetProperty("aGPMSettings", out var gpms)) return "";
        foreach (var g in gpms.EnumerateArray())
            if (g.GetProperty("strName").GetString() == panel)
            {
                var flat = g.GetProperty("dictGUIPropMap").EnumerateArray().Select(e => e.GetString()).ToList();
                var i = flat.IndexOf(key);
                return i >= 0 && i + 1 < flat.Count ? flat[i + 1] ?? "" : "";
            }
        return "";
    }
}
