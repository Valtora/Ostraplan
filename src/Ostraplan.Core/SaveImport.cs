using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace Ostraplan.Core;

/// <summary>
/// A save game on disk, for the picker: the save's folder name, the player's ship name and character (from
/// <c>saveInfo.json</c>), when it was written, and the path to its data zip.
///
/// <para><see cref="PlayTimeSeconds"/> and <see cref="GameVersion"/> are what the game's own Load screen shows
/// beside each save, and are what tell two saves of the same character at the same station apart. Every one of
/// them defaults to nothing, since a save folder can be missing its <c>saveInfo.json</c> entirely and still hold a
/// readable ship.</para>
///
/// <para><see cref="GameVersion"/> is the build the save was <b>created</b> on (<c>version</c>), which is the one
/// the game's Load screen shows; <see cref="LastSavedVersion"/> is the build that last wrote it
/// (<c>versionLastSave</c>). They differ on any save carried across a game update, and the pair is worth having:
/// the first is what the player recognises the save by, the second describes what is actually on disk.</para>
/// </summary>
public sealed record SaveEntry(
    string Name, string ShipName, string PlayerName, string When, string ZipPath,
    double PlayTimeSeconds = 0, string GameVersion = "", string LastSavedVersion = "");

/// <summary>A ship in a save the player might edit: its RegID, a friendly display name and subtitle, whether the
/// player <see cref="Owned">owns</see> it (it's in the player CO's <c>aMyShips</c>), and whether it's the ship the
/// player is currently standing on. A non-owned ship is a station or another vessel — editable, but unsupported.</summary>
public sealed record SaveShipChoice(string RegId, string Name, string Sub, bool Owned, bool Current);

/// <summary>The save's session (character) record in summary: which ship the player is standing on
/// (<c>strShip</c>), their character CO id (<c>strPlayerCO</c>, the CO carrying the money balance and the owned-
/// ship list), the current game epoch, and the record's own zip entry name — which a writer needs, because
/// ownership lives in that record and nowhere else.</summary>
internal sealed record SessionRecord(string ShipRegId, string? PlayerCoId, double Epoch, string EntryName);

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
            var zip = DataZip(sub);
            if (zip is null) continue;
            var info = ReadSaveInfo(Path.Combine(sub, "saveInfo.json"));
            list.Add(new SaveEntry(Path.GetFileName(sub), info.Ship, info.Player, info.When, zip,
                info.PlayTime, info.Version, info.LastSaved));
        }
        return list.OrderByDescending(s => s.When, StringComparer.Ordinal).ToList();
    }

    /// <summary>The save folder's own data zip. Ostranauts writes exactly one, named after the folder, but a user
    /// who has been poking at a save can leave a backup or an extracted copy beside it, and taking whichever the
    /// filesystem lists first would then read the wrong archive and report the save as unreadable.</summary>
    private static string? DataZip(string dir)
    {
        var zips = Directory.EnumerateFiles(dir, "*.zip").ToList();
        if (zips.Count <= 1) return zips.FirstOrDefault();
        var expected = Path.GetFileName(dir) + ".zip";
        return zips.FirstOrDefault(z => string.Equals(Path.GetFileName(z), expected, StringComparison.OrdinalIgnoreCase))
               ?? zips.OrderByDescending(z => new FileInfo(z).Length).First();
    }

    /// <summary>Import the player's ship from a save's data zip. Throws (for the caller to report) if it
    /// can't find the player record or that ship.</summary>
    public static ImportResult ImportPlayerShip(string zipPath, Catalog catalog)
    {
        using var zip = ZipFile.OpenRead(zipPath);

        var regId = PlayerShipRegId(zip, out var why)
            ?? throw new InvalidDataException(NoSessionMessage(why));
        var shipEntry = zip.GetEntry($"ships/{regId}.json")
            ?? throw new InvalidDataException($"The player's ship '{regId}' is not among this save's ships.");

        var tmpl = ParseShip(ReadText(shipEntry), shipEntry.FullName, regId)
            .OrderByDescending(s => s.Items.Count).First();
        return TemplateImport.FromTemplate(tmpl, catalog);
    }

    /// <summary>Import a <b>named</b> ship's layout from a save's data zip — the same pristine, layout-only read as
    /// <see cref="ImportPlayerShip"/>, for a caller that has already picked which ship it wants (see
    /// <see cref="ListPlayerShips"/>). No save identity is retained: for the write-back path that keeps it, see
    /// <see cref="SaveEditImport"/>. Throws (for the caller to report) if that ship is not in the save.</summary>
    public static ImportResult ImportShipLayout(string zipPath, string regId, Catalog catalog)
    {
        using var zip = ZipFile.OpenRead(zipPath);

        var shipEntry = zip.GetEntry($"ships/{regId}.json")
            ?? throw new InvalidDataException($"The ship '{regId}' is not among this save's ships.");

        var tmpl = ParseShip(ReadText(shipEntry), shipEntry.FullName, regId)
            .OrderByDescending(s => s.Items.Count).First();
        return TemplateImport.FromTemplate(tmpl, catalog);
    }

    /// <summary>Parse one <c>ships/*.json</c> record, throwing with the reason when nothing ship-shaped comes out.
    /// Shared with <see cref="SaveEditImport"/> so both import paths report a bad record the same way. Takes the
    /// already-read text, because the caller that needs it twice should not decompress it twice.</summary>
    internal static IReadOnlyList<ShipTemplate> ParseShip(string text, string entryName, string regId)
    {
        var ships = ShipTemplate.ParseFileChecked(text, out var failure);
        return ships.Count > 0
            ? ships
            : throw new InvalidDataException(
                $"The ship '{regId}' could not be parsed.\n\n{entryName} in this save: {failure}");
    }

    /// <summary>The player character's current-ship RegID: the one zip-root record carrying <c>strShip</c>.
    /// Shared with <see cref="SaveEditImport"/>.</summary>
    internal static string? PlayerShipRegId(ZipArchive zip) => ReadSession(zip)?.ShipRegId;

    /// <summary>As <see cref="PlayerShipRegId(ZipArchive)"/>, reporting what each candidate record was rejected
    /// for when none of them named a ship.</summary>
    internal static string? PlayerShipRegId(ZipArchive zip, out string? why) => ReadSession(zip, out why)?.ShipRegId;

    /// <summary>The message for a save with no usable character record, with the per-record reasons appended when
    /// there are any. Shared with <see cref="SaveEditImport"/>.</summary>
    internal static string NoSessionMessage(string? why) =>
        "Couldn't find the player's ship in this save (no character record naming a current ship)."
        + (why is null ? "" : "\n\n" + why);

    /// <summary>The player character CO id (<c>strPlayerCO</c>) from the session record — the CO carrying the
    /// authoritative <c>StatUSD</c> money balance. Shared with <see cref="SaveEditImport"/>.</summary>
    internal static string? PlayerCoId(ZipArchive zip) => ReadSession(zip)?.PlayerCoId;

    /// <summary>The save's current game epoch (<c>objSystem.dfEpoch</c> on the session record), or 0.</summary>
    internal static double SessionEpoch(ZipArchive zip) => ReadSession(zip)?.Epoch ?? 0;

    /// <summary>Everything the session (character) record is asked for, read in <b>one</b> parse: which ship the
    /// player is on, their CO id, the game epoch, and the record's own entry name. That record is the biggest
    /// thing in a save (tens of MB on a mature one), so a caller needing more than one of these should take them
    /// from here rather than calling the single-value helpers in sequence. Null when no root record carries
    /// <c>strShip</c>.</summary>
    internal static SessionRecord? ReadSession(ZipArchive zip) => ReadSession(zip, out _);

    /// <summary>
    /// As <see cref="ReadSession(ZipArchive)"/>, collecting why each candidate record was passed over.
    ///
    /// <para><paramref name="why"/> is null on success and on a save whose records simply are not the one being
    /// looked for. It is filled when nothing matched, because "no character record naming a current ship" is
    /// equally what a save with a <b>damaged</b> character record produces, and those two want opposite responses
    /// from the user.</para>
    /// </summary>
    internal static SessionRecord? ReadSession(ZipArchive zip, out string? why)
    {
        var rejected = new List<string>();
        var candidates = 0;

        foreach (var e in zip.Entries)
        {
            if (e.FullName.Contains('/') || !e.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            if (e.Name.Equals("saveInfo.json", StringComparison.OrdinalIgnoreCase)) continue;
            candidates++;

            string text;
            try { text = ReadText(e); }
            catch (Exception ex)
            {
                rejected.Add($"{e.FullName}: couldn't be read from the zip ({ex.Message})");
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(text);
                var el = Root(doc);
                if (Json.Str(el, "strShip") is not { Length: > 0 } ship)
                {
                    rejected.Add($"{e.FullName}: parsed, but carries no strShip naming a ship");
                    continue;
                }
                var epoch = el.TryGetProperty("objSystem", out var sys)
                    && sys.TryGetProperty("dfEpoch", out var ep) && ep.TryGetDouble(out var v) ? v : 0;
                why = null;
                return new SessionRecord(ship, Json.Str(el, "strPlayerCO"), epoch, e.FullName);
            }
            catch (JsonException ex)
            {
                rejected.Add($"{e.FullName}: {JsonDiagnostic.Describe(ex, text)}");
            }
            catch (Exception ex)
            {
                rejected.Add($"{e.FullName}: {ex.Message}");
            }
        }

        why = candidates == 0
            ? "This save's zip holds no top-level record at all (no *.json beside the ships folder), so there is "
              + "nothing to read the player's ship from. The save may be incomplete or truncated."
            : string.Join("\n\n", rejected);
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

    /// <summary>What a save's <c>saveInfo.json</c> tells the picker. Blank throughout when the file is missing or
    /// unreadable — a save can still be imported from without it, so this never throws.
    /// <para><c>playTimeElapsed</c> is <b>seconds</b>, verified against the game's own Load screen (a save reading
    /// 16539.0 there displays "4h 35m 39s").</para></summary>
    private static (string Ship, string Player, string When, double PlayTime, string Version, string LastSaved)
        ReadSaveInfo(string path)
    {
        if (!File.Exists(path)) return ("", "", "", 0, "", "");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var el = Root(doc);
            return (Json.Str(el, "shipName") ?? "", Json.Str(el, "playerName") ?? "",
                Json.Str(el, "realWorldTime") ?? "", Json.Dbl(el, "playTimeElapsed"),
                Json.Str(el, "version") ?? "", Json.Str(el, "versionLastSave") ?? "");
        }
        catch { return ("", "", "", 0, "", ""); }
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
