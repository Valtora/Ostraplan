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
        var regId = SaveImport.PlayerShipRegId(zip, out var why)
            ?? throw new InvalidDataException(SaveImport.NoSessionMessage(why));
        return ImportShip(save.ZipPath, save.Name, regId, catalog);
    }

    /// <summary>Import a <b>specific</b> ship (by RegID) from a save for editing — used by the ship picker, which
    /// resolves the player's owned ships (<see cref="SaveImport.ListPlayerShips"/>) rather than defaulting to the
    /// ship the player is standing on.</summary>
    public static SaveEditImportResult ImportForEditing(SaveEntry save, string regId, Catalog catalog) =>
        ImportShip(save.ZipPath, save.Name, regId, catalog);

    /// <summary>
    /// Rebuild just the <see cref="SaveShipContext"/> for a design reopened from an <c>.oplan</c> — the document
    /// already came from the file (with its <see cref="Placement.OriginStrID"/> tags), so this re-locates the
    /// <b>specific</b> ship (<paramref name="regId"/>) in the chosen save and returns the maps needed to inject.
    /// Throws if that ship is no longer in the save. The throwaway document it builds is discarded.
    /// </summary>
    public static SaveShipContext RelocateContext(string zipPath, string saveName, string regId, Catalog catalog) =>
        ImportShip(zipPath, saveName, regId, catalog).Context;

    /// <summary>Load one ship (by RegID) from a save zip, build the editable document, and retain the context.</summary>
    private static SaveEditImportResult ImportShip(string zipPath, string saveName, string regId, Catalog catalog)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var shipEntry = zip.GetEntry(SaveZip.ShipEntry(regId))
            ?? throw new InvalidDataException($"The ship '{regId}' is not among save \"{saveName}\"'s ships.");
        var text = SaveImport.ReadText(shipEntry);

        // Parse the same text two ways: a ShipTemplate to drive the placement pipeline, and a mutable
        // JsonNode to retain the verbatim item/CO maps the inject needs. Both select the ship with the most
        // items, so their strIDs describe the same ship.
        var tmpl = SaveImport.ParseShip(text, shipEntry.FullName, regId).OrderByDescending(s => s.Items.Count).First();
        var shipNode = ShipJson.Largest(text)
            ?? throw new InvalidDataException($"The ship '{regId}' has no readable record.");

        // Always everything. This design stays linked to the save and can be written back into it, and the
        // write-back emits each container's contents from the imported tree — so importing a ship for editing with
        // its cargo left out would delete that cargo from the save. Not a user choice.
        var import = TemplateImport.Build(tmpl, catalog, retainOrigin: true, ImportOptions.Everything);
        var source = new SaveSourceRef(saveName, regId);
        import.Doc.SourceSave = source;
        import.Doc.Kind = DocumentKindGuess.From(regId, tmpl.Designation);

        // One read of the session record, not three: it is the biggest thing in a save (tens of MB), and this
        // needs the player CO, the epoch, and — for the cost deduction — which ship the player is standing on.
        var session = SaveImport.ReadSession(zip);
        var context = BuildContext(source, zipPath, shipNode, import.Doc, catalog, session, zip, regId);

        // Build ran before BuildContext hung the cargo on the placements (this path attaches from the retained
        // context, not from Build), so its tallies still call every contained item dropped. Settle them from what
        // was actually attached, or the import report claims the ship's whole inventory was left behind.
        var attached = import.Doc.Placements.Sum(p => p.Cargo.Sum(c => c.SubtreeCount));

        // Now — after the save's own contents are on the placements, which is what Build had to leave to this
        // path — stock any nav console that has none. A pre-1.0 save is the case: consoles had no inventory
        // before 1.0, so they read in empty and the inject would write them back empty (a console the save
        // already had is kept verbatim, so the inject's own new-console fill never sees it). See NavConsole.
        var (navConsoles, navModules, navTrayed) = NavConsole.StockEmptyConsoles(import.Doc, catalog);

        import = import with
        {
            ContainedKept = attached,
            ContainedDropped = Math.Max(0, import.ContainedDropped - attached),
            NavConsolesStocked = navConsoles,
            NavModulesInstalled = navModules,
            NavModulesTrayed = navTrayed,
        };
        return new SaveEditImportResult(import, context);
    }

    /// <summary>Assemble the full context: the mutable ship record, every item/CO indexed by strID, each
    /// structural part's imported pose + contained-cargo subtree, and where the player's money is.</summary>
    private static SaveShipContext BuildContext(SaveSourceRef source, string zipPath, JsonNode shipNode, ShipDocument doc, Catalog catalog, SessionRecord? session, ZipArchive zip, string regId)
    {
        var (itemsById, cosById, children) = ShipJson.Index(shipNode);

        // one origin per structural (grid-placed) part, keyed by the strID the import tagged it with
        var origins = new Dictionary<string, OriginPart>(StringComparer.Ordinal);
        var cargoByOrigin = new Dictionary<string, IReadOnlyList<CargoItem>>(StringComparer.Ordinal);
        foreach (var p in doc.Placements)
            if (p.OriginStrID is { } id)
            {
                origins[id] = new OriginPart(p.X, p.Y, GridMath.Norm(p.Rot), Descendants(id, children));
                var forest = Cargo.BuildForest(id, children, itemsById, cosById, catalog);
                cargoByOrigin[id] = forest;
                p.Cargo = forest;   // attach the container's contents tree to the imported placement
                p.Fill = ReadFill(id, itemsById, cosById, catalog, doc.Part(p));
            }

        // The player's money follows the character, not the ship: it is on their CO, which lives in the record for
        // whatever they were standing on when the game saved. Resolved here, once, because reading it can mean
        // parsing a second large ship record and this import already runs off the UI thread.
        var (coRegId, balance) = SaveEdit.LocatePlayerBalance(
            zip, shipNode, regId, session?.PlayerCoId, session?.ShipRegId);

        return new SaveShipContext
        {
            Source = source,
            ZipPath = zipPath,
            ShipRecord = shipNode,
            PlayerCoId = session?.PlayerCoId,
            PlayerCoRegId = coRegId,
            PlayerBalance = balance,
            Epoch = session?.Epoch ?? 0,
            Origins = origins,
            ItemsById = itemsById,
            CosById = cosById,
            CargoByOrigin = cargoByOrigin,
        };
    }

    /// <summary>
    /// What a container in the save is actually holding, as an authored <see cref="Placement.Fill"/> — or null
    /// when it holds nothing editable, or is sitting at exactly the amounts its def ships with (nothing worth
    /// recording, and a design that says nothing about a tank is easier to read).
    ///
    /// <para>Without this a half-empty imported tank would be valued, rated and flown as a full one: every
    /// analysis reads a part's conditions from its <b>def</b>, and only a fill can say otherwise. The amounts are
    /// read the way the game reads them on load — the condowner's own <c>aConds</c> first, then the item's
    /// <c>aCondOverrides</c> on top, since <c>ApplyOverrideCondsToCO</c> runs after the condowner is built.</para>
    /// </summary>
    private static IReadOnlyDictionary<string, double>? ReadFill(
        string id, IReadOnlyDictionary<string, JsonNode> itemsById, IReadOnlyDictionary<string, JsonNode> cosById,
        Catalog catalog, PartDef? def)
    {
        if (ContainerFill.Describe(def, catalog) is not { } spec) return null;

        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        if (cosById.GetValueOrDefault(id) is JsonObject co && co["aConds"] is JsonArray conds)
            foreach (var (name, amount) in CondOwnerDef.ParseCondValues(StrValues(conds)))
                values[name] = amount;

        if (itemsById.GetValueOrDefault(id) is JsonObject item && item["aCondOverrides"] is JsonArray overrides)
            foreach (var node in overrides)
                if (node is JsonObject o && o["CondName"]?.GetValue<string>() is { } cond)
                {
                    var amount = o["Amount"] is JsonValue a && a.TryGetValue<double>(out var d) ? d : 0;
                    values[cond] = o["NegativeValue"]?.GetValue<bool>() == true ? -amount : amount;
                }

        var fill = spec.Lines.ToDictionary(l => l.Cond, l => values.GetValueOrDefault(l.Cond), StringComparer.Ordinal);
        return ContainerFill.IsStock(fill, spec) ? null : ContainerFill.Clamp(fill, spec);
    }

    /// <summary>The string entries of a JSON array, skipping anything else.</summary>
    private static string[] StrValues(JsonArray arr) =>
        [.. arr.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).OfType<string>()];

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

}
