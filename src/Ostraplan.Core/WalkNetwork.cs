namespace Ostraplan.Core;

/// <summary>
/// What a walk analysis should count as passable. Both switches exist because the game's own answer depends on
/// state a plan does not carry: whether the crew is suited (so the hull is fair game) and which crew member is
/// asking (a Forbid zone matches a PersonSpec, so there is no single true walk zone).
/// </summary>
/// <param name="IncludeExterior">Count tiles that are not part of the ship. The game does: in free fall an
/// unsuited-irrelevant EVA route over the hull is walkable, and <c>Tile.IsWalkable</c> requires no floor at all.
/// Left on, almost every design collapses into one zone that reaches everything the long way round, which is why
/// this defaults to <b>off</b>: the interesting question is which compartments connect <i>inside</i>.</param>
/// <param name="RespectForbidZones">Treat painted Forbid zones as impassable, as they are for a crew member the
/// zone matches.</param>
/// <remarks>
/// Deliberately a reference type, not a record struct: a struct's parameter defaults do not survive
/// <c>default(T)</c> or <c>new()</c> (both zero-initialise), which would quietly turn
/// <see cref="RespectForbidZones"/> off for every caller that did not spell it out.
/// </remarks>
public sealed record WalkOptions(bool IncludeExterior = false, bool RespectForbidZones = true)
{
    public static readonly WalkOptions Default = new();
}

/// <summary>Why a device cannot be operated from inside, or <see cref="None"/> when it can.</summary>
public enum WalkBlock
{
    None,
    /// <summary>Operable, but only from outside the hull — a suited crew member on a spacewalk. This is the normal
    /// state of exterior-mounted equipment (hull rotors, external cargo pods, sensors), not a design fault.</summary>
    EvaOnly,
    /// <summary>The interaction's target point falls outside the grid entirely.</summary>
    NoTargetPoint,
    /// <summary>Nowhere in range of the target point is a tile a crew member could stand on.</summary>
    NoStandingTile,
    /// <summary>Somewhere in range is standable, but nothing in range can see the device.</summary>
    SightBlocked,
}

/// <summary>One connected set of walkable tiles. <see cref="Exterior"/> marks a zone that includes tiles which are
/// not part of the ship (only possible with <see cref="WalkOptions.IncludeExterior"/>).</summary>
public sealed record WalkZone(int Index, IReadOnlyList<int> Tiles, bool Exterior)
{
    public int TileCount => Tiles.Count;
}

/// <summary>
/// An installed part that offers an interaction requiring a crew member to walk up to it, and whether they can.
/// <see cref="Zone"/> is the walk zone they would operate it from, or −1 when it cannot be operated at all.
/// </summary>
/// <remarks>
/// A part is reported once, not once per interaction: the question a planner is asked is "can I use this thing",
/// and a part is usable when <b>any</b> of its interactions is. <see cref="Action"/> names the interaction the
/// verdict came from — the one that succeeded, or (when none did) the most permissive one, whose failure is the
/// most informative.
/// </remarks>
/// <param name="BodyTiles">The grid tiles the part itself occupies. This, not <paramref name="TargetTile"/>, is
/// what a UI should point at: the target tile is where a crew member would <i>stand</i>, which is usually a tile
/// belonging to some other part (the floor in front, or the wall the fitting is set into), so marking it names the
/// wrong thing and selects the wrong part when clicked.</param>
public sealed record WalkDevice(
    PlacedPart Part, string Friendly, string Action, int TargetTile, double Range, int Zone, WalkBlock Reason,
    IReadOnlyList<int> BodyTiles)
{
    /// <summary>Operable on foot from a walk zone, without leaving the ship.</summary>
    public bool Reachable => Zone >= 0;

    /// <summary>Operable, but only on a spacewalk. See <see cref="WalkBlock.EvaOnly"/>.</summary>
    public bool EvaOnly => Reason == WalkBlock.EvaOnly;

    /// <summary>Genuinely unusable: no crew member can operate it, suited or not.</summary>
    public bool Blocked => !Reachable && !EvaOnly;
}

/// <summary>
/// The walk analysis of a grid: which tiles are walkable, how they group into connected zones, which installed
/// devices can be operated and from where, and which doorways need a suit to cross.
/// </summary>
public sealed record WalkResult(
    IReadOnlyList<WalkZone> Zones,
    IReadOnlyList<int> TileZone,
    IReadOnlyList<bool> Walkable,
    IReadOnlyList<WalkDevice> Devices,
    IReadOnlyList<int> EvaOnlyPortals)
{
    public static readonly WalkResult Empty = new([], [], [], [], []);

    /// <summary>Devices no crew member can operate at all — excluding the ones that merely need a spacewalk, which
    /// is how exterior-mounted equipment is always reached and is not a fault worth reporting.</summary>
    public IEnumerable<WalkDevice> Unreachable => Devices.Where(d => d.Blocked);

    /// <summary>Devices operable only from outside the hull.</summary>
    public IEnumerable<WalkDevice> EvaOnlyDevices => Devices.Where(d => d.EvaOnly);

    /// <summary>The zone holding the most tiles, or −1 when there are none — the "main body" of the ship, against
    /// which an isolated compartment is worth reporting.</summary>
    public int LargestZone
    {
        get
        {
            var best = -1;
            for (var i = 0; i < Zones.Count; i++)
                if (Zones[i].Exterior is false && (best < 0 || Zones[i].TileCount > Zones[best].TileCount)) best = i;
            return best;
        }
    }

    public bool IsEmpty => Zones.Count == 0 && Devices.Count == 0;
}

/// <summary>The walk analysis in <b>document</b> coordinates, ready for the canvas to paint without holding the
/// grid — the same flat, UI-free shape <see cref="PowerOverlay"/> uses.</summary>
public sealed record WalkOverlay(
    IReadOnlyList<IReadOnlyList<(int X, int Y)>> Zones,
    IReadOnlyList<bool> ZoneIsExterior,
    IReadOnlyList<(int X, int Y)> UnreachableDevices,
    IReadOnlyList<(int X, int Y)> EvaOnlyDevices,
    IReadOnlyList<(int X, int Y)> EvaOnlyPortals)
{
    public static readonly WalkOverlay Empty = new([], [], [], [], []);

    public bool IsEmpty => Zones.Count == 0 && UnreachableDevices.Count == 0 && EvaOnlyDevices.Count == 0;
}

/// <summary>
/// Port of the game's crew walkability and interaction-reach rules (verified 0.15.1.6): which tiles
/// <c>Tile.IsWalkable</c> admits, how <c>Ostranauts.Pathing.JumpPointSearch</c> connects them, and the range +
/// sight gate <c>Interaction.Triggered</c> applies before a device can be used.
///
/// <para><b>Walkable</b> (<c>Tile.IsWalkable</c>, in order): a Forbid-zone tile the crew matches is out; a wall
/// that is not a portal is out; a portal is out when it is <c>IsPortalStuck</c>; a tile is out when it carries
/// <c>IsObstruction</c> <i>and</i> <c>IsFixture</c> (the game's <c>bPassable = !IsObstruction</c>). Two of the
/// game's tests are runtime-only and not modelled: burning tiles (<c>IsTileBurning</c>) and the EVA-under-gravity
/// gate (which needs the ship's world position and is inert in normal flight).</para>
///
/// <para><b>No floor is required.</b> An empty in-grid tile carries none of those conditions and so is walkable —
/// that is the game's spacewalk case, and it is why <see cref="WalkOptions.IncludeExterior"/> defaults to off.</para>
///
/// <para><b>Door state is not cosmetic here.</b> Unlike rooms and the rating — where an open and a closed door
/// seal identically — walking cares which door it is. <c>ItmDoor01Closed</c> (unpowered), <c>…ClosedOnLocked</c>,
/// <c>…ClosedDmg</c> and <c>ItmDockSys03ClosedDmg</c> all add <c>TILPortalClosedStuck</c>, carrying
/// <c>IsPortalStuck</c>, and genuinely seal a section off. <c>ItmDoor01ClosedOn</c> adds plain
/// <c>TILPortalClosed</c> and is passable, because crew open it.</para>
///
/// <para><b>Connectivity.</b> The game pathfinds with jump-point search over eight directions; for a zone
/// partition only its adjacency rule matters: the four cardinals always, and a diagonal only when at least one of
/// the two shared orthogonal cells is walkable (<c>JumpPointSearch.Jump</c> rejects a diagonal whose two
/// behind-orthogonals are both blocked).</para>
///
/// <para><b>Reach.</b> Each installed part's condowner names <c>aInteractions</c>; each interaction names a target
/// map point (nearly always <c>use</c>) and a stand-off range. The crew must stand within that range measured as
/// <b>Chebyshev</b> distance (<c>TileUtils.TileRange</c> = max(|dx|,|dy|)) on a tile that is walkable, is not
/// <c>IsFixture</c>, and has line of sight to the part (<see cref="LineOfSight"/>). Ranges are per-interaction —
/// a nav console 0, an air pump 1, a cooler/heater/bed 2, a reactor 3 — so no single radius would do.</para>
///
/// <para>This is connectivity and reach, not a crew simulation: no pathing costs, no doors opening over time, no
/// crew occupancy. It is to walking what <see cref="PowerNetwork"/> is to conduit.</para>
/// </summary>
public static class WalkNetwork
{
    /// <summary>The game's <c>TIsShipTile</c> (an OR): what makes a tile part of the ship rather than open space.
    /// Distinct from <c>TIsShipTileOrSub</c> (see <see cref="RoomBuilder"/>), which also counts <c>IsSubTile</c>.</summary>
    private static readonly string[] ShipTileConds = ["IsFloor", "IsFixture", "IsObstruction", "IsPortal", "IsWall"];

    /// <summary>
    /// Mineable rock and ice (28 core defs: the <c>ItmWallRock*</c> / <c>ItmFloorRock*</c> / <c>Itm*Ice*</c>
    /// families, none of them buildable). They carry an <c>ACTMine</c> interaction, so they technically look like
    /// operable fittings, but they are <b>terrain</b>: a block in the middle of an asteroid is unreachable until
    /// you dig to it, which is what rock is, not a fault in the design. Left in, Port Mojave alone reports 1,811
    /// "unreachable devices" and buries the two findings that matter.
    /// </summary>
    private const string MineableCond = "IsMineable";

    /// <summary>
    /// Analyse a grid. <paramref name="forbiddenTiles"/> are grid tile indices covered by a painted Forbid zone
    /// (zones are document overlays and contribute no tile conditions, so the caller projects them — see
    /// <see cref="ForbiddenTiles"/>); pass null when there are none.
    /// </summary>
    public static WalkResult Build(
        ShipGrid grid, Catalog catalog, WalkOptions? options = null, IReadOnlySet<int>? forbiddenTiles = null)
    {
        options ??= WalkOptions.Default;
        var n = grid.TileCount;
        if (n == 0) return WalkResult.Empty;

        var walkable = new bool[n];
        for (var t = 0; t < n; t++) walkable[t] = IsWalkable(grid, t, options, forbiddenTiles);

        var tileZone = Label(grid, walkable);
        var zones = Collect(grid, tileZone, walkable);

        // The EVA fallback mask: the same rule with the hull exterior counted, used only to tell "nobody can ever
        // use this" apart from "you'd suit up for it". Skipped when the analysis already counts the exterior, since
        // then it would be the identical mask.
        bool[]? evaWalkable = null;
        if (!options.IncludeExterior)
        {
            var evaOpts = options with { IncludeExterior = true };
            evaWalkable = new bool[n];
            for (var t = 0; t < n; t++) evaWalkable[t] = IsWalkable(grid, t, evaOpts, forbiddenTiles);
        }

        var devices = Reach(grid, catalog, walkable, tileZone, evaWalkable);
        var evaPortals = EvaOnlyPortals(grid);

        return new WalkResult(zones, tileZone, walkable, devices, evaPortals);
    }

    /// <summary>Project a document's Forbid zones onto grid tile indices, for <see cref="Build"/>. Zones are held
    /// in document coords and the grid's origin is <c>VShipPos</c>, so this is the inverse of
    /// <see cref="ShipGrid.GridToDoc"/>.</summary>
    public static IReadOnlySet<int> ForbiddenTiles(ShipDocument doc, ShipGrid grid)
    {
        var set = new HashSet<int>();
        foreach (var zone in doc.Zones)
        {
            if (!zone.IsForbid) continue;
            foreach (var (x, y) in zone.Tiles)
            {
                int col = x - (int)grid.VShipPosX, row = y - (int)grid.VShipPosY;
                if (grid.InBounds(col, row)) set.Add(grid.Index(col, row));
            }
        }
        return set;
    }

    /// <summary>Port of <c>Tile.IsWalkable</c> — see the type remarks for the rule and what is deliberately
    /// omitted.</summary>
    private static bool IsWalkable(ShipGrid grid, int t, WalkOptions options, IReadOnlySet<int>? forbidden)
    {
        if (options.RespectForbidZones && forbidden is not null && forbidden.Contains(t)) return false;
        if (!options.IncludeExterior && !IsShipTile(grid, t)) return false;

        var portal = grid.Has(t, "IsPortal");
        if (grid.Has(t, "IsWall") && !portal) return false;
        if (portal && grid.Has(t, "IsPortalStuck")) return false;
        // bPassable = !IsObstruction; the game blocks only an obstruction that is ALSO a fixture, which is why an
        // open door (obstruction, no fixture) is walkable and an under-floor rack (fixture, no obstruction) is too
        if (grid.Has(t, "IsObstruction") && grid.Has(t, "IsFixture")) return false;
        return true;
    }

    private static bool IsShipTile(ShipGrid grid, int t) => ShipTileConds.Any(c => grid.Has(t, c));

    /// <summary>
    /// Flood each connected component of the walkable mask, using the game's own adjacency: four cardinals
    /// unconditionally, plus a diagonal only when at least one of the two orthogonal cells it passes between is
    /// walkable (a crew member cannot squeeze through a perfect corner).
    /// </summary>
    private static int[] Label(ShipGrid grid, bool[] walkable)
    {
        var tileZone = new int[walkable.Length];
        Array.Fill(tileZone, -1);
        var next = 0;
        var queue = new Queue<int>();

        for (var seed = 0; seed < walkable.Length; seed++)
        {
            if (!walkable[seed] || tileZone[seed] >= 0) continue;
            var zone = next++;
            tileZone[seed] = zone;
            queue.Enqueue(seed);
            while (queue.Count > 0)
            {
                var t = queue.Dequeue();
                foreach (var nt in Neighbours(grid, walkable, t))
                {
                    if (tileZone[nt] >= 0) continue;
                    tileZone[nt] = zone;
                    queue.Enqueue(nt);
                }
            }
        }
        return tileZone;
    }

    /// <summary>The walkable neighbours of a tile under the game's movement rule (see <see cref="Label"/>).</summary>
    private static IEnumerable<int> Neighbours(ShipGrid grid, bool[] walkable, int t)
    {
        var col = grid.Col(t);
        var row = grid.Row(t);

        bool Open(int c, int r) => grid.InBounds(c, r) && walkable[grid.Index(c, r)];

        for (var dr = -1; dr <= 1; dr++)
            for (var dc = -1; dc <= 1; dc++)
            {
                if (dc == 0 && dr == 0) continue;
                int c = col + dc, r = row + dr;
                if (!Open(c, r)) continue;
                // a diagonal needs one of the two orthogonals it cuts between to be open
                if (dc != 0 && dr != 0 && !Open(col + dc, row) && !Open(col, row + dr)) continue;
                yield return grid.Index(c, r);
            }
    }

    private static List<WalkZone> Collect(ShipGrid grid, int[] tileZone, bool[] walkable)
    {
        var tiles = new List<List<int>>();
        var exterior = new List<bool>();
        for (var t = 0; t < tileZone.Length; t++)
        {
            var z = tileZone[t];
            if (z < 0) continue;
            while (tiles.Count <= z) { tiles.Add([]); exterior.Add(false); }
            tiles[z].Add(t);
            if (!IsShipTile(grid, t)) exterior[z] = true;
        }
        return [.. tiles.Select((ts, i) => new WalkZone(i, ts, exterior[i]))];
    }

    /// <summary>
    /// Classify every installed part that offers an approach-needing interaction. See <see cref="WalkDevice"/> for
    /// why a part is reported once rather than once per interaction.
    /// </summary>
    private static List<WalkDevice> Reach(
        ShipGrid grid, Catalog catalog, bool[] walkable, int[] tileZone, bool[]? evaWalkable)
    {
        var devices = new List<WalkDevice>();
        LineOfSight? sight = null;   // built lazily: most grids in tests have no interactable parts at all

        foreach (var part in grid.Parts)
        {
            if (catalog.Lookup(part.Part.DefName) is not { } def) continue;
            if (!def.StartingConds.Contains("IsInstalled")) continue;   // loose stock is not a fixture to reach
            if (def.StartingConds.Contains(MineableCond)) continue;     // terrain, not a fitting — see the constant
            var actions = catalog.InteractionsFor(def);
            if (actions.Count == 0) continue;

            sight ??= LineOfSight.Build(grid, catalog);

            WalkDevice? best = null;
            foreach (var ia in actions.OrderByDescending(a => a.TargetPointRange))
            {   // most permissive first: the verdict is "can this be used at all"
                // GetPos falls back to the item centre for a point the def does not declare
                var declared = def.MapPoints.TryGetValue(ia.TargetPoint!, out var pt);
                var target = grid.MapPointTile(part, declared ? pt : (0.0, 0.0));

                // An undeclared target point resolves to the device's own body, which for a wall fitting is the
                // wall itself — so a range-0 interaction would demand standing inside it. That is a gap in the
                // def, not a fault in the layout: no redesign can make a wall light's own tile walkable, so
                // reporting it is noise the user cannot act on (44 of the core fleet's wall lights alone). Read
                // "range 0 from the body" as "next to the body" instead. Deliberate deviation, not a port.
                var range = declared ? ia.TargetPointRange : Math.Max(ia.TargetPointRange, 1.0);

                var zone = -1;
                var reason = WalkBlock.NoTargetPoint;
                if (target >= 0)
                {
                    var (stand, why) = Operate(grid, sight, walkable, part, target, range);
                    reason = why;
                    if (stand >= 0) zone = tileZone[stand];

                    // Nothing inside can reach it: is that because it is mounted on the hull (normal, suit up) or
                    // because it is genuinely walled in (a real fault)? Re-ask with the exterior counted to tell
                    // them apart, so external rotors and cargo pods stop drowning out the findings that matter.
                    if (stand < 0 && evaWalkable is not null
                        && Operate(grid, sight, evaWalkable, part, target, range).Stand >= 0)
                        reason = WalkBlock.EvaOnly;
                }

                var candidate = new WalkDevice(
                    part, def.Friendly, ia.Label, target, range, zone, reason, Body(grid, part));
                best ??= candidate;                       // the most permissive interaction, as the fallback verdict
                if (zone >= 0) { best = candidate; break; }
            }
            if (best is not null) devices.Add(best);
        }
        return devices;
    }

    /// <summary>The grid tiles a placed part occupies, using its <b>rotated</b> footprint. Tiles falling off the
    /// grid are dropped.</summary>
    private static IReadOnlyList<int> Body(ShipGrid grid, PlacedPart part)
    {
        var (w, h) = GridMath.Size(part.Part.Item.Width, part.Part.Item.Height, part.Rot);
        var tiles = new List<int>(w * h);
        for (var r = 0; r < h; r++)
            for (var c = 0; c < w; c++)
            {
                int col = part.TopLeftCol + c, row = part.TopLeftRow + r;
                if (grid.InBounds(col, row)) tiles.Add(grid.Index(col, row));
            }
        return tiles;
    }

    /// <summary>
    /// Find a tile a crew member could operate this part from, or −1 with the reason why not — the port of the
    /// game's two-tier destination choice (<c>Pathfinder.GetPath</c>).
    ///
    /// <para><b>Tier 1, the preference</b> (<c>GetClosestWalkableDestination</c>): a tile within
    /// <paramref name="range"/> of <paramref name="target"/> that is walkable, is <b>not</b> <c>IsFixture</c>, and
    /// has line of sight. The <c>IsFixture</c> rejection is not redundant with walkability — an under-floor rack
    /// (<c>IsFloorSealed</c> + <c>IsFixture</c>, no <c>IsObstruction</c>) is perfectly walkable, but the game would
    /// rather not park a working crew member on one.</para>
    ///
    /// <para><b>Tier 2, the fallback</b>: when that search comes up empty the game does <b>not</b> give up — it
    /// runs <c>closestWalkableDestination.Add(destination)</c> and paths to the target tile itself, which succeeds
    /// whenever <c>Tile.IsWalkable</c> admits it. This is the leeway that matters in practice: a cargo bay floored
    /// wall to wall in racks, or a switch ringed by fixtures, has no "clean" tile anywhere in range, and modelling
    /// only tier 1 reports the whole bay unusable. Nothing on this path is sight-checked, because the game's
    /// fallback goes straight to the jump-point search, which tests walkability and nothing else.</para>
    /// </summary>
    private static (int Stand, WalkBlock Reason) Operate(
        ShipGrid grid, LineOfSight sight, bool[] walkable, PlacedPart part, int target, double range)
    {
        // CEIL, not floor or truncate: the game's destination search sizes its band with
        // Mathf.CeilToInt(fRangeGoal), so a 1.5-tile interaction (a battery panel, an air vent) genuinely reaches
        // two tiles out. Rounding the other way cost a whole ring of standing room on every fractional range.
        var r = (int)Math.Ceiling(range);
        int tc = grid.Col(target), tr = grid.Row(target);
        var stoodAnywhere = false;

        for (var dr = -r; dr <= r; dr++)
            for (var dc = -r; dc <= r; dc++)
            {
                int c = tc + dc, row = tr + dr;
                if (!grid.InBounds(c, row)) continue;
                var t = grid.Index(c, row);
                if (!walkable[t] || grid.Has(t, "IsFixture")) continue;
                stoodAnywhere = true;
                if (!sight.IsVisible(part, t, ignoreEndpoints: true)) continue;
                return (t, WalkBlock.None);
            }

        if (walkable[target]) return (target, WalkBlock.None);   // tier 2

        return (-1, stoodAnywhere ? WalkBlock.SightBlocked : WalkBlock.NoStandingTile);
    }

    /// <summary>
    /// Doorways that need a suit: a portal with a pressure difference across it, which the game refuses to route an
    /// unpermitted crew member through (<c>Tile.IsWalkable</c> → <c>Pathfinder.CheckDoorPressure</c>).
    ///
    /// <para>A plan has no gas simulation, so this uses the room partition as the stand-in: a portal is EVA-only
    /// when one side is a Void room (unsealed, open to space) and another is not. That is an approximation of a
    /// live pressure reading, and is reported as advice rather than treated as a wall — crew with airlock
    /// permission do cross these, so the zones stay joined and the crossing is merely flagged.</para>
    /// </summary>
    private static List<int> EvaOnlyPortals(ShipGrid grid)
    {
        var partition = RoomBuilder.Build(grid);
        var result = new List<int>();

        for (var t = 0; t < grid.TileCount; t++)
        {
            if (!grid.Has(t, "IsPortal")) continue;
            bool voidSide = false, sealedSide = false;
            foreach (var nt in RoomBuilder.Cardinals(grid, t))
            {
                if (nt < 0) { voidSide = true; continue; }   // straight off the grid edge is open space
                var ri = partition.TileRoom[nt];
                if (ri < 0) continue;                        // a wall: no room either way
                if (partition.Rooms[ri].Void) voidSide = true; else sealedSide = true;
            }
            if (voidSide && sealedSide) result.Add(t);
        }
        return result;
    }

    /// <summary>Project a <see cref="WalkResult"/> into document coordinates for the canvas.</summary>
    public static WalkOverlay ToOverlay(ShipGrid grid, WalkResult result)
    {
        if (result.IsEmpty) return WalkOverlay.Empty;

        return new WalkOverlay(
            [.. result.Zones.Select(z => (IReadOnlyList<(int X, int Y)>)z.Tiles.Select(grid.GridToDoc).ToArray())],
            [.. result.Zones.Select(z => z.Exterior)],
            // the device's own body, not where a crew member would stand — see WalkDevice.BodyTiles
            [.. result.Unreachable.SelectMany(d => d.BodyTiles).Distinct().Select(grid.GridToDoc)],
            [.. result.EvaOnlyDevices.SelectMany(d => d.BodyTiles).Distinct().Select(grid.GridToDoc)],
            [.. result.EvaOnlyPortals.Select(grid.GridToDoc)]);
    }
}
