using System.Text.Json;
using Ostraplan.Core;

namespace Ostraplan.Tests;

/// <summary>
/// A game-free synthetic catalog/document builder for tests that don't need the real install. It
/// wires parts the way <see cref="Catalog.Build"/> does — a part's footprint tiles carry socket-add
/// loots, and those loots carry the conditions the engine reads (IsWall, IsFloorSealed, IsPortal, …) —
/// so render layering, room partitioning, the placement law and analysis all behave as they would on
/// real data. Prefer this over <see cref="TestData.RequireGame"/> whenever the logic under test doesn't
/// genuinely need real game data (only the parity corpus, real prices and sprite rendering do).
/// </summary>
public sealed class Fixtures
{
    private readonly List<PartDef> _parts = [];
    private readonly Dictionary<string, PartDef> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LootDef> _loots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CondTriggerDef> _trigs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _looseForms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _installedForms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _repairForms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _breakForms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LightDef> _lightDefs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ColorDef> _colorTable = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ParallaxDef> _parallaxDefs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InteractionDef> _interactionDefs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> _gpmTemplates = new(StringComparer.Ordinal);

    /// <summary>Register a GUI-prop-map template (data/guipropmaps): the flat key/value <c>dictGUIPropMap</c> a
    /// named panel expands to. A part declares which templates it uses via <see cref="Part"/>'s <c>gpm</c>
    /// argument, and <see cref="Catalog.GpmSettingsFor"/> joins the two — which is what an injected device needs
    /// baked onto it or it loads unwired.</summary>
    public Fixtures GpmTemplate(string name, params string[] flatKeyValues)
    {
        _gpmTemplates[name] = JsonDocument.Parse(JsonSerializer.Serialize(flatKeyValues)).RootElement.Clone();
        return this;
    }

    /// <summary>Register a named loot (the bundle of conditions a tile socket adds).</summary>
    public Fixtures Loot(string name, params string[] conds)
    {
        _loots[name] = new LootDef(name, conds, []);
        return this;
    }

    /// <summary>Register an ITEM loot (<c>strType: "item"</c>): the defs a holder spawns with, and how many of
    /// each. This is where a garment's pockets and a backpack's pouches come from — see
    /// <see cref="Catalog.IntrinsicContents"/>.</summary>
    public Fixtures ItemLoot(string name, params (string DefName, int Count)[] items)
    {
        _loots[name] = new LootDef(name, [], []) { Type = "item", Items = items };
        return this;
    }

    /// <summary>Register a presence-only condtrigger: every req present, no forbid present. Pass
    /// <paramref name="bAnd"/> false for the game's OR form (any one req present is enough), which is what the
    /// vessel and container filters use.</summary>
    public Fixtures Trig(string name, string[] reqs, string[]? forbids = null, bool bAnd = true)
    {
        _trigs[name] = new CondTriggerDef(name, reqs, forbids ?? [], false) { BAnd = bAnd };
        return this;
    }

    /// <summary>Register a colour (RGBA; a light colour's alpha is its intensity).</summary>
    public Fixtures Color(string name, byte r, byte g, byte b, byte a)
    {
        _colorTable[name] = new ColorDef(name, r, g, b, a);
        return this;
    }

    /// <summary>Register a light definition (data/lights): the colour it uses (<c>"Blank"</c> casts no real light),
    /// its radius in tiles (0 = the game default of 6), and its pixel offset from the item centre.</summary>
    public Fixtures Light(string name, string color, double radius = 0, double px = 0, double py = 0)
    {
        _lightDefs[name] = new LightDef(name, color, null, px, py, radius > 0 ? radius : LightDef.DefaultRadius, false);
        return this;
    }

    /// <summary>Register an interaction (data/interactions): the map point on the target a crew member walks to
    /// and how far away (Chebyshev tiles) they may stand — what <see cref="WalkNetwork"/> gates device reach on.</summary>
    public Fixtures Interaction(string name, string targetPoint = "use", double range = 0, string? title = null)
    {
        _interactionDefs[name] = new InteractionDef(name, title, targetPoint, range, false);
        return this;
    }

    /// <summary>
    /// Add a part. Each of its <c>w×h</c> footprint tiles adds <paramref name="tileConds"/> (auto-registered
    /// as a loot named "<c>&lt;name&gt;Adds</c>"), so the part contributes real conditions to the grid.
    /// <paramref name="reqs"/>/<paramref name="forbids"/> are the socket ring the placement law tests
    /// (3×3 flattened for a 1×1). CO-level metadata (container grid, stack limit, base price, map points) via
    /// the optional args. <paramref name="slotsWeHave"/> are the paper-doll slots this part offers,
    /// <paramref name="slotKeys"/> the ones it can be slotted into (its <c>mapSlotEffects</c> keys), and
    /// <paramref name="defaultLoot"/> the <c>strLoot</c> it spawns with — pair it with <see cref="ItemLoot"/> to
    /// give a garment its pockets.
    /// <para><paramref name="apron"/> wraps the <c>w×h</c> body in that many rings of under-floor-only
    /// reservation (IsSubTile, no solid body), exactly as the big cryogenic canisters do: the item's socket grid
    /// grows to <c>(w+2a)×(h+2a)</c> while the body it draws and can be swapped for stays <c>w×h</c>. See
    /// <see cref="Catalog.BodyBox"/>.</para>
    /// </summary>
    public Fixtures Part(string name, int w = 1, int h = 1, string[]? tileConds = null,
        string[]? reqs = null, string[]? forbids = null, string category = "MISC",
        string[]? startingConds = null, (int W, int H)? container = null, string? containerCT = null,
        int stackLimit = 0, IReadOnlyDictionary<string, (double X, double Y)>? mapPoints = null,
        double basePrice = 0, bool sheet = false, string origin = "core",
        IReadOnlyDictionary<string, double>? condValues = null,
        IReadOnlyList<(double X, double Y)>? powerInputs = null, (double X, double Y)? powerOutput = null,
        string[]? lights = null, ShadowBox[]? shadowBoxes = null, bool lightWall = false,
        string[]? interactions = null, IReadOnlyList<(string Instance, string Template)>? gpm = null,
        int apron = 0, double zScale = 1.0,
        string[]? slotsWeHave = null, string[]? slotKeys = null, string? defaultLoot = null)
    {
        var body = "Blank";
        if (tileConds is { Length: > 0 })
        {
            body = name + "Adds";
            _loots[body] = new LootDef(body, tileConds, []);
        }

        string[] adds;
        var (iw, ih) = (w + 2 * apron, h + 2 * apron);
        if (apron > 0)
        {
            const string underFloor = "SubfloorAdds";
            _loots[underFloor] = new LootDef(underFloor, ["IsSubTile"], []);
            adds = new string[iw * ih];
            for (var r = 0; r < ih; r++)
                for (var c = 0; c < iw; c++)
                    adds[r * iw + c] = r >= apron && r < apron + h && c >= apron && c < apron + w
                        ? body
                        : underFloor;
        }
        else adds = [.. Enumerable.Repeat(body, w * h)];

        var item = new ItemDef(name, name + ".png", sheet, null, 0, iw, adds, reqs ?? [], forbids ?? [])
        {
            Lights = lights ?? [],
            ShadowBoxes = shadowBoxes ?? [],
            IsWallForLight = lightWall,
            ZScale = zScale,
        };
        var values = new Dictionary<string, double>(condValues ?? new Dictionary<string, double>());
        if (basePrice > 0) values["StatBasePrice"] = basePrice;
        var part = new PartDef(name, name, category, origin, item, null, [], [],
            startingConds ?? [], values, mapPoints ?? new Dictionary<string, (double, double)>())
        {
            ContainerGrid = container,
            ContainerCT = containerCT,
            StackLimit = stackLimit,
            PowerInputPoints = powerInputs ?? [],
            PowerOutputPoint = powerOutput,
            InteractionNames = interactions ?? [],
            Gpm = gpm ?? [],
            SlotsWeHave = slotsWeHave ?? [],
            SlotKeys = slotKeys ?? [],
            DefaultLoot = defaultLoot,
            // What Catalog.ResolveDef works out for a real def. A fixture carries no cooverlay, so the base view
            // and the folded view are the same one and the rule can be read off directly.
            BehaviourConds = [.. PartDef.BehaviourBackfill(startingConds ?? [], values)
                .Select(kv => FormattableString.Invariant($"{kv.Key}=1.0x{kv.Value}"))],
        };
        _parts.Add(part);
        _byName[name] = part;
        return this;
    }

    // ---- semantic shortcuts (game-authentic tile conditions) ----
    //
    // Each carries the fZScale (ItemDef.ZScale) its real counterpart declares, so a synthetic catalog draws in the
    // same order as the game's own data: floors 0.01, a generic fixture 0.2, a bin 1.01, walls and doors 1.0 (the
    // JsonItemDef default, which those defs leave unset on purpose), conduit 1.02.

    /// <summary>A sealed floor tile (IsFloor + IsFloorSealed) — the walkable base rooms flood over.</summary>
    public Fixtures Floor(string name = "Floor") =>
        Part(name, tileConds: ["IsFloor", "IsFloorSealed"], category: "HULL", zScale: 0.01);

    /// <summary>A hull wall (IsWall + IsObstruction) — a room boundary. Carries the core wall's light-occluder box
    /// (a full tile, wall-flagged — <c>ItmWall1x1</c>'s <c>aShadowBoxes</c>), so Light Viz shadows behind it.</summary>
    public Fixtures Wall(string name = "Wall") => Part(name, tileConds: ["IsWall", "IsObstruction"],
        startingConds: ["IsWall"], category: "HULL",
        shadowBoxes: [new ShadowBox(0, 0, 0.5, 0.5, false)], lightWall: true);

    /// <summary>A glass window wall (like <c>ItmWallWindow1x1</c>): seals the hull, but its occluder box is glass —
    /// light passes straight through.</summary>
    public Fixtures Window(string name = "Window") => Part(name, tileConds: ["IsWall", "IsObstruction"],
        startingConds: ["IsWall"], category: "HULL",
        shadowBoxes: [new ShadowBox(0, 0, 0.5, 0.5, true)], lightWall: true);

    /// <summary>A door tile (IsWall + IsPortal): seals the hull like a wall, but is a walkable portal.</summary>
    public Fixtures Door(string name = "Door") => Part(name, tileConds: ["IsWall", "IsPortal"], startingConds: ["IsPortal"], category: "HULL");

    /// <summary>A thin power conduit (IsPowerConduit) — the top of the draw order.</summary>
    public Fixtures Conduit(string name = "Conduit") =>
        Part(name, tileConds: ["IsPowerConduit"], category: "POWR", zScale: 1.02);

    /// <summary>A generic solid fixture (IsFixture + IsObstruction), optionally ringed by <paramref name="apron"/>
    /// tiles of under-floor-only reservation like the big canisters.</summary>
    public Fixtures Fixture(string name, int w = 1, int h = 1, int apron = 0, double zScale = 0.2) =>
        Part(name, w, h, tileConds: ["IsFixture", "IsObstruction"], category: "FURN", apron: apron, zScale: zScale);

    /// <summary>A container fixture with an inventory grid of the given size and an optional accept-filter trigger.</summary>
    public Fixtures Container(string name, int gridW = 4, int gridH = 4, string? filterCt = null, double zScale = 1.01) =>
        Part(name, tileConds: ["IsFixture", "IsObstruction", "IsContainer"], startingConds: ["IsContainer"],
            container: (gridW, gridH), containerCT: filterCt, category: "FURN", zScale: zScale);

    /// <summary>
    /// A gas canister: a sealed vessel with a volume, a temperature and a pressure rating, holding
    /// <paramref name="mols"/> of one gas. Defaults are the game's own ordinary canister shell (0.787 m³ at
    /// 41,400 kPa and 293 K, which is 13,375 mol of capacity). Add bulk payloads with
    /// <paramref name="bulk"/> — <c>StatLiqD2O</c> and friends, in kilograms.
    /// </summary>
    public Fixtures Tank(string name, string gas = "O2", double mols = 0,
        double volume = 0.787, double pressureMax = 41400, double temp = 293,
        IReadOnlyDictionary<string, double>? bulk = null, double basePrice = 410, double zScale = 0.5)
    {
        var values = new Dictionary<string, double>
        {
            ["StatVolume"] = volume,
            ["StatGasPressureMax"] = pressureMax,
            ["StatGasTemp"] = temp,
        };
        if (mols > 0) values["StatGasMol" + gas] = mols;
        foreach (var (cond, amount) in bulk ?? new Dictionary<string, double>()) values[cond] = amount;
        return Part(name, tileConds: ["IsFixture", "IsObstruction"], category: "HVAC", basePrice: basePrice,
            startingConds: ["IsAirtight", "IsInstalled", "IsVessel" + gas, "IsGasMolChanged"], condValues: values,
            zScale: zScale);
    }

    /// <summary>Register a parallax location (data/parallax) with the given sun-light names, for Light Viz's
    /// exterior daylight.</summary>
    public Fixtures Parallax(string name, params string[] sunLights)
    {
        _parallaxDefs[name] = new ParallaxDef(name, sunLights);
        return this;
    }

    /// <summary>Record an installed⇄loose form pair (as the game's install/uninstall jobs would), so
    /// <see cref="FormSwap"/> can map between them. Both defs should already be registered as parts.</summary>
    public Fixtures FormPair(string installed, string loose)
    {
        _looseForms[installed] = loose;
        _installedForms[loose] = installed;
        return this;
    }

    /// <summary>Record a broken → working pair (as the game's repair jobs would), so <see cref="Repair"/> can map
    /// between them. One-way, like the game: nothing offers to break a part again. Both defs should already be
    /// registered as parts.</summary>
    public Fixtures RepairPair(string broken, string working)
    {
        _repairForms[broken] = working;
        return this;
    }

    /// <summary>Register the DAMAGE direction: <paramref name="whole"/> breaks into <paramref name="broken"/> once
    /// its own <c>StatDamageMax</c> fills. Give each part its pool through <c>condValues</c>. Deliberately separate
    /// from <see cref="RepairPair"/>, because in real data the two are separate tables read out of different files
    /// and one is not the inverse of the other.</summary>
    public Fixtures BreakPair(string whole, string broken)
    {
        _breakForms[whole] = broken;
        return this;
    }

    /// <summary>The part registered under <paramref name="name"/>.</summary>
    public PartDef Get(string name) => _byName[name];

    /// <summary>Assemble the synthetic <see cref="Catalog"/> (no <see cref="Catalog.Index"/> — synthetic).</summary>
    public Catalog Build() => new()
    {
        Parts = _parts,
        ByDefName = _byName,
        Loots = _loots,
        Triggers = _trigs,
        LooseForms = _looseForms,
        InstalledForms = _installedForms,
        RepairForms = _repairForms,
        BreakForms = _breakForms,
        LightDefs = _lightDefs,
        ColorTable = _colorTable,
        ParallaxDefs = _parallaxDefs,
        InteractionDefs = _interactionDefs,
        GpmTemplates = _gpmTemplates,
        Warnings = [],
    };

    // ---- document helpers (static, install-free) ----

    /// <summary>A fresh document with the given placements applied via their commands (Do bypasses the law).</summary>
    public static ShipDocument Doc(Catalog cat, params Placement[] placements)
    {
        var doc = new ShipDocument(cat);
        foreach (var p in placements) new PlaceCommand(p).Do(doc);
        return doc;
    }

    /// <summary>Place <paramref name="def"/> at (x, y) with optional rotation, returning the placement.</summary>
    public static Placement Place(ShipDocument doc, string def, int x, int y, int rot = 0)
    {
        var p = new Placement { DefName = def, X = x, Y = y, Rot = rot };
        new PlaceCommand(p).Do(doc);
        return p;
    }

    /// <summary>A bare placement (not yet added to any document).</summary>
    public static Placement P(string def, int x, int y, int rot = 0) => new() { DefName = def, X = x, Y = y, Rot = rot };
}
