using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;

namespace Ostraplan.Core;

/// <summary>The outcome of importing a save's player ship for editing: the layout-only
/// <see cref="ImportResult"/> (document + drop tallies), plus the <see cref="SaveShipContext"/> that lets
/// the design be written back into a copy of the save (Phase 2). The document is <see cref="ImportResult.Doc"/>,
/// with its <see cref="ShipDocument.SourceSave"/> already stamped.</summary>
public sealed record SaveEditImportResult(ImportResult Import, SaveShipContext Context)
{
    public ShipDocument Doc => Import.Doc;
}

/// <summary>
/// Imports the player's own ship from a save <b>for editing</b> — the richer sibling of
/// <see cref="SaveImport"/>. It produces the same editable, layout-only document, but additionally: tags
/// every placed part with its source item <c>strID</c> (<see cref="Placement.OriginStrID"/>), stamps the
/// document's <see cref="ShipDocument.SourceSave"/>, and retains a <see cref="SaveShipContext"/> — the parsed
/// ship record plus <c>strID</c>-keyed maps of every item, CO and cargo subtree — so the edited layout can
/// later be injected back into a <b>copy</b> of the save without losing crew, cargo, position or identity.
/// Reads only the one ship record; <b>writes nothing</b>.
/// </summary>
public static class SaveEditImport
{
    /// <summary>Import the player's ship from a save for editing. Throws (for the caller to report) if the
    /// player record or that ship can't be found or parsed.</summary>
    public static SaveEditImportResult ImportForEditing(SaveEntry save, Catalog catalog)
    {
        using var zip = ZipFile.OpenRead(save.ZipPath);

        var regId = SaveImport.PlayerShipRegId(zip)
            ?? throw new InvalidDataException("Couldn't find the player's ship in this save (no character record naming a current ship).");
        var shipEntry = zip.GetEntry($"ships/{regId}.json")
            ?? throw new InvalidDataException($"The player's ship '{regId}' is not among this save's ships.");
        var text = SaveImport.ReadText(shipEntry);

        // Parse the same text two ways: a ShipTemplate to drive the placement pipeline, and a mutable
        // JsonNode to retain the verbatim item/CO maps Phase 2 needs. Both select the ship with the most
        // items, so their strIDs describe the same ship.
        var tmpl = ShipTemplate.ParseFile(text).OrderByDescending(s => s.Items.Count).FirstOrDefault()
            ?? throw new InvalidDataException($"The player's ship '{regId}' could not be parsed.");
        var shipNode = LargestShip(JsonNode.Parse(text))
            ?? throw new InvalidDataException($"The player's ship '{regId}' has no readable record.");

        var import = TemplateImport.Build(tmpl, catalog, retainOrigin: true);
        var source = new SaveSourceRef(save.Name, regId);
        import.Doc.SourceSave = source;

        var context = BuildContext(source, save.ZipPath, shipNode, import.Doc);
        return new SaveEditImportResult(import, context);
    }

    /// <summary>Assemble the full context: the mutable ship record, every item/CO indexed by strID, and each
    /// structural part's imported pose + contained-cargo subtree.</summary>
    private static SaveShipContext BuildContext(SaveSourceRef source, string zipPath, JsonNode shipNode, ShipDocument doc)
    {
        var items = ArrayOf(shipNode, "aItems").ToList();
        var itemsById = ById(items);
        var cosById = ById(ArrayOf(shipNode, "aCOs"));

        // parent strID -> child strIDs, over every item declaring a container/slot parent
        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var it in items)
        {
            if (Id(it) is not { } id) continue;
            var parent = Str(it, "strParentID") ?? Str(it, "strSlotParentID");
            if (string.IsNullOrEmpty(parent)) continue;
            if (!children.TryGetValue(parent, out var kids)) children[parent] = kids = [];
            kids.Add(id);
        }

        // one origin per structural (grid-placed) part, keyed by the strID the import tagged it with
        var origins = new Dictionary<string, OriginPart>(StringComparer.Ordinal);
        foreach (var p in doc.Placements)
            if (p.OriginStrID is { } id)
                origins[id] = new OriginPart(p.X, p.Y, GridMath.Norm(p.Rot), Descendants(id, children));

        return new SaveShipContext
        {
            Source = source,
            ZipPath = zipPath,
            ShipRecord = shipNode,
            Origins = origins,
            ItemsById = itemsById,
            CosById = cosById,
        };
    }

    /// <summary>Depth-first descendant strIDs of a root (excluding the root itself), cycle-guarded.</summary>
    private static IReadOnlyList<string> Descendants(string root, Dictionary<string, List<string>> children)
    {
        if (!children.TryGetValue(root, out var direct)) return [];
        var acc = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(direct);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!seen.Add(id)) continue;
            acc.Add(id);
            if (children.TryGetValue(id, out var kids))
                foreach (var c in kids) stack.Push(c);
        }
        return acc;
    }

    /// <summary>The ship object with the most items — a file may be one object or an array of ships.</summary>
    private static JsonNode? LargestShip(JsonNode? node) => node switch
    {
        JsonArray arr => arr.OfType<JsonObject>()
            .Where(IsShip)
            .OrderByDescending(o => (o["aItems"] as JsonArray)?.Count ?? 0)
            .FirstOrDefault(),
        JsonObject obj when IsShip(obj) => obj,
        _ => null,
    };

    private static bool IsShip(JsonObject o) => o["nCols"] is not null && o["aItems"] is JsonArray;

    private static IEnumerable<JsonNode> ArrayOf(JsonNode ship, string prop) =>
        (ship as JsonObject)?[prop] is JsonArray a ? a.Where(n => n is not null).Select(n => n!) : [];

    private static Dictionary<string, JsonNode> ById(IEnumerable<JsonNode> nodes)
    {
        var map = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var n in nodes)
            if (Id(n) is { } id) map[id] = n;   // last wins on a duplicate id (should not happen in a real save)
        return map;
    }

    private static string? Id(JsonNode? n) => Str(n, "strID");

    private static string? Str(JsonNode? n, string prop) =>
        (n as JsonObject)?[prop] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
