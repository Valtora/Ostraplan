namespace Ostraplan.Core;

/// <summary>
/// What a design is. Structurally the two are the same thing (the game stores an apartment as an ordinary ship
/// record and applies the same placement, room and airtightness rules to it), so this decides only two things:
/// which analyses are shown, and which delivery routes the export wizard offers.
/// </summary>
public enum DocumentKind
{
    /// <summary>A vessel. The default, and what every design was before residences were supported.</summary>
    Ship,

    /// <summary>A station residence: no drive, no nav, reached through a transit kiosk rather than flown.
    /// Delivered into a save as a station sub-module under a <c>&lt;STATION&gt;|RES_&lt;n&gt;</c> RegID.</summary>
    Residence,
}

/// <summary>
/// The <see cref="DocumentKind"/> an import starts a design at. A default the user can overrule, never a
/// verdict: the shell exposes the kind alongside the rest of the ship identity.
/// </summary>
public static class DocumentKindGuess
{
    /// <summary>The suffix every stock residence's <c>designation</c> ends in ("Station Residence", "Aerostat
    /// Residence", "Basic Residence", …). Eleven templates on stock 1.0.0.11, all of them.</summary>
    private const string ResidenceDesignationSuffix = "Residence";

    /// <summary>
    /// The kind a design imported from a save should open at. A pipe in the RegID is conclusive rather than a
    /// guess: the game's <c>Ship.InitShip</c> makes any such ship a hidden sub-station, and the only one of
    /// those a player owns is an apartment (GAME-INTERNALS §19). Falls back to the designation otherwise, since
    /// a save's ship carries the designation of the template it spawned from.
    /// </summary>
    public static DocumentKind From(string? regId, string? designation) =>
        SaveZip.IsSubStation(regId) ? DocumentKind.Residence : FromDesignation(designation);

    /// <summary>The kind a design should open at knowing only its <c>designation</c> — the template-import case,
    /// where there is no registration to read.</summary>
    public static DocumentKind FromDesignation(string? designation) =>
        designation is not null
        && designation.TrimEnd().EndsWith(ResidenceDesignationSuffix, StringComparison.OrdinalIgnoreCase)
            ? DocumentKind.Residence
            : DocumentKind.Ship;
}

/// <summary>One placed part. X/Y = top-left tile of the ROTATED footprint; Rot in {0,90,180,270}.</summary>
public sealed class Placement
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string DefName { get; init; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Rot { get; set; }

    /// <summary>
    /// A manual draw-order bias, moving this part up (positive) or down (negative) the stack of things sharing
    /// its tiles. 0 means "wherever the automatic order puts it", which is what almost every part carries; the
    /// Move Back / Move Forward actions write explicit values over a tile's stack (see <see cref="ZOrder"/>).
    /// It is applied <b>inside</b> the part's render layer (see <see cref="Catalog.RenderLayer"/>), so no bias can
    /// push a fixture under a deck plate. Cosmetic: nothing about tile conditions, rooms, airtightness, the rating
    /// or the export reads it. It rides through a move, a rotate and a <see cref="Restate"/> — none of those change
    /// what the thing is sitting in — and is dropped by duplicate/paste along with the rest of the part's identity.
    /// </summary>
    public int ZBias { get; set; }

    /// <summary>
    /// True for structure the user did not author — an imported ship's existing parts. The game applies its
    /// placement law (Item.CheckFit sockets, the airlock envelope) only to <b>new</b> construction and
    /// <b>never re-validates what's already there</b>; so given parts are exempt from the legality scan (a valid
    /// imported ship must flag nothing). It is <b>cleared the moment the part is moved or rotated</b> — relocating
    /// a part IS an authoring act, so the law must re-apply (a device dragged off its wall should flag "needs a
    /// wall alongside"). The legality scan is build-order-aware (see <see cref="ProblemScan"/>), so dragging a
    /// whole intact compartment stays clean without needing immunity; only genuinely exotic stacks moved out of
    /// their construction order may over-flag. Given-ness is set at import (and inherited by a same-class
    /// re-skin/replace, which builds a fresh placement); an unmoved imported part keeps its immunity.
    /// </summary>
    public bool IsGiven { get; set; }

    /// <summary>
    /// The condition the designer painted on this part: 1.0 pristine, 0.0 gone. Null (and omitted from the
    /// <c>.oplan</c>) for a part nobody has painted, which is the great majority and which takes whatever the
    /// export-wide wear setting decides.
    ///
    /// <para><b>This is a deliberate reversal of "a design carries no wear".</b> That line was written for
    /// <see cref="DamageState"/>, where it is still right: a strike is a <i>measurement</i> of a layout and
    /// storing its result would make "fire again" expensive and the document dishonest. Painted condition is the
    /// other thing — an authored property of the design, the same as a container's <see cref="Fill"/> or a nav
    /// console's <see cref="NavLayout"/> — and docs/SCOPE.md already put "a wear level" among what a design
    /// carries into the game. This generalises that one whole-ship number to per part; it does not open a new
    /// category. See docs/SCOPE.md, "Painting condition".</para>
    ///
    /// <para>It reaches the game by the same route the whole-ship wear does, as an <c>aOverrideConds</c>
    /// <c>StatDamage</c> entry on a mod export and as the condition owner's own <c>StatDamage</c> on a save
    /// write, so a painted part is worn in game rather than merely worn here.</para>
    ///
    /// <para>It rides through <see cref="Restate"/> — uninstalling a battered pump does not mend it — and is
    /// dropped by duplicate and paste along with <see cref="Fill"/>, <see cref="ZBias"/> and
    /// <see cref="CustomName"/>, which is the existing convention for everything that is not def, pose or
    /// cargo.</para>
    /// </summary>
    public double? Condition { get; set; }

    /// <summary>
    /// The <c>strID</c> of the save item this part came from, set when the document was imported from a save
    /// <b>for editing</b> (<see cref="SaveEditImport"/>); null for parts the user added. Unlike
    /// <see cref="IsGiven"/> (which clears on move) it is <b>preserved</b> across move / rotate / group-rotate —
    /// the part keeps its save identity so its live-state CO and cargo travel with it on write-back, and the diff
    /// classifies it as <i>moved</i> rather than deleted+new. It is dropped only by operations that create a genuinely new
    /// part: duplicate, paste, paint, box-fill, symmetry-mirror, a def-changing replace / re-skin, and layout-only
    /// template/save import (all of which build a fresh <see cref="Placement"/> without copying it). A
    /// <b>state</b>-changing swap (<see cref="Restate"/>) drops it too — the item record can't be reused under a
    /// new def — but records it in <see cref="SwappedFromStrID"/>, so the part is still known to be one the player
    /// already owns. Drives the save-edit diff (<see cref="ShipDiff"/>). Init-only: identity is fixed at creation
    /// and never reassigned.
    /// </summary>
    public string? OriginStrID { get; init; }

    /// <summary>
    /// The <c>strID</c> of the save item this part <b>used to be</b>, when a state-changing swap
    /// (<see cref="Restate"/>: uninstall / install, open / close a door) rebuilt it under a different def. Its
    /// <see cref="OriginStrID"/> is necessarily null — the def changed, so the save's item record can't be reused
    /// and the write-back has to author a fresh item — but the object is one the player <i>already owns</i>, merely
    /// in a different state. That distinction is what stops the edit cost billing an uninstalled fixture as though
    /// it had been conjured (issue #19); the diff reports it and <see cref="EditCost"/> prices it as a move.
    /// </summary>
    public string? SwappedFromStrID { get; init; }

    /// <summary>The def this part carried before the swap that set <see cref="SwappedFromStrID"/>, so a swap back
    /// to it can restore the save identity outright (uninstall then re-install is a no-op, and must be free).
    /// Always null when <see cref="SwappedFromStrID"/> is.</summary>
    public string? SwappedFromDef { get; init; }

    /// <summary>
    /// A replacement for this part under <paramref name="targetDef"/>, for a swap that changes a part's <b>state</b>
    /// rather than its identity: uninstall / install (<see cref="FormSwap"/>) and open / close a door. Tile and
    /// cargo ride across; given-ness is cleared, so the problem scan re-checks the result like any authoring act.
    ///
    /// <para>Swapping back to the def the part came from restores <see cref="OriginStrID"/> outright — same def,
    /// same cargo, so the save's own item record genuinely can be reused again and the round trip is free. Any
    /// other swap records where it came from in <see cref="SwappedFromStrID"/> instead. A part with no save
    /// identity to preserve carries neither.</para>
    /// </summary>
    public Placement Restate(string targetDef, int rot)
    {
        var carriedId = SwappedFromStrID ?? OriginStrID;
        var carriedDef = SwappedFromDef ?? DefName;
        var backHome = carriedId is not null && string.Equals(carriedDef, targetDef, StringComparison.Ordinal);
        return new Placement
        {
            // CustomName rides across: switching a device off, or uninstalling it, does not change what it is
            // called. ZBias does too: a canister pushed behind its regulator stays behind it once uninstalled.
            // So does Fill: uninstalling a tank does not empty it, and the loose form is the same shell with the
            // same volume and rating. A line the target def does not have is dropped where the fill is applied.
            DefName = targetDef, X = X, Y = Y, Rot = rot, IsGiven = false, Cargo = Cargo, CustomName = CustomName,
            ZBias = ZBias, NavLayout = NavLayout, Fill = Fill, Condition = Condition,
            OriginStrID = backHome ? carriedId : null,
            SwappedFromStrID = backHome ? null : carriedId,
            SwappedFromDef = backHome || carriedId is null ? null : carriedDef,
        };
    }

    /// <summary>
    /// A name the user gave this part, replacing its stock one everywhere the part is named. Null (and omitted
    /// from the .oplan) when it carries the name its def came with.
    ///
    /// <para>This is the game's own rename, not a label Ostraplan invented: <c>CondOwner.Rename</c> stores it as a
    /// GUI-prop-map panel called <c>Rename</c> with a single <c>strName</c> key, and the game re-applies it on load
    /// through <c>CheckForRename</c> (from <c>Ship.CreatePart</c>, which <c>SpawnItems</c> spawns each item
    /// through). Core ships already use it (the stock <i>Babak Refit</i> names an electrical box
    /// "Pressurization SB"), which is why an import reads it as well as writing it.</para>
    ///
    /// <para>It rides through a move and through <see cref="Restate"/> (uninstall, install, switch on or off, a
    /// re-skin), since none of those change what the thing is called — unlike <see cref="OriginStrID"/>, which a
    /// non-returning Restate hands off to <see cref="SwappedFromStrID"/>. Duplicate and paste drop it along with
    /// the rest of the part's identity.</para>
    /// </summary>
    public string? CustomName { get; set; }

    /// <summary>
    /// The contained sub-objects this part holds — loose cargo and slotted equipment, nested (see
    /// <see cref="CargoItem"/>). Populated when the design is imported from a save <b>for editing</b>
    /// (<see cref="SaveEditImport"/>); empty for from-scratch or plain template parts (which carry no player
    /// cargo). It rides with the part through a move (the same object is mutated). The verbatim item/CO state
    /// stays in the <see cref="SaveShipContext"/> keyed by <see cref="CargoItem.StrID"/> and is what a write-back
    /// preserves; this tree drives the inventory view and the cargo-loss warning.
    /// </summary>
    public IReadOnlyList<CargoItem> Cargo { get; set; } = [];

    /// <summary>
    /// A nav console's own screen arrangement, when the user has laid one out: the game's <c>NavModConfig</c> map,
    /// module GUI-prefab key → anchor rect (<c>"xMin|yMin|xMax|yMax"</c>), with <c>""</c> for a module that is
    /// aboard but shelved in the console's edit menu. Null on every other part, and on a console left at the
    /// arrangement the game itself would produce (<see cref="NavConsole.Arrange"/> computes that on demand, so a
    /// design does not carry a copy of the defaults around).
    ///
    /// <para>A key the map does not mention falls back to that computed default, so a module added to the console
    /// after the arrangement was made still lands somewhere sensible rather than vanishing.</para>
    /// </summary>
    public IReadOnlyDictionary<string, string>? NavLayout { get; set; }

    /// <summary>
    /// How much of what this container holds, when the user has said: payload condition
    /// (<c>StatGasMolO2</c>, <c>StatLiqD2O</c>, …) → amount. Null on every other part, and on a canister left
    /// at the fill its def ships with, so a design only carries the ones actually changed.
    ///
    /// <para>An <b>absent line is empty</b>, not "stock": the map is the whole truth about this container's
    /// contents once it exists, which is what lets a tank be emptied at all (see
    /// <see cref="ContainerFill.Overlay"/>). Everything downstream — value, RCS reaction mass, the torch
    /// reactant clock, the rating — reads it through <see cref="ShipGrid.FromDocumentFramed"/>, which lays it
    /// over the def's own starting conditions once so no analysis has to know it exists.</para>
    ///
    /// <para>It rides through a move and through <see cref="Restate"/>, since neither empties a tank, and is
    /// dropped by duplicate / paste along with the rest of the part's identity.</para>
    /// </summary>
    public IReadOnlyDictionary<string, double>? Fill { get; set; }
}

/// <summary>
/// One thing the design draws: a placed <see cref="Placement"/> or a <see cref="LooseObject"/> lying on a tile.
/// Exactly one of the two is set. Placements and loose items share a single render order (see
/// <see cref="ShipDocument.RenderOrder"/>) and a single click order (<see cref="ShipDocument.RenderStackAt"/>),
/// so every drawing and picking site works in these rather than in two passes that could disagree.
/// </summary>
public readonly record struct RenderItem(Placement? Placement, LooseObject? Loose)
{
    public bool IsLoose => Loose is not null;
    public Guid Id => Placement?.Id ?? Loose!.Id;
    public string DefName => Placement?.DefName ?? Loose!.DefName;
    public int X => Placement?.X ?? Loose!.X;
    public int Y => Placement?.Y ?? Loose!.Y;
    public int Rot => Placement?.Rot ?? Loose!.Rot;
    public int ZBias => Placement?.ZBias ?? Loose!.ZBias;

    /// <summary>The name the user gave this one, or null when it carries its def's own (see
    /// <see cref="Rename"/>). Both halves carry one: the game renames a tool on the deck as readily as the rack
    /// it belongs in. Branches on which half is set rather than coalescing, unlike the members above: the value
    /// is itself nullable, so <c>Placement?.CustomName ?? Loose!.CustomName</c> would dereference the null half
    /// for every unnamed part.</summary>
    public string? CustomName => Placement is { } p ? p.CustomName : Loose!.CustomName;
}

/// <summary>
/// The design being edited: an unbounded tile plane of placements plus the
/// accumulated tile conditions. All mutation goes through the command stack.
/// </summary>
public sealed class ShipDocument
{
    private readonly List<Placement> _placements = [];
    private long _seq;
    private readonly Dictionary<Guid, long> _order = [];   // insertion order for stable draw/hit priority (parts AND loose items)
    private readonly Dictionary<(int, int), List<Placement>> _byTile = [];   // spatial index: tile -> parts covering it
    private readonly HashSet<Guid> _cargoEdited = [];   // placements whose container contents were authored/removed
    private readonly List<ShipZone> _zones = [];   // painted crew/trade zones (overlays, not tile-grid parts)
    private readonly Dictionary<(int, int), LooseObject> _looseByTile = [];   // loose items lying on tiles (overlay, one per tile)
    private readonly List<DeviceLink> _links = [];   // signal connections between signalable devices (overlay, by Placement.Id)
    private readonly HashSet<string> _dismissedAlerts = new(StringComparer.Ordinal);   // problem warnings the user hid (by DismissKey)

    public Catalog Catalog { get; }
    public TileConds Conds { get; }
    public string? FilePath { get; set; }

    /// <summary>
    /// When this design was imported from a save <b>for editing</b> in this session, the save + ship it came
    /// from — so the write-back knows which ship to offer without asking again. Null for from-scratch, template
    /// and layout-only designs.
    ///
    /// <para><b>Not persisted.</b> An <c>.oplan</c> is save-agnostic: it carries its own container contents and
    /// is written over whichever ship is chosen at the write, so nothing in the file depends on a save still
    /// being on this machine. This is a convenience for the sitting the import happened in, and it dies with the
    /// document.</para>
    /// </summary>
    public SaveSourceRef? SourceSave { get; set; }

    /// <summary>
    /// What this design <b>is</b>, which decides how it is analysed and how it reaches the game. A residence is
    /// the same tile grid, the same placement law and the same rooms as a ship (GAME-INTERNALS §19); what differs
    /// is that it has no drive and no nav, so the four vessel-only readouts are meaningless on it, and that it is
    /// delivered as a station sub-module rather than as a vessel.
    ///
    /// <para>Explicit and persisted rather than inferred, because the two available inferences each fail on a
    /// case that matters: a pipe in <see cref="SourceSave"/>'s RegID is conclusive but only exists for a design
    /// imported from a save, and a fitting-based guess would misfire on an unusual ship with no way to overrule
    /// it. <see cref="DocumentKindGuess"/> supplies the import default; the user owns it after that.</para>
    /// </summary>
    public DocumentKind Kind { get; set; } = DocumentKind.Ship;

    /// <summary>
    /// Where the ship this design was imported from had its grid anchor (<c>vShipPos</c>), expressed in
    /// <b>document</b> tile coords, or null for a design authored here.
    ///
    /// <para>Only one thing reads it, and it is the reason it exists: micrometeoroid strikes all converge on the
    /// tile at world origin, which the anchor decides (§26). An imported ship's convergence point is wherever its
    /// save or template put it — inside the hull for 85% of the shipped fleet — while a design Ostraplan exports
    /// gets a fresh anchor at the export grid's origin, just outside the corner. Reporting one for the other would
    /// answer the wrong question for someone checking the ship they are actually flying.</para>
    ///
    /// <para>Import-time provenance, deliberately <b>not</b> persisted in the <c>.oplan</c>: it describes the ship
    /// the design came out of rather than the design, and a reopened file is on its way to being exported afresh,
    /// which is exactly the fallback case. Document coords are the source ship's own grid coords
    /// (<see cref="ShipGrid.TemplateTile"/> subtracts <c>vShipPos</c>), so the anchor is
    /// <c>(−vShipPos.x, +vShipPos.y)</c>.</para>
    /// </summary>
    public (double X, double Y)? SourceShipPos { get; set; }

    /// <summary>True when the vessel-only analyses (Ship Rating, the nav diagnostic, propulsion, flight
    /// dynamics) do not apply to this design. Read by the shell to hide them; the export path still bakes
    /// <c>aRooms</c>/<c>aRating</c> either way, because the game re-derives both on a full load and a
    /// residence's room values are what its price is computed from.</summary>
    public bool IsResidence => Kind == DocumentKind.Residence;

    /// <summary>
    /// Extra mass in kilograms the design is expected to haul: a ship under tow, a hold of salvage, anything
    /// the layout itself does not carry. Shown as "dead weight to haul" on the Ship Rating report, because the
    /// two things a user might otherwise read it as — extra fuel, or their stowed cargo — are precisely what it
    /// is not. It feeds <see cref="Propulsion"/> only (never rooms, rating or value),
    /// where it lands in exactly the place the game puts a docked ship's mass — the divisor of
    /// <c>Ship.RCSAccelMax</c>. A design property rather than a view setting, so it is persisted in the .oplan
    /// and travels with the ship.
    /// <para>It changes no geometry, so it deliberately does <b>not</b> raise <see cref="Changed"/> — that event
    /// means "the layout moved", and firing it here would re-run the problem scan and drop the Ship Rating leak
    /// highlight on every keystroke. It is unsaved-state the shell tracks alongside ship identity and view
    /// orientation instead.</para>
    /// </summary>
    public double ExtraMassKg
    {
        get => _extraMassKg;
        set => _extraMassKg = double.IsFinite(value) ? Math.Max(0, value) : 0;
    }
    private double _extraMassKg;

    public event Action? Changed;

    private int _batchDepth;
    private bool _batchDirty;

    public ShipDocument(Catalog catalog)
    {
        Catalog = catalog;
        Conds = new TileConds(catalog);
    }

    /// <summary>
    /// Coalesce the <see cref="Changed"/> notifications of a burst of mutations into a
    /// single event fired when the scope disposes — so a group rotation / multi-part move /
    /// paste runs the (heavy) problem scan and repaint once, not once per part. Tile
    /// conditions still update per mutation, so mid-batch reads are correct.
    /// </summary>
    public IDisposable SuspendChanged()
    {
        _batchDepth++;
        return new BatchScope(this);
    }

    private void RaiseChanged()
    {
        if (_batchDepth > 0) { _batchDirty = true; return; }
        Changed?.Invoke();
    }

    private sealed class BatchScope(ShipDocument doc) : IDisposable
    {
        public void Dispose()
        {
            if (--doc._batchDepth != 0 || !doc._batchDirty) return;
            doc._batchDirty = false;
            doc.Changed?.Invoke();
        }
    }

    public IReadOnlyList<Placement> Placements => _placements;

    /// <summary>The painted crew/trade zones (see <see cref="ShipZone"/>). Zones are overlays: they are
    /// NOT in the spatial index and do NOT contribute tile conditions, rooms, or rating. All mutation goes
    /// through the command stack (the <c>internal</c> mutators below).</summary>
    public IReadOnlyList<ShipZone> Zones => _zones;

    /// <summary>The loose items dropped onto tiles (see <see cref="LooseObject"/>). Like zones these are a
    /// non-structural overlay — NOT in the spatial index and contributing no tile conditions, rooms, or rating —
    /// so analysis (the snapshot, CheckFit, room/airtightness/rating) never sees them. One per tile. All mutation
    /// goes through the command stack (the <c>internal</c> mutators below).</summary>
    public IReadOnlyCollection<LooseObject> LooseObjects => _looseByTile.Values;

    /// <summary>The loose item on a tile, or null.</summary>
    public LooseObject? LooseAt(int x, int y) => _looseByTile.GetValueOrDefault((x, y));

    /// <summary>
    /// Whether a loose item may land on a tile: true when nothing loose is there, or when the only thing there is
    /// one of the items in <paramref name="moving"/> (which is about to vacate it). One-per-tile is the loose
    /// overlay's single hard invariant, and this is how a group move, a paste or a duplicate asks about it before
    /// committing — without the <paramref name="moving"/> exemption, sliding a room one tile east would refuse
    /// itself, every item in it blocked by the one behind it.
    /// </summary>
    public bool LooseFreeAt(int x, int y, IReadOnlySet<Guid>? moving = null) =>
        LooseAt(x, y) is not { } lo || (moving is not null && moving.Contains(lo.Id));

    /// <summary>The signal connections between devices (see <see cref="DeviceLink"/>). Like zones/loose items these
    /// are a non-structural overlay — they carry no tile conditions and take no part in CheckFit/rooms/rating; they
    /// only render, persist and export. Held by <see cref="Placement.Id"/>. All mutation goes through the command
    /// stack (the <c>internal</c> mutators below).</summary>
    public IReadOnlyList<DeviceLink> Links => _links;

    /// <summary>The placement with this <see cref="Placement.Id"/>, or null — the reverse of the id a
    /// <see cref="DeviceLink"/> stores.</summary>
    public Placement? ById(Guid id) => _placements.FirstOrDefault(p => p.Id == id);

    /// <summary>The problem-warning keys the user has dismissed (see <see cref="ProblemScan"/> <c>DismissKey</c>).
    /// A dismissed warning is hidden from the PROBLEMS panel and the badge count until restored. Persisted in the
    /// <c>.oplan</c>. These are a display/persistence preference, not a design edit, so their mutators do <b>not</b>
    /// raise <see cref="Changed"/> (no re-scan) or go through the undo stack — the caller refreshes the panel.</summary>
    public IReadOnlyCollection<string> DismissedAlerts => _dismissedAlerts;

    public bool IsAlertDismissed(string key) => _dismissedAlerts.Contains(key);

    /// <summary>Dismiss a warning; true if it wasn't already dismissed.</summary>
    public bool DismissAlert(string key) => _dismissedAlerts.Add(key);

    /// <summary>Restore every dismissed warning; true if any were dismissed.</summary>
    public bool RestoreAlerts()
    {
        if (_dismissedAlerts.Count == 0) return false;
        _dismissedAlerts.Clear();
        return true;
    }

    /// <summary>Replace the dismissed-alert set (restoring the <c>.oplan</c> snapshot on open).</summary>
    public void LoadDismissedAlerts(IEnumerable<string> keys)
    {
        _dismissedAlerts.Clear();
        foreach (var k in keys) _dismissedAlerts.Add(k);
    }

    private readonly Dictionary<string, string> _factionNames = new(StringComparer.Ordinal);

    /// <summary>
    /// Friendly names for the factions this design's cargo belongs to (see <see cref="CargoItem.Factions"/>),
    /// raw id → the name a player would recognise.
    ///
    /// <para><b>It has to live on the document, because there is nowhere else it could.</b> A save invents
    /// factions at runtime — a stock playthrough carries 404 of them, one per person among the rest — and no data
    /// file under the install lists them, so this cannot be resolved from the catalog the way every other name is.
    /// It is read off the save's own system block at import and then carried in the <c>.oplan</c>, which is what
    /// keeps a design self-contained after it stopped belonging to a save.</para>
    ///
    /// <para>Display-only. Nothing about the layout, the rating, the cost or the export reads it, so it does not
    /// raise <see cref="Changed"/>.</para>
    /// </summary>
    public IReadOnlyDictionary<string, string> FactionNames => _factionNames;

    /// <summary>The friendly name for a faction id, or the id itself when the design never learned one — which is
    /// the honest answer for a design drawn from scratch, and better than showing nothing.</summary>
    public string FactionName(string id) => _factionNames.GetValueOrDefault(id, id);

    /// <summary>Replace the faction name table (an import, or restoring the <c>.oplan</c> snapshot on open).
    /// Entries with no friendly name are skipped, since the id is already the fallback.</summary>
    public void LoadFactionNames(IEnumerable<KeyValuePair<string, string>> names)
    {
        _factionNames.Clear();
        foreach (var (id, friendly) in names)
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrWhiteSpace(friendly))
                _factionNames[id] = friendly;
    }

    /// <summary>
    /// An independent copy for off-thread analysis: the same placements (poses + given-ness) with
    /// their own accumulated tile conditions, sharing the catalog. Safe to read on a background
    /// thread while the original keeps being edited on the UI thread. Cheap — rebuilding conditions
    /// for a few thousand parts is a millisecond or two.
    /// </summary>
    public ShipDocument Snapshot()
    {
        var copy = new ShipDocument(Catalog);
        foreach (var p in _placements)
            copy.Add(new Placement
            {
                DefName = p.DefName, X = p.X, Y = p.Y, Rot = p.Rot, IsGiven = p.IsGiven,
                OriginStrID = p.OriginStrID, SwappedFromStrID = p.SwappedFromStrID, SwappedFromDef = p.SwappedFromDef,
            });
        return copy;
    }

    public PartDef? Part(Placement p) => Catalog.Lookup(p.DefName);

    /// <summary>The primary airlock is fixed to the ship: no move/rotate/delete/duplicate. Identified by its
    /// CONDITIONS (see <see cref="Catalog.IsPrimaryDocksys"/>), so an imported ship whose airlock is pried open,
    /// damaged or modded is still recognised as having one.</summary>
    public bool IsLocked(Placement p) => Catalog.IsPrimaryDocksys(Part(p));

    public (int W, int H) FootprintOf(Placement p)
    {
        var part = Part(p);
        return part is null ? (1, 1) : GridMath.Size(part.Item.Width, part.Item.Height, p.Rot);
    }

    /// <summary>
    /// The bounding rect of a part's ABOVE-FLOOR body at its current pose — its footprint minus any
    /// under-floor-only reservation. The large fuel tanks project a 7×7 under-floor storage ring
    /// (<c>TILSubfloorAdds</c>, IsSubTile only) beneath a 3×3 visible body (<c>TIL2DeckAdds</c>, adds
    /// IsObstruction); the game treats only the body as "there" for selection and interaction, so
    /// that is what Ostraplan hit-tests, selects and outlines. Ordinary parts have no under-floor
    /// ring, so this equals the whole footprint. The placement law is unaffected — it keeps using the
    /// full socket grid (<see cref="CheckFit"/> reads the item's sockets directly).
    /// <para>The shape itself is <see cref="Catalog.BodyBox"/>, shared with the swap classing in
    /// <see cref="Catalog.SwapClass"/> so what a part looks like and what it can be replaced by agree.</para>
    /// </summary>
    public (int X, int Y, int W, int H) BodyBounds(Placement p)
    {
        if (Part(p) is not { } part)
        {
            var (w, h) = FootprintOf(p);
            return (p.X, p.Y, w, h);
        }
        var body = Catalog.BodyBox(part, p.Rot);
        return (p.X + body.X, p.Y + body.Y, body.W, body.H);
    }

    /// <summary>True if the tile falls inside the part's above-floor body (see <see cref="BodyBounds"/>).</summary>
    public bool Covers(Placement p, int x, int y)
    {
        var (bx, by, bw, bh) = BodyBounds(p);
        return x >= bx && x < bx + bw && y >= by && y < by + bh;
    }

    /// <summary>
    /// The sort key that puts one drawable under another, bottom-to-top. In order:
    /// <list type="number">
    /// <item>The def's own <b>z-scale</b> (<see cref="ItemDef.ZScale"/>, the game's <c>fZScale</c>), which is the
    /// game's answer and outranks everything else. It orders by <i>object type</i>: background plate 0.001,
    /// floors and floor labels 0.01, seats 0.1, chargers 0.2, canisters 0.5, alarms 0.75, walls and doors 1.0,
    /// bulkhead bins 1.01, power conduit 1.02.</item>
    /// <item>The object's <b>manual bias</b> (<see cref="Placement.ZBias"/>), which is why a nudge beats every
    /// automatic rule below it. It sits under the z-scale because a nudge exists to settle what the game leaves
    /// open, not to overrule what it decides — see <see cref="ZOrder"/>.</item>
    /// <item>The <b>object rank</b>: canisters, then other placed parts, then loose deck clutter
    /// (<see cref="Catalog.RankVessel"/>).</item>
    /// <item>The body's <b>bottom edge</b>, so a small part standing within a larger one's body reads as sitting
    /// in it.</item>
    /// <item><b>Insertion order</b>, the last resort, so an unchanged design draws the same way twice.</item>
    /// </list>
    /// <para>Everything below the z-scale only ever separates two defs the game gave the same z-scale, where its
    /// own sprite sort ties and it defines no order at all. Those terms are Ostraplan's convention; the z-scale
    /// above them is a port (§15).</para>
    /// </summary>
    private (double ZScale, int Bias, int NLayer, int Rank, int Bottom, long Seq) RenderKey(RenderItem item)
    {
        var part = Catalog.Lookup(item.DefName);
        var rank = Catalog.IsVessel(part) ? Catalog.RankVessel
                 : item.IsLoose ? Catalog.RankLoose
                 : Catalog.RankInstalled;
        // The def's own nLayer sits below the manual bias rather than above it: it is 0 for every def the game
        // ships (§15), so a mod is the only thing that can set it, and a nudge that refused to move a part would
        // be a no-op with nothing on screen to explain it.
        return (ZScaleOf(part), item.ZBias, part?.Item.NLayer ?? 0, rank, BottomEdge(item),
                _order.GetValueOrDefault(item.Id));
    }

    /// <summary>The draw-order scalar a drawable sorts by. An unresolved def (a part the catalog has never heard
    /// of, drawn as a placeholder) takes the same 1.0 the game's own DTO defaults to.</summary>
    internal double ZScaleOf(PartDef? part) => part?.Item.ZScale ?? 1.0;

    /// <summary>The row just past the bottom of a drawable's above-floor body — <see cref="BodyBounds"/> for a
    /// placement (so the big tanks measure their visible 3×3 body, not the 7×7 under-floor ring) and the rotated
    /// footprint for a loose item.</summary>
    private int BottomEdge(RenderItem item)
    {
        if (item.Placement is { } p)
        {
            var (_, by, _, bh) = BodyBounds(p);
            return by + bh;
        }
        var lo = item.Loose!;
        var def = Catalog.Lookup(lo.DefName);
        var (_, h) = def is null ? (1, 1) : GridMath.Size(def.Item.Width, def.Item.Height, lo.Rot);
        return lo.Y + h;
    }

    /// <summary>Order a set of placements bottom-to-top for painting/hit-testing (see <see cref="RenderKey"/>).</summary>
    private IEnumerable<Placement> InDrawOrder(IEnumerable<Placement> parts) =>
        parts.Select(p => new RenderItem(p, null)).OrderBy(RenderKey, RenderKeyComparer).Select(i => i.Placement!);

    private static readonly Comparer<(double ZScale, int Bias, int NLayer, int Rank, int Bottom, long Seq)> RenderKeyComparer =
        Comparer<(double, int, int, int, int, long)>.Default;

    /// <summary>The placements alone, bottom-to-top. Prefer <see cref="RenderOrder"/> for anything that draws:
    /// this omits the loose items, which share the same order.</summary>
    public IEnumerable<Placement> DrawOrder() => InDrawOrder(_placements);

    /// <summary>
    /// Everything the design draws — placements and loose floor items — in one order, bottom to top. The two used
    /// to be separate passes, which pinned every loose item on top of every part regardless of what it was lying
    /// against, and left the nudge with nothing to act on.
    /// </summary>
    public IEnumerable<RenderItem> RenderOrder() =>
        _placements.Select(p => new RenderItem(p, null))
                   .Concat(_looseByTile.Values.Select(lo => new RenderItem(null, lo)))
                   .OrderBy(RenderKey, RenderKeyComparer);

    /// <summary>Every placement covering the tile (spatial-index lookup, unordered). Empty off the ship.</summary>
    public IReadOnlyList<Placement> PlacementsAt(int x, int y) =>
        _byTile.TryGetValue((x, y), out var list) ? list : [];

    /// <summary>Topmost placement covering the tile, or null.</summary>
    public Placement? HitTest(int x, int y) => InDrawOrder(PlacementsAt(x, y)).LastOrDefault();

    /// <summary>Every placement covering the tile, topmost first (reverse draw order) — for layer picking.</summary>
    public IReadOnlyList<Placement> HitTestStack(int x, int y)
    {
        var stack = InDrawOrder(PlacementsAt(x, y)).ToList();
        stack.Reverse();
        return stack;
    }

    /// <summary>
    /// Everything drawn on the tile — placements and any loose item — topmost first. This is what a click and the
    /// stacked picker walk, so what you reach matches what you see; <see cref="HitTestStack"/> stays the
    /// placements-only view for callers that act on structure (containers, wiring, replace).
    /// </summary>
    public IReadOnlyList<RenderItem> RenderStackAt(int x, int y)
    {
        var items = PlacementsAt(x, y).Select(p => new RenderItem(p, null)).ToList();
        if (LooseAt(x, y) is { } lo) items.Add(new RenderItem(null, lo));
        return [.. items.OrderByDescending(RenderKey, RenderKeyComparer)];
    }

    public (int MinX, int MinY, int MaxX, int MaxY)? Bounds()
    {
        if (_placements.Count == 0) return null;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        foreach (var p in _placements)
        {
            var (w, h) = FootprintOf(p);
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X + w - 1);
            maxY = Math.Max(maxY, p.Y + h - 1);
        }
        return (minX, minY, maxX, maxY);
    }

    // ---- spatial index ----

    // The index (and thus hit-testing/selection) uses the ABOVE-FLOOR body, not the socket footprint:
    // clicking a tank's under-floor ring hits the floor there, not the tank centred on it.
    private IEnumerable<(int, int)> Tiles(Placement p)
    {
        var (bx, by, bw, bh) = BodyBounds(p);
        for (var r = 0; r < bh; r++)
            for (var c = 0; c < bw; c++)
                yield return (bx + c, by + r);
    }

    private void Index(Placement p)
    {
        foreach (var t in Tiles(p))
        {
            if (!_byTile.TryGetValue(t, out var list)) _byTile[t] = list = [];
            list.Add(p);
        }
    }

    private void Unindex(Placement p)
    {
        foreach (var t in Tiles(p))
            if (_byTile.TryGetValue(t, out var list) && list.Remove(p) && list.Count == 0)
                _byTile.Remove(t);
    }

    // ---- mutations (command implementations only) ----

    internal void Add(Placement p)
    {
        _placements.Add(p);
        _order[p.Id] = _seq++;
        Index(p);
        if (Part(p) is { } part) Conds.Apply(p, part.Item, +1);
        RaiseChanged();
    }

    internal void Remove(Placement p)
    {
        if (!_placements.Remove(p)) return;
        Unindex(p);
        _order.Remove(p.Id);
        if (Part(p) is { } part) Conds.Apply(p, part.Item, -1);
        RaiseChanged();
    }

    /// <summary>
    /// Reposition a part. <paramref name="given"/> is the <see cref="Placement.IsGiven"/> state to land in:
    /// null (the default) means "this is an authoring act", which clears it. An <b>undo</b> passes the state
    /// the part held before the move, so reversing a move truly restores the part — otherwise a nudge and a
    /// Ctrl+Z leave imported structure permanently re-authored, re-judged by the placement law and counted as
    /// new construction. See <see cref="MoveCommand"/>.
    /// </summary>
    internal void MoveTo(Placement p, int x, int y, bool? given = null)
    {
        var part = Part(p);
        Unindex(p);   // reindex under the new pose
        if (part is not null) Conds.Apply(p, part.Item, -1);
        p.X = x;
        p.Y = y;
        // Moving a part is an authoring act: clear its given-ness so the placement law re-applies (a device
        // dragged off its wall must flag). OriginStrID is KEPT — the part is still the same save item, so its
        // live-state CO and cargo travel with it and the diff sees a move, not a delete+new.
        p.IsGiven = given ?? false;
        if (part is not null) Conds.Apply(p, part.Item, +1);
        Index(p);
        RaiseChanged();
    }

    /// <summary>Reposition and turn a part. <paramref name="given"/> as <see cref="MoveTo"/>.</summary>
    internal void SetPose(Placement p, int x, int y, int rot, bool? given = null)
    {
        var part = Part(p);
        Unindex(p);
        if (part is not null) Conds.Apply(p, part.Item, -1);
        p.X = x;
        p.Y = y;
        p.Rot = GridMath.Norm(rot);
        // repositioning re-authors the part: clear given-ness so the law re-applies; keep OriginStrID (identity)
        p.IsGiven = given ?? false;
        if (part is not null) Conds.Apply(p, part.Item, +1);
        Index(p);
        RaiseChanged();
    }

    /// <summary>Replace a part's contained cargo (the inventory editor's add/remove result) and mark it edited, so
    /// the <c>.oplan</c> persists a full snapshot of this container's contents rather than re-reading it from the
    /// save on reopen (the authored tree is authoritative). Cargo lives inside the part, not on the tile grid, so
    /// this touches no spatial index or tile conditions — it just swaps the tree and raises <see cref="Changed"/>.</summary>
    internal void SetCargo(Placement p, IReadOnlyList<CargoItem> cargo)
    {
        p.Cargo = cargo;
        _cargoEdited.Add(p.Id);
        RaiseChanged();
    }

    /// <summary>Replace a nav console's screen arrangement (see <see cref="Placement.NavLayout"/>); null restores
    /// the computed default. Like cargo, this lives inside the part rather than on the tile grid, so it touches no
    /// spatial index or tile conditions.</summary>
    internal void SetNavLayout(Placement p, IReadOnlyDictionary<string, string>? layout)
    {
        p.NavLayout = layout;
        RaiseChanged();
    }

    /// <summary>Replace a container's authored fill (see <see cref="Placement.Fill"/>); null returns it to the
    /// amounts its def ships with. Contents live inside the part, so no spatial index or tile condition moves —
    /// but this DOES raise <see cref="Changed"/>, because value, reaction mass and the torch reactant figures all
    /// read the fill through the analysis grid and have to be recomputed.</summary>
    internal void SetFill(Placement p, IReadOnlyDictionary<string, double>? fill)
    {
        p.Fill = fill;
        RaiseChanged();
    }

    /// <summary>Set a part's painted condition (see <see cref="Placement.Condition"/>); null hands it back to
    /// whatever the export's own wear setting decides. Like a fill this moves nothing spatial, but it DOES raise
    /// <see cref="Changed"/>: the Ship Rating's Condition slot is a mean over the installed parts, so it moves
    /// with every stroke.</summary>
    internal void SetCondition(Placement p, double? condition)
    {
        p.Condition = Paint.Clamp(condition);
        RaiseChanged();
    }

    /// <inheritdoc cref="SetCondition(Placement, double?)"/>
    internal void SetCondition(LooseObject o, double? condition)
    {
        o.Condition = Paint.Clamp(condition);
        RaiseChanged();
    }

    /// <summary>True if this part's cargo was edited in-session (or restored from a persisted snapshot) — the
    /// signal that its contents are authored and must be persisted/kept verbatim rather than re-derived.</summary>
    public bool IsCargoEdited(Placement p) => _cargoEdited.Contains(p.Id);

    /// <summary>Mark a part's cargo as edited without changing it — used when reopening an <c>.oplan</c> whose
    /// snapshot already carries this container's authored contents, so a re-save persists them again.</summary>
    public void MarkCargoEdited(Placement p) => _cargoEdited.Add(p.Id);

    // ---- zone mutations (command implementations only) ----

    internal void AddZone(ShipZone z) { _zones.Add(z); RaiseChanged(); }

    /// <summary>Re-insert a zone at a specific position — the undo of a delete, so list order (hence the
    /// serialized <c>aZones</c> order and last-wins overlap) is restored exactly.</summary>
    internal void InsertZone(int index, ShipZone z) { _zones.Insert(Math.Clamp(index, 0, _zones.Count), z); RaiseChanged(); }

    internal void RemoveZone(ShipZone z) { if (_zones.Remove(z)) RaiseChanged(); }

    /// <summary>The index of a zone in the list (for a delete command to capture, so undo restores order).</summary>
    public int IndexOfZone(ShipZone z) => _zones.IndexOf(z);

    /// <summary>Replace a zone's covered tiles (a paint/erase stroke commits one of these). Zones carry no
    /// tile conditions of their own, so this touches no spatial index — it just swaps the set.</summary>
    internal void SetZoneTiles(ShipZone z, IEnumerable<(int X, int Y)> tiles) { z.Tiles = [.. tiles]; RaiseChanged(); }

    /// <summary>Replace a zone's editable non-tile fields (name/colour/type/role/advanced) from a snapshot.</summary>
    internal void SetZoneMeta(ShipZone z, ZoneMeta meta) { z.ApplyMeta(meta); RaiseChanged(); }

    // ---- loose-object mutations (command implementations only) ----

    /// <summary>Drop a loose item onto its tile. One per tile: an existing loose object there is replaced (the
    /// placement law forbids that, so in practice the tile is always empty first).</summary>
    /// <summary>Put a loose item on its tile. Every route in goes through here (a palette drop, an import, an
    /// <c>.oplan</c> load, a redo), which is why the intrinsic seed lives here rather than at each call site.</summary>
    internal void AddLoose(LooseObject o)
    {
        SeedIntrinsics(o, Catalog);
        _looseByTile[(o.X, o.Y)] = o;
        _order[o.Id] = _seq++;
        RaiseChanged();
    }

    /// <summary>Remove a loose item — only if it is still the one on its tile (guards a stale undo).</summary>
    internal void RemoveLoose(LooseObject o)
    {
        if (_looseByTile.TryGetValue((o.X, o.Y), out var cur) && ReferenceEquals(cur, o) && _looseByTile.Remove((o.X, o.Y)))
        {
            _order.Remove(o.Id);
            RaiseChanged();
        }
    }

    /// <summary>
    /// Reposition a loose item, keeping its identity — the loose twin of <see cref="MoveTo"/>. The tile index is
    /// keyed by position, so the old key has to go before the new one is written; everything else about the object
    /// (its quantity, its contents, its draw-order bias and its place in the insertion order) rides along, which is
    /// what lets the selection keep pointing at it across a drag.
    ///
    /// <para>Nothing structural is re-analysed, because a loose item contributes no tile conditions and takes no
    /// part in the law. There is no given-ness to clear either: only structure carries that.</para>
    /// </summary>
    internal void MoveLooseTo(LooseObject o, int x, int y, int rot)
    {
        if (_looseByTile.TryGetValue((o.X, o.Y), out var cur) && ReferenceEquals(cur, o)) _looseByTile.Remove((o.X, o.Y));
        o.X = x;
        o.Y = y;
        o.Rot = GridMath.Norm(rot);
        _looseByTile[(o.X, o.Y)] = o;
        RaiseChanged();
    }

    /// <summary>
    /// Reposition several loose items as one step (a group move, rotate or flip, and the undo of any of them).
    /// Every mover is lifted out of the tile index <b>before</b> any of them lands, so a set that shuffles within
    /// itself — two items swapping tiles, a room sliding one tile along — does not have the first mover overwrite
    /// the tile the second is still sitting on.
    ///
    /// <para>The caller is expected to have cleared the destinations with <see cref="LooseFreeAt"/>, exempting the
    /// movers. This does not re-check: like <see cref="MoveCommand"/> for structure, the command layer decides
    /// whether a transform is allowed and this one carries it out.</para>
    /// </summary>
    internal void SetLoosePoses(IReadOnlyList<(LooseObject Obj, int X, int Y, int Rot)> poses)
    {
        foreach (var lift in poses)
            if (_looseByTile.TryGetValue((lift.Obj.X, lift.Obj.Y), out var cur) && ReferenceEquals(cur, lift.Obj))
                _looseByTile.Remove((lift.Obj.X, lift.Obj.Y));
        foreach (var (o, x, y, rot) in poses)
        {
            o.X = x;
            o.Y = y;
            o.Rot = GridMath.Norm(rot);
            _looseByTile[(o.X, o.Y)] = o;
        }
        RaiseChanged();
    }

    /// <summary>Set the stacked quantity of a loose item in place (keeps its identity for selection).</summary>
    internal void SetLooseQuantity(LooseObject o, int quantity) { o.Quantity = quantity; RaiseChanged(); }

    /// <summary>Replace what a loose deck item holds (see <see cref="LooseObject.Cargo"/>). Contents live inside
    /// the item, not on the tile grid, so nothing structural is re-analysed; the canvas still repaints, since a
    /// filled container can draw differently and the inspector shows the count.</summary>
    internal void SetLooseCargo(LooseObject o, IReadOnlyList<CargoItem> cargo) { o.Cargo = cargo; RaiseChanged(); }

    /// <summary>
    /// Give a loose item the containers its def spawns with, unless it already carries them. The game creates
    /// those with the object (a backpack's four pouches, an EVA suit's four compartments), and a save restores an
    /// item as recorded rather than respawning it, so a deck item that never got them would reach the game empty
    /// and stay that way. Idempotent, so it is safe on every load and on an item that was imported with contents.
    /// </summary>
    internal static void SeedIntrinsics(LooseObject o, Catalog catalog)
    {
        if (catalog.Lookup(o.DefName) is not { } part) return;
        var missing = CargoEdit.IntrinsicContentsOf(part, catalog)
            .Where(seed => !o.Cargo.Any(c => c.Intrinsic && c.DefName == seed.DefName && c.SlotName == seed.SlotName))
            .ToList();
        if (missing.Count > 0) o.Cargo = [.. o.Cargo, .. missing];
    }

    // ---- draw-order mutations (command implementations only) ----

    /// <summary>Set a placed part's manual draw-order bias (see <see cref="Placement.ZBias"/>). Cosmetic: no
    /// geometry moves, so no tile conditions or spatial index change, but the canvas must repaint.</summary>
    internal void SetZBias(Placement p, int bias) { p.ZBias = bias; RaiseChanged(); }

    /// <summary>Set a loose item's manual draw-order bias (see <see cref="LooseObject.ZBias"/>).</summary>
    internal void SetZBias(LooseObject o, int bias) { o.ZBias = bias; RaiseChanged(); }

    /// <summary>Set or clear a part's own name (see <see cref="Placement.CustomName"/>). Stored verbatim with only
    /// empty collapsing to null (<see cref="Rename.OrNull"/>): typed input is normalised at the rename dialog, and
    /// normalising again here would corrupt an undo that restores a name imported exactly as the game stored it.</summary>
    internal void SetCustomName(Placement p, string? name) { p.CustomName = Rename.OrNull(name); RaiseChanged(); }

    /// <summary>Set or clear a loose deck item's own name (see <see cref="LooseObject.CustomName"/>), on the same
    /// terms as a part's: the game renames a tool on the floor as readily as the rack it belongs in.</summary>
    internal void SetCustomName(LooseObject o, string? name) { o.CustomName = Rename.OrNull(name); RaiseChanged(); }

    // ---- device-link mutations (command implementations only) ----

    /// <summary>Add a signal connection (no-op if the identical directed link already exists).</summary>
    internal void AddLink(DeviceLink link) { if (!_links.Contains(link)) { _links.Add(link); RaiseChanged(); } }

    /// <summary>Remove a signal connection.</summary>
    internal void RemoveLink(DeviceLink link) { if (_links.Remove(link)) RaiseChanged(); }

    internal void Clear()
    {
        _placements.Clear();
        _order.Clear();
        _byTile.Clear();
        Conds.Clear();
        _cargoEdited.Clear();
        _zones.Clear();
        _looseByTile.Clear();
        _links.Clear();
        _dismissedAlerts.Clear();
        _seq = 0;
        RaiseChanged();
    }
}
