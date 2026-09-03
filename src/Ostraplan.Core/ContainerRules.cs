namespace Ostraplan.Core;

/// <summary>One condition named by a rule, with the game's own wording for it where the data carries any.</summary>
/// <param name="Cond">The raw condition name, which is the handle a modder writes against.</param>
/// <param name="Friendly">The readable name (<c>IsLong</c> is "Long"), or null when nothing names it.</param>
/// <param name="Desc">The game's description with its grammar tokens stripped ("1m or longer, making it difficult
/// to stow in some containers"), or null.</param>
public sealed record RuleCond(string Cond, string? Friendly, string? Desc)
{
    /// <summary>What to print for this condition: its readable name, falling back to the raw one.</summary>
    public string Label => Friendly ?? Cond;
}

/// <summary>One line of the readable summary: a label and the sentence under it.</summary>
public sealed record RuleSummary(string Label, string Text);

/// <summary>
/// One trigger in a container's filter, as a tree the view can print.
/// </summary>
/// <param name="TriggerName">The <c>CondTrigger</c> this node is, for a modder to go and find.</param>
/// <param name="All">The trigger's <c>bAND</c>. True means every clause must hold; false means the requirements
/// are alternatives and any one of them is enough. Getting this wrong inverts the rule, and half the shipped
/// container filters take the false path.</param>
/// <param name="Requires">Conditions the item must carry (all of them, or any of them, per <see cref="All"/>).</param>
/// <param name="Forbids">Conditions that disqualify the item. Always an AND-of-nots on both paths.</param>
/// <param name="Nested">Trigger clauses evaluated the same way as <see cref="Requires"/>.</param>
/// <param name="NestedForbid">The OR path's <c>aTriggersForbid</c>, every one of which must hold.</param>
/// <param name="Unresolved">Nested names with no trigger behind them. The game auto-wraps a bare condition name
/// into a one-requirement trigger, so these read as plain conditions.</param>
public sealed record RuleNode(
    string TriggerName,
    bool All,
    IReadOnlyList<RuleCond> Requires,
    IReadOnlyList<RuleCond> Forbids,
    IReadOnlyList<RuleNode> Nested,
    IReadOnlyList<RuleNode> NestedForbid,
    IReadOnlyList<RuleCond> Unresolved)
{
    /// <summary>True when the node constrains nothing, which the game reads as a pass.</summary>
    public bool Blank =>
        Requires.Count == 0 && Forbids.Count == 0 && Nested.Count == 0
        && NestedForbid.Count == 0 && Unresolved.Count == 0;
}

/// <summary>
/// What a container will and will not hold, as something that can be read rather than only applied.
///
/// <para>Ostraplan has always evaluated this: the <b>Add item</b> picker offers only defs whose starting conds
/// satisfy the container's <c>strContainerCT</c> (see <see cref="ContainerFilter"/>), and a drop that crosses into
/// another container is held to the same test. It was never shown, so there was no way to see why a pouch takes a
/// battery and refuses a crate, which is the question a modder writing a new container has (#61).</para>
///
/// <para><b>A filter is not a whitelist and a blacklist.</b> The shipped ones nest up to three levels and half of
/// them set <c>bAND: false</c>, where the requirements are alternatives rather than all required.
/// <c>TIsFitContainerNavMod</c> requires <c>IsNavMod</c> <b>or</b> <c>IsExplosion</c>; printed as a flat list it
/// would read as demanding both, which no item carries. So this is a tree with the conjunction on every node, and
/// <see cref="RuleNode.All"/> is the field that must not be dropped.</para>
/// </summary>
/// <param name="Def">The container this describes.</param>
/// <param name="Root">The filter, or null when the container names none (or names one that is not loaded), which
/// the game and <see cref="ContainerFilter"/> both read as accepting anything.</param>
/// <param name="Accepted">How many of the catalogue's loose items pass the filter.</param>
/// <param name="Offered">How many loose items there are to pass it, so the count above has a denominator.</param>
/// <param name="Notes">Anything <see cref="CondEval"/> could not decide exactly and took the safe branch on.</param>
public sealed record ContainerRules(
    PartDef Def,
    RuleNode? Root,
    int Accepted,
    int Offered,
    IReadOnlyList<string> Notes)
{
    /// <summary>True when nothing constrains what goes in, so the view can say so in one line.</summary>
    public bool HoldsAnything => Root is null || Root.Blank;

    /// <summary>
    /// The filter as a couple of sentences, for a reader who wants to know what fits rather than how the rule is
    /// written.
    ///
    /// <para><b>Forbids merge and requirements do not.</b> A forbid is an and-of-nots on both of the game's paths,
    /// at every level of the tree, so flattening every one of them into a single "won't hold" list cannot change
    /// the meaning. A requirement can be an <c>or</c>, so each node keeps its own line and its own conjunction;
    /// merging those is exactly the mistake that would turn <c>TIsFitContainerNavMod</c> into a container that
    /// holds nothing.</para>
    ///
    /// <para>The tree itself is still there in <see cref="Root"/>. This is the reading of it, not a replacement:
    /// a modder needs the trigger names and the raw conds, and a person stocking a ship needs a sentence.</para>
    /// </summary>
    public IReadOnlyList<RuleSummary> Plain
    {
        get
        {
            if (Root is null || HoldsAnything) return [];
            var lines = new List<RuleSummary>();

            var forbids = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            Collect(Root);
            if (forbids.Count > 0) lines.Add(new RuleSummary("Won't hold", Sentence(forbids, "or")));
            Require(Root);
            return lines;

            void Collect(RuleNode n)
            {
                foreach (var f in n.Forbids)
                    if (seen.Add(f.Cond)) forbids.Add(f.Label);
                foreach (var c in n.Nested) Collect(c);
                foreach (var c in n.NestedForbid) Collect(c);
            }

            void Require(RuleNode n)
            {
                var reqs = n.Requires.Concat(n.Unresolved).Select(r => r.Label).ToList();
                if (reqs.Count > 0)
                    lines.Add(new RuleSummary("Must be", Sentence(reqs, n.All ? "and" : "or")));
                foreach (var c in n.Nested) Require(c);
                foreach (var c in n.NestedForbid) Require(c);
            }
        }
    }

    /// <summary>"A", "A or B", "A, B or C" — the conjunction spelled out, because a list joined with a separator
    /// reads as neither an "and" nor an "or" and the difference is the whole rule.</summary>
    private static string Sentence(IReadOnlyList<string> parts, string conjunction) => parts.Count switch
    {
        0 => "",
        1 => parts[0],
        _ => string.Join(", ", parts.Take(parts.Count - 1)) + $" {conjunction} " + parts[^1],
    };

    private const int MaxDepth = 8;

    /// <summary>Describe what <paramref name="def"/> accepts. Cheap enough to call per render: the filter walk is
    /// a handful of nodes, and the accepted count is the same pass the add-picker already makes.</summary>
    public static ContainerRules For(Catalog catalog, PartDef def)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(def);

        var notes = new List<string>();
        var root = def.ContainerCT is { } name && catalog.Triggers.TryGetValue(name, out var ct)
            ? Build(catalog, ct, notes, depth: 0)
            : null;

        // Reuse the picker's own pass rather than re-deriving it, so the count can never disagree with the list
        // the user is then offered.
        var offered = catalog.LooseItems.Count;
        var accepted = root is null ? offered : ContainerFilter.AcceptedBy(catalog, def).Count;

        return new ContainerRules(def, root, accepted, offered, notes);
    }

    private static RuleNode Build(Catalog catalog, CondTriggerDef ct, List<string> notes, int depth)
    {
        // The two branches CondEval cannot evaluate exactly. It takes the safe one and records why; saying so here
        // as well means a filter that is not wholly deterministic reads as such instead of looking definitive.
        if (ct.FChance < 1.0) notes.Add($"{ct.Name} fires {ct.FChance:P0} of the time; treated as always.");
        if (ct.HigherCond is not null) notes.Add($"{ct.Name} ranks conditions by amount; treated as satisfied.");

        var nested = new List<RuleNode>();
        var unresolved = new List<RuleCond>();
        var nestedForbid = new List<RuleNode>();

        void Walk(string[] names, List<RuleNode> into)
        {
            foreach (var n in names)
            {
                if (depth < MaxDepth && catalog.Triggers.TryGetValue(n, out var sub))
                    into.Add(Build(catalog, sub, notes, depth + 1));
                else
                    // GetCondTrigger wraps a lone cond name into a one-req trigger, so an unresolved name is a
                    // condition rather than a fault. CondEval reads it the same way.
                    unresolved.Add(Cond(catalog, n));
            }
        }

        Walk(ct.Triggers, nested);
        if (!ct.BAnd) Walk(ct.TriggersForbid, nestedForbid);

        return new RuleNode(
            ct.Name,
            ct.BAnd,
            [.. ct.Reqs.Select(c => Cond(catalog, c))],
            [.. ct.Forbids.Select(c => Cond(catalog, c))],
            nested,
            nestedForbid,
            unresolved);
    }

    private static RuleCond Cond(Catalog catalog, string cond) =>
        catalog.CondNames.TryGetValue(cond, out var d)
            ? new RuleCond(cond, d.Friendly, d.Plain)
            : new RuleCond(cond, null, null);
}

/// <summary>
/// What an equipment slot takes.
///
/// <para><b>A slot filters nothing of its own.</b> The gate is on the item: <c>Slot.CanFit</c> asks
/// <c>coFit.mapSlotEffects.ContainsKey(strName)</c>, so an item goes in a slot by naming that slot, and the host
/// only has to declare the slot in <c>aSlotsWeHave</c>. Ostraplan ports both sides already, as
/// <see cref="PartDef.SlotKeys"/> and <see cref="PartDef.SlotsWeHave"/>.</para>
///
/// <para>The game does have a per-slot trigger, <c>strCTAutoSlot</c>, read by <c>Slot.CanAutoSlot</c>. It is not
/// ported and should not be: all 40 slots that declare one are <b>wound</b> slots, every one of them naming
/// <c>TIsAutoSlotWound</c>, and wounds are anatomy rather than storage (see <see cref="Cargo.CanHoldCargo"/>). Not
/// one equipment slot in the game filters anything, so porting it would show a rule that never governs a design.
/// If a mod ever gives an equipment slot a filter, this is the note to revisit.</para>
/// </summary>
/// <param name="Slot">The slot's raw name, which is the key an item declares to fit it.</param>
/// <param name="Friendly">The slot's readable name, falling back to the raw one.</param>
/// <param name="Fits">How many defs in the catalogue declare this slot.</param>
/// <param name="Examples">A few of them by name, so the count is not the whole answer.</param>
public sealed record SlotRules(string Slot, string Friendly, int Fits, IReadOnlyList<string> Examples)
{
    private const int MaxExamples = 6;

    public static SlotRules For(Catalog catalog, string slot)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        // LooseItems rather than Parts: a pocket is not a buildable part and never reaches the palette, so it is
        // absent from Parts and ByDefName alike. LooseItems is the same universe the add-picker draws from, which
        // is the right one here too.
        var takers = catalog.LooseItems
            .Where(p => Array.IndexOf(p.SlotKeys, slot) >= 0)
            .Select(p => p.Friendly ?? p.DefName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        return new SlotRules(
            slot,
            catalog.Slots.GetValueOrDefault(slot)?.Friendly ?? slot,
            takers.Count,
            takers.Take(MaxExamples).ToList());
    }
}
