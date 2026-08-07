namespace Ostraplan.Core;

/// <summary>How one structural part changed between an edited document and the ship it was imported from.</summary>
public enum PartChangeKind
{
    /// <summary>Same origin id, same pose — written back verbatim (item + CO + cargo) on inject.</summary>
    Kept,
    /// <summary>Same origin id, new pose — repositioned, keeping its id / CO / cargo.</summary>
    Moved,
    /// <summary>No origin id (user-added, or produced by a def-changing edit) — a fresh item the game defaults on
    /// load. Covers a part that was only <i>re-stated</i> (uninstalled, installed, a door toggled): the item record
    /// still can't be reused, so the write-back is identical, but see <see cref="PartChange.Reformed"/> for why the
    /// cost model treats it differently.</summary>
    New,
    /// <summary>An origin id present at import but gone now — its item, CO and cargo subtree are dropped.</summary>
    Deleted,
}

/// <summary>One classified change. <see cref="Placement"/> is the current part (null when
/// <see cref="Kind"/> is <see cref="PartChangeKind.Deleted"/>); <see cref="OriginStrID"/> is the source
/// save item id (null when the part is <see cref="PartChangeKind.New"/>).</summary>
public sealed record PartChange(PartChangeKind Kind, string? OriginStrID, Placement? Placement)
{
    /// <summary>Set only on a <see cref="PartChangeKind.New"/> change that is <b>not</b> new material: the save id
    /// this part was before a state-changing swap re-stated it under another def (see
    /// <see cref="Placement.SwappedFromStrID"/>). The write-back still authors a fresh item for it — which is why
    /// the kind stays New — but it is a part the player already owns, so <see cref="EditCost"/> prices it as a
    /// move rather than as construction.</summary>
    public string? SwappedFromStrID { get; init; }

    /// <summary>True for a part that only changed state (uninstalled, installed, a door opened or shut) rather
    /// than being conjured.</summary>
    public bool Reformed => Kind == PartChangeKind.New && SwappedFromStrID is not null;
}

/// <summary>
/// The structural diff of an edited document against the ship it was imported from, classified per part by
/// identity (<see cref="Placement.OriginStrID"/>) and pose. This is the heart of the save-edit write-back
/// (Phase 2 consumes it to rebuild <c>aItems</c>/<c>aCOs</c>); Phase 1 only computes and reports it — it
/// <b>writes nothing</b>.
///
/// <para>Pure and identity-based, so it is unit-tested against a real save: a no-op import → all kept, moving
/// one part → one moved, deleting/adding → the matching class. Each non-null <see cref="Placement.OriginStrID"/>
/// is expected to be unique across the document (the identity-dropping edits guarantee it); a stray duplicate
/// would simply classify both placements and never resurface as a spurious delete.</para>
/// </summary>
public sealed class ShipDiff
{
    public IReadOnlyList<PartChange> Changes { get; }

    /// <summary>The origin ids whose item record is dropped only because a re-stated part took their place — the
    /// other half of an uninstall / install, not a deletion the user asked for.</summary>
    private readonly HashSet<string> _superseded;

    private ShipDiff(IReadOnlyList<PartChange> changes)
    {
        Changes = changes;
        _superseded = [.. changes.Where(c => c.Reformed).Select(c => c.SwappedFromStrID!)];
    }

    public int KeptCount => Count(PartChangeKind.Kept);
    public int MovedCount => Count(PartChangeKind.Moved);

    /// <summary>Parts the user removed. <b>Excludes</b> an origin superseded by a re-stated part: uninstalling a
    /// fixture drops its item record, but reporting that as a deletion alongside the part it became would count
    /// one act twice.</summary>
    public int DeletedCount =>
        Changes.Count(c => c.Kind == PartChangeKind.Deleted && !_superseded.Contains(c.OriginStrID!));

    /// <summary>Parts that are genuinely new material, <b>excluding</b> the re-stated ones counted by
    /// <see cref="ReformedCount"/>.</summary>
    public int NewCount => Changes.Count(c => c.Kind == PartChangeKind.New && !c.Reformed);

    /// <summary>Parts the player already owned that only changed state — uninstalled, installed, a door opened or
    /// shut (see <see cref="PartChange.Reformed"/>).</summary>
    public int ReformedCount => Changes.Count(c => c.Reformed);

    /// <summary>Everything the write-back has to author as a fresh item: new material and re-stated parts alike.
    /// The distinction between the two is about <i>cost</i>, not about how the save is written.</summary>
    public int FreshItemCount => Count(PartChangeKind.New);

    public IEnumerable<PartChange> OfKind(PartChangeKind kind) => Changes.Where(c => c.Kind == kind);
    private int Count(PartChangeKind kind) => Changes.Count(c => c.Kind == kind);

    /// <summary>Diff the document against its retained save context.</summary>
    public static ShipDiff Compute(ShipDocument doc, SaveShipContext context) => Compute(doc, context.Origins);

    /// <summary>Diff the document against the original structural parts (strID → imported pose).</summary>
    public static ShipDiff Compute(ShipDocument doc, IReadOnlyDictionary<string, OriginPart> origins)
    {
        var changes = new List<PartChange>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var p in doc.Placements)
        {
            if (p.OriginStrID is not { } id || !origins.TryGetValue(id, out var origin))
            {
                // user-added, or its origin no longer resolves. A part re-stated out of the save (uninstalled,
                // installed, a door toggled) still needs a fresh item, but carries the id it came from so the
                // cost model can tell it apart from conjured material.
                var from = p.SwappedFromStrID is { } s && origins.ContainsKey(s) ? s : null;
                changes.Add(new PartChange(PartChangeKind.New, null, p) { SwappedFromStrID = from });
                continue;
            }
            seen.Add(id);
            var moved = p.X != origin.X || p.Y != origin.Y || GridMath.Norm(p.Rot) != origin.Rot;
            changes.Add(new PartChange(moved ? PartChangeKind.Moved : PartChangeKind.Kept, id, p));
        }

        foreach (var id in origins.Keys)
            if (!seen.Contains(id))
                changes.Add(new PartChange(PartChangeKind.Deleted, id, null));

        return new ShipDiff(changes);
    }
}
