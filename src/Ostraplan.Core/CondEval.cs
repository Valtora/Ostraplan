namespace Ostraplan.Core;

/// <summary>
/// Port of <c>CondTrigger.Triggered</c> (verified 0.15.1.6) evaluated against a
/// CondOwner's <b>condition set</b> — presence means the cond is on the owner with
/// count &gt; 0 (installed parts carry their starting conds at ≥1, none IsDamaged in a
/// pristine planner ship). Room certification needs this, unlike CheckFit/autotile
/// which only reach the presence path (<see cref="TileConds.Triggered"/>).
///
/// <para>Ported branches: the <c>bAND</c> path (all <c>aReqs</c> present, no
/// <c>aForbids</c> present, all nested <c>aTriggers</c> satisfied) and the
/// <c>bAND=false</c> OR path (no <c>aForbids</c>, all <c>aTriggersForbid</c> satisfied,
/// then any <c>aReqs</c> present or any <c>aTriggers</c> satisfied). A blank trigger is
/// true.</para>
///
/// <para><b>Safe fallback</b> for branches unreachable from room specs: <c>fChance</c>
/// (&lt;1 randomness) evaluates as if the roll passed — deterministic; and
/// <c>strHigherCond</c>/<c>aLowerConds</c> (ranking on condition counts) evaluates as a
/// pass. Both are recorded in <paramref name="notes"/> so a surprise surfaces in the law
/// report rather than silently deciding.</para>
/// </summary>
public static class CondEval
{
    private const int MaxDepth = 12;

    public static bool Triggered(CondTriggerDef ct, IReadOnlyCollection<string> conds, Catalog catalog,
        ICollection<string>? notes = null)
    {
        var set = conds as HashSet<string> ?? new HashSet<string>(conds, StringComparer.Ordinal);
        return Eval(ct, set, catalog, notes, 0);
    }

    private static bool Eval(CondTriggerDef ct, HashSet<string> conds, Catalog catalog, ICollection<string>? notes, int depth)
    {
        if (depth > MaxDepth) return false;   // matches the game's recursion guard intent

        // IsBlank → true (an empty trigger constrains nothing)
        if (ct.Reqs.Length == 0 && ct.Forbids.Length == 0 && ct.Triggers.Length == 0 && ct.TriggersForbid.Length == 0)
            return true;

        if (ct.FChance < 1.0) notes?.Add($"{ct.Name}: fChance {ct.FChance} treated as pass");
        if (ct.HigherCond is not null) notes?.Add($"{ct.Name}: strHigherCond ranking treated as pass");

        if (ct.BAnd)
        {
            foreach (var req in ct.Reqs)
                if (!conds.Contains(req)) return false;
            foreach (var forbid in ct.Forbids)
                if (conds.Contains(forbid)) return false;
            foreach (var trig in ct.Triggers)
                if (!Nested(trig, conds, catalog, notes, depth)) return false;
            return true;
        }

        // OR path (bAND == false)
        foreach (var forbid in ct.Forbids)
            if (conds.Contains(forbid)) return false;
        foreach (var tf in ct.TriggersForbid)
            if (!Nested(tf, conds, catalog, notes, depth)) return false;   // all "forbid" triggers must hold (game semantics)
        foreach (var req in ct.Reqs)
            if (conds.Contains(req)) return true;
        foreach (var trig in ct.Triggers)
            if (Nested(trig, conds, catalog, notes, depth)) return true;
        return ct.Reqs.Length + ct.Triggers.Length == 0;   // no positive terms ⇒ vacuously true
    }

    private static bool Nested(string name, HashSet<string> conds, Catalog catalog, ICollection<string>? notes, int depth)
    {
        if (!catalog.Triggers.TryGetValue(name, out var nested))
        {
            // an unresolved nested trigger name behaves like a bare condition (game:
            // GetCondTrigger auto-wraps a lone cond name into a one-req trigger)
            return conds.Contains(name);
        }
        return Eval(nested, conds, catalog, notes, depth + 1);
    }
}
