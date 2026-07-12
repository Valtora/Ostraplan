namespace Ostraplan.Core;

/// <summary>How much wear to bake into an exported / injected ship. <see cref="TargetCondition"/> is the
/// desired <b>average</b> part condition (1.0 = pristine, 0.10 = the heaviest we allow); each installed part is
/// then damaged independently around that average. <see cref="Seed"/> pins the RNG for a reproducible roll
/// (tests / a stable export); null uses a time-based seed. <see cref="Enabled"/> off = the ship stays pristine
/// (the pre-0.31 behaviour).</summary>
public sealed record WearOptions(bool Enabled, double TargetCondition, int? Seed = null)
{
    /// <summary>Wear disabled — a pristine ship (grade A), the pre-0.31 export/inject default.</summary>
    public static WearOptions Pristine { get; } = new(false, 1.0);

    /// <summary>The game's own kiosk ("Used") wear: average part condition ≈ 87.6%.</summary>
    public static WearOptions Vanilla { get; } = new(true, WearModel.VanillaUsedCondition);
}

/// <summary>
/// A faithful port of how Ostranauts wears a ship sold from a broker kiosk — <c>Ship.DamageAllCOs</c> →
/// <c>CondOwner.BreakIn</c> (behaviour verified against a decompile; the two magnitude constants are hardcoded
/// in <c>Assembly-CSharp.dll</c>, re-verify after a game patch).
///
/// <para>Each installed, damageable part (<c>CondOwner</c>) carries <c>StatDamageMax = M</c> (its health pool,
/// from the def) and accumulates <c>StatDamage ∈ [0, M]</c>; its condition is <c>1 − StatDamage/M</c>, and the
/// Ship Rating "Condition" slot is the mean of <c>clamp01(1 − StatDamage/StatDamageMax)</c> over every installed
/// part (<c>Ship.CalculateRating</c>).</para>
///
/// <para>The game's kiosk pass is <c>DamageAllCOs(0.33)</c>: per part, <c>StatDamage = uniform(0, 0.33·M)</c>,
/// then ×0.75 because a fresh part is <c>IsPristine</c> — so the effective ceiling is
/// <see cref="VanillaUsedCeiling"/> = 0.2475 and the mean condition is ≈ 87.6% (parts spread ~75%–100%). Parts
/// flagged <c>IsSystem</c>, and parts with no <c>StatDamageMax</c>, are never damaged.</para>
///
/// <para>Ostraplan generalises the single 0.33 knob to a target <b>average condition</b>: for a target C the
/// uniform ceiling is <c>2·(1 − C)</c> (so the mean damage is <c>1 − C</c>), giving vanilla at C ≈ 0.876 and
/// grungier ships as C drops. A hard per-part floor keeps condition ≥ <see cref="MinCondition"/> (10%) — the
/// game's own kiosk wear never comes close, but a heavy custom setting would without it.</para>
/// </summary>
public static class WearModel
{
    /// <summary>The game's kiosk wear pass magnitude — <c>DamageAllCOs(0.33)</c> (the ceiling as a fraction of
    /// <c>StatDamageMax</c> before the pristine factor).</summary>
    public const double VanillaUsedPercentage = 0.33;

    /// <summary>The factor <c>CondOwner.BreakIn</c> applies to a still-<c>IsPristine</c> part's first damage
    /// (fresh parts take 25% less). Kiosk parts are all pristine when the pass runs, so it always applies.</summary>
    public const double PristineFactor = 0.75;

    /// <summary>The effective uniform ceiling of a vanilla "Used" kiosk part's damage as a fraction of its
    /// <c>StatDamageMax</c>: <c>0.33 × 0.75 = 0.2475</c>. Mean damage is half that; see
    /// <see cref="VanillaUsedCondition"/>.</summary>
    public const double VanillaUsedCeiling = VanillaUsedPercentage * PristineFactor;

    /// <summary>The per-part condition floor Ostraplan enforces: no part is ever left below 10% condition,
    /// however heavy the target. (Vanilla kiosk wear floors far higher, ≈ 75%.)</summary>
    public const double MinCondition = 0.10;

    /// <summary>The largest damage rate a part may take (so condition stays ≥ <see cref="MinCondition"/>).</summary>
    public const double MaxDamageRate = 1.0 - MinCondition;

    /// <summary>The average part condition the game's own kiosk ("Used") wear produces: <c>1 − 0.2475/2 ≈
    /// 0.876</c>. The Vanilla preset targets this.</summary>
    public static double VanillaUsedCondition => 1.0 - VanillaUsedCeiling / 2.0;

    /// <summary>The uniform damage-rate ceiling for a target average condition <paramref name="targetCondition"/>:
    /// <c>2·(1 − C)</c>, so a per-part rate drawn from <c>uniform(0, ceiling)</c> averages <c>1 − C</c>. Clamped to
    /// [0, 2]; a target ≥ 1 gives 0 (pristine).</summary>
    public static double CeilingFor(double targetCondition) =>
        Math.Clamp(2.0 * (1.0 - targetCondition), 0.0, 2.0);

    /// <summary>Draw one part's damage rate (StatDamage/StatDamageMax) from <c>uniform(0, ceiling)</c>, floored so
    /// condition stays ≥ <see cref="MinCondition"/>. Deterministic given <paramref name="rng"/>.</summary>
    public static double SampleDamageRate(Random rng, double ceiling) =>
        Math.Min(rng.NextDouble() * ceiling, MaxDamageRate);

    /// <summary>One part's damage <b>amount</b> (in <c>StatDamageMax</c> count units) for a part whose health pool
    /// is <paramref name="statDamageMax"/> — i.e. <see cref="SampleDamageRate"/> × M. Returns 0 for an
    /// undamageable part (M ≤ 0).</summary>
    public static double DamageAmount(Random rng, double ceiling, double statDamageMax) =>
        statDamageMax > 0.0 ? SampleDamageRate(rng, ceiling) * statDamageMax : 0.0;

    /// <summary>The Ship Rating "Condition" grade (A–E) for a set of per-part damage rates — the mean of
    /// <c>clamp01(1 − rate)</c>, graded by <see cref="Rating.ConditionGrade"/>. An empty set (no installed parts)
    /// is pristine (A). Undamageable parts should be included as rate 0 so the mean matches the game's
    /// all-installed-parts denominator.</summary>
    public static string GradeFor(IReadOnlyCollection<double> damageRates) =>
        damageRates.Count == 0
            ? "A"
            : Rating.ConditionGrade(damageRates.Average(r => Math.Clamp(1.0 - r, 0.0, 1.0)));

    /// <summary>A fresh RNG for a wear pass — seeded from <paramref name="options"/> for reproducibility, else
    /// time-based.</summary>
    public static Random NewRng(WearOptions options) =>
        options.Seed is { } s ? new Random(s) : new Random();
}
