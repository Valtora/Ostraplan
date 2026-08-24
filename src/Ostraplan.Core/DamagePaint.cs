namespace Ostraplan.Core;

/// <summary>
/// What the Damage Brush is set to: either one condition for everything it touches, or a range it rolls within,
/// per object, as it goes.
///
/// <para>The range is what a lived-in ship actually needs. Painting a corridor at a flat 60% gives every wall the
/// same figure, and since the wear pattern is a function of world position (see <see cref="WearShader"/>) they
/// still look different — but they read as uniformly tired rather than as a place where some things have held up
/// and others have not. A range answers that in one stroke.</para>
/// </summary>
/// <param name="Low">The worst condition the brush will paint, 0..1.</param>
/// <param name="High">The best condition the brush will paint, 0..1. Equal to <paramref name="Low"/> for a fixed
/// brush.</param>
public readonly record struct ConditionBrush(double Low, double High)
{
    /// <summary>A brush that paints exactly this condition everywhere.</summary>
    public static ConditionBrush Fixed(double condition) =>
        new(Paint.Clamp01(condition), Paint.Clamp01(condition));

    /// <summary>A brush that rolls per object between the two, in either argument order.</summary>
    public static ConditionBrush Range(double a, double b) =>
        new(Math.Min(Paint.Clamp01(a), Paint.Clamp01(b)), Math.Max(Paint.Clamp01(a), Paint.Clamp01(b)));

    /// <summary>True when both ends agree, so the brush paints one value and no roll happens.</summary>
    public bool IsFixed => High - Low < 1e-9;

    /// <summary>
    /// One object's condition. Uniform across the range, which is the honest reading of "somewhere between these
    /// two" and matches how <see cref="WearModel"/> already spreads the whole-ship wear.
    /// </summary>
    public double Roll(Random rng)
    {
        ArgumentNullException.ThrowIfNull(rng);
        return IsFixed ? Low : Low + rng.NextDouble() * (High - Low);
    }
}

/// <summary>
/// The rules the Damage Brush paints by. Kept out of the UI so they can be tested without a window, and kept out
/// of <see cref="WearModel"/> because that is the game's own whole-ship kiosk pass and this is an authoring tool
/// that happens to write the same field.
/// </summary>
public static class Paint
{
    /// <summary>Clamp to the 0..1 a condition has to live in.</summary>
    public static double Clamp01(double v) => Math.Clamp(v, 0.0, 1.0);

    /// <summary>Clamp a nullable condition, keeping null as null. Used on every path that reads a condition from
    /// a file, where the value is hand-editable and out-of-range would drive both the wear shader and the
    /// export's <c>StatDamage</c> past the pool the part actually has.</summary>
    public static double? Clamp(double? v) => v is { } d ? Clamp01(d) : null;

    /// <summary>
    /// Whether a part can carry wear at all.
    ///
    /// <para>This is the game's own test and not a policy of ours: <c>Ship.DamageAllCOs</c> skips a part that is
    /// <c>IsSystem</c> or that declares no <c>StatDamageMax</c>, and so does
    /// <see cref="WearModel"/>-driven export. Painting one anyway would write a <c>StatDamage</c> the game has no
    /// pool to hold, and the part would arrive pristine with the design claiming otherwise.</para>
    /// </summary>
    public static bool CanWear(PartDef? part) =>
        part is not null
        && part.StartingConds.Contains("IsInstalled")
        && !part.StartingConds.Contains("IsSystem")
        && part.StartingCondValues.ContainsKey("StatDamageMax");

    /// <summary>
    /// Whether a loose deck item can carry wear. Looser than <see cref="CanWear"/> in exactly one way: a loose
    /// item is by definition not <c>IsInstalled</c>, so that half of the test cannot apply. It still needs a
    /// damage pool, and a system object is still left alone.
    /// </summary>
    public static bool CanWearLoose(PartDef? part) =>
        part is not null
        && !part.StartingConds.Contains("IsSystem")
        && part.StartingCondValues.ContainsKey("StatDamageMax");

    /// <summary>
    /// The def a part painted to <paramref name="condition"/> should actually be, and the condition to store
    /// against it.
    ///
    /// <para><b>Zero condition breaks the part, because that is what the game does.</b> A condition owner whose
    /// <c>StatDamage</c> reaches <c>StatDamageMax</c> fires its break interaction and mode-switches to the def it
    /// breaks into (<c>DestCheck.DamageCheck</c>, §26) — it does not sit at the ceiling. So a brush set to 0 has
    /// to hand back <see cref="Catalog.BreakForm"/>, or the design would claim a state the game cannot hold and
    /// the part would arrive intact-but-ruined.</para>
    ///
    /// <para>The broken form starts its own life pristine, with the overflow discarded rather than carried: a
    /// brush is authoring an end state, not resolving a strike, and a strike is what
    /// <see cref="DamageState.Apply"/> is for. A part that breaks into nothing the game names is left at the
    /// lowest condition it can actually hold instead, since a design has no way to place an absence.</para>
    /// </summary>
    /// <returns>The def to place and the condition to store, or null when the part cannot take wear at all.</returns>
    public static (string Def, double? Condition)? Resolve(string defName, double condition, Catalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!CanWear(catalog.Lookup(defName))) return null;

        var c = Clamp01(condition);
        if (c > 0) return (defName, c);

        // Broken. Walk one stage, exactly one, the way a filled pool does.
        return catalog.BreakForm(defName) is { } broken && catalog.Lookup(broken) is not null
            ? (broken, null)
            : (defName, 0.0);
    }
}

/// <summary>Paint a condition onto a placed part, or clear it back to whatever the export-wide wear decides.
/// Nothing about the part's geometry changes, so no re-analysis of the layout is implied — but the value DOES
/// move the Ship Rating's Condition slot, so the document raises Changed like a fill does.</summary>
public sealed class SetConditionCommand(Placement placement, double? before, double? after)
    : IDocCommand, IAuditDescribable
{
    public void Do(ShipDocument doc) => doc.SetCondition(placement, after);
    public void Undo(ShipDocument doc) => doc.SetCondition(placement, before);
    public string Describe(Func<string, string?> f) =>
        after is null
            ? $"Cleared the painted condition on {AuditFmt.Name(f, placement.DefName)} {AuditFmt.At(placement.X, placement.Y)}"
            : $"Painted {AuditFmt.Name(f, placement.DefName)} {AuditFmt.At(placement.X, placement.Y)} to {after.Value * 100:0}% condition";
}

/// <summary>The loose twin of <see cref="SetConditionCommand"/>.</summary>
public sealed class SetLooseConditionCommand(LooseObject obj, double? before, double? after)
    : IDocCommand, IAuditDescribable
{
    public void Do(ShipDocument doc) => doc.SetCondition(obj, after);
    public void Undo(ShipDocument doc) => doc.SetCondition(obj, before);
    public string Describe(Func<string, string?> f) =>
        after is null
            ? $"Cleared the painted condition on the loose {AuditFmt.Name(f, obj.DefName)}"
            : $"Painted the loose {AuditFmt.Name(f, obj.DefName)} to {after.Value * 100:0}% condition";
}

/// <summary>
/// One Damage Brush stroke, from press to release: what the brush does to a tile, and the commands it has run so
/// the host can record the whole drag as one undo step.
///
/// <para>It lives in Core rather than in the brush window because every case in it is a rule and not chrome — a
/// part driven to nothing, a part the stroke crosses twice, a locked part, a deck item — and a rule that only
/// exists inside a mouse handler can only be eyeballed (docs/CONVENTIONS.md).</para>
///
/// <para>Commands are executed as they are made, so the wear appears under the cursor as the mouse moves, and are
/// handed over already-done on release for the stack to push as a batch.</para>
/// </summary>
/// <param name="doc">The design being painted.</param>
/// <param name="rng">Seeded by the caller once per window rather than per stroke, so re-painting the same corridor
/// twice does not hand back the identical set of rolls.</param>
public sealed class DamageStroke(ShipDocument doc, Random rng)
{
    private readonly List<IDocCommand> _cmds = [];
    private readonly HashSet<Guid> _rolled = [];
    private int _painted;
    private int _skipped;

    /// <summary>What the stroke has done so far, in order, already executed.</summary>
    public IReadOnlyList<IDocCommand> Commands => _cmds;

    /// <summary>What the stroke has painted, and what it reached but could not, across the whole drag. Running
    /// totals rather than a per-tile figure because the area brush covers its whole rectangle in one release, and
    /// a freehand stroke's last tile says nothing about the corridor behind it.</summary>
    public (int Painted, int Skipped) Totals => (_painted, _skipped);

    /// <summary>Start the next stroke. It does not undo anything: the host has taken these commands.</summary>
    public void Reset()
    {
        _cmds.Clear();
        _rolled.Clear();
        _painted = 0;
        _skipped = 0;
    }

    /// <summary>
    /// Paint everything standing on one tile. A tile can hold several parts (a floor under a wall under a conduit)
    /// and the stroke is painting the tile, so each of them takes its own roll — one figure shared across a whole
    /// deck's worth of stacked parts would read as a flat patch.
    ///
    /// <para><b>Once per object, not once per tile.</b> A part wider than a tile is reached again on every tile of
    /// its body, and re-rolling it there would give a big tank as many chances at a bad number as it has tiles, and
    /// would walk a part driven to nothing a second stage down its break chain in a single stroke.</para>
    /// </summary>
    /// <returns>How many objects took the brush, and how many were reached but could not.</returns>
    public (int Painted, int Skipped) PaintTile(int x, int y, ConditionBrush brush, bool includeLoose)
    {
        var painted = 0;
        var skipped = 0;

        // A snapshot of the tile, because painting is allowed to change what stands on it: a part driven to
        // nothing is removed and its broken form placed, and both halves edit the very index list PlacementsAt
        // hands back. Enumerating that live is a "collection was modified" crash on the first break.
        foreach (var p in doc.PlacementsAt(x, y).ToArray())
        {
            if (!_rolled.Add(p.Id)) continue;   // already had its roll earlier in this stroke
            if (doc.IsLocked(p)) { skipped++; continue; }
            if (Apply(p, brush)) painted++; else skipped++;
        }

        if (includeLoose && doc.LooseAt(x, y) is { } lo && _rolled.Add(lo.Id))
        {
            if (ApplyLoose(lo, brush)) painted++; else skipped++;
        }

        _painted += painted;
        _skipped += skipped;
        return (painted, skipped);
    }

    /// <summary>
    /// Paint every tile of a rectangle, corners inclusive and in either order: a whole area drag in one call.
    /// Identical to walking the box with <see cref="PaintTile"/>, so a part straddling the edge is painted once
    /// like any other, and the whole rectangle is still one stroke and therefore one undo step.
    ///
    /// <para>Two things it does that a tile walk cannot. The document's <c>Changed</c> is held until the end,
    /// because a box is thousands of tiles and a problem scan each would stall the app the way a big box fill
    /// once did. And the rectangle is clipped to what the design actually occupies: a drag at low zoom can bound
    /// far more empty space than ship, and none of it can take paint.</para>
    /// </summary>
    /// <returns>The stroke's running totals, which for an area brush is the area's own count.</returns>
    public (int Painted, int Skipped) PaintArea(int x0, int y0, int x1, int y1, ConditionBrush brush, bool includeLoose)
    {
        if (Occupied() is not { } ship) return Totals;

        var minX = Math.Max(Math.Min(x0, x1), ship.MinX);
        var maxX = Math.Min(Math.Max(x0, x1), ship.MaxX);
        var minY = Math.Max(Math.Min(y0, y1), ship.MinY);
        var maxY = Math.Min(Math.Max(y0, y1), ship.MaxY);

        using var _ = doc.SuspendChanged();
        for (var y = minY; y <= maxY; y++)
            for (var x = minX; x <= maxX; x++)
                PaintTile(x, y, brush, includeLoose);

        return Totals;
    }

    /// <summary>The tiles the design could hold anything on: the parts' own bounds widened to cover the deck
    /// items, which sit outside the spatial index and can lie past the last wall. Null for an empty design.</summary>
    private (int MinX, int MinY, int MaxX, int MaxY)? Occupied()
    {
        var b = doc.Bounds();
        var loose = doc.LooseObjects;
        if (b is not { } bounds) return loose.Count == 0 ? null : Spread(loose);
        if (loose.Count == 0) return bounds;

        var (lx0, ly0, lx1, ly1) = Spread(loose);
        return (Math.Min(bounds.MinX, lx0), Math.Min(bounds.MinY, ly0),
                Math.Max(bounds.MaxX, lx1), Math.Max(bounds.MaxY, ly1));

        static (int, int, int, int) Spread(IReadOnlyCollection<LooseObject> items) =>
            (items.Min(o => o.X), items.Min(o => o.Y), items.Max(o => o.X), items.Max(o => o.Y));
    }

    /// <summary>Paint one placed part, or report that it cannot take wear. Returns whether anything happened.</summary>
    private bool Apply(Placement p, ConditionBrush brush)
    {
        if (Paint.Resolve(p.DefName, brush.Roll(rng), doc.Catalog) is not { } resolved) return false;

        // A condition of zero breaks the part into its damaged form, which is a def change rather than a value
        // change, so it goes through the same swap Repair uses in the other direction.
        if (resolved.Def != p.DefName)
        {
            if (FormSwap.BuildSwap(doc, [(p, resolved.Def)]) is not { } swap) return false;
            swap.Cmd.Do(doc);
            _cmds.Add(swap.Cmd);

            foreach (var made in swap.New)
            {
                _rolled.Add(made.Id);   // the broken form is this stroke's own work, not something it found
                // A swap restates the part, and a restate carries the painted condition across, which is right
                // for an uninstall and wrong here: the broken form is a different part starting its own life,
                // which is exactly what Paint.Resolve hands back alongside the def.
                if (!Nearly(made.Condition, resolved.Condition))
                    Run(new SetConditionCommand(made, made.Condition, resolved.Condition));
            }
            return true;
        }

        if (Nearly(p.Condition, resolved.Condition)) return false;   // already there: no undo step for a no-op
        Run(new SetConditionCommand(p, p.Condition, resolved.Condition));
        return true;
    }

    /// <summary>The loose twin of <see cref="Apply"/>.</summary>
    private bool ApplyLoose(LooseObject lo, ConditionBrush brush)
    {
        if (!Paint.CanWearLoose(doc.Catalog.Lookup(lo.DefName))) return false;
        // A deck item has no break chain to walk here: breaking one would delete it from the design, and a brush
        // is not a way to remove things. It floors at whatever the roll gave instead.
        var condition = Paint.Clamp01(brush.Roll(rng));
        if (Nearly(lo.Condition, condition)) return false;
        Run(new SetLooseConditionCommand(lo, lo.Condition, condition));
        return true;
    }

    private void Run(IDocCommand cmd)
    {
        cmd.Do(doc);
        _cmds.Add(cmd);
    }

    private static bool Nearly(double? a, double? b) =>
        a is null && b is null || a is { } x && b is { } y && Math.Abs(x - y) < 1e-6;
}
