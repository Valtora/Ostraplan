using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ostraplan.Core;

/// <summary>Cargo that an inject would drop because its container was deleted — surfaced so the user can
/// confirm, or go empty it in-game first. <see cref="Items"/> are the contained items' friendly/def names.</summary>
public sealed record CargoLoss(string ContainerStrID, string ContainerName, IReadOnlyList<string> Items);

/// <summary>An opt-in deduction of the edit's cost from the player's credits: the player CO to charge, the
/// amount, and the resulting balance to write into that CO's <c>StatUSD</c> and <c>saveInfo.money</c>.</summary>
public sealed record EditCharge(string PlayerCoId, double Amount, double NewBalance);

/// <summary>What an inject did (or would do): the structural change counts, any dropped cargo, soft
/// placement-law warnings (warn-and-allow), whether the grid had to grow, the final grid size, whether the ship
/// was armed to refill its atmosphere on load, and — when the cost was deducted — the amount charged and the
/// resulting balance.</summary>
public sealed record InjectReport(
    int Kept, int Moved, int Added, int Deleted,
    IReadOnlyList<CargoLoss> CargoDropped,
    IReadOnlyList<string> Warnings,
    bool GridGrew, int NCols, int NRows,
    double? Charged = null, double? ResultingBalance = null,
    bool AtmosphereFilled = false, int PowerFixed = 0);

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
        ShipDocument doc, SaveShipContext ctx, Catalog catalog, IReadOnlyList<RoomSpecDef> specs, EditCharge? charge = null)
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
        // Cargo reconciliation is driven by the CURRENT contents of every surviving container (Placement.Cargo):
        // an original contained item still present is KEPT (written verbatim, by strID); an original no longer
        // present was removed (from a kept container) or lost (with a deleted container) and is dropped; an
        // AUTHORED item is synthesized as a fresh item + CO below. keepSet = the original cargo strIDs that
        // survive, so the drop set is every original contained item NOT in it. This subsumes slice 1's re-skin
        // transfer — cargo carried onto a re-skinned container rides in that new part's Placement.Cargo, so it
        // lands in keepSet and is re-parented (not dropped) below.
        var keepSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chg in diff.Changes)
            if (chg.Kind != PartChangeKind.Deleted && chg.Placement is { } sp)
                foreach (var node in sp.Cargo)
                    foreach (var d in Subtree(node))
                        if (!d.Authored) keepSet.Add(d.StrID);

        // the original grid frame (world) — new/moved parts map into it; kept items are already in it
        var vx0 = Dbl2(ctx.ShipRecord, "vShipPos", "x");
        var vy0 = Dbl2(ctx.ShipRecord, "vShipPos", "y");
        var nCols0 = Int(ctx.ShipRecord, "nCols");
        var nRows0 = Int(ctx.ShipRecord, "nRows");

        // drop set: deleted structural parts, plus every ORIGINAL contained item that no longer survives — removed
        // from a kept container, or lost with a deleted one (both fall out of keepSet). The cargo-loss REPORT is
        // scoped to deleted containers: an item deliberately removed from a kept container in the editor is an
        // intended edit, not a loss to warn about (so a content edit fires no false "cargo will be deleted").
        var dropSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in deleted) dropSet.Add(id);
        foreach (var origin in ctx.Origins.Values)
            foreach (var cid in origin.CargoIds)
                if (!keepSet.Contains(cid)) dropSet.Add(cid);

        var cargoLosses = new List<CargoLoss>();
        foreach (var id in deleted)
        {
            var lost = ctx.Origins[id].CargoIds.Where(cid => !keepSet.Contains(cid)).ToList();
            if (lost.Count > 0)
                cargoLosses.Add(new CargoLoss(id, ItemName(ctx, id), lost.Select(cid => ItemName(ctx, cid)).ToList()));
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

        // rebuild aItems: surviving originals (kept verbatim, moved repositioned, cargo shifted) + new parts.
        // Index the survivors by strID so a re-skinned container can re-parent its transferred cargo below.
        var outItems = new JsonArray();
        var outItemsById = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
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
            if (id is not null) outItemsById[id] = clone;
        }

        // rebuild aCOs: keep every CO whose strID survived (kept/moved parts, all cargo, crew, loot-spawners).
        // Self-heal along the way: a kept powered device whose CO lost its Power ticker (it was installed before
        // the data granted the ticker) is permanently dead — the game loads tickers from the save, not the def.
        // Re-attach the missing Power ticker so it can draw power again (the ShipsWater fix, at the data level).
        var outCOs = new JsonArray();
        var outCosById = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var powerFixed = 0;
        foreach (var co in Arr(ctx.ShipRecord, "aCOs"))
        {
            var id = Str(co, "strID");
            if (id is not null && dropSet.Contains(id)) continue;
            var clone = co.DeepClone().AsObject();
            if (Str(clone, "strCODef") is { } cdef && catalog.Lookup(cdef) is { } cpart
                && BakeTickers(clone, cpart, ctx.Epoch, catalog, onlyPower: true) > 0)
                powerFixed++;
            outCOs.Add(clone);
            if (id is not null) outCosById[id] = clone;   // for re-positioning moved/rotated original cargo below
        }

        // opt-in cost deduction: rewrite the player CO's StatUSD (the authoritative balance) to the new total.
        // The player CO is crew on their own ship, so it's one of the kept COs above; saveInfo.money is mirrored
        // at write time. The UI has already checked affordability, so a missing player CO here is a hard error.
        if (charge is { } ch)
        {
            var applied = false;
            foreach (var co in outCOs)
                if (co is JsonObject o && Str(o, "strID") == ch.PlayerCoId) { SetStatUsd(o, ch.NewBalance); applied = true; break; }
            if (!applied)
                throw new InvalidDataException(
                    "The player's money couldn't be found on this ship, so the edit cost can't be deducted. " +
                    "Uncheck \"Deduct edit cost\" and try again.");
        }

        // Emit contained cargo for every surviving container from its current Placement.Cargo — the tree is the
        // authoritative structure. An AUTHORED item becomes a fresh item + synthesized-CO entry (a save load skips
        // any item lacking a CO). An ORIGINAL item was already written verbatim above; here its parent, grid
        // position and rotation are reconciled to the tree so a re-parent (re-skin / move between containers), a
        // rearrange, or a rotate persists — writes are guarded to what actually changed, so an untouched item (or a
        // non-grid contained item like a nav module) is left exactly as it was. Recurses for nesting; a stack's
        // members are verbatim (parented to their lead) so we don't descend into them.
        void EmitCargo(IReadOnlyList<CargoItem> nodes, string parentId, double px, double py)
        {
            foreach (var c in nodes)
            {
                if (c.Authored)
                {
                    var cid = Guid.NewGuid().ToString();
                    var citem = new JsonObject
                    {
                        ["strName"] = c.DefName, ["fX"] = px, ["fY"] = py, ["fRotation"] = (double)c.GridRot, ["strID"] = cid,
                        [c.Slotted ? "strSlotParentID" : "strParentID"] = parentId,
                    };
                    outItems.Add(citem);
                    outItemsById[cid] = citem;
                    var cco = SynthesizeCo(c.DefName, cid, catalog, ctx.Source.RegId, ctx.Epoch);
                    if (c.GridX != 0 || c.GridY != 0) { cco["inventoryX"] = c.GridX; cco["inventoryY"] = c.GridY; }
                    outCOs.Add(cco);
                    EmitCargo(c.Children, cid, px, py);   // stack members / nested authored contents
                }
                else if (outItemsById.TryGetValue(c.StrID, out var orig))
                {
                    var parentField = c.Slotted ? "strSlotParentID" : "strParentID";
                    if (Str(orig, parentField) != parentId) orig[parentField] = parentId;   // re-skin / cross-container move
                    var origItem = ctx.ItemsById.GetValueOrDefault(c.StrID);
                    if (origItem is null || GridMath.Norm((int)Math.Round(Dbl(origItem, "fRotation"))) != c.GridRot)
                        orig["fRotation"] = (double)c.GridRot;                                // inventory rotation
                    if (outCosById.TryGetValue(c.StrID, out var co))
                    {
                        var origCo = ctx.CosById.GetValueOrDefault(c.StrID);
                        if (origCo is null || Int(origCo, "inventoryX") != c.GridX) co["inventoryX"] = c.GridX;
                        if (origCo is null || Int(origCo, "inventoryY") != c.GridY) co["inventoryY"] = c.GridY;
                    }
                    if (!c.IsStack)   // real container: recurse; a stack's members stay verbatim under their lead
                        EmitCargo(c.Children, c.StrID, Dbl(orig, "fX"), Dbl(orig, "fY"));
                }
            }
        }

        // new structural parts need BOTH a fresh aItems entry and a matching aCOs entry. Loading a save (unlike a
        // template) does NOT default a missing CO — DataHandler.SpawnItems skips any item whose strID isn't in
        // dictCOSaves ("Trying to load a CO ... with missing save data ... Skipping"). aConds=["DEFAULT"] makes
        // CondOwner.SetData repopulate the def's starting conds on load, i.e. a pristine item freshly built.
        foreach (var chg in diff.Changes)
        {
            if (chg.Kind == PartChangeKind.Deleted || chg.Placement is not { } p) continue;
            string containerId;
            double fx, fy;
            if (chg.Kind == PartChangeKind.New)
            {
                var (w, h) = Footprint(catalog, p);
                double frot;
                (fx, fy, frot) = DocPoseToWorld(p, w, h, vx0, vy0);
                containerId = Guid.NewGuid().ToString();
                var item = new JsonObject
                {
                    ["strName"] = p.DefName, ["fX"] = fx, ["fY"] = fy, ["fRotation"] = frot, ["strID"] = containerId,
                };
                // a powered/controlled device needs its GUI-prop-maps (Electrical + panels) baked, or it loads
                // installed-but-unwired until the player uninstalls/reinstalls it (the game only restores these
                // from the save, it doesn't rebuild them on load).
                if (GpmSettings(catalog, p.DefName) is { } gpm) item["aGPMSettings"] = gpm;
                outItems.Add(item);
                outItemsById[containerId] = item;
                outCOs.Add(SynthesizeCo(p.DefName, containerId, catalog, ctx.Source.RegId, ctx.Epoch));

                // a newly-added EMPTY nav console is a bare frame — install the standard nav-module set as contained
                // children (exactly how a real ship template carries them), each a fresh item + CO. A console that
                // already carries modules (kept, or transferred through a re-skin) keeps them via its cargo below.
                if (p.Cargo.Count == 0 && NavConsole.IsConsole(catalog.Lookup(p.DefName)))
                    foreach (var modDef in NavConsole.StandardModules)
                    {
                        var modId = Guid.NewGuid().ToString();
                        var modItem = new JsonObject
                        {
                            ["strName"] = modDef, ["fX"] = fx, ["fY"] = fy, ["fRotation"] = 0.0, ["strID"] = modId, ["strParentID"] = containerId,
                        };
                        if (GpmSettings(catalog, modDef) is { } modGpm) modItem["aGPMSettings"] = modGpm;
                        outItems.Add(modItem);
                        outCOs.Add(SynthesizeCo(modDef, modId, catalog, ctx.Source.RegId, ctx.Epoch));
                    }
            }
            else   // Kept or Moved: the container item is already in outItems (verbatim / repositioned)
            {
                containerId = chg.OriginStrID!;
                var self = outItemsById.GetValueOrDefault(containerId);
                fx = self is null ? 0 : Dbl(self, "fX");
                fy = self is null ? 0 : Dbl(self, "fY");
            }
            EmitCargo(p.Cargo, containerId, fx, fy);
        }

        // Loose floor items (the Items palette): each is a free-standing top-level item at its tile. Unlike a
        // template, a save load skips any item without a CO (DataHandler.SpawnItems), so every one needs a
        // SynthesizeCo — the head, and each extra copy of a stack. A quantity > 1 is a stack: extra copies are
        // members parented to the head, and the head's CO lists them in aStack (CondOwner.PostGameLoad rebuilds
        // the ×N stack). This mirrors how EmitCargo synthesizes authored container cargo, minus a parent container.
        foreach (var lo in doc.LooseObjects)
        {
            if (catalog.Lookup(lo.DefName) is not { } lpart) continue;
            var tmp = new Placement { DefName = lo.DefName, X = lo.X, Y = lo.Y, Rot = lo.Rot };
            var (lw, lh) = Footprint(catalog, tmp);
            var (lfx, lfy, lfrot) = DocPoseToWorld(tmp, lw, lh, vx0, vy0);
            var headId = Guid.NewGuid().ToString();
            var qtyL = Math.Clamp(lo.Quantity, 1, Math.Max(1, lpart.StackLimit));

            outItems.Add(new JsonObject { ["strName"] = lo.DefName, ["fX"] = lfx, ["fY"] = lfy, ["fRotation"] = lfrot, ["strID"] = headId });
            var headCo = SynthesizeCo(lo.DefName, headId, catalog, ctx.Source.RegId, ctx.Epoch);

            if (qtyL > 1)
            {
                var memberIds = new JsonArray();
                for (var i = 1; i < qtyL; i++)
                {
                    var mid = Guid.NewGuid().ToString();
                    outItems.Add(new JsonObject
                    {
                        ["strName"] = lo.DefName, ["fX"] = lfx, ["fY"] = lfy, ["fRotation"] = lfrot, ["strID"] = mid, ["strParentID"] = headId,
                    });
                    outCOs.Add(SynthesizeCo(lo.DefName, mid, catalog, ctx.Source.RegId, ctx.Epoch));
                    memberIds.Add(mid);
                }
                headCo["aStack"] = memberIds;
            }
            outCOs.Add(headCo);
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
            if (kv.Key is "aItems" or "aCOs" or "aRooms" or "aZones" or "aRating" or "nCols" or "nRows" or "vShipPos" or "dimensions")
                continue;
            ship[kv.Key] = kv.Value?.DeepClone();
        }
        ship["aItems"] = outItems;
        ship["aCOs"] = outCOs;
        ship["aRooms"] = roomsArr;
        // Re-project zones into the (possibly re-framed) grid rather than keeping the original aZones verbatim:
        // zone tiles are flat col+row*nCols indices, so a verbatim copy under a new nCols/origin silently
        // relocates every zone (or pushes an index out of range, which drops that zone and all after it).
        ship["aZones"] = BuildZonesJson(doc, minC, minR, nColsNew, nRowsNew, ctx.Source.RegId);
        ship["aRating"] = ratingArr;
        ship["nCols"] = nColsNew;
        ship["nRows"] = nRowsNew;
        ship["vShipPos"] = new JsonObject { ["x"] = vxNew, ["y"] = vyNew };
        ship["dimensions"] = $"{nColsNew * MetresPerTile:0.00}m x {nRowsNew * MetresPerTile:0.00}m";

        // Refill the atmosphere on load: regenerating aRooms orphans the per-room gas containers, so the whole
        // ship comes up in vacuum. bPrefill makes the game run its own PreFillRooms (22 kPa O2 / 80 kPa N2 /
        // 297 K) once, then self-clears. On a Used/Damaged/Derelict ship bPrefill ALSO fires the break-in/damage
        // path — so mark the edited hull pristine (DMGStatus New) first. Ostraplan already treats the design as a
        // pristine build (Condition A), so this is consistent, and it makes the fill safe for every ship (a
        // bought "Used" ship, a derelict) rather than only an undamaged one. Per-part wear (aCondOverrides) on
        // kept parts is untouched.
        ship["DMGStatus"] = 0;
        ship["bPrefill"] = true;
        const bool atmosphereFilled = true;

        Validate(ship, ctx, dropSet);

        var warnings = ProblemScan.Scan(doc, catalog)
            .Select(pr => $"{pr.Title}: {pr.Detail}")
            .ToList();

        var report = new InjectReport(
            diff.KeptCount, diff.MovedCount, diff.NewCount, diff.DeletedCount,
            cargoLosses, warnings, grew, nColsNew, nRowsNew,
            charge?.Amount, charge?.NewBalance, atmosphereFilled, powerFixed);
        return (ship, report);
    }

    /// <summary>Build + write to a copy of the save. <paramref name="outputSaveDir"/> defaults to a sibling
    /// "<c>&lt;name&gt; (Ostraplan)</c>" folder; <paramref name="overwrite"/> must be set to replace an existing one.
    /// The original save is never touched. Returns where the copy landed + the report.</summary>
    public static (string OutputDir, InjectReport Report) Inject(
        ShipDocument doc, SaveShipContext ctx, Catalog catalog, IReadOnlyList<RoomSpecDef> specs,
        string? outputSaveDir = null, bool overwrite = false, EditCharge? charge = null)
    {
        var (ship, report) = BuildInjectedShip(doc, ctx, catalog, specs, charge);
        var outDir = outputSaveDir ?? SuggestCopyDir(ctx);
        WriteCopy(ctx, ship, outDir, overwrite, report.ResultingBalance);
        return (outDir, report);
    }

    /// <summary>The default output folder: a sibling of the source save named "<c>&lt;name&gt; (Ostraplan)</c>".</summary>
    public static string DefaultOutputDir(SaveShipContext ctx)
    {
        var srcDir = SourceDir(ctx);
        return Path.Combine(Path.GetDirectoryName(srcDir)!, $"{new DirectoryInfo(srcDir).Name} (Ostraplan)");
    }

    /// <summary>
    /// A fresh, non-existing sibling folder for the copy: the source name with any trailing "(Ostraplan)" /
    /// "(Ostraplan N)" stripped first (so copies never accumulate the tag), then "(Ostraplan)" appended, bumped
    /// to "(Ostraplan 2)", "(Ostraplan 3)"… if that already exists. Never collides, so a copy never overwrites.
    /// </summary>
    public static string SuggestCopyDir(SaveShipContext ctx)
    {
        var srcDir = SourceDir(ctx);
        var parent = Path.GetDirectoryName(srcDir)!;
        var baseName = StripOstraplanSuffix(new DirectoryInfo(srcDir).Name);
        var candidate = Path.Combine(parent, $"{baseName} (Ostraplan)");
        for (var n = 2; Directory.Exists(candidate); n++)
            candidate = Path.Combine(parent, $"{baseName} (Ostraplan {n})");
        return candidate;
    }

    /// <summary>Strip a trailing " (Ostraplan)" / " (Ostraplan N)" so re-editing a copy doesn't stack the tag.</summary>
    private static string StripOstraplanSuffix(string name)
    {
        var m = Regex.Match(name, @"^(.*?)\s*\(Ostraplan(?:\s+\d+)?\)\s*$");
        return m.Success ? m.Groups[1].Value.TrimEnd() : name;
    }

    /// <summary>A never-colliding backup folder in the Saves root (the source save's parent): "&lt;name&gt; (backup)",
    /// then "(backup 2)"… It sits <b>beside</b> the save, never inside it, so deleting the (possibly broken) edited
    /// save can't delete its backup. Public so the UI can name it. See <see cref="WriteInPlace"/>.</summary>
    public static string SuggestBackupDir(SaveShipContext ctx)
    {
        var srcDir = SourceDir(ctx);
        var parent = Path.GetDirectoryName(srcDir)!;
        var baseName = StripBackupSuffix(StripOstraplanSuffix(new DirectoryInfo(srcDir).Name));
        var candidate = Path.Combine(parent, $"{baseName} (backup)");
        for (var n = 2; Directory.Exists(candidate); n++)
            candidate = Path.Combine(parent, $"{baseName} (backup {n})");
        return candidate;
    }

    /// <summary>Strip a trailing " (backup)" / " (backup N)" so backing up a backup doesn't stack the tag.</summary>
    private static string StripBackupSuffix(string name)
    {
        var m = Regex.Match(name, @"^(.*?)\s*\(backup(?:\s+\d+)?\)\s*$");
        return m.Success ? m.Groups[1].Value.TrimEnd() : name;
    }

    /// <summary>
    /// Duplicate the source save <b>folder</b> to <paramref name="outputSaveDir"/> and, inside the copy,
    /// replace only <c>ships/&lt;RegID&gt;.json</c> with <paramref name="ship"/>; the copied zip is renamed to match
    /// the new folder and its <c>saveInfo.json</c> <c>strName</c> (and <c>money</c>, when a charge was applied) is
    /// updated, so the copy shows as its own save. The original folder is never opened for writing.
    /// </summary>
    public static void WriteCopy(SaveShipContext ctx, JsonObject ship, string outputSaveDir, bool overwrite, double? newMoney = null)
    {
        if (Directory.Exists(outputSaveDir))
        {
            if (!overwrite) throw new IOException($"'{Path.GetFileName(outputSaveDir)}' already exists.");
            Directory.Delete(outputSaveDir, recursive: true);
        }
        var targetZip = MaterializeCopy(SourceDir(ctx), Path.GetFileName(ctx.ZipPath), outputSaveDir, newMoney);
        SpliceShipInZip(targetZip, ctx.Source.RegId, ship);
    }

    /// <summary>
    /// Write the edit <b>into the original save</b>, in place. First it copies the whole save to a separate,
    /// loadable backup save <b>in the Saves folder</b> (beside the save, never inside it, so deleting a broken
    /// edited save can't take its backup with it), then splices <c>ships/&lt;RegID&gt;.json</c> into the original zip
    /// and mirrors the deducted balance into <c>saveInfo.money</c>. Returns the backup folder's path. This is the
    /// opt-in alternative to <see cref="WriteCopy"/>; the caller confirms and ensures the game isn't in that save.
    /// </summary>
    public static string WriteInPlace(SaveShipContext ctx, JsonObject ship, double? newMoney = null)
    {
        var backupDir = SuggestBackupDir(ctx);
        MaterializeCopy(SourceDir(ctx), Path.GetFileName(ctx.ZipPath), backupDir, null);   // pre-edit backup, beside the save

        SpliceShipInZip(ctx.ZipPath, ctx.Source.RegId, ship);
        var saveInfoPath = Path.Combine(SourceDir(ctx), "saveInfo.json");
        if (newMoney is not null && File.Exists(saveInfoPath)) UpdateSaveInfo(saveInfoPath, null, newMoney);
        return backupDir;
    }

    // ---- internals ----

    /// <summary>Splice <paramref name="ship"/> into <c>ships/&lt;regId&gt;.json</c> inside <paramref name="zipPath"/>,
    /// preserving the file's array-or-object shape (a sibling-ships array keeps its other ships).</summary>
    private static void SpliceShipInZip(string zipPath, string regId, JsonObject ship)
    {
        using var za = ZipFile.Open(zipPath, ZipArchiveMode.Update);
        var entryName = $"ships/{regId}.json";
        var entry = za.GetEntry(entryName)
            ?? throw new InvalidDataException($"'{entryName}' is not in the save.");
        string original;
        using (var r = new StreamReader(entry.Open())) original = r.ReadToEnd();
        var spliced = SpliceShip(JsonNode.Parse(original), ship);
        entry.Delete();
        var fresh = za.CreateEntry(entryName);
        using var w = new StreamWriter(fresh.Open());
        w.Write(spliced.ToJsonString(Indented));
    }

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

    /// <summary>Serialize the document's zones as <c>aZones</c>, projecting each zone's document tiles into the
    /// injected grid frame (origin <paramref name="originCol"/>,<paramref name="originRow"/>, size
    /// <paramref name="nCols"/>×<paramref name="nRows"/>). Only in-range indices are emitted (one out-of-range
    /// index makes the game drop that zone and every zone after it); a zone left with no in-range tiles is
    /// skipped; names are made unique. The transient <c>aOldTiles</c>/legacy <c>ranks</c> are never written.</summary>
    private static JsonArray BuildZonesJson(ShipDocument doc, int originCol, int originRow, int nCols, int nRows, string regId)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var arr = new JsonArray();
        foreach (var z in doc.Zones)
        {
            var sorted = new List<int>(z.Tiles.Count);
            foreach (var (dx, dy) in z.Tiles)
            {
                var idx = ZoneGeometry.DocToIndex(dx, dy, originCol, originRow, nCols, nRows);
                if (idx >= 0) sorted.Add(idx);
            }
            if (sorted.Count == 0) continue;
            sorted.Sort();

            var tiles = new JsonArray();
            foreach (var i in sorted) tiles.Add(i);
            var conds = new JsonArray();
            foreach (var c in z.TileConds) conds.Add(c);

            var obj = new JsonObject
            {
                ["strName"] = UniqueZoneName(z.Name, used),
                ["strRegID"] = regId,
                ["bTriggerOnOwner"] = z.TriggerOnOwner,
                ["aTiles"] = tiles,
                ["aTileConds"] = conds,
                ["zoneColor"] = new JsonObject { ["r"] = z.Color.R, ["g"] = z.Color.G, ["b"] = z.Color.B, ["a"] = z.Color.A },
            };
            if (z.CategoryConds.Count > 0)
            {
                var cc = new JsonArray();
                foreach (var c in z.CategoryConds) cc.Add(c);
                obj["categoryConds"] = cc;
            }
            if (!string.IsNullOrEmpty(z.PersonSpec)) obj["strPersonSpec"] = z.PersonSpec;
            if (!string.IsNullOrEmpty(z.TargetPSpec)) obj["strTargetPSpec"] = z.TargetPSpec;
            arr.Add(obj);
        }
        return arr;
    }

    private static string UniqueZoneName(string name, HashSet<string> used)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "zone" : name.Trim();
        var candidate = baseName;
        for (var n = 2; !used.Add(candidate); n++) candidate = $"{baseName} {n}";
        return candidate;
    }

    /// <summary>
    /// A pristine condition owner for a newly-added part, keyed to its item's <paramref name="strID"/>. A save
    /// load requires one CO per item; <c>aConds = ["DEFAULT"]</c> tells <c>CondOwner.SetData</c> to repopulate the
    /// def's own starting conds (verified against the decompile), so the part comes up freshly-built — the same
    /// pattern the game and <see cref="ShipExport"/> use. Cooverlay skins resolve through
    /// <c>DataHandler.GetCondOwnerDef</c> to their base def, so this works for any buildable def.
    /// </summary>
    private static JsonObject SynthesizeCo(string def, string strID, Catalog catalog, string regId, double epoch)
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
        var part = catalog.Lookup(def);
        if (part?.Friendly is { Length: > 0 } friendly) co["strFriendlyName"] = friendly;
        // a freshly-built device carries all the tickers its def declares (Power, etc.) — bake them, or the part
        // loads with no Power ticker and can never draw power (a save loads tickers from the CO, not the def).
        if (part is not null) BakeTickers(co, part, epoch, catalog, onlyPower: false);
        return co;
    }

    /// <summary>Add the def's declared tickers to a CO that's missing them, stamped with the current
    /// <paramref name="epoch"/>. <paramref name="onlyPower"/> restricts to the "Power" ticker — the self-heal path
    /// for a kept device that lost power; a freshly-synthesized CO bakes all of them. Returns how many were added.</summary>
    private static int BakeTickers(JsonObject co, PartDef def, double epoch, Catalog catalog, bool onlyPower)
    {
        var declared = catalog.TickersFor(def);
        if (declared.Count == 0) return 0;

        var have = new HashSet<string>(StringComparer.Ordinal);
        var arr = co["aTickers"] as JsonArray;
        if (arr is not null)
            foreach (var n in arr) if (Str(n, "strName") is { } nm) have.Add(nm);

        var added = 0;
        foreach (var (name, tmpl) in declared)
        {
            if (onlyPower && name != "Power") continue;
            if (!have.Add(name)) continue;   // already present
            if (arr is null) { arr = new JsonArray(); co["aTickers"] = arr; }
            arr.Add(BuildTicker(tmpl, epoch));
            added++;
        }
        return added;
    }

    /// <summary>A JsonTicker from its template, stamped with <paramref name="epoch"/> as its start (so fTimeLeft,
    /// which the game recomputes on load, comes up at one period — it fires next tick).</summary>
    private static JsonObject BuildTicker(JsonElement tmpl, double epoch)
    {
        var t = JsonNode.Parse(tmpl.GetRawText())!.AsObject();
        t["fEpochStart"] = epoch;
        return t;
    }

    /// <summary>
    /// Bake a new device's <c>aGPMSettings</c> — its GUI-prop-maps (the <c>Electrical</c> power map + control
    /// panels) resolved from the def, matching what install produces — so it wires to power and its panels on
    /// load. Null when the def declares no GPM (walls, floors, tools). Connection lists stay empty: the game
    /// re-derives them from the device's power-map points touching a conduit at load, so the device still only
    /// powers if it's placed on the conduit network.
    /// </summary>
    private static JsonArray? GpmSettings(Catalog catalog, string defName)
    {
        if (catalog.Lookup(defName) is not { } part) return null;
        var settings = catalog.GpmSettingsFor(part);
        if (settings.Count == 0) return null;
        var arr = new JsonArray();
        foreach (var (instance, dict) in settings)
            arr.Add(new JsonObject { ["strName"] = instance, ["dictGUIPropMap"] = JsonNode.Parse(dict.GetRawText()) });
        return arr;
    }

    private static string SourceDir(SaveShipContext ctx) => Path.GetDirectoryName(ctx.ZipPath)!;

    private static void UpdateSaveInfo(string path, string? name, double? money)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path));
            var obj = node is JsonArray a && a.Count > 0 ? a[0] as JsonObject : node as JsonObject;
            if (obj is null) return;
            if (name is not null) obj["strName"] = name;
            if (money is { } m) obj["money"] = m;
            File.WriteAllText(path, node!.ToJsonString(Indented));
        }
        catch { /* a cosmetic saveInfo update must never fail the inject */ }
    }

    /// <summary>The player's current credit balance for this ship — the summed <c>StatUSD</c> on the player CO
    /// (<see cref="SaveShipContext.PlayerCoId"/>), or null when that CO isn't on this ship (no deduction possible).</summary>
    public static double? CurrentBalance(SaveShipContext ctx)
    {
        if (ctx.PlayerCoId is not { } coId) return null;
        foreach (var co in Arr(ctx.ShipRecord, "aCOs"))
            if (Str(co, "strID") == coId) return SumStatUsd(co);
        return null;
    }

    /// <summary>Sum every <c>StatUSD=…</c> starting cond on a CO (the game accumulates them via GetCondAmount).</summary>
    private static double SumStatUsd(JsonNode co)
    {
        double sum = 0;
        if ((co as JsonObject)?["aConds"] is JsonArray conds)
            foreach (var n in conds)
                if (n is JsonValue v && v.TryGetValue<string>(out var s) && s.StartsWith("StatUSD=", StringComparison.Ordinal))
                    sum += LootDef.CondAmount(s);
        return sum;
    }

    /// <summary>Set a CO's balance to <paramref name="newBalance"/>: drop its existing <c>StatUSD</c> conds and add
    /// a single <c>StatUSD=1.0x&lt;newBalance&gt;</c> (a plain accumulator, safe to collapse).</summary>
    private static void SetStatUsd(JsonObject co, double newBalance)
    {
        if (co["aConds"] is not JsonArray conds) co["aConds"] = conds = new JsonArray();
        for (var i = conds.Count - 1; i >= 0; i--)
            if (conds[i] is JsonValue v && v.TryGetValue<string>(out var s) && s.StartsWith("StatUSD=", StringComparison.Ordinal))
                conds.RemoveAt(i);
        conds.Add($"StatUSD=1.0x{newBalance.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}");
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.EnumerateFiles(src))
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.EnumerateDirectories(src))
            CopyDir(dir, Path.Combine(dst, Path.GetFileName(dir)));
    }

    /// <summary>Copy a save FOLDER to <paramref name="destDir"/> as a self-standing, loadable save: rename the copied
    /// zip to match the new folder (the game's <c>&lt;folder&gt;/&lt;folder&gt;.zip</c> convention) and repoint saveInfo's
    /// display name (and money, when given). Assumes <paramref name="destDir"/> does not yet exist. Returns the new
    /// zip's path. Shared by <see cref="WriteCopy"/> and the <see cref="WriteInPlace"/> pre-edit backup.</summary>
    private static string MaterializeCopy(string srcDir, string srcZipName, string destDir, double? newMoney)
    {
        CopyDir(srcDir, destDir);
        var newName = new DirectoryInfo(destDir).Name;
        var copiedZip = Path.Combine(destDir, srcZipName);
        var targetZip = Path.Combine(destDir, newName + ".zip");
        if (!string.Equals(copiedZip, targetZip, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(targetZip)) File.Delete(targetZip);
            File.Move(copiedZip, targetZip);
        }
        var saveInfoPath = Path.Combine(destDir, "saveInfo.json");
        if (File.Exists(saveInfoPath)) UpdateSaveInfo(saveInfoPath, newName, newMoney);
        return targetZip;
    }

    private static string ItemName(SaveShipContext ctx, string strID) =>
        ctx.ItemsById.TryGetValue(strID, out var it) && Str(it, "strName") is { Length: > 0 } n ? n : strID;

    /// <summary>A cargo node and every descendant, depth-first — for building the keep-set from a container's tree.</summary>
    private static IEnumerable<CargoItem> Subtree(CargoItem c)
    {
        yield return c;
        foreach (var child in c.Children)
            foreach (var d in Subtree(child))
                yield return d;
    }

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
