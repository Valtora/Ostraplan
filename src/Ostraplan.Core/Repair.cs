namespace Ostraplan.Core;

/// <summary>
/// "Repair" / "Repair All": swap every part that is <b>broken as a def</b> — a damaged wall, a wrecked alarm, a
/// patched hull plate — for the working def the game's own repair job yields, in place, keeping its tile, rotation,
/// name and any cargo. The mapping is <see cref="Catalog.RepairForms"/>, read straight out of
/// <c>data/installables</c>, so nothing is invented: the working def brings its own sprite and conditions, and the
/// analysis engine recomputes rooms, certification and rating from them.
///
/// <para><b>This is one half of "100% health", and the design half.</b> A part carries damage two ways in
/// Ostranauts. It can accumulate <c>StatDamage</c> against its health pool, which is per-instance save state living
/// on a condition owner, has no representation in a design, and is cleared by the repair mode of
/// <see cref="WearOptions"/> when the design is written into a save. Or it can <i>be</i> a broken def in its own
/// right, which is a fact about the layout and travels in the <c>.oplan</c> like any other part choice — that is
/// this file. A design imported from a real ship routinely carries both.</para>
///
/// <para>Like <see cref="FormSwap"/>, whose <see cref="FormSwap.BuildSwap"/> does the actual work, a repair is a
/// <b>state</b> change rather than an identity change (<see cref="Placement.Restate"/>): the game reaches these defs
/// through damage and repair, never through a build job, so on a save edit the part is one the player already owns
/// and <see cref="EditCost"/> prices it as a move rather than billing it as newly conjured material.</para>
/// </summary>
public static class Repair
{
    /// <summary>The (unlocked) parts in the set that are broken and have a working counterpart, each paired with
    /// the def repairing it yields. Only targets that resolve to real geometry are returned, and a part that is
    /// already intact simply isn't in the result.</summary>
    public static IReadOnlyList<(Placement Part, string Target)> Repairable(
        ShipDocument doc, IReadOnlyList<Placement> parts)
    {
        var result = new List<(Placement, string)>();
        foreach (var p in parts)
            if (!doc.IsLocked(p) && doc.Catalog.RepairForm(p.DefName) is { } target
                && target != p.DefName && doc.Catalog.Lookup(target) is not null)
                result.Add((p, target));
        return result;
    }

    /// <summary>Every broken part on the ship, paired with its repaired def — what "Repair All" acts on.</summary>
    public static IReadOnlyList<(Placement Part, string Target)> RepairableAll(ShipDocument doc) =>
        Repairable(doc, doc.Placements);
}
