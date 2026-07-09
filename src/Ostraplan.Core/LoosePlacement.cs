namespace Ostraplan.Core;

/// <summary>
/// "The Law" for dropping a loose item (the Items palette) onto the ship: an item can land on a walkable floor
/// tile (resting on the deck, one per tile) or go into a container covering that tile that accepts it. This mirrors
/// how the game stores loose cargo — free items sit on a floor, everything else lives inside a container — and
/// keeps the planner from authoring items floating in vacuum or clipped inside a wall.
/// </summary>
public static class LoosePlacement
{
    // The floor conditions a tile must carry for an item to rest on it (matches Catalog's floor render layer).
    private static readonly string[] FloorConds = ["IsFloor", "IsFloorSealed", "IsFloorFlex"];

    /// <summary>True if a loose item may rest on this tile: it carries a floor condition and holds no loose item
    /// yet (one per tile).</summary>
    public static bool CanRestOnFloor(ShipDocument doc, int x, int y)
    {
        if (doc.LooseAt(x, y) is not null) return false;
        return HasFloor(doc, x, y);
    }

    /// <summary>True if the tile carries any floor condition (a deck an item can sit on).</summary>
    public static bool HasFloor(ShipDocument doc, int x, int y)
    {
        var conds = doc.Conds.At(x, y);
        if (conds is null) return false;
        foreach (var f in FloorConds)
            if (conds.ContainsKey(f)) return true;
        return false;
    }

    /// <summary>
    /// The container covering (<paramref name="x"/>,<paramref name="y"/>) that would accept one more
    /// <paramref name="item"/> — the topmost placement that is a container, passes the item's
    /// <see cref="ContainerFilter"/>, and still has room (<see cref="CargoEdit.MaxAddable"/> &gt; 0). Null when no
    /// such container is under the cursor, so the caller falls back to a floor drop.
    /// </summary>
    public static Placement? AcceptingContainerAt(ShipDocument doc, Catalog catalog, int x, int y, PartDef item)
    {
        foreach (var p in doc.HitTestStack(x, y))   // topmost first
        {
            if (doc.Part(p) is not { IsContainer: true } container) continue;
            if (!ContainerFilter.Accepts(catalog, container, item)) continue;
            var grid = container.ContainerGrid ?? (6, 6);
            if (CargoEdit.MaxAddable(p.Cargo, null, grid, item) > 0) return p;
        }
        return null;
    }
}
