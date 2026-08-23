namespace Ostraplan.Core;

/// <summary>Which edge of the ship's bounding box a trajectory entered through. The game only ever spreads a
/// multi-tile impact <b>along</b> the entry edge, so this decides the spread axis.</summary>
public enum EntryEdge { Top, Bottom, Left, Right }

/// <summary>Where an impact begins and the unit direction it travels, in <b>document</b> tile coords. This is the
/// path the user drew: the game itself would only ever start one on the bounding box
/// (<c>DamageSystem.FindIntersection</c>), but what happens along the path once drawn is its arithmetic exactly.</summary>
/// <param name="Length">How far the drawn line runs, in tiles. Reported for the readout and used to derive the
/// direction; it does <b>not</b> bound the shot. The line is an aim rather than a path, so a projectile travels
/// along it until it finds something or leaves the ship, however short the drag that set the heading (see
/// <see cref="WeaponImpact.Steps"/>).</param>
public sealed record ImpactEntry(
    double DocX, double DocY, double DirX, double DirY, EntryEdge Edge, double Length);

/// <summary>One part a weapon impact damaged.</summary>
public sealed record ImpactHit(
    Guid PlacementId, string FromDef, double Absorbed, int StagesBroken, bool Destroyed, int DocX, int DocY);

/// <summary>The result of one impact.</summary>
/// <param name="Centre">The tile it went off on, for a blast or a point impact, or null when nothing along the
/// drawn path could be detonated against. Worth surfacing: as successive shots open a corridor this walks inward,
/// and seeing it move is how you tell a strike that punched deeper from one that simply missed.</param>
public sealed record ImpactResult(
    string Attack,
    ImpactEntry Entry,
    double TotalDamage,
    double Delivered,
    (int X, int Y)? Centre,
    IReadOnlyList<(int X, int Y)> Cells,
    IReadOnlyList<ImpactHit> Hits)
{
    /// <summary>True when nothing absorbed anything. Two different things can cause it, and
    /// <see cref="Centre"/> is what tells them apart: a null centre means the shot never found a tile to go off
    /// on, and a set one means it did but every part within reach was already spent.</summary>
    public bool Missed => Hits.Count == 0;
}

/// <summary>
/// A ship-weapon impact against a design: missiles, mass driver rounds, point-defence fire, collisions and
/// scuttling. Ported from <c>DamageSystem.DamageRayShallow</c> → <c>ProjectRayOnGrid</c> (§26, verified 1.0.0.11).
///
/// <para><b>This is the other damage system, not a variant of the first.</b> Where a micrometeoroid raycasts
/// Unity colliders and takes only a part's current form, every projectile in the game walks the <b>tile grid</b>
/// and prices each cell against the whole break chain (<see cref="Catalog.MaxHealth"/>). A missile can therefore
/// take a wall from whole to gone in one cell, which no micrometeoroid ever does. Projectiles use this path even
/// against the player's own deep-loaded ship, so it is not the "far away ships" model.</para>
///
/// <para><b>What is deliberately not reproduced.</b> The game jitters every shot before it lands
/// (<c>AddVariance</c>: ±10° on direction and a slide of ±40% of the grid along the entry edge) and rolls per-part
/// fire chances. Both are live RNG, so an aim point here is exact and a plan reports the worst case rather than a
/// sample. Fire and chain explosions past the first step are simulation and out of scope. The path is the user's
/// to draw, which is more freedom than the game gives an attacker and is the point: a designer needs to ask what a
/// hit <i>here</i> would do, not only what the game happens to roll.</para>
/// </summary>
public static class WeaponImpact
{
    /// <summary>
    /// The impact a drawn path describes: it starts where the line starts and travels along it.
    ///
    /// <para>The <see cref="EntryEdge"/> is still derived, because the game spreads a multi-tile impact along the
    /// edge a projectile came through and the spread axis has to come from somewhere. It is read off the direction
    /// of travel, which is the same answer the game's own <c>FindIntersection</c> would reach for a trajectory
    /// heading that way.</para>
    ///
    /// <para>Null only for a path of no length, which describes nothing.</para>
    ///
    /// <para><b>The path is in the centre frame</b> (<see cref="TileFrame"/>), the same as the micrometeoroid
    /// solver's: <see cref="StartingCells"/> rounds to the nearest tile, which is only the right answer when an
    /// integer already means a tile's middle. A caller holding canvas coordinates converts with
    /// <see cref="TileFrame.CornerToCentre"/> first.</para>
    /// </summary>
    public static ImpactEntry? EntryAlong(ShipDocument doc, (double X, double Y) startDoc, (double X, double Y) endDoc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        double dx = endDoc.X - startDoc.X, dy = endDoc.Y - startDoc.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len <= 1e-9) return null;
        dx /= len;
        dy /= len;

        // Travelling in (dx, dy) means it came through the edge it is heading away from.
        var edge = Math.Abs(dx) >= Math.Abs(dy)
            ? dx >= 0 ? EntryEdge.Left : EntryEdge.Right
            : dy >= 0 ? EntryEdge.Top : EntryEdge.Bottom;
        return new ImpactEntry(startDoc.X, startDoc.Y, dx, dy, edge, len);
    }

    /// <summary>
    /// Resolve <paramref name="attack"/> entering at <paramref name="entry"/>, mutating <paramref name="state"/>.
    /// </summary>
    public static ImpactResult Fire(
        ShipDocument doc, ShipAttackDef attack, ImpactEntry entry, DamageState state)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(attack);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(state);

        var grid = CellGrid(doc);
        var starts = StartingCells(entry, attack.Radius);
        var cells = new List<(int X, int Y)>();
        var hits = new List<ImpactHit>();
        var delivered = 0.0;
        (int X, int Y)? centre = null;

        switch (attack.Type)
        {
            case ImpactType.Ray:
                for (var i = 0; i < starts.Count; i++)
                {
                    var budget = attack.TotalDamage / starts.Count;
                    var soft = IsSoftEdge(i, starts.Count, attack.SoftEdgeTileRadius);
                    delivered += WalkRay(doc, grid, starts[i], entry, attack, budget, soft, cells, hits, state);
                }
                break;

            case ImpactType.Circular:
                if (ImpactPoint(doc, grid, starts[0], entry, attack, state) is { } blastAt)
                {
                    centre = blastAt;
                    delivered += Blast(doc, grid, blastAt, attack, cells, hits, state);
                }
                break;

            case ImpactType.Point:
            case ImpactType.Fragmentation:
                for (var i = 0; i < starts.Count; i++)
                {
                    if (ImpactPoint(doc, grid, starts[i], entry, attack, state) is not { } at) continue;
                    centre ??= at;
                    var soft = IsSoftEdge(i, starts.Count, attack.SoftEdgeTileRadius);
                    cells.Add(at);
                    delivered += ApplyToCell(doc, grid, at, attack.TotalDamage, soft, hits, state);
                }
                break;
        }

        return new ImpactResult(attack.Name, entry, attack.TotalDamage, delivered, centre, cells, hits);
    }

    // ---- the grid ----

    /// <summary>
    /// Document tile → the parts on it, the game's <c>GridUtils.CreateShallowItemGrid</c>. Only installed parts
    /// count, and a <b>bulky</b> part is entered once per cell of its footprint rather than only on its anchor, so
    /// a nav station or a thruster cluster can be hit anywhere across its span.
    /// </summary>
    private static Dictionary<(int X, int Y), List<Placement>> CellGrid(ShipDocument doc)
    {
        var grid = new Dictionary<(int, int), List<Placement>>();
        foreach (var p in doc.Placements)
        {
            if (doc.Part(p) is not { } def) continue;
            if (!def.StartingConds.Contains("IsInstalled")) continue;
            if (def.StartingConds.Contains("IsMooringPort")) continue;

            if (IsBulky(def))
            {
                var (w, h) = GridMath.Size(def.Item.Width, def.Item.Height, p.Rot);
                for (var dy = 0; dy < h; dy++)
                    for (var dx = 0; dx < w; dx++)
                        Add(grid, (p.X + dx, p.Y + dy), p);
            }
            else Add(grid, (p.X, p.Y), p);
        }
        return grid;

        static void Add(Dictionary<(int, int), List<Placement>> g, (int, int) k, Placement p)
        {
            if (!g.TryGetValue(k, out var list)) g[k] = list = [];
            list.Add(p);
        }
    }

    /// <summary>The game's bulky set: parts that occupy every cell of their footprint for damage purposes rather
    /// than just their anchor tile.</summary>
    private static bool IsBulky(PartDef def) =>
        def.StartingConds.Contains("IsPortal") || def.StartingConds.Contains("IsNavStation")
        || def.StartingConds.Contains("IsHeavyLiftRotor") || def.StartingConds.Contains("IsRCSCluster")
        || def.StartingConds.Contains("IsShipWeapon");

    // ---- patterns ----

    /// <summary>The entry cell plus <c>fRadius</c> cells either side of it <b>along the entry edge</b>, giving
    /// <c>2r+1</c> parallel starts. The game's <c>AddStartingTiles</c>.</summary>
    private static List<(int X, int Y)> StartingCells(ImpactEntry entry, int radius)
    {
        var c = ((int)Math.Round(entry.DocX), (int)Math.Round(entry.DocY));
        var list = new List<(int X, int Y)> { c };
        if (radius <= 0) return list;

        var horizontal = entry.Edge is EntryEdge.Top or EntryEdge.Bottom;
        for (var i = 1; i <= radius; i++)
        {
            list.Add(horizontal ? (c.Item1 - i, c.Item2) : (c.Item1, c.Item2 - i));
            list.Add(horizontal ? (c.Item1 + i, c.Item2) : (c.Item1, c.Item2 + i));
        }
        return list;
    }

    /// <summary>The outermost starts are "soft": a part they find still whole is capped at <c>Health</c> rather
    /// than <c>MaxHealth</c>, so one pass takes it into its next form and no further. The cap is on the part's
    /// state, not on the start, and lifts once the part reads as damaged (<see cref="DamageState.IsDamaged"/>).
    /// With three starts and a soft edge of 2 — point-defence fire — every start is soft, so 20mm cannot take
    /// anything from whole to gone in one burst, but a second burst on the same tile prices it against the whole
    /// chain and can.</summary>
    private static bool IsSoftEdge(int index, int count, int softRadius) =>
        softRadius > 0 && (index <= softRadius - 1 || index >= count - softRadius);

    /// <summary>Walk one sub-ray, spending its budget cell by cell. An empty cell does not count against the
    /// range, so <c>fMaxRange</c> bounds <b>occupied</b> cells rather than distance.</summary>
    private static double WalkRay(
        ShipDocument doc, Dictionary<(int X, int Y), List<Placement>> grid, (int X, int Y) start,
        ImpactEntry entry, ShipAttackDef attack, double budget, bool soft,
        List<(int X, int Y)> cells, List<ImpactHit> hits, DamageState state)
    {
        var spent = 0.0;
        double px = start.X, py = start.Y;
        var range = (int)attack.MaxRange;
        var bounds = Bounds(grid);
        var entered = false;
        var steps = Steps(bounds, px, py);

        while (range >= 0 && budget > 0 && steps-- > 0)
        {
            var cell = ((int)Math.Round(px), (int)Math.Round(py));
            if (grid.ContainsKey(cell))
            {
                var used = ApplyToCell(doc, grid, cell, budget, soft, hits, state);
                if (used > 0)
                {
                    cells.Add(cell);
                    budget -= used;
                    spent += used;
                    // Only a cell that actually absorbed spends range. The game does this explicitly — a cell its
                    // ApplyDamageToCell reports as unhit gets the decrement handed back (`if (!hit) num2++`) — and
                    // it is what lets a second shot down the same line reach further than the first, once the
                    // first has emptied the tiles nearest the hull.
                    range--;
                }
            }
            px += entry.DirX;
            py += entry.DirY;
            // A drawn path may begin well clear of the ship, which the game's own never did — it always started on
            // the grid edge. So only stop once the walk has actually reached the ship and then left it again.
            if (!Outside(bounds, px, py)) entered = true;
            else if (entered) break;
        }
        return spent;
    }

    /// <summary>
    /// The first cell along the trajectory that can be hit — and, for an attack with trigger conds, the first
    /// holding one of them. This is what makes a missile detonate on the hull rather than at the centre.
    ///
    /// <para><b>Every part on a tile is tested, which is a deliberate deviation from the game.</b> The game's
    /// <c>FindPointsOfImpact</c> walks a cell's parts, skips any that are spent, and then <c>break</c>s
    /// unconditionally after examining the first one it does not skip — whether or not that part matched a trigger
    /// cond. So in game a wall sharing a tile with a floor triggers a missile only when the wall happens to be the
    /// first of the two, and listed the other way round the missile sails over a tile with a wall on it. Measured
    /// on a real hull, 15% of trigger-carrying tiles are in that state (§26).</para>
    ///
    /// <para>Reproducing it made the answer depend on the order parts appear in the ship's item list, which is not
    /// a property of the design and is not something a designer can see, reason about or change. Two plans
    /// identical on screen gave different impact points. A planner whose job is "what would a hit here break"
    /// cannot answer "it depends how the file was written", so this asks whether the <b>tile</b> holds a trigger
    /// rather than whether its first part does. The effect is that a wall stops a missile whenever there is a wall
    /// there, which is also what a user reading the plan expects.</para>
    ///
    /// <para><b>Deliberate deviation, not a port.</b> Everything after the impact point (the blast falloff, the
    /// doubled centre, what each cell absorbs) is the game's arithmetic exactly.</para>
    /// </summary>
    private static (int X, int Y)? ImpactPoint(
        ShipDocument doc, Dictionary<(int X, int Y), List<Placement>> grid, (int X, int Y) start,
        ImpactEntry entry, ShipAttackDef attack, DamageState state)
    {
        double px = start.X, py = start.Y;
        var bounds = Bounds(grid);
        var entered = false;
        var steps = Steps(bounds, px, py);
        while (steps-- > 0)
        {
            var cell = ((int)Math.Round(px), (int)Math.Round(py));
            if (grid.TryGetValue(cell, out var parts))
            {
                // Spent parts are skipped rather than examined, which is what walks the impact point inward as
                // successive shots chew through the outer hull.
                if (!attack.DetonatesOnContact)
                {
                    // No trigger conds: anything with something left to give is enough, so order never mattered.
                    if (FirstStanding(doc, parts, state) is not null) return cell;
                }
                else if (Triggers(doc, parts, attack, state)) return cell;
            }
            px += entry.DirX;
            py += entry.DirY;
            if (!Outside(bounds, px, py)) entered = true;
            else if (entered) return null;
        }
        return null;
    }

    /// <summary>A disc around the impact cell, falling off linearly with distance, nearest first.</summary>
    private static double Blast(
        ShipDocument doc, Dictionary<(int X, int Y), List<Placement>> grid, (int X, int Y) centre,
        ShipAttackDef attack, List<(int X, int Y)> cells, List<ImpactHit> hits, DamageState state)
    {
        var r = Math.Max(0, attack.Radius);
        var ordered = new List<((int X, int Y) Cell, double Dist)>();
        // The game seeds the list with the impact cell and the square scan then adds it AGAIN at distance 0, so
        // the centre takes two full-strength applications. Reproduced: it is a real doubling at the heart of every
        // blast, and a design that survives one but not two would otherwise be reported wrongly.
        ordered.Add((centre, 0));
        for (var y = centre.Y - r; y <= centre.Y + r; y++)
            for (var x = centre.X - r; x <= centre.X + r; x++)
            {
                double ddx = x - centre.X, ddy = y - centre.Y;
                var d2 = ddx * ddx + ddy * ddy;
                if (d2 <= (double)r * r) ordered.Add(((x, y), Math.Sqrt(d2)));
            }

        var spent = 0.0;
        foreach (var (cell, dist) in ordered.OrderBy(t => t.Dist))
        {
            if (!grid.ContainsKey(cell)) continue;
            var amount = attack.TotalDamage * (1 - dist / Math.Max(1, r));
            if (amount <= 0) continue;
            var soft = attack.SoftEdgeTileRadius > 0 && dist > r - attack.SoftEdgeTileRadius;
            cells.Add(cell);
            spent += ApplyToCell(doc, grid, cell, amount, soft, hits, state);
        }
        return spent;
    }

    // ---- damage ----

    /// <summary>
    /// Spend a cell's share of the damage across the parts on it, in placement order, each priced against the
    /// whole break chain — or, on a soft edge, against its own pool only, which caps it at damaged.
    /// </summary>
    private static double ApplyToCell(
        ShipDocument doc, Dictionary<(int X, int Y), List<Placement>> grid, (int X, int Y) cell,
        double budget, bool soft, List<ImpactHit> hits, DamageState state)
    {
        var used = 0.0;
        if (!grid.TryGetValue(cell, out var parts)) return 0;

        foreach (var p in parts)
        {
            if (budget <= 0) break;
            // ApplyDamageToCell's own skip, and the same test FindPointsOfImpact uses to choose a detonation
            // tile. A part with no damage pool at all is spent from the start and the shot passes through it.
            if (state.IsSpent(p, doc.Catalog)) continue;

            // The soft edge caps the cell at the first break rather than the whole chain, but the game applies
            // that cap ONLY to a part that is still whole (`if (damageOnly && !item.IsDamaged)`). Once a part
            // reads as damaged the cap comes off, which is what lets point-defence fire finish something on a
            // later pass instead of stalling against a wall it has already cracked.
            var ceiling = soft && !state.IsDamaged(p, doc.Catalog)
                ? doc.Catalog.Health(p.DefName)
                : doc.Catalog.MaxHealth(p.DefName);
            var left = ceiling - state.TotalDamage(p, doc.Catalog);
            if (left <= 0) continue;

            var take = Math.Min(budget, left);
            budget -= take;
            used += take;

            // Drive the part through as many stages as the damage covers: unlike a micrometeoroid, one cell of a
            // blast can carry a wall the whole way.
            var stages = 0;
            var remaining = take;
            var destroyed = false;
            var from = state.CurrentDef(p);
            while (remaining > 0)
            {
                var cur = state.CurrentDef(p);
                var room = doc.Catalog.Health(cur) - state.DamageOn(p);
                if (room <= 0) break;
                var bite = Math.Min(remaining, room);
                remaining -= bite;
                var (broke, to) = state.Apply(p, cur, bite, doc.Catalog);
                if (!broke) break;
                stages++;
                if (to is null) { destroyed = true; break; }
            }
            hits.Add(new ImpactHit(p.Id, from, take, stages, destroyed, cell.X, cell.Y));
        }
        return used;
    }

    /// <summary>The occupied extent plus a small margin, computed once per walk. A trajectory that leaves it has
    /// nothing left to reach, which is what terminates every walk here — the game bounds its own by the grid
    /// array, and this is the same bound expressed over a sparse map.</summary>
    private static (int MinX, int MaxX, int MinY, int MaxY)? Bounds(Dictionary<(int X, int Y), List<Placement>> grid)
    {
        if (grid.Count == 0) return null;
        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
        foreach (var (x, y) in grid.Keys)
        {
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }
        return (minX - 2, maxX + 2, minY - 2, maxY + 2);
    }

    private static bool Outside((int MinX, int MaxX, int MinY, int MaxY)? bounds, double px, double py) =>
        bounds is not { } b || px < b.MinX || px > b.MaxX || py < b.MinY || py > b.MaxY;

    /// <summary>
    /// The first part on a tile that still has something left to give, or null when every part on it is spent.
    ///
    /// <para>The game's gate is <see cref="DamageState.IsSpent"/>, not destroyed: <c>FindPointsOfImpact</c> skips
    /// on <c>|CurrentDamage − GetMaxHealth()| &lt; 0.01</c> and then <c>break</c>s, so the first part with any
    /// capacity left is the only one a missile's trigger conds are ever tested against.</para>
    /// </summary>
    private static Placement? FirstStanding(ShipDocument doc, List<Placement> parts, DamageState state)
    {
        foreach (var p in parts)
            if (!state.IsSpent(p, doc.Catalog))
                return p;
        return null;
    }

    /// <summary>Whether anything still standing on this tile carries one of the attack's trigger conditions. See
    /// <see cref="ImpactPoint"/> for why this asks about the tile rather than about its first part.</summary>
    private static bool Triggers(
        ShipDocument doc, List<Placement> parts, ShipAttackDef attack, DamageState state)
    {
        foreach (var p in parts)
        {
            if (state.IsSpent(p, doc.Catalog)) continue;
            if (doc.Part(p) is { } def && attack.TriggerConds.Any(def.StartingConds.Contains)) return true;
        }
        return false;
    }

    /// <summary>
    /// How many one-tile steps a walk may take: enough to cross the whole ship from wherever the line starts.
    ///
    /// <para><b>The drawn line is an aim, not a path, and that is a deliberate deviation.</b> A shot travels along
    /// the direction it was given until it leaves the ship, however short the drag that set it. The alternative —
    /// stopping at the point the mouse was released — made the answer depend on how far someone happened to drag,
    /// so the same shot down the same line hit or missed according to a gesture rather than according to the hull.
    /// Nothing in the game bounds a projectile by a distance either: it enters at the grid edge and runs until it
    /// finds something or leaves. Treating the drag as a pointer is closer to that than treating it as a
    /// segment.</para>
    ///
    /// <para>The walk still stops the moment it leaves the ship (see the <c>entered</c> tests in the callers), so
    /// "infinite" costs nothing: this is only the backstop that keeps a line aimed away from the ship, which never
    /// enters and so never trips that test, from running forever.</para>
    /// </summary>
    private static int Steps((int MinX, int MaxX, int MinY, int MaxY)? bounds, double startX, double startY)
    {
        if (bounds is not { } b) return 0;
        var span = (b.MaxX - b.MinX) + (b.MaxY - b.MinY);
        var reach = Math.Abs(startX - b.MinX) + Math.Abs(startX - b.MaxX)
                  + Math.Abs(startY - b.MinY) + Math.Abs(startY - b.MaxY);
        return (int)Math.Min(8192, span + reach + 8);
    }
}
