using System.Text.Json;

namespace Ostraplan.Core;

/// <summary>
/// Typed views over the raw game JSON. Field semantics mirror the game's own
/// DTOs (JsonItemDef, JsonInstallable, JsonCondOwner); defaults match the
/// game's constructors (nCols=1, empty socket arrays).
/// </summary>
public sealed record ItemDef(
    string Name,
    string Img,
    bool HasSpriteSheet,
    string? CtSpriteSheet,
    int NLayer,
    int NCols,
    string[] SocketAdds,
    string[] SocketReqs,
    string[] SocketForbids)
{
    /// <summary>Footprint, exactly as Item.SetData derives it: W = nCols, H = adds/W.</summary>
    public int Width => NCols < 1 ? 1 : NCols;
    public int Height => SocketAdds.Length == 0 ? 1 : Math.Max(1, SocketAdds.Length / Width);

    /// <summary>The light definitions this item attaches (<c>aLights</c>, names into <c>data/lights</c>) — the
    /// glow/illumination a lit fixture emits. Empty for a part that emits no light. See <see cref="LightDef"/>.</summary>
    public string[] Lights { get; init; } = [];

    public static ItemDef Parse(JsonElement e) => new(
        Json.Str(e, "strName") ?? "",
        Json.Str(e, "strImg") ?? "",
        Json.Bool(e, "bHasSpriteSheet"),
        Json.Str(e, "ctSpriteSheet"),
        Json.Int(e, "nLayer"),
        Json.Int(e, "nCols", 1),
        Json.StrArray(e, "aSocketAdds"),
        Json.StrArray(e, "aSocketReqs"),
        Json.StrArray(e, "aSocketForbids"))
    {
        Lights = Json.StrArray(e, "aLights"),
    };
}

/// <summary>
/// A light definition (<c>data/lights</c>, the game's <c>JsonLight</c>): the coloured point light a fixture
/// attaches through its item def's <c>aLights</c>. <see cref="Color"/> names a <see cref="ColorDef"/> whose alpha
/// is the light's intensity; the sentinel <c>"Blank"</c> means the light casts <b>no</b> real illumination (a glow
/// decal only). <see cref="Img"/> is that additive glow sprite (optional). <see cref="PosX"/>/<see cref="PosY"/>
/// are a pixel offset from the item centre (+y up, 16 px = 1 tile), resolved onto the grid exactly like a map
/// point. <see cref="Radius"/> is the light radius in tiles, defaulting to the game's <c>DEFAULTVISIBILITYRANGE</c>
/// (6) when the def omits <c>fRadius</c> or sets it ≤ 0 (verified <c>Visibility.Awake</c> / <c>Item</c> light
/// attach, 0.15.x).
/// </summary>
public sealed record LightDef(string Name, string Color, string? Img, double PosX, double PosY, double Radius, bool CanBlink)
{
    /// <summary>The game's default light radius (<c>Visibility.DEFAULTVISIBILITYRANGE</c>) applied when a real
    /// light's def carries no positive <c>fRadius</c>.</summary>
    public const double DefaultRadius = 6.0;

    public static LightDef Parse(JsonElement e)
    {
        double px = 0, py = 0;
        if (e.TryGetProperty("ptPos", out var pt) && pt.ValueKind == JsonValueKind.Object)
        {
            px = Json.Dbl(pt, "x");
            py = Json.Dbl(pt, "y");
        }
        var radius = Json.Dbl(e, "fRadius");
        return new(
            Json.Str(e, "strName") ?? "",
            Json.Str(e, "strColor") ?? "Blank",
            Json.Str(e, "strImg"),
            px, py,
            radius > 0 ? radius : DefaultRadius,
            Json.Bool(e, "bCanBlink"));
    }
}

/// <summary>
/// An RGBA colour (<c>data/colors</c>, the game's <c>JsonColor</c>): channels 0-255. For a <b>light</b> colour the
/// alpha channel encodes intensity (the game multiplies it through to the shader as <c>_LightColor</c>), so
/// Ostraplan reads <see cref="A"/>/255 as the light's strength. Referenced by name from a <see cref="LightDef"/>.
/// </summary>
public sealed record ColorDef(string Name, byte R, byte G, byte B, byte A)
{
    public static ColorDef Parse(JsonElement e) => new(
        Json.Str(e, "strName") ?? "",
        Chan(e, "nR"), Chan(e, "nG"), Chan(e, "nB"), Chan(e, "nA"));

    private static byte Chan(JsonElement e, string name) => (byte)Math.Clamp(Json.Int(e, name), 0, 255);
}

/// <summary>
/// A cooverlay is a named skin over a base condowner: when the game can't find
/// a CO by name it falls back to dictCOOverlays and uses strCOBase with the
/// overlay's cosmetic overrides (DataHandler.LoadCO).
/// </summary>
public sealed record CoOverlayDef(
    string Name, string? NameFriendly, string? Img, string? COBase, string[] GpmNames, string[] ModeSwitches)
{
    /// <summary>The condition-loot this skin applies on top of its base condowner (<c>strCondLoot</c>) — the game's
    /// <c>COOverlay.Init</c> runs <c>Loot.ApplyCondLoot</c> with it on <b>every</b> spawn, so a skin's real stats are
    /// the base's plus these signed deltas (e.g. an MSS "Light Framework" wall is <c>ItmWall1x1</c>'s 24 kg minus a
    /// <c>-StatMass x4</c> to 20, plus <c>IsMSS</c>/<c>IsWhite</c>). Null for a purely-cosmetic skin. Ignoring it is
    /// why the branded walls all read the base 24. See <see cref="Catalog"/>'s cond-loot application.</summary>
    public string? CondLoot { get; init; }

    public static CoOverlayDef Parse(JsonElement e) => new(
        Json.Str(e, "strName") ?? "",
        Json.Str(e, "strNameFriendly"),
        Json.Str(e, "strImg"),
        Json.Str(e, "strCOBase"),
        Json.StrArray(e, "mapGUIPropMaps"),
        // Flat [base, skin] pairs mapping each base-condowner state to this skin's counterpart
        // (installed/patch/damaged/loose); the game uses it to re-skin a base drop. See LooseForms.
        Json.StrArray(e, "mapModeSwitches"))
    {
        CondLoot = Json.Str(e, "strCondLoot"),
    };
}

public sealed record CondOwnerDef(
    string Name, string? NameFriendly, string? Desc, string? ItemDefName, string[] StartingCondNames,
    IReadOnlyDictionary<string, double> StartingCondValues,
    IReadOnlyDictionary<string, (double X, double Y)> MapPoints,
    string[] GpmNames,   // GUI-prop-map declarations (the "Panel A"/"Electrical"/... a device wires up on install)
    string[] TickerNames)   // per-second tickers (e.g. "Power") the device needs, granted on install and loaded from the save
{
    /// <summary>Inventory-grid size this item takes up when held in a container (<c>nContainerWidth</c>×
    /// <c>nContainerHeight</c> on a <b>container</b> def is its grid; these are the <b>item's own</b> footprint on
    /// a grid: <c>inventoryWidth</c>/<c>inventoryHeight</c>). 0 means "use the item def's map footprint" — the
    /// game's <c>GUIInventoryItem.GetWidthHeightForCO</c> falls back to <c>nWidthInTiles</c>/<c>nHeightInTiles</c>.</summary>
    public int InvW { get; init; }
    public int InvH { get; init; }

    /// <summary>If this def is a container, its inventory grid dimensions (<c>nContainerWidth</c>×
    /// <c>nContainerHeight</c>). 0 when not a container (or an old-style container) — the game then defaults the
    /// grid to 6×6 (<c>Container.gridLayout</c>).</summary>
    public int ContainerW { get; init; }
    public int ContainerH { get; init; }

    /// <summary>The condtrigger that filters which items this container accepts (<c>strContainerCT</c>), or null.
    /// Not needed to <i>view</i> contents; used by the authoring phase to validate what can be dropped in.</summary>
    public string? ContainerCT { get; init; }

    /// <summary>Max stack size for this item (<c>nStackLimit</c>); ≤1 = not stackable. Only informative for the
    /// viewer (identical items sharing a cell render as one stack with a count).</summary>
    public int StackLimit { get; init; }

    /// <summary>The named equipment slots this item provides (<c>aSlotsWeHave</c>) — e.g. a backpack's four
    /// pockets, an EVA suit's O₂/battery/tool pockets, a crew body's limbs. Empty for a plain grid container.</summary>
    public string[] SlotsWeHave { get; init; } = [];

    /// <summary>Paper-doll layout for this item's slots (<c>dictSlotsLayout</c>): slot name → (x, y) pixel offset
    /// from the host image (the <c>z</c> depth is dropped). Includes a <c>"self"</c> anchor for the host sprite.
    /// Empty when the item lays no paper-doll (its slots, if any, fall back to a simple row).</summary>
    public IReadOnlyDictionary<string, (double X, double Y)> SlotLayout { get; init; } =
        new Dictionary<string, (double, double)>();

    /// <summary>The slot name(s) this item occupies when equipped into a parent (the keys of its
    /// <c>mapSlotEffects</c>). On load the game slots a contained item into the first of these its parent has
    /// (<c>Slots.SlotItem</c>), so the paper-doll uses <c>SlotKeys ∩ parent.SlotsWeHave</c> to place it.</summary>
    public string[] SlotKeys { get; init; } = [];

    /// <summary>The <c>jsonPI</c> field: the name of this device's <see cref="PowerInfoDef"/> (an entry in
    /// <c>data/powerinfos</c>), or null for a part that draws no power. The game adds a <c>Powered</c> component
    /// keyed by this name (<c>CondOwner.SetData</c> → <c>Powered.SetData(jid.jsonPI)</c>); the power-info's
    /// <c>aInputPts</c> name the map points where the device plugs into the conduit network.</summary>
    public string? Jpi { get; init; }

    public static CondOwnerDef Parse(JsonElement e)
    {
        var conds = Json.StrArray(e, "aStartingConds");
        return new(
            Json.Str(e, "strName") ?? "",
            Json.Str(e, "strNameFriendly"),
            Json.Str(e, "strDesc"),
            Json.Str(e, "strItemDef"),
            conds.Select(LootDef.CondName).ToArray(),
            ParseCondValues(conds),
            ParseMapPoints(Json.StrArray(e, "mapPoints")),
            Json.StrArray(e, "mapGUIPropMaps"),
            Json.StrArray(e, "aTickers"))
        {
            InvW = Json.Int(e, "inventoryWidth"),
            InvH = Json.Int(e, "inventoryHeight"),
            ContainerW = Json.Int(e, "nContainerWidth"),
            ContainerH = Json.Int(e, "nContainerHeight"),
            ContainerCT = Json.Str(e, "strContainerCT"),
            StackLimit = Json.Int(e, "nStackLimit"),
            SlotsWeHave = Json.StrArray(e, "aSlotsWeHave"),
            SlotLayout = ParsePointObject(e, "dictSlotsLayout"),
            SlotKeys = DictKeys(Json.StrArray(e, "mapSlotEffects")),
            Jpi = Json.Str(e, "jsonPI"),
        };
    }

    /// <summary>The keys of a flat alternating key/value string array (the game's
    /// <c>ConvertStringArrayToDict</c> shape used by <c>mapSlotEffects</c>): elements 0, 2, 4, ….</summary>
    private static string[] DictKeys(string[] flat)
    {
        var keys = new List<string>(flat.Length / 2 + 1);
        for (var i = 0; i < flat.Length; i += 2)
            if (!string.IsNullOrEmpty(flat[i])) keys.Add(flat[i]);
        return keys.ToArray();
    }

    /// <summary>Parse a <c>dictSlotsLayout</c>-shaped object — <c>{ "slot": { "x": .., "y": .., "z": .. } }</c> —
    /// into name → (x, y), dropping z. Absent/malformed → empty.</summary>
    private static IReadOnlyDictionary<string, (double X, double Y)> ParsePointObject(JsonElement e, string name)
    {
        var map = new Dictionary<string, (double, double)>(StringComparer.Ordinal);
        if (e.TryGetProperty(name, out var obj) && obj.ValueKind == JsonValueKind.Object)
            foreach (var p in obj.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Object)
                    map[p.Name] = (Json.Dbl(p.Value, "x"), Json.Dbl(p.Value, "y"));
        return map;
    }

    /// <summary>
    /// The starting-cond magnitudes: name -&gt; the amount the game would apply. The condition
    /// string is "&lt;Cond&gt;=&lt;chance&gt;x&lt;amount&gt;" and the amount is the number AFTER the
    /// 'x' ("StatMass=1.0x45" -&gt; 45), matching GetCondAmount; the number before it is the apply
    /// chance (almost always 1.0), not the amount. Later entries win, matching the game's last-write
    /// accumulation. See <see cref="LootDef.CondAmount"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, double> ParseCondValues(string[] entries)
    {
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var name = LootDef.CondName(entry);
            if (name.Length == 0) continue;
            map[name] = LootDef.CondAmount(entry);
        }
        return map;
    }

    /// <summary>"DockA,0,8" entries: name, x, y in pixels around the item centre, +y up.</summary>
    public static IReadOnlyDictionary<string, (double X, double Y)> ParseMapPoints(string[] entries)
    {
        var map = new Dictionary<string, (double, double)>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var parts = entry.Split(',');
            if (parts.Length == 3
                && double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var x)
                && double.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var y))
                map[parts[0].Trim()] = (x, y);
        }
        return map;
    }
}

/// <summary>
/// A power-info template (<c>data/powerinfos</c>, the game's <c>JsonPowerInfo</c> / <c>dictPowerInfo</c>): the
/// electrical profile a powered device references by name through its condowner's <c>jsonPI</c> field. Only the
/// fields Ostraplan needs to visualise connectivity are kept — <see cref="InputPointNames"/> (<c>aInputPts</c>),
/// the map-point names where the device plugs into the conduit network. The runtime draw/recharge fields
/// (<c>fAmount</c>, <c>strUsePowerCT</c>, …) drive the in-game power <i>simulation</i>, a non-goal here.
/// </summary>
public sealed record PowerInfoDef(string Name, string[] InputPointNames)
{
    public static PowerInfoDef Parse(JsonElement e) => new(
        Json.Str(e, "strName") ?? "",
        Json.StrArray(e, "aInputPts"));
}

/// <summary>
/// A named equipment slot (<c>data/slots</c>): the metadata the paper-doll needs to label and draw a slot an
/// item declares in its <c>aSlotsWeHave</c>. The slot's on-figure position comes from the <b>host</b> item's
/// <c>dictSlotsLayout</c> (see <see cref="CondOwnerDef.SlotLayout"/>), not from here.
/// </summary>
public sealed record SlotDef(string Name, string? NameFriendly, string? IconImg, bool Hide)
{
    public string Friendly => NameFriendly ?? Name;

    public static SlotDef Parse(JsonElement e) => new(
        Json.Str(e, "strName") ?? "",
        Json.Str(e, "strNameFriendly"),
        Json.Str(e, "strIconImage"),
        Json.Bool(e, "bHide"));
}

public sealed record InstallableDef(
    string Name, string BuildType, string JobType, string StartInstall,
    string[] Inputs, string[] Tools, bool NoJobMenu)
{
    /// <summary>The condowner this job acts on (<c>strActionCO</c>): for an <c>install</c> job the loose form it
    /// consumes, for an <c>uninstall</c> job the installed fixture it removes. Empty when the job names none.</summary>
    public string ActionCO { get; init; } = "";

    /// <summary>The condowner(s) this job yields (<c>aLootCOs</c>): an <c>install</c> job's installed form, an
    /// <c>uninstall</c> job's loose/packaged form. Drives the installed⇄loose form map (see <see cref="Catalog"/>).</summary>
    public string[] LootCOs { get; init; } = [];

    public static InstallableDef Parse(JsonElement e) => new(
        Json.Str(e, "strName") ?? "",
        Json.Str(e, "strBuildType") ?? "",
        Json.Str(e, "strJobType") ?? "",
        Json.Str(e, "strStartInstall") ?? "",
        Json.StrArray(e, "aInputs"),
        Json.StrArray(e, "aToolCTsUse"),
        Json.Bool(e, "bNoJobMenu"))
    {
        ActionCO = Json.Str(e, "strActionCO") ?? "",
        LootCOs = Json.StrArray(e, "aLootCOs"),
    };
}

/// <summary>
/// A loot's direct payload lives in aCOs ("IsWall=1.0x1" for condition-type
/// loots); aLoots nests other loots. Tile socket adds expand through both.
/// </summary>
public sealed record LootDef(string Name, string[] Conds, string[] Loots)
{
    public static LootDef Parse(JsonElement e) => new(
        Json.Str(e, "strName") ?? "",
        Json.StrArray(e, "aCOs").Select(CondName).Where(s => s.Length > 0).ToArray(),
        Json.StrArray(e, "aLoots").Where(s => s.Length > 0).ToArray());

    /// <summary>"IsWall=1.0x1" -> "IsWall".</summary>
    public static string CondName(string entry)
    {
        var i = entry.IndexOf('=');
        return (i < 0 ? entry : entry[..i]).Trim();
    }

    /// <summary>
    /// The magnitude the game applies for a condition entry, matching Loot.ParseCondEquation and
    /// CondOwner.GetCondAmount. The string is "&lt;Cond&gt;=&lt;chance&gt;x&lt;amount&gt;": the number
    /// AFTER the 'x' is the amount ("StatMass=1.0x45" -&gt; 45), the number before it is the apply
    /// chance (almost always 1.0), <b>not</b> the amount. A leading '-' negates the entry; ranges
    /// ("x4-6") take the low end (a deterministic static read, not the game's random roll). A bare
    /// name with no '=' reads as 1.0 (present); anything unparseable falls back to 1.0.
    /// </summary>
    public static double CondAmount(string entry)
    {
        var s = entry.Trim();
        var neg = s.StartsWith('-');
        if (neg) s = s[1..];
        var eq = s.IndexOf('=');
        if (eq < 0) return neg ? -1.0 : 1.0;
        var rest = s[(eq + 1)..];
        var x = rest.IndexOf('x');
        var amount = x < 0 ? rest : rest[(x + 1)..];   // the magnitude is AFTER the 'x'
        var dash = amount.IndexOf('-');                // "4-6" range -> low end
        if (dash > 0) amount = amount[..dash];
        return double.TryParse(amount.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? (neg ? -v : v) : 1.0;
    }
}

/// <summary>
/// A condtrigger. The presence-only path (every <see cref="Reqs"/> present, no
/// <see cref="Forbids"/> present) drives autotiling and CheckFit; the full reachable
/// evaluator (<see cref="CondEval"/>) additionally follows <see cref="Triggers"/>/
/// <see cref="TriggersForbid"/> and the <see cref="BAnd"/>=false OR path for room
/// certification. The <c>HasNested</c> positional flag is retained for existing
/// callers; the richer fields are populated by <see cref="Parse"/>.
/// </summary>
public sealed record CondTriggerDef(string Name, string[] Reqs, string[] Forbids, bool HasNested)
{
    public string[] Triggers { get; init; } = [];           // nested aTriggers (AND path)
    public string[] TriggersForbid { get; init; } = [];     // nested aTriggersForbid (OR path)
    public bool BAnd { get; init; } = true;                 // bAND: AND of reqs vs OR of reqs
    public double FChance { get; init; } = 1.0;             // <1 = randomised (unreachable from room specs)
    public string? HigherCond { get; init; }                // strHigherCond / aLowerConds ranking (unreachable)
    public string[] LowerConds { get; init; } = [];

    public static CondTriggerDef Parse(JsonElement e) => new(
        Json.Str(e, "strName") ?? "",
        Json.StrArray(e, "aReqs").Select(LootDef.CondName).ToArray(),
        Json.StrArray(e, "aForbids").Select(LootDef.CondName).ToArray(),
        Json.StrArray(e, "aTriggers").Length > 0)
    {
        Triggers = Json.StrArray(e, "aTriggers"),
        TriggersForbid = Json.StrArray(e, "aTriggersForbid"),
        BAnd = !(e.TryGetProperty("bAND", out var b) && b.ValueKind == JsonValueKind.False),   // default true
        FChance = Json.Dbl(e, "fChance", 1.0),
        HigherCond = Json.Str(e, "strHigherCond"),
        LowerConds = Json.StrArray(e, "aLowerConds").Select(LootDef.CondName).ToArray(),
    };
}

internal static class Json
{
    public static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True;

    public static int Int(JsonElement e, string name, int fallback = 0) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i : fallback;

    public static double Dbl(JsonElement e, string name, double fallback = 0) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)
            ? d : fallback;

    public static int[] IntArray(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out _))
                 .Select(x => x.GetInt32()).ToArray()
            : [];

    public static string[] StrArray(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
            : [];
}
