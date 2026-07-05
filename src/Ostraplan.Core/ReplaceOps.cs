namespace Ostraplan.Core;

/// <summary>
/// "Replace with…": swap a selection of parts for a compatible buildable part, keeping each
/// part's tile and rotation. Compatible = the <b>same render layer AND the same base footprint</b>
/// (item W×H) — so a floor swaps for any other floor, a wall for any wall, but a floor can't
/// become a canister (Floor vs Fixture) nor a 1×1 wall a 5×1 door (footprint differs). The swap
/// itself is a remove-old + place-new pair per part (Placement.DefName is init-only), all in one
/// undo step; the new parts are authored, so the placement law re-checks them (illegal results are
/// flagged, not blocked — matching how every other edit behaves).
/// </summary>
public static class ReplaceOps
{
    /// <summary>The (layer, w, h) class shared by every part in the set, or null if they don't share one (or it's empty).</summary>
    public static (int Layer, int W, int H)? CommonClass(ShipDocument doc, IReadOnlyList<Placement> parts)
    {
        (int, int, int)? cls = null;
        foreach (var p in parts)
        {
            var part = doc.Part(p);
            if (part is null) return null;
            var key = (doc.Catalog.RenderLayer(part), part.Item.Width, part.Item.Height);
            if (cls is null) cls = key;
            else if (cls.Value != key) return null;
        }
        return cls;
    }

    /// <summary>Buildable palette parts compatible with a class: same render layer and base footprint.</summary>
    public static IReadOnlyList<PartDef> CompatibleTargets(Catalog catalog, (int Layer, int W, int H) cls) =>
        catalog.Parts
            .Where(p => catalog.RenderLayer(p) == cls.Layer && p.Item.Width == cls.W && p.Item.Height == cls.H)
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
            var repl = new Placement { DefName = newDef, X = p.X, Y = p.Y, Rot = sheet ? 0 : p.Rot };
            cmds.Add(new RemoveCommand([p]));
            cmds.Add(new PlaceCommand(repl));
            created.Add(repl);
        }
        return cmds.Count == 0 ? null : (new CompositeCommand(cmds), created);
    }
}
