using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ostraplan.Core;

/// <summary>What the user chose in the export dialog. <see cref="DestinationParent"/> is
/// the folder the mod folder is created <b>inside</b> (a user-picked directory or the game's
/// <c>Ostranauts_Data/Mods</c>); the mod folder itself is named after the ship.
/// <para><see cref="PublicName"/> is the ship's in-game display name (shown at the XPDR
/// transponder, comms, broker listings, MFD dock info, and the rating screen) — distinct from
/// <see cref="ShipName"/>, which only names the mod/file. A name typed here is written through
/// verbatim and sticks, because the game only re-rolls a random <c>publicName</c> when the
/// on-disk value is null, empty or <c>"$TEMPLATE"</c> (verified against decompiled
/// <c>Ship.InitShip</c>). Left blank it resolves to <see cref="ShipExport.VariedNames"/> and the
/// game names each spawned copy as it names its own ships. It must <b>not</b> fall back to
/// <see cref="ShipName"/>: that is the design's file name, and shipping it as the ship's visible
/// name is what put "fCargoTug" on a player's nav display.</para>
/// <para><see cref="Make"/>/<see cref="Model"/>/<see cref="Year"/>/<see cref="Designation"/>/
/// <see cref="Description"/> map straight onto the game's own <c>JsonShip</c> fields (present on
/// core ships and used by mods like Ithalan's Additional Ships) — flavor text only, no game logic
/// reads them beyond display.</para>
/// <para><see cref="ReplaceTarget"/>, when set, is the <c>strName</c> of an existing (core or mod)
/// ship this design should <b>replace</b>: the exported ship is keyed to that name so — loaded after
/// core — the game's whole-object override swaps the design in for the original everywhere it spawns.
/// It changes nothing about naming: an export takes the vanilla varied names when
/// <see cref="PublicName"/> is blank whether or not it replaces anything.</para>
/// <para><see cref="ModName"/> names the mod itself (its <c>mod_info.json strName</c> + folder), separate
/// from the ship. Blank resolves (<see cref="ShipExport.ResolveModName"/>) to <c>"{ReplaceTarget} - Replaced
/// via Ostraplan"</c> for a replacement (so the mod is distinct from the ship it overrides), else to
/// <see cref="ShipName"/>.</para></summary>
/// <param name="Preview">The rendered preview images the mod ships in <c>images/ships/</c>, or null to write none.
/// Rendering needs the sprite atlas, which only the app has, so the caller supplies the encoded PNGs and
/// <see cref="ShipExport.Write"/> only files them. See <see cref="ShipPreview"/> for why they matter.</param>
public sealed record ExportOptions(
    string ShipName, string Author, string Notes, string ModVersion, string GameVersion,
    string DestinationParent, string PublicName, string Make = "", string Model = "",
    string Year = "", string Designation = "", string Description = "",
    ShipDelivery? Delivery = null, string? ReplaceTarget = null, string ModName = "",
    WearOptions? Wear = null, ShipPreview? Preview = null);

/// <summary>
/// The preview art a ship mod ships alongside its data file, as encoded PNG bytes.
///
/// <para>The game looks these up by the ship's <c>strName</c> under <c>&lt;mod&gt;/images/ships/&lt;strName&gt;/</c>
/// (<c>DataHandler.LoadPNG</c> searches every loaded mod's <c>images/</c> in load order, most recent first). Chargen
/// loads exactly one file, <c>&lt;strName&gt;.png</c>, and has <b>no</b> fallback: a miss renders
/// <c>Resources/Sprites/missing</c>, the red X. The broker kiosk instead loads the whole folder
/// (<c>DataHandler.LoadPNGFolder</c>), treats the one file whose name contains the ship's <c>strName</c> as the main
/// image and every other as a room thumbnail, and falls back to a generated silhouette when the folder is empty.</para>
///
/// <para><see cref="Rooms"/> names are the game's own room-spec <c>strName</c>s, deduplicated with an
/// <c>_1</c>/<c>_2</c> suffix exactly as <c>ScreenshotUtil.BuildTargetDict</c> does, because the broker maps a
/// thumbnail back to its room icon by stripping at the first underscore and looking the remainder up in
/// <c>data/rooms</c>.</para>
/// </summary>
public sealed record ShipPreview(byte[] Ship, IReadOnlyList<ShipPreviewRoom> Rooms);

/// <summary>One room thumbnail: the file stem (a room-spec <c>strName</c>, possibly <c>_N</c>-suffixed) and its
/// encoded PNG.</summary>
public sealed record ShipPreviewRoom(string Name, byte[] Png);

/// <summary>How the exported ship becomes obtainable in game — the loot/chargen data an export
/// injects on top of the ship file. All of it is full-object overrides / additive entries the game
/// merges by <c>strName</c>; a same-pool clash with another ship mod is Ostrasort's <c>--patch</c> case.
/// <see cref="TouchesLoot"/> is true when anything here writes <c>data/loot</c> (drives the Ostrasort
/// patch follow-up). Default <see cref="None"/> exports the ship file only, as before.</summary>
/// <param name="StartingShipExclusive">When a starting ship: if true, the exported ship <b>replaces</b> the
/// Shipbreaker start-event pick pool with only this ship (guaranteed start), dropping the vanilla salvage pods;
/// if false (default) it is appended as one more weighted option alongside them.</param>
/// <param name="DerelictPools">Derelict-ring pools to add the ship to (see <see cref="KioskExport.DerelictPools"/>).
/// These place wrecks at <b>world generation</b>, so they only ever affect a new game; the spawner marks the ship
/// derelict and damages it, which is why an export aimed at them should bake no wear of its own.</param>
public sealed record ShipDelivery(
    IReadOnlyList<string> BrokerPools, double BrokerWeight,
    IReadOnlyList<string> SpecialOfferPools,
    bool StartingShip, double StartingShipWeight, string StartingShipStation,
    double StartingShipMortgage, string StartingShipTitle, string StartingShipDesc,
    bool StartingShipExclusive = false,
    IReadOnlyList<string>? DerelictPools = null, double DerelictWeight = 0.05)
{
    /// <summary>The derelict pools, never null.</summary>
    public IReadOnlyList<string> Derelicts => DerelictPools ?? [];

    public bool TouchesLoot =>
        BrokerPools.Count > 0 || SpecialOfferPools.Count > 0 || StartingShip || Derelicts.Count > 0;

    /// <summary>Whether anything at all will spawn this ship. A false here is a ship file the game will never
    /// place on its own, which the export wizard refuses rather than writing an unreachable mod.</summary>
    public bool IsObtainable => TouchesLoot;

    public static ShipDelivery None => new([], 0, [], false, 0, "OKLG", 0, "", "");
}

/// <summary>The outcome of a successful export: where it landed and what it contains.
/// <see cref="TouchedLootPools"/> is true when the export wrote broker/Special-Offer/starting-ship loot
/// (so the caller knows an Ostrasort <c>--patch</c> pass may be warranted).</summary>
public sealed record ExportResult(
    string ModDir, string ShipJsonPath, string ModInfoPath,
    int PartCount, int RoomCount, ShipRating Rating, IReadOnlyList<string> Warnings,
    bool TouchedLootPools = false, int PreviewCount = 0);

/// <summary>Flavor/identity fields for <see cref="ShipExport.Build"/>, split out from
/// <see cref="ExportOptions"/> so callers that don't care about ship metadata (most tests) can omit
/// it entirely. See <see cref="ExportOptions.PublicName"/> for why <see cref="PublicName"/> matters
/// more than it looks — it's the one field the game actually keeps sticky across spawns.</summary>
public sealed record ExportMetadata(
    string PublicName = "", string Make = "", string Model = "", string Year = "",
    string Designation = "", string Description = "");

/// <summary>
/// Exports the current design as a spawnable local data mod: a <c>data/ships/&lt;Name&gt;.json</c>
/// in the game's own JsonShip shape plus a <c>mod_info.json</c>. The hard part is the reverse of
/// the loader's coordinate/rotation mapping (<see cref="ShipGrid"/>): a document part's 0-based
/// grid top-left + Ostraplan rotation become the game's centre <c>(fX,fY)</c> + CCW
/// <c>fRotation</c>, with the export grid anchored at <c>vShipPos = (0,0)</c> so the two extra
/// offset terms vanish. <c>aRooms</c>/<c>aRating</c> are precomputed with the same P2 engine the
/// game recomputes on full load, so the broker/registry rating shown on shallow load already
/// matches — verified by the round-trip test.
/// <para><b>Never writes <c>loading_order.json</c></b>: registration stays single-owner with
/// ModTools/Ostrasort (the caller's dialog says so). Staging into the game Mods folder is the only
/// write into the install and only on the user's explicit choice.</para>
/// </summary>
public static class ShipExport
{
    /// <summary>
    /// The <c>publicName</c> the game reads as "name this ship yourself". <c>Ship.InitShip</c> rolls a fresh
    /// <c>DataHandler.GetShipName()</c> whenever the stored value is null, empty or this sentinel, and keeps any
    /// other string verbatim (GAME-INTERNALS §"Ship identity on spawn"). All but a handful of the 220 core ship
    /// templates carry it, so it is what an unnamed ship should be written as rather than blank: blank and the
    /// sentinel behave identically in game, but the sentinel is what the game's own data looks like.
    /// </summary>
    public const string VariedNames = "$TEMPLATE";

    /// <summary>Metres per tile (16&#160;px); the game's dimensions string uses this (10×12 → "3.20m x 3.84m").</summary>
    private const double MetresPerTile = 0.32;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The per-instance override every contained item carries so the game keeps it on a template spawn
    /// (see <c>EmitCargo</c>). A single StatDamage=0 (Amount 0 = undamaged) — a benign "pristine" instruction that is
    /// also a non-null array, which is the exact condition <c>Ship.SpawnItems</c> tests to retain a parented item.
    /// Immutable and shared: the exporter only ever reads it.</summary>
    private static readonly ExportedCondOverride[] PristineMarker =
        [new() { CondName = "StatDamage", Chance = 1.0, Amount = 0.0, NegativeValue = false }];

    /// <summary>
    /// Run the P2 engine and assemble the JsonShip-shaped export object for the design.
    /// Pure and testable — no file I/O. <paramref name="warnings"/> collects anything the
    /// export dropped (currently: nothing — every placed part resolves — but reserved for
    /// unresolved defs so the caller can always surface a report).
    ///
    /// <para><paramref name="itemIdByPlacementId"/> is an optional collector, filled with
    /// <see cref="Placement.Id"/> → the fresh item <c>strID</c> this export minted for it. Every item here is new,
    /// so this is the only way back from a written item to the design part that produced it — which
    /// <see cref="SaveGrant"/> needs to carry each part's real condition over from the save it came from.
    /// Optional because no other caller has anything to say about where a part came from.</para>
    /// </summary>
    public static (ExportedShip Ship, ShipRating Rating, int RoomCount) Build(
        ShipDocument doc, Catalog catalog, IReadOnlyList<RoomSpecDef> specs, string shipName,
        List<string>? warnings = null, ExportMetadata? meta = null, WearOptions? wear = null,
        IDictionary<string, string>? itemIdByPlacementId = null)
    {
        var grid = ShipGrid.FromDocument(doc, catalog);
        var partition = RoomBuilder.Build(grid);
        RoomCertifier.CertifyAll(partition, specs, catalog);
        var rating = Rating.Calculate(grid, partition, catalog);

        // Optional wear: damage each installed part the way a kiosk "Used" ship is worn (see WearModel). The
        // damage rides on each part's aCondOverrides (StatDamage) below, and the mean condition becomes the baked
        // aRating "Condition" slot. DMGStatus stays 0 (New) so the game keeps exactly this baked wear — a New ship
        // never runs its own DamageAllCOs pass.
        Random? wearRng = null;
        double wearCeiling = 0;
        if (wear is { Enabled: true } wo)
        {
            wearRng = WearModel.NewRng(wo);
            wearCeiling = WearModel.CeilingFor(wo.TargetCondition);
        }

        // A design can also carry condition PAINTED per part (Placement.Condition), which is authored rather than
        // rolled and therefore travels whatever the wear setting says — a pristine export of a deliberately
        // battered station still has to arrive battered. So the rates list is collected whenever either source is
        // in play, and a painted part wins over the roll wherever both would speak.
        var anyPainted = doc.Placements.Any(p => p.Condition is not null);
        List<double>? wearRates = wearRng is not null || anyPainted ? [] : null;

        // map a grid part back to its source placement (PlacedPart.StrID == Placement.Id) so its contained cargo
        // travels into the export
        var byPlacementId = doc.Placements.ToDictionary(p => p.Id.ToString());

        // device signal connections need each part's fresh export strID + its emitted item, gathered in the loop
        // below and wired up after (see WireDeviceLinks).
        var exportIdByPlacementId = new Dictionary<string, string>();
        var itemByExportId = new Dictionary<string, ExportedItem>();

        var items = new List<ExportedItem>(grid.Parts.Count);
        var cos = new List<ExportedCondOwnerSave>();

        // Installed docking ports, collected as we emit items so they can be baked into aDockingPorts below.
        // Anchor = the port's centre grid tile, so the boarding spawner can be placed just inside the airlock.
        var docksysPorts = new List<(string Id, bool TypeB, bool PrimaryDef, int Anchor)>();

        // Emit a container's contents the way a SAVE stores them, because a data/ships file spawns as a TEMPLATE
        // (bTemplateOnly) and a template can't otherwise keep authored cargo. Ship.SpawnItems (decompiled):
        //   * drops any parented item unless it has aCondOverrides (→ the item's root container is flagged, which
        //     also clears bLoot so the container isn't refilled from its DEFAULT loot) OR bForceLoad (→ the item
        //     keeps its strID instead of getting a fresh one);
        //   * reconstructs a STACK only from the stack-head CO's aStack (a list of member strIDs) in
        //     CondOwner.PostGameLoad — which needs the head's baked CO and the members to keep their strIDs.
        // So each contained item carries BOTH bForceLoad (keep strID) AND the aCondOverrides "pristine" marker
        // (survive + suppress the container's default loot), plus a baked aCOs entry; a stack head's CO lists its
        // members in aStack so the game rebuilds the ×N stack at the right count (a bare lead+members chain alone
        // orphaned the members and collapsed the stack). The marker is a StatDamage=0 override (Amount 0 =
        // undamaged): real, benign, and a non-null array (the exact gate SpawnItems tests). Recurses so nested
        // containers and stacks come through; loose cargo parents by strParentID, equipped gear by strSlotParentID.
        // Returns the emitted item's fresh strID so a parent stack head can collect its members.
        string EmitContained(CargoItem c, string parentStrID, double fx, double fy)
        {
            var cid = Guid.NewGuid().ToString();
            var item = new ExportedItem
            {
                StrName = c.DefName, FX = fx, FY = fy, FRotation = c.GridRot, StrID = cid,
                ACondOverrides = PristineMarker, BForceLoad = true,
            };
            if (c.Slotted) item.StrSlotParentID = parentStrID; else item.StrParentID = parentStrID;
            // A name the user gave this item, on its own GPM panel exactly as a placed part's is. A stack head
            // carries the pile's name and its members carry none, which is where the game keeps it.
            if (c.CustomName is { } cargoName) item.AGPMSettings = [RenameGpm(cargoName)];
            items.Add(item);

            var childIds = c.Children.Select(child => EmitContained(child, cid, fx, fy)).ToList();

            var cargoCo = ExportedCondOwnerSave.For(c.DefName, cid, catalog);
            cargoCo.InventoryX = c.GridX;
            cargoCo.InventoryY = c.GridY;
            // What the game re-slots a slotted item by on load: Ship.SpawnItems passes this to
            // compSlots.SlotItem, which returns false on a null name and leaves the item unattached.
            cargoCo.StrSlotName = c.Slotted ? c.SlotName : null;
            // A stack head lists its members; a real (drillable) container does not — its children are separate
            // items positioned by their own inventory cells, not stack members of the container.
            cargoCo.AStack = c.IsStack && childIds.Count > 0 ? childIds.ToArray() : null;
            cos.Add(cargoCo);
            return cid;
        }

        void EmitCargo(IReadOnlyList<CargoItem> nodes, string parentStrID, double fx, double fy)
        {
            foreach (var c in nodes) EmitContained(c, parentStrID, fx, fy);
        }

        // aItems is emitted in document order, which is the order the parts were laid down. It used to be
        // partitioned with every trigger-carrying part first, because up to game 1.0.0.16 a missile judged a tile
        // on its first part alone; 1.0.0.17 reads the whole stack, so there is nothing left to arrange (§26, #45).
        foreach (var part in grid.Parts)
        {
            var (w, h) = GridMath.Size(part.Part.Item.Width, part.Part.Item.Height, part.Rot);
            // inverse of ShipGrid.FromTemplate with vShipPos=(0,0): centre = top-left + (size/2 − 0.5),
            // y flips (grid is y-down, the game is y-up), and fRotation is CCW = Norm(−Rot).
            var fx = part.TopLeftCol + (w / 2.0 - 0.5);
            var fy = -(part.TopLeftRow + (h / 2.0 - 0.5));
            var strID = Guid.NewGuid().ToString();
            var item = new ExportedItem
            {
                StrName = part.Part.DefName,
                FX = fx,
                FY = fy,
                FRotation = GridMath.Norm(-part.Rot),
                StrID = strID,
            };
            // A name the user gave this part travels as the game's own Rename panel, which Ship.SpawnItems
            // re-applies on load (see Rename). Set before wiring, which appends to whatever is already here.
            if (part.StrID is { } namedId && byPlacementId.TryGetValue(namedId, out var namedPlacement)
                && namedPlacement.CustomName is { } customName)
                item.AGPMSettings = [RenameGpm(customName)];
            items.Add(item);
            if (part.StrID is { } placementId)
            {
                exportIdByPlacementId[placementId] = strID;
                itemByExportId[strID] = item;
            }

            var placement = part.StrID is { } pid ? byPlacementId.GetValueOrDefault(pid) : null;

            // Per-instance condition overrides. Two things write here — wear and an authored container fill —
            // so they accumulate into one list and are assigned once at the end. Assigning the array directly
            // would let whichever ran second erase the other's work.
            List<ExportedCondOverride>? overrides = null;

            // Wear: damage installed parts (the set the rating's Condition slot averages over). A part with a
            // StatDamageMax health pool and no IsSystem flag takes uniform(0, ceiling)·M damage; system/undamageable
            // installed parts count as pristine in the grade but are left untouched, exactly like the game.
            if (wearRates is not null && part.Part.Has("IsInstalled"))
            {
                var damageMax = part.Part.StartingCondValues.GetValueOrDefault("StatDamageMax");
                if (damageMax > 0 && !part.Part.Has("IsSystem"))
                {
                    // A painted condition is an authored fact and takes precedence over the roll; an unpainted
                    // part falls to the roll, or to nothing when the pass is unarmed. Both land in the same
                    // StatDamage override, because both are the same thing to the game.
                    var dmg = placement?.Condition is { } painted
                        ? (1.0 - painted) * damageMax
                        : wearRng is null ? 0.0 : WearModel.DamageAmount(wearRng, wearCeiling, damageMax);
                    if (dmg > 0)
                        (overrides ??= []).Add(new ExportedCondOverride { CondName = "StatDamage", Chance = 1.0, Amount = dmg });
                    wearRates.Add(dmg / damageMax);
                }
                else
                {
                    wearRates.Add(0.0);
                }
            }

            // An authored fill: one override per payload line, set absolutely. JsonItem.ApplyOverrideCondsToCO
            // calls CondOwner.SetCondAmount, so an override REPLACES the def's own amount rather than adding to
            // it, which is what a fill needs — and it means an emptied line has to be written as an explicit 0
            // or the def's 13,373 mol of O2 would survive being emptied. The game derives StatGasPressure and
            // every StatGasPp* from these on load (the canisters all carry IsGasMolChanged), so only the amounts
            // are written here.
            if (placement?.Fill is { } fill && ContainerFill.Describe(catalog.Lookup(part.Part.DefName), catalog) is { } spec)
                foreach (var line in spec.Lines)
                {
                    var amount = fill.GetValueOrDefault(line.Cond);
                    if (Math.Abs(amount - line.Stock) <= ContainerFill.Epsilon) continue;   // already what the def gives
                    (overrides ??= []).Add(new ExportedCondOverride { CondName = line.Cond, Chance = 1.0, Amount = amount });
                }

            // A weapon's MFD page: one override per cond the designer moved off the def's own value. The same
            // absolute-set semantics the fill above relies on is what makes this work at all — a firing group is
            // a replacement, not an increment — and it is how a mass thrower gets a group its def never declared
            // (CondOwner.AddCondAmount falls back to the global cond, so an override may introduce one).
            if (placement?.Weapon is { } weapon)
                foreach (var (cond, amount) in WeaponPanel.Overrides(weapon, catalog.Lookup(part.Part.DefName)))
                    (overrides ??= []).Add(new ExportedCondOverride { CondName = cond, Chance = 1.0, Amount = amount });

            if (overrides is { Count: > 0 }) item.ACondOverrides = [.. overrides];

            if (IsDocksysPart(part.Part, catalog))
                docksysPorts.Add((strID, part.Part.Has(ProblemScan.TypeBCond),
                    catalog.IsPrimaryDocksys(catalog.Lookup(part.Part.DefName)), part.AnchorIndex));

            if (placement is { Cargo.Count: > 0 })
            {
                EmitCargo(placement.Cargo, strID, fx, fy);   // the design's contents (original + authored), pristine
            }
            if (NavConsole.IsConsole(part.Part))
            {
                // The console's screen arrangement. Whatever modules it ends up carrying, bake where each one sits
                // (NavConsole.Arrange / ConfigEntries) so the console the player sits at is laid out the way we
                // planned it rather than however the game happens to walk the container. Item prop maps merge into
                // the def's key by key, so this panel touches nothing else the console declares.
                var modules = NavConsole.NeedsModules(placement?.Cargo ?? [])
                    ? NavConsole.StandardModules
                    : placement!.Cargo.Where(c => !c.Slotted).Select(c => c.DefName).ToList();
                if (catalog.Lookup(part.Part.DefName) is { } consoleDef
                    && NavConsole.ConfigEntries(catalog, consoleDef, modules, placement?.NavLayout) is { Count: > 0 } entries)
                    item.AGPMSettings = [.. item.AGPMSettings ?? [], NavConfigGpm(entries)];
            }
            if (NavConsole.IsConsole(part.Part) && NavConsole.NeedsModules(placement?.Cargo ?? []))
            {
                // A nav console with no MODULES is a bare frame: its interface is assembled from hot-swappable
                // module items held loose inside it, so install the standard set here or it spawns blank. The test
                // is NavConsole.NeedsModules, not "has no cargo": every console carries a slotted data chip, which
                // is not a screen. Each module is baked the same way as EmitContained's cargo (bForceLoad + marker +
                // a CO): a nav console has no default module loot, so without that the modules would be dropped on a
                // template spawn and the console would come back empty (see EmitContained, NavConsole).
                // A console that already carries modules keeps exactly those, via EmitCargo above.
                foreach (var modDef in NavConsole.StandardModules)
                {
                    var modId = Guid.NewGuid().ToString();
                    items.Add(new ExportedItem
                    {
                        StrName = modDef, FX = fx, FY = fy, FRotation = 0, StrID = modId,
                        StrParentID = strID, ACondOverrides = PristineMarker, BForceLoad = true,
                    });
                    cos.Add(ExportedCondOwnerSave.For(modDef, modId, catalog));
                }
            }
        }

        // Device signal connections, both channels: the breaker graph on each wired part's Electrical panel, and
        // each device's own panel naming the sensor it follows. Only resolved links (both endpoints present)
        // reach either. See GAME-INTERNALS §14.
        WireDeviceLinks(doc, exportIdByPlacementId, itemByExportId);
        WireSensorLinks(doc, catalog, exportIdByPlacementId, itemByExportId, byPlacementId);

        if (itemIdByPlacementId is not null)
            foreach (var (placementId, exportId) in exportIdByPlacementId)
                itemIdByPlacementId[placementId] = exportId;

        // Fold the applied wear into the rating's Condition slot (the mean over installed parts), so the baked
        // aRating the broker/registry reads on a shallow load already shows the worn grade.
        if (wearRates is not null)
            rating = rating with { Condition = WearModel.GradeFor(wearRates) };

        // Loose items dropped on the floor (the Items palette): the stack head is a free-standing, parentless
        // top-level item at its tile — exactly how a core template lists floor cargo (a salvage pod's scrap, a
        // bunk's effects). The loader keeps a top-level item unconditionally (unlike a parented one, which needs the
        // bForceLoad/marker gate), so the single-item case needs no CO record. A quantity > 1 is a STACK: the extra
        // copies are members parented to the head (with the pristine marker + bForceLoad so they survive and keep
        // their strIDs), and the head gets a CO whose aStack lists them, the same shape EmitContained bakes for a
        // container's stacked cargo (see CondOwner.PostGameLoad).
        // Person-spawn points the design authors itself. They are deck items in the document and a separate array
        // in the file, because the game keeps the two apart absolutely: every spawner in aItems is a Loot one and
        // every spawner in aShallowPSpecs is a Pspec or Pspec Loot one (#55). So the panel's type is what decides
        // which array a spawner leaves by, and this collects the ones bound for the other one.
        var authoredPSpecs = new List<ExportedShallowPSpec>();

        foreach (var lo in doc.LooseObjects)
        {
            if (catalog.Lookup(lo.DefName) is not { } part) { warnings?.Add($"Loose item '{lo.DefName}' has no def; skipped."); continue; }
            var (w, h) = GridMath.Size(part.Item.Width, part.Item.Height, lo.Rot);
            // A LooseObject stores DOCUMENT tile coords, but the file is written in GRID coords (vShipPos is
            // (0,0) and every part above comes from PlacedPart.TopLeftCol/Row). Rebase through the grid origin
            // — ShipGrid.VShipPos holds the document tile at grid (0,0), i.e. the bbox minus the one-tile pad —
            // or every loose item lands displaced by that origin, which is (0,0) only for a design whose bounds
            // start at (1,1). Zones do the same conversion via ZoneGeometry.DocToIndex.
            var gx = lo.X - (int)grid.VShipPosX;
            var gy = lo.Y - (int)grid.VShipPosY;
            var fx = gx + (w / 2.0 - 0.5);
            var fy = -(gy + (h / 2.0 - 0.5));
            var rot = GridMath.Norm(-lo.Rot);
            var headId = Guid.NewGuid().ToString();
            var qty = Math.Clamp(lo.Quantity, 1, Math.Max(1, part.StackLimit));

            var head = new ExportedItem { StrName = lo.DefName, FX = fx, FY = fy, FRotation = rot, StrID = headId };
            // A name the user gave this deck item, on the head alone: the stack's extra copies below are members of
            // it, and the game keeps the name on the head's CO (see Rename and LooseObject.CustomName).
            if (lo.CustomName is { } looseName) head.AGPMSettings = [RenameGpm(looseName)];
            // A condition the user painted, likewise on the head alone: the pile is worn as a pile, which is where
            // the game keeps it and how the name above lands. The stack members below keep their pristine marker,
            // so a worn stack reads by its head exactly as a renamed one does.
            if (lo.Condition is { } looseCond && Paint.CanWearLoose(part)
                && part.StartingCondValues.GetValueOrDefault("StatDamageMax") is > 0 and var looseMax)
            {
                var dmg = (1.0 - looseCond) * looseMax;
                if (dmg > 0)
                    head.ACondOverrides =
                        [new ExportedCondOverride { CondName = "StatDamage", Chance = 1.0, Amount = dmg }];
            }
            // A loot spawner's panel is the whole of what it is: without it the game builds one from the def's
            // template defaults (strLoot "Blank") and it spawns nothing at all, which is what every spawner
            // Ostraplan exported did before #55.
            if (lo.Spawner is { } spawner)
            {
                var panel = new ExportedGpmSetting { DictGUIPropMap = [.. spawner.Clamped().ToPanelKeys()] };
                if (spawner.IsPersonSpawn)
                {
                    authoredPSpecs.Add(new ExportedShallowPSpec
                    {
                        StrName = lo.DefName, FX = fx, FY = fy, FRotation = rot, StrID = headId,
                        AGPMSettings = [panel],
                    });
                    continue;   // it leaves by aShallowPSpecs, so it is not an aItems entry and has no stack
                }
                head.AGPMSettings = [.. head.AGPMSettings ?? [], panel];
            }

            items.Add(head);

            if (qty > 1)
            {
                var memberIds = new List<string>(qty - 1);
                for (var i = 1; i < qty; i++)
                {
                    var mid = Guid.NewGuid().ToString();
                    items.Add(new ExportedItem
                    {
                        StrName = lo.DefName, FX = fx, FY = fy, FRotation = rot, StrID = mid,
                        StrParentID = headId, ACondOverrides = PristineMarker, BForceLoad = true,
                    });
                    memberIds.Add(mid);
                }
                var stackCo = ExportedCondOwnerSave.For(lo.DefName, headId, catalog);
                stackCo.AStack = memberIds.ToArray();
                cos.Add(stackCo);
            }
            // What a deck item holds. A top-level item in a TEMPLATE normally carries no CO, so the game spawns it
            // with bLoot: true and fills it from its own def — which is right, and why an untouched backpack is
            // written as a bare item here. The moment the user has actually put something in it that has to stop,
            // or the def's loot and the authored contents both arrive and fight over the same slots. Baking the
            // head's CO is what stops it: GetCondOwner takes the dictCOSaves branch and recurses with bLoot: false.
            //
            // The test is deliberately "holds something that is not its own pockets". Pockets alone are exactly
            // what the loot would have produced, so leaving those to the game keeps the file smaller and the
            // behaviour identical.
            else if (lo.Cargo.Any(c => !c.Intrinsic || c.Children.Count > 0))
            {
                cos.Add(ExportedCondOwnerSave.For(lo.DefName, headId, catalog));
                EmitCargo(lo.Cargo, headId, fx, fy);
            }
        }

        // The game's roomValue is the room's PARTS value (Room.CalculateRoomValue = Σ GetBasePrice × modifier),
        // which GetShipValue sums on a shallow load. Bake that, not the physical volume — a volume figure (~0.256
        // per tile) made a spawned design read as near-worthless at a broker until the game recomputed on full load.
        var valueModifiers = specs.ToDictionary(s => s.Name, s => s.ValueModifier, StringComparer.Ordinal);
        var rooms = partition.Rooms.Select(r => new ExportedRoom
        {
            StrID = Guid.NewGuid().ToString(),
            BVoid = r.Void,
            ATiles = r.Tiles.ToArray(),
            RoomSpec = r.RoomSpec,
            RoomValue = ShipValue.RoomValueOf(r, valueModifiers, catalog),
        }).ToArray();

        var roomCount = partition.Rooms.Count(r => r.RoomSpec is not ("" or "Blank"));

        var regId = GenerateRegID();
        var zones = BuildZones(doc, grid, regId);

        // publicName is written verbatim: the caller has already resolved the display-name policy via
        // ResolvePublicName. Build is a mechanical writer, and handed nothing at all it writes the sentinel that
        // asks the game to name the ship. It must never fall back to shipName: on a mod export that is the design's
        // file name and on a save grant it is the registration, and both have shipped as a ship's visible name.
        var publicName = meta?.PublicName is { Length: > 0 } pn ? pn : VariedNames;

        // Bake the installed docking ports + primary. The game rebuilds these from items only on a Full/Edit load
        // (Ship load clears aDockingPorts then re-registers via AddCO); a SHALLOW-loaded spawn reads them straight
        // from the file and never rebuilds. Vendor stock (Trader.AddNewShips), the Special Offer
        // (GUIShipBroker.AddSpecialOfferShip), and the shallow-station dock branch of OnPurchaseConfirm all spawn/
        // dock the ship Shallow — so omitting these left a purchased ship exposing zero open ports, and the game
        // could not mate it to the station and stranded it at its objSS instead of docking.
        string[]? aDockingPorts = null;
        string? primaryPortId = null;
        if (docksysPorts.Count > 0)
        {
            // Mirror the game's registration order: non-TypeB (primary) ports first, TypeB ports last; the primary
            // is a primary-class airlock when present (by conditions, so any state variant counts), else the first
            // non-TypeB port.
            var ordered = docksysPorts.Where(p => !p.TypeB).Concat(docksysPorts.Where(p => p.TypeB)).ToList();
            primaryPortId = docksysPorts.FirstOrDefault(p => p.PrimaryDef).Id ?? ordered[0].Id;
            aDockingPorts = ordered.Select(p => p.Id).OrderBy(id => id == primaryPortId ? 0 : 1).ToArray();
        }

        // Boarding / crew-spawn points (aShallowPSpecs): the game materialises a person ARRIVING at the ship
        // (the P.A.S.S. ferry, a skywalk) at the "Boarding" spawner and an NPC already assigned to the ship at
        // the "NotBoarding" one. Every core crewable ship carries these as SysLootSpawner objects, but Ostraplan
        // drops all IsSystem objects on import and never modeled aShallowPSpecs — so exported/purchased ships had
        // none and dumped PASS arrivals at a fallback tile (the map origin, frequently outside the hull). Anchor
        // the Boarding point at the interior tile nearest the primary airlock (the dock entry, where an arrival
        // should appear); with no docking port fall back to the interior centroid.
        var primaryPort = docksysPorts.FirstOrDefault(p => p.PrimaryDef);
        var airlockAnchor = docksysPorts.Count == 0 ? -1
            : primaryPort.Id is not null ? primaryPort.Anchor : docksysPorts[0].Anchor;
        // Authored beats synthesised, per role. The pair below exists because a ship with no boarding point dumps
        // an arrival at the map origin, often outside the hull, so it is a fallback rather than a fixture: a
        // design that says where its own arrivals appear should be obeyed. A design that authors only one of the
        // two roles still gets the other synthesised, which is why this is per role and not all-or-nothing.
        var authoredRoles = authoredPSpecs
            .Select(RoleOf)
            .Where(r => r is not null)
            .ToHashSet(StringComparer.Ordinal);
        var shallowPSpecs = BuildBoardingSpawners(grid, partition, airlockAnchor)
            .Where(p => RoleOf(p) is not { } role || !authoredRoles.Contains(role))
            .Concat(authoredPSpecs)
            .ToArray();

        // The shallow-state block the game writes on save (Ship.GetJSON) and reads straight back on a SHALLOW
        // spawn (Ship.InitShip). Every core template carries it; leaving it at zero is not neutral:
        //   * fShallowMass is what Ship.Mass returns until the ship is fully loaded, so a zero mass gives the
        //     flight model a divide-by-zero and shows "Mass: 0 (kg)" on the chargen/broker spec sheet;
        //   * fRCSCount/nRCSDistroCount together GATE RCS flight (Ship.Maneuver bails outright when either is
        //     zero), so a shallow AI or vendor-docked copy could not manoeuvre at all;
        //   * the torch block is what "Torch Drive: Yes/No" reads.
        // Every figure here is the one the game's own accessor would produce, mapped in Propulsion.
        var prop = Propulsion.Estimate(doc, grid, catalog);

        var ship = new ExportedShip
        {
            StrName = shipName,
            StrRegID = regId,
            PublicName = publicName,
            Make = meta?.Make ?? "",
            Model = meta?.Model ?? "",
            Year = meta?.Year ?? "",
            Designation = meta?.Designation ?? "",
            Description = meta?.Description ?? "",
            NCols = grid.NCols,
            NRows = grid.NRows,
            VShipPos = new ExportedVec2(),   // (0,0): the anchor the coordinate inverse assumes
            AItems = items.ToArray(),
            ADockingPorts = aDockingPorts,
            StrPrimaryDockingPortID = primaryPortId,
            ACOs = cos.Count > 0 ? cos.ToArray() : null,   // save-style CO data for authored cargo; omitted when none
            ARooms = rooms,
            AZones = zones,
            AShallowPSpecs = shallowPSpecs.Length > 0 ? shallowPSpecs : null,
            // The shallow-load broker value is Σ roomValue ×3 iff nO2PumpCount > 0 (Ship.GetShipValue);
            // the game only re-derives the count from the items on an Edit/Full load, so bake the real
            // figure (installed pumps fed by an O2-charged RTA can at their GasInput tile) or a spawned
            // design with a working O2 supply under-quotes at the broker until first fully loaded.
            NO2PumpCount = ShipValue.CountO2Pumps(grid, catalog),
            // objSS at exact (0,0) around "Sol" is Sol's own coordinate origin, not a neutral placeholder: the
            // loot-spawn path (kiosk/Special-Offer/starting-ship) does NOT reposition it like template import does,
            // so a literal (0,0) spawns the ship inside the star. Every core template instead carries small nonzero
            // leftover save-state coordinates (e.g. SalvageCustom2.json: -0.2178, -0.3177) and never exhibits this.
            ObjSS = new ExportedSitu { VPosx = -0.25, VPosy = -0.35 },
            ARating = [rating.Epoch.Length == 0 ? "0" : rating.Epoch,
                rating.Condition, rating.RoomCount, rating.Maneuver, rating.Size, rating.Slot5],
            // InvariantCulture: the game parses/prints '.' decimals (a comma-decimal locale would emit "3,20m")
            Dimensions = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{grid.NCols * MetresPerTile:0.00}m x {grid.NRows * MetresPerTile:0.00}m"),
            ShipCO = ExportedShipCO.Pristine(),
            // fShallowMass = Ship.Mass: the StatMass walk over placed parts plus the loose items on the deck.
            // ExtraMassKg is deliberately excluded — it is the design's planning haul figure, not ship mass.
            FShallowMass = prop.PartsMass + prop.LooseMass,
            FShallowRCSRemass = prop.RcsReactionMass,        // Ship.GetRCSRemain
            FShallowRCSRemassMax = prop.RcsReactionMassMax,  // Ship.GetRCSMax
            NRCSCount = prop.RcsThrust,                      // Ship.fRCSCount (Σ StatThrustStrength, not a headcount)
            NRCSDistroCount = prop.RcsDistrosPresent,        // counted on install, independent of power state
            // A template's reactor is unlit, but every core torch ship still ships bFusionTorch true with its
            // thrust/pellet figures baked: shallow, the block IS the ship's torch capability, and FusionIC
            // recomputes all three the moment the ship loads far enough to run.
            BFusionTorch = prop.HasTorchFigures,
            FFusionThrustMax = prop.HasTorchFigures ? prop.TorchThrustNewtons : 0,
            FFusionPelletMax = prop.PelletMax,
            FShallowFusionRemain = prop.ReactantSeconds,
        };

        return (ship, rating, roomCount);
    }

    /// <summary>Serialize the ship as the game expects a <c>data/ships</c> file: a one-element top-level array.</summary>
    public static string Serialize(ExportedShip ship) => JsonSerializer.Serialize(new[] { ship }, Json);

    /// <summary>Serialize several ships as one <c>data/ships</c> file. The game reads a data file as an array and
    /// keys each element by its own <c>strName</c>, so a mod carrying a fleet is one file listing them, not one
    /// file each. See <see cref="BundleExport"/>.</summary>
    public static string Serialize(IReadOnlyList<ExportedShip> ships) => JsonSerializer.Serialize(ships, Json);

    /// <summary>
    /// The built ship as a single mutable node, for a caller that has to keep editing it — <see cref="SaveGrant"/>,
    /// which turns this template-shaped record into the save-shaped one a save game needs. Goes through the same
    /// <see cref="Json"/> options as <see cref="Serialize"/> (notably <c>WhenWritingNull</c>), so a field the DTO
    /// leaves null is omitted here exactly as it would be on disk, rather than materialising as a JSON null the
    /// game would then read as a real value.
    /// </summary>
    internal static JsonObject ToJsonObject(ExportedShip ship) =>
        JsonNode.Parse(JsonSerializer.Serialize(ship, Json))!.AsObject();

    /// <summary>Serialize the mod metadata as the game expects <c>mod_info.json</c>: a one-element top-level
    /// array, the same shape as every core data file. A bare object parses to an empty collection, so the
    /// loader (<c>DataHandler.JsonToData</c>) falls back to a default name and logs a spurious
    /// "Missing mod_info.json" warning plus an "Error loading file" for the mod.</summary>
    public static string SerializeModInfo(ModInfo modInfo) => JsonSerializer.Serialize(new[] { modInfo }, Json);

    /// <summary>
    /// Build the design and write a complete mod folder (<c>mod_info.json</c> + <c>data/ships/&lt;Name&gt;.json</c>,
    /// plus <c>images/ships/&lt;Name&gt;/</c> when <see cref="ExportOptions.Preview"/> is supplied)
    /// under <see cref="ExportOptions.DestinationParent"/>. Overwrites an existing same-named mod folder's data
    /// files and that one image folder (never deletes anything else). When <see cref="ExportOptions.Delivery"/> asks for kiosk/Special-Offer/
    /// starting-ship availability, also writes the loot/lifeevent/interaction files — which needs
    /// <paramref name="index"/> to clone the current effective loot pools. Returns where it landed; throws on I/O
    /// failure for the caller to report.
    /// </summary>
    public static ExportResult Write(
        ShipDocument doc, Catalog catalog, IReadOnlyList<RoomSpecDef> specs, ExportOptions opts, DataIndex? index = null)
    {
        // One ship is a bundle of one. The merge, the sweep and the staged write are all the same work whether the
        // mod holds one design or ten, and keeping a second copy of them here is how the two would drift.
        var ship = new BundleShip(
            doc, opts.ShipName,
            new ExportMetadata(opts.PublicName, opts.Make, opts.Model, opts.Year, opts.Designation, opts.Description),
            opts.Wear, opts.Delivery, opts.ReplaceTarget, opts.Preview);

        var bundle = new BundleOptions(
            ResolveModName(opts.ModName, opts.ShipName, opts.ReplaceTarget),
            opts.Author, opts.Notes, opts.ModVersion, opts.GameVersion, opts.DestinationParent, [ship],
            ExclusiveStart: opts.Delivery?.StartingShipExclusive ?? false);

        var result = BundleExport.Write(catalog, specs, bundle, index);
        var only = result.Ships[0];

        return new ExportResult(
            result.ModDir, result.ShipJsonPath, result.ModInfoPath, only.PartCount, only.RoomCount, only.Rating,
            result.Warnings, result.TouchedLootPools, only.PreviewCount);
    }

    /// <summary>
    /// Resolve the ship's in-game <c>publicName</c> from the user's input. A real typed name (not blank, not the
    /// literal <see cref="VariedNames"/> sentinel) is always honoured; anything else falls through to
    /// <paramref name="whenBlank"/>, which is the caller's answer to "what does leaving it blank mean here".
    ///
    /// <para>A ship the <b>game</b> hands out, which is a mod export whether or not it replaces an existing
    /// ship, passes <see cref="VariedNames"/>: that is what every one of the 220 core templates carries, and a
    /// design's own name is a file name rather than a ship's. A ship written straight into a <b>save</b> as one
    /// you already own passes the design name instead, since it has to be findable as the thing you
    /// designed.</para>
    /// </summary>
    public static string ResolvePublicName(string? custom, string whenBlank) =>
        custom is { Length: > 0 } c && c.Trim() is { Length: > 0 } t && t != VariedNames ? t : whenBlank;

    /// <summary>
    /// Resolve the mod's name (its <c>mod_info.json strName</c> + folder), which is separate from the ship. A name
    /// the user typed is honoured; otherwise a <b>replacement</b> defaults to <c>"{replaceTarget} - Replaced via
    /// Ostraplan"</c> — so the mod reads distinctly from the ship it overrides, rather than colliding with the
    /// replaced ship's own name — while a <b>new</b> ship's mod takes the ship name.
    /// </summary>
    public static string ResolveModName(string? modName, string shipName, string? replaceTarget) =>
        modName is { Length: > 0 } m && m.Trim() is { Length: > 0 } t ? t
        : replaceTarget is { Length: > 0 } r && r.Trim() is { Length: > 0 } target ? $"{target} - Replaced via Ostraplan"
        : shipName;

    /// <summary>
    /// Bake the design's device signal connections onto the exported items. For each resolved link (source drives
    /// target), the source's <c>Electrical</c> GPM gains the target in <c>outputConnections</c> and the target gains
    /// the source in <c>inputConnections</c> — the exact shape a core template carries (see GAME-INTERNALS "Device
    /// signal connections"). Connections are by item <c>strID</c> with no geometry, so this is a pure id rewrite.
    /// </summary>
    private static void WireDeviceLinks(
        ShipDocument doc, Dictionary<string, string> exportIdByPlacementId, Dictionary<string, ExportedItem> itemByExportId)
    {
        var outputs = new Dictionary<string, List<string>>();   // source export id → target export ids it drives
        var inputs = new Dictionary<string, List<string>>();    // target export id → source export ids driving it
        var live = new Dictionary<string, bool>();              // export id → whether its def is a powered state

        static void Add(Dictionary<string, List<string>> map, string key, string value)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            list.Add(value);
        }

        foreach (var (_, source, target) in DeviceLinks.Resolved(doc))
        {
            if (!exportIdByPlacementId.TryGetValue(source.Id.ToString(), out var sId)
                || !exportIdByPlacementId.TryGetValue(target.Id.ToString(), out var tId))
                continue;
            Add(outputs, sId, tId);
            Add(inputs, tId, sId);
            // A device whose def is an Off state loads with its breaker status false, exactly as the stock ships
            // write it (77 of their 274 wired items). Anything else loads live.
            live[sId] = doc.Part(source) is not { } sDef || !sDef.StartingConds.Contains("IsOff");
            live[tId] = doc.Part(target) is not { } tDef || !tDef.StartingConds.Contains("IsOff");
        }

        foreach (var id in outputs.Keys.Union(inputs.Keys))
            if (itemByExportId.TryGetValue(id, out var item))
                // Append rather than assign: an item can carry several panels, and a renamed device already has
                // its Rename one (the stock Babak Refit carries exactly this pair). Assigning dropped the name.
                item.AGPMSettings =
                [
                    .. item.AGPMSettings ?? [],
                    ElectricalGpm(
                        inputs.GetValueOrDefault(id) ?? [],
                        outputs.GetValueOrDefault(id) ?? [],
                        live.GetValueOrDefault(id, true)),
                ];
    }

    /// <summary>
    /// Bake the design's <b>sensor</b> connections and device panel settings. Unlike the breaker graph this
    /// writes nothing to <c>Electrical</c>: a device follows a sensor through a single <c>strInput01</c> key on
    /// its <b>own</b> control panel, which is what <c>GasPump.UpdateRemote</c> and <c>Heater.UpdateRemote</c> read
    /// each time they wake. Without it a pump, contaminant scrubber, heater or cooler tests itself for a condition
    /// only a tripped alarm carries and so never runs.
    ///
    /// <para>Only the <b>authored</b> keys are written. The rest of the panel (its prefab, title, valid-source
    /// trigger, monitored cond, heat points) is template data the game itself materialises from the def on spawn
    /// — <c>CondOwner.SetData</c> copies every declared panel out of <c>data/guipropmaps</c> before
    /// <c>Ship.CreatePart</c> merges the item's own panels over it <b>per key</b>. So a partial panel is not just
    /// safe, it is better: baking a copy of a game template into every exported ship would go stale the first
    /// time the game or a mod changed one.</para>
    ///
    /// <para>A mode key is written only where the def declares the matching cond, which is the gate the game's own
    /// panel applies. It is not cosmetic: <c>GasPump.UpdateRemote</c> grants <c>IsTurboOn</c> from <c>bTurbo</c>
    /// unconditionally, while the rate multiplier it reads off <c>IsTurbo</c> is zero on a def that does not
    /// declare it, so an ungated turbo flag would stop the pump.</para>
    /// </summary>
    private static void WireSensorLinks(
        ShipDocument doc, Catalog catalog, Dictionary<string, string> exportIdByPlacementId,
        Dictionary<string, ExportedItem> itemByExportId, Dictionary<string, Placement> byPlacementId)
    {
        var sensorByTarget = new Dictionary<Guid, string>();   // target placement id → source export id
        foreach (var (_, source, target) in SensorLinks.Resolved(doc))
            if (exportIdByPlacementId.TryGetValue(source.Id.ToString(), out var sourceExportId))
                sensorByTarget[target.Id] = sourceExportId;   // one sensor per device; a later link wins

        foreach (var placement in byPlacementId.Values)
        {
            if (catalog.Lookup(placement.DefName) is not { } part) continue;

            // A fusion core's own panel, on the same terms as the device one below: only where the designer set
            // something, since the def's template already reads as a cold core. FusionIC reads these keys off the
            // condition owner every tick, so this is what decides whether the ship spawns with its core lit.
            if (DevicePanels.ReactorPanel(catalog, part) is { } reactorPanel
                && placement.Reactor is { } reactor && !reactor.IsDefault
                && exportIdByPlacementId.TryGetValue(placement.Id.ToString(), out var reactorId)
                && itemByExportId.TryGetValue(reactorId, out var reactorItem))
                reactorItem.AGPMSettings =
                [
                    .. reactorItem.AGPMSettings ?? [],
                    new ExportedGpmSetting { StrName = reactorPanel.Instance, DictGUIPropMap = [.. reactor.ToPanelKeys()] },
                ];

            if (DevicePanels.SensorPanel(catalog, part) is not { } panel) continue;

            var settings = (placement.Device ?? DeviceSettings.Default).ClampTo(part);
            var sensor = sensorByTarget.GetValueOrDefault(placement.Id);
            // Nothing to say about a device left wholly alone: the def's own panel already reads "no sensor, bus
            // on auto, no modes", so writing that back would be noise in every exported ship.
            if (sensor is null && settings.IsDefault) continue;
            if (!exportIdByPlacementId.TryGetValue(placement.Id.ToString(), out var exportId)) continue;
            if (!itemByExportId.TryGetValue(exportId, out var item)) continue;

            var keys = new List<object?>();
            if (sensor is not null) { keys.Add(DevicePanel.SensorInputKey); keys.Add(sensor); }
            keys.Add("nKnobBus");
            keys.Add(((int)settings.Bus).ToString());
            if (DeviceSettings.Applicable(part, DevicePanels.TurboCond)) { keys.Add("bTurbo"); keys.Add(Bool(settings.Turbo)); }
            if (DeviceSettings.Applicable(part, DevicePanels.ReverseCond)) { keys.Add("bReverse"); keys.Add(Bool(settings.Reverse)); }
            if (DeviceSettings.Applicable(part, DevicePanels.SlowCond)) { keys.Add("bSlowMode"); keys.Add(Bool(settings.Slow)); }

            item.AGPMSettings =
            [
                .. item.AGPMSettings ?? [],
                new ExportedGpmSetting { StrName = panel.Instance, DictGUIPropMap = [.. keys] },
            ];
        }

        static string Bool(bool value) => value ? "true" : "false";
    }

    /// <summary>The <c>Rename</c> GPM panel for a part the user named (see <see cref="Rename"/>).</summary>
    private static ExportedGpmSetting RenameGpm(string name) => new()
    {
        StrName = Rename.Panel,
        DictGUIPropMap = [Rename.NameKey, name],
    };

    /// <summary>Build a nav console's <c>NavModConfig</c> panel: each module's key against its screen anchor rect,
    /// or <c>""</c> for one that waits in the edit-menu tray (see <see cref="NavConsole.ConfigEntries"/>).</summary>
    private static ExportedGpmSetting NavConfigGpm(IReadOnlyList<(string Key, string Value)> entries) => new()
    {
        StrName = "NavModConfig",
        DictGUIPropMap = [.. entries.SelectMany(e => new object?[] { e.Key, e.Value })],
    };

    /// <summary>
    /// Build the <c>Electrical</c> GPM panel for a wired item — the canonical eight keys of the game's own
    /// <c>Electrical</c> prop map, in its order. A connection entry is
    /// <c>&lt;strID&gt;#&lt;signalType&gt;#&lt;switchStatus&gt;#&lt;nickName&gt;</c>.
    ///
    /// <para><b>The signal type differs by side, and getting it wrong stops the driven device.</b> An output entry
    /// carries <c>None</c> (0) and an input entry carries <c>On</c> (2) — the stock ships are unanimous, 203 of 203
    /// outputs at 0 and every one of their inputs at 1 (Off) or 2 (On). The reason is in
    /// <c>Electrical.ResolveSignalQueue</c>: it counts inputs whose type is <c>On</c>, and under the default
    /// <c>OR</c> gate a device with a connection that is not <c>On</c> resolves false, raises <c>IsSignalOff</c>
    /// and is shut down by <c>Powered.Run</c>. Ostraplan wrote 0 on both sides, so every wire it exported held its
    /// own target off. A source cannot rescue it either: a device propagates only when its gate result
    /// <i>changes</i>, and one with no inputs of its own never changes.</para>
    ///
    /// <para>The three keys this used to invent — <c>inputIDs</c>, <c>outputIDs</c>, <c>positives</c> — are gone.
    /// <c>Electrical</c> neither reads nor writes them (they survive in three legacy stock ships and nowhere
    /// else), and <c>positives</c> was actively misleading, carrying the input count where stock carries 0.
    /// <c>sendQueue</c>, <c>override</c> and <c>delay</c> are now written, at the values every stock template
    /// uses.</para>
    /// </summary>
    /// <param name="status">Whether the device loads live. False for an Off-state def, which is how the stock
    /// ships write it; a false status raises <c>IsSignalOff</c> at load, exactly as intended for something the
    /// design says is switched off.</param>
    private static ExportedGpmSetting ElectricalGpm(List<string> inputIds, List<string> outputIds, bool status)
    {
        // SignalType: None = 0, Off = 1, On = 2 (Ostranauts.Electrical.SignalType).
        static string Join(List<string> ids, int signalType) =>
            string.Join(",", ids.Select(id => $"{id}#{signalType}#true#"));

        return new ExportedGpmSetting
        {
            StrName = "Electrical",
            DictGUIPropMap =
            [
                "status", status ? "true" : "false",
                "inputConnections", Join(inputIds, 2),    // On — anything else holds this device shut down
                "outputConnections", Join(outputIds, 0),  // None — the driving side carries no signal of its own
                "signalQueue", "",
                "sendQueue", "",
                "override", "true",
                "delay", "0.0",
                "gate", "0",                              // GateMode.OR
            ],
        };
    }

    /// <summary>
    /// Project the document's zones into the export grid frame (which the parts used: <c>vShipPos=(0,0)</c>,
    /// origin = the grid's document-coord origin). Only in-range flat indices are emitted — one out-of-range
    /// index would make the game drop that zone and every zone after it — so a zone whose tiles all fall outside
    /// the exported hull is skipped (it would be inert). Names are made unique (<c>mapZones</c> is name-keyed).
    /// </summary>
    private static ExportedZone[] BuildZones(ShipDocument doc, ShipGrid grid, string regId)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ExportedZone>(doc.Zones.Count);
        foreach (var z in doc.Zones)
        {
            var tiles = new List<int>(z.Tiles.Count);
            foreach (var (dx, dy) in z.Tiles)
            {
                var idx = ZoneGeometry.DocToIndex(dx, dy, (int)grid.VShipPosX, (int)grid.VShipPosY, grid.NCols, grid.NRows);
                if (idx >= 0) tiles.Add(idx);
            }
            if (tiles.Count == 0) continue;
            tiles.Sort();
            result.Add(new ExportedZone
            {
                StrName = UniqueName(z.Name, used),
                StrRegID = regId,
                BTriggerOnOwner = z.TriggerOnOwner,
                ATiles = tiles.ToArray(),
                ATileConds = z.TileConds.ToArray(),
                CategoryConds = z.CategoryConds.Count > 0 ? z.CategoryConds.ToArray() : null,
                StrPersonSpec = z.PersonSpec,
                StrTargetPSpec = z.TargetPSpec,
                ZoneColor = new ExportedColor { R = z.Color.R, G = z.Color.G, B = z.Color.B, A = z.Color.A },
            });
        }
        return result.ToArray();
    }

    /// <summary>
    /// Build the ship's boarding / crew-spawn points as <c>aShallowPSpecs</c> entries — the SysLootSpawner
    /// objects every core crewable ship carries. The game places a person ARRIVING at the ship (the P.A.S.S.
    /// ferry, a skywalk) at the "Boarding" spawner and an NPC already assigned to the ship at the "NotBoarding"
    /// one; without them the arrival lands at a fallback tile (the map origin, often outside the hull). Boarding
    /// sits on the interior tile nearest the primary airlock (<paramref name="airlockAnchor"/>, a grid tile
    /// index, or -1 when the design has no docking port); NotBoarding sits deep inside (nearest the interior
    /// centroid). Returns an empty array when the design has no pressurized interior (nothing to anchor to) —
    /// they collapse onto the same tile when the interior is a single cell, which the game handles fine.
    /// </summary>
    private static ExportedShallowPSpec[] BuildBoardingSpawners(ShipGrid grid, RoomPartition partition, int airlockAnchor)
    {
        var interior = partition.Rooms.Where(r => !r.Void).SelectMany(r => r.Tiles).ToList();
        if (interior.Count == 0) return [];

        var boarding = airlockAnchor >= 0
            ? interior.OrderBy(t => Dist2(grid, t, airlockAnchor)).First()
            : NearestToCentroid(grid, interior);
        var notBoarding = NearestToCentroid(grid, interior);

        return
        [
            PspecSpawner("Boarding", grid.Col(boarding), grid.Row(boarding)),
            PspecSpawner("NotBoarding", grid.Col(notBoarding), grid.Row(notBoarding)),
        ];
    }

    /// <summary>Squared tile distance between two grid tile indices.</summary>
    private static int Dist2(ShipGrid grid, int a, int b)
    {
        int dx = grid.Col(a) - grid.Col(b), dy = grid.Row(a) - grid.Row(b);
        return dx * dx + dy * dy;
    }

    /// <summary>The tile nearest the centroid of the given tiles (the "deepest inside" pick).</summary>
    private static int NearestToCentroid(ShipGrid grid, IReadOnlyList<int> tiles)
    {
        double cx = tiles.Average(t => grid.Col(t)), cy = tiles.Average(t => grid.Row(t));
        return tiles.OrderBy(t =>
        {
            double dx = grid.Col(t) - cx, dy = grid.Row(t) - cy;
            return dx * dx + dy * dy;
        }).First();
    }

    /// <summary>The <c>strLoot</c> a person-spawn entry names, which for the two synthesised ones is the role
    /// ("Boarding" / "NotBoarding"). Null when the entry carries no readable panel, which no entry this code
    /// builds ever does, but an authored one round-tripped from a mod could.</summary>
    private static string? RoleOf(ExportedShallowPSpec spec)
    {
        foreach (var panel in spec.AGPMSettings)
        {
            var flat = panel.DictGUIPropMap;
            for (var i = 0; i + 1 < flat.Length; i += 2)
                if (flat[i] as string == "strLoot") return flat[i + 1] as string;
        }
        return null;
    }

    /// <summary>A person-spawn <c>SysLootSpawner</c> (an <c>aShallowPSpecs</c> entry) for the "Boarding" /
    /// "NotBoarding" role at a 1×1 tile — the exact <c>aGPMSettings</c> shape a core ship carries (verified
    /// against Squall.json). A 1×1 object's stored centre equals its tile, so <c>fX = col</c> and <c>fY = -row</c>
    /// (the parts' y-down grid → y-up world flip), the same inverse the placed parts use with <c>vShipPos=(0,0)</c>.</summary>
    private static ExportedShallowPSpec PspecSpawner(string role, int col, int row) => new()
    {
        FX = col,
        FY = -row,
        StrID = Guid.NewGuid().ToString(),
        AGPMSettings =
        [
            new ExportedGpmSetting
            {
                DictGUIPropMap =
                [
                    "strGUIPrefab", "GUILootSpawn",
                    "strFriendlyName", "Loot Spawner",
                    "strGUIPrefabRight", null,
                    "strGUIPrefabLeft", null,
                    "strGUIPrefabUp", null,
                    "strGUIPrefabDown", null,
                    "strType", "Pspec",
                    "strLoot", role,
                    "strRange", "1",
                    "strCount", "0",
                    "strNew", "True",
                    "strDamaged", "True",
                    "strDerelict", "True",
                ],
            },
        ],
    };

    /// <summary>A per-ship-unique zone name: the given name, else "zone", suffixed " 2", " 3"… on a clash.</summary>
    private static string UniqueName(string name, HashSet<string> used)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "zone" : name.Trim();
        var candidate = baseName;
        for (var n = 2; !used.Add(candidate); n++) candidate = $"{baseName} {n}";
        return candidate;
    }

    /// <summary>True when a placed part is an installed docking port the game registers into
    /// <c>Ship.aDockingPorts</c> — it triggers <c>TIsDockSysInstalled</c>, the same predicate
    /// <see cref="ProblemScan.IsDocksys"/> uses for the "no docking port" design check.</summary>
    private static bool IsDocksysPart(ResolvedPart part, Catalog catalog) =>
        catalog.Triggers.TryGetValue(ProblemScan.DocksysTrigger, out var ct)
        && ct.Reqs.Length > 0
        && ct.Reqs.All(part.Has);

    /// <summary>A plausible RegID (letter-prefixed, non-empty — the game indexes <c>strRegID[0]</c> and
    /// regenerates it on spawn anyway). Uppercase, GUID-derived so distinct per export.</summary>
    private static string GenerateRegID() => "H-" + Guid.NewGuid().ToString("N")[..3].ToUpperInvariant();

    /// <summary>A file/folder-safe form of the ship name (invalid path chars → '_'; never empty).</summary>
    public static string SanitizeName(string name)
    {
        var cleaned = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        return cleaned.Length == 0 ? "OstraplanShip" : cleaned;
    }
}

// ---- the JsonShip-shaped export DTOs (a well-formed subset: the fields present on every core
// template, plus aRating). Newtonsoft (the game's serializer) defaults anything omitted and
// ignores anything extra, so this loads cleanly; pristine values sit where the game recomputes. ----

/// <summary>A <c>data/ships</c> ship object. Field names/casing match the game's JsonShip exactly.</summary>
public sealed class ExportedShip
{
    [JsonPropertyName("strName")] public string StrName { get; set; } = "";
    [JsonPropertyName("strRegID")] public string StrRegID { get; set; } = "";
    [JsonPropertyName("nCurrentWaypoint")] public int NCurrentWaypoint { get; set; } = -1;
    [JsonPropertyName("fTimeEngaged")] public double FTimeEngaged { get; set; }
    [JsonPropertyName("fWearManeuver")] public double FWearManeuver { get; set; }
    [JsonPropertyName("fWearAccrued")] public double FWearAccrued { get; set; }
    [JsonPropertyName("shipCO")] public ExportedShipCO ShipCO { get; set; } = new();
    [JsonPropertyName("aItems")] public ExportedItem[] AItems { get; set; } = [];

    /// <summary>Save-style CO records for authored contained cargo (see <c>EmitContained</c>); a template needs
    /// these so the game keeps the cargo and rebuilds stacks (<c>aStack</c>). Null — and omitted — when the design
    /// has no cargo, matching a core template.</summary>
    [JsonPropertyName("aCOs")] public ExportedCondOwnerSave[]? ACOs { get; set; }
    [JsonPropertyName("vShipPos")] public ExportedVec2 VShipPos { get; set; } = new();
    [JsonPropertyName("objSS")] public ExportedSitu ObjSS { get; set; } = new();
    [JsonPropertyName("aRooms")] public ExportedRoom[] ARooms { get; set; } = [];
    [JsonPropertyName("aZones")] public ExportedZone[] AZones { get; set; } = [];

    /// <summary>The ship's boarding / crew-spawn points (<c>SysLootSpawner</c> objects tagged Boarding /
    /// NotBoarding). The game places a person arriving via the P.A.S.S. ferry or a skywalk at the Boarding
    /// spawner and an already-aboard NPC at the NotBoarding one; core crewable ships all carry these. Null —
    /// and omitted — when the design has no pressurized interior to anchor them to.</summary>
    [JsonPropertyName("aShallowPSpecs")] public ExportedShallowPSpec[]? AShallowPSpecs { get; set; }

    /// <summary>Installed docking-port item strIDs (primary/non-TypeB first, TypeB last). The game rebuilds this
    /// from items on a Full/Edit load, but a <b>Shallow</b>-loaded spawn (vendor stock, Special Offer, and the
    /// shallow-station dock branch in <c>GUIShipBroker.OnPurchaseConfirm</c>) reads it verbatim from the file —
    /// without it a purchased ship exposes no open ports (<c>Ship.GetOpenDockingPorts</c>) and the game strands it
    /// at its <c>objSS</c> instead of docking. Null — and omitted — when the design has no docking port (every
    /// valid export carries the Primary Airlock).</summary>
    [JsonPropertyName("aDockingPorts")] public string[]? ADockingPorts { get; set; }

    /// <summary>The primary docking port's item strID (the Primary Airlock). The game derives this from
    /// <see cref="ADockingPorts"/> when empty, but baking it keeps a Shallow spawn unambiguous. Null — and
    /// omitted — when the design has no docking port.</summary>
    [JsonPropertyName("strPrimaryDockingPortID")] public string? StrPrimaryDockingPortID { get; set; }
    [JsonPropertyName("aRating")] public string[] ARating { get; set; } = [];
    [JsonPropertyName("DMGStatus")] public int DMGStatus { get; set; }
    [JsonPropertyName("fLastVisit")] public double FLastVisit { get; set; }
    [JsonPropertyName("fFirstVisit")] public double FFirstVisit { get; set; }
    [JsonPropertyName("fAIDockingExpire")] public double FAIDockingExpire { get; set; }
    [JsonPropertyName("fAIPauseTimer")] public double FAIPauseTimer { get; set; }
    [JsonPropertyName("bPrefill")] public bool BPrefill { get; set; }
    [JsonPropertyName("bBreakInUsed")] public bool BBreakInUsed { get; set; }
    [JsonPropertyName("bNoCollisions")] public bool BNoCollisions { get; set; }
    [JsonPropertyName("dLastScanTime")] public double DLastScanTime { get; set; }
    [JsonPropertyName("bLocalAuthority")] public bool BLocalAuthority { get; set; }
    [JsonPropertyName("bAIShip")] public bool BAIShip { get; set; }
    [JsonPropertyName("make")] public string Make { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("year")] public string Year { get; set; } = "";
    [JsonPropertyName("origin")] public string Origin { get; set; } = "$TEMPLATE";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("designation")] public string Designation { get; set; } = "";
    [JsonPropertyName("publicName")] public string PublicName { get; set; } = "$TEMPLATE";
    [JsonPropertyName("dimensions")] public string Dimensions { get; set; } = "";
    [JsonPropertyName("fShallowMass")] public double FShallowMass { get; set; }
    [JsonPropertyName("fShallowRCSRemass")] public double FShallowRCSRemass { get; set; }
    [JsonPropertyName("fShallowRCSRemassMax")] public double FShallowRCSRemassMax { get; set; }
    [JsonPropertyName("fShallowFusionRemain")] public double FShallowFusionRemain { get; set; }
    [JsonPropertyName("fFusionThrustMax")] public double FFusionThrustMax { get; set; }
    [JsonPropertyName("fFusionPelletMax")] public double FFusionPelletMax { get; set; }
    [JsonPropertyName("fLastQuotedPrice")] public double FLastQuotedPrice { get; set; }
    [JsonPropertyName("fEpochNextGrav")] public double FEpochNextGrav { get; set; }
    [JsonPropertyName("fBreakInMultiplier")] public double FBreakInMultiplier { get; set; }
    [JsonPropertyName("nRCSCount")] public double NRCSCount { get; set; }
    [JsonPropertyName("fShallowRotorStrength")] public double FShallowRotorStrength { get; set; }
    [JsonPropertyName("nRCSDistroCount")] public int NRCSDistroCount { get; set; }
    [JsonPropertyName("fAeroCoefficient")] public double FAeroCoefficient { get; set; }
    [JsonPropertyName("bFusionTorch")] public bool BFusionTorch { get; set; }
    [JsonPropertyName("bXPDRAntenna")] public bool BXPDRAntenna { get; set; }
    [JsonPropertyName("bShipHidden")] public bool BShipHidden { get; set; }
    [JsonPropertyName("bIsUnderConstruction")] public bool BIsUnderConstruction { get; set; }
    [JsonPropertyName("nO2PumpCount")] public int NO2PumpCount { get; set; }
    [JsonPropertyName("commData")] public ExportedComm CommData { get; set; } = new();
    [JsonPropertyName("ShipType")] public int ShipType { get; set; }
    [JsonPropertyName("nConstructionProgress")] public int NConstructionProgress { get; set; } = 100;
    [JsonPropertyName("nInitConstructionProgress")] public int NInitConstructionProgress { get; set; }
    [JsonPropertyName("nRows")] public int NRows { get; set; }
    [JsonPropertyName("nCols")] public int NCols { get; set; }
    [JsonPropertyName("nGridRotation")] public int NGridRotation { get; set; }
}

/// <summary>One item in the exported ship: a top-level placed part, or a contained sub-object — a nav-console
/// module, or a container's cargo — when <see cref="StrParentID"/>/<see cref="StrSlotParentID"/> is set.</summary>
public sealed class ExportedItem
{
    [JsonPropertyName("strName")] public string StrName { get; set; } = "";
    [JsonPropertyName("fX")] public double FX { get; set; }
    [JsonPropertyName("fY")] public double FY { get; set; }
    [JsonPropertyName("fRotation")] public double FRotation { get; set; }
    [JsonPropertyName("strID")] public string StrID { get; set; } = "";

    /// <summary>Set only on loose contained cargo (and nav-console modules): the <c>strID</c> of the container that
    /// holds it. Null — and omitted from the JSON — for a top-level part.</summary>
    [JsonPropertyName("strParentID")] public string? StrParentID { get; set; }

    /// <summary>Set only on equipped contained gear: the <c>strID</c> of the host it is slotted into. Null — and
    /// omitted from the JSON — otherwise.</summary>
    [JsonPropertyName("strSlotParentID")] public string? StrSlotParentID { get; set; }

    /// <summary>Per-instance condition overrides. Set on contained/slotted items so a template spawn retains them
    /// (<c>Ship.SpawnItems</c> keeps a parented item only when this is non-null) and suppresses the container's
    /// default loot; null — and omitted — on a top-level part, which the loader keeps unconditionally.</summary>
    [JsonPropertyName("aCondOverrides")] public ExportedCondOverride[]? ACondOverrides { get; set; }

    /// <summary>Set on contained/slotted items so the template spawn keeps their <c>strID</c> (instead of assigning
    /// a fresh one), which is what links each item to its baked CO and lets a stack head find its members. Null —
    /// and omitted — on a top-level part.</summary>
    [JsonPropertyName("bForceLoad")] public bool? BForceLoad { get; set; }

    /// <summary>GPM panels baked onto this item — used to write the <c>Electrical</c> signal-connection state on a
    /// wired device (its <c>inputConnections</c>/<c>outputConnections</c>; see <see cref="ShipExport"/> device
    /// links). Null — and omitted — on an unwired part.</summary>
    [JsonPropertyName("aGPMSettings")] public ExportedGpmSetting[]? AGPMSettings { get; set; }
}

/// <summary>One entry in an item's <c>aCondOverrides</c>: a condition set to a fixed value on the spawned instance.
/// Matches the game's <c>JsonCondOverride</c> shape (see any core <c>data/ships</c> file).</summary>
public sealed class ExportedCondOverride
{
    [JsonPropertyName("CondName")] public string CondName { get; set; } = "";
    [JsonPropertyName("Chance")] public double Chance { get; set; } = 1.0;
    [JsonPropertyName("Amount")] public double Amount { get; set; }
    [JsonPropertyName("NegativeValue")] public bool NegativeValue { get; set; }
}

/// <summary>A minimal per-instance CO save record for a piece of authored cargo (the game's <c>JsonCondOwnerSave</c>
/// shape). A <c>data/ships</c> file spawns as a template, which keeps contained items only if they carry save-style
/// CO data; <c>aConds = ["DEFAULT"]</c> tells <c>CondOwner.SetData</c> to repopulate the def's starting conds (a
/// pristine item), and a stack head's <c>aStack</c> (member <c>strID</c>s) is what the game reads to rebuild the
/// ×N stack at the authored count. Mirrors <see cref="SaveEdit"/>'s synthesized COs, including the two rules
/// <see cref="For"/> exists to apply.</summary>
public sealed class ExportedCondOwnerSave
{
    [JsonPropertyName("strID")] public string StrID { get; set; } = "";
    [JsonPropertyName("strCODef")] public string StrCODef { get; set; } = "";
    [JsonPropertyName("bAlive")] public bool BAlive { get; set; } = true;
    [JsonPropertyName("aConds")] public string[] AConds { get; set; } = ["DEFAULT"];

    /// <summary>The cond rules the def declares. <c>SetData</c> reads them from the save block whenever there is
    /// one, so a CO that omits the field loads with none at all — which is what every canister, RTA tank, reactor
    /// core and fire extinguisher written into a container used to get. The marker expands to an empty set for a
    /// def that declares none, so it is always safe.</summary>
    [JsonPropertyName("aCondRules")] public string[] ACondRules { get; set; } = ["DEFAULT"];

    [JsonPropertyName("strCondID")] public string StrCondID { get; set; } = "";
    [JsonPropertyName("strIdleAnim")] public string StrIdleAnim { get; set; } = "Idle";
    [JsonPropertyName("inventoryX")] public int InventoryX { get; set; }
    [JsonPropertyName("inventoryY")] public int InventoryY { get; set; }

    /// <summary>On a stack head only: the member <c>strID</c>s the game re-collects into the stack. Null — and
    /// omitted — for a single item or a real container.</summary>
    [JsonPropertyName("aStack")] public string[]? AStack { get; set; }

    /// <summary>On a slotted item only (a garment's pockets, a console's data chip): the paper-doll slot it sits
    /// in. <c>Ship.SpawnItems</c> re-slots by this name, and <c>Slots.SlotItem</c> refuses a null one, so a
    /// slotted item without it never attaches to its host. Null — and omitted — for loose grid cargo.</summary>
    [JsonPropertyName("strSlotName")] public string? StrSlotName { get; set; }

    /// <summary>The CO for <paramref name="def"/>, with its conds resolved the two ways a save-shaped record has to
    /// carry them. Use this rather than the constructor. A cooverlay skin must have its conds written out in full,
    /// because the <c>DEFAULT</c> marker resolves to the skin's base condowner and the skin's cond-loot deltas can
    /// never be applied to a CO the game loaded from save data (<see cref="PartDef.SavedConds"/>); and every part
    /// needs the conds the game backfills in <c>SetUpBehaviours</c>, which is frozen out on that same path
    /// (<see cref="PartDef.BehaviourConds"/>).</summary>
    public static ExportedCondOwnerSave For(string def, string strID, Catalog catalog)
    {
        var part = catalog.Lookup(def);
        return new ExportedCondOwnerSave
        {
            StrID = strID,
            StrCODef = def,
            StrCondID = def + strID,
            AConds = [.. (part?.SavedConds ?? ["DEFAULT"]).Concat(part?.BehaviourConds ?? [])],
        };
    }
}

/// <summary>A baked room: tile indices (row-major into nCols×nRows), certified spec, void flag.</summary>
public sealed class ExportedRoom
{
    [JsonPropertyName("strID")] public string StrID { get; set; } = "";
    [JsonPropertyName("bVoid")] public bool BVoid { get; set; }
    [JsonPropertyName("aTiles")] public int[] ATiles { get; set; } = [];
    [JsonPropertyName("roomSpec")] public string RoomSpec { get; set; } = "Blank";
    [JsonPropertyName("roomValue")] public double RoomValue { get; set; }
}

/// <summary>A painted zone as the game expects it in <c>aZones</c> (field names/casing match JsonZone).
/// Tiles are flat row-major indices into nCols×nRows; the transient <c>aOldTiles</c> and legacy <c>ranks</c>
/// are intentionally never emitted.</summary>
public sealed class ExportedZone
{
    [JsonPropertyName("strName")] public string StrName { get; set; } = "";
    [JsonPropertyName("strRegID")] public string StrRegID { get; set; } = "";
    [JsonPropertyName("bTriggerOnOwner")] public bool BTriggerOnOwner { get; set; }
    [JsonPropertyName("aTiles")] public int[] ATiles { get; set; } = [];
    [JsonPropertyName("aTileConds")] public string[] ATileConds { get; set; } = [];
    [JsonPropertyName("categoryConds")] public string[]? CategoryConds { get; set; }
    [JsonPropertyName("strPersonSpec")] public string? StrPersonSpec { get; set; }
    [JsonPropertyName("strTargetPSpec")] public string? StrTargetPSpec { get; set; }
    [JsonPropertyName("zoneColor")] public ExportedColor ZoneColor { get; set; } = new();
}

/// <summary>A person-spawn point in <c>aShallowPSpecs</c>: a <c>SysLootSpawner</c> whose <c>aGPMSettings</c>
/// prop map tags it Boarding (where a P.A.S.S. ferry / skywalk arrival appears) or NotBoarding (where an NPC
/// already assigned to the ship spawns). Field names/casing match the game's JsonShip shape. A 1×1 object, so
/// its stored <c>fX/fY</c> is both its centre and its tile.</summary>
public sealed class ExportedShallowPSpec
{
    [JsonPropertyName("strName")] public string StrName { get; set; } = "SysLootSpawner";
    [JsonPropertyName("fX")] public double FX { get; set; }
    [JsonPropertyName("fY")] public double FY { get; set; }
    [JsonPropertyName("fRotation")] public double FRotation { get; set; }
    [JsonPropertyName("strID")] public string StrID { get; set; } = "";
    [JsonPropertyName("aGPMSettings")] public ExportedGpmSetting[] AGPMSettings { get; set; } = [];
}

/// <summary>One GUI-prop-map panel on a spawner. <c>dictGUIPropMap</c> is the game's flat, order-sensitive
/// key/value array with <c>string</c> and <c>null</c> values interleaved, so it is modeled as a mixed
/// <c>object?[]</c> carried through verbatim (System.Text.Json writes each element by its runtime type, and
/// null array elements are always emitted — <c>WhenWritingNull</c> only suppresses null object properties).</summary>
public sealed class ExportedGpmSetting
{
    [JsonPropertyName("strName")] public string StrName { get; set; } = "Panel A";
    [JsonPropertyName("dictGUIPropMap")] public object?[] DictGUIPropMap { get; set; } = [];
}

/// <summary>A zoneColor {r,g,b,a} (components 0..1).</summary>
public sealed class ExportedColor
{
    [JsonPropertyName("r")] public double R { get; set; }
    [JsonPropertyName("g")] public double G { get; set; }
    [JsonPropertyName("b")] public double B { get; set; }
    [JsonPropertyName("a")] public double A { get; set; } = 1;
}

/// <summary>vShipPos / a Vector2 as the game serializes it.</summary>
public sealed class ExportedVec2
{
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
}

/// <summary>The ship's own condition owner: a pristine ShipCO with the standard progress-cap conds.</summary>
public sealed class ExportedShipCO
{
    [JsonPropertyName("strID")] public string StrID { get; set; } = "";
    [JsonPropertyName("strCODef")] public string StrCODef { get; set; } = "ShipCO";
    [JsonPropertyName("bAlive")] public bool BAlive { get; set; } = true;
    [JsonPropertyName("aConds")] public string[] AConds { get; set; } = [];
    [JsonPropertyName("strCondID")] public string StrCondID { get; set; } = "";
    [JsonPropertyName("strIdleAnim")] public string StrIdleAnim { get; set; } = "Idle";
    [JsonPropertyName("strFriendlyName")] public string StrFriendlyName { get; set; } = "ShipCO";

    public static ExportedShipCO Pristine()
    {
        var id = Guid.NewGuid().ToString();
        return new ExportedShipCO
        {
            StrID = "CO-" + id,
            StrCondID = id,
            AConds =
            [
                "StatInstallProgressMax=1.0x1000",
                "StatUninstallProgressMax=1.0x1000",
                "StatRepairProgressMax=1.0x1000",
                "DEFAULT",
            ],
        };
    }
}

/// <summary>The star-system situation (position/velocity). Template <b>import</b> repositions this on load, but the
/// loot-spawn path (kiosk/Special-Offer/starting-ship) does not — a literal (0,0) spawns the ship inside "Sol" (see
/// <see cref="ShipExport.Build"/>), so this must never default to exact zero.</summary>
public sealed class ExportedSitu
{
    [JsonPropertyName("boPORShip")] public string BoPORShip { get; set; } = "Sol";
    [JsonPropertyName("vPosx")] public double VPosx { get; set; }
    [JsonPropertyName("vPosy")] public double VPosy { get; set; }
    [JsonPropertyName("vVelX")] public double VVelX { get; set; }
    [JsonPropertyName("vVelY")] public double VVelY { get; set; }
    [JsonPropertyName("bIsNoFees")] public bool BIsNoFees { get; set; } = true;
    [JsonPropertyName("size")] public int Size { get; set; }
}

/// <summary>Comm/clearance state — empty for a fresh design.</summary>
public sealed class ExportedComm
{
    [JsonPropertyName("strClearanceType")] public string StrClearanceType { get; set; } = "";
}

/// <summary>The mod's <c>mod_info.json</c> (matches the sample/CLAUDE.md fields).</summary>
public sealed class ModInfo
{
    [JsonPropertyName("strName")] public string StrName { get; set; } = "";
    [JsonPropertyName("strAuthor")] public string StrAuthor { get; set; } = "";
    [JsonPropertyName("strModURL")] public string StrModURL { get; set; } = "";
    [JsonPropertyName("strGameVersion")] public string StrGameVersion { get; set; } = "";
    [JsonPropertyName("strModVersion")] public string StrModVersion { get; set; } = "1.0.0";
    [JsonPropertyName("strNotes")] public string StrNotes { get; set; } = "";
}
