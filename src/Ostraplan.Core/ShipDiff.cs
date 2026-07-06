namespace Ostraplan.Core;

/// <summary>How one structural part changed between an edited document and the ship it was imported from.</summary>
public enum PartChangeKind
{
    /// <summary>Same origin id, same pose — written back verbatim (item + CO + cargo) on inject.</summary>
    Kept,
    /// <summary>Same origin id, new pose — repositioned, keeping its id / CO / cargo.</summary>
    Moved,
    /// <summary>No origin id (user-added, or produced by a def-changing edit) — a fresh item the game defaults on load.</summary>
    New,
    /// <summary>An origin id present at import but gone now — its item, CO and cargo subtree are dropped.</summary>
    Deleted,
}

/// <summary>One classified change. <see cref="Placement"/> is the current part (null when
/// <see cref="Kind"/> is <see cref="PartChangeKind.Deleted"/>); <see cref="OriginStrID"/> is the source
/// save item id (null when the part is <see cref="PartChangeKind.New"/>).</summary>
public sealed record PartChange(PartChangeKind Kind, string? OriginStrID, Placement? Placement);

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

    private ShipDiff(IReadOnlyList<PartChange> changes) => Changes = changes;

    public int KeptCount => Count(PartChangeKind.Kept);
    public int MovedCount => Count(PartChangeKind.Moved);
    public int NewCount => Count(PartChangeKind.New);
    public int DeletedCount => Count(PartChangeKind.Deleted);

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
                changes.Add(new PartChange(PartChangeKind.New, null, p));   // user-added, or its origin no longer resolves
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
