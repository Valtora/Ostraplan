namespace Ostraplan.Core;

/// <summary>
/// "Make Loose Item" / "Install item": swap a placed part between its <b>installed</b> and <b>loose (packaged)</b>
/// forms in place, keeping its tile, rotation and any cargo. The mapping is the game's own install/uninstall jobs
/// (<see cref="Catalog.LooseForm"/> / <see cref="Catalog.InstalledForm"/>) — an uninstall job's loot is the loose
/// form, an install job's <c>strStartInstall</c> the installed form — so nothing is invented: the loose def brings
/// its own sprite, its <c>["drag","Blank"]</c> handle, and (for gas canisters) the same baked contents, and the
/// analysis engine recomputes rooms/rating from the new def's conditions. A loose form carries no socket
/// conditions, so making a fixture loose correctly de-certifies whatever room required it.
///
/// <para>Like <see cref="ReplaceOps"/> the swap is a remove-old + place-new pair per part in one undo step. The
/// result is treated as <b>authored</b> (given-ness cleared), so the problem scan re-checks it: a loose form has
/// no placement requirements and always passes, while an installed form that no longer fits is <b>flagged, not
/// blocked</b> — consistent with moves/replaces landing in an illegal spot. Cargo rides across (a container's two
/// forms share their inventory grid), so uninstalling a stocked locker keeps its contents.</para>
/// </summary>
public static class FormSwap
{
    /// <summary>The (unlocked) parts in the set that have a loose form, each paired with its loose target def
    /// (installed → loose). Only targets that resolve to real geometry are returned.</summary>
    public static IReadOnlyList<(Placement Part, string Target)> Loosenable(ShipDocument doc, IReadOnlyList<Placement> parts) =>
        Map(doc, parts, doc.Catalog.LooseForm);

    /// <summary>The (unlocked) parts in the set that have an installed form, each paired with its installed target
    /// def (loose → installed). Only targets that resolve to real geometry are returned.</summary>
    public static IReadOnlyList<(Placement Part, string Target)> Installable(ShipDocument doc, IReadOnlyList<Placement> parts) =>
        Map(doc, parts, doc.Catalog.InstalledForm);

    private static IReadOnlyList<(Placement, string)> Map(ShipDocument doc, IReadOnlyList<Placement> parts, Func<string, string?> form)
    {
        var result = new List<(Placement, string)>();
        foreach (var p in parts)
            if (!doc.IsLocked(p) && form(p.DefName) is { } target && doc.Catalog.Lookup(target) is not null)
                result.Add((p, target));
        return result;
    }

    /// <summary>
    /// Swap each (part → target) to the target def at the same tile and rotation, cargo carried, all in one undo
    /// step. Returns the composite command and the created placements (to re-select), or null when nothing swaps.
    /// </summary>
    public static (CompositeCommand Cmd, List<Placement> New)? BuildSwap(
        ShipDocument doc, IReadOnlyList<(Placement Part, string Target)> swaps)
    {
        var cmds = new List<IDocCommand>();
        var created = new List<Placement>();
        foreach (var (p, target) in swaps)
        {
            if (doc.IsLocked(p) || p.DefName == target) continue;
            // A sheet target (a wall/floor) auto-tiles and must sit at rot 0; a loose fixture keeps its rotation.
            var sheet = doc.Catalog.Lookup(target)?.Item.HasSpriteSheet == true;
            var repl = new Placement { DefName = target, X = p.X, Y = p.Y, Rot = sheet ? 0 : p.Rot, IsGiven = false, Cargo = p.Cargo };
            cmds.Add(new RemoveCommand([p]));
            cmds.Add(new PlaceCommand(repl));
            created.Add(repl);
        }
        return cmds.Count == 0 ? null : (new CompositeCommand(cmds), created);
    }
}
