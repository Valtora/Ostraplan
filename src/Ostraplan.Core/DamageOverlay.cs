namespace Ostraplan.Core;

/// <summary>
/// One part as the damage heat map draws it: the tiles it occupies and how much of its life is left.
/// </summary>
/// <param name="PlacementId">The document placement, so clicking the tint selects the part at fault.</param>
/// <param name="Condition">1 untouched, 0 destroyed — the green-to-red scale. Measured against the part's
/// <b>whole</b> break chain (<see cref="Catalog.MaxHealth"/>), so a wall that has broken once reads two thirds
/// rather than snapping back to full the moment it changed form.</param>
/// <param name="Stages">How many times it has broken. 0 is untouched, and any positive value means the part on
/// the tile is no longer the part the design names.</param>
/// <param name="CurrentDef">The form it is in now, for the tooltip: a damaged wall is a different def from the
/// wall that was drawn there.</param>
/// <param name="Tiles">The part's <b>body</b> in document coords, which is what gets tinted: the object itself
/// rather than its socket clearance. See <see cref="ShipDocument.BodyBounds"/>.</param>
public sealed record DamagedPart(
    Guid PlacementId, double Condition, int Stages, bool Destroyed, string CurrentDef,
    IReadOnlyList<(int X, int Y)> Tiles);

/// <summary>
/// The damage heat map: every part a run of strikes has touched, tinted by what it has left.
///
/// <para>Unlike RoomViz or PowerViz this is <b>not</b> derived from the document alone, so it is not rebuilt by
/// the background analysis scan. It is a view over a <see cref="DamageState"/>, which is session state the user
/// drives one strike at a time, and it changes only when they fire. Building it is O(parts) with no flood fill,
/// so it is cheap enough to rebuild on the UI thread after every strike.</para>
///
/// <para>Only damaged parts appear. An untouched ship produces <see cref="Empty"/> and the canvas draws nothing,
/// which is what makes the overlay readable: the eye goes to the handful of parts that took the hit rather than
/// to a wash of green over the whole plan.</para>
/// </summary>
public sealed record DamageOverlay(IReadOnlyList<DamagedPart> Parts, int Destroyed, double WorstCondition)
{
    public static readonly DamageOverlay Empty = new([], 0, 1.0);

    public bool IsEmpty => Parts.Count == 0;

    /// <summary>Build the heat map for the damage accumulated so far.</summary>
    public static DamageOverlay Build(ShipDocument doc, DamageState state)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(state);
        if (state.IsPristine) return Empty;

        var parts = new List<DamagedPart>();
        var destroyed = 0;
        var worst = 1.0;

        foreach (var p in doc.Placements)
        {
            if (state.For(p) is not { } d) continue;
            if (doc.Part(p) is not { } def) continue;

            var condition = state.Condition(p, doc.Catalog);
            if (d.Destroyed) destroyed++;
            if (condition < worst) worst = condition;

            // The BODY, not the socket. A part's footprint is its clearance, which for the LHe canisters is 7×7
            // around a 3×3 object, so tinting the socket painted a 49-tile block of deck for a tank that absorbed
            // on one cell and read as though the whole bay had gone. The body is what the eye is looking at, it is
            // what the selection outline already uses, and it is exactly the collider a micrometeoroid raycasts.
            var (bx, by, bw, bh) = doc.BodyBounds(p);
            var tiles = new List<(int X, int Y)>(bw * bh);
            for (var dy = 0; dy < bh; dy++)
                for (var dx = 0; dx < bw; dx++)
                    tiles.Add((bx + dx, by + dy));

            parts.Add(new DamagedPart(p.Id, condition, d.Stages, d.Destroyed, d.Def, tiles));
        }

        // Worst first, so a UI listing them leads with what actually broke.
        parts.Sort((a, b) => a.Condition.CompareTo(b.Condition));
        return new DamageOverlay(parts, destroyed, worst);
    }
}
