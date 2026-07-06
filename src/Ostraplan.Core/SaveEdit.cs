using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ostraplan.Core;

/// <summary>Cargo that an inject would drop because its container was deleted — surfaced so the user can
/// confirm, or go empty it in-game first. <see cref="Items"/> are the contained items' friendly/def names.</summary>
public sealed record CargoLoss(string ContainerStrID, string ContainerName, IReadOnlyList<string> Items);

/// <summary>What an inject did (or would do): the structural change counts, any dropped cargo, soft
/// placement-law warnings (warn-and-allow), whether the grid had to grow, and the final grid size.</summary>
public sealed record InjectReport(
    int Kept, int Moved, int Added, int Deleted,
    IReadOnlyList<CargoLoss> CargoDropped,
    IReadOnlyList<string> Warnings,
    bool GridGrew, int NCols, int NRows);

/// <summary>
/// Writes an edited design back into the player's ship — the inject half of save-edit. It takes the edited
/// <see cref="ShipDocument"/> and its <see cref="SaveShipContext"/>, computes the structural
/// <see cref="ShipDiff"/>, and rebuilds the ship record: kept parts verbatim, moved parts repositioned
/// (cargo shifted with them), new parts as a fresh item entry <b>plus a synthesized pristine CO</b> (a save
/// load skips any item lacking one), deleted parts (and their cargo) dropped; <c>aRooms</c>/<c>aRating</c>/grid
/// are recomputed by the P2 engine, and every
/// other field — crew, world position, docking, economy, identity — is preserved verbatim off the retained
/// record. Then it writes to a <b>copy</b> of the save (the original is never opened for writing).
///
/// <para>Coordinate care (plan §6): kept items keep their world-absolute <c>fX/fY</c> verbatim (zero
/// rounding drift); new/moved items map document-tile → world in the <b>original</b> <c>vShipPos</c> frame;
/// the grid never shrinks below the original (so grid-relative indices like crew <c>nDestTile</c> stay put),
/// and only when a new part lands outside it does the grid grow — then crew <c>nDestTile</c> is recomputed
/// from each crew's world position.</para>
/// </summary>
public static class SaveEdit
{
    private const double MetresPerTile = 0.32;   // matches ShipExport

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    // ---- public API ----

    /// <summary>Build the rebuilt ship object (a standalone clone) + report, without any file I/O. Throws
    /// <see cref="InvalidDataException"/> on a hard integrity failure (dangling reference, lost CO, duplicate id);
    /// placement-law problems are collected as warnings, not thrown (warn-and-allow).</summary>
    public static (JsonObject Ship, InjectReport Report) BuildInjectedShip(
        ShipDocument doc, SaveShipContext ctx, Catalog catalog, IReadOnlyList<RoomSpecDef> specs)
    {
        var diff = ShipDiff.Compute(doc, ctx);

        // classify the origins we need to act on
        var moved = new Dictionary<string, Placement>(StringComparer.Ordinal);
        var deleted = new List<string>();
        foreach (var c in diff.Changes)
        {
            if (c.Kind == PartChangeKind.Moved && c.OriginStrID is { } m) moved[m] = c.Placement!;
            else if (c.Kind == PartChangeKind.Deleted && c.OriginStrID is { } d) deleted.Add(d);
        }
        var added = diff.OfKind(PartChangeKind.New).Select(c => c.Placement!).ToList();

        // the original grid frame (world) — new/moved parts map into it; kept items are already in it
        var vx0 = Dbl2(ctx.ShipRecord, "vShipPos", "x");
        var vy0 = Dbl2(ctx.ShipRecord, "vShipPos", "y");
        var nCols0 = Int(ctx.ShipRecord, "nCols");
        var nRows0 = Int(ctx.ShipRecord, "nRows");

        // drop set: deleted structural parts + their whole cargo subtrees; and the cargo-loss report
        var dropSet = new HashSet<string>(StringComparer.Ordinal);
        var cargoLosses = new List<CargoLoss>();
        foreach (var id in deleted)
        {
            dropSet.Add(id);
            var cargo = ctx.Origins[id].CargoIds;
            foreach (var cid in cargo) dropSet.Add(cid);
            if (cargo.Count > 0)
                cargoLosses.Add(new CargoLoss(id, ItemName(ctx, id), cargo.Select(cid => ItemName(ctx, cid)).ToList()));
        }

        // moved parts: new world pose + a delta to rigidly shift each part's cargo subtree
        var movedPose = new Dictionary<string, (double fx, double fy, double frot)>(StringComparer.Ordinal);
        var cargoDelta = new Dictionary<string, (double dx, double dy)>(StringComparer.Ordinal);
        foreach (var (id, p) in moved)
        {
            var (w, h) = Footprint(catalog, p);
            var pose = DocPoseToWorld(p, w, h, vx0, vy0);
            movedPose[id] = pose;
            if (ctx.ItemsById.TryGetValue(id, out var orig))
            {
                var (dx, dy) = (pose.fx - Dbl(orig, "fX"), pose.fy - Dbl(orig, "fY"));
                foreach (var cid in ctx.Origins[id].CargoIds) cargoDelta[cid] = (dx, dy);
            }
        }

        // rebuild aItems: surviving originals (kept verbatim, moved repositioned, cargo shifted) + new parts
        var outItems = new JsonArray();
        foreach (var it in Arr(ctx.ShipRecord, "aItems"))
        {
            var id = Str(it, "strID");
            if (id is not null && dropSet.Contains(id)) continue;
            var clone = it.DeepClone().AsObject();
            if (id is not null && movedPose.TryGetValue(id, out var mp))
            {
                clone["fX"] = mp.fx; clone["fY"] = mp.fy; clone["fRotation"] = mp.frot;
            }
            else if (id is not null && cargoDelta.TryGetValue(id, out var d))
            {
                clone["fX"] = Dbl(clone, "fX") + d.dx; clone["fY"] = Dbl(clone, "fY") + d.dy;
            }
            outItems.Add(clone);
        }

        // rebuild aCOs: keep every CO whose strID survived (kept/moved parts, all cargo, crew, loot-spawners)
        var outCOs = new JsonArray();
        foreach (var co in Arr(ctx.ShipRecord, "aCOs"))
        {
            var id = Str(co, "strID");
            if (id is not null && dropSet.Contains(id)) continue;
            outCOs.Add(co.DeepClone());
        }

        // new parts need BOTH a fresh aItems entry and a matching aCOs entry. Loading a save (unlike a template)
        // does NOT default a missing CO — DataHandler.SpawnItems skips any item whose strID isn't in dictCOSaves
        // ("Trying to load a CO ... with missing save data ... Skipping"). aConds=["DEFAULT"] makes CondOwner.SetData
        // repopulate the def's own starting conds on load, i.e. a pristine item exactly like one freshly built.
        foreach (var p in added)
        {
            var (w, h) = Footprint(catalog, p);
            var (fx, fy, frot) = DocPoseToWorld(p, w, h, vx0, vy0);
            var id = Guid.NewGuid().ToString();
            outItems.Add(new JsonObject
            {
                ["strName"] = p.DefName, ["fX"] = fx, ["fY"] = fy, ["fRotation"] = frot, ["strID"] = id,
            });
            outCOs.Add(SynthesizeCo(p.DefName, id, catalog, ctx.Source.RegId));
        }

        // grid frame: never shrink below the original (keeps nDestTile valid); grow to fit new parts
        int minC = 0, minR = 0, maxC = nCols0 - 1, maxR = nRows0 - 1;
        if (doc.Bounds() is { } b)
        {
            minC = Math.Min(minC, b.MinX); minR = Math.Min(minR, b.MinY);
            maxC = Math.Max(maxC, b.MaxX); maxR = Math.Max(maxR, b.MaxY);
        }
        var nColsNew = maxC - minC + 1;
        var nRowsNew = maxR - minR + 1;
        var grew = minC != 0 || minR != 0 || nColsNew != nCols0 || nRowsNew != nRows0;
        var vxNew = vx0 + minC;
        var vyNew = vy0 - minR;

        // recompute rooms + rating in that grid (the game recomputes on load; these need only be self-consistent)
        var grid = ShipGrid.FromDocumentFramed(doc, catalog, minC, minR, nColsNew, nRowsNew);
        var partition = RoomBuilder.Build(grid);
        RoomCertifier.CertifyAll(partition, specs, catalog);
        var rating = Rating.Calculate(grid, partition, catalog);

        var roomsArr = new JsonArray();
        foreach (var r in partition.Rooms)
        {
            var tiles = new JsonArray();
            foreach (var t in r.Tiles) tiles.Add(t);
            roomsArr.Add(new JsonObject
            {
                ["strID"] = Guid.NewGuid().ToString(),
                ["bVoid"] = r.Void,
                ["aTiles"] = tiles,
                ["roomSpec"] = r.RoomSpec,
                ["roomValue"] = r.Volume,
            });
        }
        var ratingArr = new JsonArray(
            rating.Epoch.Length == 0 ? "0" : rating.Epoch, rating.Condition, rating.RoomCount,
            rating.Maneuver, rating.Size, rating.Slot5);

        // if the grid grew, crew nDestTile (a flat index into nCols*nRows) is stale — recompute from world pos
        if (grew) RecomputeCrewDestTiles(ctx, outCOs, vxNew, vyNew, nColsNew, nRowsNew);

        // assemble: clone every original field verbatim, then overwrite only the structural ones
        var ship = new JsonObject();
        foreach (var kv in ctx.ShipRecord.AsObject())
        {
            if (kv.Key is "aItems" or "aCOs" or "aRooms" or "aRating" or "nCols" or "nRows" or "vShipPos" or "dimensions")
                continue;
            ship[kv.Key] = kv.Value?.DeepClone();
        }
        ship["aItems"] = outItems;
        ship["aCOs"] = outCOs;
        ship["aRooms"] = roomsArr;
        ship["aRating"] = ratingArr;
        ship["nCols"] = nColsNew;
        ship["nRows"] = nRowsNew;
        ship["vShipPos"] = new JsonObject { ["x"] = vxNew, ["y"] = vyNew };
        ship["dimensions"] = $"{nColsNew * MetresPerTile:0.00}m x {nRowsNew * MetresPerTile:0.00}m";

        Validate(ship, ctx, dropSet);

        var warnings = ProblemScan.Scan(doc, catalog)
            .Select(pr => $"{pr.Title}: {pr.Detail}")
            .ToList();

        var report = new InjectReport(
            diff.KeptCount, diff.MovedCount, diff.NewCount, diff.DeletedCount,
            cargoLosses, warnings, grew, nColsNew, nRowsNew);
        return (ship, report);
    }

    /// <summary>Build + write to a copy of the save. <paramref name="outputSaveDir"/> defaults to a sibling
    /// "<c>&lt;name&gt; (Ostraplan)</c>" folder; <paramref name="overwrite"/> must be set to replace an existing one.
    /// The original save is never touched. Returns where the copy landed + the report.</summary>
    public static (string OutputDir, InjectReport Report) Inject(
        ShipDocument doc, SaveShipContext ctx, Catalog catalog, IReadOnlyList<RoomSpecDef> specs,
        string? outputSaveDir = null, bool overwrite = false)
    {
        var (ship, report) = BuildInjectedShip(doc, ctx, catalog, specs);
        var outDir = outputSaveDir ?? DefaultOutputDir(ctx);
        WriteCopy(ctx, ship, outDir, overwrite);
        return (outDir, report);
    }

    /// <summary>The default output folder: a sibling of the source save named "<c>&lt;name&gt; (Ostraplan)</c>".</summary>
    public static string DefaultOutputDir(SaveShipContext ctx)
    {
        var srcDir = SourceDir(ctx);
        return Path.Combine(Path.GetDirectoryName(srcDir)!, $"{new DirectoryInfo(srcDir).Name} (Ostraplan)");
    }

    /// <summary>
    /// Duplicate the source save <b>folder</b> to <paramref name="outputSaveDir"/> and, inside the copy,
    /// replace only <c>ships/&lt;RegID&gt;.json</c> with <paramref name="ship"/>; the copied zip is renamed to match
    /// the new folder and its <c>saveInfo.json</c> <c>strName</c> is updated, so the copy shows as its own save.
    /// The original folder is never opened for writing.
    /// </summary>
    public static void WriteCopy(SaveShipContext ctx, JsonObject ship, string outputSaveDir, bool overwrite)
    {
        var srcDir = SourceDir(ctx);
        if (Directory.Exists(outputSaveDir))
        {
            if (!overwrite) throw new IOException($"'{Path.GetFileName(outputSaveDir)}' already exists.");
            Directory.Delete(outputSaveDir, recursive: true);
        }
        CopyDir(srcDir, outputSaveDir);

        var newName = new DirectoryInfo(outputSaveDir).Name;

        // rename the copied zip to "<newFolder>.zip" (the game's <folder>/<folder>.zip convention)
        var copiedZip = Path.Combine(outputSaveDir, Path.GetFileName(ctx.ZipPath));
        var targetZip = Path.Combine(outputSaveDir, newName + ".zip");
        if (!string.Equals(copiedZip, targetZip, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(targetZip)) File.Delete(targetZip);
            File.Move(copiedZip, targetZip);
        }

        // point saveInfo.json's display name at the copy
        var saveInfoPath = Path.Combine(outputSaveDir, "saveInfo.json");
        if (File.Exists(saveInfoPath)) UpdateSaveInfoName(saveInfoPath, newName);

        // splice ships/<RegID>.json inside the copied zip, preserving the file's array-or-object shape
        using var za = ZipFile.Open(targetZip, ZipArchiveMode.Update);
        var entryName = $"ships/{ctx.Source.RegId}.json";
        var entry = za.GetEntry(entryName)
            ?? throw new InvalidDataException($"'{entryName}' is not in the copied save.");
        string original;
        using (var r = new StreamReader(entry.Open())) original = r.ReadToEnd();
        var spliced = SpliceShip(JsonNode.Parse(original), ship);
        entry.Delete();
        var fresh = za.CreateEntry(entryName);
        using var w = new StreamWriter(fresh.Open());
        w.Write(spliced.ToJsonString(Indented));
    }

    // ---- internals ----

    /// <summary>Replace the ship element inside the file's original shape (a bare object, or the
    /// largest-by-items element of a top-level array), so sibling ships and the wrapping are preserved.</summary>
    private static JsonNode SpliceShip(JsonNode? top, JsonObject ship)
    {
        if (top is JsonArray arr)
        {
            int idx = -1, best = -1;
            for (var i = 0; i < arr.Count; i++)
                if (arr[i] is JsonObject o && o["aItems"] is JsonArray a && a.Count > best) { best = a.Count; idx = i; }
            if (idx >= 0) arr[idx] = ship; else arr.Add(ship);
            return arr;
        }
        return ship;
    }

    /// <summary>Hard integrity checks — throw to abort the whole inject before anything is written.</summary>
    private static void Validate(JsonObject ship, SaveShipContext ctx, HashSet<string> dropSet)
    {
        var itemIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var it in (ship["aItems"] as JsonArray)!)
        {
            var id = Str(it, "strID");
            if (id is not null && !itemIds.Add(id))
                throw new InvalidDataException($"Two items share strID '{id}' — inject aborted.");
        }
        var coIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var co in (ship["aCOs"] as JsonArray)!)
            if (Str(co, "strID") is { } id) coIds.Add(id);

        // THE invariant a save load enforces (DataHandler.SpawnItems): every item needs a matching CO, or the
        // game logs "missing save data" and silently skips it. This is a hard guard against a broken ship.
        foreach (var it in (ship["aItems"] as JsonArray)!)
        {
            var id = Str(it, "strID");
            if (id is { Length: > 0 } && !coIds.Contains(id) && !id.Contains("MP|"))
                throw new InvalidDataException(
                    $"Item '{id}' ({Str(it, "strName")}) has no condition owner — the game would skip it on load. Inject aborted.");
        }

        foreach (var it in (ship["aItems"] as JsonArray)!)
        {
            var parent = Str(it, "strParentID") ?? Str(it, "strSlotParentID");
            if (parent is { Length: > 0 } && !itemIds.Contains(parent) && !coIds.Contains(parent))
                throw new InvalidDataException($"Item '{Str(it, "strID")}' is parented to missing '{parent}' — inject aborted.");
        }
        foreach (var id in ctx.Origins.Keys)   // every surviving structural part must keep its condition owner
            if (!dropSet.Contains(id) && !coIds.Contains(id))
                throw new InvalidDataException($"Structural part '{id}' lost its condition owner — inject aborted.");
    }

    /// <summary>Recompute each crew member's flat destination-tile index from their world position in the new grid.</summary>
    private static void RecomputeCrewDestTiles(SaveShipContext ctx, JsonArray outCOs, double vx, double vy, int nCols, int nRows)
    {
        var crew = new Dictionary<string, (double fx, double fy)>(StringComparer.Ordinal);
        foreach (var c in Arr(ctx.ShipRecord, "aCrew"))
            if (Str(c, "strID") is { } id) crew[id] = (Dbl(c, "fX"), Dbl(c, "fY"));
        if (crew.Count == 0) return;

        foreach (var co in outCOs)
        {
            if (Str(co, "strID") is not { } id || !crew.TryGetValue(id, out var pos)) continue;
            var col = (int)Math.Round(pos.fx - vx, MidpointRounding.AwayFromZero);
            var row = -(int)Math.Round(pos.fy - vy, MidpointRounding.AwayFromZero);
            if (col >= 0 && col < nCols && row >= 0 && row < nRows)
                co!["nDestTile"] = col + row * nCols;
        }
    }

    /// <summary>Document top-left tile + rotation → game world centre (fX,fY) + CCW fRotation, anchored at
    /// (<paramref name="vsx"/>,<paramref name="vsy"/>) — the exact inverse of <see cref="ShipGrid.TemplateTile"/>.</summary>
    private static (double fx, double fy, double frot) DocPoseToWorld(Placement p, int w, int h, double vsx, double vsy) =>
        (p.X + (w / 2.0 - 0.5) + vsx, -(p.Y + (h / 2.0 - 0.5)) + vsy, GridMath.Norm(-p.Rot));

    private static (int W, int H) Footprint(Catalog catalog, Placement p) =>
        catalog.Lookup(p.DefName) is { } part ? GridMath.Size(part.Item.Width, part.Item.Height, p.Rot) : (1, 1);

    /// <summary>
    /// A pristine condition owner for a newly-added part, keyed to its item's <paramref name="strID"/>. A save
    /// load requires one CO per item; <c>aConds = ["DEFAULT"]</c> tells <c>CondOwner.SetData</c> to repopulate the
    /// def's own starting conds (verified against the decompile), so the part comes up freshly-built — the same
    /// pattern the game and <see cref="ShipExport"/> use. Cooverlay skins resolve through
    /// <c>DataHandler.GetCondOwnerDef</c> to their base def, so this works for any buildable def.
    /// </summary>
    private static JsonObject SynthesizeCo(string def, string strID, Catalog catalog, string regId)
    {
        var co = new JsonObject
        {
            ["strID"] = strID,
            ["strCODef"] = def,
            ["bAlive"] = true,
            ["aConds"] = new JsonArray("DEFAULT"),
            ["strCondID"] = def + strID,
            ["strIdleAnim"] = "Idle",
            ["strRegIDLast"] = regId,
        };
        if (catalog.Lookup(def)?.Friendly is { Length: > 0 } friendly) co["strFriendlyName"] = friendly;
        return co;
    }

    private static string SourceDir(SaveShipContext ctx) => Path.GetDirectoryName(ctx.ZipPath)!;

    private static void UpdateSaveInfoName(string path, string name)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path));
            var obj = node is JsonArray a && a.Count > 0 ? a[0] as JsonObject : node as JsonObject;
            if (obj is null) return;
            obj["strName"] = name;
            File.WriteAllText(path, node!.ToJsonString(Indented));
        }
        catch { /* a cosmetic name update must never fail the inject */ }
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.EnumerateFiles(src))
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.EnumerateDirectories(src))
            CopyDir(dir, Path.Combine(dst, Path.GetFileName(dir)));
    }

    private static string ItemName(SaveShipContext ctx, string strID) =>
        ctx.ItemsById.TryGetValue(strID, out var it) && Str(it, "strName") is { Length: > 0 } n ? n : strID;

    // ---- JsonNode field readers ----

    private static IEnumerable<JsonNode> Arr(JsonNode? ship, string prop) =>
        (ship as JsonObject)?[prop] is JsonArray a ? a.Where(n => n is not null).Select(n => n!) : [];

    private static string? Str(JsonNode? n, string prop) =>
        (n as JsonObject)?[prop] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static double Dbl(JsonNode? n, string prop) =>
        (n as JsonObject)?[prop] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0.0;

    private static double Dbl2(JsonNode? n, string a, string b) =>
        ((n as JsonObject)?[a] as JsonObject)?[b] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0.0;

    private static int Int(JsonNode? n, string prop) => (int)Math.Round(Dbl(n, prop), MidpointRounding.AwayFromZero);
}
