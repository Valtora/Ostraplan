using System.Globalization;
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
/// placement-law warnings (warn-and-allow), whether the grid was reframed (it can grow OR shrink — the frame
/// tracks the part bounding box plus the game's one-tile margin), the final grid size, whether the ship
/// was armed to refill its atmosphere on load, and — when the cost was deducted — the amount charged and the
/// resulting balance.</summary>
public sealed record InjectReport(
    int Kept, int Moved, int Added, int Deleted,
    IReadOnlyList<CargoLoss> CargoDropped,
    IReadOnlyList<string> Warnings,
    bool GridReframed, int NCols, int NRows,
    double? Charged = null, double? ResultingBalance = null,
    bool AtmosphereFilled = false, int PowerFixed = 0)
{
    /// <summary>Kept condition owners repaired on the way through: parts an older Ostraplan wrote into this save
    /// without the conds the game backfills on spawn, which left them unable to take a hit and invisible to
    /// explosions (see <see cref="PartDef.BehaviourConds"/>). Zero for a save whose parts the game built, which is
    /// every save Ostraplan has not written to.</summary>
    public int BehaviourFixed { get; init; }

    /// <summary>Parts that only changed state — uninstalled, installed, a door opened or shut (see
    /// <see cref="ShipDiff.ReformedCount"/>). Written as fresh items like <see cref="Added"/>, but reported apart
    /// from it because nothing was actually built.</summary>
    public int Reformed { get; init; }
}

/// <summary>
/// Writes an edited design back into the player's ship — the inject half of save-edit. It takes the edited
/// <see cref="ShipDocument"/> and its <see cref="SaveShipContext"/>, computes the structural
/// <see cref="ShipDiff"/>, and rebuilds the ship record: kept parts verbatim, moved parts repositioned
/// (cargo shifted with them), new parts as a fresh item entry <b>plus a synthesized pristine CO</b> (a save
/// load skips any item lacking one), deleted parts (and their cargo) dropped; <c>aRooms</c>/<c>aRating</c>/grid
/// are recomputed by the P2 engine, and every
/// other field — crew, world position, docking, economy — is preserved verbatim off the retained
/// record. The ship's flavour identity is preserved too <b>unless</b> the caller passes one to write
/// (see <see cref="ApplyIdentity"/>). Then it writes to a <b>copy</b> of the save (the original is never
/// opened for writing).
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
    /// placement-law problems are collected as warnings, not thrown (warn-and-allow).
    ///
    /// <para><paramref name="identity"/> is the ship's in-game flavour identity to write over the retained
    /// record's; null (the default) keeps whatever the ship already has. See <see cref="ApplyIdentity"/>.</para></summary>
    public static (JsonObject Ship, InjectReport Report) BuildInjectedShip(
        ShipDocument doc, SaveShipContext ctx, Catalog catalog, IReadOnlyList<RoomSpecDef> specs, EditCharge? charge = null,
        WearOptions? wear = null, ExportMetadata? identity = null)
    {
        // Every surviving structural part's strID (kept / moved / new), collected as the item/CO tree is rebuilt.
        // The optional wear pass (below) re-rolls StatDamage on exactly this set — the installed structure the
        // Ship Rating's Condition slot averages over — leaving cargo, crew and system spawners alone.
        var structuralIds = new HashSet<string>(StringComparer.Ordinal);
        // Condition the designer painted on a part, keyed by the CO strID that part ends up as. An authored value
        // beats the roll and applies whether or not the wear pass is armed, so a battered station written back
        // over a ship arrives battered even on a "keep the existing condition" pass.
        var paintedById = new Dictionary<string, double>(StringComparer.Ordinal);
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

        // The ship's ROOM objects (see RoomCoIds): rooms are regenerated below with fresh strIDs, so every
        // original room CO must go or it lingers as a ghost room. Held apart from dropSet — these are neither
        // structural parts nor cargo, so they must not reach the cargo-loss report or the structural CO check.
        var roomCoIds = RoomCoIds(ctx);

        // drop set: deleted structural parts, plus every ORIGINAL contained item that no longer survives — removed
        // from a kept container, or lost with a deleted one (both fall out of keepSet). The cargo-loss REPORT is
        // scoped to deleted containers: an item deliberately removed from a kept container in the editor is an
        // intended edit, not a loss to warn about (so a content edit fires no false "cargo will be deleted").
        var dropSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in deleted) dropSet.Add(id);
        // Stand-ins (see Substitution): a placement tagged with a source item that is NOT one of the modelled
        // originals is the user's replacement for a part whose mod isn't loaded. The original (and anything
        // parented to it) must go, or the ship would carry both it and its stand-in. The diff already classifies
        // the stand-in itself as New — its origin resolves to nothing — so it lands as a fresh item + CO.
        var standInDrops = Substitution.StandInDrops(doc, ctx);
        foreach (var (root, kids) in standInDrops)
        {
            dropSet.Add(root);
            foreach (var kid in kids) dropSet.Add(kid);
        }
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
        // a substituted part is replaced outright, so anything it held goes with it — report that like any other
        // container loss rather than dropping it silently
        foreach (var (root, kids) in standInDrops)
            if (kids.Count > 0)
                cargoLosses.Add(new CargoLoss(root, ItemName(ctx, root), kids.Select(cid => ItemName(ctx, cid)).ToList()));

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
            if (id is not null && (dropSet.Contains(id) || roomCoIds.Contains(id))) continue;
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
        var behaviourFixed = 0;
        foreach (var co in SourceCos(ctx))
        {
            var id = Str(co, "strID");
            if (id is not null && (dropSet.Contains(id) || roomCoIds.Contains(id))) continue;
            var clone = co.DeepClone().AsObject();
            if (Str(clone, "strCODef") is { } cdef && catalog.Lookup(cdef) is { } cpart)
            {
                if (BakeTickers(clone, cpart, ctx.Epoch, catalog, onlyPower: true) > 0) powerFixed++;
                if (HealBehaviourConds(clone, cpart, ctx.Epoch)) behaviourFixed++;
            }
            outCOs.Add(clone);
            if (id is not null) outCosById[id] = clone;   // for re-positioning moved/rotated original cargo below
        }

        // opt-in cost deduction: rewrite the player CO's StatUSD (the authoritative balance) to the new total.
        // The CO is one of the kept COs above whenever the player was standing on the ship being edited; when they
        // were docked at a station or on another of their ships it lives in that record instead, and the writer
        // patches it there (see PatchPlayerBalance). saveInfo.money is mirrored at write time either way. The UI
        // has already checked affordability, so a player CO that is nowhere at all is a hard error.
        if (charge is { } ch)
        {
            var applied = false;
            foreach (var co in outCOs)
                if (co is JsonObject o && Str(o, "strID") == ch.PlayerCoId) { SetStatUsd(o, ch.NewBalance); applied = true; break; }
            if (!applied && (ctx.PlayerCoRegId is not { Length: > 0 } reg || reg == ctx.Source.RegId))
                throw new InvalidDataException(
                    "The player's money couldn't be found in this save, so the edit cost can't be deducted. " +
                    "Untick \"Deduct the edit cost\" and try again.");
        }

        // Emit contained cargo for every surviving container from its current Placement.Cargo — the tree is the
        // authoritative structure. An AUTHORED item becomes a fresh item + synthesized-CO entry (a save load skips
        // any item lacking a CO). An ORIGINAL item was already written verbatim above; here its parent, grid
        // position and rotation are reconciled to the tree so a re-parent (re-skin / move between containers), a
        // rearrange, or a rotate persists — writes are guarded to what actually changed, so an untouched item (or a
        // non-grid contained item like a nav module) is left exactly as it was. Recurses for nesting and for a
        // stack's members alike: a stack the user topped up holds ORIGINAL members plus authored ones, and skipping
        // the descent left the authored half out of the save entirely. Returns the strIDs emitted at this level, so
        // a stack head can list its members (see SetStackMembers).
        List<string> EmitCargo(IReadOnlyList<CargoItem> nodes, string parentId, double px, double py)
        {
            var emitted = new List<string>(nodes.Count);
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
                    // A contained DEVICE needs its GUI-prop-maps baked too, the same as a new placed part: a nav
                    // module's page comes from its GPM, and a save load restores these from the file rather than
                    // rebuilding them from the def. Null for inert cargo (a tool, a shirt, a can).
                    if (GpmSettings(catalog, c.DefName) is { } cgpm) citem["aGPMSettings"] = cgpm;
                    // A name the user gave it, after the def's own panels so it lands alongside them rather than
                    // replacing them (#37). ApplyRename creates the array when the def brought none.
                    ApplyRename(citem, c.CustomName);
                    outItems.Add(citem);
                    outItemsById[cid] = citem;
                    var cco = SynthesizeCo(c.DefName, cid, catalog, ctx.Source.RegId, ctx.Epoch);
                    if (c.GridX != 0 || c.GridY != 0) { cco["inventoryX"] = c.GridX; cco["inventoryY"] = c.GridY; }
                    // A slotted item is re-slotted on load by the NAME its own CO carries — Ship.SpawnItems calls
                    // compSlots.SlotItem(co.jCOS.strSlotName, co), and SlotItem returns false outright on a null
                    // name. strSlotParentID alone is not enough: without this the item never attaches to its host.
                    if (c.Slotted && c.SlotName is { Length: > 0 } cslot) cco["strSlotName"] = cslot;
                    outCOs.Add(cco);
                    SetStackMembers(cco, c, EmitCargo(c.Children, cid, px, py));   // stack members / nested authored contents
                    emitted.Add(cid);
                }
                else if (outItemsById.TryGetValue(c.StrID, out var orig))
                {
                    var parentField = c.Slotted ? "strSlotParentID" : "strParentID";
                    if (Str(orig, parentField) != parentId) orig[parentField] = parentId;   // re-skin / cross-container move
                    var origItem = ctx.ItemsById.GetValueOrDefault(c.StrID);
                    if (origItem is null || GridMath.Norm((int)Math.Round(Dbl(origItem, "fRotation"))) != c.GridRot)
                        orig["fRotation"] = (double)c.GridRot;                                // inventory rotation
                    // The name, only when it actually moved. A kept item is written back verbatim, so touching its
                    // panels unconditionally would rewrite a name the player typed in game as a no-op edit.
                    if (Rename.FromItem(origItem) != c.CustomName) ApplyRename(orig, c.CustomName);
                    outCosById.TryGetValue(c.StrID, out var co);
                    if (co is not null)
                    {
                        var origCo = ctx.CosById.GetValueOrDefault(c.StrID);
                        if (origCo is null || Int(origCo, "inventoryX") != c.GridX) co["inventoryX"] = c.GridX;
                        if (origCo is null || Int(origCo, "inventoryY") != c.GridY) co["inventoryY"] = c.GridY;
                    }
                    var childIds = EmitCargo(c.Children, c.StrID, Dbl(orig, "fX"), Dbl(orig, "fY"));
                    // The save's own aStack is only right while the stack is untouched. Rewriting it from the
                    // members that actually survived is what makes a top-up and a part-removal both land, and it
                    // clears ids the drop set has already taken out of aItems.
                    if (co is not null) SetStackMembers(co, c, childIds);
                    emitted.Add(c.StrID);
                }
            }
            return emitted;
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
                var newCo = SynthesizeCo(p.DefName, containerId, catalog, ctx.Source.RegId, ctx.Epoch);
                outCOs.Add(newCo);
                outCosById[containerId] = newCo;

                // a newly-added nav console with no MODULES is a bare frame — install the standard nav-module set as
                // contained children, each a fresh item + CO. The test is NavConsole.NeedsModules, not "has no
                // cargo": a slotted data chip is not a screen. A console that already carries modules (kept, or
                // transferred through a re-skin) keeps them via its cargo below.
                if (NavConsole.NeedsModules(p.Cargo) && NavConsole.IsConsole(catalog.Lookup(p.DefName)))
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
            // The part's name, in whichever direction it changed. This runs for kept and moved parts too, because
            // the save's own item carries whatever it was called in game and the user may have renamed it here, or
            // cleared a name back to the stock one — both have to reach the item that is actually written.
            if (outItemsById.GetValueOrDefault(containerId) is { } namedItem)
                ApplyRename(namedItem, p.CustomName);

            // An authored fill, for a new tank and a kept one alike, written in BOTH places the game keeps a
            // container's contents. The item's aCondOverrides is the shallow-state channel, read off a ship nobody
            // has loaded; the CO's aConds is what the ship actually comes up holding. The two are not
            // interchangeable and the game writes both — see SetFillConds for why an override alone is dropped on
            // a full load, which is what used to happen to every fill a write-back authored.
            if (p.Fill is { } fill)
            {
                if (outItemsById.GetValueOrDefault(containerId) is { } filledItem)
                    SetFillOverrides(filledItem, fill, catalog.Lookup(p.DefName), catalog);
                SetFillConds(outCosById.GetValueOrDefault(containerId), fill, catalog.Lookup(p.DefName), catalog);
            }

            // A nav console's screen arrangement: where each module it holds sits, so the console reads the way the
            // design intends instead of however the game walks the container (see NavConsole.Arrange). On a KEPT
            // console only the keys the save leaves empty are filled — a player who arranged their own console in
            // game keeps that arrangement, and only the modules Ostraplan just put in get placed.
            if (NavConsole.IsConsole(catalog.Lookup(p.DefName)) && catalog.Lookup(p.DefName) is { } consoleDef
                && outItemsById.GetValueOrDefault(containerId) is { } consoleItem)
            {
                var modules = NavConsole.NeedsModules(p.Cargo) && chg.Kind == PartChangeKind.New
                    ? NavConsole.StandardModules
                    : p.Cargo.Where(c => !c.Slotted).Select(c => c.DefName).ToList();
                // An arrangement the user made is theirs, not a default, so it overwrites the save's own config
                // even on a kept console — that is what asking for it means.
                ApplyNavConfig(consoleItem, NavConsole.ConfigEntries(catalog, consoleDef, modules, p.NavLayout),
                    onlyFillEmpty: chg.Kind != PartChangeKind.New && p.NavLayout is null);
            }

            structuralIds.Add(containerId);
            if (p.Condition is { } painted) paintedById[containerId] = painted;
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

            var headItem = new JsonObject { ["strName"] = lo.DefName, ["fX"] = lfx, ["fY"] = lfy, ["fRotation"] = lfrot, ["strID"] = headId };
            // A name the user gave this deck item, on the head alone — the stack's extra copies are members of it
            // (see LooseObject.CustomName). Every loose item is written fresh on each write-back, so there is never
            // an existing panel to replace here; ApplyRename is used all the same, for one rule about the shape.
            ApplyRename(headItem, lo.CustomName);
            outItems.Add(headItem);
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
            // What the item holds — a backpack's pouches and whatever the user put in them. The CO above is what
            // stops the game spawning even the pockets for itself, so this is the only chance to write them.
            // Only for a single: nothing that holds anything stacks (CargoEdit.PlaceInto pins it to 1), and a
            // stack keeps its members where the contents would have to go. A design older than deck-item
            // inventories has no tree, so fall back to the def's own intrinsic contents.
            if (qtyL == 1)
            {
                if (lo.Cargo.Count > 0) EmitCargo(lo.Cargo, headId, lfx, lfy);
                else EmitIntrinsicContents(outItems, outCOs, headId, lpart, catalog, ctx.Source.RegId, ctx.Epoch, lfx, lfy);
            }
        }

        // Grid frame: the part bounding box plus a ONE-TILE MARGIN, which is exactly what the game rebuilds on
        // load and therefore the only frame the tile indices written below may be expressed in. Ship.UpdateTiles
        // pads the tilemap around every spawned item by (-1,+1) tiles (TileUtils.PadTilemap) and seeds vShipPos
        // off the first item, so the loaded grid is always bbox±1 regardless of the nCols/vShipPos we write —
        // those only feed the shallow (unloaded) view, Ship.cs' `LoadState > Loaded.Shallow ? nCols : json.nCols`.
        // Get this wrong and every aRooms/aZones index decodes against a grid of a different width: rooms bind to
        // the wrong tiles (ghost rooms) and zones skew, the error compounding by one column per row.
        // Hugging the content with no margin was the ghost-room bug — an under-floor tank ring reaching the old
        // edge grew the frame to the footprint exactly. Verified against real saves: bbox±1 reproduces the game's
        // own frame on all four edges (see SaveEditFrameTests).
        int minC = 0, minR = 0, maxC = nCols0 - 1, maxR = nRows0 - 1;
        if (doc.Bounds() is { } b)
        {
            minC = b.MinX - 1; minR = b.MinY - 1;
            maxC = b.MaxX + 1; maxR = b.MaxY + 1;
        }
        var nColsNew = maxC - minC + 1;
        var nRowsNew = maxR - minR + 1;
        // The frame moved if the origin shifted OR the extent changed — it can now SHRINK (deleting the outermost
        // parts tightens the game's grid too), so this is a "reframed" test, not the old "grew" one. Every
        // grid-relative index we do not rewrite (crew nDestTile) is stale whenever this is true.
        var reframed = minC != 0 || minR != 0 || nColsNew != nCols0 || nRowsNew != nRows0;
        var vxNew = vx0 + minC;
        var vyNew = vy0 - minR;

        // recompute rooms + rating in that grid (the game recomputes on load; these need only be self-consistent)
        var grid = ShipGrid.FromDocumentFramed(doc, catalog, minC, minR, nColsNew, nRowsNew);
        var partition = RoomBuilder.Build(grid);
        RoomCertifier.CertifyAll(partition, specs, catalog);
        var rating = Rating.Calculate(grid, partition, catalog);

        // Optional wear: re-roll StatDamage on every installed structural part's CO (kept, moved, and new alike) to
        // the chosen average condition — the game stores per-part wear exactly this way (a CO's aConds carry
        // StatDamage). DMGStatus stays 0 (New) below, so the game keeps this baked wear rather than running its own
        // pass. The resulting mean condition becomes the baked aRating Condition slot.
        var wornGrade = ApplyWear(wear, outCOs, structuralIds, catalog, outItemsById, paintedById);

        // roomValue is the room's PARTS value (Room.CalculateRoomValue), which Ship.GetShipValue sums on a
        // shallow load — bake that, not the physical volume, so a broker quote taken before the ship is
        // fully re-loaded reads the real worth (same fix as ShipExport).
        var valueModifiers = specs.ToDictionary(s => s.Name, s => s.ValueModifier, StringComparer.Ordinal);
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
                ["roomValue"] = ShipValue.RoomValueOf(r, valueModifiers, catalog),
            });
        }
        var ratingArr = new JsonArray(
            rating.Epoch.Length == 0 ? "0" : rating.Epoch, wornGrade ?? rating.Condition, rating.RoomCount,
            rating.Maneuver, rating.Size, rating.Slot5);

        // if the grid was reframed, crew nDestTile (a flat index into nCols*nRows) is stale — recompute from world pos
        if (reframed) RecomputeCrewDestTiles(ctx, outCOs, vxNew, vyNew, nColsNew, nRowsNew);

        // assemble: clone every original field verbatim, then overwrite only the structural ones
        var ship = new JsonObject();
        foreach (var kv in ctx.ShipRecord.AsObject())
        {
            if (kv.Key is "aItems" or "aCOs" or "aRooms" or "aZones" or "aRating" or "nCols" or "nRows" or "vShipPos" or "dimensions")
                continue;
            ship[kv.Key] = kv.Value?.DeepClone();
        }
        if (identity is not null) ApplyIdentity(ship, identity);
        ship["aItems"] = outItems;
        ship["aCOs"] = outCOs;
        ship["aRooms"] = roomsArr;
        // Re-project zones into the (possibly re-framed) grid rather than keeping the original aZones verbatim:
        // zone tiles are flat col+row*nCols indices, so a verbatim copy under a new nCols/origin silently
        // relocates every zone (or pushes an index out of range, which drops that zone and all after it).
        ship["aZones"] = BuildZonesJson(doc, minC, minR, nColsNew, nRowsNew, ctx.Source.RegId);
        ship["aRating"] = ratingArr;
        // the shallow value multiplier flag: recompute from the edited layout (the verbatim-cloned figure
        // reflects the pre-edit pumps; the game itself refreshes it only on an Edit/Full load)
        ship["nO2PumpCount"] = ShipValue.CountO2Pumps(grid, catalog);
        ship["nCols"] = nColsNew;
        ship["nRows"] = nRowsNew;
        ship["vShipPos"] = new JsonObject { ["x"] = vxNew, ["y"] = vyNew };
        // InvariantCulture: the game parses/prints '.' decimals, so a comma-decimal locale must not leak a
        // "15,36m x 11,20m" into the save.
        ship["dimensions"] = string.Create(CultureInfo.InvariantCulture,
            $"{nColsNew * MetresPerTile:0.00}m x {nRowsNew * MetresPerTile:0.00}m");

        // Refill the atmosphere on load: regenerating aRooms orphans the per-room gas containers, so the whole
        // ship comes up in vacuum. bPrefill makes the game run its own PreFillRooms (22 kPa O2 / 80 kPa N2 /
        // 297 K) once, then self-clears. On a Used/Damaged/Derelict ship bPrefill ALSO fires the break-in/damage
        // path — so mark the edited hull pristine (DMGStatus New) first. Ostraplan already treats the design as a
        // pristine build (Condition A), so this is consistent, and it makes the fill safe for every ship (a
        // bought "Used" ship, a derelict) rather than only an undamaged one. Any per-part wear applied above
        // (StatDamage on each structural CO) rides along regardless — DMGStatus New only suppresses the game's
        // own break-in pass, it doesn't clear the damage we baked.
        ship["DMGStatus"] = 0;
        ship["bPrefill"] = true;
        const bool atmosphereFilled = true;

        Validate(ship, ctx, dropSet);

        var warnings = ProblemScan.Scan(doc, catalog)
            .Select(pr => $"{pr.Title}: {pr.Detail}")
            .ToList();

        var report = new InjectReport(
            diff.KeptCount, diff.MovedCount, diff.NewCount, diff.DeletedCount,
            cargoLosses, warnings, reframed, nColsNew, nRowsNew,
            charge?.Amount, charge?.NewBalance, atmosphereFilled, powerFixed)
        {
            Reformed = diff.ReformedCount,
            BehaviourFixed = behaviourFixed,
        };
        return (ship, report);
    }

    /// <summary>Build + write to a copy of the save. <paramref name="outputSaveDir"/> defaults to a sibling
    /// "<c>&lt;name&gt; (Ostraplan)</c>" folder; <paramref name="overwrite"/> must be set to replace an existing one.
    /// The original save is never touched. Returns where the copy landed + the report.</summary>
    public static (string OutputDir, InjectReport Report) Inject(
        ShipDocument doc, SaveShipContext ctx, Catalog catalog, IReadOnlyList<RoomSpecDef> specs,
        string? outputSaveDir = null, bool overwrite = false, EditCharge? charge = null, WearOptions? wear = null)
    {
        var (ship, report) = BuildInjectedShip(doc, ctx, catalog, specs, charge, wear);
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
    public static string SuggestBackupDir(SaveShipContext ctx) => SuggestBackupDir(SourceDir(ctx));

    /// <inheritdoc cref="SuggestBackupDir(SaveShipContext)"/>
    public static string SuggestBackupDir(string srcDir)
    {
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
        PruneRelocatedCos(ctx, targetZip);              // no-op unless the ship's COs were in the session record
        PatchPlayerBalance(ctx, targetZip, newMoney);   // no-op unless the player's money lives in another record
    }

    /// <summary>
    /// Write the edit <b>into the original save</b>, in place. When <paramref name="backup"/> is true (the default)
    /// it first copies the whole save to a separate, loadable backup save <b>in the Saves folder</b> (beside the
    /// save, never inside it, so deleting a broken edited save can't take its backup with it); it then splices
    /// <c>ships/&lt;RegID&gt;.json</c> into the original zip and mirrors the deducted balance into
    /// <c>saveInfo.money</c>. Returns the backup folder's path, or <b>null</b> when the caller opted out of the
    /// backup. This is the opt-in alternative to <see cref="WriteCopy"/>; the caller confirms and ensures the game
    /// isn't in that save.
    /// </summary>
    public static string? WriteInPlace(SaveShipContext ctx, JsonObject ship, double? newMoney = null, bool backup = true)
    {
        string? backupDir = null;
        if (backup)
        {
            backupDir = SuggestBackupDir(ctx);
            MaterializeCopy(SourceDir(ctx), Path.GetFileName(ctx.ZipPath), backupDir, null);   // pre-edit backup, beside the save
        }

        SpliceShipInZip(ctx.ZipPath, ctx.Source.RegId, ship);
        PruneRelocatedCos(ctx, ctx.ZipPath);             // no-op unless the ship's COs were in the session record
        PatchPlayerBalance(ctx, ctx.ZipPath, newMoney);   // no-op unless the player's money lives in another record
        var saveInfoPath = Path.Combine(SourceDir(ctx), "saveInfo.json");
        if (newMoney is not null && File.Exists(saveInfoPath)) UpdateSaveInfo(saveInfoPath, null, newMoney);
        return backupDir;
    }

    // ---- identity ----

    /// <summary>
    /// The ship's in-game flavour identity as it stands in the save, for seeding the editor when a design is
    /// imported. A save's ship carries its make/model/designation copied off the template it spawned from, and
    /// its <c>strName</c> is the registration rather than a display name (see <see cref="SaveGrant"/>), so the
    /// name here is <c>publicName</c> — blanked when it is still the <c>"$TEMPLATE"</c> sentinel, which is not a
    /// name but the game's instruction to roll one.
    /// </summary>
    public static ExportMetadata ReadIdentity(SaveShipContext ctx)
    {
        var r = ctx.ShipRecord;
        var publicName = Str(r, "publicName") ?? "";
        return new ExportMetadata(
            publicName == "$TEMPLATE" ? "" : publicName,
            Str(r, "make") ?? "", Str(r, "model") ?? "", Str(r, "year") ?? "",
            Str(r, "designation") ?? "", Str(r, "description") ?? "");
    }

    /// <summary>
    /// Write <paramref name="identity"/> over the rebuilt record's own. The five flavour fields are written as
    /// given, blanks included, because a user who cleared one meant to clear it: no game logic reads them beyond
    /// display (see <see cref="ExportOptions.Make"/>).
    ///
    /// <para><c>publicName</c> is the exception twice over. A blank one is left alone rather than written,
    /// because on this path blank means "keep the name it already has" and because the game re-rolls a random
    /// name on every load for a ship whose stored value is blank or <c>"$TEMPLATE"</c> (verified against
    /// decompiled <c>Ship.InitShip</c>) — so writing a blank would not clear the name, it would make it unstable.
    /// <c>strName</c>/<c>strRegID</c>/<c>strXPDR</c> are never touched: on a save's ship those are the
    /// registration, which the rest of the save references the ship by.</para>
    /// </summary>
    private static void ApplyIdentity(JsonObject ship, ExportMetadata identity)
    {
        ship["make"] = identity.Make;
        ship["model"] = identity.Model;
        ship["year"] = identity.Year;
        ship["designation"] = identity.Designation;
        ship["description"] = identity.Description;

        var publicName = identity.PublicName.Trim();
        if (publicName.Length > 0 && publicName != "$TEMPLATE") ship["publicName"] = publicName;
    }

    // ---- internals ----

    /// <summary>Splice <paramref name="ship"/> into <c>ships/&lt;regId&gt;.json</c> inside <paramref name="zipPath"/>,
    /// preserving the file's array-or-object shape (a sibling-ships array keeps its other ships).</summary>
    internal static void SpliceShipInZip(string zipPath, string regId, JsonObject ship)
    {
        using var za = ZipFile.Open(zipPath, ZipArchiveMode.Update);
        var entryName = SaveZip.ShipEntry(regId);
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

        ValidateSubStation(ship, ctx);
    }

    /// <summary>
    /// What makes an apartment an apartment has to survive the rebuild untouched.
    ///
    /// <para>It does, and by construction: the assemble step clones every original field verbatim and overwrites
    /// only the structural ones, so <c>strRegID</c>, <c>bShipHidden</c> and the whole of <c>objSS</c> (the
    /// body-orbit lock that pins the residence to its station) come through as they were. <c>objSS.vPosx/vPosy</c>
    /// are absolute system coordinates in AU, not offsets into the tile frame, so even a grid reframe cannot
    /// disturb them.</para>
    ///
    /// <para>This asserts it anyway, because the guarantee rests entirely on that skip-list a few lines up. Adding
    /// <c>objSS</c> to it — for a plausible-sounding reason, by someone who has never opened GAME-INTERNALS §19 —
    /// would unpin every edited apartment from its station and lose the player's home, with nothing failing until
    /// the save was loaded. A residence is recognised the way the game recognises one: a pipe in the RegID
    /// (<c>Ship.InitShip</c>).</para>
    /// </summary>
    private static void ValidateSubStation(JsonObject ship, SaveShipContext ctx)
    {
        if (!SaveZip.IsSubStation(ctx.Source.RegId)) return;

        if (Str(ship, "strRegID") != ctx.Source.RegId)
            throw new InvalidDataException(
                $"The residence's registration changed from '{ctx.Source.RegId}' to '{Str(ship, "strRegID")}' — "
                + "the save references it by that ID, so the edit was aborted.");

        if (ship["objSS"] is not JsonObject situ)
            throw new InvalidDataException(
                "The residence lost its situation block (objSS), which is what locks it to its station — "
                + "inject aborted.");

        if (situ["bIsBO"]?.GetValue<bool>() != true || situ["bBOLocked"]?.GetValue<bool>() != true)
            throw new InvalidDataException(
                "The residence is no longer locked to its station's body orbit (objSS.bIsBO/bBOLocked) — "
                + "it would come adrift in the save. Inject aborted.");
    }

    /// <summary>
    /// Every ROOM condition-owner in the source ship. A room is not a buildable part: the game backs each one
    /// with a <c>Compartment</c> CO (<c>Room</c>'s <c>coRoom</c>) and saves the room under <b>that CO's</b>
    /// strID (<c>Room.GetJSONSave</c>: <c>jsonRoom.strID = coRoom.strID</c>). Compartments never enter the
    /// document (they aren't parts), so an inject that rebuilds <c>aRooms</c> with fresh strIDs would leave
    /// every original Compartment behind, referenced by nothing: on load <c>Ship.CreateRooms</c> resolves each
    /// saved room via <c>GetCOByID(strID)</c>, misses, logs "Generating new room with old ID" and mints a
    /// replacement, while the originals are never consumed by its <c>RemoveCO</c> and linger as unbound IsRoom
    /// COs — the ghost rooms. Dropping them lets the game rebuild each room cleanly from the saved strID.
    ///
    /// <para>Identified by the source's own <c>aRooms</c> (authoritative — these ARE the room strIDs), widened
    /// to any CO whose def or conds mark it a room, so a Compartment orphaned by an earlier ghost-room episode
    /// is swept up too rather than being preserved forever.</para>
    /// </summary>
    /// <summary>
    /// Every condowner the rebuild may keep: this ship record's own, then the ones adopted out of the session
    /// record for items the record does not cover (see <see cref="SaveShipContext.RelocatedCoIds"/>). Writing the
    /// adopted ones into the ship record is what makes the edited ship self-contained; the writer then takes them
    /// out of the session record so no <c>strID</c> is defined twice.
    /// </summary>
    private static IEnumerable<JsonNode> SourceCos(SaveShipContext ctx) =>
        Arr(ctx.ShipRecord, "aCOs")
            .Concat(ctx.RelocatedCoIds.Select(id => ctx.CosById.GetValueOrDefault(id)).OfType<JsonNode>());

    private static HashSet<string> RoomCoIds(SaveShipContext ctx)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in Arr(ctx.ShipRecord, "aRooms"))
            if (Str(r, "strID") is { } id) ids.Add(id);
        foreach (var co in Arr(ctx.ShipRecord, "aCOs"))
            if (Str(co, "strID") is { } id
                && (Str(co, "strCODef") == RoomCoDef || HasCond(co, "IsRoom")))
                ids.Add(id);
        return ids;
    }

    /// <summary>The game's room condowner def (<c>Room</c>: <c>DataHandler.GetCondOwner("Compartment")</c>).</summary>
    private const string RoomCoDef = "Compartment";

    /// <summary>True when a saved CO's <c>aConds</c> carries <paramref name="cond"/> (entries are
    /// "<c>Name=valuexcount</c>", so match on the name before '=').</summary>
    private static bool HasCond(JsonNode? co, string cond)
    {
        foreach (var c in Arr(co, "aConds"))
            if (c is JsonValue v && v.TryGetValue<string>(out var s) && LootDef.CondName(s) == cond) return true;
        return false;
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
    /// Re-roll <c>StatDamage</c> on every structural CO in <paramref name="structuralIds"/> to the wear target,
    /// mirroring the game's own per-part storage (a CO's <c>aConds</c> carries <c>StatDamage</c>). Skips
    /// <c>IsSystem</c> and undamageable (no <c>StatDamageMax</c>) parts, which stay pristine but still count in the
    /// mean. Returns the resulting Ship-Rating Condition grade, or null when wear is off (leaving each part's
    /// existing damage untouched). Mutates <paramref name="outCOs"/> in place.
    ///
    /// <para>"Repair All" (<see cref="WearOptions.IsRepair"/>) is the same pass run the other way: every structural
    /// CO has its <c>StatDamage</c> cleared outright, including the system and undamageable parts the roll skips.
    /// Skipping those would be right for a roll — the game never damages them either — but wrong for a repair,
    /// where the point is that nothing is left carrying damage whatever put it there. This is the only path that
    /// removes damage at all: on an update the kept COs arrive with the ship's real wear on them, and an unarmed
    /// pass deliberately leaves it.</para>
    ///
    /// <para><b>A painted condition is authored and outranks all of it</b> (see <see cref="Placement.Condition"/>).
    /// It is applied whether or not the pass is armed, so the whole method now runs for a painted design even on
    /// an unarmed one; it beats the roll on a worn pass; and it beats the clear on a repair, because repairing
    /// everything is a statement about the parts the designer did <i>not</i> speak for. Unpainted parts behave
    /// exactly as they always did.</para>
    /// </summary>
    private static string? ApplyWear(
        WearOptions? wear, JsonArray outCOs, HashSet<string> structuralIds, Catalog catalog,
        IReadOnlyDictionary<string, JsonObject> itemsById,
        IReadOnlyDictionary<string, double>? painted = null)
    {
        var anyPainted = painted is { Count: > 0 };
        if (wear is not { Enabled: true } w)
        {
            // Unarmed: the ship's own wear is left alone, but a painted part still has to be written or the
            // design would claim a condition it never delivered.
            if (!anyPainted) return null;
            foreach (var node in outCOs)
                if (node is JsonObject co && Str(co, "strID") is { } id && painted!.TryGetValue(id, out var c)
                    && DamagePoolOf(co, catalog) is { } max)
                    SetStatDamage(co, (1.0 - c) * max, DefOf(co, catalog), itemsById.GetValueOrDefault(id));
            // No grade: an unarmed pass does not know the condition of the parts it deliberately left alone, so
            // it must not claim to. The game recomputes the rating on a full load either way.
            return null;
        }

        if (w.IsRepair)
        {
            foreach (var node in outCOs)
            {
                if (node is not JsonObject repaired || Str(repaired, "strID") is not { } rid
                    || !structuralIds.Contains(rid)) continue;
                if (anyPainted && painted!.TryGetValue(rid, out var c) && DamagePoolOf(repaired, catalog) is { } max)
                    SetStatDamage(repaired, (1.0 - c) * max, DefOf(repaired, catalog), itemsById.GetValueOrDefault(rid));
                else
                    SetStatDamage(repaired, 0, DefOf(repaired, catalog), itemsById.GetValueOrDefault(rid));
            }
            return anyPainted ? null : Rating.ConditionGrade(1.0);
        }

        var rng = WearModel.NewRng(w);
        var ceiling = WearModel.CeilingFor(w.TargetCondition);
        var rates = new List<double>();
        foreach (var node in outCOs)
        {
            if (node is not JsonObject co || Str(co, "strID") is not { } id || !structuralIds.Contains(id)) continue;
            if (Str(co, "strCODef") is not { } def || catalog.Lookup(def) is not { } part
                || part.StartingConds.Contains("IsSystem"))
            {
                rates.Add(0.0);   // undamageable / system installed part: pristine in the grade, left untouched
                continue;
            }
            var damageMax = part.StartingCondValues.GetValueOrDefault("StatDamageMax");
            if (damageMax <= 0) { rates.Add(0.0); continue; }
            var dmg = anyPainted && painted!.TryGetValue(id, out var c)
                ? (1.0 - c) * damageMax
                : WearModel.DamageAmount(rng, ceiling, damageMax);
            SetStatDamage(co, dmg, part, itemsById.GetValueOrDefault(id));
            rates.Add(dmg / damageMax);
        }
        return WearModel.GradeFor(rates);
    }

    /// <summary>A CO's damage pool from its def, or null when it has none and so cannot hold wear at all — the
    /// same test <see cref="Paint.CanWear"/> applies in the editor, run here against the CO's own
    /// <c>strCODef</c>.</summary>
    private static double? DamagePoolOf(JsonObject co, Catalog catalog)
    {
        if (Str(co, "strCODef") is not { } def || catalog.Lookup(def) is not { } part) return null;
        if (part.StartingConds.Contains("IsSystem")) return null;
        var max = part.StartingCondValues.GetValueOrDefault("StatDamageMax");
        return max > 0 ? max : null;
    }

    /// <summary>The palette entry a CO's <c>strCODef</c> names, or null when it names nothing this install has.</summary>
    private static PartDef? DefOf(JsonObject co, Catalog catalog) =>
        Str(co, "strCODef") is { } def ? catalog.Lookup(def) : null;

    /// <summary>
    /// Repair a <b>kept</b> condition owner that is missing the conds the game gives every part on the day it is
    /// built (<see cref="PartDef.BehaviourConds"/>), plus the two fields beside them a synthesized CO used to
    /// leave out. Returns whether anything was added.
    ///
    /// <para>This is the same shape as the Power-ticker self-heal above and exists for the same reason: a part an
    /// older Ostraplan wrote into this save is missing them permanently, because a kept CO is copied verbatim and
    /// nothing in the game ever adds them a second time. On a save whose parts the game built, every one of these
    /// is already present and this does nothing at all.</para>
    ///
    /// <para><b>Only ever adds, never overwrites.</b> Each cond is written only when the CO has no say on it, so a
    /// value the game or the player arrived at is left alone. Three cases are deliberately left to the CO rather
    /// than the def, because the def cannot know about them: a part that picked up <c>IsUndamageable</c> or
    /// <c>IsIndestructable</c> at runtime keeps whichever flag that implies, and an <c>fLastICOUpdate</c> that is
    /// already set is a real catch-up point the game is entitled to act on. Nothing in the game's code removes any
    /// of these conds, so absence means the backfill never ran rather than that something took it away.</para>
    ///
    /// <para><b><c>aCondRules</c> is deliberately NOT healed.</b> A synthesized CO needs the field, but an absent
    /// one is no evidence that a CO was synthesized: the save writer omits an empty array rather than writing
    /// <c>[]</c>, so absence is the game saying "this part has no cond rules". 340 of the 487 COs on one real
    /// station are missing it. Filling those with the <c>DEFAULT</c> marker would hand every one of them its def's
    /// rules — inventing state for a part the game recorded as having none.</para>
    ///
    /// <para>Nothing it touches feeds price, mass or the rating, so a heal cannot move what a ship is worth.</para>
    /// </summary>
    private static bool HealBehaviourConds(JsonObject co, PartDef def, double epoch)
    {
        var healed = false;

        // Present and non-zero on all 487 COs of a real station record, so absence here IS a reliable tell.
        if (co["fLastICOUpdate"] is null) { co["fLastICOUpdate"] = epoch; healed = true; }

        if (def.BehaviourConds.Length == 0) return healed;
        if (co["aConds"] is not JsonArray conds) return healed;

        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in conds)
            if (n is JsonValue v && v.TryGetValue<string>(out var s)) present.Add(LootDef.CondName(s));

        foreach (var entry in def.BehaviourConds)
        {
            var name = LootDef.CondName(entry);
            if (present.Contains(name)) continue;
            if (name == "IsDamageable" && present.Contains("IsUndamageable")) continue;
            if (name == "IsDestructable" && present.Contains("IsIndestructable")) continue;
            conds.Add(entry);
            healed = true;
        }
        return healed;
    }

    /// <summary>
    /// Spell out a CO's <c>aConds</c> when the <c>DEFAULT</c> marker would overrule something this writer is about
    /// to put on <paramref name="conds"/> — and only then, because expanding a marker that costs nothing would put
    /// twenty entries on every part in the record.
    ///
    /// <para><c>CondOwner.SetData</c> expands the marker by <b>appending</b> the def's own <c>aStartingConds</c> to
    /// whatever else the array holds, and applies the result in order, zeroing each cond before it sets it. So the
    /// def has the last word on every cond it declares, and an authored amount written beside the marker is
    /// overwritten by the def's stock value on load. Writing the def's conds out in full is the only way to get
    /// the last word back.</para>
    ///
    /// <para>Most conds a writer sets are not declared by any def — <c>StatDamage</c> is the usual one, and only
    /// the thirteen mineral defs declare it (as a spawn roll) — so this normally does nothing at all.</para>
    /// </summary>
    private static void ExpandDefaultIfContested(JsonObject co, PartDef? def, params string[] conds)
    {
        if (def is null || co["aConds"] is not JsonArray arr) return;
        var marker = -1;
        for (var i = 0; i < arr.Count; i++)
            if (arr[i] is JsonValue v && v.TryGetValue<string>(out var s) && s == "DEFAULT") { marker = i; break; }
        if (marker < 0) return;
        if (!conds.Any(def.Declares)) return;
        arr.RemoveAt(marker);
        foreach (var entry in def.CondEntries) arr.Add(entry);
    }

    /// <summary>Set a CO's <c>StatDamage</c> to <paramref name="amount"/> (in <c>StatDamageMax</c> count units):
    /// drop any existing <c>StatDamage</c>, and — when actually damaging it — its <c>IsPristine</c> resale flag
    /// (the game removes it on first damage), then add a single <c>StatDamage=1.0x&lt;amount&gt;</c>. A zero amount
    /// leaves the part undamaged (and keeps <c>IsPristine</c>). Does not touch <c>StatDamageMax</c>.
    /// <para><paramref name="def"/> is only needed for the handful of defs that declare <c>StatDamage</c>
    /// themselves — the minerals, which roll theirs on spawn. See <see cref="ExpandDefaultIfContested"/>.</para>
    /// <para><paramref name="item"/> is the part's item entry, and passing it is how wear reaches the <b>shallow</b>
    /// half of the record as well. The game writes both: <c>Ship.GetJsonItem</c> rebuilds each item entry from live
    /// state and mirrors the CO's damage onto it, and <c>DamageSystem.RemoveShallowDamage</c> exists to strip
    /// exactly that entry. Setting the CO alone leaves a worn ship reading its OLD condition at a broker and its
    /// new one once loaded.</para>
    /// <para>Shared with <see cref="SaveGrant"/>, which wears a granted ship the same way.</para></summary>
    internal static void SetStatDamage(JsonObject co, double amount, PartDef? def = null, JsonObject? item = null)
    {
        if (co["aConds"] is not JsonArray conds) co["aConds"] = conds = new JsonArray();
        ExpandDefaultIfContested(co, def, "StatDamage", "IsPristine");
        for (var i = conds.Count - 1; i >= 0; i--)
            if (conds[i] is JsonValue v && v.TryGetValue<string>(out var s)
                && (s.StartsWith("StatDamage=", StringComparison.Ordinal)
                    || (amount > 0 && s.StartsWith("IsPristine=", StringComparison.Ordinal))))
                conds.RemoveAt(i);
        if (amount > 0)
            conds.Add($"StatDamage=1.0x{amount.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)}");
        if (item is not null) SetDamageOverride(item, amount);
    }

    /// <summary>
    /// Mirror a part's damage onto its <b>item</b>, which is where the game keeps the copy it can read without
    /// loading the ship — the broker's item wrapper and the micrometeoroid pass over an unvisited hull both read
    /// it there (<c>Ostranauts.Trading.DataCOWrapper</c>, <c>Ostranauts.Ships.DamageSystem</c>).
    ///
    /// <para><c>Ship.GetJsonItem</c>'s exact rule: an entry when the damage clears <c>0.001</c>, and otherwise
    /// none at all. The game gets the "otherwise" for free because it builds every item entry from scratch on each
    /// save; a record edited in place has to take the stale entry out, or repairing a part leaves it reading
    /// damaged to everything that has not loaded the ship.</para>
    ///
    /// <para>Only <c>StatDamage</c> is mirrored, because that is the only cond the game itself ever writes here.
    /// A fill is the deliberate exception (see <see cref="SetFillOverrides"/>): the game has no authored-fill
    /// concept to write, both shallow readers pass unknown conds through untouched, and
    /// <c>RemoveShallowDamage</c> explicitly keeps everything that is not <c>StatDamage</c>.</para>
    /// </summary>
    private static void SetDamageOverride(JsonObject item, double amount)
    {
        var overrides = item["aCondOverrides"] as JsonArray;
        if (overrides is not null)
            for (var i = overrides.Count - 1; i >= 0; i--)
                if (overrides[i] is JsonObject o && Str(o, "CondName") == "StatDamage")
                    overrides.RemoveAt(i);

        if (amount <= 0.001)
        {
            if (overrides is { Count: 0 }) item.Remove("aCondOverrides");   // no entries is no field, as the game writes it
            return;
        }
        if (overrides is null) item["aCondOverrides"] = overrides = new JsonArray();
        overrides.Add(new JsonObject
        {
            ["CondName"] = "StatDamage",
            ["Chance"] = 1.0,
            ["Amount"] = Math.Abs(amount),
            ["NegativeValue"] = amount < 0,
        });
    }

    /// <summary>
    /// Write a container's authored fill onto its condition owner, which is what decides its contents once the ship
    /// actually loads.
    ///
    /// <para><see cref="SetFillOverrides"/> writes the same amounts onto the <b>item</b>, and that is not the same
    /// thing and not enough on its own. An item's <c>aCondOverrides</c> is the shallow-state channel: it is what
    /// the broker and the damage system read off a ship nobody has loaded, and on a full load
    /// <c>Ship.SpawnItems</c> feeds it to <c>CondOwner.SetCondAmount</c> — which routes through
    /// <c>AddCondAmount</c> and returns on its first line, because a CO built from save data has had its conds
    /// frozen since <c>SetData</c> and <c>Ship.PostGameLoad</c> does not thaw them until several hundred lines
    /// later. The game writes both and never notices, since its CO already carries the same values. A writer that
    /// puts an authored fill only on the item gets a tank that reads right at a broker and comes up holding the
    /// def's stock 13,373 mol the moment the player flies it.</para>
    ///
    /// <para>A template is the exception that proves it: a top-level item there carries no CO, so nothing is
    /// frozen and the override is the whole mechanism. That is why the mod export needs none of this.</para>
    /// </summary>
    internal static void SetFillConds(JsonObject? co, IReadOnlyDictionary<string, double> fill, PartDef? def, Catalog catalog)
    {
        if (co is null || ContainerFill.Describe(def, catalog) is not { } spec) return;
        if (co["aConds"] is not JsonArray conds) co["aConds"] = conds = new JsonArray();

        var lines = spec.Lines.Select(l => l.Cond).ToArray();
        ExpandDefaultIfContested(co, def, lines);
        var ours = new HashSet<string>(lines, StringComparer.Ordinal);
        for (var i = conds.Count - 1; i >= 0; i--)
            if (conds[i] is JsonValue v && v.TryGetValue<string>(out var s) && ours.Contains(LootDef.CondName(s)))
                conds.RemoveAt(i);

        // Every payload line is written, including the ones at the def's stock amount: the expansion above may have
        // just re-stated the def's own entry, and an emptied line has to say zero out loud or there is nothing to
        // zero. An entry at zero adds nothing after SetData's ZeroCondAmount, which is exactly "empty".
        foreach (var line in spec.Lines)
            conds.Add(FormattableString.Invariant($"{line.Cond}=1.0x{fill.GetValueOrDefault(line.Cond)}"));
    }

    /// <summary>
    /// Write a container's authored fill onto its <b>item</b> as per-instance condition overrides, merging with
    /// whatever overrides the save already put there (a part's own wear, typically) rather than replacing them.
    /// One entry per payload line that differs from what the def ships with, including an explicit <c>0</c> for an
    /// emptied line — without that the def's own 13,373 mol of O2 would survive being emptied.
    ///
    /// <para><c>JsonItem.ApplyOverrideCondsToCO</c> feeds each entry to <c>CondOwner.SetCondAmount</c>, so an
    /// override <b>sets</b> the amount rather than adding to it, and runs after the condowner is fully built.
    /// The game recomputes <c>StatGasPressure</c> and every <c>StatGasPp*</c> from these amounts on load (every
    /// canister carries <c>IsGasMolChanged</c>), so only the amounts are written.</para>
    ///
    /// <para>No-op when the part holds nothing editable.</para>
    /// </summary>
    internal static void SetFillOverrides(
        JsonObject item, IReadOnlyDictionary<string, double> fill, PartDef? def, Catalog catalog)
    {
        if (ContainerFill.Describe(def, catalog) is not { } spec) return;

        var wanted = new List<(string Cond, double Amount)>();
        foreach (var line in spec.Lines)
        {
            var amount = fill.GetValueOrDefault(line.Cond);
            if (Math.Abs(amount - line.Stock) > ContainerFill.Epsilon) wanted.Add((line.Cond, amount));
        }

        if (item["aCondOverrides"] is not JsonArray overrides)
        {
            if (wanted.Count == 0) return;   // nothing to say and nothing already said
            item["aCondOverrides"] = overrides = new JsonArray();
        }
        // drop our own previous say on these conds, keeping anything else the save wrote (wear, the cargo marker)
        var ours = new HashSet<string>(spec.Lines.Select(l => l.Cond), StringComparer.Ordinal);
        for (var i = overrides.Count - 1; i >= 0; i--)
            if (overrides[i] is JsonObject o && Str(o, "CondName") is { } name && ours.Contains(name))
                overrides.RemoveAt(i);

        foreach (var (cond, amount) in wanted)
            overrides.Add(new JsonObject
            {
                ["CondName"] = cond,
                ["Chance"] = 1.0,
                ["Amount"] = amount,
                ["NegativeValue"] = false,
            });
    }

    /// <summary>
    /// Emit the containers <paramref name="part"/> spawns with as part of ITSELF — a garment's pockets, a
    /// backpack's pouches, a PDA's data store — as slotted children of <paramref name="parentId"/>, each with its
    /// own condition owner naming the slot it occupies.
    ///
    /// <para>This is needed wherever a top-level item is given a synthesized CO, because giving it one is exactly
    /// what stops the game filling the item in for itself. <c>DataHandler.GetCondOwner</c> runs a def's
    /// <c>strLoot</c> only <c>if (bLoot &amp;&amp; jCOSIn == null)</c>, and an item that has save data takes the
    /// <c>dictCOSaves</c> branch instead, which recurses with <c>bLoot: false</c>. A save load also skips any item
    /// with no CO at all (<c>Ship.SpawnItems</c>), so for a save there is no version of this where the loot gets
    /// to run: the pockets are either written out here or the item never has any. A pure template export is the
    /// opposite case and needs none of this — a top-level item there carries no CO, so the game spawns it with
    /// <c>bLoot: true</c> and hands it its own pockets.</para>
    ///
    /// <para>Contained cargo does not come through here: it has a real cargo tree, and
    /// <c>EmitCargo</c> already writes its intrinsic children with the rest of it.</para>
    /// </summary>
    internal static void EmitIntrinsicContents(
        JsonArray outItems, JsonArray outCOs, string parentId, PartDef part, Catalog catalog,
        string regId, double epoch, double fx, double fy)
    {
        foreach (var child in CargoEdit.IntrinsicContentsOf(part, catalog)) Emit(child, parentId);

        void Emit(CargoItem c, string pid)
        {
            var id = Guid.NewGuid().ToString();
            outItems.Add(new JsonObject
            {
                ["strName"] = c.DefName, ["fX"] = fx, ["fY"] = fy, ["fRotation"] = 0.0, ["strID"] = id,
                [c.Slotted ? "strSlotParentID" : "strParentID"] = pid,
            });
            var co = SynthesizeCo(c.DefName, id, catalog, regId, epoch);
            if (c.GridX != 0 || c.GridY != 0) { co["inventoryX"] = c.GridX; co["inventoryY"] = c.GridY; }
            if (c.Slotted && c.SlotName is { Length: > 0 } slot) co["strSlotName"] = slot;
            outCOs.Add(co);
            foreach (var grandchild in c.Children) Emit(grandchild, id);   // a pocket could carry its own
        }
    }

    /// <summary>
    /// A pristine condition owner for a newly-added part, keyed to its item's <paramref name="strID"/>. A save
    /// load requires one CO per item; <c>aConds = ["DEFAULT"]</c> tells <c>CondOwner.SetData</c> to repopulate the
    /// def's own starting conds (verified against the decompile), so the part comes up freshly-built — the same
    /// pattern the game uses for a part whose conds still match its def.
    ///
    /// <para><b>A cooverlay skin cannot use that marker</b>, which is what <see cref="PartDef.SavedConds"/> is
    /// for: the marker resolves through <c>DataHandler.GetCondOwnerDef</c> to the skin's <b>base</b> condowner, and
    /// the skin's own cond-loot deltas can never be applied on top of a save-loaded CO (<c>SetData</c> freezes
    /// conds the moment it has save data, and the <c>COOverlay</c> runs after that). So a skin gets its conds
    /// written out in full, exactly as the game's own <c>CondOwner.GetJSON</c> writes them.</para>
    ///
    /// <para><c>aCondRules = ["DEFAULT"]</c> for the same reason the conds carry a marker, and it is not
    /// optional: <c>SetData</c> takes the rules from <c>jCOSIn.aCondRules</c> whenever there is save data, so a CO
    /// that omits the field loads with <b>no</b> cond rules at all. Sixty vanilla condowners declare them —
    /// every canister, RTA tank, reactor core and fire extinguisher — and the game writes the field on each. A def
    /// that declares none expands the marker to an empty set, so this is safe to write unconditionally.</para>
    /// </summary>
    internal static JsonObject SynthesizeCo(string def, string strID, Catalog catalog, string regId, double epoch)
    {
        var part = catalog.Lookup(def);
        var co = new JsonObject
        {
            ["strID"] = strID,
            ["strCODef"] = def,
            ["bAlive"] = true,
            // The def's own conds, plus the ones the game would have backfilled in SetUpBehaviours and cannot,
            // because that runs on a frozen CO (see PartDef.BehaviourConds). None of the backfilled conds is
            // declared by any def, by construction of the guards there, so they never collide with the marker's
            // expansion and can sit beside it.
            ["aConds"] = new JsonArray([
                .. (part?.SavedConds ?? ["DEFAULT"]).Concat(part?.BehaviourConds ?? []).Select(c => (JsonNode)c!)]),
            ["aCondRules"] = new JsonArray("DEFAULT"),
            ["strCondID"] = def + strID,
            // When this CO was last brought up to date. The game stamps a newly-created one with the current epoch
            // (CondOwner's own init does exactly this), and CatchUp/EndTurn advance every TIMED cond on the CO by
            // fEpoch - fLastICOUpdate. Left at zero that elapsed span is the whole age of the save, which expires
            // any timed cond the moment the ship is visited. Only two item families declare one as a starting cond
            // (the meat that evolves and the meat planter's harvest timer), but the cost of being right is a field.
            ["fLastICOUpdate"] = epoch,
            ["strIdleAnim"] = "Idle",
            ["strRegIDLast"] = regId,
        };
        if (part?.Friendly is { Length: > 0 } friendly) co["strFriendlyName"] = friendly;
        // a freshly-built device carries all the tickers its def declares (Power, etc.) — bake them, or the part
        // loads with no Power ticker and can never draw power (a save loads tickers from the CO, not the def).
        if (part is not null) BakeTickers(co, part, epoch, catalog, onlyPower: false);
        return co;
    }

    /// <summary>
    /// Record <paramref name="memberIds"/> on a stack head's condition owner, or clear the field when
    /// <paramref name="node"/> is not a stack.
    ///
    /// <para>A stack exists in a save only as its head CO's <c>aStack</c> (a list of member <c>strID</c>s that
    /// <c>CondOwner.PostGameLoad</c> re-collects). A lead item with its copies merely parented to it is not a
    /// stack: the game orphans the members and the container comes up holding N loose singles, which is what a
    /// hundred rounds of ammo authored into a ship weapon used to turn into. A real (drillable) container is left
    /// alone, since its children are separate items with their own inventory cells rather than stack members.</para>
    ///
    /// <para>A saved <c>aStack</c> is cleared only when the node has been taken down to a lone item, whose members
    /// the drop set has just removed from <c>aItems</c>. Anything else that carries the field keeps it: the field
    /// belongs to whatever wrote it, and a def that is stackable <i>and</i> reads as a container would be walked
    /// here as a container (<see cref="Cargo.BuildForest"/>) with its stack none of this method's business.</para>
    /// </summary>
    private static void SetStackMembers(JsonObject co, CargoItem node, IReadOnlyList<string> memberIds)
    {
        if (node.IsStack && memberIds.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var id in memberIds) arr.Add(id);
            co["aStack"] = arr;
        }
        else if (node.Children.Count == 0)
        {
            co.Remove("aStack");
        }
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
    internal static JsonArray? GpmSettings(Catalog catalog, string defName)
    {
        if (catalog.Lookup(defName) is not { } part) return null;
        var settings = catalog.GpmSettingsFor(part);
        if (settings.Count == 0) return null;
        var arr = new JsonArray();
        foreach (var (instance, dict) in settings)
            arr.Add(new JsonObject { ["strName"] = instance, ["dictGUIPropMap"] = JsonNode.Parse(dict.GetRawText()) });
        return arr;
    }

    /// <summary>
    /// Set or clear an item's <c>Rename</c> GPM panel to match the design (see <see cref="Rename"/>), leaving every
    /// other panel on the item alone.
    ///
    /// <para>Rewriting rather than adding matters on the kept/moved path, where the item is the save's own: a part
    /// the player named in game arrives carrying that panel, so renaming it here has to <b>replace</b> it and
    /// clearing the name has to <b>remove</b> it. Appending would happen to work today — the game's load merges
    /// duplicate panels per key with the last winning (<c>Ship.CreatePart</c>) — but it would grow a stale panel
    /// per edit and leave the item a shape the game itself never writes, which every later reader (Ostraplan's own
    /// import included) would have to know about.</para>
    ///
    /// <para>The name is written <b>verbatim</b> (<see cref="Rename.OrNull"/>): a name typed in Ostraplan was
    /// already normalised at the dialog, and one read off the save must go back exactly as it came, or a no-op
    /// write-back would rewrite the player's own data.</para>
    /// </summary>
    internal static void ApplyRename(JsonObject item, string? name)
    {
        var panels = item["aGPMSettings"] as JsonArray;
        var value = Rename.OrNull(name);
        if (panels is null && value is null) return;   // nothing there, nothing wanted

        if (panels is not null)
            for (var i = panels.Count - 1; i >= 0; i--)
                if (panels[i] is JsonObject existing && Str(existing, "strName") == Rename.Panel)
                    panels.RemoveAt(i);

        if (value is null)
        {
            // an item whose only panel was the rename keeps no empty array behind
            if (panels is { Count: 0 }) item.Remove("aGPMSettings");
            return;
        }

        if (panels is null) item["aGPMSettings"] = panels = [];
        panels.Add(Rename.PanelNode(value));
    }

    /// <summary>
    /// Write a nav console's <c>NavModConfig</c> panel — the module → screen-rect map the console's GUI reads
    /// (<see cref="NavConsole.ConfigEntries"/>) — merging into whatever panel the item already carries rather
    /// than replacing it, since a save's console holds the player's own arrangement in the same map.
    ///
    /// <para>With <paramref name="onlyFillEmpty"/> an existing non-empty position is never touched: on a kept
    /// console only the modules Ostraplan has just put in get placed, and a screen the player laid out in game
    /// comes back exactly as they left it. A new console has no history to protect, so its entries are written
    /// outright over the defaults its def supplied.</para>
    /// </summary>
    internal static void ApplyNavConfig(JsonObject item, IReadOnlyList<(string Key, string Value)> entries, bool onlyFillEmpty)
    {
        if (entries.Count == 0) return;
        if (item["aGPMSettings"] is not JsonArray panels) item["aGPMSettings"] = panels = [];

        var panel = panels.OfType<JsonObject>().FirstOrDefault(p => Str(p, "strName") == "NavModConfig");
        if (panel is null)
        {
            panel = new JsonObject { ["strName"] = "NavModConfig", ["dictGUIPropMap"] = new JsonArray() };
            panels.Add(panel);
        }
        if (panel["dictGUIPropMap"] is not JsonArray flat) panel["dictGUIPropMap"] = flat = [];

        foreach (var (key, value) in entries)
        {
            // the map is the game's flat [key, value, key, value, …] array, so an existing key is rewritten in place
            var at = -1;
            for (var i = 0; i + 1 < flat.Count; i += 2)
                if (flat[i]?.GetValue<string>() == key) { at = i + 1; break; }
            if (at < 0) { flat.Add(key); flat.Add(value); continue; }
            if (onlyFillEmpty && flat[at]?.GetValue<string>() is { Length: > 0 }) continue;   // the player's own placement
            flat[at] = value;
        }
    }

    private static string SourceDir(SaveShipContext ctx) => Path.GetDirectoryName(ctx.ZipPath)!;

    internal static void UpdateSaveInfo(string path, string? name, double? money)
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

    /// <summary>The player's current credit balance — the summed <c>StatUSD</c> on the player CO
    /// (<see cref="SaveShipContext.PlayerCoId"/>), or null when there is no CO to charge. Reads the edited record
    /// first, since the player is usually standing on the ship they are editing, and otherwise falls back to the
    /// balance <see cref="LocatePlayerBalance"/> resolved at import from wherever the CO actually lives.</summary>
    public static double? CurrentBalance(SaveShipContext ctx) =>
        ctx.PlayerCoId is { } coId && FindCo(ctx.ShipRecord, coId) is { } co ? SumStatUsd(co) : ctx.PlayerBalance;

    /// <summary>
    /// Find the player CO and read their balance, for the import that builds a <see cref="SaveShipContext"/>.
    /// Returns the RegID of the record holding the CO and the balance on it, or <c>(null, null)</c> when the
    /// character cannot be located at all.
    ///
    /// <para>The CO is on the edited ship whenever the player was standing on it, and that record is already
    /// parsed. Otherwise it is in the record for whatever they <b>were</b> standing on — a station while docked,
    /// or another of their ships — which has to be read out of the zip. That second read is why this is resolved
    /// once, at import, rather than on demand: a station record runs to tens of megabytes.</para>
    /// </summary>
    internal static (string? RegId, double? Balance) LocatePlayerBalance(
        ZipArchive zip, JsonNode editedRecord, string editedRegId, string? coId, string? standingOnRegId)
    {
        if (coId is not { Length: > 0 }) return (null, null);
        if (FindCo(editedRecord, coId) is { } aboard) return (editedRegId, SumStatUsd(aboard));
        if (standingOnRegId is not { Length: > 0 } || standingOnRegId == editedRegId) return (null, null);

        try
        {
            if (zip.GetEntry(SaveZip.ShipEntry(standingOnRegId)) is not { } entry) return (null, null);
            if (FindPlayerCoInFile(JsonNode.Parse(SaveImport.ReadText(entry)), coId) is not { } elsewhere)
                return (null, null);
            return (standingOnRegId, SumStatUsd(elsewhere));
        }
        catch (Exception)
        {
            return (null, null);   // an unreadable record just means no deduction, never a failed import
        }
    }

    /// <summary>The player CO inside a ship <b>file</b>'s parsed node, searching every ship record in it rather
    /// than only the largest — a save's ship file can hold siblings, and the character is not necessarily on the
    /// biggest of them. Used by both the read and the write, so the two always agree on which entry to touch.</summary>
    private static JsonObject? FindPlayerCoInFile(JsonNode? top, string coId)
    {
        IEnumerable<JsonObject> ships = top switch
        {
            JsonArray arr => arr.OfType<JsonObject>(),
            JsonObject obj => new[] { obj },
            _ => [],
        };
        foreach (var ship in ships)
            if (FindCo(ship, coId) is { } co) return co;
        return null;
    }

    /// <summary>One ship record's <c>aCOs</c> entry with this <c>strID</c>, or null.</summary>
    private static JsonObject? FindCo(JsonNode? ship, string coId)
    {
        foreach (var co in Arr(ship, "aCOs"))
            if (co is JsonObject o && Str(o, "strID") == coId) return o;
        return null;
    }

    /// <summary>
    /// Take this ship's condowners out of the session record, now that the spliced ship record carries them.
    ///
    /// <para>A no-op in the usual case: when the player was standing on the ship being edited, its COs were already
    /// in its own record and nothing was adopted. Otherwise the same <c>strID</c> would be defined in two records
    /// and <c>DataHandler.dictCOSaves</c> would keep whichever loaded last — which for an edited CO (re-rolled
    /// wear, a healed power ticker, a recomputed <c>nDestTile</c>) decides whether the edit took effect at all. The
    /// room condowners go too: <c>aRooms</c> is regenerated with fresh ids, so the old ones are left referring to
    /// compartments that no longer exist.</para>
    /// </summary>
    private static void PruneRelocatedCos(SaveShipContext ctx, string zipPath)
    {
        if (ctx.RelocatedCoIds.Count == 0 || ctx.SessionEntryName is not { Length: > 0 } entryName) return;

        var remove = new HashSet<string>(ctx.RelocatedCoIds, StringComparer.Ordinal);
        remove.UnionWith(RoomCoIds(ctx));

        using var za = ZipFile.Open(zipPath, ZipArchiveMode.Update);
        var buffer = new MemoryStream();
        SessionCos.RemoveInto(za, entryName, remove, buffer);

        za.GetEntry(entryName)!.Delete();
        buffer.Position = 0;
        using var dest = za.CreateEntry(entryName).Open();
        buffer.CopyTo(dest);
    }

    /// <summary>
    /// Write the deducted balance onto the player CO in the record that <b>holds</b> it, inside
    /// <paramref name="zipPath"/>. A no-op in the usual case: when the player was aboard the ship being edited,
    /// the deduction already rode along in the ship record the inject spliced. This is the docked (or
    /// standing-on-another-ship) case, where the character — and so the money — is in a second record that the
    /// edit does not otherwise touch.
    /// </summary>
    private static void PatchPlayerBalance(SaveShipContext ctx, string zipPath, double? newBalance)
    {
        if (newBalance is not { } bal || ctx.PlayerCoId is not { Length: > 0 } coId) return;
        if (ctx.PlayerCoRegId is not { Length: > 0 } reg || reg == ctx.Source.RegId) return;

        using var za = ZipFile.Open(zipPath, ZipArchiveMode.Update);
        var entryName = SaveZip.ShipEntry(reg);
        var entry = za.GetEntry(entryName)
            ?? throw new InvalidDataException(
                $"'{entryName}' holds the player's money but is not in the save, so the edit cost can't be deducted.");

        string text;
        using (var r = new StreamReader(entry.Open())) text = r.ReadToEnd();
        var top = JsonNode.Parse(text);
        if (FindPlayerCoInFile(top, coId) is not { } co)
            throw new InvalidDataException(
                "The player's money is no longer where the save said it was, so the edit cost can't be deducted. " +
                "Untick \"Deduct the edit cost\" and try again.");

        SetStatUsd(co, bal);
        entry.Delete();
        using var w = new StreamWriter(za.CreateEntry(entryName).Open());
        w.Write(top!.ToJsonString(Indented));
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
    internal static void SetStatUsd(JsonObject co, double newBalance)
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
    internal static string MaterializeCopy(string srcDir, string srcZipName, string destDir, double? newMoney)
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
