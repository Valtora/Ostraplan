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

    /// <summary>
    /// The loose item already on (<paramref name="x"/>,<paramref name="y"/>) that would take one more
    /// <paramref name="item"/> — a crate or a backpack lying on the deck, tested exactly as an installed container
    /// is. Null when the tile is empty, holds something that cannot store this, or is full, so the caller falls
    /// back to a floor drop and reports the tile as taken.
    ///
    /// <para>Checked <b>after</b> <see cref="AcceptingContainerAt"/>: an installed container wins a tile it shares
    /// with a deck item, matching the topmost-first rule there.</para>
    /// </summary>
    public static LooseObject? AcceptingLooseAt(ShipDocument doc, Catalog catalog, int x, int y, PartDef item)
    {
        if (doc.LooseAt(x, y) is not { } lo) return null;
        if (catalog.Lookup(lo.DefName) is not { IsContainer: true } host) return null;
        if (!ContainerFilter.Accepts(catalog, host, item)) return null;
        var grid = host.ContainerGrid ?? (6, 6);
        return CargoEdit.MaxAddable(lo.Cargo, null, grid, item) > 0 ? lo : null;
    }
}
