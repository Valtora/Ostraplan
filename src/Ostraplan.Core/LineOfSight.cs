namespace Ostraplan.Core;

/// <summary>
/// Port of the game's <c>Visibility.IsCondOwnerLOSVisibleBlocks</c> (verified 0.15.1.6) — the sight test that,
/// alongside range, decides whether a crew member standing on a given tile may actually operate a device.
///
/// <para>The rule, exactly as the game runs it for the interaction gate
/// (<c>Interaction.Triggered</c>, which calls it with <c>bIgnoreEndpoints: false</c>, <c>bIgnoreGlass: true</c>):
/// sight runs from the target's <b>LOS</b> map point (falling back to its centre, since <c>CondOwner.GetPos</c>
/// returns <c>tf.position</c> for a point the def does not declare) to the standing position. Anything within one
/// unit is visible outright. Otherwise every occluder box is tested, skipping glass, boxes belonging to the target
/// itself, and any box that <i>contains</i> either endpoint — that last exemption is what lets a crew member reach
/// a device whose own footprint they are standing inside. A proper intersection with any of a box's four edges
/// blocks sight.</para>
///
/// <para>The occluders are the same <c>aShadowBoxes</c> set Light Viz floods
/// (<see cref="LightNetwork.Occluders"/>), so the two agree by construction: a window is glass and sight passes,
/// a thin/aero wall carries no boxes and blocks nothing, an open door blocks only its two end caps, and a bed or
/// canister does block. Everything runs in <b>game</b> coords (+y up) like <see cref="VisibilityMesh"/>, converting
/// at the boundary.</para>
/// </summary>
public sealed class LineOfSight
{
    private readonly ShipGrid _grid;
    private readonly (PlacedPart Owner, LightBlock Block)[] _blocks;

    private LineOfSight(ShipGrid grid, (PlacedPart, LightBlock)[] blocks)
    {
        _grid = grid;
        _blocks = blocks;
    }

    /// <summary>Resolve the grid's occluders once, ready to answer many sight queries.</summary>
    public static LineOfSight Build(ShipGrid grid, Catalog catalog) =>
        new(grid, LightNetwork.Occluders(grid, catalog).ToArray());

    /// <summary>The game-coord point sight is measured from for a part: its <c>LOS</c> map point, or its centre
    /// when it declares none (<c>CondOwner.GetPos</c>'s fallback).</summary>
    public (double X, double Y) SightOrigin(PlacedPart part)
    {
        var px = part.Part.MapPoints.TryGetValue("LOS", out var los) ? los : (0.0, 0.0);
        var (col, row) = _grid.MapPointPos(part, px);
        return ToGame(col, row);
    }

    /// <summary>The game-coord centre of a grid tile.</summary>
    public (double X, double Y) TileCentre(int tile) =>
        ToGame(_grid.Col(tile) + 0.5, _grid.Row(tile) + 0.5);

    /// <summary>Continuous grid position (cell (c,r) centred at (c+0.5, r+0.5), +y down) → game coords (+y up),
    /// the same conversion <see cref="LightNetwork.Occluders"/> applies to a block centre.</summary>
    private (double X, double Y) ToGame(double col, double row) =>
        (col + _grid.VShipPosX, -(row + _grid.VShipPosY));

    /// <summary>
    /// True when the part sits <b>inside the hull line</b> — its anchor tile carries <c>IsWall</c>. Sensors,
    /// antennas, wall lights, ship weapons and cladding are all mounted this way (roughly 87 core defs; the same
    /// population that needs the room-membership use-point fallback, see <c>RoomBuilder.AssignParts</c>).
    ///
    /// <para><b>Why they are exempt from the sight test.</b> Their sight origin is a point within the wall, so
    /// every ray out of the ship crosses the co-planar occluder boxes of the neighbouring wall tiles and the
    /// segment test refuses every standing tile in the room. The game does not have this problem, because the
    /// branch that decides where to stand falls back to <c>Visibility.IsCondOwnerLOSVisible</c> — a
    /// <c>Physics.RaycastNonAlloc</c>, which a planner cannot reproduce (§5.7's in-game-only predicates) and which
    /// does not register the collider its origin is inside. Rather than emit a false "cannot be reached" for every
    /// wall-embedded fitting on the ship, sight is granted; range and walkability still have to be satisfied.</para>
    /// </summary>
    private bool IsEmbedded(PlacedPart part) =>
        part.AnchorIndex >= 0 && _grid.Has(part.AnchorIndex, "IsWall");

    /// <summary>Can a crew member standing on <paramref name="standingTile"/> see <paramref name="target"/>?
    /// <paramref name="ignoreEndpoints"/> as on the overload below — reach queries want <c>true</c>.</summary>
    public bool IsVisible(PlacedPart target, int standingTile, bool ignoreEndpoints = false) =>
        IsVisible(target, TileCentre(standingTile), ignoreEndpoints);

    /// <summary>
    /// The sight test itself, from <paramref name="target"/>'s LOS point to a game-coord position.
    /// <paramref name="ignoreEndpoints"/> mirrors the game's <c>bIgnoreEndpoints</c>: <c>false</c> treats a
    /// segment that merely grazes a box edge as blocking, <c>true</c> does not.
    ///
    /// <para><b>Reach queries want <c>true</c>.</b> The strict form is what
    /// <c>Interaction.Triggered</c> uses, but only on its <c>bNoWalk</c> branch — for an interaction the crew walks
    /// to, the deciding call is <c>Pathfinder.GetClosestWalkableDestination</c>, which passes
    /// <c>bIgnoreEndpoints: true</c> (and, for axis-aligned targets, defers to a physics raycast that blocks only
    /// on installed walls and closed portals). The difference is not cosmetic: a device embedded <i>in</i> the hull
    /// line — a sensor, a wall light, a ship weapon — sights along its own wall's edge to reach the room, so the
    /// strict form refuses every tile and reports it unusable when the game is perfectly happy.</para>
    /// </summary>
    public bool IsVisible(PlacedPart target, (double X, double Y) at, bool ignoreEndpoints = false)
    {
        var from = SightOrigin(target);
        var dx = at.X - from.X;
        var dy = at.Y - from.Y;
        if (dx * dx + dy * dy <= 1.0) return true;   // MathUtils.GetDistanceSquared(pos, where) <= 1
        if (IsEmbedded(target)) return true;         // see below

        // segment bounds, so a box nowhere near the sight line is rejected before its four edges are solved
        double minX = Math.Min(from.X, at.X), maxX = Math.Max(from.X, at.X);
        double minY = Math.Min(from.Y, at.Y), maxY = Math.Max(from.Y, at.Y);

        foreach (var (owner, b) in _blocks)
        {
            if (ReferenceEquals(owner, target)) continue;   // block.TF.parent == co.tf — a part never blocks itself
            if (b.X + b.Rx < minX || b.X - b.Rx > maxX || b.Y + b.Ry < minY || b.Y - b.Ry > maxY) continue;
            // a box containing either endpoint is exempt (you can see out of, and into, the thing you stand in)
            if (Math.Abs(b.X - from.X) < b.Rx && Math.Abs(b.Y - from.Y) < b.Ry) continue;
            if (Math.Abs(b.X - at.X) < b.Rx && Math.Abs(b.Y - at.Y) < b.Ry) continue;

            foreach (var (a, c) in b.Segments())
            {
                float s = 0f, t = 0f;
                if (!VisibilityMesh.SolveST(ref s, ref t,
                        (float)from.X, (float)from.Y, (float)at.X, (float)at.Y, a.X, a.Y, c.X, c.Y))
                    continue;
                if (ignoreEndpoints)
                {
                    if (s <= 0f || 1f <= s || t <= 0f || 1f <= t) continue;
                }
                else if (s < 0f || 1f < s || t < 0f || 1f < t) continue;
                return false;
            }
        }
        return true;
    }
}
