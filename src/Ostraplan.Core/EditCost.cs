namespace Ostraplan.Core;

/// <summary>The credit cost of an edit, broken down for display: how many parts were added / moved, the raw
/// base-value sums, the multiplier applied, and the resulting <see cref="Total"/>. Deletes are free and don't
/// appear here; a re-skin is counted as its new part (the diff models it as delete + new).</summary>
public sealed record EditCostBreakdown(
    int NewParts, int MovedParts, double NewValue, double MovedValue, double Multiplier, double Total);

/// <summary>
/// The "feel less cheaty" cost model for writing an edit back into a save: a player-set multiplier over the
/// base value of everything the edit added or moved. New parts count at full base value (you're conjuring them
/// outside the game's build economy); moved parts count at half (you already own them); deletes are free. The
/// multiplier is the player's tax knob — 0× makes edits free, higher makes them bite — and at the default of
/// <see cref="DefaultMultiplier"/> a new part costs 2× and a moved part 1× its base value, matching the
/// originally-specified premium. Base value is the part's <c>StatBasePrice</c> (see <see cref="PartDef.BasePrice"/>).
/// </summary>
public static class EditCost
{
    /// <summary>New parts count at full base value.</summary>
    public const double NewWeight = 1.0;

    /// <summary>Moved parts count at half base value (you already own them).</summary>
    public const double MovedWeight = 0.5;

    /// <summary>The slider's starting multiplier — 2× new / 1× moved, the specified premium.</summary>
    public const double DefaultMultiplier = 2.0;

    /// <summary>The slider's ceiling.</summary>
    public const double MaxMultiplier = 10.0;

    /// <summary>Cost the edit described by <paramref name="diff"/> at the given <paramref name="multiplier"/>,
    /// pricing each changed part from its <see cref="PartDef.BasePrice"/> (0 when a def has no price or can't
    /// resolve). Pure and deterministic.</summary>
    public static EditCostBreakdown Compute(ShipDiff diff, Catalog catalog, double multiplier)
    {
        double newValue = 0, movedValue = 0;
        int newParts = 0, movedParts = 0;
        foreach (var c in diff.Changes)
        {
            if (c.Placement is null) continue;   // deleted parts are free
            var price = catalog.Lookup(c.Placement.DefName)?.BasePrice ?? 0;
            if (c.Kind == PartChangeKind.New) { newValue += price; newParts++; }
            else if (c.Kind == PartChangeKind.Moved) { movedValue += price; movedParts++; }
        }
        var total = multiplier * (newValue * NewWeight + movedValue * MovedWeight);
        return new EditCostBreakdown(newParts, movedParts, newValue, movedValue, multiplier, total);
    }
}
