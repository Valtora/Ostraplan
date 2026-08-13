using System.Text.Json.Nodes;

namespace Ostraplan.Core;

/// <summary>
/// The raw-JSON view of a ship record, for the parts of an import that a parsed <see cref="ShipTemplate"/> cannot
/// answer. Container contents live in the ship's own <c>aItems</c>/<c>aCOs</c> (parent links on the items, grid
/// positions on the condowners), so the cargo builder needs the document rather than the abstraction.
///
/// <para>Shared by <see cref="TemplateImport"/> and <see cref="SaveEditImport"/>, which used to carry a copy each.</para>
/// </summary>
internal static class ShipJson
{
    /// <summary>The ship object with the most items in a ship file's text — a file may hold one ship or an array of
    /// them, and this must pick the same one <see cref="ShipTemplate"/> parsing does. Null when nothing parses.
    /// Parsed with the same relaxations as <see cref="ShipTemplate.ParseFileChecked"/> (trailing commas, comments),
    /// or a hand-edited modded ship would import its structure while its cargo dropped with no explanation.</summary>
    public static JsonNode? Largest(string text)
    {
        try
        {
            return Largest(JsonNode.Parse(text, documentOptions: new System.Text.Json.JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
            }));
        }
        catch (System.Text.Json.JsonException) { return null; }   // the template parser reports the failure
    }

    /// <inheritdoc cref="Largest(string)"/>
    public static JsonNode? Largest(JsonNode? node) => node switch
    {
        JsonArray arr => arr.OfType<JsonObject>()
            .Where(IsShip)
            .OrderByDescending(o => (o["aItems"] as JsonArray)?.Count ?? 0)
            .FirstOrDefault(),
        JsonObject obj when IsShip(obj) => obj,
        _ => null,
    };

    private static bool IsShip(JsonObject o) => o["nCols"] is not null && o["aItems"] is JsonArray;

    /// <summary>Index a ship record for the cargo builder: its items and condowners by <c>strID</c>, and the
    /// parent → children map every contained item declares through <c>strParentID</c>/<c>strSlotParentID</c>.</summary>
    public static (Dictionary<string, JsonNode> ItemsById, Dictionary<string, JsonNode> CosById,
        Dictionary<string, List<string>> Children) Index(JsonNode shipNode)
    {
        var items = ArrayOf(shipNode, "aItems").ToList();
        var itemsById = ById(items);
        var cosById = ById(ArrayOf(shipNode, "aCOs"));

        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var it in items)
        {
            if (Id(it) is not { } id) continue;
            // same rule as ShipTemplate's Contained: an empty strParentID still defers to strSlotParentID
            var pp = Str(it, "strParentID");
            var parent = string.IsNullOrEmpty(pp) ? Str(it, "strSlotParentID") : pp;
            if (string.IsNullOrEmpty(parent)) continue;
            if (!children.TryGetValue(parent, out var kids)) children[parent] = kids = [];
            kids.Add(id);
        }
        return (itemsById, cosById, children);
    }

    public static IEnumerable<JsonNode> ArrayOf(JsonNode ship, string prop) =>
        (ship as JsonObject)?[prop] is JsonArray a ? a.Where(n => n is not null).Select(n => n!) : [];

    public static Dictionary<string, JsonNode> ById(IEnumerable<JsonNode> nodes)
    {
        var map = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var n in nodes)
            if (Id(n) is { } id) map[id] = n;   // last wins on a duplicate id (should not happen in a real save)
        return map;
    }

    public static string? Id(JsonNode? n) => Str(n, "strID");

    public static string? Str(JsonNode? n, string prop) =>
        (n as JsonObject)?[prop] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
