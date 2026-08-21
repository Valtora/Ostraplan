namespace Ostraplan.Core;

/// <summary>Where the frame a strike is measured in came from, which decides the convergence point and so the
/// whole answer. Reported alongside every result because the two frames disagree.</summary>
public enum StrikeFrame
{
    /// <summary>The frame the ship will have once Ostraplan puts it in the game. Both write paths anchor a fresh
    /// ship at the export grid's own origin, which is the bounding box minus its one-tile pad, so the convergence
    /// point lands just outside the top-left corner of the hull.</summary>
    AsExported,

    /// <summary>The frame the ship already has, carried in from the save or template it was imported out of. This
    /// is the honest answer for "is the ship I am flying vulnerable", and it is usually somewhere else entirely:
    /// on the shipped fleet the convergence point sits inside the hull 85% of the time.</summary>
    AsImported,
}

/// <summary>
/// The fixed point every micrometeoroid ray passes through, in <b>document</b> tile coords, plus where that came
/// from. See <see cref="MicrometeoroidStrike"/> for why a strike has one at all.
/// </summary>
public sealed record StrikeAnchor(double DocX, double DocY, StrikeFrame Frame);

/// <summary>One part the ray reached, and what the strike did to it.</summary>
/// <param name="PlacementId">The document placement hit, so the canvas can tint the right part.</param>
/// <param name="FromDef">The form the part was in when the ray arrived.</param>
/// <param name="Absorbed">Damage this part took out of the pool.</param>
/// <param name="Broke">True when the pool filled and the part changed form.</param>
/// <param name="ToDef">The form it broke into, or null when it broke into nothing the game names — which is what
/// destroyed means.</param>
/// <param name="Distance">Distance along the ray to the part's collider, for ordering and for drawing.</param>
public sealed record StrikeHit(
    Guid PlacementId, string FromDef, double Absorbed, bool Broke, string? ToDef, double Distance);

/// <summary>The result of one strike: the path it took and what it cost the ship.</summary>
public sealed record StrikeResult(
    double SpeedMs,
    double Multiplier,
    double Pool,
    double PoolRemaining,
    (double X, double Y) StartDoc,
    (double X, double Y) EndDoc,
    IReadOnlyList<StrikeHit> Hits)
{
    /// <summary>True when the path crossed nothing able to absorb it.</summary>
    public bool Missed => Hits.Count == 0;

    /// <summary>Damage actually delivered into the ship.</summary>
    public double Delivered => Pool - PoolRemaining;
}

/// <summary>
/// A micrometeoroid strike against a design, ported from <c>StarSystem.SpawnMicroMeteoroid</c> →
/// <c>DamageSystem.DamageRayRandom</c> → <c>DamageRay</c> (§26 of docs/GAME-INTERNALS.md, verified 1.0.0.11).
///
/// <para><b>This is not the weapon solver.</b> The game runs two unrelated damage systems and a micrometeoroid
/// takes the physics one: a Unity raycast against per-item colliders, ordered by distance, where each part absorbs
/// only its <b>current form's</b> remaining pool before mode-switching and the ray moves on. So a strike advances a
/// part exactly one break stage. See <c>WeaponImpact</c> for the grid-based half, which reads the whole chain.</para>
///
/// <para><b>The collider is the sprite rectangle</b>, not the socket footprint, because every item is one prefab
/// whose unit BoxCollider is scaled by the sprite (see <see cref="SpriteExtent"/>). And a raycast returns one hit
/// per collider, so a multi-tile part absorbs once however many of its tiles the ray crosses.</para>
///
/// <para><b>Every ray passes through one fixed point.</b> The game aims at world origin rather than at the ship
/// (<c>-vStart.normalized</c> normalises the start position itself), so the origin is not a free parameter: the
/// angle is. That point is wherever the ship's <c>vShipPos</c> anchor puts it, which is why a strike needs a
/// <see cref="StrikeAnchor"/> and why the answer differs between the ship as imported and as exported.</para>
/// </summary>
public static class MicrometeoroidStrike
{
    /// <summary>The game's <c>AModeMicrometeoroid</c> environmental damage. Only this pool damages structure; the
    /// blunt and cut figures alongside it exist to wound crew, which a design has none of.</summary>
    public const double DamageEnv = 55.0;

    /// <summary><c>CrewSim.ATC_SPEED_LIMIT</c> in m/s — the unit the strength multiplier is measured in. The game
    /// stores it as 5.013440329548757e-9 AU/s, which against its own AU is exactly this.</summary>
    public const double AtcSpeedLimit = 750.0;

    /// <summary>The floor under the multiplier. A ship exactly matching the body's velocity still takes
    /// half-strength strikes.</summary>
    public const double MultiplierFloor = 0.5;

    /// <summary>The fastest impact velocity any authored micrometeoroid shell can produce, to the nearest 100 m/s:
    /// circular orbit at Earth's stratosphere shell, the innermost with a non-zero chance. Used to clamp the
    /// speed input, since nothing in the game reaches beyond it.</summary>
    public const double MaxClosingSpeedMs = 7700.0;

    /// <summary>The slowest impact velocity worth asking about, <c>750 × 0.5</c>. The multiplier floors at
    /// <see cref="MultiplierFloor"/>, so every speed below this produces exactly the same strike and a slider
    /// that ran to zero would be offering a range the game cannot tell apart.</summary>
    public const double MinClosingSpeedMs = AtcSpeedLimit * MultiplierFloor;

    /// <summary>The impact velocity a micrometeoroid arrives at when the <b>tension beat</b> spawns it rather than
    /// an atmosphere shell: <c>BeatManager.Micrometeoroid</c> passes <c>fMult: 1f</c> outright, which is the ATC
    /// limit exactly. That path fires anywhere in the system, so it is the one speed a ship is exposed to no
    /// matter where it is, and the sensible default to open on.</summary>
    public const double TensionBeatSpeedMs = AtcSpeedLimit;

    /// <summary>The strength multiplier for a closing speed: <c>max(v / 750, 0.5)</c>, the game's
    /// <c>Mathf.Max(|v_body − v_ship| / ATC_SPEED_LIMIT, 0.5f)</c>.</summary>
    public static double MultiplierFor(double closingSpeedMs) =>
        Math.Max(closingSpeedMs / AtcSpeedLimit, MultiplierFloor);

    /// <summary>
    /// The worst-case environmental pool for a closing speed: <c>55 × roll × multiplier</c> with the roll pinned
    /// to 1. In game the roll is <c>MathUtils.Rand(0, 1, Mid)</c> and a strike's strength is random even at a
    /// fixed speed, so this is the ceiling rather than the expectation — which is the figure worth designing
    /// against, and the one the request asked for.
    /// </summary>
    public static double WorstCasePool(double closingSpeedMs) => DamageEnv * MultiplierFor(closingSpeedMs);

    /// <summary>
    /// Where this design's rays converge, in document tile coords.
    ///
    /// <para>Prefer <paramref name="importedShipPos"/> when the design came in from a save or template: that is the
    /// anchor the ship already has and the answer for the ship the player is actually flying. With none, fall back
    /// to the frame Ostraplan will write, which both export paths anchor at the grid origin — the bounding box
    /// minus its one-tile pad — putting the convergence point just outside the top-left corner.</para>
    /// </summary>
    public static StrikeAnchor AnchorFor(ShipDocument doc, (double DocX, double DocY)? importedShipPos = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        // The document's own import provenance is the default source, so no caller has to remember to pass it and
        // an imported ship cannot silently be measured in the wrong frame.
        if ((importedShipPos ?? doc.SourceShipPos) is { } a)
            return new StrikeAnchor(a.Item1, a.Item2, StrikeFrame.AsImported);
        var b = doc.Bounds();
        return b is null
            ? new StrikeAnchor(0, 0, StrikeFrame.AsExported)
            : new StrikeAnchor(b.Value.MinX - 1, b.Value.MinY - 1, StrikeFrame.AsExported);
    }

    /// <summary>
    /// Fire one strike along the path <paramref name="startDoc"/> → <paramref name="endDoc"/> and resolve it
    /// against <paramref name="state"/>, which is mutated: a strike leaves damage behind, and drawing the same
    /// line twice is how a wall goes from whole to damaged to gone. Pass a fresh <see cref="DamageState"/> for a
    /// single-strike worst case.
    ///
    /// <para><b>The path is whatever you draw.</b> The game itself can only produce paths through one fixed point
    /// (see <see cref="AnchorFor"/> and <see cref="GameRayFor"/>), but a planner that could only show those could
    /// not answer "what if it came in here instead", which is the question a designer actually has. What the ray
    /// does once it is drawn is the game's own arithmetic, exactly.</para>
    /// </summary>
    /// <param name="doc">The design. Never modified: accumulated damage lives in <paramref name="state"/>, because
    /// wear is not part of a design (§12) and must not reach the .oplan.</param>
    /// <param name="closingSpeedMs">Speed relative to the body, clamped to <see cref="MaxClosingSpeedMs"/>.</param>
    public static StrikeResult Fire(
        ShipDocument doc, (double X, double Y) startDoc, (double X, double Y) endDoc,
        double closingSpeedMs, DamageState state)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(state);

        var speed = Math.Clamp(closingSpeedMs, 0, MaxClosingSpeedMs);
        var mult = MultiplierFor(speed);
        var pool = WorstCasePool(speed);

        var geom = RayThrough(startDoc, endDoc);
        var hits = new List<StrikeHit>();
        var remaining = pool;

        foreach (var (placement, distance) in Along(doc, geom))
        {
            if (remaining <= 0) break;
            // A destroyed part is not on the tile any more, so it neither absorbs nor shields. Without this it
            // would soak its last form's pool again on every subsequent strike and never stop.
            if (state.IsDestroyed(placement)) continue;
            var from = state.CurrentDef(placement);
            // DmgLeft on the CURRENT form only. A part with no pool at all absorbs nothing and the ray goes on
            // through it, which is the game's `DmgLeft <= 0` branch rather than a stop.
            var left = doc.Catalog.Health(from) - state.DamageOn(placement);
            if (left <= 0) continue;

            var taken = Math.Min(remaining, left);
            remaining -= taken;
            var (broke, to) = state.Apply(placement, from, taken, doc.Catalog);
            hits.Add(new StrikeHit(placement.Id, from, taken, broke, to, distance));
        }

        return new StrikeResult(speed, mult, pool, remaining, startDoc, endDoc, hits);
    }

    /// <summary>
    /// The path the <b>game</b> would fire at a given angle — reference only, and no longer how a strike is aimed.
    ///
    /// <para>Kept because it is the one thing a free-drawn line cannot tell you: where real micrometeoroids
    /// actually go on this hull. Every one of them runs through <see cref="AnchorFor"/>, so a part no such ray
    /// reaches is one the game will never chip, however badly a hand-drawn path treats it.</para>
    /// </summary>
    public static ((double X, double Y) StartDoc, (double X, double Y) EndDoc) GameRayFor(
        ShipDocument doc, StrikeAnchor anchor, double angleDeg)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(anchor);
        var g = Geometry(doc, anchor, angleDeg);
        return (g.StartDoc, g.EndDoc);
    }

    /// <summary>A ray straight from one document point to another: the path the user drew.</summary>
    private static Ray RayThrough((double X, double Y) startDoc, (double X, double Y) endDoc)
    {
        double dx = endDoc.X - startDoc.X, dy = endDoc.Y - startDoc.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        return len <= 1e-9
            ? new Ray(startDoc.X, startDoc.Y, 0, 0, 0, startDoc, endDoc)
            : new Ray(startDoc.X, startDoc.Y, dx / len, dy / len, len, startDoc, endDoc);
    }

    // ---- geometry ----

    /// <summary>A path across the plan in <b>document</b> tile coords. Everything downstream works in this one
    /// frame; the world/anchor frame exists only inside <see cref="Geometry"/>, which is the only place the game's
    /// own aiming is reproduced.</summary>
    private readonly record struct Ray(
        double StartX, double StartY, double DirX, double DirY, double Length,
        (double X, double Y) StartDoc, (double X, double Y) EndDoc);

    /// <summary>
    /// The ray, reproducing <c>DamageRayRandom</c>:
    /// <code>
    /// half   = (nCols/2, −nRows/2);  r = |half|
    /// vStart = vShipPos + half + AngleAxis(θ) · up · r
    /// dir    = −normalize(vStart);   length = 2r
    /// </code>
    /// World coords are the anchor-relative frame: <c>world = (docX − anchorX, −(docY − anchorY))</c>, so world
    /// origin IS the anchor and normalising the start position aims at it, exactly as the game does.
    /// </summary>
    private static Ray Geometry(ShipDocument doc, StrikeAnchor anchor, double angleDeg)
    {
        var (cx, cy, r) = CentreAndRadius(doc, anchor);

        // Quaternion.AngleAxis(θ, forward) * Vector3.up is (−sin θ, cos θ) — a CCW turn of +y about +z.
        var rad = angleDeg * Math.PI / 180.0;
        var sx = cx - Math.Sin(rad) * r;
        var sy = cy + Math.Cos(rad) * r;

        var mag = Math.Sqrt(sx * sx + sy * sy);
        // One angle per ship starts the ray exactly ON the anchor, and there the game fires nothing: Unity's
        // Vector3.normalized returns zero below its epsilon rather than a unit vector, and RaycastAll along a zero
        // direction hits nothing at all. Reproduced rather than papered over — inventing a direction here would
        // manufacture a strike the game never delivers. It is reachable: for a square ship it is exactly 45°.
        if (mag <= 1e-9) return RayThrough(ToDoc(sx, sy, anchor), ToDoc(sx, sy, anchor));

        var len = 2 * r;
        var (dx, dy) = (-sx / mag, -sy / mag);
        return RayThrough(ToDoc(sx, sy, anchor), ToDoc(sx + dx * len, sy + dy * len, anchor));
    }

    /// <summary>The ship centre in world coords and the half-diagonal the ray's start swings on — the game's
    /// <c>vShipPos + (nCols/2, −nRows/2)</c> and <c>|(nCols/2, −nRows/2)|</c>. Shared by the forward geometry and
    /// by <see cref="AngleFrom"/>, so the drag and the strike can never disagree about where the circle is.
    /// <para>The grid the game builds is the bounding box plus a one-tile margin (§18) whatever frame the items are
    /// expressed in, so the extent comes from the content and not from the anchor.</para></summary>
    private static (double CX, double CY, double R) CentreAndRadius(ShipDocument doc, StrikeAnchor anchor)
    {
        var b = doc.Bounds();
        var (nCols, nRows) = b is null ? (1, 1) : (b.Value.MaxX - b.Value.MinX + 3, b.Value.MaxY - b.Value.MinY + 3);
        var (ox, oy) = ToWorld(b is null ? 0 : b.Value.MinX - 1, b is null ? 0 : b.Value.MinY - 1, anchor);
        double halfX = nCols / 2.0, halfY = -(nRows / 2.0);
        return (ox + halfX, oy + halfY, Math.Sqrt(halfX * halfX + halfY * halfY));
    }

    /// <summary>Document tile coords → the anchor-relative world frame (+y up, like the game).</summary>
    private static (double X, double Y) ToWorld(double docX, double docY, StrikeAnchor a) =>
        (docX - a.DocX, -(docY - a.DocY));

    /// <summary>The inverse, for handing geometry back to the canvas.</summary>
    private static (double X, double Y) ToDoc(double worldX, double worldY, StrikeAnchor a) =>
        (worldX + a.DocX, a.DocY - worldY);

    /// <summary>
    /// Every placement whose collider the ray crosses, nearest first — the ordered <c>Physics.RaycastAll</c> hit
    /// list. One entry per part however many tiles it spans, because a raycast returns one hit per collider.
    /// </summary>
    private static List<(Placement Part, double Distance)> Along(ShipDocument doc, Ray ray)
    {
        var hits = new List<(Placement, double)>();
        if (ray.Length <= 0) return hits;   // a path of no length hits nothing
        foreach (var p in doc.Placements)
        {
            if (doc.Part(p) is not { } def) continue;

            // Collider centre is the FOOTPRINT centre (the item's own transform position), and its extent is the
            // SPRITE, which for the big tanks is much the smaller of the two.
            var (fw, fh) = GridMath.Size(def.Item.Width, def.Item.Height, p.Rot);
            var centreX = p.X + fw / 2.0 - 0.5;
            var centreY = p.Y + fh / 2.0 - 0.5;
            var (sw, sh) = SpriteExtent.Tiles(def);
            // The collider turns with the transform, and every rotation is a right angle, so a turned box is just
            // the swapped extents.
            if (p.Rot is 90 or 270) (sw, sh) = (sh, sw);

            if (SlabHit(ray, centreX, centreY, sw / 2.0, sh / 2.0) is { } d) hits.Add((p, d));
        }
        hits.Sort((a, b) => a.Item2.CompareTo(b.Item2));
        return hits;
    }

    /// <summary>Ray against an axis-aligned box, returning the entry distance along the ray or null when it does
    /// not cross within the ray's length. The standard slab test; a ray starting inside enters at 0.</summary>
    private static double? SlabHit(Ray ray, double cx, double cy, double hx, double hy)
    {
        double near = 0, far = ray.Length;
        if (!Slab(ray.StartX, ray.DirX, cx - hx, cx + hx, ref near, ref far)) return null;
        if (!Slab(ray.StartY, ray.DirY, cy - hy, cy + hy, ref near, ref far)) return null;
        return near <= far ? near : null;
    }

    private static bool Slab(double start, double dir, double lo, double hi, ref double near, ref double far)
    {
        if (Math.Abs(dir) < 1e-12) return start >= lo && start <= hi;   // parallel: inside the slab or never
        var t0 = (lo - start) / dir;
        var t1 = (hi - start) / dir;
        if (t0 > t1) (t0, t1) = (t1, t0);
        near = Math.Max(near, t0);
        far = Math.Min(far, t1);
        return near <= far;
    }

    private static double Norm360(double deg)
    {
        var d = deg % 360.0;
        return d < 0 ? d + 360.0 : d;
    }
}
