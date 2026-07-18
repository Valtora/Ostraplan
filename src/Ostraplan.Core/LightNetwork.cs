namespace Ostraplan.Core;

/// <summary>
/// One light as Light Viz draws it: its centre and radius, its colour + intensity, and the <b>visibility polygon</b>
/// it illuminates — the region reachable from the centre before a wall cuts it off, as a fan of boundary points in
/// document coords (sub-tile). The canvas fills this polygon with a radial gradient (bright centre fading out),
/// clipped exactly to the shadow the walls cast. All coordinates are document tiles.
/// </summary>
public sealed record LightPoly(
    double CenterX, double CenterY, double Radius,
    byte R, byte G, byte B, double Intensity,
    IReadOnlyList<(double X, double Y)> Polygon);

/// <summary>
/// The ship's interior lighting as a set of per-light visibility polygons, ready to draw. Built by
/// <see cref="LightNetwork.Build"/>. A flat, UI-free payload the canvas renders directly.
/// </summary>
public sealed record LightOverlay(IReadOnlyList<LightPoly> Lights)
{
    public static readonly LightOverlay Empty = new([]);

    public bool IsEmpty => Lights.Count == 0;
}

/// <summary>
/// Reproduces the game's interior lighting model for Light Viz (v2): black ambient, a light illuminates iff its
/// colour is not <c>"Blank"</c>, intensity = colour alpha/255, default radius 6 tiles, 16 px/tile (all verified
/// against the decompiled <c>Visibility</c> + <c>Item</c> light attach, 0.15.x). Each light is placed at its exact
/// sub-tile position (so a wall light faces its room) and its lit region is found by <b>angular ray casting</b>:
/// a fan of rays marches the grid (a DDA against wall tiles) and stops at the exact point each ray
/// enters a wall, or at the radius. The fan's hit points form the visibility polygon the canvas fills with a
/// radial falloff, giving smooth gradients and crisp straight shadow edges.
///
/// <para>This is a visibility-polygon reconstruction of the game's shadow-cast light model, not a transliteration
/// of its Unity mesh code, and not the per-tick renderer — no normals, ambient occlusion or flicker are modelled,
/// and (this slice) glass occludes like any wall and exterior star light is not yet added. Lighting gates nothing
/// (there is no darkness gameplay stat), so it is a faithful preview, not a Law-critical port.</para>
/// </summary>
public static class LightNetwork
{
    /// <summary>Rays per light in the visibility fan. Enough that straight shadow edges stay straight (their
    /// vertices land on the wall face) and the radial rim reads smooth once the gradient fades it out.</summary>
    private const int Rays = 180;

    public static LightOverlay Build(ShipGrid grid, Catalog catalog)
    {
        var n = grid.TileCount;
        // Only walls (and closed doors, which carry IsWall) occlude light. Fixtures/obstructions do NOT: the game
        // lights a room smoothly across its clutter, so blocking on every console/tank would cast a jagged shadow
        // off each one — the faceted mess a per-obstruction test produces. Wall-only keeps light room-bounded and
        // smooth, and the hull (walls) naturally keeps interior light from spilling into space.
        var blocker = new bool[n];
        for (var t = 0; t < n; t++)
            blocker[t] = grid.Has(t, "IsWall");

        var lights = new List<LightPoly>();
        foreach (var part in grid.Parts)
        {
            if (catalog.Lookup(part.Part.DefName) is not { } def) continue;
            foreach (var light in catalog.LightsFor(def))
            {
                if (!light.CastsLight || light.Intensity <= 0 || light.Radius <= 0) continue;

                var (ox, oy) = grid.MapPointPos(part, (light.OffsetX, light.OffsetY));
                if (!grid.InBounds((int)Math.Floor(ox), (int)Math.Floor(oy))) continue;   // light sits off-grid

                var poly = CastFan(grid, blocker, ox, oy, light.Radius);
                lights.Add(new LightPoly(
                    ox + grid.VShipPosX, oy + grid.VShipPosY, light.Radius,
                    light.R, light.G, light.B, light.Intensity, poly));
            }
        }
        return lights.Count == 0 ? LightOverlay.Empty : new LightOverlay(lights);
    }

    /// <summary>The visibility polygon: one boundary point per fan ray, in document coords.</summary>
    private static (double X, double Y)[] CastFan(ShipGrid grid, bool[] blocker, double ox, double oy, double radius)
    {
        var pts = new (double X, double Y)[Rays];
        for (var i = 0; i < Rays; i++)
        {
            var ang = i * (2.0 * Math.PI / Rays);
            var (hx, hy) = March(grid, blocker, ox, oy, Math.Cos(ang), Math.Sin(ang), radius);
            pts[i] = (hx + grid.VShipPosX, hy + grid.VShipPosY);
        }
        return pts;
    }

    /// <summary>
    /// March a ray from (ox, oy) in grid-continuous space along (dx, dy) until it enters a wall tile (returning the
    /// exact entry point on that tile's face) or reaches <paramref name="radius"/>. A grid DDA (Amanatides &amp;
    /// Woo); the light's own tile is skipped because the walk only tests tiles it steps <i>into</i>, and tiles
    /// outside the grid never block (that is open space).
    /// </summary>
    private static (double X, double Y) March(
        ShipGrid grid, bool[] blocker, double ox, double oy, double dx, double dy, double radius)
    {
        int cx = (int)Math.Floor(ox), cy = (int)Math.Floor(oy);
        int stepX = Math.Sign(dx), stepY = Math.Sign(dy);
        double tMaxX = stepX != 0 ? (stepX > 0 ? cx + 1 - ox : ox - cx) / Math.Abs(dx) : double.PositiveInfinity;
        double tMaxY = stepY != 0 ? (stepY > 0 ? cy + 1 - oy : oy - cy) / Math.Abs(dy) : double.PositiveInfinity;
        double tDeltaX = stepX != 0 ? 1.0 / Math.Abs(dx) : double.PositiveInfinity;
        double tDeltaY = stepY != 0 ? 1.0 / Math.Abs(dy) : double.PositiveInfinity;

        while (true)
        {
            double t;
            if (tMaxX < tMaxY) { t = tMaxX; cx += stepX; tMaxX += tDeltaX; }
            else { t = tMaxY; cy += stepY; tMaxY += tDeltaY; }

            if (t >= radius) return (ox + dx * radius, oy + dy * radius);   // reached the rim, unobstructed
            if (grid.InBounds(cx, cy) && blocker[grid.Index(cx, cy)])
                return (ox + dx * t, oy + dy * t);   // exact entry point on the wall face
        }
    }
}
