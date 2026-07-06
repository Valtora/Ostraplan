namespace Ostraplan.Core;

/// <summary>
/// "Replace with…": swap a selection of parts for a compatible buildable part, keeping each
/// part's tile and rotation. Compatible = the <b>same render layer AND the same base footprint</b>
/// (item W×H) — so a floor swaps for any other floor, a wall for any wall, but a floor can't
/// become a canister (Floor vs Fixture) nor a 1×1 wall a 5×1 door (footprint differs).
/// <b>Containers are excluded</b> from both source and target: their inventory grid and cargo don't
/// carry across a def-change (footprint match says nothing about grid size), so a container is changed
/// by a manual delete + place, and its cargo is edited in the inventory viewer. The swap
/// itself is a remove-old + place-new pair per part (Placement.DefName is init-only), all in one
/// undo step. The replacement <b>inherits the original's given-ness</b>: a same-layer,
/// same-footprint swap keeps the tiles' structural role, so re-skinning imported (given) structure
/// stays given and exempt from the placement-law scan (else a bulk re-skin would re-validate a valid
/// imported ship the game never re-checks — sensors mounted through walls, stacked fixtures, etc.).
/// Swapping a user-authored part yields an authored replacement, re-checked as before.
/// </summary>
public static class ReplaceOps
{
    /// <summary>The (layer, w, h) class shared by every part in the set, or null if they don't share one, the set
    /// is empty, or <b>any part is a container</b> — containers aren't re-skinnable (their inventory grid and cargo
    /// don't carry across a def-change; changing a container is a manual delete + place).</summary>
    public static (int Layer, int W, int H)? CommonClass(ShipDocument doc, IReadOnlyList<Placement> parts)
    {
        (int, int, int)? cls = null;
        foreach (var p in parts)
        {
            var part = doc.Part(p);
            if (part is null || part.IsContainer) return null;
            var key = (doc.Catalog.RenderLayer(part), part.Item.Width, part.Item.Height);
            if (cls is null) cls = key;
            else if (cls.Value != key) return null;
        }
        return cls;
    }

    /// <summary>Buildable palette parts compatible with a class: same render layer and base footprint, and
    /// <b>never a container</b> (you can't re-skin something into a container either — that would create an empty
    /// inventory grid out of thin air).</summary>
    public static IReadOnlyList<PartDef> CompatibleTargets(Catalog catalog, (int Layer, int W, int H) cls) =>
        catalog.Parts
            .Where(p => !p.IsContainer && catalog.RenderLayer(p) == cls.Layer && p.Item.Width == cls.W && p.Item.Height == cls.H)
            .ToList();

    /// <summary>
    /// Swap each (unlocked) selected part to <paramref name="newDef"/> at the same tile/rotation, as one
    /// undo step. Sheet targets (walls/floors) normalise to rot 0 (they auto-tile). Returns the command
    /// and the created placements to re-select, or null if nothing changes.
    /// </summary>
    public static (CompositeCommand Cmd, List<Placement> New)? BuildSwap(ShipDocument doc, IReadOnlyList<Placement> parts, string newDef)
    {
        var sheet = doc.Catalog.Lookup(newDef)?.Item.HasSpriteSheet == true;
        var cmds = new List<IDocCommand>();
        var created = new List<Placement>();
        foreach (var p in parts)
        {
            if (doc.IsLocked(p) || p.DefName == newDef) continue;
            // Carry any cargo onto the replacement — a defensive safety net. The UI won't re-skin a container
            // (CommonClass/CompatibleTargets exclude them), so p.Cargo is empty here for walls/floors/fixtures and
            // this is a no-op; but should a def-change ever carry cargo, the inject re-parents it instead of
            // silently dropping it.
            var repl = new Placement { DefName = newDef, X = p.X, Y = p.Y, Rot = sheet ? 0 : p.Rot, IsGiven = p.IsGiven, Cargo = p.Cargo };
            cmds.Add(new RemoveCommand([p]));
            cmds.Add(new PlaceCommand(repl));
            created.Add(repl);
        }
        return cmds.Count == 0 ? null : (new CompositeCommand(cmds), created);
    }
}
