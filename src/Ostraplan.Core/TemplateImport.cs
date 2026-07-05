using System.IO;

namespace Ostraplan.Core;

/// <summary>One ship template on disk, for the browser: its display name, the source that
/// provides it (core or a mod label), and the file path.</summary>
public sealed record ShipFileEntry(string Name, string Origin, string Path);

/// <summary>A def an import couldn't resolve (its geometry isn't in the loaded data) and how
/// many tiles it dropped — surfaced so the user can enable the right mod and re-import.</summary>
public sealed record SkippedDef(string DefName, int Count);

/// <summary>The outcome of an import: the new document, unresolved defs, count of contained
/// sub-objects dropped (cargo/tools — layout only), and the ship's name.</summary>
public sealed record ImportResult(
    ShipDocument Doc, IReadOnlyList<SkippedDef> Skipped, int ContainedDropped, string ShipName, int PartCount);

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

    /// <summary>Parse a ship file and import its ship (the largest, for multi-ship batch files).</summary>
    public static ImportResult LoadFile(string path, Catalog catalog)
    {
        var ships = ShipTemplate.ParseFile(File.ReadAllText(path)).ToList();
        var tmpl = ships.OrderByDescending(s => s.Items.Count).FirstOrDefault()
            ?? throw new InvalidDataException($"'{Path.GetFileName(path)}' contains no ship.");
        return FromTemplate(tmpl, catalog);
    }

    /// <summary>Build an editable document from a parsed template.</summary>
    public static ImportResult FromTemplate(ShipTemplate tmpl, Catalog catalog)
    {
        var doc = new ShipDocument(catalog);
        var skipped = new Dictionary<string, int>(StringComparer.Ordinal);
        var contained = 0;

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
                var (col, row, rot) = ShipGrid.TemplateTile(
                    item.FX, item.FY, item.FRotation, part.Item.Width, part.Item.Height, tmpl.VShipPosX, tmpl.VShipPosY);
                new PlaceCommand(new Placement { DefName = item.DefName, X = col, Y = row, Rot = rot }).Do(doc);
            }
        }

        var skippedList = skipped
            .Select(kv => new SkippedDef(kv.Key, kv.Value))
            .OrderByDescending(s => s.Count).ThenBy(s => s.DefName, StringComparer.Ordinal)
            .ToList();
        var name = string.IsNullOrWhiteSpace(tmpl.Name) ? "Imported ship" : tmpl.Name;
        return new ImportResult(doc, skippedList, contained, name, doc.Placements.Count);
    }
}
