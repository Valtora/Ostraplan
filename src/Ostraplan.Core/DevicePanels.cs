using System.Text.Json;

namespace Ostraplan.Core;

/// <summary>
/// One <b>device control panel</b> a def declares — an entry in its <c>mapGUIPropMaps</c> resolved through
/// <c>data/guipropmaps</c> (see <see cref="Catalog.GpmSettingsFor"/> for the raw form). This is the metadata the
/// game's own wiring UI reads, and it is what decides <b>what may be wired to what</b>: the panel names the GUI
/// class that drives it (<see cref="Prefab"/>), the condtrigger a candidate source must satisfy
/// (<see cref="ValidSourceTrigger"/>), and whether it offers a sensor input socket at all
/// (<see cref="HasSensorInput"/>).
///
/// <para>Two prefabs matter, because they are the two ways the game creates a connection (GAME-INTERNALS §14):
/// <c>GUIBreaker</c> calls <c>Electrical.SetUpConnection</c> and so writes the <c>Electrical</c> GPM graph, while
/// <c>GUIAirPump</c> writes nothing but its own <c>strInput01</c>, which <c>GasPump</c>/<c>Heater</c> read back as
/// the sensor the device follows. Nothing else creates a connection.</para>
///
/// <para>The <c>strTitle</c>, <c>strFriendlyName</c>, <c>strCondMonitor01</c>, <c>strSubPoint</c> and
/// <c>strAddPoint</c> keys are template constants, identical on every instance of a def, so they are def data
/// rather than anything a design authors. <see cref="CondMonitor"/> is kept because it is what a device tests on
/// its sensor and so explains the link in the UI; the rest are not modelled.</para>
/// </summary>
public sealed record DevicePanel(
    string Instance,             // the panel's instance name on the def ("Panel A", "Electrical", …)
    string Template,             // the data/guipropmaps entry it expands to ("AirPump", "ElectricalBox01", …)
    string? Prefab,              // strGUIPrefab — the GUI class, which is what decides the wiring channel
    string? ValidSourceTrigger,  // strValidCOTrigger01 — the condtrigger a source must satisfy
    string? CondMonitor,         // strCondMonitor01 — the cond this device tests on its source
    bool HasSensorInput)         // declares strInput01 — i.e. it takes exactly one sensor
{
    /// <summary>The <c>strGUIPrefab</c> of the breaker-box panel — the only GUI class that creates an
    /// <c>Electrical</c> connection (<c>GUIBreaker.SetInput</c> → <c>Electrical.SetUpConnection</c>).</summary>
    public const string BreakerPrefab = "GUIBreaker";

    /// <summary>The key holding the sensor this device follows. Read by <c>GasPump.UpdateRemote</c> and
    /// <c>Heater.UpdateRemote</c> off the device's own panel; nothing else consumes it.</summary>
    public const string SensorInputKey = "strInput01";

    /// <summary>True when this panel is a breaker box: it can be the <b>source</b> of an <c>Electrical</c>
    /// connection, driving anything its <see cref="ValidSourceTrigger"/> admits. Stock data has exactly one such
    /// def (<c>ItmElectricalBox01</c> and its Off/Damaged forms); a mod declaring the same panel gets the same
    /// treatment, which is why this reads the prefab rather than testing a def name.</summary>
    public bool IsBreaker => Prefab == BreakerPrefab;
}

/// <summary>
/// Resolves a def's declared <see cref="DevicePanel"/>s and answers the two questions the wiring editor asks of
/// them: can this part <b>drive</b> over a given channel, and can it <b>be driven</b>.
///
/// <para>Everything here is read from game data (the def's <c>mapGUIPropMaps</c> joined to
/// <c>data/guipropmaps</c>), never from a hardcoded def list, so a modded pump with an <c>AirPump</c> panel or a
/// modded breaker with a <c>GUIBreaker</c> one behaves exactly like the stock article.</para>
/// </summary>
public static class DevicePanels
{
    /// <summary>The panels <paramref name="part"/> declares, in declaration order. Empty for inert structure and
    /// for a def whose panel template is not loaded.</summary>
    public static IReadOnlyList<DevicePanel> For(Catalog catalog, PartDef part) => catalog.DevicePanels(part);

    /// <summary>The panel that offers a sensor input socket, or null when the part takes no sensor. At most one
    /// in all stock data (a device has a single <c>strInput01</c>), so the first is the answer.</summary>
    public static DevicePanel? SensorPanel(Catalog catalog, PartDef part) =>
        catalog.DevicePanels(part).FirstOrDefault(p => p.HasSensorInput);

    /// <summary>The part's breaker panel, or null when it is not a breaker box.</summary>
    public static DevicePanel? BreakerPanel(Catalog catalog, PartDef part) =>
        catalog.DevicePanels(part).FirstOrDefault(p => p.IsBreaker);

    /// <summary>
    /// Does <paramref name="source"/> satisfy <paramref name="trigger"/>? The game asks this exact question when
    /// it populates the input selector (<c>CrewSim.ShowInputSelector(DataHandler.GetCondTrigger(...))</c>), so it
    /// is the whole of the "may this drive that" rule on both channels.
    ///
    /// <para>A panel naming no trigger, or naming one this install does not define, admits anything — the game's
    /// <c>GetCondTrigger</c> falls back to the blank trigger and <c>CondTrigger.Triggered</c> returns true for a
    /// blank one. Faithful, and it keeps a mod that omits the key from being unwireable.</para>
    /// </summary>
    public static bool Satisfies(Catalog catalog, PartDef source, string? trigger) =>
        trigger is not { Length: > 0 }
        || !catalog.Triggers.TryGetValue(trigger, out var ct)
        || CondEval.Triggered(ct, source.StartingConds, catalog);

    /// <summary>The conds whose presence on a def turns on one of the device panel's optional controls, exactly as
    /// <c>GUIAirPump.LoadCOStats</c> gates them: no cond, no control, and no key written for it. This matters
    /// beyond the UI — <c>GasPump.UpdateRemote</c> applies <c>bTurbo</c> whether or not the def declares
    /// <c>IsTurbo</c>, while the rate multiplier it then reads (<c>GetCondAmount("IsTurbo")</c>) is zero on a def
    /// that does not, so authoring turbo where the game would not offer it stops the pump dead.</summary>
    public const string TurboCond = "IsTurbo";
    public const string ReverseCond = "IsReverse";
    public const string SlowCond = "IsSlowMode";

    /// <summary>Parse the panels declared by <paramref name="part"/>. Called once per def by
    /// <see cref="Catalog.DevicePanels"/>, which caches the result.</summary>
    internal static IReadOnlyList<DevicePanel> Parse(Catalog catalog, PartDef part)
    {
        if (part.Gpm.Count == 0) return [];
        var panels = new List<DevicePanel>(part.Gpm.Count);
        foreach (var (instance, template) in part.Gpm)
        {
            if (!catalog.GpmTemplates.TryGetValue(template, out var dict)) continue;
            var keys = Flatten(dict);
            panels.Add(new DevicePanel(
                instance,
                template,
                keys.GetValueOrDefault("strGUIPrefab"),
                keys.GetValueOrDefault("strValidCOTrigger01"),
                keys.GetValueOrDefault("strCondMonitor01"),
                keys.ContainsKey(DevicePanel.SensorInputKey)));
        }
        return panels;
    }

    /// <summary>A <c>dictGUIPropMap</c> — a <b>flat alternating</b> key/value array, not an object — as a lookup.
    /// A trailing unpaired key is dropped, which is what the game's <c>ConvertStringArrayToDict</c> does; a null
    /// value (the template's own way of saying "none") reads back as null rather than being skipped, so
    /// <c>ContainsKey</c> still reports the key as declared.</summary>
    private static Dictionary<string, string?> Flatten(JsonElement dict)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (dict.ValueKind != JsonValueKind.Array) return map;
        var flat = dict.EnumerateArray().ToArray();
        for (var i = 0; i + 1 < flat.Length; i += 2)
            if (flat[i].ValueKind == JsonValueKind.String && flat[i].GetString() is { } key)
                map[key] = flat[i + 1].ValueKind == JsonValueKind.String ? flat[i + 1].GetString() : null;
        return map;
    }
}
