namespace Ostraplan.Core;

/// <summary>
/// One ray as the game aims it: through the centre of <see cref="Target"/>, coming in from
/// <see cref="AngleDeg"/>, from <see cref="StartDoc"/> to <see cref="EndDoc"/>. See
/// <see cref="MicrometeoroidStrike.GameRayFor"/>.
///
/// <para>Centre frame, like the rest of the solver (<see cref="TileFrame"/>): drawing this on the canvas takes a
/// <see cref="TileFrame.CentreToCorner"/> first, or the line lands half a tile off the one the game fires.</para>
/// </summary>
/// <param name="Target">The grid tile the ray passes through, in document tile coords. Any tile of the padded grid,
/// empty space included.</param>
/// <param name="AngleDeg">The game's roll, 0 to 360. At 0 the strike comes in from the top of the plan.</param>
/// <param name="StartDoc">Where the ray starts: one full grid diagonal out from the target, so always beyond the
/// grid.</param>
/// <param name="EndDoc">Where it stops: one full grid diagonal past the target, so always beyond the far side.</param>
public sealed record GameRay(
    (int X, int Y) Target, double AngleDeg, (double X, double Y) StartDoc, (double X, double Y) EndDoc);

/// <summary>One part the ray reached, and what the strike did to it.</summary>
/// <param name="PlacementId">The document placement hit, so the canvas can tint the right part.</param>
/// <param name="FromDef">The form the part was in when the ray arrived.</param>
/// <param name="Absorbed">Damage this part took out of the pool.</param>
/// <param name="Broke">True when the pool filled and the part changed form.</param>
/// <param name="ToDef">The form it broke into, or null when it broke into nothing the game names. Not the test
/// for destroyed: a break into loose debris is named and is still the end of the part (see
/// <see cref="DamageState.Apply"/>).</param>
/// <param name="Destroyed">True when the ship has lost the part outright, whether or not the wreckage has a name.</param>
/// <param name="Distance">Distance along the strike to the part's collider, measured from where the strike starts
/// beyond the edge of the grid. What the hits are ordered by.</param>
public sealed record StrikeHit(
    Guid PlacementId, string FromDef, double Absorbed, bool Broke, string? ToDef, bool Destroyed, double Distance);

/// <summary>The result of one strike. <see cref="StartDoc"/> and <see cref="EndDoc"/> are the line as it was
/// drawn; the strike itself crosses the whole grid along that line, so a hit before <see cref="StartDoc"/> or past
/// <see cref="EndDoc"/> is expected rather than a fault (see <see cref="MicrometeoroidStrike.Fire"/>).</summary>
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
/// <c>DamageSystem.DamageRayRandom</c> → <c>DamageRay</c> (§26 of docs/GAME-INTERNALS.md, the aim verified against
/// 1.1.0.1).
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
/// <para><b>Every ray crosses the whole ship.</b> Since 1.1.0.1 the game aims each strike through the centre of a
/// random tile of the grid on a random heading, starting and stopping one full grid diagonal either side of it, so
/// a strike always comes in from space and leaves into space. Any line across the hull is therefore a strike the
/// game can fire, which is why a drawn line is resolved the same way (<see cref="Fire"/>). Before 1.1.0.1 every ray
/// aimed at world origin instead, one fixed point per ship set by its <c>vShipPos</c> anchor; nothing here depends
/// on the anchor any more.</para>
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

    /// <summary>
    /// The fallback ceiling, used only when the game's own bodies cannot be read (no install, or a data layout
    /// this cannot parse). Prefer <see cref="FastestClosingSpeed"/>, which derives it from the authored shells.
    ///
    /// <para>7700 is the old hard-coded figure, kept as the fallback because it is safely above every stock
    /// shell rather than because it is one of them. It is <b>not</b> a speed the game produces.</para>
    /// </summary>
    public const double MaxClosingSpeedMs = 7700.0;

    /// <summary>
    /// The fastest strike the loaded game data can actually deliver, in m/s.
    ///
    /// <para><b>The game imposes no ceiling of its own.</b> <c>StarSystem.SpawnMicroMeteoroid</c> passes
    /// <c>fMult</c> straight through, and the only clamp anywhere on the path is the <see cref="MultiplierFloor"/>
    /// at the bottom. So the top comes from the data rather than from the code: a strike is only ever harder than
    /// the standard one at the <b>atmosphere</b> spawn site, that site only fires in a band declaring
    /// <c>fMicrometeoroidChance</c>, and the strength is the ship's speed relative to the body it is orbiting.
    /// Fastest such band, therefore fastest strike.</para>
    ///
    /// <para>Circular orbit is the bound taken, being the speed a ship actually holding one of those shells is
    /// doing. A ship on a hyperbolic pass through the same band would be quicker and the game would let it, but
    /// nothing in the data says how much quicker, so inventing a margin would put positions on the control that
    /// nothing in the game stands behind.</para>
    ///
    /// <para>Derived from the bodies rather than hard-coded, so a mod that gives Ceres a micrometeoroid band is
    /// picked up like any other data. Falls back to <see cref="StandardSpeedMs"/> when nothing declares one at
    /// all: with no atmosphere site reachable, the standard strike is the only strike there is.</para>
    /// </summary>
    public static double FastestClosingSpeed(IReadOnlyList<CelestialBody> bodies)
    {
        ArgumentNullException.ThrowIfNull(bodies);
        var fastest = StandardSpeedMs;
        foreach (var body in bodies)
            foreach (var band in body.Bands)
            {
                if (band.MicrometeoroidChance <= 0) continue;
                // v = sqrt(g·r) at the band's ceiling, using the game's own gravity so the figure agrees with
                // every other acceleration the tool reports.
                var altitude = band.CeilingKm - body.RadiusKm;
                var speed = Math.Sqrt(body.GravityAt(altitude) * band.CeilingKm * 1000.0);
                if (speed > fastest) fastest = speed;
            }
        return fastest;
    }

    /// <summary>
    /// The bodies whose atmosphere can produce a strike harder than the standard one, name-sorted.
    ///
    /// <para>The answer to "when does a micrometeoroid hit harder than 55", and it is a property of the loaded
    /// data rather than of the code. The system-wide spawn site passes <c>fMult: 1f</c> outright, so the only
    /// place strength varies at all is inside an atmosphere shell that declares an
    /// <see cref="AtmosphereBand.MicrometeoroidChance"/> — and in stock data that is Earth and nowhere else, which
    /// is why the whole range above the standard strike describes one planet. Derived rather than written down, so
    /// a mod that gives another body a micrometeoroid band is named here like any other.</para>
    ///
    /// <para>Empty when nothing declares one, which means the standard strike is the only strike there is.</para>
    /// </summary>
    public static IReadOnlyList<string> StrongStrikeBodies(IReadOnlyList<CelestialBody> bodies)
    {
        ArgumentNullException.ThrowIfNull(bodies);
        return [.. bodies
            .Where(b => b.Bands.Any(band => band.MicrometeoroidChance > 0))
            .Select(b => b.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The slowest impact velocity worth asking about, <c>750 × 0.5</c>. The multiplier floors at
    /// <see cref="MultiplierFloor"/>, so every speed below this produces exactly the same strike and a slider
    /// that ran to zero would be offering a range the game cannot tell apart.</summary>
    public const double MinClosingSpeedMs = AtcSpeedLimit * MultiplierFloor;

    /// <summary>
    /// The impact velocity of the strike a ship is exposed to <b>anywhere it is actually flown</b>, and so the
    /// default: <see cref="AtcSpeedLimit"/> exactly.
    ///
    /// <para>The game spawns micrometeoroids from two places and only one of them reaches normal play.
    /// <c>BeatManager.Micrometeoroid</c> fires anywhere in the system, at any ship not docked, not on a station,
    /// not running the torch and not inside an atmosphere, and it passes <c>fMult: 1f</c> outright. The other site
    /// rolls per atmosphere shell on <c>fMicrometeoroidChance</c>, and in stock data <b>only Earth's shells declare
    /// a non-zero one</b>, so it needs the ship to be inside Earth's atmosphere: the game is played out at Ceres,
    /// Venus and the Jovian stations, where it can never fire at all.</para>
    ///
    /// <para>Which is why this is not one option among several. In every place a player will be, a micrometeoroid
    /// arrives at exactly this speed and does exactly <see cref="StandardDamage"/>, and the rest of the range
    /// below describes Earth's atmosphere and nothing else.</para>
    /// </summary>
    public const double StandardSpeedMs = AtcSpeedLimit;

    /// <summary>What a strike does at <see cref="StandardSpeedMs"/>, worst case: 55 damage. The one figure worth
    /// designing a hull against, since it is the only one reachable away from Earth.</summary>
    public const double StandardDamage = DamageEnv;

    /// <summary>The weakest strike the game can produce, as damage rather than as a speed. The damage is what a
    /// hull meets; the velocity is the parameter the game happens to express it through. Exact: the multiplier
    /// floor is in the game's own code.</summary>
    public static double MinDamage => WorstCasePool(MinClosingSpeedMs);

    /// <summary>The strongest strike the loaded data can produce, as damage. See
    /// <see cref="FastestClosingSpeed"/> for where the ceiling comes from, since the code has none.</summary>
    public static double MaxDamageFor(IReadOnlyList<CelestialBody> bodies) =>
        WorstCasePool(FastestClosingSpeed(bodies));

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
    /// The inverse of <see cref="WorstCasePool"/>: the closing speed that delivers a given worst-case damage,
    /// clamped to the range the game can actually produce.
    ///
    /// <para>It exists so a caller can work in damage, which is what a hull meets and what every other figure in
    /// the tool is denominated in, and hand the solver the velocity it wants without either side having to know
    /// the multiplier in between. Damage below <see cref="MinDamage"/> is not a weaker strike, it is no strike the
    /// game has: the multiplier floors, so every speed under the floor delivers the same 27.5.</para>
    /// </summary>
    public static double SpeedForDamage(double damage) =>
        Math.Clamp(damage / DamageEnv * AtcSpeedLimit, MinClosingSpeedMs, MaxClosingSpeedMs);

    /// <summary>
    /// Fire one strike along the line <paramref name="startDoc"/> → <paramref name="endDoc"/> and resolve it
    /// against <paramref name="state"/>, which is mutated: a strike leaves damage behind, and drawing the same
    /// line twice is how a wall goes from whole to damaged to gone. Pass a fresh <see cref="DamageState"/> for a
    /// single-strike worst case.
    ///
    /// <para><b>The two points set a line and a direction, and nothing else.</b> The strike comes in along that line
    /// from beyond the grid, travelling from <paramref name="startDoc"/> towards <paramref name="endDoc"/>, and
    /// carries on until it leaves the far side, which is the only kind of ray the game fires (see
    /// <see cref="GameRayFor"/>). Where the line was started and how far it was dragged decide nothing. Stopping at
    /// the release point made the answer turn on how far someone happened to pull, and starting at the press point
    /// let a strike begin inside the hull and skip every part behind it, which no real strike can do.
    /// <see cref="StrikeResult.StartDoc"/> and <see cref="StrikeResult.EndDoc"/> still report the line as it was
    /// given.</para>
    ///
    /// <para><b>The path is in the centre frame</b> (<see cref="TileFrame"/>): an integer is a tile's centre, not
    /// its corner, because that is the frame the colliders below are built in. A caller holding canvas coordinates
    /// converts with <see cref="TileFrame.CornerToCentre"/> first.</para>
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

        var geom = Across(doc, startDoc, endDoc);
        var hits = new List<StrikeHit>();
        var remaining = pool;

        foreach (var (placement, distance) in Along(doc, geom, state))
        {
            if (remaining <= 0) break;
            // A destroyed part is not on the tile any more, so it neither absorbs nor shields. Without this it
            // would soak its last form's pool again on every subsequent strike and never stop. It covers a chain
            // that ended in loose debris as well as one that ended in nothing: the scrap is a real object in the
            // game, but it is not a part of the ship and the plan does not draw it (see DamageState.Apply).
            if (state.IsDestroyed(placement)) continue;
            var from = state.CurrentDef(placement);
            // DmgLeft on the CURRENT form only. A part with no pool at all absorbs nothing and the ray goes on
            // through it, which is the game's `DmgLeft <= 0` branch rather than a stop.
            var left = doc.Catalog.Health(from) - state.DamageOn(placement);
            if (left <= 0) continue;

            var taken = Math.Min(remaining, left);
            remaining -= taken;
            var (broke, to, gone) = state.Apply(placement, from, taken, doc.Catalog);
            hits.Add(new StrikeHit(placement.Id, from, taken, broke, to, gone, distance));
        }

        return new StrikeResult(speed, mult, pool, remaining, startDoc, endDoc, hits);
    }

    /// <summary>
    /// The ray the <b>game</b> fires through one tile at one angle, reproducing <c>DamageRayRandom</c> as of
    /// 1.1.0.1:
    /// <code>
    /// target = GetRandomTile1(bWalkable: false).position    // any grid cell, empty space included
    /// D      = sqrt(nCols² + nRows²)                        // the whole grid's diagonal
    /// u      = AngleAxis(−θ, forward) · up                  // the roll is negated first
    /// DamageRay(target + u·D, −u, 2D, ...)
    /// </code>
    /// So the ray passes through the target tile's centre, starts and stops one grid diagonal either side of it,
    /// and crosses the whole grid from any tile at any angle.
    ///
    /// <para>Null for an empty design, which has no grid. The game falls back to the grid's centre when a ship has
    /// no tiles, but a design with none has nothing for the ray to reach either way.</para>
    /// </summary>
    /// <param name="target">The tile to aim through, in document tile coords. Not required to be on the grid, so a
    /// caller can ask about any line; <see cref="RandomGameRay"/> only ever picks one that is.</param>
    /// <param name="angleDeg">The game's roll. At 0 the strike comes in from the top of the plan, and a larger angle
    /// moves where it comes from clockwise round the plan as drawn.</param>
    public static GameRay? GameRayFor(ShipDocument doc, (int X, int Y) target, double angleDeg)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (Grid(doc) is not { } g) return null;
        var d = Diagonal(g);
        var (ux, uy) = SourceDirection(angleDeg);
        return new GameRay(target, angleDeg,
            (target.X + ux * d, target.Y + uy * d),
            (target.X - ux * d, target.Y - uy * d));
    }

    /// <summary>
    /// One strike aimed the way the game aims it: a tile of the padded grid chosen uniformly, and a heading
    /// uniform round the circle.
    ///
    /// <para>The game's pick is <c>Ship.GetRandomTile1(bWalkable: false)</c>, which takes any entry of
    /// <c>aTiles</c>, and <c>aTiles</c> is every cell of the grid (<c>TileUtils.PadTilemap</c> fills the whole
    /// rectangle) rather than only the occupied ones. So the margin and the empty space inside the bounding box are
    /// as likely a target as the reactor, and a strike through a corner at a grazing angle crosses nothing at
    /// all.</para>
    /// </summary>
    public static GameRay? RandomGameRay(ShipDocument doc, Random rng)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(rng);
        if (Grid(doc) is not { } g) return null;
        var target = (g.X0 + rng.Next(g.Cols), g.Y0 + rng.Next(g.Rows));
        return GameRayFor(doc, target, rng.NextDouble() * 360.0);
    }

    // ---- geometry ----

    /// <summary>A ray across the plan in <b>document</b> tile coords, centre frame.</summary>
    private readonly record struct Ray(double StartX, double StartY, double DirX, double DirY, double Length);

    /// <summary>The grid the game builds for a design, as its top-left tile and its size in document tile coords.
    /// The bounding box plus a one-tile margin all round (§18), whatever frame the items are expressed in, so the
    /// extent comes from the content and not from any anchor.</summary>
    private readonly record struct GridBox(int X0, int Y0, int Cols, int Rows);

    private static GridBox? Grid(ShipDocument doc) =>
        doc.Bounds() is { } b
            ? new GridBox(b.MinX - 1, b.MinY - 1, b.MaxX - b.MinX + 3, b.MaxY - b.MinY + 3)
            : null;

    /// <summary>The game's <c>Mathf.Sqrt(nCols * nCols + nRows * nRows)</c>: the whole grid's diagonal, and how far
    /// a ray reaches either side of the tile it is aimed through.</summary>
    private static double Diagonal(GridBox g) => Math.Sqrt((double)g.Cols * g.Cols + (double)g.Rows * g.Rows);

    /// <summary>The unit vector from the target towards where a strike comes from, in document coords. The game's
    /// <c>AngleAxis(−θ, forward) · up</c> is <c>(sin θ, cos θ)</c> with world y up, so <c>(sin θ, −cos θ)</c> once
    /// document rows run down.</summary>
    private static (double X, double Y) SourceDirection(double angleDeg)
    {
        var rad = angleDeg * Math.PI / 180.0;
        return (Math.Sin(rad), -Math.Cos(rad));
    }

    /// <summary>
    /// The ray a strike along a line travels: in from beyond the grid, across it in the direction from
    /// <paramref name="startDoc"/> to <paramref name="endDoc"/>, and out the far side.
    ///
    /// <para>Built the way the game builds its own, from a point on the line out one full grid diagonal either way.
    /// The point taken is the one nearest the grid's centre, which is within half a diagonal of every part of the
    /// grid the line crosses, so the ray clears the grid with half a diagonal to spare at each end.</para>
    /// </summary>
    private static Ray Across(ShipDocument doc, (double X, double Y) startDoc, (double X, double Y) endDoc)
    {
        double dx = endDoc.X - startDoc.X, dy = endDoc.Y - startDoc.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        // A drag that never moved describes no line, and an empty design has no grid to cross. Either way the
        // strike reaches nothing.
        if (len <= 1e-9 || Grid(doc) is not { } g) return new Ray(startDoc.X, startDoc.Y, 0, 0, 0);

        var (ux, uy) = (dx / len, dy / len);
        // Tile centres run from X0 to X0 + Cols − 1, so the middle of the grid is half that span in.
        var cx = g.X0 + (g.Cols - 1) / 2.0;
        var cy = g.Y0 + (g.Rows - 1) / 2.0;
        var t = (cx - startDoc.X) * ux + (cy - startDoc.Y) * uy;
        var (mx, my) = (startDoc.X + ux * t, startDoc.Y + uy * t);
        var d = Diagonal(g);
        return new Ray(mx - ux * d, my - uy * d, ux, uy, 2 * d);
    }

    /// <summary>
    /// Every placement whose collider the ray crosses, nearest first — the ordered <c>Physics.RaycastAll</c> hit
    /// list. One entry per part however many tiles it spans, because a raycast returns one hit per collider.
    ///
    /// <para><b>The target is the form the part is in now, not the one the design names.</b> A break replaces the
    /// object outright (<c>CondOwner.ModeSwitch</c> swaps in a new <c>CondOwner</c> with its own <c>Item</c>), so
    /// the next ray meets the new form's collider, and 140 of the 1152 stock break pairs change sprite size that
    /// way. Reading the original def would leave the wreck shielding the compartment behind it at the size the
    /// part used to be.</para>
    ///
    /// <para>A part whose chain has ended in loose debris is not here at all: <see cref="DamageState.Apply"/>
    /// destroys it and <see cref="Fire"/> passes over it, because the design no longer owns that tile. So this
    /// walk only ever sees a form the ship still has.</para>
    /// </summary>
    private static List<(Placement Part, double Distance)> Along(ShipDocument doc, Ray ray, DamageState state)
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
            // The centre comes from the ORIGINAL footprint and the extent from the CURRENT form. ModeSwitch hands
            // the replacement the outgoing object's transform position verbatim, so a part that breaks shrinks
            // about the point it already stood on rather than moving.
            var (sw, sh) = SpriteExtent.Tiles(doc.Catalog.Lookup(state.CurrentDef(p)) ?? def);
            // The collider turns with the transform, and every rotation is a right angle, so a turned box is just
            // the swapped extents. Rotation survives a break: ModeSwitch carries fLastRotation across.
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
}
