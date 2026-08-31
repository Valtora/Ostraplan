using System.IO;
using System.Text.Json;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The reactor's own control panel (#56): the thirteen <c>ReactorIC</c> keys <c>FusionIC</c> reads off the
/// condition owner every tick, and therefore what decides whether an exported ship spawns with its core lit.
/// </summary>
public class ReactorPanelTests
{
    private static IReadOnlyDictionary<string, string?> Panel(params string?[] flat)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < flat.Length; i += 2)
            if (flat[i] is { } key) map[key] = flat[i + 1];
        return map;
    }

    private static IReadOnlyDictionary<string, string?> AsKeys(IReadOnlyList<object?> flat)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < flat.Count; i += 2) map[(string)flat[i]!] = flat[i + 1] as string;
        return map;
    }

    /// <summary>The panel a stock ship writes for a core that spawns running: bus on CHRG with all eight switches
    /// thrown. 32 of the 57 reactor panels the shipped ships carry are exactly this.</summary>
    private static IReadOnlyDictionary<string, string?> RunningPanel() => Panel(
        "knobBus", "2", "knobPump", "0", "knobRatio", "0",
        "chkAlign", "True", "chkCoilFwd", "True", "chkCoilRear", "True", "chkCryo", "True",
        "chkFuelReg", "True", "chkIgnition", "True", "chkMHDOn", "True", "chkPellet", "True",
        "slidCycle", "0", "slidFlow", "0");

    [Fact]
    public void Reads_the_panel_a_stock_ship_writes_for_a_running_core()
    {
        var r = ReactorSettings.FromPanel(RunningPanel());

        Assert.NotNull(r);
        Assert.Equal(ReactorPowerBus.Chrg, r.Bus);
        Assert.Equal(ReactorCorePurge.Off, r.Purge);
        Assert.True(r.LaserAlign);
        Assert.True(r.CoilForward);
        Assert.True(r.CoilRear);
        Assert.True(r.Cryo);
        Assert.True(r.FuelRegulator);
        Assert.True(r.Ignition);
        Assert.True(r.Mhd);
        Assert.True(r.PelletFeed);
        Assert.False(r.TorchThrust);
        Assert.Equal(0.0, r.Cycle);
        Assert.Equal(0.0, r.Flow);
        Assert.False(r.IsDefault);
    }

    [Fact]
    public void A_panel_of_the_templates_own_defaults_reads_as_a_cold_core()
    {
        // The other 23 the stock ships carry. It is a real panel and must read as one, but it says nothing the
        // def does not already say, so the design stores nothing for it.
        var r = ReactorSettings.FromPanel(Panel(
            "knobBus", "0", "knobPump", "0", "knobRatio", "0",
            "chkAlign", "false", "chkCoilFwd", "false", "chkCoilRear", "false", "chkCryo", "false",
            "chkFuelReg", "false", "chkIgnition", "false", "chkMHDOn", "false", "chkPellet", "false",
            "slidCycle", "0", "slidFlow", "0"));

        Assert.NotNull(r);
        Assert.True(r.IsDefault);
        Assert.Null(r.OrNull());
    }

    [Fact]
    public void A_panel_that_is_not_a_reactors_reads_as_null()
    {
        Assert.Null(ReactorSettings.FromPanel(Panel("strGUIPrefab", "GUIAirPump", "strInput01", "abc")));
        Assert.Null(ReactorSettings.FromPanel(Panel("strGUIPrefab", "GUILootSpawn", "strLoot", "X")));
        Assert.Null(ReactorSettings.FromPanel(Panel("inputConnections", "x", "override", "true")));
    }

    [Fact]
    public void The_panel_written_out_is_the_panel_read_back()
    {
        var before = new ReactorSettings
        {
            Bus = ReactorPowerBus.Batt, Purge = ReactorCorePurge.Turbo, TorchThrust = true,
            LaserAlign = true, CoilForward = false, CoilRear = true, Cryo = true,
            FuelRegulator = true, Ignition = true, Mhd = false, PelletFeed = true,
            Cycle = 0.25, Flow = 0.5,
        };

        Assert.Equal(before, ReactorSettings.FromPanel(AsKeys(before.ToPanelKeys())));
    }

    [Fact]
    public void Every_key_the_simulation_reads_is_written()
    {
        // FusionIC.Update reads all thirteen off GetGPMInfo("Panel A", …). A key left out is one the game takes
        // from whatever the template happens to declare, which is a different reactor from the one authored.
        var keys = AsKeys(ReactorSettings.Default.ToPanelKeys()).Keys.ToList();
        foreach (var key in new[]
                 {
                     "knobBus", "knobPump", "knobRatio", "chkAlign", "chkCoilFwd", "chkCoilRear", "chkCryo",
                     "chkFuelReg", "chkIgnition", "chkMHDOn", "chkPellet", "slidCycle", "slidFlow",
                 })
            Assert.Contains(key, keys);
        Assert.Equal(13, keys.Count);   // and nothing else: the rest of the panel is template data
    }

    [Fact]
    public void Booleans_are_written_the_way_the_game_writes_them_and_read_either_way()
    {
        var flat = AsKeys(new ReactorSettings { Ignition = true }.ToPanelKeys());
        Assert.Equal("True", flat["chkIgnition"]);
        Assert.Equal("False", flat["chkAlign"]);

        // The template's own defaults are lowercase, and bool.TryParse does not care.
        var r = ReactorSettings.FromPanel(Panel("chkIgnition", "true", "chkAlign", "FALSE"));
        Assert.NotNull(r);
        Assert.True(r.Ignition);
        Assert.False(r.LaserAlign);
    }

    [Fact]
    public void A_ratio_that_is_not_one_is_torch_off_exactly_as_the_simulation_reads_it()
    {
        // FusionIC.Update coerces any knobRatio that is not 1 to 0 before it splits the power.
        Assert.False(ReactorSettings.FromPanel(Panel("knobRatio", "0"))!.TorchThrust);
        Assert.True(ReactorSettings.FromPanel(Panel("knobRatio", "1"))!.TorchThrust);
        Assert.False(ReactorSettings.FromPanel(Panel("knobRatio", "7"))!.TorchThrust);
        Assert.False(ReactorSettings.FromPanel(Panel("knobRatio", "rubbish"))!.TorchThrust);
    }

    [Fact]
    public void A_knob_past_its_last_position_reads_as_off_rather_than_as_a_position_the_game_has_no_sprite_for()
    {
        Assert.Equal(ReactorPowerBus.Off, ReactorSettings.FromPanel(Panel("knobBus", "9"))!.Bus);
        Assert.Equal(ReactorPowerBus.Off, ReactorSettings.FromPanel(Panel("knobBus", "-1"))!.Bus);
        Assert.Equal(ReactorCorePurge.Turbo, ReactorSettings.FromPanel(Panel("knobPump", "2"))!.Purge);
    }

    [Fact]
    public void Clamping_survives_a_hand_edited_file()
    {
        var wild = new ReactorSettings
        {
            Bus = (ReactorPowerBus)42, Purge = (ReactorCorePurge)9, Cycle = 40, Flow = -3,
        }.Clamped();

        Assert.Equal(ReactorPowerBus.Off, wild.Bus);
        Assert.Equal(ReactorCorePurge.Off, wild.Purge);
        Assert.Equal(1.0, wild.Cycle);
        Assert.Equal(0.0, wild.Flow);
        Assert.Equal(0.0, new ReactorSettings { Flow = double.NaN }.Clamped().Flow);
    }

    [Fact]
    public void Slider_values_are_written_in_the_invariant_form()
    {
        // Convert.ToDouble on the game's side is culture-sensitive too, so a comma decimal separator is the one
        // thing a machine in a French locale must not put in a ship file.
        var was = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("fr-FR");
            var flat = AsKeys(new ReactorSettings { Cycle = 0.5 }.ToPanelKeys());
            Assert.Equal("0.5", flat["slidCycle"]);
            Assert.Equal(0.5, ReactorSettings.FromPanel(flat)!.Cycle);
        }
        finally { Thread.CurrentThread.CurrentCulture = was; }
    }

    [Fact]
    public void Pairs_and_keys_say_the_same_thing_so_an_export_and_a_save_edit_cannot_drift()
    {
        var r = new ReactorSettings { Bus = ReactorPowerBus.Chrg, Ignition = true, Cycle = 0.4 };
        Assert.Equal(AsKeys(r.ToPanelKeys()),
            r.ToPanelPairs().ToDictionary(p => p.Key, p => (string?)p.Value, StringComparer.Ordinal));
    }

    [Fact]
    public void A_reactor_panel_is_found_among_an_items_other_panels()
    {
        var item = JsonDocument.Parse("""
            {
              "strName": "ItmFusionReactorCore01Ignition",
              "aGPMSettings": [
                { "strName": "Rename", "dictGUIPropMap": ["strName", "Number One"] },
                { "strName": "Panel A", "dictGUIPropMap": ["knobBus", "2", "chkIgnition", "True"] }
              ]
            }
            """).RootElement;

        var panels = GpmPanels.Read(item);
        var reactor = GpmPanels.Reactor(panels);
        Assert.NotNull(reactor);
        Assert.Equal(ReactorPowerBus.Chrg, reactor.Bus);
        Assert.True(reactor.Ignition);
        // …and the rename panel beside it is untouched: an item carries several, and each is read on its own.
        Assert.Equal("Number One", panels["Rename"]["strName"]);
    }

    [Fact]
    public void The_electrical_panel_is_never_mistaken_for_a_reactors()
    {
        var item = JsonDocument.Parse("""
            {
              "aGPMSettings": [
                { "strName": "Electrical", "dictGUIPropMap": ["status", "true", "gate", "0", "delay", "0.0"] }
              ]
            }
            """).RootElement;

        Assert.Null(GpmPanels.Reactor(GpmPanels.Read(item)));
    }

    // ---- end to end: a design with a lit core has to reach the game as one ----

    [SkippableFact]
    public void Export_writes_the_authored_panel_and_leaves_a_cold_core_alone()
    {
        var g = TestData.RequireGame();
        var core = g.Catalog.Parts.FirstOrDefault(p => DevicePanels.ReactorPanel(g.Catalog, p) is not null);
        Skip.If(core is null, "no buildable fusion core in this install");

        var doc = new ShipDocument(g.Catalog);
        var cold = new Placement { DefName = core!.DefName, X = 0, Y = 0 };
        var lit = new Placement
        {
            DefName = core.DefName, X = 20, Y = 0,
            Reactor = new ReactorSettings
            {
                Bus = ReactorPowerBus.Chrg, LaserAlign = true, CoilForward = true, CoilRear = true, Cryo = true,
                FuelRegulator = true, Ignition = true, Mhd = true, PelletFeed = true, Cycle = 0.5,
            },
        };
        new PlaceCommand(cold).Do(doc);
        new PlaceCommand(lit).Do(doc);

        var (items, dest) = Export(doc, g);
        try
        {
            var both = items.EnumerateArray().Where(it => it.GetProperty("strName").GetString() == core.DefName).ToList();
            Assert.Equal(2, both.Count);
            var withPanel = both.Where(it => PanelKeys(it, DevicePanels.ReactorPanelInstance).Count > 0).ToList();

            // Exactly one panel: the core left alone says nothing the def's own template does not already say.
            Assert.Single(withPanel);
            Assert.Equal("2", PanelKey(withPanel[0], DevicePanels.ReactorPanelInstance, "knobBus"));
            Assert.Equal("True", PanelKey(withPanel[0], DevicePanels.ReactorPanelInstance, "chkIgnition"));
            Assert.Equal("0.5", PanelKey(withPanel[0], DevicePanels.ReactorPanelInstance, "slidCycle"));
            Assert.Equal(13, PanelKeys(withPanel[0], DevicePanels.ReactorPanelInstance).Count);
        }
        finally { Directory.Delete(dest, recursive: true); }
    }

    [SkippableFact]
    public void A_ship_exported_with_a_lit_core_imports_with_the_same_one()
    {
        var g = TestData.RequireGame();
        var core = g.Catalog.Parts.FirstOrDefault(p => DevicePanels.ReactorPanel(g.Catalog, p) is not null);
        Skip.If(core is null, "no buildable fusion core in this install");

        var settings = new ReactorSettings
        {
            Bus = ReactorPowerBus.Batt, Purge = ReactorCorePurge.Rough, TorchThrust = true,
            LaserAlign = true, FuelRegulator = true, Ignition = true, PelletFeed = true, Flow = 0.75,
        };
        var doc = new ShipDocument(g.Catalog);
        new PlaceCommand(new Placement { DefName = core!.DefName, X = 0, Y = 0, Reactor = settings }).Do(doc);

        var specs = RoomCertifier.LoadSpecs(g.Index);
        var dest = Path.Combine(Path.GetTempPath(), "OstraplanReactor_" + System.Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dest);
        try
        {
            var opts = new ExportOptions("ReactorTest", "Tester", "", "1.0.0",
                g.Env.InstalledVersion ?? GameEnv.VerifiedGameVersion, dest, "ReactorTest");
            var written = ShipExport.Write(doc, g.Catalog, specs, opts, g.Index);

            var back = TemplateImport.LoadFile(written.ShipJsonPath, g.Catalog).Doc;
            var reactor = back.Placements.Select(p => p.Reactor).FirstOrDefault(r => r is not null);
            Assert.Equal(settings, reactor);
        }
        finally { Directory.Delete(dest, recursive: true); }
    }

    [SkippableFact]
    public void The_oplan_carries_the_panel_across_a_save_and_reopen()
    {
        var g = TestData.RequireGame();
        var core = g.Catalog.Parts.FirstOrDefault(p => DevicePanels.ReactorPanel(g.Catalog, p) is not null);
        Skip.If(core is null, "no buildable fusion core in this install");

        var settings = new ReactorSettings
        {
            Bus = ReactorPowerBus.Chrg, Purge = ReactorCorePurge.Turbo, TorchThrust = true,
            Cryo = true, Ignition = true, Cycle = 0.25, Flow = 1.0,
        };
        var doc = new ShipDocument(g.Catalog);
        new PlaceCommand(new Placement { DefName = core!.DefName, X = 0, Y = 0, Reactor = settings }).Do(doc);

        var tmp = Path.Combine(Path.GetTempPath(),
            "OstraplanReactor_" + System.Guid.NewGuid().ToString("N")[..8] + ".oplan");
        try
        {
            OplanFile.FromDocument(doc, g.Index, new OplanMeta()).Save(tmp);
            var (back, missing) = OplanFile.Load(tmp).ToDocument(g.Catalog);

            Assert.Empty(missing);
            Assert.Equal(settings, Assert.Single(back.Placements).Reactor);
        }
        finally { File.Delete(tmp); }
    }

    [SkippableFact]
    public void A_core_left_cold_adds_nothing_to_the_oplan()
    {
        var g = TestData.RequireGame();
        var core = g.Catalog.Parts.FirstOrDefault(p => DevicePanels.ReactorPanel(g.Catalog, p) is not null);
        Skip.If(core is null, "no buildable fusion core in this install");

        var doc = new ShipDocument(g.Catalog);
        new PlaceCommand(new Placement { DefName = core!.DefName, X = 0, Y = 0 }).Do(doc);

        var tmp = Path.Combine(Path.GetTempPath(),
            "OstraplanReactor_" + System.Guid.NewGuid().ToString("N")[..8] + ".oplan");
        try
        {
            OplanFile.FromDocument(doc, g.Index, new OplanMeta()).Save(tmp);
            Assert.DoesNotContain("\"reactor\"", File.ReadAllText(tmp), StringComparison.Ordinal);
            Assert.Null(Assert.Single(OplanFile.Load(tmp).ToDocument(g.Catalog).Doc.Placements).Reactor);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void A_duplicated_or_restated_core_keeps_its_switches()
    {
        var settings = new ReactorSettings { Bus = ReactorPowerBus.Chrg, Ignition = true };
        var p = new Placement { DefName = "ItmCoreA", X = 0, Y = 0, Reactor = settings };

        // A copy is the same core the designer set up, at another tile.
        Assert.Equal(settings, p.CopyAt(4, 4).Reactor);
        // …and so is one switched to its Battery Mode or Running def: Restate changes the state, not the object.
        Assert.Equal(settings, p.Restate("ItmCoreARunning", 0).Reactor);
    }

    [SkippableFact]
    public void The_stock_cores_declare_the_panel_and_a_pump_does_not()
    {
        var g = TestData.RequireGame();

        var core = g.Catalog.Lookup("ItmFusionReactorCore01Off");
        Skip.If(core is null, "this install has no fusion reactor core");
        var panel = DevicePanels.ReactorPanel(g.Catalog, core!);
        Assert.NotNull(panel);
        Assert.Equal(DevicePanel.ReactorPrefab, panel.Prefab);
        // FusionIC and Ship.GetReactorGPMValue both name the instance literally.
        Assert.Equal(DevicePanels.ReactorPanelInstance, panel.Instance);
        // The reactor panel declares no sensor input, which is why the DEVICE block never showed one of these.
        Assert.False(panel.HasSensorInput);

        if (g.Catalog.Lookup("ItmAirPump01") is { } pump)
        {
            Assert.Null(DevicePanels.ReactorPanel(g.Catalog, pump));
            Assert.NotNull(DevicePanels.SensorPanel(g.Catalog, pump));
        }
    }

    [SkippableFact]
    public void Every_def_the_game_gives_a_reactor_panel_is_offered_one_here()
    {
        var g = TestData.RequireGame();
        var declared = g.Index.Type("condowners").Keys
            .Select(n => g.Catalog.Lookup(n))
            .Where(p => p is not null && DevicePanels.ReactorPanel(g.Catalog, p!) is not null)
            .Select(p => p!.DefName)
            .ToList();

        Skip.If(declared.Count == 0, "this install's data declares no reactor panel");
        // 13 on stock 1.0.0.13: the ItmReactorIC03* and ItmFusionReactorCore01* families in every state, plus
        // ItmReactorIC02OffLoose. Asserted as a floor rather than an equality so a mod cannot fail the suite.
        Assert.Contains("ItmFusionReactorCore01Off", declared);
        Assert.Contains("ItmFusionReactorCore01Ignition", declared);
        Assert.Contains("ItmReactorIC03Off", declared);
    }

    // ---- helpers, the same shape DeviceLinkTests uses ----

    private static (JsonElement Items, string Dest) Export(
        ShipDocument doc, (GameEnv Env, DataIndex Index, Catalog Catalog) g)
    {
        var specs = RoomCertifier.LoadSpecs(g.Index);
        var dest = Path.Combine(Path.GetTempPath(), "OstraplanReactorExport_" + System.Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dest);
        var opts = new ExportOptions("ReactorTest", "Tester", "", "1.0.0",
            g.Env.InstalledVersion ?? GameEnv.VerifiedGameVersion, dest, "ReactorTest");
        var result = ShipExport.Write(doc, g.Catalog, specs, opts, g.Index);
        var json = JsonDocument.Parse(File.ReadAllText(result.ShipJsonPath));
        return (json.RootElement[0].GetProperty("aItems").Clone(), dest);
    }

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
