namespace Ostraplan.Core;

/// <summary>
/// The theme picker: re-skin every wall and/or floor on the ship to a chosen cooverlay skin, in one
/// undo step. A "skin" is just a buildable wall/floor variant (the game's ~25 wall and ~110 floor
/// cooverlays each carry an install job, so they are ordinary palette parts over the same base
/// geometry as the plain wall/floor). Re-skinning is therefore a ship-wide <see cref="ReplaceOps"/>
/// swap: every placed part sharing a chosen skin's (render layer, footprint) class becomes that skin.
/// It changes only sprites and names — a wall skin keeps IsWall, a floor skin keeps IsFloorSealed —
/// so rooms, airtightness, certification and rating are untouched (like <see cref="ReplaceOps"/>,
/// the swapped-in parts are re-checked by the problem scan but never blocked).
/// </summary>
public static class ThemeOps
{
    /// <summary>The (render layer, W, H) class of a placement, or null if its def can't be resolved.</summary>
    private static (int Layer, int W, int H)? ClassOf(ShipDocument doc, Placement p)
    {
        var part = doc.Part(p);
        return part is null ? null : (doc.Catalog.RenderLayer(part), part.Item.Width, part.Item.Height);
    }

    /// <summary>
    /// Every unlocked placed part sharing <paramref name="skinDef"/>'s (layer, footprint) class — the
    /// parts a ship-wide re-skin to that def would swap. Empty if the def can't be resolved.
    /// </summary>
    public static IReadOnlyList<Placement> SameClassPlacements(ShipDocument doc, string skinDef)
    {
        var skin = doc.Catalog.Lookup(skinDef);
        if (skin is null) return [];
        var cls = (doc.Catalog.RenderLayer(skin), skin.Item.Width, skin.Item.Height);
        return doc.Placements.Where(p => !doc.IsLocked(p) && ClassOf(doc, p) == cls).ToList();
    }

    /// <summary>
    /// Build a ship-wide re-skin to the given skin defs (a wall skin and/or a floor skin; nulls and
    /// blanks ignored), as one undo step. Each skin swaps every same-class placed part that isn't
    /// already it; the created placements are returned to re-select. Null if nothing would change.
    /// </summary>
    public static (CompositeCommand Cmd, List<Placement> New)? BuildReskin(ShipDocument doc, params string?[] skinDefs)
    {
        var subCmds = new List<IDocCommand>();
        var created = new List<Placement>();
        foreach (var def in skinDefs.Where(d => !string.IsNullOrEmpty(d)).Distinct(StringComparer.Ordinal))
        {
            var targets = SameClassPlacements(doc, def!);
            if (ReplaceOps.BuildSwap(doc, targets, def!) is { } swap)
            {
                subCmds.Add(swap.Cmd);
                created.AddRange(swap.New);
            }
        }
        return subCmds.Count == 0 ? null : (new CompositeCommand(subCmds), created);
    }
}
