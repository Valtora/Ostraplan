using System.IO;

namespace Ostraplan.Core;

/// <summary>One ship template on disk, for the browser: its display name, the source that
/// provides it (core or a mod label), and the file path.</summary>
public sealed record ShipFileEntry(string Name, string Origin, string Path);

/// <summary>A def an import couldn't resolve (its geometry isn't in the loaded data) and how
/// many tiles it dropped — surfaced so the user can enable the right mod and re-import.</summary>
public sealed record SkippedDef(string DefName, int Count);

/// <summary>The outcome of an import: the new document, unresolved defs, counts of dropped
/// contained sub-objects (cargo/tools) and system objects (loot spawners), and the ship's name.</summary>
public sealed record ImportResult(
    ShipDocument Doc, IReadOnlyList<SkippedDef> Skipped, int ContainedDropped, int SystemDropped,
    string ShipName, int PartCount);

/// <summary>
/// Imports a game ship template (core or mod <c>data/ships/*.json</c>) into an editable document —
/// the forward of the export mapping. Each stored item's centre <c>(fX,fY)</c> + CCW rotation becomes
/// a top-left tile placement via the shared <see cref="ShipGrid.TemplateTile"/>. Items whose geometry
/// can't be resolved at all (a def whose mod isn't loaded) are skipped and reported; everything else —
/// including the many non-buildable defs a real ship uses (raw hull, systems, tiles) — resolves through
/// <see cref="Catalog.Lookup"/> and both renders and analyses. Runtime state (crew, cargo, wear) is not
/// read: only the top-level item layout, so an import is always pristine.
/// </summary>
public static class TemplateImport
{
    /// <summary>Every ship file across core + loaded mods, later source winning a filename clash, name-sorted.</summary>
    public static IReadOnlyList<ShipFileEntry> ListShipFiles(DataIndex index)
    {
        var byName = new Dictionary<string, ShipFileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in index.Sources)
        {
            var dir = Path.Combine(source.DataDir, "ships");
            if (!Directory.Exists(dir)) continue;
            foreach (var path in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories))
                byName[Path.GetFileNameWithoutExtension(path)] = new ShipFileEntry(
                    Path.GetFileNameWithoutExtension(path), source.Label, path);
        }
        return byName.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The actual <c>strName</c> of a ship file's primary ship (the largest, matching <see cref="LoadFile"/>'s
    /// choice) — the authoritative override key for "replace this ship" export, which the filename only usually
    /// matches. Null if the file can't be parsed or holds no ship.</summary>
    public static string? ResolveShipStrName(string path)
    {
        try
        {
            return ShipTemplate.ParseFile(File.ReadAllText(path)).ToList()
                .OrderByDescending(s => s.Items.Count).FirstOrDefault()?.Name;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Parse a ship file and import its ship (the largest, for multi-ship batch files).</summary>
    public static ImportResult LoadFile(string path, Catalog catalog)
    {
        var ships = ShipTemplate.ParseFile(File.ReadAllText(path)).ToList();
        var tmpl = ships.OrderByDescending(s => s.Items.Count).FirstOrDefault()
            ?? throw new InvalidDataException($"'{Path.GetFileName(path)}' contains no ship.");
        return FromTemplate(tmpl, catalog);
    }

    /// <summary>Build an editable, layout-only document from a parsed template. Placed parts carry no
    /// save identity — for the save-edit import that tags each part, see <see cref="SaveEditImport"/>.</summary>
    public static ImportResult FromTemplate(ShipTemplate tmpl, Catalog catalog) =>
        Build(tmpl, catalog, retainOrigin: false);

    /// <summary>
    /// Shared import core. With <paramref name="retainOrigin"/> set (the save-edit path), each placed part
    /// is tagged with its source item <c>strID</c> via <see cref="Placement.OriginStrID"/>; otherwise
    /// (template / layout-only save import) that stays null and the part is treated as new construction on
    /// any later write-back. Behaviour is identical for both paths in every other respect.
    /// </summary>
    internal static ImportResult Build(ShipTemplate tmpl, Catalog catalog, bool retainOrigin)
    {
        var doc = new ShipDocument(catalog);
        var skipped = new Dictionary<string, int>(StringComparer.Ordinal);
        var contained = 0;
        var systems = 0;

        using (doc.SuspendChanged())
        {
            foreach (var item in tmpl.Items)
            {
                if (item.Contained) { contained++; continue; }   // cargo/slotted sub-object — layout only
                var part = catalog.Lookup(item.DefName);
                if (part is null)
                {
                    skipped[item.DefName] = skipped.GetValueOrDefault(item.DefName) + 1;
                    continue;
                }
                if (part.StartingConds.Contains("IsSystem"))   // loot spawners, fire, explosions — runtime, not structure
                {
                    systems++;
                    continue;
                }
                var (col, row, rot) = ShipGrid.TemplateTile(
                    item.FX, item.FY, item.FRotation, part.Item.Width, part.Item.Height, tmpl.VShipPosX, tmpl.VShipPosY);
                // imported structure is "given" — pre-existing, not user-authored, so the placement
                // law (which the game applies only to new construction) doesn't re-validate it
                new PlaceCommand(new Placement
                {
                    DefName = item.DefName, X = col, Y = row, Rot = rot, IsGiven = true,
                    OriginStrID = retainOrigin ? item.StrID : null,
                }).Do(doc);
            }

            // Convert stored zones to document coordinates. On import the document origin coincides with the
            // game grid origin, so a flat index maps straight to a doc tile; indices past the grid are dropped
            // (a corrupt/stale ship). Zones are pure overlays — no placement law, no tile conds.
            var tileCount = tmpl.NCols * tmpl.NRows;
            foreach (var sz in tmpl.Zones)
            {
                var zone = new ShipZone
                {
                    Name = sz.Name,
                    Color = sz.Color,
                    TileConds = [.. sz.TileConds],
                    CategoryConds = [.. sz.CategoryConds],
                    PersonSpec = sz.PersonSpec,
                    TargetPSpec = sz.TargetPSpec,
                    TriggerOnOwner = sz.TriggerOnOwner,
                };
                foreach (var idx in sz.Tiles)
                    if (idx >= 0 && idx < tileCount && tmpl.NCols > 0)
                        zone.Tiles.Add(ZoneGeometry.IndexToDoc(idx, tmpl.NCols));
                doc.AddZone(zone);
            }
        }

        var skippedList = skipped
            .Select(kv => new SkippedDef(kv.Key, kv.Value))
            .OrderByDescending(s => s.Count).ThenBy(s => s.DefName, StringComparer.Ordinal)
            .ToList();
        return new ImportResult(doc, skippedList, contained, systems, ShipName(tmpl), doc.Placements.Count);
    }

    /// <summary>The friendliest name for an imported ship: its player-given <c>publicName</c>
    /// (e.g. from a save) when it's a real one, else its <c>strName</c>.</summary>
    public static string ShipName(ShipTemplate tmpl) =>
        tmpl.PublicName is { Length: > 0 } pn && pn != "$TEMPLATE" ? pn
        : string.IsNullOrWhiteSpace(tmpl.Name) ? "Imported ship" : tmpl.Name;
}
