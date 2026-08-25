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
    /// <c>|CurrentDamage − GetMaxHealth()| &lt; ε</c>, never asking what form it ended up in. So a part can be
    /// spent without <see cref="IsDestroyed"/> ever being reached: one driven to <see cref="Catalog.MaxHealth"/>
    /// by the projectile solver's whole-chain pricing has nothing left to give whatever form it is sitting in.
    /// Reading destroyed alone left such a part standing as an obstacle for ever, and since a bin carries
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
    /// whatever damage overflowed.</para>
    ///
    /// <para><b>A break that leaves loose debris destroys the part.</b> A chain does not have to end in nothing
    /// for the ship to have lost a part. <c>ItmWall1x1</c> breaks into <c>ItmWall1x1Dmg</c>, which is still
    /// <c>IsInstalled</c> and still carries <c>IsWall</c>: the ship changed, but a part is still standing there.
    /// <c>ItmStorageBin2x101</c> ends as <c>ItmScrapTrash</c> and <c>ItmCanisterLHe02</c> as
    /// <c>ItmScrapAluminum</c>, both of which the catalog can name and neither of which is installed. Asking
    /// whether the break form <i>has</i> a name cannot tell those two apart, so it read a heap of scrap as a part
    /// that had merely broken — which is how it was reported ("a storage bay becoming aluminium should count as
    /// destroyed, not damaged, and yet it's highlighted yellow").</para>
    ///
    /// <para>The test is <see cref="Catalog.IsInstalledForm"/>, which is the line the game's own
    /// <c>DataCO.GetMaxHealth</c> stops its chain walk at, so the two agree about where a part's life ends.
    /// <see cref="PartDamage.Def"/> still carries the debris so a report can say what was left behind; what
    /// changes is that the design no longer owns anything on that tile. Both solvers pass over it from then on,
    /// which is what <see cref="IsSpent"/> already did for the projectile one: before this the two disagreed and a
    /// micrometeoroid went on chewing through the scrap's own pool.</para>
    /// </summary>
    /// <returns>Whether the part broke, the form it broke into (null when it broke into nothing the game names),
    /// and whether the ship has lost the part outright.</returns>
    public (bool Broke, string? ToDef, bool Gone) Apply(Placement p, string fromDef, double amount, Catalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var prior = _parts.GetValueOrDefault(p.Id);
        var stages = prior?.Stages ?? 0;
        var damage = (prior?.Damage ?? 0) + amount;
        var ceiling = catalog.Health(fromDef);

        if (ceiling <= 0 || damage < ceiling)
        {
            _parts[p.Id] = new PartDamage(fromDef, damage, stages, Destroyed: false);
            return (false, null, false);
        }

        // The pool filled. The game subtracts the whole ceiling rather than clamping, so the overflow carries into
        // the next form — which matters for a chain, where one large hit can cross more than one stage. There is
        // nothing to carry it into once the part is gone, so the overflow is dropped with it.
        var next = catalog.BreakForm(fromDef);
        var gone = next is null || !catalog.IsInstalledForm(next);
        _parts[p.Id] = new PartDamage(next ?? fromDef, gone ? 0 : damage - ceiling, stages + 1, gone);
        return (true, next, gone);
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

    /// <summary>
    /// The ship as this run of strikes has left it: an independent document with each broken part replaced by the
    /// form it is in now, and each destroyed part gone.
    ///
    /// <para><b>Why this exists.</b> A strike's own report can only count parts, and a count cannot tell a dent
    /// from a hole. What a designer actually asked was whether the hit opened a compartment to vacuum or cut a
    /// device off from the crew, and those are the questions the ordinary design checks already answer: they are
    /// static properties of a layout, so asking them of the damaged layout needs no simulation at all. This is the
    /// layout to ask.</para>
    ///
    /// <para><b>It is a projection, not an edit.</b> Nothing here touches the document the user is working on, and
    /// nothing produced here can reach the <c>.oplan</c>. A design carries no wear (§12), and the scope line that
    /// admits a single impact is about <i>measuring</i> a layout rather than storing a damaged one: what follows a
    /// strike over time is still a simulation and still out of scope. Answering "what would be broken about this
    /// ship the instant after" is the same one-off measurement the strike itself is.</para>
    ///
    /// <para>A part that broke into a form the catalog does not name is dropped rather than left standing as its
    /// old self, which would have the wreck go on sealing a compartment it no longer seals.</para>
    ///
    /// <para><b>Each surviving part keeps its <see cref="Placement.Id"/>.</b> A projection is the same ship with
    /// damage on it, so a part in it is the <i>same</i> part, and anything comparing the two hulls needs to be able
    /// to say so. <see cref="ShipDocument.Snapshot"/> deliberately does not do this — it mints fresh ids, because
    /// what it produces is an independent document rather than a view of this one — which is why
    /// <see cref="DamageFallout"/> is handed projections at both ends and never a snapshot. A pristine state
    /// projects a document unchanged, which is how it gets the intact side.</para>
    /// </summary>
    public ShipDocument Project(ShipDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var copy = new ShipDocument(doc.Catalog);
        foreach (var p in doc.Placements)
        {
            var d = _parts.GetValueOrDefault(p.Id);
            if (d?.Destroyed == true) continue;
            var def = d?.Def ?? p.DefName;
            // A form the catalog cannot resolve is not a part any more. Keeping the original in its place would
            // make the projection claim structure the strike removed.
            if (doc.Catalog.Lookup(def) is null) continue;
            copy.Add(new Placement
            {
                Id = p.Id,
                DefName = def, X = p.X, Y = p.Y, Rot = p.Rot, IsGiven = p.IsGiven,
                OriginStrID = p.OriginStrID, SwappedFromStrID = p.SwappedFromStrID, SwappedFromDef = p.SwappedFromDef,
            });
        }
        // Zones ride along: a Forbid zone changes what the walk analysis admits, so dropping them would make the
        // damaged ship answer a different question from the intact one.
        foreach (var z in doc.Zones) copy.AddZone(z);
        return copy;
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
