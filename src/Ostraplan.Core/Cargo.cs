using System.Text.Json.Nodes;

namespace Ostraplan.Core;

/// <summary>
/// One contained sub-object held by a placed part — loose cargo (parented by <c>strParentID</c>) or equipped
/// gear (a named slot, <c>strSlotParentID</c>) — together with its own nested contents. Ostraplan models the
/// grid part as a <see cref="Placement"/>; this is the tree of everything inside it, so a container's contents
/// can be shown, preserved through edits, and authored. It is an identity + display node: the verbatim item/CO
/// state (wear, gas, power, inventory) for a save-derived design stays in <see cref="SaveShipContext"/> keyed by
/// <see cref="StrID"/>, which the write-back preserves.
/// </summary>
public sealed record CargoItem(
    string StrID,
    string DefName,
    string? Friendly,
    bool Slotted,
    IReadOnlyList<CargoItem> Children)
{
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
    /// index (<paramref name="children"/>) and its <c>strID</c>-keyed item nodes. Friendly names resolve through
    /// the catalog (null when a def doesn't resolve). Cycle-guarded, and skips ids with no item node.
    /// </summary>
    public static IReadOnlyList<CargoItem> BuildForest(
        string rootId,
        IReadOnlyDictionary<string, List<string>> children,
        IReadOnlyDictionary<string, JsonNode> itemsById,
        Catalog catalog)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        IReadOnlyList<CargoItem> Build(string parentId)
        {
            if (!children.TryGetValue(parentId, out var kids)) return [];
            var result = new List<CargoItem>();
            foreach (var id in kids)
            {
                if (!seen.Add(id)) continue;                             // cycle / already placed under another parent
                if (!itemsById.TryGetValue(id, out var item)) continue;  // referenced but absent (shouldn't happen)
                var defName = Str(item, "strName") ?? "";
                result.Add(new CargoItem(
                    id, defName, catalog.Lookup(defName)?.Friendly,
                    Slotted: (item as JsonObject)?["strSlotParentID"] is not null,
                    Build(id)));
            }
            return result;
        }

        return Build(rootId);
    }

    private static string? Str(JsonNode? n, string prop) =>
        (n as JsonObject)?[prop] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
