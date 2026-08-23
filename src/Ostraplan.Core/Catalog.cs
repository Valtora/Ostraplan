using System.Collections.Concurrent;
using System.Text.Json;

namespace Ostraplan.Core;

/// <summary>
/// A light a placed part emits, resolved from its item def's <c>aLights</c> through the light + colour tables:
/// the pixel offset from the item centre (+y up, the game's <c>ptPos</c>, rotated onto the grid via
/// <see cref="ShipGrid.MapPointTile"/> at sim time like a power input point), the RGB colour and
/// <see cref="Intensity"/> (colour alpha / 255), the <see cref="Radius"/> in tiles, whether it
/// <see cref="CastsLight"/> (colour != <c>"Blank"</c>) versus being a glow decal only, the glow-decal sprite
/// (<see cref="GlowSprite"/>, absolute PNG path or null), and whether it blinks. Feeds <see cref="LightNetwork"/>.
/// </summary>
public sealed record PartLight(
    double OffsetX, double OffsetY, byte R, byte G, byte B,
    double Intensity, double Radius, bool CastsLight, string? GlowSprite, bool CanBlink);

/// <summary>One palette entry: an installable build job joined to its placed item def.</summary>
public sealed record PartDef(
    string DefName,          // the installed item / condowner strName (what ships reference)
    string Friendly,
    string Category,         // HULL/HVAC/POWR/SENS/CTRL/FURN/APPS/MISC
    string Origin,           // which source defined the item (core or a mod label)
    ItemDef Item,
    string? SpriteAbs,       // resolved PNG, null = missing sprite
    string[] Inputs,
    string[] Tools,
    string[] StartingConds,  // the placed condowner's aStartingConds cond names
    IReadOnlyDictionary<string, double> StartingCondValues,  // name -> magnitude/amount (StatMass kg, StatPower, ...); see LootDef.CondAmount
    IReadOnlyDictionary<string, (double X, double Y)> MapPoints)  // condowner mapPoints (DockA/DockB, RoomA/B, ...)
{
    /// <summary>The item's normal-map PNG resolved on disk (<c>strImgNorm</c>), or null when it names none/"blank"
    /// or the file is missing. Light Viz shades N·L against it, exactly like the game's deferred normal RT.
    /// Note a cooverlay skin keeps its <b>base</b> item's normal map (the game's <c>JsonCOOverlay</c> overrides
    /// only <c>strImg</c>).</summary>
    public string? SpriteNormAbs { get; init; }

    /// <summary>The item's damaged-overlay PNG resolved on disk (<c>strImgDamaged</c>), or null when it names
    /// none/"blank" or the file is missing — which is the majority, and which the wear shader reads as
    /// <c>_DmgPresent = 0</c>. Unlike the normal map, a cooverlay skin CAN override this (see
    /// <see cref="ItemDef.ImgDamaged"/>).</summary>
    public string? SpriteDamagedAbs { get; init; }

    /// <summary>Resolved GUI-prop-map declarations — (instance, template) name pairs from the def's
    /// <c>mapGUIPropMaps</c> (base condowner + cooverlay). Drives the <c>aGPMSettings</c> baked onto a
    /// newly-injected device so it wires to power/panels on load (see <see cref="Catalog.GpmSettingsFor"/>).
    /// Empty for non-device parts (walls, floors, tools).</summary>
    public IReadOnlyList<(string Instance, string Template)> Gpm { get; init; } = [];

    /// <summary>The condowner's in-game flavour description (<c>strDesc</c>), or null when the def carries none.</summary>
    public string? Desc { get; init; }

    /// <summary>The per-second tickers the device's def declares (<c>aTickers</c>, e.g. "Power") — granted on
    /// install. The Power ticker grants <c>IsReadyUsePower</c> each tick, the gate a powered device needs to draw
    /// power; a save loads these from the CO (not the def), so an injected device must carry them or it stays
    /// dead. Empty for non-device parts (walls, floors).</summary>
    public string[] TickerNames { get; init; } = [];

    /// <summary>The part's in-game base value in credits (the <c>StatBasePrice</c> starting cond), or 0 when the
    /// def has none. This is the raw value the game reads before per-kiosk discount/markup — used for the
    /// save-edit cost estimate (see <see cref="EditCost"/>) and shown in the inspector.</summary>
    public double BasePrice => StartingCondValues.GetValueOrDefault("StatBasePrice");

    /// <summary>The cooverlay cond-loot this def's real conds depend on (<c>strCondLoot</c>), or null when the def
    /// is not a skin. <see cref="StartingConds"/>/<see cref="StartingCondValues"/> already have its deltas folded
    /// in; this flag says the fold happened, and it is what tells a <b>save</b> writer it may not fall back on the
    /// <c>aConds = ["DEFAULT"]</c> marker.
    ///
    /// <para>A save load cannot reproduce the fold. <c>CondOwner.SetData</c> sets <c>bFreezeConds</c> as soon as it
    /// has save data, and <c>DataHandler.GetCondOwner</c> attaches the <c>COOverlay</c> and runs <c>Init</c> →
    /// <c>Loot.ApplyCondLoot</c> only <i>after</i> that, where every write goes through <c>ParseCondEquation</c> /
    /// <c>AddCondAmount</c> and both return immediately while frozen. <c>bFreezeConds</c> is not cleared until
    /// <c>Ship.PostGameLoad</c>, so the deltas are gone for good: the DEFAULT marker resolves through
    /// <c>GetCondOwnerDef</c> to the <b>base</b> condowner and the part comes up wearing the shared base's flat
    /// stats. An MSS "Light Framework" wall loads at ItmWall1x1's 21 credits and 24 kg with no <c>IsMSS</c> or
    /// <c>IsWhite</c>, and a software textbook keeps electrical engineering's conds while the skin's interaction
    /// swap still happens, leaving it with a study action whose <c>CTTestThem</c> can never pass.</para>
    ///
    /// <para>The game's own writer has the same rule from the other side: <c>CondOwner.GetJSON</c> writes every
    /// cond out in full and collapses to DEFAULT only when the set matches the def exactly.</para></summary>
    public string? SkinCondLoot { get; init; }

    /// <summary>The <c>aConds</c> to write on a freshly synthesized condition owner for this def in a <b>save</b>
    /// (or on the save-shaped CO a template gives its contained cargo). <c>["DEFAULT"]</c> for a plain def, which
    /// is what the game writes for a part whose conds still match its def; the conds in full for a skin, because
    /// the marker resolves to the base condowner and the skin's deltas can never be applied afterwards (see
    /// <see cref="SkinCondLoot"/>).
    ///
    /// <para>Formatted as the game's own <c>&lt;cond&gt;=&lt;chance&gt;x&lt;amount&gt;</c>, invariant: its parser
    /// (<c>Loot.ParseCondEquation</c>) splits the amount on <c>'-'</c> to read a range and reads the number with
    /// the ambient culture, so a comma decimal separator would be mis-parsed and a negative amount read as a
    /// range. Neither can arise here — the cond fold already drops everything at or below zero.</para></summary>
    public string[] SavedConds => SkinCondLoot is null ? ["DEFAULT"] : CondEntries;

    /// <summary>Every one of this def's starting conds written out as a game-format entry, skin deltas included.
    /// This is what the <c>DEFAULT</c> marker stands for, spelled out — needed wherever a writer has to put
    /// something of its own on a cond the def also declares, because <c>CondOwner.SetData</c> expands the marker by
    /// <b>appending</b> the def's entries, so the def always has the last word over anything written beside it.
    /// See <see cref="SavedConds"/> for the format.</summary>
    public string[] CondEntries =>
        [.. DeclaredConds.Select(n =>
            FormattableString.Invariant($"{n}=1.0x{StartingCondValues.GetValueOrDefault(n, 1.0)}"))];

    /// <summary>Every cond name this def declares. Real data parses <see cref="StartingConds"/> and
    /// <see cref="StartingCondValues"/> out of one <c>aStartingConds</c> array so the two always agree; taking the
    /// union anyway costs nothing and keeps a def assembled by hand from behaving unlike a real one.</summary>
    public IEnumerable<string> DeclaredConds =>
        StartingConds.Concat(StartingCondValues.Keys.Where(k => !StartingConds.Contains(k, StringComparer.Ordinal)));

    /// <summary>Whether this def declares <paramref name="cond"/> itself, and would therefore overrule a writer
    /// that put its own amount for it beside the <c>DEFAULT</c> marker.</summary>
    public bool Declares(string cond) => DeclaredConds.Contains(cond, StringComparer.Ordinal);

    /// <summary>
    /// The conds the game gives a part that its def does not declare — <c>CondOwner.SetUpBehaviours</c>, ported
    /// rule for rule. A <b>save</b> writer has to record these itself, because that method is the last line of
    /// <c>SetData</c> and every one of its <c>AddCondAmount</c> calls is frozen out for a CO built from save data.
    ///
    /// <para>Nothing declares <c>IsDamageable</c> or <c>IsDestructable</c>: every part in the game gets them here,
    /// on first spawn, and from then on the save carries them like any other cond — which is why a game-built part
    /// has them and a def does not. A written part missing them cannot be damaged by a hit or an environmental
    /// attack (<c>Interaction</c> tests <c>IsDamageable</c> before applying either) and is not picked up as an
    /// explosion target (<c>TIsExplosionTarget</c> requires <c>IsDestructable</c>). The destroy chain itself
    /// survives, because the component <c>AddCommand</c> builds is not a condition.</para>
    ///
    /// <para>The three progress ceilings matter for a different reason: <c>DestCheck.DamageCheck</c> completes a
    /// job when progress <c>&gt;=</c> its max, and an absent cond reads as zero, so a missing ceiling makes the
    /// job finish on its first tick rather than never. 342 of the palette's 359 defs leave
    /// <c>StatRepairProgressMax</c> to this backfill.</para>
    ///
    /// <para>The two early returns are the game's and are load-bearing: a part with no <c>StatDamageMax</c>, or
    /// carrying <c>IsSystem</c>, stops after the ceilings, and an <c>IsUndamageable</c> part — or one that is
    /// neither installed nor solid, i.e. has no physical presence to hit — stops before <c>IsDamageable</c>.</para>
    /// </summary>
    public string[] BehaviourConds { get; init; } = [];

    /// <summary>
    /// <c>SetUpBehaviours</c>' rule itself, as cond name → amount, evaluated against the cond set the CO holds
    /// <b>at the moment the method runs</b>: the base condowner's, before any skin loot. <see cref="Catalog"/>
    /// applies it there and lets the loot settle on top, which is the game's order and the only one that gets
    /// <c>ItmConduit04</c> and the 247 damaged/patch floor skins right.
    /// </summary>
    public static IReadOnlyDictionary<string, double> BehaviourBackfill(
        IReadOnlyList<string> declared, IReadOnlyDictionary<string, double> values)
    {
        var has = new HashSet<string>(declared, StringComparer.Ordinal);
        foreach (var k in values.Keys) has.Add(k);
        var add = new Dictionary<string, double>(StringComparer.Ordinal);
        if (!has.Contains("StatInstallProgressMax")) add["StatInstallProgressMax"] = 1000;
        if (!has.Contains("StatUninstallProgressMax")) add["StatUninstallProgressMax"] = 1000;
        if (!has.Contains("StatRepairProgressMax")) add["StatRepairProgressMax"] = 1000;
        if (!has.Contains("StatDamageMax") || has.Contains("IsSystem")) return add;
        if (!has.Contains("IsDestructable") && !has.Contains("IsIndestructable")) add["IsDestructable"] = 1;
        if (has.Contains("IsUndamageable") || (!has.Contains("IsInstalled") && !has.Contains("IsSolid"))) return add;
        if (!has.Contains("IsDamageable")) add["IsDamageable"] = 1;
        return add;
    }

    /// <summary>This item's footprint <b>on an inventory grid</b>, in tiles — the def's
    /// <c>inventoryWidth</c>/<c>inventoryHeight</c> when set, else its map footprint. Drives the size of the
    /// block it occupies in a container's grid (the game's <c>GUIInventoryItem.GetWidthHeightForCO</c>).</summary>
    public (int W, int H) InvSize { get; init; } = (1, 1);

    /// <summary>Whether this item may be turned on an inventory grid. Sheet items (walls, floors) never rotate:
    /// the game's <c>Item.RotateCW</c> returns immediately for <c>bHasSpriteSheet</c> and the <c>fLastRotation</c>
    /// setter is guarded the same way, so a rotation authored for one would not survive a load. A square footprint
    /// has no second orientation to offer either.</summary>
    public bool CanRotateInInventory => !Item.HasSpriteSheet && InvSize.W != InvSize.H;

    /// <summary>If this part is a container, its inventory grid dimensions in tiles (the def's
    /// <c>nContainerWidth</c>×<c>nContainerHeight</c>, defaulting to 6×6 when it is a container but declares no
    /// grid — matching the game's <c>Container</c> default). Null when the part isn't a container.</summary>
    public (int W, int H)? ContainerGrid { get; init; }

    /// <summary>The item filter a container applies to what may be dropped in (<c>strContainerCT</c>), or null.</summary>
    public string? ContainerCT { get; init; }

    /// <summary>The def's default loot (<c>strLoot</c>), or null. Resolved into the parts of it Ostraplan models
    /// by <see cref="Catalog.IntrinsicContents"/>.</summary>
    public string? DefaultLoot { get; init; }

    /// <summary>Max stack size (<c>nStackLimit</c>); ≤1 = not stackable.</summary>
    public int StackLimit { get; init; }

    /// <summary>The named equipment slots this part provides (<c>aSlotsWeHave</c>) — for the paper-doll.</summary>
    public string[] SlotsWeHave { get; init; } = [];

    /// <summary>Paper-doll layout for this part's slots (slot name → on-figure (x, y) pixel offset), incl. a
    /// <c>"self"</c> host anchor. Empty when the part declares no layout.</summary>
    public IReadOnlyDictionary<string, (double X, double Y)> SlotLayout { get; init; } =
        new Dictionary<string, (double X, double Y)>();

    /// <summary>The slot name(s) this part occupies when equipped into a parent (its <c>mapSlotEffects</c> keys).</summary>
    public string[] SlotKeys { get; init; } = [];

    /// <summary>Where this part plugs <b>into</b> the conduit network — the map-point offsets named by its
    /// power-info's <c>aInputPts</c> (pixels around the item centre, +y up), resolved from the condowner's map
    /// points. Empty for a part that draws no power. Drawn as input connector nubs on the build cursor and used to
    /// decide whether a device is hooked up (its input tile sits on a live power path). See <see cref="PowerNetwork"/>.</summary>
    public IReadOnlyList<(double X, double Y)> PowerInputPoints { get; init; } = [];

    /// <summary>Where this part feeds the conduit network — its <c>PowerOutput</c> map point (pixels around the
    /// item centre, +y up), or null when it is not a power source. Generators/batteries/recharging containers
    /// carry one; the power flood starts here (<see cref="PowerNetwork"/>). Drawn as an output connector nub.</summary>
    public (double X, double Y)? PowerOutputPoint { get; init; }

    /// <summary>True when this part participates in the power network as a drawing device or a source (has any
    /// input point or an output point) — i.e. it should show connector nubs and take part in PowerViz.</summary>
    public bool IsPowered => PowerInputPoints.Count > 0 || PowerOutputPoint is not null;

    /// <summary>The interactions this part offers (its condowner's <c>aInteractions</c>, after any cooverlay
    /// substitutions), as names into <c>data/interactions</c>. Resolved to <see cref="InteractionDef"/>s per part by
    /// <see cref="Catalog.InteractionsFor"/>, which is what decides whether crew can reach and operate it (see
    /// <see cref="WalkNetwork"/>). Empty for inert structure (walls, floors, conduit).</summary>
    public string[] InteractionNames { get; init; } = [];

    /// <summary>True if this part holds an inventory grid (has a container grid).</summary>
    public bool IsContainer => ContainerGrid is not null;

    /// <summary>True when this part carries the game's <c>IsSignalable</c> starting cond — i.e. it owns an
    /// <c>Electrical</c> GPM and can be wired into a signal connection (alarms, pumps, sensors, lights, …). These
    /// are the endpoints the connection editor lets you wire together (see <see cref="DeviceLink"/>).</summary>
    public bool IsSignalable => Array.IndexOf(StartingConds, "IsSignalable") >= 0;

    /// <summary>
    /// True if this part came from a mod rather than the base game (its <see cref="Origin"/> is not core). Ostraplan's
    /// placement law is a port of the <b>core</b> game's logic, so it is authoritative for core parts but only
    /// best-effort for modded ones (a mod can add its own conditions or even code that changes what fits). This flag
    /// gates the "allow modded overrides" behaviour: a modded part flagged illegal is a <i>warning</i>, not a hard
    /// block, and may be placed anyway when the user opts in (see <see cref="AppSettings.AllowModdedOverrides"/>).
    /// </summary>
    public bool IsModded => Origin is not "core";
}

/// <summary>
/// The buildable catalog, derived the way the game builds its install menu:
/// data/installables entries with strJobType=="install" land in the tab named
/// by strBuildType, and place the item named by strStartInstall
/// (Installables.dictJobBuildOptions / GUIPDA.ShowJobOptions).
/// </summary>
public sealed class Catalog
{
    public static readonly string[] Categories = ["HULL", "HVAC", "POWR", "SENS", "CTRL", "FURN", "APPS", "MISC"];

    /// <summary>
    /// "Primary Exterior Airlock": every ship owns exactly one - IsIndestructable,
    /// no install job, never sold - so it resolves for documents but stays out of
    /// the palette. Ostraplan seeds it at the origin, locked.
    /// </summary>
    public const string PrimaryDocksysDef = "ItmDockSys02Closed";

    public required IReadOnlyList<PartDef> Parts { get; init; }
    public required IReadOnlyDictionary<string, PartDef> ByDefName { get; init; }
    public required IReadOnlyDictionary<string, LootDef> Loots { get; init; }
    public required IReadOnlyDictionary<string, CondTriggerDef> Triggers { get; init; }
    public required List<DataWarning> Warnings { get; init; }

    /// <summary>The effective data, kept so any <b>placed</b> def can be resolved on demand
    /// (imports reference far more than the buildable menu). Null in synthetic test catalogs.</summary>
    public DataIndex? Index { get; init; }

    private IReadOnlyDictionary<string, double>? _gasPrices;

    /// <summary>
    /// Gas prices in credits/kg from the <c>GasPrices</c> loot (<c>GasContainer.InitGasPrices</c> reads the
    /// same table: entries like "O2=1x13.2" → 13.2). Feeds the gas-contents part of a part's value
    /// (<see cref="ShipValue"/>). Empty in synthetic catalogs (no <see cref="Index"/>) or when a mod removes
    /// the table.
    /// </summary>
    public IReadOnlyDictionary<string, double> GasPrices => _gasPrices ??= ParseGasPrices();

    private IReadOnlyDictionary<string, double> ParseGasPrices()
    {
        var prices = new Dictionary<string, double>(StringComparer.Ordinal);
        if (Index is { } idx && idx.Type("loot").TryGetValue("GasPrices", out var raw))
            foreach (var entry in Json.StrArray(raw.El, "aCOs"))
            {
                var gas = LootDef.CondName(entry);
                if (gas.Length > 0) prices[gas] = LootDef.CondAmount(entry);
            }
        return prices;
    }

    private IReadOnlySet<string>? _declaredConds;

    /// <summary>
    /// Every condition name <c>data/conditions</c> declares. A condition the data does not declare cannot be
    /// put on anything: <c>CondOwner.AddCondAmount</c> gives up the moment <c>DataHandler.GetCond</c> returns
    /// null, with no error anywhere. That is what decides which gas species a container may actually be filled
    /// with (see <see cref="ContainerFill.Offerable"/>).
    /// <para>Empty in a synthetic catalog (no <see cref="Index"/>), which callers read as "unverifiable"
    /// rather than as "nothing is declared".</para>
    /// </summary>
    public IReadOnlySet<string> DeclaredConds =>
        _declaredConds ??= Index is { } idx
            ? new HashSet<string>(idx.Type("conditions").Keys, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    private IReadOnlyDictionary<string, string>? _condFriendly;

    /// <summary>A condition's own <c>strNameFriendly</c> ("Oxygen Gas", "Deuterium (Liquid)"), or null when the
    /// data gives it none. Lets the fill editor label its lines from the game's own words rather than a table
    /// Ostraplan would have to keep in step.</summary>
    public string? CondFriendly(string cond)
    {
        _condFriendly ??= Index is { } idx
            ? idx.Type("conditions")
                 .Select(kv => (kv.Key, Friendly: Json.Str(kv.Value.El, "strNameFriendly")))
                 .Where(x => !string.IsNullOrWhiteSpace(x.Friendly))
                 .ToDictionary(x => x.Key, x => x.Friendly!, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        return _condFriendly.GetValueOrDefault(cond);
    }

    private IReadOnlyDictionary<string, CondDisplayDef>? _condDisplay;

    /// <summary>
    /// The conditions the game puts on an object's mega tool tip as a figure — those declaring
    /// <c>nDisplayType == 1</c>, which its <c>NumberModule</c> is the whole of. Keyed by condition name.
    ///
    /// <para><b>There are four of them on stock data</b> (gas temperature and the three liquids), which is worth
    /// knowing before building anything on top: an ordinary crate's tool tip carries none, and a panel that fills
    /// itself with every <c>Stat*</c> the def declares is showing something the game does not.</para>
    ///
    /// <para>Empty in synthetic test catalogs.</para>
    /// </summary>
    public IReadOnlyDictionary<string, CondDisplayDef> CondDisplay =>
        _condDisplay ??= Index is { } idx
            ? idx.Type("conditions")
                 .Select(kv => CondDisplayDef.Parse(kv.Key, kv.Value.El))
                 .OfType<CondDisplayDef>()
                 .ToDictionary(d => d.Name, d => d, StringComparer.Ordinal)
            : new Dictionary<string, CondDisplayDef>(StringComparer.Ordinal);

    /// <summary>GUI-prop-map templates by name (from <c>data/guipropmaps</c>) — the <c>dictGUIPropMap</c> array
    /// each named map (e.g. "Electrical", "AirPump") expands to. Used to bake a new device's <c>aGPMSettings</c>
    /// so it wires up on load. Empty in synthetic test catalogs.</summary>
    public IReadOnlyDictionary<string, JsonElement> GpmTemplates { get; init; } = new Dictionary<string, JsonElement>();

    /// <summary>
    /// The <c>aGPMSettings</c> a freshly-built instance of <paramref name="part"/> would have: for each of the
    /// def's (instance, template) GUI-prop-map declarations, the template's <c>dictGUIPropMap</c>. Empty for a
    /// non-device part, or when a referenced template isn't loaded. This is what install produces and what the
    /// save load restores — a device injected without it loads unwired to power/panels.
    /// </summary>
    public IReadOnlyList<(string Instance, JsonElement Dict)> GpmSettingsFor(PartDef part)
    {
        var result = new List<(string, JsonElement)>();
        foreach (var (instance, template) in part.Gpm)
            if (GpmTemplates.TryGetValue(template, out var dict))
                result.Add((instance, dict));
        return result;
    }

    /// <summary>Ticker templates by name (from <c>data/tickers</c>) — the full <c>JsonTicker</c> (strCondLoot,
    /// fPeriod, bRepeat, …) each named ticker (e.g. "Power") expands to. Used to bake a device's <c>aTickers</c>
    /// so it works on load. Empty in synthetic test catalogs.</summary>
    public IReadOnlyDictionary<string, JsonElement> TickerTemplates { get; init; } = new Dictionary<string, JsonElement>();

    /// <summary>Equipment-slot metadata by slot name (from <c>data/slots</c>) — friendly name + icon for the
    /// inventory viewer's paper-doll. Empty in synthetic test catalogs.</summary>
    public IReadOnlyDictionary<string, SlotDef> Slots { get; init; } = new Dictionary<string, SlotDef>();

    /// <summary>Light definitions by name (from <c>data/lights</c>) — the <c>ptPos</c>/colour/radius each item's
    /// <c>aLights</c> entry expands to. Resolved per part by <see cref="LightsFor"/>. Empty in synthetic catalogs.</summary>
    public IReadOnlyDictionary<string, LightDef> LightDefs { get; init; } = new Dictionary<string, LightDef>();

    /// <summary>The RGBA colour table (from <c>data/colors</c>) — a light colour's alpha is its intensity. Used to
    /// resolve a <see cref="LightDef"/>'s colour into a <see cref="PartLight"/>. Empty in synthetic catalogs.</summary>
    public IReadOnlyDictionary<string, ColorDef> ColorTable { get; init; } = new Dictionary<string, ColorDef>();

    /// <summary>Parallax location definitions by name (from <c>data/parallax</c>) — each location's exterior sun
    /// lights for Light Viz's daylight simulation. Empty in synthetic catalogs.</summary>
    public IReadOnlyDictionary<string, ParallaxDef> ParallaxDefs { get; init; } = new Dictionary<string, ParallaxDef>();

    /// <summary>Interaction definitions by name (from <c>data/interactions</c>) — the target point and stand-off
    /// range each of a device's <c>aInteractions</c> entries expands to. Resolved per part by
    /// <see cref="InteractionsFor"/>. Empty in synthetic catalogs.</summary>
    public IReadOnlyDictionary<string, InteractionDef> InteractionDefs { get; init; } = new Dictionary<string, InteractionDef>();

    /// <summary>The ship-scale attacks the loaded data declares (<c>data/attackmodes/shipAttacks</c>), by name —
    /// the missiles, the mass driver, point-defence fire, the default explosion and the scuttling charge. Empty in
    /// synthetic test catalogs. See <see cref="ShipAttackDef"/> for why the coAttacks in the same folder are not
    /// here.</summary>
    public IReadOnlyDictionary<string, ShipAttackDef> ShipAttacks { get; init; } = new Dictionary<string, ShipAttackDef>();

    private readonly ConcurrentDictionary<string, IReadOnlyList<InteractionDef>> _interactions = new(StringComparer.Ordinal);

    /// <summary>
    /// The interactions a part offers that actually require a crew member to walk up to it: its
    /// <see cref="PartDef.InteractionNames"/> resolved through <see cref="InteractionDefs"/>, keeping only those
    /// with a real target point (<see cref="InteractionDef.NeedsApproach"/>). A remote or point-less interaction
    /// never constrains a layout, and an unresolved name (a mod removed the def) is skipped. Cached by def name;
    /// empty for inert structure or in a synthetic catalog with no interaction table.
    /// </summary>
    public IReadOnlyList<InteractionDef> InteractionsFor(PartDef part) =>
        _interactions.GetOrAdd(part.DefName, _ => part.InteractionNames
            .Select(n => InteractionDefs.GetValueOrDefault(n))
            .OfType<InteractionDef>()
            .Where(ia => ia.NeedsApproach)
            .ToArray());

    /// <summary>Loose cargo items — condowners typed <c>"Item"</c> that aren't installed structure (a wall is
    /// also <c>strType "Item"</c> but carries <c>IsInstalled</c>), plus the cooverlay skins of those items (themed
    /// loose walls/floors, and nav-console modules that exist only as cooverlays). This is the universe the
    /// inventory editor's add-picker draws from; a given container narrows it with <see cref="ContainerFilter"/>.
    /// Sorted by friendly name. Empty in synthetic test catalogs.</summary>
    public IReadOnlyList<PartDef> LooseItems { get; init; } = [];

    /// <summary>
    /// Installed structure the game places itself but offers no install job for — asteroid and ice cores,
    /// regolith walls, floor signs and emblems, station kiosks, terminals and transit lifts, station furniture.
    /// They resolve, render, and go through the placement law like any other part; they simply never reach the
    /// buildable tabs, which is why copying one out of a ship template used to be the only way to get one.
    /// <para>
    /// Two kinds of def are deliberately left out. A <b>runtime state</b> of a part the palette already offers
    /// (<see cref="StateOnlyConds"/>: a damaged or patched wall, a switched-off or locked device) is not a design
    /// choice, it is what happens to a design. And a def the data never named (<c>strNameFriendly</c> absent, so
    /// <see cref="PartDef.Friendly"/> falls back to the internal name) is a dev/test artefact rather than
    /// something a ship is built from.
    /// </para>
    /// Sorted by friendly name. Empty in synthetic test catalogs.
    /// </summary>
    public IReadOnlyList<PartDef> SpecialItems { get; init; } = [];

    /// <summary>Conds that mark a placed def as a runtime <b>state</b> of some other part rather than a part in
    /// its own right. The game reaches these through damage, repair, and power toggles, never through a build
    /// job, so they would otherwise all pile into <see cref="SpecialItems"/> as near-duplicates of the buildable
    /// entry they came from.</summary>
    private static readonly HashSet<string> StateOnlyConds =
        new(StringComparer.Ordinal) { "IsDamaged", "IsPatched", "IsOff", "IsLocked" };

    /// <summary><c>strProgressStat</c> of the repair jobs that rebuild a broken def into a working one, as opposed
    /// to the undamage jobs that file under the same <c>strJobType</c> and only grind off wear. See
    /// <see cref="RepairForms"/>.</summary>
    private const string RepairProgressStat = "StatRepairProgress";

    /// <summary>Installed def → its loose/packaged form, from the game's own <c>uninstall</c> jobs (the job's
    /// <c>strActionCO</c> → <c>aLootCOs</c>, or <c>strLootOut</c>, or — when both name only a runtime marker with
    /// no condowner, as the Nav Station and Transponder families do — the inverse of the matching <c>install</c>
    /// job). This is what "Make Loose Item" swaps to; only entries whose loose form resolves to real geometry are
    /// kept, so a swap never lands on an unrenderable def. A def with no uninstall job (raw hull, the fixed
    /// airlock, scrap-only structure) is simply absent — the action isn't offered. Empty in synthetic test
    /// catalogs.</summary>
    public IReadOnlyDictionary<string, string> LooseForms { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Loose def → its installed form, from the game's own <c>install</c> jobs (the job's
    /// <c>strActionCO</c> → <c>strStartInstall</c>) — the inverse of <see cref="LooseForms"/>, what "Install item"
    /// swaps to. Only resolvable targets are kept. Empty in synthetic test catalogs.</summary>
    public IReadOnlyDictionary<string, string> InstalledForms { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The loose/packaged counterpart of an installed part's def (its uninstall job's loot), or null if it
    /// has none. Mirror of <see cref="InstalledForm"/>; both key on the placed def, like <see cref="DoorToggle"/>.</summary>
    public string? LooseForm(string defName) => LooseForms.GetValueOrDefault(defName);

    /// <summary>The installed counterpart of a loose part's def (its install job's <c>strStartInstall</c>), or null
    /// if it has none.</summary>
    public string? InstalledForm(string defName) => InstalledForms.GetValueOrDefault(defName);

    /// <summary>
    /// Broken def → the working def repairing it yields, from the game's own <b>repair</b> jobs (the job's
    /// <c>strActionCO</c> → <c>aLootCOs</c>, or <c>strLootOut</c>). This is what "Repair" swaps to.
    ///
    /// <para>The game files two different jobs under <c>strJobType: "repair"</c> and only one of them belongs here.
    /// An <i>undamage</i> job (<c>strProgressStat: StatDamage</c>, 503 of them on stock 1.0.0.9) grinds accumulated
    /// wear off a part and hands the <b>same def</b> back — that is <c>StatDamage</c>, which lives on a save's
    /// condition owners and not in a design at all (see <see cref="WearOptions"/>). A <i>repair</i> job
    /// (<c>strProgressStat: StatRepairProgress</c>, 252 of them) rebuilds a part that is broken <b>as a def</b> — a
    /// damaged wall, a wrecked alarm — into its working counterpart, which is a real change of what is on the tile.
    /// Keying on the progress stat rather than the job type is what keeps the twelve undamage jobs whose loot does
    /// differ (<c>ItmDoor01ClosedOnLocked</c> → <c>ItmDoor01Closed</c>) from turning a repair into a silent unlock,
    /// and drops the 15 dev-only <c>reset</c>/<c>StatDebugProgress</c> entries with it.</para>
    ///
    /// <para>Targets run through <see cref="PreferPoweredState"/> like an install's do: the game's repair jobs hand
    /// back the <c>…Off</c> state (<c>ItmAlarmSmokeDmg</c> → <c>ItmAlarmSmokeOff</c>), which the ship rating never
    /// counts, and a design whose repaired devices are all switched off is not what "repair" means to anyone. Only
    /// entries whose target resolves to real geometry are kept. Empty in synthetic test catalogs.</para>
    /// </summary>
    public IReadOnlyDictionary<string, string> RepairForms { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The working counterpart of a broken part's def (its repair job's loot), or null if it has none —
    /// which is the normal answer for a part that is not broken in the first place.</summary>
    public string? RepairForm(string defName) => RepairForms.GetValueOrDefault(defName);

    /// <summary>
    /// Working def → the def it becomes when its damage pool fills, from the condowner's own <c>Destructable</c>
    /// declaration (<see cref="CondOwnerDef.BreakLoot"/>) rather than from any job. <c>ItmWall1x1</c> →
    /// <c>ItmWall1x1Dmg</c> → destroyed. Empty in synthetic test catalogs.
    ///
    /// <para>This is <b>not</b> the inverse of <see cref="RepairForms"/> and must not be derived from it. Repair is
    /// built from <c>data/installables</c> jobs and answers "what does fixing this yield"; damage is declared on the
    /// condowner and answers "what does breaking this yield". The two disagree in both directions — a repair job
    /// normalises a door's lock and power state (§12), and a break can hand back a def no repair job restores — so
    /// inverting either one produces a plausible table that is wrong in exactly the cases that matter.</para>
    ///
    /// <para>Used by <see cref="MaxHealth"/> and by the damage solvers (§26). Unlike an install or a repair the
    /// target is <b>not</b> run through <see cref="PreferPoweredState"/>: a part that breaks does not come back
    /// switched on.</para>
    /// </summary>
    public IReadOnlyDictionary<string, string> BreakForms { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The def this part becomes when its own damage pool fills, or null when it breaks into nothing the
    /// game names — which is what "destroyed" means, and is also the answer for an indestructible part.</summary>
    public string? BreakForm(string defName) => BreakForms.GetValueOrDefault(defName);

    /// <summary>True when the def carries a damage pool at all (<c>StatDamageMax</c>). A part without one absorbs
    /// nothing and a strike passes straight through it.</summary>
    public bool IsDestructable(string defName) => Lookup(defName)?.StartingCondValues.ContainsKey("StatDamageMax") is true;

    /// <summary>
    /// This form's own damage pool — its <c>StatDamageMax</c> (the game's <c>DataCO.Health</c>), 0 when it declares
    /// none. Filling it is what breaks the part into <see cref="BreakForm"/>; it is <b>not</b> what destroys it,
    /// for which see <see cref="MaxHealth"/>.
    /// </summary>
    public double Health(string defName) => Lookup(defName)?.StartingCondValues.GetValueOrDefault("StatDamageMax") ?? 0;

    /// <summary>
    /// The damage this part absorbs before it is gone: its own <see cref="Health"/> plus, recursively, that of
    /// every form it breaks through. A port of <c>DataCO.GetMaxHealth</c> including its fallbacks — a def that is
    /// not <c>IsInstalled</c>, declares no break, or whose break does not resolve, is worth its own pool alone.
    ///
    /// <para><c>ItmWall1x1</c> is 15 to damage and 45 to destroy; <c>ItmDoor01Closed</c> is 20 and 80. 62% of
    /// installed damageable parts have a chain longer than one stage, so reading only <see cref="Health"/>
    /// understates a ship's resilience badly.</para>
    ///
    /// <para>Memoised, and cycle-guarded — the game's own walk carries a recursion counter it increments but never
    /// tests, so a data cycle would hang it where this returns the pool accumulated so far.</para>
    /// </summary>
    public double MaxHealth(string defName) => _maxHealth.GetOrAdd(defName, d => MaxHealthWalk(d, []));

    private readonly ConcurrentDictionary<string, double> _maxHealth = new(StringComparer.Ordinal);

    private double MaxHealthWalk(string defName, HashSet<string> seen)
    {
        var own = Health(defName);
        if (!seen.Add(defName)) return own;
        // The game only chains for an installed def; a loose or packaged form is worth its own pool alone.
        if (Lookup(defName)?.StartingConds.Contains("IsInstalled") is not true) return own;
        return BreakForms.GetValueOrDefault(defName) is { } next ? own + MaxHealthWalk(next, seen) : own;
    }

    /// <summary>The ticker templates a freshly-installed instance of <paramref name="part"/> would carry: each of
    /// the def's declared <c>aTickers</c> names resolved to its template. Empty for a non-device part, or when a
    /// referenced ticker isn't loaded.</summary>
    public IReadOnlyList<(string Name, JsonElement Template)> TickersFor(PartDef part)
    {
        var result = new List<(string, JsonElement)>();
        foreach (var name in part.TickerNames)
            if (TickerTemplates.TryGetValue(name, out var tmpl))
                result.Add((name, tmpl));
        return result;
    }

    private readonly ConcurrentDictionary<string, PartDef?> _placed = new(StringComparer.Ordinal);

    /// <summary>
    /// Resolve a placed def to its <see cref="PartDef"/> — a buildable palette entry if it is one,
    /// otherwise resolved on demand from the effective data (raw hull, ship-special systems, tiles:
    /// the non-buildable defs a template/save references). Non-buildable defs get category "—" and
    /// stay out of the palette; they still render and analyse. Null only if no geometry exists at all
    /// (a def whose mod isn't loaded). Cached; safe across threads.
    /// </summary>
    public PartDef? Lookup(string? defName)
    {
        if (string.IsNullOrEmpty(defName)) return null;
        if (ByDefName.TryGetValue(defName, out var built)) return built;
        return Index is null ? null : _placed.GetOrAdd(defName, d => ResolveDef(Index, d, "—", "imported", [], []));
    }

    /// <summary>
    /// A loot's condition names — its own aCOs plus, recursively, nested aLoots,
    /// flattened. This is Loot.GetLootNames for the deterministic "Cond=1.0x1"
    /// units socket masks use, and the same expansion <see cref="TileConds"/> runs
    /// when accumulating. "Blank"/empty/unresolved → none (an unconstrained cell).
    /// Recomputed per call (expansions are shallow); no cache, so it's safe to
    /// share one Catalog across threads.
    /// </summary>
    public IReadOnlyList<string> LootConds(string? lootName)
    {
        var acc = new List<string>();
        if (!string.IsNullOrEmpty(lootName) && lootName != "Blank")
            GatherConds(lootName, acc, []);
        return acc;
    }

    private void GatherConds(string name, List<string> acc, HashSet<string> visited)
    {
        if (!visited.Add(name) || !Loots.TryGetValue(name, out var loot)) return;
        acc.AddRange(loot.Conds);
        foreach (var child in loot.Loots) GatherConds(child, acc, visited);
    }

    // What KIND of deck element a part is, from what it contributes to its tiles. This is a classification, not
    // the draw order: the draw order is the game's own fZScale (<see cref="ItemDef.ZScale"/> ->
    // <see cref="ShipDocument.RenderOrder"/>), and the two deliberately disagree — the game puts walls at 1.0,
    // above most fixtures, while a wall is still "structure" here whatever it draws over. What reads these is the
    // swap classing (<see cref="SwapClass"/>), the Surfaces focus, and the right-click layer filter.
    public const int LayerFloor = 0;      // IsFloor / IsFloorSealed / IsFloorFlex
    public const int LayerWall = 1;       // IsWall / IsPortal (doors carry both wall and floor conds — checked first)
    public const int LayerFixture = 2;    // beds, appliances, sensors, and everything unclassified
    public const int LayerConduit = 3;    // IsPowerConduit — thin power runs

    // Automatic rank below the z-scale and the manual bias, so it only ever separates two defs the game gave the
    // same fZScale. Canisters slot into and against the machinery they feed (a gas canister on an RCS regulator's
    // GasInput point shares its row, and the two can share a z-scale), so they draw under it; loose deck clutter
    // draws over both, which is where a dropped item reads as lying on the floor rather than being part of the
    // fixture.
    public const int RankVessel = 0;      // gas / fuel canisters and RTAs (the game's own TIsVessel set)
    public const int RankInstalled = 1;   // every other placed part
    public const int RankLoose = 2;       // loose floor items (see LooseObject)

    /// <summary>The game's own definition of a canister, <c>TIsVessel</c>: an OR over <c>IsVessel01</c> /
    /// <c>IsVesselH2</c> / <c>IsVesselHe</c> / <c>IsVesselHe3</c> / <c>IsVesselCO2</c> / <c>IsVesselO2</c> /
    /// <c>IsVesselN2</c>. Used only when the trigger itself is missing from the data (a synthetic catalog).</summary>
    private static readonly HashSet<string> VesselConds = new(StringComparer.Ordinal)
    {
        "IsVessel01", "IsVesselH2", "IsVesselHe", "IsVesselHe3", "IsVesselCO2", "IsVesselO2", "IsVesselN2",
    };

    private readonly ConcurrentDictionary<string, bool> _isVessel = new(StringComparer.Ordinal);

    /// <summary>
    /// True when a part is a gas or fuel canister — evaluated through the game's <c>TIsVessel</c> trigger against
    /// the part's starting conds, so a modded canister that declares a vessel cond is caught without naming it.
    /// Drives <see cref="RankVessel"/>, which is why it reads the trigger rather than a def-name list.
    /// </summary>
    public bool IsVessel(PartDef? part)
    {
        if (part is null) return false;
        return _isVessel.GetOrAdd(part.DefName, _ =>
            Triggers.TryGetValue("TIsVessel", out var ct)
                ? CondEval.Triggered(ct, part.StartingConds, this)
                : part.StartingConds.Any(VesselConds.Contains));
    }

    private readonly ConcurrentDictionary<string, IReadOnlyList<(string DefName, int Count)>> _intrinsic =
        new(StringComparer.Ordinal);

    /// <summary>
    /// The containers a def spawns with as part of ITSELF: its <c>strLoot</c> expanded through nested loots,
    /// keeping only children that are containers in their own right. These are the object's anatomy, not its
    /// stock — a pair of coveralls declares no container grid and gets its capacity entirely from here
    /// (<c>OutfitSuit02</c> → <c>ItmPocketsCoverallsx2</c> → 2× <c>PocketHip01</c>), and a backpack's four
    /// <c>PocketPouchSmall01</c> pouches work the same way.
    ///
    /// <para><b>Why only the container children.</b> The same <c>strLoot</c> field also carries genuine stock: a
    /// Coilgun declares 15× <c>ItmAmmo150mm</c>, and a body part declares its wounds. Materialising those would
    /// hand the user ammo they never authored and move the bill of materials, so the container test draws the
    /// line without needing a def-name list. Pockets are containers; ammo and wounds are not.</para>
    ///
    /// <para>Without this a garment written into a save arrived with no pockets at all and was permanently
    /// useless: Ostraplan synthesises a contained item straight from its def, and a save-loaded item is restored
    /// as recorded rather than respawned, so nothing ever ran the loot.</para>
    /// </summary>
    public IReadOnlyList<(string DefName, int Count)> IntrinsicContents(PartDef? part)
    {
        if (part?.DefaultLoot is not { Length: > 0 } root) return [];
        return _intrinsic.GetOrAdd(part.DefName, _ =>
        {
            var found = new List<(string, int)>();
            Walk(root, new HashSet<string>(StringComparer.Ordinal), found);
            return found;
        });

        void Walk(string lootName, HashSet<string> seen, List<(string, int)> into)
        {
            if (!seen.Add(lootName) || !Loots.TryGetValue(lootName, out var loot)) return;   // loot graphs can nest
            foreach (var (def, count) in loot.Items)
                if (Lookup(def) is { IsContainer: true }) into.Add((def, count));
            foreach (var child in loot.Loots) Walk(child, seen, into);
        }
    }

    private readonly ConcurrentDictionary<string, bool> _isPrimaryPort = new(StringComparer.Ordinal);

    /// <summary>
    /// True when a part is a <b>primary-class</b> docking port: the game's <c>TIsDockSysInstalled</c> trigger fires
    /// and the part is not <c>IsTypeB</c>. This is the port <c>Ship.AddCO</c> files with <c>aDocksys.Insert(0, …)</c>,
    /// so it leads <c>aDocksys</c> and bounds construction (see <see cref="ProblemScan.BoundingPort"/>); every
    /// Secondary is TypeB and is freely placeable.
    ///
    /// <para>Read the <b>conditions</b>, never the def name. <see cref="PrimaryDocksysDef"/> is only the variant a
    /// new design is seeded with; the same port has other states, and a save carries whichever one the ship is
    /// actually in. A player who pries their airlock open has <c>ItmDockSys02Open</c>, and matching on
    /// <c>ItmDockSys02Closed</c> alone made Ostraplan treat that ship as having no primary port at all: it stopped
    /// being fixed against edits, the export tagged no port as primary, and reopening the design seeded a SECOND
    /// airlock at the origin, which moved the written grid frame and broke docking.</para>
    /// </summary>
    public bool IsPrimaryDocksys(PartDef? part)
    {
        if (part is null) return false;
        return _isPrimaryPort.GetOrAdd(part.DefName, _ =>
            Triggers.ContainsKey(ProblemScan.DocksysTrigger)
                ? ProblemScan.IsDocksys(part, this) && !part.StartingConds.Contains(ProblemScan.TypeBCond)
                // No trigger in the data means a synthetic catalog (Fixtures), which carries no condtrigs; fall
                // back to the seeded def so those tests still see a fixed port. Same shape as IsVessel's fallback.
                : part.DefName == PrimaryDocksysDef);
    }

    private static readonly HashSet<string> WallConds = new(StringComparer.Ordinal) { "IsWall", "IsPortal" };
    private static readonly HashSet<string> FloorConds = new(StringComparer.Ordinal) { "IsFloor", "IsFloorSealed", "IsFloorFlex" };
    private readonly ConcurrentDictionary<string, int> _renderLayer = new(StringComparer.Ordinal);

    /// <summary>The kind of deck element a part is, from the conditions its sockets add (memoized by def name).
    /// Not the draw order — see the constants above.</summary>
    public int RenderLayer(PartDef? part)
    {
        if (part is null) return LayerFixture;
        return _renderLayer.GetOrAdd(part.DefName, _ =>
        {
            var conds = part.Item.SocketAdds.SelectMany(LootConds).ToHashSet(StringComparer.Ordinal);
            if (conds.Overlaps(WallConds)) return LayerWall;        // walls & doors (a door also seals floor — resolve here)
            if (conds.Overlaps(FloorConds)) return LayerFloor;
            if (conds.Contains("IsPowerConduit")) return LayerConduit;
            return LayerFixture;
        });
    }

    private readonly ConcurrentDictionary<(string Def, int Rot), (int X, int Y, int W, int H)> _bodyBox = new();

    /// <summary>
    /// The part's ABOVE-FLOOR body within its own footprint at <paramref name="rot"/>: the bounding rect of every
    /// cell that is not under-floor-only reservation (<see cref="IsUnderFloorLoot"/>), as an offset from the
    /// footprint's top-left plus its size. Ordinary parts have no such ring, so this is <c>(0, 0, W, H)</c> — the
    /// whole footprint. The big cryogenic canisters do: a 3×3 body sitting at <c>(2, 2)</c> inside a 7×7 socket
    /// grid whose outer two rings only reserve sub-floor. Sprite-sheet parts (walls/floors, which auto-tile
    /// rather than rotate) are read unrotated, matching how they are placed.
    /// <para>This is what the user actually sees and grabs — selection, hit-testing and the outline all work off
    /// it (<see cref="ShipDocument.BodyBounds"/>) — so it is also what decides swap compatibility
    /// (<see cref="SwapClass"/>). The placement law is unaffected: <see cref="CheckFit"/> keeps reading the full
    /// socket grid.</para>
    /// </summary>
    public (int X, int Y, int W, int H) BodyBox(PartDef? part, int rot = 0)
    {
        if (part is null) return (0, 0, 1, 1);
        var effRot = part.Item.HasSpriteSheet ? 0 : GridMath.Norm(rot);
        return _bodyBox.GetOrAdd((part.DefName, effRot), _ =>
        {
            var (w, h) = GridMath.Size(part.Item.Width, part.Item.Height, effRot);
            var (rw, rh, adds) = GridMath.Rotate(part.Item.SocketAdds, part.Item.Width, part.Item.Height, effRot);
            if (adds.Length != rw * rh) return (0, 0, w, h);

            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            for (var r = 0; r < rh; r++)
                for (var c = 0; c < rw; c++)
                    if (!IsUnderFloorLoot(adds[r * rw + c]))
                    {
                        minX = Math.Min(minX, c); minY = Math.Min(minY, r);
                        maxX = Math.Max(maxX, c); maxY = Math.Max(maxY, r);
                    }
            // an all-under-floor part has no body to speak of: treat the whole footprint as one
            return maxX < minX ? (0, 0, w, h) : (minX, minY, maxX - minX + 1, maxY - minY + 1);
        });
    }

    /// <summary>
    /// The swap class a part belongs to: its render layer plus the size of its above-floor <see cref="BodyBox"/>.
    /// Two parts of one class occupy the same visible tiles, so one can stand in for the other — what
    /// <see cref="ReplaceOps"/>, <see cref="ThemeOps"/> and <see cref="SurfacePaint"/> all key on.
    /// <para>Keyed on the <b>body</b> rather than the raw socket grid because that is the object the user sees:
    /// a Liq. He Canister is a 3×3 machine with a two-tile sub-floor apron, and classing it 7×7 left it swappable
    /// only with the two other canisters that share the apron, while the visibly identical 3×3 tanks and reactors
    /// were unreachable.</para>
    /// </summary>
    public (int Layer, int W, int H) SwapClass(PartDef part)
    {
        var body = BodyBox(part);
        return (RenderLayer(part), body.W, body.H);
    }

    private readonly ConcurrentDictionary<string, IReadOnlyList<PartLight>> _lights = new(StringComparer.Ordinal);

    /// <summary>
    /// The lights a part emits: its item def's <c>aLights</c> resolved through the light + colour tables into
    /// <see cref="PartLight"/>s (pixel offset, RGB + intensity, radius, casts-vs-glow-only, glow sprite). This is
    /// the data <see cref="LightNetwork"/> floods from; an unresolved light name (a mod removed the def) is
    /// skipped. Cached by def name; empty for a part with no lights or in a synthetic catalog (no light table).
    /// </summary>
    public IReadOnlyList<PartLight> LightsFor(PartDef part) =>
        _lights.GetOrAdd(part.DefName, _ => ResolveLights(part));

    private IReadOnlyList<PartLight> ResolveLights(PartDef part)
    {
        if (part.Item.Lights.Length == 0) return [];
        var lights = new List<PartLight>(part.Item.Lights.Length);
        foreach (var name in part.Item.Lights)
        {
            if (!LightDefs.TryGetValue(name, out var ld)) continue;
            byte r = 0, g = 0, b = 0;
            var intensity = 0.0;
            if (ColorTable.TryGetValue(ld.Color, out var c))
                (r, g, b, intensity) = (c.R, c.G, c.B, c.A / 255.0);
            var glow = !string.IsNullOrEmpty(ld.Img) ? Index?.ResolveImage(ld.Img) : null;
            lights.Add(new PartLight(ld.PosX, ld.PosY, r, g, b, intensity, ld.Radius, ld.Color != "Blank", glow, ld.CanBlink));
        }
        return lights;
    }

    /// <summary>
    /// The open/closed counterpart of a door placement's def, or null if this isn't a
    /// toggleable door (both ends resolve via <see cref="ByDefName"/>). Door state is
    /// cosmetic to the room/airtightness/rating law: the door's wall cells seal each side
    /// of the hull the same open or closed, so CreateRooms gives the same room count and
    /// airtightness either way. This swaps only the sprite and name, never that analysis.
    ///
    /// <para>It is <b>not</b> cosmetic to <see cref="WalkNetwork"/>, though: a closed door that
    /// is unpowered, locked or damaged carries <c>IsPortalStuck</c> and is a solid wall to crew
    /// pathing, so toggling one shut can seal a section off. That is the game's behaviour, not a
    /// modelling choice.</para>
    /// </summary>
    public string? DoorToggle(string defName)
    {
        if (!ByDefName.TryGetValue(defName, out var self) || !self.StartingConds.Contains("IsPortal")) return null;
        var peer =
            defName.Contains("Open") ? ReplaceLast(defName, "Open", "Closed") :
            defName.Contains("Closed") ? ReplaceLast(defName, "Closed", "Open") :
            null;
        return peer is not null && ByDefName.ContainsKey(peer) ? peer : null;
    }

    private readonly ConcurrentDictionary<string, string?> _powerToggle = new(StringComparer.Ordinal);

    /// <summary>
    /// The power-state counterpart of a placed def: a switched-off device's <b>nominal</b> switched-on form, or a
    /// switched-on device's off form. Null when the part has no power state, or when its on-state is ambiguous.
    ///
    /// <para>The game installs powered fixtures in their <c>Off</c> state and
    /// <see cref="PreferPoweredState"/> builds the On one instead, but only when it can name it: a device whose On
    /// form is a colour variant (the Transponder's <c>…OnR</c>) falls through and is placed Off, with no way to
    /// switch it on. This is that way.</para>
    ///
    /// <para><b>Nominal, never an alert state.</b> Six alarms carry several on-states because the colour <i>is</i>
    /// the alarm level (CO2 has Green/Yellow/Red, the thermostat Blue/White/Red). Offering those as design choices
    /// would let a ship be authored mid-alarm, so only the nominal one is reachable. It is identified from the data
    /// rather than a colour list: the alert states qualify their friendly name ("O2 Pressure Alarm (Alert)") and the
    /// nominal one does not, which picks <c>OnG</c> for all five gas alarms and <c>OnW</c> for the thermostat, and
    /// keeps working for a modded alarm that invents its own colours.</para>
    ///
    /// <para>Setting a nominal alarm cannot lie to the player, because the game overrules it: the on-states carry a
    /// <c>GasPressureSense</c> / <c>Sensor</c> update command and a sense ticker (the off state carries neither),
    /// which read the real conditions at the sensor's map point every tick and queue the interaction that swaps the
    /// state. An O2 alarm authored Green aboard a ship in vacuum is flipped to Red by its own sensor. See
    /// GAME-INTERNALS §12.</para>
    /// </summary>
    public string? PowerToggle(string defName) => _powerToggle.GetOrAdd(defName, ComputePowerToggle);

    private string? ComputePowerToggle(string defName)
    {
        // Lookup, not ByDefName: the palette holds the On form of anything PreferPoweredState could name, so the
        // Off states this walks are mostly absent until something is placed. Lookup resolves any def on demand.
        if (Lookup(defName) is not { } self || !self.StartingConds.Contains("IsInstalled")) return null;

        // switched on -> off. The on form may be "…On", a colour variant ("…OnR"), or the bare stem, so try
        // dropping an On suffix as well as simply appending Off.
        if (!self.StartingConds.Contains("IsOff"))
        {
            var stem = OnStem(defName);
            foreach (var candidate in stem is null ? [defName + "Off"] : new[] { stem + "Off", defName + "Off" })
                if (Lookup(candidate) is { } off
                    && off.StartingConds.Contains("IsOff") && SameFootprint(self, off))
                    return candidate;
            return null;
        }

        // switched off -> nominal on. The candidate names come from the condowner data rather than a hardcoded
        // colour list, so a modded alarm that invents its own suffix is handled like any other.
        if (!defName.EndsWith("Off", StringComparison.Ordinal) || Index is null) return null;
        var offStem = defName[..^3];

        var on = new List<PartDef>();
        foreach (var name in Index.Type("condowners").Keys)
        {
            if (name == defName || !name.StartsWith(offStem, StringComparison.Ordinal)) continue;
            if (!IsOnSuffix(name[offStem.Length..])) continue;
            // a bare item with no condowner is never a real state (see PreferPoweredState)
            if (Lookup(name) is not { } def || def.StartingConds.Length == 0) continue;
            if (def.StartingConds.Contains("IsOff") || !SameFootprint(self, def)) continue;
            on.Add(def);
        }

        if (on.Count == 1) return on[0].DefName;
        // several states: only the unqualified (nominal) one is offered, and only when it is unambiguous.
        var nominal = on.Where(d => !d.Friendly.Contains('(')).ToList();
        return nominal.Count == 1 ? nominal[0].DefName : null;
    }

    /// <summary>The stem of a switched-on def name, dropping a trailing <c>On</c> or colour-suffixed <c>OnX</c>.
    /// Null when the name carries no such suffix (a device whose on-state is the bare stem).</summary>
    private static string? OnStem(string defName)
    {
        if (defName.EndsWith("On", StringComparison.Ordinal)) return defName[..^2];
        return defName.Length > 3 && defName[^3] == 'O' && defName[^2] == 'n' && char.IsAsciiLetterUpper(defName[^1])
            ? defName[..^3]
            : null;
    }

    /// <summary>Is this the remainder of an on-state name after its off form's stem — empty, <c>On</c>, or a
    /// single-letter colour variant like <c>OnG</c>?</summary>
    private static bool IsOnSuffix(string rest) =>
        rest.Length == 0
        || (rest.Length == 2 && rest is "On")
        || (rest.Length == 3 && rest[0] == 'O' && rest[1] == 'n' && char.IsAsciiLetterUpper(rest[2]));

    /// <summary>Two states of one device occupy the same tiles; anything else is a different part.</summary>
    private static bool SameFootprint(PartDef a, PartDef b) =>
        a.Item.Width == b.Item.Width && a.Item.Height == b.Item.Height;

    /// <summary>Replace the last occurrence of <paramref name="find"/>, or null if absent.</summary>
    private static string? ReplaceLast(string s, string find, string repl)
    {
        var i = s.LastIndexOf(find, StringComparison.Ordinal);
        return i < 0 ? null : string.Concat(s.AsSpan(0, i), repl, s.AsSpan(i + find.Length));
    }

    /// <summary>
    /// True if a socket-add loot marks a tile as <b>under-floor</b> reservation —
    /// sub-floor storage projected beneath walkable floor (IsSubTile) with no solid
    /// body there (no IsObstruction). The large tanks lay TIL2DeckAdds (obstruction,
    /// the visible 3x3 tank) over a TILSubfloorAdds ring (IsSubTile only), so the ring
    /// is under-floor and the core is above-floor — the distinction the build ghost draws.
    /// </summary>
    public bool IsUnderFloorLoot(string? lootName)
    {
        var conds = LootConds(lootName);
        return conds.Contains("IsSubTile") && !conds.Contains("IsObstruction");
    }

    public static Catalog Build(DataIndex index)
    {
        var warnings = new List<DataWarning>();
        // Attributed to the source the def came from, so a defect in the game's own data can be told apart from one
        // in a mod. See DataWarning: only the latter is worth putting in front of a user.
        void Warn(string origin, string message) =>
            warnings.Add(new DataWarning(origin, message, index.IsCoreSource(origin)));

        var loots = index.Type("loot").ToDictionary(kv => kv.Key, kv => LootDef.Parse(kv.Value.El), StringComparer.Ordinal);
        var trigs = index.Type("condtrigs").ToDictionary(kv => kv.Key, kv => CondTriggerDef.Parse(kv.Value.El), StringComparer.Ordinal);
        var gpmTemplates = index.Type("guipropmaps")
            .Where(kv => kv.Value.El is { ValueKind: JsonValueKind.Object } el && el.TryGetProperty("dictGUIPropMap", out _))
            .ToDictionary(kv => kv.Key, kv => kv.Value.El.GetProperty("dictGUIPropMap").Clone(), StringComparer.Ordinal);
        var tickerTemplates = index.Type("tickers")
            .Where(kv => kv.Value.El.ValueKind == JsonValueKind.Object)
            .ToDictionary(kv => kv.Key, kv => kv.Value.El.Clone(), StringComparer.Ordinal);
        var slots = index.Type("slots").ToDictionary(kv => kv.Key, kv => SlotDef.Parse(kv.Value.El), StringComparer.Ordinal);
        var lightDefs = index.Type("lights").ToDictionary(kv => kv.Key, kv => LightDef.Parse(kv.Value.El), StringComparer.Ordinal);
        var colorTable = index.Type("colors").ToDictionary(kv => kv.Key, kv => ColorDef.Parse(kv.Value.El), StringComparer.Ordinal);
        var parallaxDefs = index.Type("parallax").ToDictionary(kv => kv.Key, kv => ParallaxDef.Parse(kv.Value.El), StringComparer.Ordinal);
        var interactionDefs = index.Type("interactions").ToDictionary(kv => kv.Key, kv => InteractionDef.Parse(kv.Value.El), StringComparer.Ordinal);
        var shipAttacks = index.Type("attackmodes")
            .Select(kv => ShipAttackDef.Parse(kv.Value.El))
            .OfType<ShipAttackDef>()
            .Where(a => a.Name.Length > 0)
            .ToDictionary(a => a.Name, StringComparer.Ordinal);

        // Loose cargo items for the inventory editor's add-picker: condowners typed "Item" minus installed
        // structure (a wall is strType "Item" too but carries IsInstalled), PLUS the cooverlay skins of those
        // items. A themed loose wall/floor (ItmFloorAERO01Loose, ...) is a cooverlay whose strCOBase is a loose
        // base condowner, and a nav-console module (ItmNavModControls, ...) exists ONLY as a cooverlay — the game
        // offers every one of them, so enumerating condowners alone left the picker with a single generic
        // "Floor (Loose)" and no real nav modules. A cooverlay is type-checked through its base condowner (the skin
        // itself has no strType). Narrowed per container by ContainerFilter, so a broad universe is safe.
        //
        // The same sweep also collects every INSTALLED def it meets, because the non-buildable ones among them
        // are what SpecialItems is made of. Which those are can only be decided once the buildable menu exists,
        // so the split happens further down; resolving each def is the expensive half and this way it happens
        // once.
        var owners = index.Type("condowners");
        var looseItems = new List<PartDef>();
        var installedItems = new List<PartDef>();
        var itemSeen = new HashSet<string>(StringComparer.Ordinal);
        void AddItem(string defName, string origin, bool typeIsItem)
        {
            if (!typeIsItem || !itemSeen.Add(defName)) return;
            if (ResolveDef(index, defName, "—", origin, [], []) is not { } it) return;
            (it.StartingConds.Contains("IsInstalled") ? installedItems : looseItems).Add(it);
        }
        foreach (var (name, (el, origin)) in owners)
            AddItem(name, origin, Json.Str(el, "strType") == "Item");
        foreach (var (name, (el, origin)) in index.Type("cooverlays"))
        {
            var baseName = Json.Str(el, "strCOBase");
            AddItem(name, origin, !string.IsNullOrEmpty(baseName)
                && owners.TryGetValue(baseName, out var baseCo)
                && Json.Str(baseCo.El, "strType") == "Item");
        }
        looseItems.Sort((a, b) => string.Compare(a.Friendly, b.Friendly, StringComparison.OrdinalIgnoreCase));

        // Installed⇄loose form maps for "Make Loose Item" / "Install item", straight from the game's own jobs: an
        // uninstall job maps the fixture it removes (strActionCO) → the loose form it drops (aLootCOs); an install
        // job maps the loose form (strActionCO) → the installed form it builds (strStartInstall). Keep only entries
        // whose target resolves to real geometry, so a swap can't land on an unrenderable def.
        var looseForms = new Dictionary<string, string>(StringComparer.Ordinal);
        var installedForms = new Dictionary<string, string>(StringComparer.Ordinal);
        var repairForms = new Dictionary<string, string>(StringComparer.Ordinal);
        var uninstallable = new HashSet<string>(StringComparer.Ordinal);   // installed defs the game offers an uninstall job for
        bool Resolvable(string n) => ResolveDef(index, n, "—", "core", [], []) is not null;
        string? FirstResolvable(IEnumerable<string> names) => names.FirstOrDefault(s => !string.IsNullOrEmpty(s) && Resolvable(s));
        foreach (var (_, (el, _)) in index.Type("installables"))
        {
            var job = InstallableDef.Parse(el);
            if (string.IsNullOrEmpty(job.ActionCO) || !Resolvable(job.ActionCO)) continue;
            if (job.JobType == "uninstall")
            {
                uninstallable.Add(job.ActionCO);
                // Most jobs name their loose drop in aLootCOs; a few (Nav Station, Transponder) use strLootOut.
                if (FirstResolvable(job.LootCOs.Append(job.LootOut)) is { } loose)
                    looseForms[job.ActionCO] = loose;
            }
            else if (job.JobType == "install" && !string.IsNullOrEmpty(job.StartInstall) && Resolvable(job.StartInstall))
                installedForms[job.ActionCO] = PreferPoweredState(index, job.StartInstall);   // re-install RCS ON too
            // The broken → working swap. Same loot shapes as an uninstall (aLootCOs, or strLootOut for the Nav
            // Station family), and gated on the progress stat rather than the job type — see RepairForms. Powered
            // state is preferred at the END of the build, once the skins below have been mapped in base-state terms.
            else if (job.ProgressStat == RepairProgressStat
                     && FirstResolvable(job.LootCOs.Append(job.LootOut)) is { } repaired
                     && repaired != job.ActionCO)
                repairForms[job.ActionCO] = repaired;
        }

        // The Nav Station and Transponder families point their uninstall's strLootOut at a runtime-only
        // "…LooseEmpty"/"…LooseChance" marker that has no condowner, so the loop above left them without a loose
        // form and "Make Loose Item" was silently unavailable — even though these fixtures ARE uninstallable and
        // their INSTALL job names the real packaged def (ItmStationNavLoose → ItmStationNav). Recover the loose
        // form by inverting that install map: it is guaranteed resolvable (every install ActionCO was checked
        // above) and round-trips with "Install item". Gated on an actual uninstall job existing, so we never
        // invent an uninstall the game itself doesn't offer, and never override a loot-derived mapping.
        foreach (var (loose, installed) in installedForms)
            if (uninstallable.Contains(installed) && !looseForms.ContainsKey(installed))
                looseForms[installed] = loose;

        // Themed walls/floors/conduits are PLACED as cooverlay skins (ItmFloorAERO01), but only the BASE structural
        // condowner (ItmFloorGrate01) carries an uninstall job — so the skin had no loose form and "Make Loose Item"
        // was silently unavailable on it. The game re-skins the base's loose drop to match via the cooverlay's
        // mapModeSwitches (flat [base, skin] pairs across the installed/patch/damaged/loose states); mirror that so
        // loosening a themed skin yields the THEMED loose form (ItmFloorAERO01Loose), not the generic base. Only the
        // installed skin gets an entry: a skin whose base maps to a base-loose form (loose skins base off a loose
        // condowner, which has no uninstall job, so they're skipped).
        foreach (var (name, (el, _)) in index.Type("cooverlays"))
        {
            if (looseForms.ContainsKey(name)) continue;   // a direct uninstall job already covers it
            var overlay = CoOverlayDef.Parse(el);
            if (string.IsNullOrEmpty(overlay.COBase) || !looseForms.TryGetValue(overlay.COBase, out var baseLoose))
                continue;
            var skinLoose = ReskinModeSwitch(overlay.ModeSwitches, baseLoose) ?? baseLoose;
            if (Resolvable(skinLoose)) looseForms[name] = skinLoose;
        }

        // The same skin problem for repair, and the same fix — but read off the mode switches BOTH ways, because a
        // themed damaged wall is not a cooverlay of its own: ItmWallAERO01's switches carry all four base states at
        // once (ItmWall1x1 → ItmWallAERO01, ItmWall1x1Dmg → ItmWallAERO01Dmg, …), so the damaged skin only ever
        // appears as the right-hand side of a pair. Walk each pair, repair the LEFT side through the base map, then
        // re-skin the result forward through the same overlay. Every cooverlay damaged state on stock 1.0.0.9
        // resolves this way (1,794 of them, none needing a cross-overlay hop), so no unskinned fallback is offered:
        // repairing a Testudo wall into a generic one would be a re-skin nobody asked for.
        foreach (var (_, (el, _)) in index.Type("cooverlays"))
        {
            var overlay = CoOverlayDef.Parse(el);
            var switches = overlay.ModeSwitches;
            for (var i = 0; i + 1 < switches.Length; i += 2)
            {
                var (baseState, skinState) = (switches[i], switches[i + 1]);
                if (repairForms.ContainsKey(skinState) || !repairForms.TryGetValue(baseState, out var baseRepaired))
                    continue;
                if (ReskinModeSwitch(switches, baseRepaired) is { } skinRepaired && Resolvable(skinRepaired))
                    repairForms[skinState] = skinRepaired;
            }
        }

        // A repair job hands back the Off state (ItmAlarmSmokeDmg → ItmAlarmSmokeOff), which the ship rating never
        // counts. Prefer the powered counterpart exactly as the install map does, applied last so a skin mapped in
        // base-state terms above gets its own On state rather than the base's.
        foreach (var broken in repairForms.Keys.ToList())
            repairForms[broken] = PreferPoweredState(index, repairForms[broken]);

        // The DAMAGE direction, which is a different table from repairForms above and built from a different
        // source: a part declares what it breaks into on its own condowner (aUpdateCommands' Destructable line →
        // CondOwnerDef.BreakLoot), not in data/installables. Walk it exactly as DataCO.GetMaxHealth does — break
        // loot → the interaction(s) it names → that interaction's objLootModeSwitch → a loot naming EXACTLY ONE
        // condowner. The single-entry rule is the game's own and it bites: ItmReactorIC03Ignition breaks through
        // ACTReactorIC03DamageExplode, whose mode-switch loot names both the wreck and SysExplosionFusion, so the
        // game abandons the walk there and the reactor's chain stops at its own pool. No PreferPoweredState here:
        // breaking a device is not installing one, and the game hands back precisely the def it names.
        var breakForms = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, (el, _)) in index.Type("condowners"))
        {
            var co = CondOwnerDef.Parse(el);
            if (string.IsNullOrEmpty(co.BreakLoot)) continue;
            if (ResolveBreakTarget(co.BreakLoot, loots, interactionDefs) is { } broken && broken != name && Resolvable(broken))
                breakForms[name] = broken;
        }

        // Themed skins carry no condowner of their own, so the loop above never saw them. Same shape as the repair
        // skin pass and for the same reason (§12): a cooverlay's mapModeSwitches carries all of its base's states
        // as [base, skin] pairs, so walk each pair, break the LEFT side through the base map, then re-skin the
        // result forward through the same overlay. A themed wall must break into the themed damaged wall, not the
        // generic one, or a strike would silently re-skin the ship.
        foreach (var (_, (el, _)) in index.Type("cooverlays"))
        {
            var overlay = CoOverlayDef.Parse(el);
            var switches = overlay.ModeSwitches;
            for (var i = 0; i + 1 < switches.Length; i += 2)
            {
                var (baseState, skinState) = (switches[i], switches[i + 1]);
                if (breakForms.ContainsKey(skinState) || !breakForms.TryGetValue(baseState, out var baseBroken))
                    continue;
                if (ReskinModeSwitch(switches, baseBroken) is { } skinBroken && skinBroken != skinState && Resolvable(skinBroken))
                    breakForms[skinState] = skinBroken;
            }
        }

        // Resolve a buildable palette entry, warning when its sprite is missing on disk.
        PartDef? Resolve(string defName, string category, string fallbackOrigin, string[] inputs, string[] tools, bool warnMissingSprite)
        {
            var part = ResolveDef(index, defName, category, fallbackOrigin, inputs, tools);
            if (part is not null && part.SpriteAbs is null && warnMissingSprite)
                Warn(part.Origin, $"No sprite on disk for '{defName}' (strImg '{part.Item.Img}').");
            return part;
        }

        var parts = new Dictionary<string, PartDef>(StringComparer.Ordinal);
        foreach (var (name, (el, origin)) in index.Type("installables"))
        {
            var inst = InstallableDef.Parse(el);
            if (inst.JobType != "install" || inst.NoJobMenu) continue;
            if (inst.StartInstall.Length == 0 || !Categories.Contains(inst.BuildType)) continue;
            var placedDef = PreferPoweredState(index, inst.StartInstall);   // build RCS thrusters ON, not the as-installed Off
            if (parts.ContainsKey(placedDef)) continue;   // several jobs can place the same item

            var part = Resolve(placedDef, inst.BuildType, origin, inst.Inputs, inst.Tools, warnMissingSprite: true);
            if (part is null)
            {
                Warn(origin, $"Installable '{name}' places '{placedDef}' but no items def exists - skipped.");
                continue;
            }
            parts[placedDef] = part;
        }

        // resolvable-but-not-buildable defs join ByDefName only (out of the palette).
        var byDefName = new Dictionary<string, PartDef>(parts, StringComparer.Ordinal);

        // The primary airlock: every ship owns one, but it has no install job.
        if (!byDefName.ContainsKey(PrimaryDocksysDef) &&
            Resolve(PrimaryDocksysDef, "HULL", "core", [], [], warnMissingSprite: false) is { } primary)
            byDefName[PrimaryDocksysDef] = primary with
            {
                Friendly = primary.Friendly == PrimaryDocksysDef ? "Primary Exterior Airlock" : primary.Friendly,
            };

        // Closed-door counterparts: a door's buildable form is the Open state; its Closed
        // skin/base is a real item but carries no build job, so register it here so a placed
        // door can be toggled shut (same base geometry, its own sprite/name). Door state is
        // cosmetic to the law: a door's wall cells seal each side of the hull the same
        // whether it is open or closed, so the game's CreateRooms gives an identical room
        // count and airtightness either way - toggling never changes rooms/rating. (The one
        // per-cell difference - the centre tile is a walkable portal when open and a solid
        // IsWall+IsPortal boundary when closed - is what AssignPortals handles.)
        foreach (var door in parts.Values.Where(p => p.StartingConds.Contains("IsPortal") && p.DefName.Contains("Open")).ToList())
        {
            var closed = ReplaceLast(door.DefName, "Open", "Closed");
            if (closed is null || byDefName.ContainsKey(closed)) continue;
            if (Resolve(closed, door.Category, door.Origin, door.Inputs, door.Tools, warnMissingSprite: false) is { } cp)
                byDefName[closed] = cp;
        }

        // SpecialItems: the installed defs swept up above that the buildable menu never reaches. byDefName is the
        // right thing to subtract, not parts — it already accounts for the two defs registered by hand (the primary
        // airlock, each door's closed counterpart), so neither shows up twice.
        var specialItems = installedItems
            .Where(p => !byDefName.ContainsKey(p.DefName)
                        && !p.StartingConds.Any(StateOnlyConds.Contains)
                        && p.Friendly != p.DefName)
            .OrderBy(p => p.Friendly, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new Catalog
        {
            Parts = parts.Values.OrderBy(p => p.Category).ThenBy(p => p.Friendly, StringComparer.OrdinalIgnoreCase).ToList(),
            ByDefName = byDefName,
            Loots = loots,
            Triggers = trigs,
            GpmTemplates = gpmTemplates,
            TickerTemplates = tickerTemplates,
            Slots = slots,
            LightDefs = lightDefs,
            ColorTable = colorTable,
            ParallaxDefs = parallaxDefs,
            InteractionDefs = interactionDefs,
            ShipAttacks = shipAttacks,
            LooseItems = looseItems,
            SpecialItems = specialItems,
            LooseForms = looseForms,
            InstalledForms = installedForms,
            RepairForms = repairForms,
            BreakForms = breakForms,
            Warnings = warnings,
            Index = index,
        };
    }

    /// <summary>
    /// Map a base-condowner state to a cooverlay skin's counterpart via <c>mapModeSwitches</c> — a flat list of
    /// <c>[base, skin]</c> pairs (installed/patch/damaged/loose). Given the base's loose form, returns the skin's
    /// loose form (e.g. <c>ItmFloorGrate01Loose</c> → <c>ItmFloorAERO01Loose</c>). Null if the base state isn't
    /// listed, so the caller can fall back to the generic base drop.
    /// </summary>
    /// <summary>
    /// The def a break loot turns its target into, or null when the walk does not resolve: the game's
    /// <c>DataCO.GetMaxHealth</c> inner loop, reproduced.
    ///
    /// <para>The loot names interactions (its <c>aCOs</c>, plus nested <c>aLoots</c>); each is looked up and its
    /// <c>objLootModeSwitch</c> read as a further loot; that loot must not be <c>Blank</c> and must name
    /// <b>exactly one</b> condowner, which is the broken form. The first entry that satisfies all of it wins,
    /// matching the game, which assigns and then keeps walking without breaking out.</para>
    /// </summary>
    private static string? ResolveBreakTarget(
        string breakLoot,
        IReadOnlyDictionary<string, LootDef> loots,
        IReadOnlyDictionary<string, InteractionDef> interactions)
    {
        foreach (var iaName in FlattenLootCos(breakLoot, loots))
        {
            if (interactions.GetValueOrDefault(iaName) is not { LootModeSwitch: { Length: > 0 } ms }) continue;
            if (ms == "Blank" || loots.GetValueOrDefault(ms) is not { } target) continue;
            if (target.Name == "Blank" || target.Conds.Length != 1) continue;
            return target.Conds[0];
        }
        return null;
    }

    /// <summary>A loot's <c>aCOs</c> names plus, recursively, those of its nested <c>aLoots</c> — the build-time
    /// twin of <see cref="LootConds"/>, which needs a constructed Catalog and so cannot be used from
    /// <see cref="Build"/>. Cycle-guarded; an unresolved name contributes nothing.</summary>
    private static List<string> FlattenLootCos(string lootName, IReadOnlyDictionary<string, LootDef> loots)
    {
        var acc = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Walk(string n)
        {
            if (!seen.Add(n) || loots.GetValueOrDefault(n) is not { } l) return;
            acc.AddRange(l.Conds);
            foreach (var child in l.Loots) Walk(child);
        }
        if (!string.IsNullOrEmpty(lootName) && lootName != "Blank") Walk(lootName);
        return acc;
    }

    private static string? ReskinModeSwitch(string[] modeSwitches, string baseState)
    {
        for (var i = 0; i + 1 < modeSwitches.Length; i += 2)
            if (modeSwitches[i] == baseState)
                return modeSwitches[i + 1];
        return null;
    }

    /// <summary>
    /// The def Ostraplan should actually build for an install target. Ostranauts installs powered fixtures in their
    /// <b>Off</b> state (e.g. <c>strStartInstall = ItmRCSCluster01Off</c>) — the state a ship's rating never counts
    /// (rating triggers forbid <c>IsOff</c>: the RCS maneuver grade's <c>TIsRCSClusterAudioEmitter</c> is one of
    /// many) and which a player must switch on after loading. Ostraplan builds the identical <b>On</b> variant
    /// instead — same footprint, only the power state differs, and core ship templates are authored with the On
    /// variant — so a design's rating is meaningful and an exported ship's devices work on spawn.
    /// <para>
    /// The game's On-state naming isn't uniform, so the counterpart is found by trying, most-specific first,
    /// <c>"…On"</c> (cooler, switch), <c>"…OnG"</c> (the green/normal state air pumps and most alarms use), then the
    /// bare stem with <c>Off</c> dropped (RCS, heater, bed). A candidate is accepted only when it resolves to a real
    /// placeable <b>condowner</b> (carries starting conditions — see below), is itself not <c>IsOff</c>, and shares
    /// the Off state's footprint. Anything without such a counterpart — a device whose only On states are ambiguous
    /// colour/alert modes (temp alarm, transponder), a startup sequence (fusion reactor core), an Open/Closed vent,
    /// or a plain "…Off" that isn't a power state — is left exactly as installed.
    /// </para>
    /// <para>
    /// The <b>condowner requirement</b> (non-empty <c>StartingConds</c>) is load-bearing: some devices ship a bare
    /// <b>item</b> for their glow/animation state with no condowner (e.g. <c>ItmFusionReactorCore01On</c> — same 5×5
    /// geometry and sockets as the Off form, but no CO). The game never <i>installs</i> such an orphan; it only ever
    /// installs condowners. Resolving one as the build target used to hand the palette a reactor core with an
    /// internal-name label, <c>StatMass</c>/<c>StatBasePrice</c> of 0, and no <c>IsFusionReactorCore</c> conds —
    /// silently wrong mass (maneuver rating), room value, bill of materials, and export. Placement still "worked"
    /// because the sockets are identical, which is exactly why this hid. Requiring a real condowner leaves the
    /// reactor core (and any similar orphan) as the installable <c>…Off</c> CO, while the genuine operational
    /// counterparts (RCS, coolers, field coils, laser array, pumps) — all real condowners — still swap.
    /// </para>
    /// </summary>
    private static string PreferPoweredState(DataIndex index, string def)
    {
        if (!def.EndsWith("Off", StringComparison.Ordinal)) return def;
        if (ResolveDef(index, def, "—", "core", [], []) is not { } off || !off.StartingConds.Contains("IsOff"))
            return def;
        var stem = def[..^3];
        foreach (var candidate in new[] { stem + "On", stem + "OnG", stem })
        {
            if (candidate == def) continue;
            if (ResolveDef(index, candidate, "—", "core", [], []) is not { } on
                || on.StartingConds.Length == 0   // a bare-item orphan (no condowner) is never a real install target
                || on.StartingConds.Contains("IsOff")
                || on.Item.Width != off.Item.Width || on.Item.Height != off.Item.Height)
                continue;
            return candidate;
        }
        return def;
    }

    /// <summary>
    /// Resolve a placed def to a <see cref="PartDef"/> the way the game's installer / loader does:
    /// the name is a CONDOWNER (directly, or via a cooverlay skin whose <c>strCOBase</c> is the real
    /// one — DataHandler.LoadCO's fallback), geometry/sockets come from that condowner's
    /// <c>strItemDef</c>, and an overlay swaps the sprite + friendly name. Falls back to treating the
    /// name itself as an item def (some ship parts place a raw item). Null if no geometry exists at all.
    /// Shared by <see cref="Build"/> (buildable menu) and <see cref="Lookup"/> (any placed def).
    /// </summary>
    private static PartDef? ResolveDef(DataIndex index, string defName, string category, string fallbackOrigin, string[] inputs, string[] tools)
    {
        var overlays = index.Type("cooverlays");
        var owners = index.Type("condowners");
        var items = index.Type("items");

        // A REAL condowner of this name always wins; the overlay is a fallback for names that have none
        // (DataHandler.GetCondOwner: `if (!dictCOs.ContainsKey(strCO)) { … strCO = overlay.strCOBase; }`).
        // Eight core names are both — the grey Rakow "Reserve" bins (ItmStorageBin2x104 / 2x2C04 + their
        // Dmg/Loose forms) ship a full condowner alongside a legacy cooverlay pointing at the "01" sibling.
        // Reading the overlay first gave them the 01's item def (a socket mask demanding a floor the
        // exterior-mountable grey bin does not need), the 01's price/mass, and the skin's condloot on top.
        var direct = owners.ContainsKey(defName);
        var overlay = !direct && overlays.TryGetValue(defName, out var ov) ? CoOverlayDef.Parse(ov.El) : null;
        var coName = string.IsNullOrWhiteSpace(overlay?.COBase) ? defName : overlay!.COBase!;
        var co = owners.TryGetValue(coName, out var rawCo) ? CondOwnerDef.Parse(rawCo.El) : null;
        var itemName = string.IsNullOrWhiteSpace(co?.ItemDefName) ? coName : co!.ItemDefName!;

        // A cooverlay skin's real conds are its base condowner's PLUS the deltas in the skin's strCondLoot: the
        // game's COOverlay.Init runs Loot.ApplyCondLoot on every spawn, so e.g. an MSS "Light Framework" wall is
        // ItmWall1x1's 24 kg minus a -StatMass x4 (= 20), plus IsMSS/IsWhite. Fold them in here, or every branded
        // wall/floor reads the shared base's flat stats. Non-skinned parts (no overlay) keep their base conds.
        var baseNames = co?.StartingCondNames ?? [];
        var baseVals = co?.StartingCondValues ?? new Dictionary<string, double>();
        var startNames = baseNames;
        var startVals = baseVals;
        if (overlay is { CondLoot: { Length: > 0 } condLoot })
            (startNames, startVals) = ApplyCondLoot(index, condLoot, startVals);

        // What CondOwner.SetUpBehaviours would add, replayed in the game's own order: it runs on the last line of
        // SetData, against the BASE conds, and the skin's cond loot lands on the result afterwards in
        // COOverlay.Init. Reading the guards off the folded conds instead gets 249 defs wrong — 254 skin loots
        // move StatDamageMax, which is one of the guards, and ItmConduit04's loot drives StatInstallProgressMax
        // to zero, which the game leaves absent (an instant install) and a naive backfill would restore to 1000.
        var backfill = PartDef.BehaviourBackfill(baseNames, baseVals);
        var behaviourConds = Array.Empty<string>();
        if (backfill.Count > 0)
        {
            var withBackfill = new Dictionary<string, double>(baseVals, StringComparer.Ordinal);
            foreach (var (k, v) in backfill) withBackfill[k] = v;
            var settled = overlay is { CondLoot: { Length: > 0 } bcl }
                ? ApplyCondLoot(index, bcl, withBackfill).Values
                : withBackfill;
            behaviourConds = [.. backfill.Keys
                .Where(settled.ContainsKey)   // a delta that drives one to zero leaves it absent, as the game does
                .Select(k => FormattableString.Invariant($"{k}=1.0x{settled[k]}"))];
        }

        if (!items.TryGetValue(itemName, out var rawItem) && !items.TryGetValue(defName, out rawItem))
            return null;   // no geometry (e.g. a modded def whose mod isn't loaded)

        var item = ItemDef.Parse(rawItem.El);
        if (!string.IsNullOrWhiteSpace(overlay?.Img))
            item = item with { Img = overlay!.Img! };
        // The skin's wear textures override the base's the same way its sprite does — COOverlay.Init hands all
        // four to Item.SetAlt in one call — so a branded wall wears in its own colours. A skin that names neither
        // keeps the base item's, which is why each is guarded separately rather than as a pair.
        if (!string.IsNullOrWhiteSpace(overlay?.ImgDamaged))
            item = item with { ImgDamaged = overlay!.ImgDamaged! };
        if (!string.IsNullOrWhiteSpace(overlay?.DmgColor))
            item = item with { DmgColor = overlay!.DmgColor! };

        var friendly =
            !string.IsNullOrWhiteSpace(overlay?.NameFriendly) ? overlay!.NameFriendly! :
            !string.IsNullOrWhiteSpace(co?.NameFriendly) ? co!.NameFriendly! :
            defName;
        var origin =
            overlay is not null && overlays.TryGetValue(defName, out var rov) ? rov.Origin :
            items.TryGetValue(itemName, out var ri) ? ri.Origin :
            items.TryGetValue(defName, out var rd) ? rd.Origin : fallbackOrigin;

        // A container if the def declares any container signal (a filter CT, a grid, or the IsContainer cond);
        // the grid defaults to 6×6 when it's a container but names no dimensions (the game's Container default).
        var isContainer = co is not null &&
            (co.ContainerCT is not null || co.ContainerW > 0 || startNames.Contains("IsContainer"));
        (int, int)? containerGrid = isContainer
            ? (co!.ContainerW > 0 ? co.ContainerW : 6, co.ContainerH > 0 ? co.ContainerH : 6)
            : null;
        // This item's own footprint on a grid: inventoryWidth/Height override the map footprint, else 1×1.
        var invSize = (
            Math.Max(1, co?.InvW is > 0 ? co.InvW : item.Width),
            Math.Max(1, co?.InvH is > 0 ? co.InvH : item.Height));

        // Power connectors. A device names a power-info via its condowner's jsonPI (data/powerinfos); that
        // power-info's aInputPts name the map points where it plugs into the conduit network. The game skips input
        // nubs on a part carrying IsPowerInputIgnore (Powered draw path). A source (generator/battery) instead
        // carries a PowerOutput map point — the flood's start tile. Resolve names → (x, y) offsets now.
        var powerInputs = new List<(double X, double Y)>();
        if (co is not null && !string.IsNullOrEmpty(co.Jpi)
            && !startNames.Contains("IsPowerInputIgnore")
            && index.Type("powerinfos").TryGetValue(co.Jpi, out var rawPi))
            foreach (var ptName in PowerInfoDef.Parse(rawPi.El).InputPointNames)
                if (co.MapPoints.TryGetValue(ptName, out var pt)) powerInputs.Add(pt);
        (double X, double Y)? powerOutput =
            co is not null && co.MapPoints.TryGetValue("PowerOutput", out var po) ? po : null;

        // A skin can swap interactions on its base (aInteractionsReplace) — the game's COOverlay.Init walks the
        // base's aInteractions and substitutes in place, so a skinned device offers the skin's action, not the
        // base's. Non-skinned parts keep the base list.
        var interactions = co?.Interactions ?? [];
        if (overlay is { InteractionReplacements.Count: > 0 })
            interactions = interactions
                .Select(n => overlay.InteractionReplacements.FirstOrDefault(r => r.From == n) is { To: { Length: > 0 } to }
                    ? to : n)
                .ToArray();

        return new PartDef(
            defName, friendly, category, origin, item, index.ResolveImage(item.Img), inputs, tools,
            startNames,
            startVals,
            co?.MapPoints ?? new Dictionary<string, (double, double)>())
        {
            SpriteNormAbs = item.ImgNorm is { Length: > 0 } and not "blank" ? index.ResolveImage(item.ImgNorm) : null,
            SpriteDamagedAbs = item.ImgDamaged is { Length: > 0 } and not "blank" ? index.ResolveImage(item.ImgDamaged) : null,
            Gpm = ResolveGpm(co?.GpmNames, overlay?.GpmNames),
            Desc = co?.Desc,
            SkinCondLoot = overlay is { CondLoot: { Length: > 0 } skinLoot } ? skinLoot : null,
            BehaviourConds = behaviourConds,
            TickerNames = co?.TickerNames ?? [],
            InvSize = invSize,
            ContainerGrid = containerGrid,
            ContainerCT = co?.ContainerCT,
            DefaultLoot = co?.Loot,
            StackLimit = co?.StackLimit ?? 0,
            SlotsWeHave = co?.SlotsWeHave ?? [],
            SlotLayout = co?.SlotLayout ?? new Dictionary<string, (double X, double Y)>(),
            SlotKeys = co?.SlotKeys ?? [],
            PowerInputPoints = powerInputs,
            PowerOutputPoint = powerOutput,
            InteractionNames = interactions,
        };
    }

    /// <summary>
    /// Fold a cooverlay's <c>strCondLoot</c> into a base condowner's starting conds, replicating the game's
    /// <c>COOverlay.Init</c> → <c>Loot.ApplyCondLoot</c>: each of the loot's <c>aCOs</c> entries is a <b>signed
    /// delta</b> accumulated onto the base value (a leading <c>-</c> subtracts — see <see cref="LootDef.CondAmount"/>),
    /// and nested <c>aLoots</c> recurse. Only <c>strType:"condition"</c> loots apply (the game early-returns
    /// otherwise). Conds driven to zero are dropped, matching the game's <c>UpdateCondRecords</c> (and its "x0 is
    /// absent" rule). Returns the merged cond names + magnitudes — the real per-skin stats.
    /// </summary>
    private static (string[] Names, IReadOnlyDictionary<string, double> Values) ApplyCondLoot(
        DataIndex index, string lootName, IReadOnlyDictionary<string, double> baseVals)
    {
        var vals = new Dictionary<string, double>(baseVals, StringComparer.Ordinal);
        var loots = index.Type("loot");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Apply(string name)
        {
            if (!seen.Add(name) || !loots.TryGetValue(name, out var raw)) return;
            var el = raw.El;
            if (!string.Equals(Json.Str(el, "strType"), "condition", StringComparison.Ordinal)) return;
            foreach (var entry in Json.StrArray(el, "aCOs"))
            {
                // A leading '-' negates the delta and is NOT part of the cond name (LootDef.CondAmount reads the
                // sign; CondName must see the un-prefixed name, or "-StatMass=..." would key a bogus "-StatMass").
                var cn = LootDef.CondName(entry.StartsWith('-') ? entry[1..] : entry);
                if (cn.Length == 0) continue;
                vals[cn] = vals.GetValueOrDefault(cn) + LootDef.CondAmount(entry);   // signed-delta accumulate
            }
            foreach (var child in Json.StrArray(el, "aLoots"))
                Apply(child);
        }
        Apply(lootName);
        // The game removes any cond whose count reaches <= 0 (UpdateCondRecords; "zero == absent"), so a delta that
        // drives a stat to zero or below (e.g. a CAYL floor loot subtracting more mass than the grate base carries)
        // leaves the cond ABSENT, not negative. Match that, or the part shows a bogus <=0 mass/health.
        var cleaned = vals.Where(kv => kv.Value > 0.0)
                          .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        return (cleaned.Keys.ToArray(), cleaned);
    }

    /// <summary>
    /// Merge a condowner's and its cooverlay's <c>mapGUIPropMaps</c> into ordered (instance, template) pairs.
    /// The flat list alternates instance/template names; a trailing single name — the odd "Electrical" the
    /// game's own <c>ConvertStringArrayToDict</c> drops but adds back via power-connection logic — is taken as
    /// instance == template. The overlay's entries override the base's by instance name (the game applies the
    /// base first, then the skin).
    /// </summary>
    private static IReadOnlyList<(string, string)> ResolveGpm(string[]? baseNames, string[]? overlayNames)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>();
        void Add(string[]? names)
        {
            if (names is null) return;
            for (var i = 0; i < names.Length; i += 2)
            {
                var instance = names[i];
                if (string.IsNullOrEmpty(instance)) continue;
                var template = i + 1 < names.Length && !string.IsNullOrEmpty(names[i + 1]) ? names[i + 1] : instance;
                if (!map.ContainsKey(instance)) order.Add(instance);
                map[instance] = template;
            }
        }
        Add(baseNames);
        Add(overlayNames);
        return order.Select(k => (k, map[k])).ToList();
    }
}
