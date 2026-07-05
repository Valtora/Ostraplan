using System.Collections.Concurrent;

namespace Ostraplan.Core;

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
    IReadOnlyDictionary<string, double> StartingCondValues,  // name -> value (StatMass, StatThrustStrength, ...)
    IReadOnlyDictionary<string, (double X, double Y)> MapPoints);  // condowner mapPoints (DockA/DockB, RoomA/B, ...)

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
    public required List<string> Warnings { get; init; }

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

    // Render z-order (bottom -> top). The game leaves nLayer at 0 for every item and
    // Y-sorts sprites over a floor tile-layer; Ostraplan's own renderer instead ranks
    // each part by what it contributes to its tiles, so floors never occlude what sits
    // on them. Ties within a layer keep the existing Y-then-insertion order.
    public const int LayerFloor = 0;      // IsFloor / IsFloorSealed / IsFloorFlex
    public const int LayerWall = 1;       // IsWall / IsPortal (doors carry both wall and floor conds — checked first)
    public const int LayerFixture = 2;    // beds, appliances, sensors, and everything unclassified
    public const int LayerConduit = 3;    // IsPowerConduit — thin power runs, drawn on top

    private static readonly HashSet<string> WallConds = new(StringComparer.Ordinal) { "IsWall", "IsPortal" };
    private static readonly HashSet<string> FloorConds = new(StringComparer.Ordinal) { "IsFloor", "IsFloorSealed", "IsFloorFlex" };
    private readonly ConcurrentDictionary<string, int> _renderLayer = new(StringComparer.Ordinal);

    /// <summary>The z-layer a part draws in, from the conditions its sockets add (memoized by def name).</summary>
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

    /// <summary>
    /// The open/closed counterpart of a door placement's def, or null if this isn't a
    /// toggleable door (both ends resolve via <see cref="ByDefName"/>). Door state is
    /// cosmetic to the room/airtightness/rating law: the door's wall cells seal each side
    /// of the hull the same open or closed, so CreateRooms gives the same room count and
    /// airtightness either way. This swaps only the sprite and name, never the analysis.
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
        var warnings = new List<string>();

        var items = index.Type("items").ToDictionary(kv => kv.Key, kv => ItemDef.Parse(kv.Value.El), StringComparer.Ordinal);
        var owners = index.Type("condowners").ToDictionary(kv => kv.Key, kv => CondOwnerDef.Parse(kv.Value.El), StringComparer.Ordinal);
        var overlays = index.Type("cooverlays").ToDictionary(kv => kv.Key, kv => CoOverlayDef.Parse(kv.Value.El), StringComparer.Ordinal);
        var loots = index.Type("loot").ToDictionary(kv => kv.Key, kv => LootDef.Parse(kv.Value.El), StringComparer.Ordinal);
        var trigs = index.Type("condtrigs").ToDictionary(kv => kv.Key, kv => CondTriggerDef.Parse(kv.Value.El), StringComparer.Ordinal);

        // Resolve a placed def (strStartInstall or a toggled variant) the way the game
        // does: strStartInstall names the placed CONDOWNER - directly, or via a cooverlay
        // skin whose strCOBase is the real one (DataHandler.LoadCO's fallback). Geometry
        // /sockets come from the base's item def; the overlay swaps the sprite and name.
        PartDef? Resolve(string defName, string category, string fallbackOrigin, string[] inputs, string[] tools, bool warnMissingSprite)
        {
            overlays.TryGetValue(defName, out var overlay);
            var coName = string.IsNullOrWhiteSpace(overlay?.COBase) ? defName : overlay!.COBase!;
            owners.TryGetValue(coName, out var co);
            var itemName = string.IsNullOrWhiteSpace(co?.ItemDefName) ? coName : co!.ItemDefName!;
            if (!items.TryGetValue(itemName, out var item)) return null;
            if (!string.IsNullOrWhiteSpace(overlay?.Img))
                item = item with { Img = overlay!.Img! };

            var friendly =
                !string.IsNullOrWhiteSpace(overlay?.NameFriendly) ? overlay!.NameFriendly! :
                !string.IsNullOrWhiteSpace(co?.NameFriendly) ? co!.NameFriendly! :
                defName;
            var itemOrigin =
                overlay is not null && index.Type("cooverlays").TryGetValue(defName, out var rawOv) ? rawOv.Origin :
                index.Type("items").TryGetValue(itemName, out var raw) ? raw.Origin : fallbackOrigin;
            var sprite = index.ResolveImage(item.Img);
            if (sprite is null && warnMissingSprite)
                warnings.Add($"No sprite on disk for '{defName}' (strImg '{item.Img}').");

            return new PartDef(
                defName, friendly, category, itemOrigin, item, sprite, inputs, tools,
                co?.StartingCondNames ?? [],
                co?.StartingCondValues ?? new Dictionary<string, double>(),
                co?.MapPoints ?? new Dictionary<string, (double, double)>());
        }

        var parts = new Dictionary<string, PartDef>(StringComparer.Ordinal);
        foreach (var (name, (el, origin)) in index.Type("installables"))
        {
            var inst = InstallableDef.Parse(el);
            if (inst.JobType != "install" || inst.NoJobMenu) continue;
            if (inst.StartInstall.Length == 0 || !Categories.Contains(inst.BuildType)) continue;
            if (parts.ContainsKey(inst.StartInstall)) continue;   // several jobs can place the same item

            var part = Resolve(inst.StartInstall, inst.BuildType, origin, inst.Inputs, inst.Tools, warnMissingSprite: true);
            if (part is null)
            {
                warnings.Add($"Installable '{name}' places '{inst.StartInstall}' but no items def exists - skipped.");
                continue;
            }
            parts[inst.StartInstall] = part;
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

        return new Catalog
        {
            Parts = parts.Values.OrderBy(p => p.Category).ThenBy(p => p.Friendly, StringComparer.OrdinalIgnoreCase).ToList(),
            ByDefName = byDefName,
            Loots = loots,
            Triggers = trigs,
            Warnings = warnings,
        };
    }
}
