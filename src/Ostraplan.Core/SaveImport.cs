using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace Ostraplan.Core;

/// <summary>A save game on disk, for the picker: the save's folder name, the player's ship name
/// and character (from <c>saveInfo.json</c>), when it was written, and the path to its data zip.</summary>
public sealed record SaveEntry(string Name, string ShipName, string PlayerName, string When, string ZipPath);

/// <summary>A ship in a save the player might edit: its RegID, a friendly display name and subtitle, whether the
/// player <see cref="Owned">owns</see> it (it's in the player CO's <c>aMyShips</c>), and whether it's the ship the
/// player is currently standing on. A non-owned ship is a station or another vessel — editable, but unsupported.</summary>
public sealed record SaveShipChoice(string RegId, string Name, string Sub, bool Owned, bool Current);

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

    /// <summary>The player character's current-ship RegID: the one zip-root record carrying <c>strShip</c>.
    /// Shared with <see cref="SaveEditImport"/>.</summary>
    internal static string? PlayerShipRegId(ZipArchive zip) =>
        PlayerRecord(zip) is { } el && Json.Str(el, "strShip") is { Length: > 0 } s ? s : null;

    /// <summary>The player character CO id (<c>strPlayerCO</c>) from the session record — the CO carrying the
    /// authoritative <c>StatUSD</c> money balance. Shared with <see cref="SaveEditImport"/>.</summary>
    internal static string? PlayerCoId(ZipArchive zip) =>
        PlayerRecord(zip) is { } el && Json.Str(el, "strPlayerCO") is { Length: > 0 } s ? s : null;

    /// <summary>The save's current game epoch (<c>objSystem.dfEpoch</c> on the session record), or 0.</summary>
    internal static double SessionEpoch(ZipArchive zip) =>
        PlayerRecord(zip) is { } el && el.TryGetProperty("objSystem", out var sys)
            && sys.TryGetProperty("dfEpoch", out var ep) && ep.TryGetDouble(out var v) ? v : 0;

    /// <summary>The zip-root session record — the one JSON at the archive root (not <c>saveInfo.json</c>) that
    /// carries <c>strShip</c>. Returns its root element (unwrapping a single-element array), or null.</summary>
    private static JsonElement? PlayerRecord(ZipArchive zip)
    {
        foreach (var e in zip.Entries)
        {
            if (e.FullName.Contains('/') || !e.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            if (e.Name.Equals("saveInfo.json", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var doc = JsonDocument.Parse(ReadText(e));
                var el = Root(doc);
                if (Json.Str(el, "strShip") is { Length: > 0 }) return el.Clone();   // clone: outlives the JsonDocument
            }
            catch { /* not the player record — keep looking */ }
        }
        return null;
    }

    /// <summary>
    /// The ships in a save the player could edit: every ship the player owns (the player CO's <c>aMyShips</c>),
    /// plus the ship they're currently standing on if that isn't one of them (a station or another vessel — the
    /// currently-occupied ship is what a naive import would grab). Owned ships come first, the current ship is
    /// flagged. Never throws — returns an empty list if the save can't be read.
    /// </summary>
    public static IReadOnlyList<SaveShipChoice> ListPlayerShips(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var current = PlayerShipRegId(zip);
            var owned = ReadMyShips(zip, current, PlayerCoId(zip));

            var order = new List<string>();
            foreach (var r in owned) if (!order.Contains(r)) order.Add(r);
            if (current is not null && !order.Contains(current)) order.Add(current);   // current-but-not-owned goes last

            var result = new List<SaveShipChoice>();
            foreach (var reg in order)
            {
                var (name, sub) = ShipDisplay(zip, reg);
                result.Add(new SaveShipChoice(reg, name ?? reg, sub, owned.Contains(reg), reg == current));
            }
            return result;
        }
        catch { return []; }
    }

    /// <summary>The player CO's <c>aMyShips</c> (owned ship RegIDs), read from the player CO on the ship they're
    /// currently on. Empty if the record/CO can't be found.</summary>
    private static IReadOnlyList<string> ReadMyShips(ZipArchive zip, string? currentShipReg, string? playerCoId)
    {
        if (currentShipReg is null || playerCoId is null) return [];
        if (zip.GetEntry($"ships/{currentShipReg}.json") is not { } entry) return [];
        try
        {
            using var doc = JsonDocument.Parse(ReadText(entry));
            if (LargestShipEl(doc.RootElement) is not { } ship
                || !ship.TryGetProperty("aCOs", out var cos) || cos.ValueKind != JsonValueKind.Array) return [];
            foreach (var co in cos.EnumerateArray())
                if (Json.Str(co, "strID") == playerCoId)
                    return Json.StrArray(co, "aMyShips");
        }
        catch { /* unreadable ship record -> no owned ships */ }
        return [];
    }

    /// <summary>A ship's display name + subtitle from its record: publicName / make+model / RegID, and a
    /// "make model · designation" subtitle.</summary>
    private static (string? Name, string Sub) ShipDisplay(ZipArchive zip, string reg)
    {
        if (zip.GetEntry($"ships/{reg}.json") is not { } entry) return (null, reg);
        try
        {
            using var doc = JsonDocument.Parse(ReadText(entry));
            if (LargestShipEl(doc.RootElement) is not { } s) return (null, reg);
            var make = Json.Str(s, "make") ?? "";
            var model = Json.Str(s, "model") ?? "";
            var designation = Json.Str(s, "designation") ?? "";
            var makeModel = $"{make} {model}".Trim();
            var name = Json.Str(s, "publicName") is { Length: > 0 } pub ? pub
                : makeModel.Length > 0 ? makeModel
                : Json.Str(s, "strName");
            var sub = string.Join("  ·  ", new[] { makeModel, designation, reg }.Where(x => x.Length > 0));
            return (name, sub);
        }
        catch { return (null, reg); }
    }

    /// <summary>The ship object with the most items in a ships/*.json (a file is one ship or an array of ships).</summary>
    private static JsonElement? LargestShipEl(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            JsonElement? best = null;
            var bestN = -1;
            foreach (var e in root.EnumerateArray())
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("aItems", out var a)
                    && a.ValueKind == JsonValueKind.Array && a.GetArrayLength() > bestN)
                { bestN = a.GetArrayLength(); best = e; }
            return best;
        }
        return root.ValueKind == JsonValueKind.Object ? root : null;
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

    internal static string ReadText(ZipArchiveEntry e)
    {
        using var s = e.Open();
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
