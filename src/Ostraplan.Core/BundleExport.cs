using System.IO;
using System.Text.Json.Nodes;

namespace Ostraplan.Core;

/// <summary>
/// One ship inside a mod: the design, what to call it, and how the game is to hand it out. Everything here is
/// per ship, because a bundle's whole point is that two ships in one mod need not be delivered the same way.
/// </summary>
/// <param name="Name">The design's name. Becomes the ship's <c>strName</c> unless it replaces something.</param>
/// <param name="ReplaceTarget">The <c>strName</c> of an existing ship this design replaces, or null to add a new
/// one. See <see cref="ExportOptions.ReplaceTarget"/>: it is the override key, so it wins over
/// <paramref name="Name"/>.</param>
/// <param name="Preview">The rendered preview art, or null to write none. Rendering needs the sprite atlas, which
/// only the app has, so the caller supplies encoded PNGs (see <see cref="ShipPreview"/>).</param>
public sealed record BundleShip(
    ShipDocument Doc,
    string Name,
    ExportMetadata Identity,
    WearOptions? Wear = null,
    ShipDelivery? Delivery = null,
    string? ReplaceTarget = null,
    ShipPreview? Preview = null)
{
    /// <summary>The name the game keys everything on: the ship object, the loot pools that spawn it, and the
    /// folder its art is filed under.</summary>
    public string StrName =>
        ReplaceTarget is { Length: > 0 } r && r.Trim() is { Length: > 0 } target ? target : Name;

    /// <summary>How this ship is to be obtained, never null.</summary>
    public ShipDelivery Routes => Delivery ?? ShipDelivery.None;
}

/// <summary>
/// What a mod is, as against what the ships in it are.
/// </summary>
/// <param name="ExclusiveStart">Pin the Shipbreaker start to this mod's ships alone, dropping the vanilla salvage
/// pods. It is a property of the <b>mod</b> rather than of a ship because the pool it pins is one pool: "only this
/// ship" cannot be said twice in a single mod, whereas "only this mod's ships" can.</param>
/// <param name="PreviouslyWritten">The ship <c>strName</c>s a previous export of this same mod wrote, from the
/// caller's own record of it. Unioned with what the mod folder itself still says, and used to take a dropped
/// ship's kiosk entries and preview art back out (see <see cref="OwnedNames"/>). Null when the caller keeps no
/// record, which is every single-ship export.</param>
public sealed record BundleOptions(
    string ModName, string Author, string Notes, string ModVersion, string GameVersion,
    string DestinationParent, IReadOnlyList<BundleShip> Ships,
    bool ExclusiveStart = false,
    IReadOnlyList<string>? PreviouslyWritten = null);

/// <summary>What one ship in the bundle turned out to be, once the engine had run over it.</summary>
public sealed record BundleShipResult(
    string StrName, string Name, int PartCount, int RoomCount, ShipRating Rating, int PreviewCount);

/// <summary>Where a bundle landed and what it contains. <see cref="RemovedArt"/> names the ships whose preview
/// folders were swept because the mod no longer holds them.</summary>
public sealed record BundleResult(
    string ModDir, string ShipJsonPath, string ModInfoPath,
    IReadOnlyList<BundleShipResult> Ships, IReadOnlyList<string> Warnings,
    IReadOnlyList<string> RemovedArt, bool TouchedLootPools);

/// <summary>
/// Writes a mod folder holding one or more ship designs: <c>mod_info.json</c>, a single
/// <c>data/ships/&lt;Mod&gt;.json</c> carrying every ship, a preview folder per ship, and the merged
/// <c>data/loot</c> / <c>data/lifeevents</c> / <c>data/interactions</c> that make them obtainable.
///
/// <para><b>Why several ships cannot simply be a loop over a one-ship export.</b> The game merges data by
/// <c>(type, strName)</c>, whole-object, with the last one loaded winning (GAME-INTERNALS §2). A kiosk pool is one
/// object, so two ships written independently produce two complete <c>RandomShipBrokerOKLG</c> objects and the
/// second erases the first. The pools therefore have to be merged here, once, with every ship appended into the
/// same clone. The same is true of the Shipbreaker events pool, which every starting ship in the bundle shares.
/// Nothing else needs merging: item and room ids are GUIDs, a ship's <c>strRegID</c> is overwritten on spawn, and
/// preview art is filed per ship name.</para>
///
/// <para><b>Nothing is written until everything has been built.</b> The engine runs over every design and the
/// whole folder is assembled in memory, then staged into a sibling directory, then moved into place. A failure
/// anywhere before the move leaves the previous export exactly as it was, rather than half of a new one that the
/// game would load and the user would have to unpick.</para>
///
/// <para><b>Never writes <c>loading_order.json</c></b>, the same as every other export path: registration stays
/// single-owner with Ostrasort/ModTools.</para>
/// </summary>
public static class BundleExport
{
    /// <summary>The suffix of the staging directory, a sibling of the mod folder so the commit is a move within
    /// one volume. Swept on the next export if a crash ever leaves one behind.</summary>
    private const string StagingSuffix = ".ostraplan-staging";

    // ---- validation ----

    /// <summary>
    /// Why this bundle cannot be written, or empty when it can. Every rule here is a collision that only exists
    /// once a mod holds more than one ship, plus the two per-ship refusals a mod export has always had.
    ///
    /// <para>Deliberately not a check on whether a ship can be obtained at all: a bare ship file is a legitimate
    /// thing to want (a modpack piece, loot wired by hand), and only the caller knows whether the user asked for
    /// one or forgot to. That refusal lives in the UI, where the question can be asked.</para>
    /// </summary>
    public static IReadOnlyList<string> Validate(BundleOptions opts)
    {
        var problems = new List<string>();
        if (opts.Ships.Count == 0)
        {
            problems.Add("A mod needs at least one ship in it.");
            return problems;
        }

        foreach (var ship in opts.Ships)
        {
            var label = ship.Name is { Length: > 0 } n ? $"\"{n}\"" : "A design";
            if (string.IsNullOrWhiteSpace(ship.Name))
                problems.Add("A design in this mod has no name. Every ship needs one: it is what the game keys the "
                             + "ship, its art and its kiosk listing on.");
            if (ship.Doc.Placements.Count == 0)
                problems.Add($"{label} has no parts in it.");
            if (ship.Doc.IsResidence)
                problems.Add($"{label} is an apartment, and an apartment can't be a ship mod. Every route a mod "
                             + "offers puts the design in front of a ship broker. Use \"Into a save game\" for it.");
        }

        foreach (var clash in opts.Ships.GroupBy(s => s.StrName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"Two or more ships in this mod are called \"{clash.Key}\". A ship's name is what the game "
                         + "keys its data and its pictures on, so they would overwrite each other. Rename one.");

        foreach (var clash in opts.Ships
                     .Where(s => s.ReplaceTarget is { Length: > 0 })
                     .GroupBy(s => s.ReplaceTarget!.Trim(), StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
            problems.Add($"Two ships in this mod both replace \"{clash.Key}\". Only one design can take over an "
                         + "existing ship's identity.");

        // A Special Offer pool is a single pinned ship at weight 1 (GAME-INTERNALS §19), so unlike a kiosk's
        // weighted stock there is no merge to make: a second claimant simply overwrites the first.
        foreach (var clash in opts.Ships
                     .SelectMany(s => s.Routes.SpecialOfferPools.Select(p => (Pool: p, Ship: s.StrName)))
                     .GroupBy(x => x.Pool, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1))
            problems.Add($"{string.Join(" and ", clash.Select(x => $"\"{x.Ship}\""))} are both set as the Special "
                         + $"Offer at {clash.Key}. That slot holds one ship. Pick one, or put the others in a "
                         + "broker kiosk instead.");

        return problems;
    }

    // ---- write ----

    /// <summary>
    /// Build every design and write the mod folder. Throws <see cref="ArgumentException"/> when
    /// <see cref="Validate"/> refuses, and rethrows any I/O failure for the caller to report, having first taken
    /// the half-written export back out.
    /// </summary>
    public static BundleResult Write(
        Catalog catalog, IReadOnlyList<RoomSpecDef> specs, BundleOptions opts, DataIndex? index = null)
    {
        if (Validate(opts) is { Count: > 0 } refusals)
            throw new ArgumentException("This mod can't be written:\n" + string.Join("\n", refusals), nameof(opts));

        var warnings = new List<string>();
        var folderName = ShipExport.SanitizeName(opts.ModName);
        var modDir = Path.Combine(opts.DestinationParent, folderName);
        var shipFileRel = Path.Combine("data", "ships", folderName + ".json");
        var shipPath = Path.Combine(modDir, shipFileRel);

        // Read while the folder is still the old one: what the mod held last time is the only record of a name it
        // put into the game's loot pools, and after the commit there is no way back to it.
        var previous = OwnedNames(ReadShipNames(shipPath).Concat(opts.PreviouslyWritten ?? []), opts.Ships);

        // ---- build, all of it, before a single file is touched ----

        var files = new StagedFiles();
        var results = new List<BundleShipResult>(opts.Ships.Count);
        var built = new List<ExportedShip>(opts.Ships.Count);

        foreach (var ship in opts.Ships)
        {
            var publicName = ShipExport.ResolvePublicName(ship.Identity.PublicName, ShipExport.VariedNames);
            var meta = ship.Identity with { PublicName = publicName };
            var (exported, rating, roomCount) =
                ShipExport.Build(ship.Doc, catalog, specs, ship.StrName, warnings, meta, ship.Wear);
            built.Add(exported);

            var previews = StagePreview(files, ship, warnings);
            results.Add(new BundleShipResult(
                ship.StrName, ship.Name, exported.AItems.Length, roomCount, rating, previews));
        }

        files.Text(shipFileRel, ShipExport.Serialize(built));
        files.Text("mod_info.json", ShipExport.SerializeModInfo(new ModInfo
        {
            StrName = opts.ModName,
            StrAuthor = opts.Author,
            StrGameVersion = opts.GameVersion,
            StrModVersion = string.IsNullOrWhiteSpace(opts.ModVersion) ? "1.0.0" : opts.ModVersion,
            StrNotes = string.IsNullOrWhiteSpace(opts.Notes) ? DefaultNotes(opts) : opts.Notes,
        }));

        var touchedLoot = StageDelivery(files, opts, previous, index, warnings);

        // ---- commit ----

        var orphans = OrphanedArt(modDir, previous, opts.Ships.Select(s => s.StrName));
        Commit(modDir, files, orphans);

        return new BundleResult(modDir, shipPath, Path.Combine(modDir, "mod_info.json"),
            results, warnings, orphans, touchedLoot);
    }

    /// <summary>The <c>mod_info</c> note when the user typed none: what the mod is, in the terms the MODS screen
    /// will show it in.</summary>
    private static string DefaultNotes(BundleOptions opts)
    {
        if (opts.Ships.Count > 1)
            return $"{opts.Ships.Count} ship designs exported from Ostraplan.";

        var only = opts.Ships[0];
        return only.ReplaceTarget is { Length: > 0 }
            ? $"Replaces \"{only.StrName}\" in-game with a design exported from Ostraplan."
            : $"\"{only.Name}\", a ship design exported from Ostraplan.";
    }

    // ---- delivery ----

    /// <summary>
    /// Stage the merged loot/lifeevent/interaction files: <b>one</b> object per pool however many ships are in it,
    /// because the game's whole-object merge would otherwise keep only the last. Returns whether any loot was
    /// written.
    ///
    /// <para>Every clone is stripped of the names this mod owns before the current ships are appended, so a ship
    /// since renamed or dropped from the bundle leaves no entry naming a template the mod no longer defines (see
    /// <see cref="KioskExport.StripShipsFromPool"/>).</para>
    /// </summary>
    private static bool StageDelivery(
        StagedFiles files, BundleOptions opts, IReadOnlyCollection<string> owned, DataIndex? index,
        List<string> warnings)
    {
        var wanted = opts.Ships.Any(s => s.Routes.TouchesLoot);
        if (!wanted)
        {
            // Nothing to deliver. The files are staged as deletions rather than skipped: a route the user has
            // taken away has to leave, or the mod goes on offering a ship nothing now points at.
            files.Delete(LootPath);
            files.Delete(LifePath);
            files.Delete(InteractionPath);
            return false;
        }

        if (index is null)
        {
            // Nothing is swept here on purpose: with no data to rebuild the pools from, the previous export's
            // files are a better state to leave behind than none at all.
            warnings.Add("Delivery options were set but no game data was available to resolve loot pools; skipped.");
            return false;
        }

        // One object per pool, cloned and stripped the first time a ship asks for it, appended to after that.
        var pools = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var order = new List<string>();
        JsonObject PoolFor(string name)
        {
            if (pools.TryGetValue(name, out var existing)) return existing;
            var pool = KioskExport.StripShipsFromPool(KioskExport.ClonePoolOrDefault(index, name), owned);
            pools[name] = pool;
            order.Add(name);
            return pool;
        }

        foreach (var ship in opts.Ships)
        {
            var routes = ship.Routes;
            foreach (var pool in routes.BrokerPools)
                KioskExport.AppendShipToPool(PoolFor(pool), ship.StrName, routes.BrokerWeight);
            foreach (var pool in routes.Derelicts)
                KioskExport.AppendShipToPool(PoolFor(pool), ship.StrName, routes.DerelictWeight);
            foreach (var pool in routes.SpecialOfferPools)
                KioskExport.PinShipToPool(PoolFor(pool), ship.StrName);
        }

        var loot = order.Select(name => pools[name]).ToList();
        var lifeevents = new List<JsonObject>();
        var interactions = new List<JsonObject>();

        var starters = opts.Ships.Where(s => s.Routes.StartingShip).ToList();
        if (starters.Count > 0)
        {
            // Every starting ship in the bundle contributes an intro to the one pool the Shipbreaker career rolls,
            // so the clone is shared and each Build appends into it. Passing exclusive here would pin the pool to
            // whichever ship happened to be last; the bundle-level flag below pins it to all of them at once.
            var events = KioskExport.StripShipsFromPool(
                KioskExport.ClonePoolOrDefault(index, StartingShipExport.ShipEventsPool),
                owned.Select(StartingShipExport.IntroName));

            foreach (var ship in starters)
            {
                var routes = ship.Routes;
                var frags = StartingShipExport.Build(
                    events, ship.StrName, routes.StartingShipWeight, routes.StartingShipStation,
                    routes.StartingShipMortgage,
                    string.IsNullOrWhiteSpace(routes.StartingShipTitle) ? ship.StrName + "." : routes.StartingShipTitle,
                    string.IsNullOrWhiteSpace(routes.StartingShipDesc)
                        ? $"You come across a listing for the {ship.StrName}. It could be your ticket out of the day-labour berth."
                        : routes.StartingShipDesc,
                    exclusive: false);

                // The events pool comes back among the fragments because it is the object we passed in. It is one
                // object shared by every starter, so it is added once, below, rather than once per ship.
                loot.AddRange(frags.LootObjects.Where(o => !ReferenceEquals(o, events)));
                lifeevents.AddRange(frags.Lifeevents);
                interactions.AddRange(frags.Interactions);
            }

            if (opts.ExclusiveStart)
                KioskExport.PinShipsToPool(events, starters.Select(s =>
                    (StartingShipExport.IntroName(s.StrName), s.Routes.StartingShipWeight)));

            loot.Add(events);
        }

        files.Json(LootPath, loot);
        files.Json(LifePath, lifeevents);
        files.Json(InteractionPath, interactions);
        return loot.Count > 0;
    }

    private static readonly string LootPath = Path.Combine("data", "loot", "loot.json");
    private static readonly string LifePath = Path.Combine("data", "lifeevents", "lifeevents.json");
    private static readonly string InteractionPath = Path.Combine("data", "interactions", "interactions.json");

    // ---- preview art ----

    /// <summary>
    /// Stage a ship's previews under <c>images/ships/&lt;strName&gt;/</c>, the one place the game looks for them
    /// (see <see cref="ShipPreview"/>). The whole-ship image takes the ship's own <c>strName</c> as its file name,
    /// which is both what chargen asks for by name and what the broker matches its main image on. A room thumbnail
    /// whose stem would collide with that is dropped rather than overwriting it.
    /// </summary>
    private static int StagePreview(StagedFiles files, BundleShip ship, List<string> warnings)
    {
        if (ship.Preview is not { } preview || preview.Ship.Length == 0) return 0;

        // The folder name IS the ship's strName, because that is the string the game builds its lookup path from.
        // Sanitising it would just file the art somewhere the game never looks, so a name that cannot be a folder
        // is reported instead: the ship still exports, it simply carries no picture.
        if (ship.StrName != ShipExport.SanitizeName(ship.StrName))
        {
            warnings.Add($"No preview art was written for \"{ship.StrName}\": that name cannot be a folder name, " +
                         "and the game looks for a ship's art under its name exactly. Rename it to give it a " +
                         "picture in game.");
            return 0;
        }

        var dir = ArtDir(ship.StrName);
        // The broker loads this folder wholesale, so a thumbnail left behind by an earlier export of a since
        // redesigned ship would keep showing a room that no longer exists. The game's own writer
        // (ScreenshotUtil.GetScreenShots) sweeps the same way.
        files.ClearPngs(dir);
        files.Bytes(Path.Combine(dir, ship.StrName + ".png"), preview.Ship);
        var written = 1;

        foreach (var room in preview.Rooms)
        {
            if (room.Png.Length == 0) continue;
            if (string.Equals(room.Name, ship.StrName, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"Skipped the \"{room.Name}\" room thumbnail: its name collides with the ship's own " +
                             "preview image.");
                continue;
            }
            files.Bytes(Path.Combine(dir, room.Name + ".png"), room.Png);
            written++;
        }
        return written;
    }

    private static string ArtDir(string strName) => Path.Combine("images", "ships", strName);

    /// <summary>
    /// The preview folders in an existing mod folder that belong to ships it no longer holds, so the caller can
    /// say what a re-export will remove before it removes it.
    ///
    /// <para>Only folders named by a ship this mod is known to have written are ever offered. A folder someone put
    /// there by hand is not this export's to delete, and cannot be told from one of ours by looking.</para>
    /// </summary>
    public static IReadOnlyList<string> OrphanedArt(
        string modDir, IEnumerable<string> previouslyWritten, IEnumerable<string> currentNames)
    {
        var current = new HashSet<string>(currentNames, StringComparer.OrdinalIgnoreCase);
        return [.. previouslyWritten
            .Where(n => !current.Contains(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => Directory.Exists(Path.Combine(modDir, ArtDir(n))))
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    // ---- what the mod owns ----

    /// <summary>
    /// The ship names this mod itself has put into the game's loot pools, and may therefore take back out.
    ///
    /// <para>It is every ship being written plus whatever the mod held before, because a design renamed or dropped
    /// between exports leaves its old name in a pool that this write is about to clone. A <b>replacement</b> owns
    /// no name: its <c>strName</c> is a core ship's, listed in core's own pools, and stripping that would take a
    /// vanilla ship out of the game. That subtraction runs last so it also catches the name arriving via
    /// <paramref name="previous"/>.</para>
    /// </summary>
    internal static IReadOnlyCollection<string> OwnedNames(
        IEnumerable<string> previous, IReadOnlyList<BundleShip> ships) =>
        OwnedNames(previous, ships.Select(s => (s.StrName, s.ReplaceTarget)));

    /// <inheritdoc cref="OwnedNames(IEnumerable{string}, IReadOnlyList{BundleShip})"/>
    internal static IReadOnlyCollection<string> OwnedNames(
        IEnumerable<string> previous, IEnumerable<(string StrName, string? ReplaceTarget)> ships)
    {
        var list = ships.ToList();
        var owned = new HashSet<string>(previous, StringComparer.Ordinal);
        foreach (var (strName, _) in list) owned.Add(strName);
        foreach (var (_, replaceTarget) in list)
            if (replaceTarget is { Length: > 0 } target) owned.Remove(target.Trim());
        return owned;
    }

    /// <summary>
    /// The <c>strName</c>s in a <c>data/ships</c> file this export is about to overwrite, or empty when there is
    /// no readable file there. Never throws: a file that cannot be read tells us nothing about what the mod used
    /// to hold, which is the same position as there being no file, and neither is a reason to refuse the export.
    /// </summary>
    internal static IReadOnlyList<string> ReadShipNames(string shipFilePath)
    {
        try
        {
            if (!File.Exists(shipFilePath)) return [];
            if (JsonNode.Parse(File.ReadAllText(shipFilePath)) is not JsonArray ships) return [];
            return [.. ships.OfType<JsonObject>()
                .Select(s => s["strName"]?.GetValue<string>())
                .Where(n => n is { Length: > 0 })
                .Select(n => n!)];
        }
        catch
        {
            return [];
        }
    }

    // ---- staging ----

    /// <summary>
    /// The mod folder as it is about to be, held in memory: the files to write, the ones to remove, and the
    /// preview folders to clear first. Assembling it before anything is touched is what lets a failed export
    /// leave the previous one intact.
    /// </summary>
    private sealed class StagedFiles
    {
        public Dictionary<string, byte[]> Write { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Remove { get; } = [];
        public List<string> ClearedDirs { get; } = [];

        public void Text(string relative, string content) =>
            Write[relative] = new System.Text.UTF8Encoding(false).GetBytes(content);

        public void Bytes(string relative, byte[] content) => Write[relative] = content;

        /// <summary>Write a data file, or take away the one that is there when this export has nothing to put in
        /// it.</summary>
        public void Json(string relative, IReadOnlyList<JsonObject> objects)
        {
            if (objects.Count == 0) { Delete(relative); return; }
            var arr = new JsonArray();
            foreach (var o in objects) arr.Add(o.DeepClone());   // DeepClone: a node can't be re-parented into two arrays
            Text(relative, arr.ToJsonString(JsonFormat));
        }

        public void Delete(string relative) => Remove.Add(relative);

        public void ClearPngs(string relativeDir) => ClearedDirs.Add(relativeDir);
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonFormat = new() { WriteIndented = true };

    /// <summary>
    /// Put the assembled folder on disk: stage every file into a sibling directory, then move it into place, then
    /// take away what this export no longer holds. A failure during staging leaves the previous export untouched;
    /// the staging directory goes either way.
    /// </summary>
    private static void Commit(string modDir, StagedFiles files, IReadOnlyList<string> orphanedArt)
    {
        var staging = modDir + StagingSuffix;
        SweepStale(staging);
        var createdModDir = !Directory.Exists(modDir);

        try
        {
            foreach (var (relative, content) in files.Write)
            {
                var path = Path.Combine(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, content);
            }

            // Everything below this line is the short window: local moves and deletes inside one folder.
            foreach (var dir in files.ClearedDirs)
            {
                var target = Path.Combine(modDir, dir);
                if (!Directory.Exists(target)) continue;
                foreach (var stale in Directory.EnumerateFiles(target, "*.png")) File.Delete(stale);
            }

            foreach (var relative in files.Write.Keys)
            {
                var target = Path.Combine(modDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(Path.Combine(staging, relative), target, overwrite: true);
            }

            foreach (var relative in files.Remove) RemoveIfPresent(Path.Combine(modDir, relative));
            foreach (var name in orphanedArt) RemoveArtFolder(modDir, name);
        }
        catch
        {
            // Take the half-written export back out rather than leave the game something broken to load. Only a
            // folder this call created is removed: an existing mod folder holds files that are not ours.
            SweepStale(staging);
            if (createdModDir && Directory.Exists(modDir))
                try { Directory.Delete(modDir, recursive: true); } catch { /* best effort; the throw below is what matters */ }
            throw;
        }

        SweepStale(staging);
    }

    private static void SweepStale(string staging)
    {
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
    }

    /// <summary>Delete a data file this mod wrote before, and its folder with it once that folder is empty, so a
    /// removed delivery leaves no trace rather than an empty <c>data/loot</c>.</summary>
    private static void RemoveIfPresent(string path)
    {
        if (!File.Exists(path)) return;
        File.Delete(path);
        var dir = Path.GetDirectoryName(path);
        if (dir is not null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            Directory.Delete(dir);
    }

    /// <summary>Take away the preview folder of a ship this mod no longer holds. PNGs only, and the folder itself
    /// only once nothing else is in it: whatever else someone filed there is not this export's to delete.</summary>
    private static void RemoveArtFolder(string modDir, string strName)
    {
        var dir = Path.Combine(modDir, ArtDir(strName));
        if (!Directory.Exists(dir)) return;
        foreach (var png in Directory.EnumerateFiles(dir, "*.png")) File.Delete(png);
        if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
    }
}
