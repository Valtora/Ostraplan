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
    private readonly Dictionary<string, LightDef> _lightDefs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ColorDef> _colorTable = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ParallaxDef> _parallaxDefs = new(StringComparer.Ordinal);

    /// <summary>Register a named loot (the bundle of conditions a tile socket adds).</summary>
    public Fixtures Loot(string name, params string[] conds)
    {
        _loots[name] = new LootDef(name, conds, []);
        return this;
    }

    /// <summary>Register a presence-only condtrigger: every req present, no forbid present.</summary>
    public Fixtures Trig(string name, string[] reqs, string[]? forbids = null)
    {
        _trigs[name] = new CondTriggerDef(name, reqs, forbids ?? [], false);
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

    /// <summary>
    /// Add a part. Each of its <c>w×h</c> footprint tiles adds <paramref name="tileConds"/> (auto-registered
    /// as a loot named "<c>&lt;name&gt;Adds</c>"), so the part contributes real conditions to the grid.
    /// <paramref name="reqs"/>/<paramref name="forbids"/> are the socket ring the placement law tests
    /// (3×3 flattened for a 1×1). CO-level metadata (container grid, stack limit, base price, map points) via
    /// the optional args.
    /// </summary>
    public Fixtures Part(string name, int w = 1, int h = 1, string[]? tileConds = null,
        string[]? reqs = null, string[]? forbids = null, string category = "MISC",
        string[]? startingConds = null, (int W, int H)? container = null, string? containerCT = null,
        int stackLimit = 0, IReadOnlyDictionary<string, (double X, double Y)>? mapPoints = null,
        double basePrice = 0, bool sheet = false, string origin = "core",
        IReadOnlyDictionary<string, double>? condValues = null,
        IReadOnlyList<(double X, double Y)>? powerInputs = null, (double X, double Y)? powerOutput = null,
        string[]? lights = null, ShadowBox[]? shadowBoxes = null, bool lightWall = false)
    {
        string[] adds;
        if (tileConds is { Length: > 0 })
        {
            var lootName = name + "Adds";
            _loots[lootName] = new LootDef(lootName, tileConds, []);
            adds = [.. Enumerable.Repeat(lootName, w * h)];
        }
        else adds = [.. Enumerable.Repeat("Blank", w * h)];

        var item = new ItemDef(name, name + ".png", sheet, null, 0, w, adds, reqs ?? [], forbids ?? [])
        {
            Lights = lights ?? [],
            ShadowBoxes = shadowBoxes ?? [],
            IsWallForLight = lightWall,
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
        };
        _parts.Add(part);
        _byName[name] = part;
        return this;
    }

    // ---- semantic shortcuts (game-authentic tile conditions) ----

    /// <summary>A sealed floor tile (IsFloor + IsFloorSealed) — the walkable base rooms flood over.</summary>
    public Fixtures Floor(string name = "Floor") => Part(name, tileConds: ["IsFloor", "IsFloorSealed"], category: "HULL");

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

    /// <summary>A thin power conduit (IsPowerConduit) — the top render layer.</summary>
    public Fixtures Conduit(string name = "Conduit") => Part(name, tileConds: ["IsPowerConduit"], category: "POWR");

    /// <summary>A generic solid fixture (IsFixture + IsObstruction).</summary>
    public Fixtures Fixture(string name, int w = 1, int h = 1) => Part(name, w, h, tileConds: ["IsFixture", "IsObstruction"], category: "FURN");

    /// <summary>A container fixture with an inventory grid of the given size and an optional accept-filter trigger.</summary>
    public Fixtures Container(string name, int gridW = 4, int gridH = 4, string? filterCt = null) =>
        Part(name, tileConds: ["IsFixture", "IsObstruction", "IsContainer"], startingConds: ["IsContainer"],
            container: (gridW, gridH), containerCT: filterCt, category: "FURN");

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
        LightDefs = _lightDefs,
        ColorTable = _colorTable,
        ParallaxDefs = _parallaxDefs,
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
