using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace Ostraplan.Core;

/// <summary>A save game on disk, for the picker: the save's folder name, the player's ship name
/// and character (from <c>saveInfo.json</c>), when it was written, and the path to its data zip.</summary>
public sealed record SaveEntry(string Name, string ShipName, string PlayerName, string When, string ZipPath);

/// <summary>
/// Imports the <b>player's own ship</b> from a save game — layout only. A save folder holds a
/// <c>&lt;name&gt;.zip</c> whose <c>ships/&lt;RegID&gt;.json</c> files are JsonShip records (a superset of a
/// template); the player's ship is the one the player character record points at (<c>strShip</c>).
/// Import reads only the top-level item layout through <see cref="TemplateImport"/>, so crew, cargo,
/// slotted modules, wear and damage are all dropped — the result is a pristine, editable design.
/// </summary>
public static class SaveImport
{
    /// <summary>Every save under the Saves folder that has a data zip, newest first.</summary>
    public static IReadOnlyList<SaveEntry> ListSaves(GameEnv env)
    {
        var list = new List<SaveEntry>();
        if (env.SavesDir is not { } dir || !Directory.Exists(dir)) return list;

        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var zip = Directory.EnumerateFiles(sub, "*.zip").FirstOrDefault();
            if (zip is null) continue;
            var (ship, player, when) = ReadSaveInfo(Path.Combine(sub, "saveInfo.json"));
            list.Add(new SaveEntry(Path.GetFileName(sub), ship, player, when, zip));
        }
        return list.OrderByDescending(s => s.When, StringComparer.Ordinal).ToList();
    }

    /// <summary>Import the player's ship from a save's data zip. Throws (for the caller to report) if it
    /// can't find the player record or that ship.</summary>
    public static ImportResult ImportPlayerShip(string zipPath, Catalog catalog)
    {
        using var zip = ZipFile.OpenRead(zipPath);

        var regId = PlayerShipRegId(zip)
            ?? throw new InvalidDataException("Couldn't find the player's ship in this save (no character record naming a current ship).");
        var shipEntry = zip.GetEntry($"ships/{regId}.json")
            ?? throw new InvalidDataException($"The player's ship '{regId}' is not among this save's ships.");

        var tmpl = ShipTemplate.ParseFile(ReadText(shipEntry)).OrderByDescending(s => s.Items.Count).FirstOrDefault()
            ?? throw new InvalidDataException($"The player's ship '{regId}' could not be parsed.");
        return TemplateImport.FromTemplate(tmpl, catalog);
    }

    /// <summary>The player character's current-ship RegID: the one zip-root record carrying <c>strShip</c>.</summary>
    private static string? PlayerShipRegId(ZipArchive zip)
    {
        foreach (var e in zip.Entries)
        {
            if (e.FullName.Contains('/') || !e.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            if (e.Name.Equals("saveInfo.json", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var doc = JsonDocument.Parse(ReadText(e));
                var el = Root(doc);
                if (Json.Str(el, "strShip") is { Length: > 0 } ship) return ship;
            }
            catch { /* not the player record — keep looking */ }
        }
        return null;
    }

    private static (string Ship, string Player, string When) ReadSaveInfo(string path)
    {
        if (!File.Exists(path)) return ("", "", "");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var el = Root(doc);
            return (Json.Str(el, "shipName") ?? "", Json.Str(el, "playerName") ?? "", Json.Str(el, "realWorldTime") ?? "");
        }
        catch { return ("", "", ""); }
    }

    private static JsonElement Root(JsonDocument doc) =>
        doc.RootElement is { ValueKind: JsonValueKind.Array } a && a.GetArrayLength() > 0 ? a[0] : doc.RootElement;

    private static string ReadText(ZipArchiveEntry e)
    {
        using var s = e.Open();
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
