namespace Ostraplan.Core;

/// <summary>
/// Whether a container accepts an item — the game's <c>strContainerCT</c> (a CondTrigger) evaluated against the
/// item's conditions. This is what the inventory editor's add-picker uses to show only items a given container
/// will hold ("the Law" for cargo), matching the game's own fit test. The common container filters forbid
/// <c>IsInstalled</c> and require <c>IsSolid</c>, so installed structure is rejected and loose cargo passes.
/// </summary>
public static class ContainerFilter
{
    /// <summary>True if <paramref name="container"/> would accept <paramref name="item"/> — its
    /// <see cref="PartDef.ContainerCT"/> trigger holds against the item's starting conds. A container that names no
    /// filter, or whose filter def isn't loaded, accepts anything (permissive rather than block-everything).</summary>
    public static bool Accepts(Catalog catalog, PartDef container, PartDef item)
    {
        if (container.ContainerCT is not { } ctName) return true;
        if (!catalog.Triggers.TryGetValue(ctName, out var ct)) return true;
        return CondEval.Triggered(ct, item.StartingConds, catalog);
    }

    /// <summary>The loose items <paramref name="container"/> accepts, from the catalog's universe — the list the
    /// add-picker offers for that container.</summary>
    public static IReadOnlyList<PartDef> AcceptedBy(Catalog catalog, PartDef container) =>
        catalog.LooseItems.Where(i => Accepts(catalog, container, i)).ToList();
}
