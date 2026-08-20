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
    /// the def is unknown. This is the <b>un-rotated</b> footprint; see <see cref="EffW"/>/<see cref="EffH"/> for
    /// the footprint after <see cref="GridRot"/>.</summary>
    public int GridW { get; init; } = 1;
    public int GridH { get; init; } = 1;

    /// <summary>The item's inventory rotation in degrees (0/90/180/270). The game stores an inventory item's
    /// rotation in the same <c>fRotation</c> field a placed part uses (save load: <c>item.fLastRotation =
    /// objItem.fRotation</c>); at 90°/270° the grid footprint is swapped (<see cref="EffW"/>/<see cref="EffH"/>).
    /// 0 for an unrotated item; kept normalized to {0, 90, 180, 270}.</summary>
    public int GridRot { get; init; }

    /// <summary>The effective grid footprint width after <see cref="GridRot"/> — swapped with the height at
    /// 90°/270°, matching the game's <c>Swap(itemWidthOnGrid, itemHeightOnGrid)</c> on rotate.</summary>
    public int EffW => GridRot % 180 == 0 ? GridW : GridH;

    /// <summary>The effective grid footprint height after <see cref="GridRot"/> (swapped with width at 90°/270°).</summary>
    public int EffH => GridRot % 180 == 0 ? GridH : GridW;

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

    /// <summary>
    /// True when this item is part of its parent rather than cargo put into it: a garment's pockets, a backpack's
    /// pouches, a PDA's data store. The game spawns these from the parent def's <c>strLoot</c> (see
    /// <see cref="Catalog.IntrinsicContents"/>), so they come and go with the parent and are never separately
    /// bought. They are written to the save like any other authored item — without them a garment arrives with no
    /// pockets and cannot hold anything — but they are excluded from the bill of materials and the edit cost,
    /// because you do not buy pockets separately from the coveralls.
    /// </summary>
    public bool Intrinsic { get; init; }

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
            var takenSlots = new HashSet<string>(StringComparer.Ordinal);   // one slot holds one item
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
                // ×N and doesn't offer to drill into a container of itself. Guard on "not a container" so a real one
                // that happens to hold same-def items (a crate of crates) isn't mistaken for a stack.
                var isStack = sub.Count > 0 && def?.IsContainer != true && sub.All(k => k.DefName == defName);
                // Heal a pocket an older Ostraplan wrote as ordinary cargo. Only this exact shape is touched: a
                // child that is one of the host's OWN intrinsic contents, parented rather than slotted, with a
                // free slot on the host waiting for it. The game cannot produce that (it slots them on spawn) and
                // neither can the user, so it is always the old writer's doing — and left alone it would be
                // written straight back out and the suit would come up empty again.
                var heal = !slotted && IsIntrinsicOf(catalog, parentDef, defName)
                    && FreeSlotFor(parentDef, def, takenSlots) is not null;
                var isSlotted = slotted || heal;
                result.Add(new CargoItem(id, defName, def?.Friendly, isSlotted, sub)
                {
                    GridX = isSlotted ? 0 : Int(co, "inventoryX"),
                    GridY = isSlotted ? 0 : Int(co, "inventoryY"),
                    GridRot = GridMath.Norm((int)Math.Round(Dbl(item, "fRotation"))),   // inventory rotation rides on the item's fRotation
                    GridW = gw,
                    GridH = gh,
                    Stack = isStack ? sub.Count + 1 : 1,
                    IsStack = isStack,
                    SlotName = isSlotted ? ResolveSlot(heal ? null : co, def, parentDef, takenSlots) : null,
                });
            }
            return result;
        }

        return Build(rootId);
    }

    /// <summary>
    /// Mark a whole forest <see cref="CargoItem.Authored"/>, keeping every id. For an import with no save behind it
    /// (a template, or a layout-only save import): the contents are real and must be written out on export, but
    /// there is no save record to defer to, so they are the design's own from the moment they arrive. Unlike
    /// <see cref="CloneForest"/> the ids are kept, since nothing is being duplicated.
    /// </summary>
    public static IReadOnlyList<CargoItem> AsAuthored(IReadOnlyList<CargoItem> forest) =>
        forest.Select(Author).ToList();

    private static CargoItem Author(CargoItem item) =>
        item with { Authored = true, Children = AsAuthored(item.Children) };

    /// <summary>
    /// Deep-clone a cargo forest, giving every node a fresh <see cref="CargoItem.StrID"/> and marking it
    /// <see cref="CargoItem.Authored"/> — for copy/paste and duplicate of a container, so the copy holds an
    /// independent set of contents (no shared item identity with the original) that the write-back and export
    /// treat as freshly authored items. All other fields (def, grid position/size/rotation, stack, slot) are
    /// preserved. Cloning at paste/duplicate time (not at copy time) means every paste gets its own new ids.
    /// </summary>
    public static IReadOnlyList<CargoItem> CloneForest(IReadOnlyList<CargoItem> forest) =>
        forest.Select(Clone).ToList();

    private static CargoItem Clone(CargoItem item) =>
        item with
        {
            StrID = Guid.NewGuid().ToString(),
            Children = item.Children.Select(Clone).ToList(),
            Authored = true,
        };

    /// <summary>
    /// True when a def can hold things in its own right: a real container (a crate, a locker, an ammo box), or
    /// something that spawns carrying its own containers (a garment, a backpack, an EVA suit, a wrist PDA).
    ///
    /// <para>This is the test for offering an inventory on a <b>deck item</b>. It deliberately does not accept
    /// "declares any slot": a severed limb declares wound sockets, and those are anatomy rather than storage.
    /// The intrinsic-contents test tells the two apart without a def-name list, the same way
    /// <see cref="Catalog.IntrinsicContents"/> separates a garment's pockets from a rifle's ammunition.</para>
    /// </summary>
    public static bool CanHoldCargo(PartDef? part, Catalog catalog) =>
        part is not null && (part.IsContainer || catalog.IntrinsicContents(part).Count > 0);

    /// <summary>
    /// The slot on <paramref name="host"/> that <paramref name="child"/> takes, mirroring how the game itself
    /// assigns one (<c>CondOwner.SetData</c>'s loot pass): it walks the child's <c>mapSlotEffects</c> keys in
    /// declaration order and takes the first slot the host declares that still has room, since
    /// <c>Slots.SlotItem</c> refuses a full slot. <paramref name="taken"/> is the slots the child's siblings
    /// already hold, which is what puts a backpack's four identical <c>PocketPouchSmall01</c> pouches in four
    /// different slots rather than all four in the first one. Null when the host has no free slot it fits.
    /// </summary>
    public static string? FreeSlotFor(PartDef? host, PartDef? child, IReadOnlySet<string> taken)
    {
        if (host is null || child is null) return null;
        foreach (var key in child.SlotKeys)
            if (Array.IndexOf(host.SlotsWeHave, key) >= 0 && !taken.Contains(key))
                return key;
        return null;
    }

    /// <summary>True when <paramref name="defName"/> is one of the containers <paramref name="host"/> spawns with
    /// as part of itself (see <see cref="Catalog.IntrinsicContents"/>) — a pocket, a pouch, a data store.</summary>
    private static bool IsIntrinsicOf(Catalog catalog, PartDef? host, string defName) =>
        host is not null && catalog.IntrinsicContents(host).Any(c => c.DefName == defName);

    /// <summary>The slot an equipped item occupies. The save records it outright on the item's condition owner
    /// (<c>strSlotName</c>, written from <c>slotNow</c>) and that is what the game re-slots by on load, so it wins;
    /// a template carries no COs, so fall back to the same assignment the game would make, then to the item's first
    /// declared key so nothing is left unaccounted for.</summary>
    private static string? ResolveSlot(JsonNode? co, PartDef? childDef, PartDef? parentDef, HashSet<string> taken)
    {
        if (Str(co, "strSlotName") is { Length: > 0 } saved) { taken.Add(saved); return saved; }
        if (FreeSlotFor(parentDef, childDef, taken) is { } free) { taken.Add(free); return free; }
        return childDef is { SlotKeys.Length: > 0 } ? childDef.SlotKeys[0] : null;
    }

    private static string? Str(JsonNode? n, string prop) =>
        (n as JsonObject)?[prop] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int Int(JsonNode? n, string prop) =>
        (n as JsonObject)?[prop] is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;

    private static double Dbl(JsonNode? n, string prop) =>
        (n as JsonObject)?[prop] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0.0;
}
