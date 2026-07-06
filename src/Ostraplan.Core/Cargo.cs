using System.Text.Json.Nodes;

namespace Ostraplan.Core;

/// <summary>
/// One contained sub-object held by a placed part — loose cargo (parented by <c>strParentID</c>) or equipped
/// gear (a named slot, <c>strSlotParentID</c>) — together with its own nested contents. Ostraplan models the
/// grid part as a <see cref="Placement"/>; this is the tree of everything inside it, so a container's contents
/// can be shown, preserved through edits, and authored. It is an identity + display node: the verbatim item/CO
/// state (wear, gas, power, inventory) for a save-derived design stays in <see cref="SaveShipContext"/> keyed by
/// <see cref="StrID"/>, which the write-back preserves.
///
/// <para>The layout fields (<see cref="GridX"/>/<see cref="GridY"/>/<see cref="GridW"/>/<see cref="GridH"/>/
/// <see cref="Stack"/>/<see cref="SlotName"/>) let the inventory viewer mirror the in-game window: a loose item
/// sits at its grid cell taking up its footprint; an equipped item fills a named paper-doll slot. Positions come
/// from the CO's persisted <c>inventoryX</c>/<c>inventoryY</c> (often 0,0 for a container never opened in-game —
/// the viewer packs those the way the game does on open), sizes from the def.</para>
/// </summary>
public sealed record CargoItem(
    string StrID,
    string DefName,
    string? Friendly,
    bool Slotted,
    IReadOnlyList<CargoItem> Children)
{
    /// <summary>The item's persisted grid cell in its container (the CO's <c>inventoryX</c>/<c>inventoryY</c>);
    /// 0,0 when unset. Meaningful only for loose (non-<see cref="Slotted"/>) items.</summary>
    public int GridX { get; init; }
    public int GridY { get; init; }

    /// <summary>The item's footprint on the grid in tiles (the resolved <see cref="PartDef.InvSize"/>); 1×1 when
    /// the def is unknown.</summary>
    public int GridW { get; init; } = 1;
    public int GridH { get; init; } = 1;

    /// <summary>How many identical items are stacked here — the game's <c>StackCount</c> = the number of same-def
    /// stack members (held as this item's children) + 1. 1 for a single, unstacked item. (Do <b>not</b> read this
    /// from the <c>IsStacking</c> cond — that is the stack <i>capacity</i> <c>nStackLimit-1</c>, constant per def,
    /// not the current count.)</summary>
    public int Stack { get; init; } = 1;

    /// <summary>True when this node is a <b>stack</b> — its <see cref="Children"/> are copies of itself (the
    /// game's <c>aStack</c> members persisted as same-def child items), not distinct nested cargo. The viewer
    /// draws a stack as one block with an ×<see cref="Stack"/> count and does <b>not</b> let you drill into it; the
    /// members are retained (for preservation and, later, splitting a stack) but aren't a container.</summary>
    public bool IsStack { get; init; }

    /// <summary>For an equipped (<see cref="Slotted"/>) item, the named slot it occupies on its parent's
    /// paper-doll — its def's <c>mapSlotEffects</c> key intersected with the parent's <c>aSlotsWeHave</c>. Null
    /// for loose grid cargo, or when the slot can't be resolved.</summary>
    public string? SlotName { get; init; }

    /// <summary>True when this item was <b>authored in Ostraplan</b> — added to a container in the inventory
    /// editor rather than imported from the save. Its <see cref="StrID"/> is a fresh local GUID with no save
    /// counterpart, so the write-back synthesizes a pristine item + condition owner for it (see
    /// <see cref="SaveEdit"/>); an original (non-authored) item is written back verbatim from the save. A stack's
    /// authored members carry this too. Persisted in the <c>.oplan</c> cargo snapshot so authored edits survive a
    /// reopen (see <see cref="OplanCargo"/>).</summary>
    public bool Authored { get; init; }

    /// <summary>This item's <see cref="StrID"/> plus every descendant's, depth-first — the whole subtree.</summary>
    public IEnumerable<string> SubtreeIds()
    {
        yield return StrID;
        foreach (var child in Children)
            foreach (var id in child.SubtreeIds())
                yield return id;
    }

    /// <summary>Total items in this subtree, counting this one.</summary>
    public int SubtreeCount => 1 + Children.Sum(c => c.SubtreeCount);
}

/// <summary>Builds the <see cref="CargoItem"/> forest for a container from a ship's parent→children index.</summary>
public static class Cargo
{
    /// <summary>
    /// The direct children of <paramref name="rootId"/>, each as a tree, resolved from the ship's parent→children
    /// index (<paramref name="children"/>), its <c>strID</c>-keyed item nodes and CO nodes. Item defs resolve
    /// through the catalog for friendly names, grid footprint and slot metadata; positions and stack counts come
    /// from the CO nodes. Cycle-guarded, and skips ids with no item node.
    /// </summary>
    public static IReadOnlyList<CargoItem> BuildForest(
        string rootId,
        IReadOnlyDictionary<string, List<string>> children,
        IReadOnlyDictionary<string, JsonNode> itemsById,
        IReadOnlyDictionary<string, JsonNode> cosById,
        Catalog catalog)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        IReadOnlyList<CargoItem> Build(string parentId)
        {
            if (!children.TryGetValue(parentId, out var kids)) return [];
            var parentDef = catalog.Lookup(Str(itemsById.GetValueOrDefault(parentId), "strName"));
            var result = new List<CargoItem>();
            foreach (var id in kids)
            {
                if (!seen.Add(id)) continue;                             // cycle / already placed under another parent
                if (!itemsById.TryGetValue(id, out var item)) continue;  // referenced but absent (shouldn't happen)
                var defName = Str(item, "strName") ?? "";
                var def = catalog.Lookup(defName);
                var slotted = (item as JsonObject)?["strSlotParentID"] is not null;
                var co = cosById.GetValueOrDefault(id);
                var (gw, gh) = def?.InvSize ?? (1, 1);
                var sub = Build(id);
                // A stack persists as a lead item plus its copies as same-def children (StackCount = aStack.Count+1),
                // NOT as a container of distinct items — collapse it to one entry with a count so the viewer shows
                // ×N and doesn't offer to drill into a box of itself. Guard on "not a container" so a real container
                // that happens to hold same-def items (a crate of crates) isn't mistaken for a stack.
                var isStack = sub.Count > 0 && def?.IsContainer != true && sub.All(k => k.DefName == defName);
                result.Add(new CargoItem(id, defName, def?.Friendly, slotted, sub)
                {
                    GridX = Int(co, "inventoryX"),
                    GridY = Int(co, "inventoryY"),
                    GridW = gw,
                    GridH = gh,
                    Stack = isStack ? sub.Count + 1 : 1,
                    IsStack = isStack,
                    SlotName = slotted ? ResolveSlot(def, parentDef) : null,
                });
            }
            return result;
        }

        return Build(rootId);
    }

    /// <summary>The slot an equipped item occupies: its <c>mapSlotEffects</c> key that its parent actually has,
    /// else its first declared key.</summary>
    private static string? ResolveSlot(PartDef? childDef, PartDef? parentDef)
    {
        if (childDef is null || childDef.SlotKeys.Length == 0) return null;
        if (parentDef is not null)
            foreach (var key in childDef.SlotKeys)
                if (Array.IndexOf(parentDef.SlotsWeHave, key) >= 0)
                    return key;
        return childDef.SlotKeys[0];
    }

    private static string? Str(JsonNode? n, string prop) =>
        (n as JsonObject)?[prop] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int Int(JsonNode? n, string prop) =>
        (n as JsonObject)?[prop] is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
}
