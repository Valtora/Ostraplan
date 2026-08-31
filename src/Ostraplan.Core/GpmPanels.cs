using System.Text.Json;

namespace Ostraplan.Core;

/// <summary>
/// Reads the GUI-prop-map panels off one <c>aItems</c> entry of a ship template or a save — the general form of
/// what <see cref="Rename"/> does for its single panel.
///
/// <para>Two shapes to respect, both of them the game's. <c>dictGUIPropMap</c> is a <b>flat alternating</b>
/// key/value array rather than an object, and a trailing unpaired key is dropped, which is what
/// <c>DataHandler.ConvertStringArrayToDict</c> does. And an item may carry the <b>same panel more than once</b>:
/// <c>Ship.CreatePart</c> merges every panel onto the condition owner key by key with the <b>last</b> occurrence
/// winning, so that is how they are folded here rather than taking the first or rejecting the duplicate.</para>
/// </summary>
public static class GpmPanels
{
    /// <summary>The panel a nav console keeps its screen arrangement in (see <see cref="NavConsole"/>).</summary>
    public const string NavConfigPanel = "NavModConfig";

    /// <summary>The <c>Electrical</c> panel's name, and the two connection keys on it.</summary>
    public const string ElectricalPanel = "Electrical";
    public const string InputConnectionsKey = "inputConnections";
    public const string OutputConnectionsKey = "outputConnections";

    /// <summary>Every panel on this item, folded per key with the last duplicate winning: panel name → key →
    /// value (null where the game's own template writes a null). Empty when the item carries none.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> Read(JsonElement item)
    {
        var result = new Dictionary<string, Dictionary<string, string?>>(StringComparer.Ordinal);
        if (!item.TryGetProperty("aGPMSettings", out var panels) || panels.ValueKind != JsonValueKind.Array)
            return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, string?>)kv.Value, StringComparer.Ordinal);

        foreach (var panel in panels.EnumerateArray())
        {
            if (panel.ValueKind != JsonValueKind.Object) continue;
            if (Json.Str(panel, "strName") is not { Length: > 0 } name) continue;
            if (!panel.TryGetProperty("dictGUIPropMap", out var map) || map.ValueKind != JsonValueKind.Array) continue;

            if (!result.TryGetValue(name, out var keys)) result[name] = keys = new Dictionary<string, string?>(StringComparer.Ordinal);
            var flat = map.EnumerateArray().ToArray();
            for (var i = 0; i + 1 < flat.Length; i += 2)
                if (flat[i].ValueKind == JsonValueKind.String && flat[i].GetString() is { } key)
                    keys[key] = flat[i + 1].ValueKind == JsonValueKind.String ? flat[i + 1].GetString() : null;
        }
        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, string?>)kv.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// The <c>strID</c> of the sensor this item follows, or null when it follows none. Found by scanning for
    /// whichever panel declares <see cref="DevicePanel.SensorInputKey"/> rather than by assuming the panel is
    /// called "Panel A", so a modded device that names its panel something else still reads.
    ///
    /// <para><b>An item pointing at itself follows nothing.</b> That is how the game records "no input":
    /// <c>GUIAirPump.SetInput(null)</c> falls back to the device's own condition owner and writes its
    /// <c>strID</c>, and 337 stock devices carry exactly that. <c>Heater</c> tests for it explicitly and
    /// <c>GasPump</c> reaches the same outcome by testing itself, so both read it as unwired.</para>
    /// </summary>
    public static string? SensorInput(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> panels, string? ownStrId)
    {
        foreach (var (_, keys) in panels)
            if (keys.TryGetValue(DevicePanel.SensorInputKey, out var value) && value is { Length: > 0 })
                return string.Equals(value, ownStrId, StringComparison.Ordinal) ? null : value;
        return null;
    }

    /// <summary>The item <c>strID</c>s named by one of the <c>Electrical</c> panel's connection lists. Entries are
    /// comma-joined <c>&lt;strID&gt;#&lt;signalType&gt;#&lt;switchStatus&gt;#&lt;nickName&gt;</c>; only the id is
    /// read, since the rest is runtime state the design does not carry.</summary>
    public static IReadOnlyList<string> Connections(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> panels, string key)
    {
        if (!panels.TryGetValue(ElectricalPanel, out var electrical)) return [];
        if (!electrical.TryGetValue(key, out var raw) || raw is not { Length: > 0 }) return [];
        var ids = new List<string>();
        foreach (var entry in raw.Split(','))
        {
            if (entry.Length == 0) continue;
            var id = entry.Split('#')[0];
            if (id.Length > 0) ids.Add(id);
        }
        return ids;
    }

    /// <summary>
    /// A nav console's own screen arrangement as the item carries it: the <c>NavModConfig</c> panel, module GUI
    /// prefab → anchor rect, with <c>""</c> for a module the console holds but does not show. Null when the item
    /// carries no such panel at all.
    ///
    /// <para>A null value is read as <c>""</c>, which is what the game means by one: <c>SaveModules</c> blanks
    /// every key before writing the active anchors, so an empty entry is the shelved marker rather than a gap.</para>
    /// </summary>
    public static IReadOnlyDictionary<string, string>? NavConfig(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> panels)
    {
        if (!panels.TryGetValue(NavConfigPanel, out var keys) || keys.Count == 0) return null;
        return keys.ToDictionary(kv => kv.Key, kv => kv.Value ?? "", StringComparer.Ordinal);
    }

    /// <summary>The device panel settings this item carries (see <see cref="DeviceSettings"/>), or null when it
    /// carries none that differ from the default. Read off whichever panel declares them, same as
    /// <see cref="SensorInput"/>.</summary>
    public static DeviceSettings? Settings(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> panels)
    {
        foreach (var (name, keys) in panels)
        {
            if (name == ElectricalPanel) continue;   // its "override"/"status" are a different thing entirely
            if (!keys.ContainsKey("nKnobBus") && !keys.ContainsKey("bTurbo")
                && !keys.ContainsKey("bReverse") && !keys.ContainsKey("bSlowMode")) continue;

            var settings = new DeviceSettings
            {
                // An unparseable or unknown knob position reads as Auto, which is what the game does with one:
                // GasPump's switch grants IsOverrideOff only for its default branch, and Auto is the safe reading
                // for a design (it follows the sensor rather than forcing the device one way).
                Bus = keys.TryGetValue("nKnobBus", out var knob) && int.TryParse(knob, out var n)
                      && Enum.IsDefined(typeof(DeviceBusMode), n) ? (DeviceBusMode)n : DeviceBusMode.Auto,
                Turbo = Flag(keys, "bTurbo"),
                Reverse = Flag(keys, "bReverse"),
                Slow = Flag(keys, "bSlowMode"),
            };
            return settings.OrNull();
        }
        return null;

        static bool Flag(IReadOnlyDictionary<string, string?> keys, string key) =>
            keys.TryGetValue(key, out var v) && bool.TryParse(v, out var b) && b;
    }

    /// <summary>The reactor panel settings this item carries (see <see cref="ReactorSettings"/>), or null when it
    /// carries none. Found by the keys rather than by the def, because a stock station authors this panel on
    /// <c>ItmReactorIC02Ignition</c>, whose condowner never declares it.
    ///
    /// <para>Returned even when every value equals the template's default, unlike <see cref="Settings"/>: an
    /// authored all-zero reactor panel is what the stock ships write for a core that spawns cold, and dropping it
    /// would make an import indistinguishable from one Ostraplan failed to read.</para></summary>
    public static ReactorSettings? Reactor(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> panels)
    {
        foreach (var (name, keys) in panels)
        {
            if (name == ElectricalPanel) continue;
            if (ReactorSettings.FromPanel(keys) is { } reactor) return reactor;
        }
        return null;
    }

    /// <summary>The loot-spawner panel this item carries (see <see cref="SpawnerSettings"/>), or null when it is
    /// not a spawner. Unlike <see cref="Settings"/> this returns the settings even when they equal the default,
    /// because a spawner IS its panel: an item with no panel to speak of is not a spawner at its defaults, it is
    /// a spawner Ostraplan failed to read.</summary>
    public static SpawnerSettings? Spawner(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> panels)
    {
        foreach (var (name, keys) in panels)
        {
            if (name == ElectricalPanel) continue;
            if (SpawnerSettings.FromPanel(keys) is { } spawner) return spawner;
        }
        return null;
    }
}
