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
