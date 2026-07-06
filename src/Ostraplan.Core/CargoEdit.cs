namespace Ostraplan.Core;

/// <summary>
/// Pure edits of a container's <see cref="CargoItem"/> tree — the model behind the inventory editor's add and
/// remove. Every operation returns a <b>new</b> tree (the nodes are immutable records), rebuilt along the path to
/// the touched container, so the result drops straight into a command's before/after and the write-back reads the
/// current tree as the source of truth.
///
/// <para><b>Adding</b> (<see cref="Add"/>) enforces "the Law" for cargo: the item drops into the first free grid
/// cell and the add fails (returns <c>null</c>) if it will not fit the container's declared grid. Identical items
/// stack the way the game stores them — a lead item plus its copies as same-def members, up to the def's
/// <c>nStackLimit</c> — so several of one thing occupy one tile with an ×N count. <b>Removing</b>
/// (<see cref="RemoveOne"/>/<see cref="RemoveWhole"/>) takes one from a stack or the whole node; removing a
/// container removes its contents with it (they leave the tree, so the write-back drops them).</para>
/// </summary>
public static class CargoEdit
{
    /// <summary>
    /// Add <paramref name="quantity"/> of <paramref name="itemDef"/> into the container identified by
    /// <paramref name="containerId"/> (<c>null</c> = the root placement's direct cargo) within
    /// <paramref name="rootCargo"/>. Fills existing same-def stacks up to their limit first, then lays new
    /// stacks/singles into free cells. Returns the rebuilt root cargo, or <c>null</c> if the whole quantity will
    /// not fit the container's <paramref name="grid"/> (capacity — the caller blocks the add and says so).
    /// </summary>
    public static IReadOnlyList<CargoItem>? Add(
        IReadOnlyList<CargoItem> rootCargo, string? containerId, (int W, int H) grid, PartDef itemDef, int quantity)
    {
        if (quantity <= 0) return rootCargo;
        var kids = ChildrenOf(rootCargo, containerId);
        if (kids is null) return null;                       // container not found in the tree
        // only LOOSE items sit on the grid — equipped (slotted) gear on a container that also has slots keeps its
        // paper-doll place and must not consume grid cells. Pack the loose items, leave the slotted ones untouched.
        var loose = kids.Where(k => !k.Slotted).ToList();
        var slotted = kids.Where(k => k.Slotted).ToList();
        var placed = PlaceInto(loose, grid, itemDef, quantity);
        return placed is null ? null : ReplaceChildren(rootCargo, containerId, [.. placed, .. slotted]);
    }

    /// <summary>Remove <b>one</b> of the item with <paramref name="targetId"/>: a stack of N becomes N−1 (a stack
    /// of 2 collapses to a single), and a single item is removed outright (with any contents).</summary>
    public static IReadOnlyList<CargoItem> RemoveOne(IReadOnlyList<CargoItem> rootCargo, string targetId) =>
        Rewrite(rootCargo, targetId, node =>
        {
            if (node.IsStack && node.Stack > 1)
            {
                var count = node.Stack - 1;
                // drop the last member (authored members are appended last, so an original stack keeps its
                // save members preferentially); collapsing to 1 leaves a plain single with no members.
                var members = node.Children.Take(node.Children.Count - 1).ToList();
                return [node with { Stack = count, IsStack = count > 1, Children = members }];
            }
            return [];   // a single — remove it (and, if it is a container, its contents leave with it)
        });

    /// <summary>Remove the whole item/stack with <paramref name="targetId"/> — and, if it is a container, its
    /// entire contents subtree with it.</summary>
    public static IReadOnlyList<CargoItem> RemoveWhole(IReadOnlyList<CargoItem> rootCargo, string targetId) =>
        Rewrite(rootCargo, targetId, _ => []);

    // ---- placement / stacking ----

    private static IReadOnlyList<CargoItem>? PlaceInto(IReadOnlyList<CargoItem> kids, (int W, int H) grid, PartDef def, int quantity)
    {
        var limit = def.StackLimit > 1 ? def.StackLimit : 1;
        var result = kids.ToList();
        var remaining = quantity;

        // 1. top up existing same-def loose stacks/singles (a real container of the same def is never a stack — a
        //    stack's members share the item's OWN def, so guard on IsStack-or-bare-single, never a drillable box).
        if (limit > 1)
            for (var i = 0; i < result.Count && remaining > 0; i++)
            {
                var k = result[i];
                if (k.Slotted || k.DefName != def.DefName || (!k.IsStack && k.Children.Count > 0)) continue;
                if (k.Stack >= limit) continue;
                var add = Math.Min(limit - k.Stack, remaining);
                var members = k.Children.ToList();
                for (var m = 0; m < add; m++) members.Add(Member(def));
                var count = k.Stack + add;
                result[i] = k with { Children = members, Stack = count, IsStack = count > 1 };
                remaining -= add;
            }

        // 2. new stacks/singles into free cells (each occupies one cell; the loop re-packs so cells don't collide)
        while (remaining > 0)
        {
            var take = Math.Min(limit, remaining);
            if (InventoryGrid.FirstFreeCell(grid.W, grid.H, result, def.InvSize.W, def.InvSize.H) is not { } cell)
                return null;   // won't fit the declared grid — capacity reached
            result.Add(Stack(def, cell.X, cell.Y, take));
            remaining -= take;
        }
        return result;
    }

    /// <summary>An authored stack (or single, when <paramref name="count"/> is 1) of <paramref name="def"/> at a
    /// grid cell — a lead item plus <paramref name="count"/>−1 same-def members, mirroring the game's storage.</summary>
    private static CargoItem Stack(PartDef def, int gx, int gy, int count)
    {
        var members = new List<CargoItem>();
        for (var i = 1; i < count; i++) members.Add(Member(def));
        return new CargoItem(Guid.NewGuid().ToString(), def.DefName, def.Friendly, Slotted: false, members)
        {
            Authored = true,
            GridX = gx,
            GridY = gy,
            GridW = def.InvSize.W,
            GridH = def.InvSize.H,
            Stack = count,
            IsStack = count > 1,
        };
    }

    /// <summary>One authored stack member (a same-def copy of the lead; carries no grid position of its own).</summary>
    private static CargoItem Member(PartDef def) =>
        new(Guid.NewGuid().ToString(), def.DefName, def.Friendly, Slotted: false, [])
        {
            Authored = true,
            GridW = def.InvSize.W,
            GridH = def.InvSize.H,
            Stack = 1,
        };

    // ---- immutable tree walks ----

    /// <summary>The direct children of the node with <paramref name="containerId"/> (or the root list when it is
    /// <c>null</c>); <c>null</c> if no such container exists in the tree.</summary>
    private static IReadOnlyList<CargoItem>? ChildrenOf(IReadOnlyList<CargoItem> root, string? containerId)
    {
        if (containerId is null) return root;
        foreach (var it in root)
        {
            if (it.StrID == containerId) return it.Children;
            if (ChildrenOf(it.Children, containerId) is { } found) return found;
        }
        return null;
    }

    /// <summary>Rebuild <paramref name="root"/> with the children of <paramref name="containerId"/> (root when
    /// <c>null</c>) replaced by <paramref name="newChildren"/>.</summary>
    private static IReadOnlyList<CargoItem> ReplaceChildren(IReadOnlyList<CargoItem> root, string? containerId, IReadOnlyList<CargoItem> newChildren)
    {
        if (containerId is null) return newChildren;
        return root.Select(it =>
            it.StrID == containerId ? it with { Children = newChildren }
            : it.Children.Count == 0 ? it
            : it with { Children = ReplaceChildren(it.Children, containerId, newChildren) }).ToList();
    }

    /// <summary>Replace the node with <paramref name="targetId"/> anywhere in the tree by
    /// <paramref name="replace"/>'s result (empty = delete), recursing into other nodes' children.</summary>
    private static IReadOnlyList<CargoItem> Rewrite(IReadOnlyList<CargoItem> items, string targetId, Func<CargoItem, IReadOnlyList<CargoItem>> replace) =>
        items.SelectMany(it =>
            it.StrID == targetId ? replace(it)
            : it.Children.Count == 0 ? [it]
            : (IReadOnlyList<CargoItem>)[it with { Children = Rewrite(it.Children, targetId, replace) }]).ToList();
}
