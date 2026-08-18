using System.Collections.Generic;
using System.Linq;

namespace Ostraplan.Core;

/// <summary>The credit cost of an edit, broken down for display: how many parts were added / moved, the raw
/// base-value sums, the two multipliers applied, and the resulting <see cref="Total"/>. Deletes are free and don't
/// appear here; a re-skin is counted as its new part (the diff models it as delete + new).</summary>
public sealed record EditCostBreakdown(
    int NewParts, int MovedParts, double NewValue, double MovedValue,
    double NewMultiplier, double MovedMultiplier, double Total)
{
    /// <summary>How many cargo items were authored into containers (see <see cref="CargoItem.Authored"/>) — each
    /// priced at full base value, like a new part, since you're conjuring it outside the game's economy. A stack's
    /// members count individually (a stack of 10 costs ten). Removed cargo is free, like a deleted part.</summary>
    public int NewCargo { get; init; }

    /// <summary>The summed base value of the authored cargo (before the multiplier).</summary>
    public double CargoValue { get; init; }

    /// <summary>How many parts only changed state — uninstalled, installed, a door opened or shut (see
    /// <see cref="PartChange.Reformed"/>). Counted apart from <see cref="MovedParts"/> so the readout can name
    /// them, but priced on the same multiplier: the player already owns them.</summary>
    public int ReformedParts { get; init; }

    /// <summary>The summed base value of the re-stated parts (before the multiplier).</summary>
    public double ReformedValue { get; init; }
}

/// <summary>
/// The "feel less cheaty" cost model for writing an edit back into a save: player-set multipliers over the base
/// value of everything the edit added or moved. Adding and moving are priced independently, because they are
/// different acts: a new part is conjured outside the game's build economy, while a moved part is one you already
/// own being put somewhere else. Deletes are free either way.
///
/// <para>"Moved" is the broader of the two, and covers any part you already own that merely changed: repositioned,
/// or <b>re-stated</b> — uninstalled to its packaged form, installed from one, a door opened or shut. The
/// write-back has to author a fresh item for a re-stated part (its def changed, so the save's item record can't be
/// reused), but that is a storage detail, not a purchase, and pricing it as construction was the bug behind
/// issue #19. What is <i>not</i> covered is a replace or re-skin: swapping a part for a genuinely different part
/// is new material, and stays on the added side.</para>
///
/// <para>The two multipliers are the player's tax knobs — 0× makes that side of the edit free, higher makes it
/// bite — and at the defaults (<see cref="DefaultNewMultiplier"/> and <see cref="DefaultMovedMultiplier"/>) a new
/// part costs 2× and a moved part 1× its base value, the originally-specified premium. Splitting them is what lets
/// a modular refit, where a lot of parts shift but nothing is really conjured, cost close to nothing (issue #19).
/// Base value is the part's <c>StatBasePrice</c> (see <see cref="PartDef.BasePrice"/>).</para>
/// </summary>
public static class EditCost
{
    /// <summary>The new-parts slider's starting multiplier.</summary>
    public const double DefaultNewMultiplier = 2.0;

    /// <summary>The moved-parts slider's starting multiplier, half the new-parts default: you already own it.</summary>
    public const double DefaultMovedMultiplier = 1.0;

    /// <summary>Both sliders' ceiling.</summary>
    public const double MaxMultiplier = 10.0;

    /// <summary>Cost the edit described by <paramref name="diff"/> at the given multipliers, pricing each changed
    /// part from its <see cref="PartDef.BasePrice"/> (0 when a def has no price or can't resolve). Authored cargo
    /// rides the new-parts multiplier, since it is conjured the same way. Pure and deterministic.</summary>
    public static EditCostBreakdown Compute(ShipDiff diff, Catalog catalog,
        double newMultiplier, double movedMultiplier, IEnumerable<LooseObject>? looseObjects = null)
    {
        double newValue = 0, movedValue = 0, reformedValue = 0, cargoValue = 0;
        int newParts = 0, movedParts = 0, reformedParts = 0, newCargo = 0;
        foreach (var c in diff.Changes)
        {
            if (c.Placement is null) continue;   // deleted parts are free
            var price = catalog.Lookup(c.Placement.DefName)?.BasePrice ?? 0;
            // a re-stated part is New to the write-back but not new material, so it is priced as a move
            if (c.Reformed) { reformedValue += price; reformedParts++; }
            else if (c.Kind == PartChangeKind.New) { newValue += price; newParts++; }
            else if (c.Kind == PartChangeKind.Moved) { movedValue += price; movedParts++; }
            // authored cargo added to this surviving container (a kept container can gain items) — full value
            foreach (var node in c.Placement.Cargo)
                AddAuthoredCargo(node, catalog, ref cargoValue, ref newCargo);
        }
        // Cargo in a DECK item is charged the same as cargo in an installed one: a backpack filled on the floor
        // costs what the identical items cost in a locker. Loose items are not in the diff at all (they are a
        // non-structural overlay), so they are walked separately. Their own pockets are free, as everywhere else.
        foreach (var lo in looseObjects ?? [])
            foreach (var node in lo.Cargo)
                AddAuthoredCargo(node, catalog, ref cargoValue, ref newCargo);

        var total = Total(newValue, movedValue + reformedValue, cargoValue, newMultiplier, movedMultiplier);
        return new EditCostBreakdown(newParts, movedParts, newValue, movedValue, newMultiplier, movedMultiplier, total)
        {
            NewCargo = newCargo,
            CargoValue = cargoValue,
            ReformedParts = reformedParts,
            ReformedValue = reformedValue,
        };
    }

    /// <summary>Re-total an already-computed breakdown at different multipliers, without walking the diff again.
    /// The cost step computes once at 1×/1× and calls this as the sliders move.</summary>
    public static double Total(EditCostBreakdown breakdown, double newMultiplier, double movedMultiplier) =>
        Total(breakdown.NewValue, breakdown.MovedValue + breakdown.ReformedValue, breakdown.CargoValue,
            newMultiplier, movedMultiplier);

    private static double Total(double newValue, double movedValue, double cargoValue,
        double newMultiplier, double movedMultiplier) =>
        newMultiplier * (newValue + cargoValue) + movedMultiplier * movedValue;

    /// <summary>Accumulate the base value + count of every authored item in a cargo subtree (stack members and
    /// nested authored items included); original save items are free — they already exist.</summary>
    private static void AddAuthoredCargo(CargoItem node, Catalog catalog, ref double value, ref int count)
    {
        // Intrinsic containers (a garment's pockets, a backpack's pouches) are authored so they reach the save,
        // but they are part of the parent object and are not bought separately — pricing them would inflate the
        // cost of every garment against what the game charges. See CargoItem.Intrinsic.
        if (node.Authored && !node.Intrinsic)
        {
            value += catalog.Lookup(node.DefName)?.BasePrice ?? 0;
            count++;
        }
        foreach (var child in node.Children)
            AddAuthoredCargo(child, catalog, ref value, ref count);
    }
}
