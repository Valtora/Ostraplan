namespace Ostraplan.Core;

/// <summary>
/// One light-occluder block in world space (game coords, +y up, tiles): centre, half-extents, and whether it is a
/// wall for the light engine (wall blocks get a lit skirt of depth <c>max(rx, ry)</c>; non-wall blocks cast shadow
/// but are fully lit themselves when touched). Glass boxes are filtered out before this point — they never occlude.
/// Built from a placed part's <see cref="ShadowBox"/>es rotated with the part.
/// </summary>
public sealed record LightBlock(float X, float Y, float Rx, float Ry, bool IsWall)
{
    /// <summary>The block's four edges, exactly <c>Block.GetSegments</c>: top L→R, right T→B, bottom R→L, left B→T.</summary>
    public IEnumerable<((float X, float Y) A, (float X, float Y) B)> Segments()
    {
        yield return ((X - Rx, Y + Ry), (X + Rx, Y + Ry));
        yield return ((X + Rx, Y + Ry), (X + Rx, Y - Ry));
        yield return ((X + Rx, Y - Ry), (X - Rx, Y - Ry));
        yield return ((X - Rx, Y - Ry), (X - Rx, Y + Ry));
    }
}

/// <summary>
/// The lit region one light covers, in light-local game coords (tiles, +y up, origin at the light centre):
/// <see cref="Fan"/> is the triangle-fan boundary (each entry is one fan triangle centre→A→B) and
/// <see cref="Quads"/> are the fully-lit rectangles of touched non-wall blocks (each 4 corners, two triangles).
/// Produced by <see cref="VisibilityMesh.Build"/>, an exact port of the game's <c>Visibility.LateUpdate</c> mesh
/// build (0.34.x decompile): angular occluder merge against the shadow blocks, a 64-segment rim at radius−0.5,
/// wall skirts extruded by wall thickness (so wall faces are lit), a minimum lit ring of 0.5, and lit-block quads.
/// </summary>
public sealed record LightRegion(
    IReadOnlyList<((float X, float Y) A, (float X, float Y) B)> Fan,
    IReadOnlyList<((float X, float Y) P1, (float X, float Y) P2, (float X, float Y) P3, (float X, float Y) P4)> Quads);

/// <summary>
/// Exact port of the game's shadow-mesh geometry (<c>Visibility</c> + <c>Occluder</c>, Assembly-CSharp): given a
/// light centre, radius and the world's occluder blocks, produce the region the light illuminates. All arithmetic
/// is <c>float</c> to match the game. Coordinates are game-style (+y up); callers convert at the boundary.
/// </summary>
public static class VisibilityMesh
{
    private sealed class Occ : IComparable<Occ>
    {
        public float AngleLeft, AngleRight, RadiusLeft, RadiusRight;
        public LightBlock? Block;
        public float PxL, PyL, PxR, PyR;

        public void UpdatePositionLeft() { PxL = MathF.Cos(AngleLeft) * RadiusLeft; PyL = MathF.Sin(AngleLeft) * RadiusLeft; }
        public void UpdatePositionRight() { PxR = MathF.Cos(AngleRight) * RadiusRight; PyR = MathF.Sin(AngleRight) * RadiusRight; }

        public int CompareTo(Occ? s)
        {
            if (s is null) return 1;
            if (AngleRight != s.AngleRight) return AngleRight < s.AngleRight ? -1 : 1;
            return 0;
        }
    }

    private const float StartAngle = -MathF.PI;
    private const float FinishAngle = MathF.PI;

    /// <summary>
    /// Build the lit region for a light at <paramref name="cx"/>,<paramref name="cy"/> (game coords, tiles) with
    /// <paramref name="radius"/>, against <paramref name="blocks"/> (glass already removed). Mirrors
    /// <c>Visibility.LateUpdate</c>: outer pass (rim at radius−0.5 + block occluders → skirt), inner pass
    /// (0.5 ring merged with the extruded skirt, keeping the farther boundary), then the fan + lit-block quads.
    /// </summary>
    public static LightRegion Build(float cx, float cy, float radius, IReadOnlyList<LightBlock> blocks)
    {
        // --- outer pass: sfSign = +1 (keep the NEARER boundary) ---
        var outer = new List<Occ>(256);
        AddRing(outer, radius - 0.5f);
        var touched = new List<LightBlock>();
        AddBlocks(outer, blocks, cx, cy, radius);

        // EmitOccluders(useSkirt: true): collect the skirt (one entry per boundary segment) + record touched blocks
        var skirt = new List<(float X, float Y)>(outer.Count * 2);
        var skirtW = new List<float>(outer.Count);
        var seen = new HashSet<LightBlock>();
        foreach (var o in outer)
        {
            skirt.Add((o.PxL, o.PyL));
            skirt.Add((o.PxR, o.PyR));
            skirtW.Add(Thickness(o.Block));
            if (o.Block is { } blk && seen.Add(blk) && !blk.IsWall) touched.Add(blk);
        }

        // --- inner pass: sfSign = -1 (keep the FARTHER boundary) ---
        var inner = new List<Occ>(256);
        AddRing(inner, 0.5f);
        EmitSkirt(inner, skirt, skirtW);

        // EmitOccluders(useSkirt: false): the fan triangles come straight from the merged inner boundary
        var fan = new List<((float, float), (float, float))>(inner.Count);
        foreach (var o in inner)
            fan.Add(((o.PxL, o.PyL), (o.PxR, o.PyR)));

        // IlluminateBlock quads for touched non-wall blocks (TL, BL, BR, TR relative to the light centre)
        var quads = new List<((float, float), (float, float), (float, float), (float, float))>(touched.Count);
        foreach (var b in touched)
        {
            float nx = b.X - cx, ny = b.Y - cy;
            quads.Add(((nx - b.Rx, ny + b.Ry), (nx - b.Rx, ny - b.Ry), (nx + b.Rx, ny - b.Ry), (nx + b.Rx, ny + b.Ry)));
        }

        return new LightRegion(fan, quads);
    }

    /// <summary>Helper.HelperGetThicknessFromBlock: null → 0.5 (the rim ring), wall → max(rx, ry), other → 0.</summary>
    private static float Thickness(LightBlock? b) =>
        b is null ? 0.5f : b.IsWall ? MathF.Max(b.Rx, b.Ry) : 0f;

    /// <summary>Visibility.AddOccludersAtRadius: 64 equal segments spanning [−π, π] at a fixed radius.</summary>
    private static void AddRing(List<Occ> occluders, float radius)
    {
        const int n = 64;
        var angleLeft = StartAngle;
        for (var i = 1; i <= n; i++)
        {
            var a = i * (FinishAngle - StartAngle) / n + StartAngle;
            var o = new Occ { AngleLeft = angleLeft, AngleRight = a, RadiusLeft = radius, RadiusRight = radius };
            o.UpdatePositionLeft();
            o.UpdatePositionRight();
            occluders.Add(o);
            angleLeft = a;
        }
    }

    /// <summary>Visibility.AddOccludersFromCrewSimBlocks: range-cull, then merge each block edge (glass is
    /// filtered before this point; the game skips <c>bIsGlass</c> here).</summary>
    private static void AddBlocks(List<Occ> occluders, IReadOnlyList<LightBlock> blocks, float cx, float cy, float radius)
    {
        foreach (var b in blocks)
        {
            var dx = b.X - cx;
            var dy = b.Y - cy;
            var reach = MathF.Max(b.Rx, b.Ry) * 2f;
            if (dx * dx + dy * dy > (radius + reach) * (radius + reach)) continue;
            foreach (var (a, p) in b.Segments())
                AddOccluder(occluders, sfSign: 1f, a.X - cx, a.Y - cy, p.X - cx, p.Y - cy, b);
        }
    }

    /// <summary>Visibility.AddOccluder: skip degenerate/back-facing edges, wrap across ±π.</summary>
    private static void AddOccluder(List<Occ> occluders, float sfSign, float x1, float y1, float x2, float y2, LightBlock? block)
    {
        var a1 = MathF.Atan2(y1, x1);
        var a2 = MathF.Atan2(y2, x2);
        if (a1 == a2) return;
        if (a2 < a1) a2 += MathF.PI * 2f;
        if (a1 + MathF.PI < a2) return;   // back-facing (subtends more than π after normalisation)
        var d1 = MathF.Sqrt(x1 * x1 + y1 * y1);
        var d2 = MathF.Sqrt(x2 * x2 + y2 * y2);
        MergeOccluder(occluders, sfSign, x1, y1, a1, d1, x2, y2, a2, d2, block);
        if (a2 >= MathF.PI)
            MergeOccluder(occluders, sfSign, x1, y1, a1 - MathF.PI * 2f, d1, x2, y2, a2 - MathF.PI * 2f, d2, block);
    }

    /// <summary>Visibility.RadiusAtIntercept: distance along the ray at <paramref name="cosI"/>/<paramref name="sinI"/>
    /// where segment a→b crosses it; parallel → the mean of the endpoint distances.</summary>
    private static float RadiusAtIntercept(float ax, float ay, float d1, float bx, float by, float d2, float cosI, float sinI)
    {
        float s = -1f, t = -1f;
        if (SolveST0(ref s, ref t, ax, ay, bx, by, cosI, sinI) && t > 0f) return t;
        return (d1 + d2) / 2f;
    }

    private static float RadiusAtIntercept(float ax, float ay, float d1, float bx, float by, float d2, float angle) =>
        RadiusAtIntercept(ax, ay, d1, bx, by, d2, MathF.Cos(angle), MathF.Sin(angle));

    /// <summary>Visibility.SplitOccluder: cut occluders[key] at <paramref name="angle"/>, the left half keeping the
    /// original left edge and taking (<paramref name="dist"/>, angle) as its new right edge.</summary>
    private static bool SplitOccluder(List<Occ> occluders, int key, float dist, float angle)
    {
        var o = occluders[key];
        if (o.AngleLeft >= angle || o.AngleRight <= angle) return false;
        var left = new Occ
        {
            AngleLeft = o.AngleLeft,
            AngleRight = angle,
            RadiusLeft = o.RadiusLeft,
            RadiusRight = dist,
            Block = o.Block,
            PxL = o.PxL,
            PyL = o.PyL,
        };
        o.RadiusLeft = dist;
        o.AngleLeft = angle;
        o.UpdatePositionLeft();
        left.PxR = o.PxL;
        left.PyR = o.PyL;
        occluders.Insert(key, left);
        return true;
    }

    private static bool TrySplitOccluder(List<Occ> occluders, float sfSign, int key, float splitRadius, float angle)
    {
        var o = occluders[key];
        var cosI = MathF.Cos(angle);
        var sinI = MathF.Sin(angle);
        var r = RadiusAtIntercept(o.PxL, o.PyL, o.RadiusLeft, o.PxR, o.PyR, o.RadiusRight, cosI, sinI);
        if (r * sfSign <= splitRadius * sfSign) return false;
        return SplitOccluder(occluders, key, r, angle);
    }

    /// <summary>Visibility.MergeOccluder: clamp the new segment into [−π, π], split the existing boundary at its
    /// ends and at true intersections, then overwrite every span the new segment beats (nearer for the outer pass,
    /// farther for the inner pass), merging same-block neighbours.</summary>
    private static void MergeOccluder(
        List<Occ> occluders, float sfSign,
        float p1x, float p1y, float angle1, float dist1,
        float p2x, float p2y, float angle2, float dist2, LightBlock? block)
    {
        if (angle1 < StartAngle)
        {
            dist1 = RadiusAtIntercept(p1x, p1y, dist1, p2x, p2y, dist2, MathF.Cos(StartAngle), MathF.Sin(StartAngle));
            angle1 = StartAngle;
            p1x = MathF.Cos(angle1) * dist1;
            p1y = MathF.Sin(angle1) * dist1;
        }
        if (angle2 > FinishAngle)
        {
            dist2 = RadiusAtIntercept(p1x, p1y, dist1, p2x, p2y, dist2, MathF.Cos(FinishAngle), MathF.Sin(FinishAngle));
            angle2 = FinishAngle;
            p2x = MathF.Cos(angle2) * dist2;
            p2y = MathF.Sin(angle2) * dist2;
        }
        if (angle2 <= angle1) return;

        var probe = new Occ { AngleLeft = angle1, AngleRight = angle1 };
        var lo = occluders.BinarySearch(probe);
        if (lo < 0)
        {
            lo = ~lo;
            TrySplitOccluder(occluders, sfSign, lo, dist1, angle1);
        }
        probe.AngleLeft = probe.AngleRight = angle2;
        var hi = occluders.BinarySearch(probe);
        if (hi < 0)
        {
            hi = ~hi;
            if (TrySplitOccluder(occluders, sfSign, hi, dist2, angle2)) hi++;
        }

        // split the existing boundary wherever the new segment truly crosses it
        for (var i = hi; i >= lo; i--)
        {
            float s = -1f, t = -1f;
            var o = occluders[i];
            if (SolveST(ref s, ref t, o.PxL, o.PyL, o.PxR, o.PyR, p1x, p1y, p2x, p2y)
                && 0f < s && s < 1f && 0f < t && t < 1f)
            {
                var ix = p1x + t * (p2x - p1x);
                var iy = p1y + t * (p2y - p1y);
                var ia = MathF.Atan2(iy, ix);
                if (SplitOccluder(occluders, i, MathF.Sqrt(ix * ix + iy * iy), ia)) hi++;
            }
        }

        // overwrite spans the new segment beats; merge same-block neighbours as they accumulate
        var run = 0;
        for (var i = hi; i >= lo; i--)
        {
            var o = occluders[i];
            var mid = (o.AngleLeft + o.AngleRight) * 0.5f;
            if (mid < angle1 || angle2 < mid) continue;
            var rNew = RadiusAtIntercept(p1x, p1y, dist1, p2x, p2y, dist2, mid);
            var rOld = RadiusAtIntercept(o.PxL, o.PyL, o.RadiusLeft, o.PxR, o.PyR, o.RadiusRight, mid);
            if (rNew * sfSign < rOld * sfSign)
            {
                o.RadiusLeft = RadiusAtIntercept(p1x, p1y, dist1, p2x, p2y, dist2, o.AngleLeft);
                o.RadiusRight = RadiusAtIntercept(p1x, p1y, dist1, p2x, p2y, dist2, o.AngleRight);
                o.Block = block;
                o.UpdatePositionLeft();
                o.UpdatePositionRight();
                run++;
            }
            else run = 0;
            if (run >= 2 && o.Block == occluders[i + 1].Block)
            {
                o.AngleRight = occluders[i + 1].AngleRight;
                o.RadiusRight = occluders[i + 1].RadiusRight;
                o.PxR = occluders[i + 1].PxR;
                o.PyR = occluders[i + 1].PyR;
                occluders.RemoveAt(i + 1);
            }
        }
    }

    /// <summary>
    /// Visibility.EmitSkirt: turn the outer-pass boundary into the final lit outline — merge collinear runs, wrap
    /// the seam, extrude each face outward by its thickness (0.5 for the rim, wall thickness for walls, 0 for
    /// other blocks), mitre the joins, and feed the extruded edges into the inner pass as occluders (which keeps
    /// the FARTHER boundary, so the lit region reaches into wall faces and out to the full radius).
    /// </summary>
    private static void EmitSkirt(List<Occ> inner, List<(float X, float Y)> skirt, List<float> skirtW)
    {
        if (skirt.Count < 2) return;

        // collapse collinear neighbours that share an endpoint (tolerance 0.0001 on the perpendicular)
        for (var i = skirt.Count - 4; i >= 0; i -= 2)
        {
            if (skirt[i + 1] != skirt[i + 2]) continue;
            var ex = skirt[i + 3].X - skirt[i].X;
            var ey = skirt[i + 3].Y - skirt[i].Y;
            var mx = skirt[i + 1].X - skirt[i].X;
            var my = skirt[i + 1].Y - skirt[i].Y;
            var u = (mx * ex + my * ey) / (ex * ex + ey * ey);
            var px = ex * u - mx;
            var py = ey * u - my;
            if (px * px + py * py < 0.0001f)
            {
                skirt.RemoveAt(i + 2);
                skirt.RemoveAt(i + 1);
                skirtW.RemoveAt(i / 2);
            }
        }

        // wrap the first three segments to close the seam
        for (var i = 0; i < 6; i += 2)
        {
            skirt.Add(skirt[i]);
            skirt.Add(skirt[i + 1]);
            skirtW.Add(skirtW[i / 2]);
        }

        // snap near-coincident joins
        for (var j = 0; j + 2 < skirt.Count; j += 2)
        {
            var dx = skirt[j + 1].X - skirt[j + 2].X;
            var dy = skirt[j + 1].Y - skirt[j + 2].Y;
            if (MathF.Sqrt(dx * dx + dy * dy) < 0.01f) skirt[j + 1] = skirt[j + 2];
        }

        // extrude each segment outward along its normal by its thickness
        var ext = new List<(float X, float Y)>(skirt.Count);
        for (var k = 0; k < skirt.Count; k += 2)
        {
            var (ax, ay) = skirt[k];
            var (bx, by) = skirt[k + 1];
            var dx = ax - bx;
            var dy = ay - by;
            var len = MathF.Sqrt(dx * dx + dy * dy);
            if (len > 0) { dx /= len; dy /= len; }
            var w = skirtW[k / 2];
            var nx = -dy * w;
            var ny = dx * w;
            ext.Add((ax + nx, ay + ny));
            ext.Add((bx + nx, by + ny));
        }

        // mitre the joins: shared endpoints meet at the line intersection; open joins clamp toward the corner ray
        for (var l = 0; l + 2 < skirt.Count; l += 2)
        {
            if (skirt[l + 1] == skirt[l + 2])
            {
                float s = -1f, t = -1f;
                if (SolveST(ref s, ref t, ext[l].X, ext[l].Y, ext[l + 1].X, ext[l + 1].Y,
                        ext[l + 2].X, ext[l + 2].Y, ext[l + 3].X, ext[l + 3].Y))
                {
                    var vx = ext[l].X + s * (ext[l + 1].X - ext[l].X);
                    var vy = ext[l].Y + s * (ext[l + 1].Y - ext[l].Y);
                    ext[l + 1] = (vx, vy);
                    ext[l + 2] = (vx, vy);
                }
                continue;
            }
            float s2 = -1f, t2 = -1f;
            if (SolveST0(ref s2, ref t2, ext[l].X, ext[l].Y, ext[l + 1].X, ext[l + 1].Y, skirt[l + 1].X, skirt[l + 1].Y))
            {
                var dx = ext[l + 1].X - ext[l].X;
                var dy = ext[l + 1].Y - ext[l].Y;
                var d = MathF.Sqrt(dx * dx + dy * dy);
                var cap = 1f + 0.5f / d;
                if (s2 > cap) s2 = cap;
                ext[l + 1] = (ext[l].X + s2 * dx, ext[l].Y + s2 * dy);
            }
            else ext[l + 1] = skirt[l + 1];
            if (SolveST0(ref s2, ref t2, ext[l + 3].X, ext[l + 3].Y, ext[l + 2].X, ext[l + 2].Y, skirt[l + 2].X, skirt[l + 2].Y))
            {
                var dx = ext[l + 2].X - ext[l + 3].X;
                var dy = ext[l + 2].Y - ext[l + 3].Y;
                var d = MathF.Sqrt(dx * dx + dy * dy);
                var cap = 1f + 0.5f / d;
                if (s2 > cap) s2 = cap;
                ext[l + 2] = (ext[l + 3].X + s2 * dx, ext[l + 3].Y + s2 * dy);
            }
            else ext[l + 2] = skirt[l + 2];
        }

        // feed the extruded outline into the inner pass (block = null; sfSign = -1 keeps the farther boundary)
        for (var m = 4; m + 2 < skirt.Count; m += 2)
        {
            AddOccluder(inner, sfSign: -1f, ext[m - 1].X, ext[m - 1].Y, ext[m].X, ext[m].Y, null);
            AddOccluder(inner, sfSign: -1f, ext[m].X, ext[m].Y, ext[m + 1].X, ext[m + 1].Y, null);
        }
    }

    /// <summary>Visibility.SolveST: parametric intersection of segment S (fromS→toS) and segment T (fromT→toT).</summary>
    public static bool SolveST(ref float s, ref float t,
        float fsx, float fsy, float tsx, float tsy, float ftx, float fty, float ttx, float tty)
    {
        var dsx = tsx - fsx;
        var dsy = tsy - fsy;
        var dtx = ttx - ftx;
        var dty = tty - fty;
        var den = dsy * dtx - dsx * dty;
        if (den is > -1E-05f and < 1E-05f) return false;
        var ox = ftx - fsx;
        var oy = fty - fsy;
        s = (dtx * oy - dty * ox) / den;
        t = (dsx * oy - dsy * ox) / den;
        return true;
    }

    /// <summary>Visibility.SolveST0: intersection of segment origin→target with the ray from (0,0) along (dxt, dyt).
    /// On success <paramref name="t"/> is the distance along the ray, <paramref name="s"/> the segment parameter.</summary>
    public static bool SolveST0(ref float s, ref float t,
        float ox, float oy, float tx, float ty, float dxt, float dyt)
    {
        var dx = tx - ox;
        var dy = ty - oy;
        var den = dy * dxt - dx * dyt;
        if (den is > -1E-05f and < 1E-05f) return false;
        s = (dyt * ox - dxt * oy) / den;
        t = (dy * ox - dx * oy) / den;
        return true;
    }
}
