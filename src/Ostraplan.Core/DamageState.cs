namespace Ostraplan.Core;

/// <summary>How far through its break chain one part has been driven, and how much damage sits on the form it is
/// in now.</summary>
/// <param name="Def">The form the part is in after everything applied so far. Equal to the placement's own def
/// until something breaks it.</param>
/// <param name="Damage">Damage accumulated against <paramref name="Def"/>'s own pool, always below it — filling
/// the pool breaks the part rather than resting at the ceiling.</param>
/// <param name="Stages">How many times this part has broken. 0 is untouched.</param>
/// <param name="Destroyed">True once it broke into nothing the game names.</param>
public sealed record PartDamage(string Def, double Damage, int Stages, bool Destroyed);

/// <summary>
/// Accumulated strike damage across a run, keyed by placement.
///
/// <para><b>Deliberately not part of the document.</b> A design carries no wear: <c>StatDamage</c> is per-instance
/// save state and no def declares it (§12), and the scope line that puts a single impact in scope
/// (docs/SCOPE.md) is about measuring a layout, not about storing a damaged one. So this is session state that
/// lives beside the document, never in it and never in the <c>.oplan</c>. Discarding it restores a pristine ship,
/// which is what makes "fire again" and "start over" the same cheap operation.</para>
///
/// <para>One strike advances a part at most one stage, because that is what the micrometeoroid path does: the
/// physics hit list is built before anything breaks, so a part that mode-switches mid-walk is not revisited for
/// its new form's pool. Firing repeatedly at the same angle is how a wall goes whole → damaged → gone, and is why
/// this accumulates rather than being recomputed per strike.</para>
/// </summary>
public sealed class DamageState
{
    private readonly Dictionary<Guid, PartDamage> _parts = [];

    /// <summary>True when nothing has been hit yet — a pristine ship, and the state a single-strike worst case
    /// starts from.</summary>
    public bool IsPristine => _parts.Count == 0;

    /// <summary>Every part carrying damage, for the heat overlay to paint.</summary>
    public IReadOnlyDictionary<Guid, PartDamage> Parts => _parts;

    /// <summary>What this part has taken, or null when it is untouched.</summary>
    public PartDamage? For(Placement p) => _parts.GetValueOrDefault(p.Id);

    /// <summary>The form the part is in now: whatever it has broken into, or its own def while intact.</summary>
    public string CurrentDef(Placement p) => _parts.GetValueOrDefault(p.Id)?.Def ?? p.DefName;

    /// <summary>Damage sitting on the current form's own pool.</summary>
    public double DamageOn(Placement p) => _parts.GetValueOrDefault(p.Id)?.Damage ?? 0;

    /// <summary>True once the part has broken into nothing the game names.</summary>
    public bool IsDestroyed(Placement p) => _parts.GetValueOrDefault(p.Id)?.Destroyed ?? false;

    /// <summary>
    /// True once this part has absorbed everything its break chain can take, and so is passed over by the
    /// projectile solver (§26).
    ///
    /// <para><b>Not the same thing as destroyed, and it is this rather than destroyed that the game tests.</b>
    /// Both <c>FindPointsOfImpact</c> and <c>ApplyDamageToCell</c> skip a part on
    /// <c>|CurrentDamage − GetMaxHealth()| &lt; ε</c>, never asking what form it ended up in. The two part
    /// company whenever a chain finishes on something the game still names: <c>ItmStorageBin2x101</c> ends as
    /// <c>ItmScrapTrash</c>, loose debris that is not installed and so is not counted by
    /// <see cref="Catalog.MaxHealth"/>, which leaves the bin full but never <see cref="IsDestroyed"/>. Reading
    /// destroyed alone left such a part standing as an obstacle for ever, and since a bin carries
    /// <c>IsRigid</c> that was enough to detonate every later missile on the same tile as the first.</para>
    ///
    /// <para>A part that declares no damage pool at all is spent from the start, both here and in the game: its
    /// max health is zero, so the test passes on an untouched part and a strike goes straight through it.</para>
    /// </summary>
    public bool IsSpent(Placement p, Catalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return IsDestroyed(p) || TotalDamage(p, catalog) >= catalog.MaxHealth(p.DefName) - 0.01;
    }

    /// <summary>
    /// True when the part reads as damaged, the game's <c>DataCOWrapper.IsDamaged</c>: either its def says so
    /// outright, or it has taken at least its first form's own pool.
    ///
    /// <para>What it governs is the soft edge. <c>damageOnly</c> caps a cell at <see cref="Catalog.Health"/>
    /// instead of <see cref="Catalog.MaxHealth"/> <b>only while the part is still whole</b>; once it is damaged
    /// the cap comes off and even point-defence fire prices it against the whole chain. So 20mm cannot take a
    /// part from whole to gone, but it can finish one that something else already opened up, and it can do it
    /// on its own second pass.</para>
    /// </summary>
    public bool IsDamaged(Placement p, Catalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.Lookup(p.DefName)?.StartingConds.Contains("IsDamaged") is true) return true;
        var total = TotalDamage(p, catalog);
        return total > 0 && total >= catalog.Health(p.DefName);
    }

    /// <summary>Throw the run away and start from a pristine ship.</summary>
    public void Clear() => _parts.Clear();

    /// <summary>A copy, so a speculative strike can be resolved without committing it.</summary>
    public DamageState Snapshot()
    {
        var copy = new DamageState();
        foreach (var (k, v) in _parts) copy._parts[k] = v;
        return copy;
    }

    /// <summary>
    /// Apply <paramref name="amount"/> to a part sitting in <paramref name="fromDef"/> and report what happened.
    ///
    /// <para>Filling the form's pool breaks it: the game's <c>DestCheck.DamageCheck</c> fires the break
    /// interaction, subtracts the ceiling and clears <c>IsPristine</c>, so the part lands in its next form with
    /// whatever damage overflowed. A form that breaks into nothing the catalog names is destroyed and stops
    /// absorbing.</para>
    /// </summary>
    /// <returns>Whether the part broke, and the form it broke into (null when destroyed or unchanged).</returns>
    public (bool Broke, string? ToDef) Apply(Placement p, string fromDef, double amount, Catalog catalog)
    {
        var prior = _parts.GetValueOrDefault(p.Id);
        var stages = prior?.Stages ?? 0;
        var damage = (prior?.Damage ?? 0) + amount;
        var ceiling = catalog.Health(fromDef);

        if (ceiling <= 0 || damage < ceiling)
        {
            _parts[p.Id] = new PartDamage(fromDef, damage, stages, Destroyed: false);
            return (false, null);
        }

        // The pool filled. The game subtracts the whole ceiling rather than clamping, so the overflow carries into
        // the next form — which matters for a chain, where one large hit can cross more than one stage.
        var next = catalog.BreakForm(fromDef);
        _parts[p.Id] = new PartDamage(next ?? fromDef, next is null ? 0 : damage - ceiling, stages + 1, next is null);
        return (true, next);
    }

    /// <summary>
    /// This part's condition as a fraction of what it can take before it is gone: 1 untouched, 0 destroyed. The
    /// heat overlay's scale, and deliberately measured against the <b>whole break chain</b>
    /// (<see cref="Catalog.MaxHealth"/>) rather than the current form's pool, so a wall reads two thirds rather
    /// than jumping back to full the moment it breaks.
    /// </summary>
    public double Condition(Placement p, Catalog catalog)
    {
        var max = catalog.MaxHealth(p.DefName);
        if (max <= 0) return 1;
        return Math.Clamp(1 - TotalDamage(p, catalog) / max, 0, 1);
    }

    /// <summary>Everything this part has absorbed across every stage, in the original form's terms.</summary>
    public double TotalDamage(Placement p, Catalog catalog)
    {
        if (_parts.GetValueOrDefault(p.Id) is not { } d) return 0;
        // Walk the stages it has been through, adding each form's full pool, then the damage on the form it is in.
        var total = 0.0;
        var def = p.DefName;
        for (var i = 0; i < d.Stages; i++)
        {
            total += catalog.Health(def);
            if (catalog.BreakForm(def) is not { } next) break;
            def = next;
        }
        return total + d.Damage;
    }
}
