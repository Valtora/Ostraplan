namespace Ostraplan.Core;

/// <summary>
/// The standard set of navigation modules Ostraplan installs into a nav console that has none.
/// A built/placed console (<c>ItmStationNav</c>) is only an empty frame — the game's flight interface is
/// assembled from separate hot-swappable module items (the <c>ItmNavMod*</c> cooverlays) contained inside it,
/// so a console with no modules spawns blank. The console's own screen-layout GUI-prop-map
/// (<c>NavModConfig</c>) comes from its def, so only the physical modules are needed.
///
/// <para><b>How the game stocks a console.</b> A 1.0 ship template does <b>not</b> parent modules to the
/// console: across the 220 core <c>data/ships</c> files, not one of the 127 consoles carries a module item.
/// Instead a <c>SysLootSpawner</c> sits on the console's tile carrying a <c>strType: "Loot"</c> prop map that
/// names one of the stock sets in <c>data/loot</c> (<c>ItmNavStationModsPod</c>, <c>…TorchShip</c>,
/// <c>…Atmo</c>, <c>…Combat</c>, …), and the game rolls it at spawn. 82 of those consoles have a spawner; the
/// other 45 are meant to spawn bare. Ostraplan drops every <c>IsSystem</c> object on import (see
/// <see cref="TemplateImport"/>), so an imported console arrives with whatever module items the source actually
/// held, which for a template is none and for a <b>pre-1.0</b> ship is none either — nav consoles had no
/// inventory at all before 1.0. That is what <see cref="StockEmptyConsoles"/> and the export/inject fallbacks
/// are for: Ostraplan bakes the modules as literal contained items, which works identically on the template and
/// save paths and needs no spawner.</para>
/// </summary>
public static class NavConsole
{
    /// <summary>
    /// The console loadout, <b>in screen-priority order</b>: <c>ItmNavStationModsPod</c> (the stock set 35 of the
    /// 82 stocked core consoles carry, more than any other), then the two situational modules a planned ship may
    /// want — course plotting for a torch burn and flight dynamics for atmosphere. Weapons and torch-specific
    /// modules are deliberately out. All names verified against <c>data/cooverlays/cooverlays_navmods.json</c>
    /// and the loot sets in <c>data/loot/loot.json</c>.
    ///
    /// <para>The order is load-bearing, not cosmetic: the console screen is tiled by the stock 13 with only one
    /// gap left, and neither trailing module fits it, so each lands in the console's edit-menu tray for the
    /// player to place (see <see cref="Arrange"/>). Earlier entries keep their default screen position, which is
    /// why the two that lose out are the situational ones and not, say, mooring or target data.</para>
    /// </summary>
    public static readonly IReadOnlyList<string> StandardModules =
    [
        // the stock pod set (data/loot ItmNavStationModsPod), which tiles the console screen
        "ItmNavModControlToggle", "ItmNavModMap", "ItmNavModControls", "ItmNavModDiagnostics",
        "ItmNavModDisplayControls", "ItmNavModEngineMode", "ItmNavModWarnings", "ItmNavModReserves",
        "ItmNavModSensorsMFD", "ItmNavModTransponder", "ItmNavModTimeZoom", "ItmNavModTargetData",
        "ItmNavModMooringControl",
        // + the situational pair: carried aboard, placed by the player when the trip calls for them
        "ItmNavModCoursePlot", "ItmNavModFlightDynamics",
    ];

    /// <summary>The starting condition that marks a part as a navigation console — data-driven detection, so a
    /// modded or variant console is recognised too rather than hard-coding the base-game <c>ItmStationNav</c> name.</summary>
    private const string NavStationCond = "IsNavStation";

    /// <summary>A placed part is a nav console (and so gets <see cref="StandardModules"/> installed) when its def
    /// carries the <see cref="NavStationCond"/> starting condition.</summary>
    public static bool IsConsole(PartDef? def) =>
        def is not null && System.Array.IndexOf(def.StartingConds, NavStationCond) >= 0;

    /// <inheritdoc cref="IsConsole(PartDef?)"/>
    public static bool IsConsole(ResolvedPart? part) => part is not null && part.Has(NavStationCond);

    /// <summary>
    /// True when a console has no modules and so needs the standard set: nothing <b>loose</b> in its inventory
    /// grid. Modules are loose grid cargo; a <c>DataStore</c> chip in the console's own <c>data</c> slot is
    /// <see cref="CargoItem.Slotted"/> equipment, not a module.
    ///
    /// <para>The distinction is the whole test. Every core console carries that chip and nothing else, so "holds
    /// anything at all" reads every imported console as already stocked — which is how an imported ship used to
    /// export with a data chip in its console and no screens whatsoever.</para>
    /// </summary>
    public static bool NeedsModules(IReadOnlyList<CargoItem> cargo) => !cargo.Any(c => !c.Slotted);

    /// <summary>
    /// Fit <see cref="StandardModules"/> into every console in <paramref name="doc"/> that came in with nothing
    /// inside, as authored cargo, and report how many consoles and modules that was.
    ///
    /// <para>Run at <b>import</b>, so the modules are visible and editable in the planner rather than appearing
    /// only in the exported file, and so the one place that fills a console serves every downstream path: the
    /// template export, the save inject (including a console the save already had, which the inject writes back
    /// verbatim and would otherwise leave blank), and the <c>.oplan</c>. A pre-1.0 ship is the case that needs
    /// it — consoles had no inventory before 1.0, so they all read in empty — but a 1.0 template's consoles are
    /// empty too (their modules come from a loot spawner Ostraplan drops), and both want the same fix.</para>
    ///
    /// <para>A console that already holds a module is left exactly as it is: a partly-stripped salvage console is
    /// a fact about that ship, not a gap to fill (see <see cref="NeedsModules"/> for why "holds anything" is the
    /// wrong test). Whatever is in the console's slots stays. Modules are laid out through
    /// <see cref="CargoEdit.Add"/>, so
    /// they occupy real cells on the console's declared grid (5×4 on <c>ItmStationNav</c>) and the fill stops
    /// early rather than overfilling a smaller modded console. A module def missing from the loaded data is
    /// skipped rather than fatal.</para>
    /// </summary>
    public static (int Consoles, int Modules, int Trayed) StockEmptyConsoles(ShipDocument doc, Catalog catalog)
    {
        int consoles = 0, modules = 0, trayed = 0;
        foreach (var placement in doc.Placements)
        {
            if (!NeedsModules(placement.Cargo)) continue;
            if (catalog.Lookup(placement.DefName) is not { } def || !IsConsole(def)) continue;

            var grid = def.ContainerGrid ?? (6, 6);
            var cargo = placement.Cargo;   // keeps whatever is in the console's slots (its data chip)
            var fitted = 0;
            foreach (var modDef in StandardModules)
            {
                if (catalog.Lookup(modDef) is not { } mod) continue;      // module's def isn't in the loaded data
                if (CargoEdit.Add(cargo, null, grid, mod, 1) is not { } next) break;   // console is full
                cargo = next;
                fitted++;
            }
            if (fitted == 0) continue;

            // Authored contents, so they persist into the .oplan and the save re-attach on reopen leaves them
            // alone — the save they came from has no such items to re-derive them from.
            doc.SetCargo(placement, cargo);
            consoles++;
            modules += fitted;
            // how many of them the console screen has no room for, so the report can say where they went
            trayed += Arrange(catalog, def, cargo.Where(c => !c.Slotted).Select(c => c.DefName)).Count(s => !s.OnScreen);
        }
        return (consoles, modules, trayed);
    }

    // ---- the screen arrangement ----

    /// <summary>Where one module sits on the console screen. <see cref="Pos"/> is the game's anchor-rect string
    /// (<c>xMin|yMin|xMax|yMax</c>, 0..1, y up) that <c>NavModConfig</c> stores, or <b>null</b> when the module
    /// is aboard but has nowhere to go — the game's "tray" state, where it waits in the console's edit menu until
    /// the player places it. <see cref="Key"/> is the name the config is keyed by (the module's GUI prefab,
    /// e.g. <c>NavModMap</c>), which is not its item def name.</summary>
    public sealed record NavModSlot(string DefName, string Key, string? Pos)
    {
        /// <summary>True when the module has a place on the screen; false when it rides in the edit-menu tray.</summary>
        public bool OnScreen => Pos is not null;
    }

    /// <summary>
    /// The screen arrangement the game would end up with for <paramref name="moduleDefs"/> in
    /// <paramref name="consoleDef"/>, computed exactly as <c>GUIOrbitDraw.LoadModules</c> does it.
    ///
    /// <para>A console's screen is not a grid of bays: each module is an anchor rect in 0..1, read from the
    /// <b>console's</b> <c>NavModConfig</c> prop map keyed by the module's GUI prefab, falling back to the
    /// module's own <c>strDefaultPos</c>. A module whose rect leaves the screen or overlaps one already placed is
    /// <c>DisableMod()</c>'d — still in the console, still in the edit menu, just not on screen
    /// (<c>EditMenu.DoesModFit</c>). So the order modules are considered in decides who keeps their slot, which
    /// is why <see cref="StandardModules"/> is ordered by screen priority.</para>
    ///
    /// <para>The stock defaults tile the screen exactly for the stock 13, with one 0.15×0.4 gap that neither
    /// remaining module fits, and several modules share a rect by design (mooring and flight dynamics are the
    /// same slot; sensors, torch drive and weapons are another) because no stock set carries both. Nothing here
    /// invents a position or resizes a panel: an arrangement Ostraplan made up is not one the game would ever
    /// produce, and the edit menu is where a player is meant to choose.</para>
    /// </summary>
    public static IReadOnlyList<NavModSlot> Arrange(
        Catalog catalog, PartDef consoleDef, IEnumerable<string> moduleDefs,
        IReadOnlyDictionary<string, string>? stored = null)
    {
        var config = ConfigMap(catalog, consoleDef);
        var placed = new List<NavRect>();
        var result = new List<NavModSlot>();

        foreach (var defName in moduleDefs)
        {
            var mod = catalog.Lookup(defName);
            // empty for something in the console that is not a nav module at all (the player can put a wrench in
            // there): it has no screen key, so it is never written into the config and never claims a slot
            var key = ConfigKey(catalog, mod) ?? "";
            // The design's own arrangement wins where it has an opinion ("" being a deliberate "shelve this"),
            // then the console's config, then the module's default — the same precedence LoadModules reads, with
            // the user's layout standing in for the console's saved one.
            var raw = stored is not null && stored.TryGetValue(key, out var chosen)
                ? chosen
                : config.GetValueOrDefault(key) is { Length: > 0 } fromConsole ? fromConsole : DefaultPos(catalog, mod);
            if (ParseRect(raw) is not { } rect || !RectFits(rect, placed))
            {
                result.Add(new NavModSlot(defName, key, null));   // no rect, off-screen, or taken: the tray
                continue;
            }
            placed.Add(rect);
            result.Add(new NavModSlot(defName, key, FormatRect(rect)));
        }
        return result;
    }

    /// <summary>
    /// The <c>NavModConfig</c> entries to bake onto a console holding <paramref name="moduleDefs"/>: each
    /// module's key against its rect, or against <c>""</c> when it rides in the tray — the exact shape
    /// <c>GUIOrbitDraw.SaveModules</c> writes when a player closes the console.
    ///
    /// <para>Baking it is what makes the arrangement <b>ours</b> rather than a coin toss. The game resolves a
    /// contested slot in favour of whichever module it happens to walk first out of the container, so without
    /// this the mooring page and the flight-dynamics page would swap places depending on nothing in particular.
    /// The item's prop maps merge into the def's <b>key by key</b> (<c>Ship.CreatePart</c>), so writing this
    /// panel leaves the console's other panels, and any config key not named here, untouched.</para>
    /// </summary>
    public static IReadOnlyList<(string Key, string Value)> ConfigEntries(
        Catalog catalog, PartDef consoleDef, IEnumerable<string> moduleDefs,
        IReadOnlyDictionary<string, string>? stored = null) =>
        Arrange(catalog, consoleDef, moduleDefs, stored)
            .Where(s => s.Key.Length > 0)
            .Select(s => (s.Key, s.Pos ?? ""))
            .ToList();

    /// <summary>The console's <c>NavModConfig</c> prop map (module key → anchor-rect string) from its def, or
    /// empty when it declares none.</summary>
    private static Dictionary<string, string> ConfigMap(Catalog catalog, PartDef consoleDef)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (instance, dict) in catalog.GpmSettingsFor(consoleDef))
            if (instance == "NavModConfig")
                foreach (var (k, v) in FlatPairs(dict)) map[k] = v;
        return map;
    }

    /// <summary>A module's key into <c>NavModConfig</c>: the <c>strGUIPrefab</c> of the template its <c>NavMod</c>
    /// prop map names (the template name itself when it declares no prefab). Null when the def carries no
    /// <c>NavMod</c> map at all — not a nav module.</summary>
    private static string? ConfigKey(Catalog catalog, PartDef? mod)
    {
        if (mod is null) return null;
        foreach (var (instance, template) in mod.Gpm)
        {
            if (instance != "NavMod") continue;
            if (catalog.GpmTemplates.TryGetValue(template, out var dict))
                foreach (var (k, v) in FlatPairs(dict))
                    if (k == "strGUIPrefab" && v.Length > 0) return v;
            return template;
        }
        return null;
    }

    /// <summary>A module's own fallback position (<c>strDefaultPos</c> on its <c>NavMod</c> template).</summary>
    private static string? DefaultPos(Catalog catalog, PartDef? mod)
    {
        if (mod is null) return null;
        foreach (var (instance, template) in mod.Gpm)
            if (instance == "NavMod" && catalog.GpmTemplates.TryGetValue(template, out var dict))
                foreach (var (k, v) in FlatPairs(dict))
                    if (k == "strDefaultPos") return v;
        return null;
    }

    /// <summary>A prop map's flat <c>[key, value, key, value, …]</c> array as pairs, skipping null values.</summary>
    private static IEnumerable<(string Key, string Value)> FlatPairs(System.Text.Json.JsonElement dict)
    {
        if (dict.ValueKind != System.Text.Json.JsonValueKind.Array) yield break;
        var flat = dict.EnumerateArray().ToList();
        for (var i = 0; i + 1 < flat.Count; i += 2)
            if (flat[i].ValueKind == System.Text.Json.JsonValueKind.String
                && flat[i].GetString() is { } k && flat[i + 1].GetString() is { } v)
                yield return (k, v);
    }

    /// <summary>An anchor rect on the console screen: <c>0..1</c> in both axes with <b>y up</b>, exactly as the
    /// game stores it (<c>xMin|yMin|xMax|yMax</c>). A module's size is fixed by its def; only its corner moves.</summary>
    public readonly record struct NavRect(double X0, double Y0, double X1, double Y1)
    {
        public double W => X1 - X0;
        public double H => Y1 - Y0;

        /// <summary>The same rect with its lower-left corner at (<paramref name="x"/>,<paramref name="y"/>),
        /// rounded to 2dp — the granularity the game's own drag writes (<c>Draggable.MoveRectTransformUsingAnchors</c>
        /// rounds the anchor min and carries the size across).</summary>
        public NavRect MovedTo(double x, double y)
        {
            var (rx, ry) = (Math.Round(x, 2), Math.Round(y, 2));
            return new NavRect(rx, ry, rx + W, ry + H);
        }
    }

    /// <summary>Parse an anchor rect. Null when it isn't four numbers, is empty (the game's own "shelved" marker),
    /// or falls outside the 0..1 screen — the bounds half of <c>EditMenu.DoesModFit</c>.</summary>
    public static NavRect? ParseRect(string? raw)
    {
        if (raw is not { Length: > 0 }) return null;
        var parts = raw.Split('|');
        if (parts.Length != 4) return null;
        var v = new double[4];
        for (var i = 0; i < 4; i++)
            if (!double.TryParse(parts[i], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out v[i])) return null;
        var rect = new NavRect(v[0], v[1], v[2], v[3]);
        return InBounds(rect) ? rect : null;
    }

    /// <summary>The rect back as the game writes it in <c>SaveModules</c>: two decimals, invariant.</summary>
    public static string FormatRect(NavRect r) => string.Format(
        System.Globalization.CultureInfo.InvariantCulture, "{0:f2}|{1:f2}|{2:f2}|{3:f2}", r.X0, r.Y0, r.X1, r.Y1);

    /// <summary><c>EditMenu.DoesModFit</c>: on the screen, and not strictly overlapping anything already placed.
    /// Rects that merely share an edge fit, which is what lets the stock set tile the screen.</summary>
    public static bool RectFits(NavRect r, IEnumerable<NavRect> others) =>
        InBounds(r) && !others.Any(o => Overlaps(o, r));

    /// <summary>The rect a module is drawn at when nothing else has an opinion: the console's <c>NavModConfig</c>
    /// entry for it, else the module's own <c>strDefaultPos</c>. Null when the def declares no screen position at
    /// all, which is what makes it tray-only. Its <b>size</b> is what the arrange dialog places from the tray.</summary>
    public static NavRect? DefaultRect(Catalog catalog, PartDef consoleDef, string moduleDefName)
    {
        var mod = catalog.Lookup(moduleDefName);
        var key = ConfigKey(catalog, mod);
        if (key is not null && ConfigMap(catalog, consoleDef).GetValueOrDefault(key) is { Length: > 0 } fromConsole
            && ParseRect(fromConsole) is { } rect) return rect;
        return ParseRect(DefaultPos(catalog, mod));
    }

    /// <summary>The key a module is stored under in a console's <c>NavModConfig</c> (its GUI prefab, which is not
    /// its item def name). Null when the def is not a nav module.</summary>
    public static string? KeyFor(Catalog catalog, string moduleDefName) => ConfigKey(catalog, catalog.Lookup(moduleDefName));

    /// <summary>
    /// Every nav module the data knows, keyed the way the screen is, with the size of the screen each takes at
    /// stock: the rect the first console def gives it, else its own default. For <c>NavModArt</c>, which lays a
    /// module's prefab out at the size the console shows it rather than the size the prefab was saved at (the
    /// Controls prefab keeps its container full-screen and sizes its buttons in pixels, so laid out at its own
    /// size it is a postage stamp in a corner).
    /// </summary>
    public static IReadOnlyDictionary<string, (double W, double H)> ScreenSizes(Catalog catalog)
    {
        var sizes = new Dictionary<string, (double W, double H)>(StringComparer.Ordinal);
        var console = catalog.Parts.FirstOrDefault(IsConsole);
        foreach (var def in catalog.Parts.Concat(catalog.LooseItems))
        {
            if (ConfigKey(catalog, def) is not { Length: > 0 } key || sizes.ContainsKey(key)) continue;
            var rect = console is not null
                ? DefaultRect(catalog, console, def.DefName)
                : ParseRect(DefaultPos(catalog, def));
            if (rect is { } r && r.W > 0 && r.H > 0) sizes[key] = (r.W, r.H);
        }
        return sizes;
    }

    /// <summary>
    /// The arrangement an imported console is actually carrying, for <see cref="Placement.NavLayout"/>: the
    /// <c>NavModConfig</c> panel off its own item (<see cref="GpmPanels.NavConfig"/>), or <b>null</b> when it says
    /// nothing its def does not already say.
    ///
    /// <para>Reading it is what makes the planner agree with the game. A console the player has sat at holds their
    /// layout in this panel — <c>SaveModules</c> writes it whenever the console GUI closes — and dropping it left
    /// the arrange dialog showing a recomputed stock screen instead of the ship's own, with modules in the wrong
    /// places and a strip of screen reading as free when in game it is occupied. Worse, applying an arrangement
    /// from that state wrote the recomputed one back over the player's.</para>
    ///
    /// <para>The null case is not an optimisation, it is the common one: <b>all 120 consoles across the core
    /// <c>data/ships</c> files carry this panel as a verbatim copy of the def's own</b>, because that is what the
    /// item spawns with and nobody has opened it. Storing that would put a redundant map on every imported console,
    /// carry it into every <c>.oplan</c>, and — since a stored layout is the design speaking rather than a default
    /// — make the save write-back stamp it over a console it should have left alone
    /// (<see cref="SaveEdit"/>'s <c>onlyFillEmpty</c>). Only a map that differs from the def's is an arrangement
    /// somebody chose.</para>
    /// </summary>
    public static IReadOnlyDictionary<string, string>? StoredLayout(
        Catalog catalog, PartDef consoleDef, IReadOnlyDictionary<string, string>? fromItem)
    {
        if (fromItem is not { Count: > 0 }) return null;
        var fromDef = ConfigMap(catalog, consoleDef);
        return fromItem.All(kv => fromDef.GetValueOrDefault(kv.Key) == kv.Value) ? null : fromItem;
    }

    /// <summary>
    /// The slack every edge comparison carries, and the reason it has to exist: the game does this arithmetic in
    /// <b>float32</b>, where a panel butted against its neighbour lands on the neighbour's edge exactly, and this
    /// does it in double, where it does not. A module 0.10 wide dropped at 0.05 has a right edge of
    /// <c>0.05 + 0.10 = 0.15000000000000002</c>, a hair past a neighbour starting at <c>0.15</c> — so a drop the
    /// game accepts read as an overlap, and the arrange dialog tinted it red over visibly clear screen. Every rect
    /// in play is a 2dp anchor (<c>SaveModules</c> writes two decimals, <see cref="NavRect.MovedTo"/> rounds to
    /// them), so this is four orders of magnitude below anything real and cannot mask a genuine collision.
    /// </summary>
    private const double Edge = 1e-6;

    private static bool InBounds(NavRect r) =>
        r.X0 >= -Edge && r.Y0 >= -Edge && r.X1 <= 1 + Edge && r.Y1 <= 1 + Edge
        && r.X1 - r.X0 > Edge && r.Y1 - r.Y0 > Edge;

    /// <summary><c>EditMenu.AreAnchorsOverlapping</c>: strict, so rects that merely share an edge fit (see
    /// <see cref="Edge"/> for why sharing one has to be tested with slack).</summary>
    private static bool Overlaps(NavRect a, NavRect b) =>
        a.X0 + Edge < b.X1 && a.X1 > b.X0 + Edge && a.Y0 + Edge < b.Y1 && a.Y1 > b.Y0 + Edge;
}
