namespace Ostraplan.Core;

/// <summary>
/// What a strike run has actually done to a part, in the terms the game changes it in.
///
/// <para>These are <b>states</b>, not bands of a scale. The game does not degrade a part continuously: it fills
/// the current form's pool and then replaces the object outright (<c>CondOwner.ModeSwitch</c>), so at any moment a
/// part either is still the thing the design names, or is a different thing, or is not there. A reader shown a
/// green-to-red ramp has to guess where the thresholds are and will guess wrong, because there are none: the ramp
/// is continuous and the thing it describes is not.</para>
/// </summary>
public enum DamageGrade
{
    /// <summary>Carrying damage, still the part the design names. Nothing about the ship has changed except how
    /// much more it can take.</summary>
    Chipped,

    /// <summary>It broke, and what stands there now is a different def from the one that was drawn. This is the
    /// state that actually changes a design, and the one a summary counting "parts damaged" hides.</summary>
    Broken,

    /// <summary>The ship has lost the part. The design owns nothing on that tile any more, whether the break
    /// ended in nothing at all or in loose debris the game still names (see <see cref="DamageState.Apply"/>).</summary>
    Destroyed,
}

/// <summary>
/// One part as the damage overlay draws it: where it is, what state it is in, and how much of its life is left.
/// </summary>
/// <param name="PlacementId">The document placement, so clicking the tint selects the part at fault.</param>
/// <param name="Grade">Which of the game's three outcomes this part is in. The overlay's primary channel.</param>
/// <param name="Condition">1 untouched, 0 destroyed. Measured against the part's <b>whole</b> break chain
/// (<see cref="Catalog.MaxHealth"/>), so a wall that has broken once reads two thirds rather than snapping back to
/// full the moment it changed form. Secondary: it grades within a state rather than defining one.</param>
/// <param name="Stages">How many times it has broken. 0 is untouched, and any positive value means the part on the
/// tile is no longer the part the design names.</param>
/// <param name="OriginalDef">The form the design names, for saying what it used to be.</param>
/// <param name="CurrentDef">The form it is in now: a damaged wall is a different def from the wall that was
/// drawn there. On a <see cref="DamageGrade.Destroyed"/> part this is the wreckage it left, which is worth
/// naming even though the design no longer owns it.</param>
/// <param name="Body">The part's <b>body</b> in document coords as one rectangle, which is what gets drawn: the
/// object itself rather than its socket clearance. See <see cref="ShipDocument.BodyBounds"/>.</param>
public sealed record DamagedPart(
    Guid PlacementId, DamageGrade Grade, double Condition, int Stages, string OriginalDef, string CurrentDef,
    (int X, int Y, int W, int H) Body)
{
    /// <summary>True once it broke into nothing the game names.</summary>
    public bool Destroyed => Grade == DamageGrade.Destroyed;

    /// <summary>True when the part standing there is no longer the one the design names, destroyed included.</summary>
    public bool ChangedForm => Grade is DamageGrade.Broken or DamageGrade.Destroyed;

    /// <summary>The part's tiles, for anything that works a cell at a time. The body is always a rectangle, which
    /// is why <see cref="Body"/> is the form the drawing uses: one outline rather than a grid of them.</summary>
    public IEnumerable<(int X, int Y)> Tiles
    {
        get
        {
            for (var dy = 0; dy < Body.H; dy++)
                for (var dx = 0; dx < Body.W; dx++)
                    yield return (Body.X + dx, Body.Y + dy);
        }
    }
}

/// <summary>
/// The damage overlay: every part a run of strikes has touched, and what state each one is in.
///
/// <para>Unlike RoomViz or PowerViz this is <b>not</b> derived from the document alone, so it is not rebuilt by
/// the background analysis scan. It is a view over a <see cref="DamageState"/>, which is session state the user
/// drives one strike at a time, and it changes only when they fire. Building it is O(parts) with no flood fill,
/// so it is cheap enough to rebuild on the UI thread after every strike.</para>
///
/// <para>Only damaged parts appear. An untouched ship produces <see cref="Empty"/> and the canvas draws nothing,
/// which is what makes the overlay readable: the eye goes to the handful of parts that took the hit rather than
/// to a wash over the whole plan.</para>
/// </summary>
public sealed record DamageOverlay(IReadOnlyList<DamagedPart> Parts, int Destroyed, int Broken, double WorstCondition)
{
    public static readonly DamageOverlay Empty = new([], 0, 0, 1.0);

    public bool IsEmpty => Parts.Count == 0;

    /// <summary>Parts carrying damage that are still the part the design names.</summary>
    public int Chipped => Parts.Count - Broken - Destroyed;

    /// <summary>Parts that are no longer what the design names, destroyed included. The figure that answers "has
    /// this changed my ship", which neither a count of damaged parts nor a count of destroyed ones does.</summary>
    public int ChangedForm => Broken + Destroyed;

    /// <summary>Build the overlay for the damage accumulated so far.</summary>
    public static DamageOverlay Build(ShipDocument doc, DamageState state)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(state);
        if (state.IsPristine) return Empty;

        var parts = new List<DamagedPart>();
        var destroyed = 0;
        var broken = 0;
        var worst = 1.0;

        foreach (var p in doc.Placements)
        {
            if (state.For(p) is not { } d) continue;
            if (doc.Part(p) is null) continue;

            var condition = state.Condition(p, doc.Catalog);
            var grade = d.Destroyed ? DamageGrade.Destroyed
                      : d.Stages > 0 ? DamageGrade.Broken
                      : DamageGrade.Chipped;
            if (grade == DamageGrade.Destroyed) destroyed++;
            else if (grade == DamageGrade.Broken) broken++;
            if (condition < worst) worst = condition;

            // The BODY, not the socket. A part's footprint is its clearance, which for the LHe canisters is 7×7
            // around a 3×3 object, so drawing the socket painted a 49-tile block of deck for a tank that absorbed
            // on one cell and read as though the whole bay had gone. The body is what the eye is looking at, it is
            // what the selection outline already uses, and it is exactly the collider a micrometeoroid raycasts.
            parts.Add(new DamagedPart(p.Id, grade, condition, d.Stages, p.DefName, d.Def, doc.BodyBounds(p)));
        }

        // Worst first, so a UI listing them leads with what actually broke.
        parts.Sort((a, b) => a.Condition.CompareTo(b.Condition));
        return new DamageOverlay(parts, destroyed, broken, worst);
    }
}
