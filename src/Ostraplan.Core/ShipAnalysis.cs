namespace Ostraplan.Core;

/// <summary>One room in the law report: its certification and size. <see cref="NearMisses"/>
/// are ready-to-show lines for an uncertified room — the closest specs with what to add and,
/// crucially, which member items <b>block</b> them (a forbid, e.g. a canister in a quarters).</summary>
public sealed record RoomInfo(
    int Index, string Spec, string SpecFriendly, bool Void, bool Outside,
    int TileCount, double Volume, IReadOnlyList<string> NearMisses);

/// <summary>
/// An airtightness breach — a compartment that is not sealed against space, and the
/// precise tiles to highlight (the leak, not the whole flooded area). Two kinds:
/// <list type="bullet">
///   <item><see cref="OpenToSpace"/>: floor the player laid that is <b>not enclosed by
///   walls</b> (its fill reaches the exterior). <see cref="Tiles"/> are the <b>leak
///   points</b> — the missing-wall gaps where sealed floor meets open space (or the
///   exposed floor itself at the grid edge); <see cref="ExposedFloorCount"/> is how many
///   floor tiles that opening vents to space.</item>
///   <item>otherwise: an enclosed compartment missing a <b>sealed floor</b> on some tiles
///   — <see cref="Tiles"/> are the holes.</item>
/// </list>
/// </summary>
public sealed record Breach(bool OpenToSpace, IReadOnlyList<(int X, int Y)> Tiles, int RoomTileCount, int ExposedFloorCount = 0);

/// <summary>
/// An optional value-raising move for one sealed room: the specs it could certify as (or upgrade
/// to), each line naming what to add / remove and the broker-sell gain on the room's <b>current</b>
/// contents. <see cref="GainSell"/> is the best line's gain, used to order the report.
/// <see cref="Tiles"/> are the room's tiles in document coords, so the report can highlight
/// exactly which room a hint is talking about. Rooms are a disjoint flood-fill partition — each
/// room yields at most one entry and no tile belongs to two rooms.
/// </summary>
public sealed record RoomOpportunity(
    int RoomIndex, string CurrentSpecFriendly, bool Certified, int TileCount,
    double GainSell, IReadOnlyList<string> Lines, IReadOnlyList<(int X, int Y)> Tiles);

/// <summary>The full "Ship Rating" analysis: the six-slot rating, room detail, breaches, and the
/// optional value-opportunity hints (<see cref="Opportunities"/> + the O2-bonus state).</summary>
public sealed record AnalysisReport(ShipRating Rating, IReadOnlyList<RoomInfo> Rooms, IReadOnlyList<Breach> Breaches, int PartCount,
    IReadOnlyList<RoomOpportunity> Opportunities, bool O2BonusActive, double O2PotentialSell)
{
    public IEnumerable<RoomInfo> Certified => Rooms.Where(r => r.Spec != "Blank");
    public IEnumerable<RoomInfo> Uncertifiable => Rooms.Where(r => r is { Spec: "Blank", Void: false } && r.NearMisses.Count > 0);
    public int BreachTileCount => Breaches.Sum(b => b.Tiles.Count);
}

/// <summary>
/// Runs the P2 engine end-to-end for the UI's on-demand "Ship Rating" action, and exposes
/// the airtightness detection the live problem scan reuses.
/// </summary>
public static class ShipAnalysis
{
    public static AnalysisReport AnalyzeDocument(ShipDocument doc, Catalog catalog,
        IReadOnlyList<RoomSpecDef> specs, IProgress<(string Stage, double Frac)>? progress = null)
    {
        progress?.Report(("Building tile grid…", 0.10));
        var grid = ShipGrid.FromDocument(doc, catalog);

        progress?.Report(("Detecting rooms & airtightness…", 0.35));
        var partition = RoomBuilder.Build(grid);

        progress?.Report(("Certifying rooms…", 0.60));
        RoomCertifier.CertifyAll(partition, specs, catalog);

        progress?.Report(("Calculating Ship Rating…", 0.85));
        var rating = Rating.Calculate(grid, partition, catalog);

        var friendly = specs.ToDictionary(s => s.Name, s => s.Friendly, StringComparer.Ordinal);
        var rooms = new List<RoomInfo>(partition.Rooms.Count);
        for (var i = 0; i < partition.Rooms.Count; i++)
        {
            var r = partition.Rooms[i];
            var near = r is { RoomSpec: "Blank", Void: false } && r.Parts.Count > 0
                ? NearMisses(r, specs, catalog) : [];
            rooms.Add(new RoomInfo(i, r.RoomSpec, friendly.GetValueOrDefault(r.RoomSpec, r.RoomSpec),
                r.Void, r.Outside, r.TileCount, r.Volume, near));
        }

        var o2Active = ShipValue.CountO2Pumps(grid, catalog) > 0;
        var modifiers = specs.ToDictionary(s => s.Name, s => s.ValueModifier, StringComparer.Ordinal);
        var roomsValue = partition.Rooms.Sum(r => ShipValue.RoomValueOf(r, modifiers, catalog));
        // the single biggest value lever: a fed O2 pump ×3s the whole room value at the broker
        var o2Potential = o2Active ? 0 : roomsValue * ShipValue.O2Multiplier * ShipValue.BrokerSellFactor;

        progress?.Report(("Done", 1.0));
        return new AnalysisReport(rating, rooms, FindBreaches(grid, partition), doc.Placements.Count,
            Opportunities(grid, partition, specs, catalog, o2Active), o2Active, o2Potential);
    }

    /// <summary>
    /// Airtightness breaches in a partition. A room is sealed only if it is <b>non-void</b>
    /// — enclosed by walls (its fill never reached the exterior) with a sealed floor on
    /// every tile. Anything else with the player's floor in it is a breach. To point the
    /// user at the fix rather than the symptom, an open-to-space breach reports only the
    /// <b>leak points</b> — the missing-wall gaps where sealed floor abuts open space — not
    /// the whole flooded compartment; an enclosed room reports its unsealed holes. Tiles
    /// are document coords.
    /// </summary>
    public static List<Breach> FindBreaches(ShipGrid grid, RoomPartition partition)
    {
        var breaches = new List<Breach>();
        for (var ri = 0; ri < partition.Rooms.Count; ri++)
        {
            var room = partition.Rooms[ri];
            if (!room.Void) continue;   // sealed compartment — good

            if (!room.Outside)
            {
                // enclosed but unsealed: the holes are exactly the tiles missing a sealed floor
                var holes = room.Tiles.Where(t => !grid.Has(t, "IsFloorSealed")).Select(grid.GridToDoc).ToArray();
                if (holes.Length > 0) breaches.Add(new Breach(false, holes, room.TileCount));
                continue;
            }

            // open to space: the player's floor escaped through a gap. Highlight the GAP
            // (the missing wall), not the flooded floor. A gap is an open tile (no floor,
            // no wall) in this same exterior room next to a sealed floor; a floor at the
            // grid edge has no gap tile to mark, so mark the floor itself.
            var gaps = new HashSet<int>();
            foreach (var t in room.Tiles)
            {
                if (!grid.Has(t, "IsFloorSealed")) continue;
                foreach (var nt in RoomBuilder.Cardinals(grid, t))
                {
                    if (nt < 0) gaps.Add(t);   // exposed at the grid edge — no gap tile to mark
                    else if (partition.TileRoom[nt] == ri && !grid.Has(nt, "IsFloorSealed") && !grid.Has(nt, "IsWall"))
                        gaps.Add(nt);          // an open gap where a wall is missing
                }
            }
            if (gaps.Count > 0)
            {
                var area = room.Tiles.Count(t => grid.Has(t, "IsFloorSealed"));   // all floor this opening vents
                breaches.Add(new Breach(true, gaps.Select(grid.GridToDoc).ToArray(), room.TileCount, area));
            }
        }
        return breaches;
    }

    /// <summary>Fast airtightness-only pass for the live problem scan: breaches for the current document.</summary>
    public static List<Breach> Airtightness(ShipDocument doc, Catalog catalog)
    {
        var grid = ShipGrid.FromDocument(doc, catalog);
        return FindBreaches(grid, RoomBuilder.Build(grid));
    }

    /// <summary>
    /// The closest specs a room could certify as, each with a concrete fix. Ranked by fewest
    /// missing requirement units, then by most requirements already satisfied (so a bedroom
    /// missing only a chair beats the Reactor room, which also "misses one" but has nothing
    /// going for it), then by spec priority. Includes <b>blocking items</b> — a spec whose
    /// requirements are all met but that a member part forbids (the classic silent failure:
    /// a gas canister or RTA parked in an otherwise-valid quarters).
    /// </summary>
    public static IReadOnlyList<string> NearMisses(RoomModel room, IReadOnlyList<RoomSpecDef> specs, Catalog catalog)
    {
        return specs.Where(s => !s.IsBlank)
            .Select(s => RoomCertifier.Diagnose(s, room, catalog))
            .Where(d => d.ShapeOk && !AdvisesReactorCore(d)
                && !AdvisesUnbuildableTowing(d, "Blank", room.TileCount))   // near-misses are uncertified rooms
            .OrderBy(d => d.MissingCount)
            .ThenByDescending(d => d.SatisfiedCount)
            .ThenByDescending(d => d.Spec.Priority)
            .Take(2)
            .Select(Describe)
            .ToList();
    }

    /// <summary>
    /// Never ADVISE building a reactor core: <c>TIsReactorIC</c> means the 5×5 fusion core — a
    /// ship-defining build with its own chain (field coils, vacuum exposure), effectively one per
    /// ship — and the Reactor spec's shape gate is just "≥4 sealed tiles", so "add a reactor core"
    /// would spam every single room list. A room that already <b>has</b> a core still gets Reactor
    /// lines (the core isn't among its missing requirements then).
    /// </summary>
    private static bool AdvisesReactorCore(SpecDiagnosis d) =>
        d.Missing.Any(m => m.Trigger == "TIsReactorIC");

    /// <summary>
    /// A Towing Room is an upgraded airlock, so only suggest it there: the brace's own placement
    /// ring requires a docking-system tile (<c>TILDockSys</c> — it can only ever be built beside a
    /// docking port), and it is a 7×2 fixture, so the room also needs its 7-tile wall run. Without
    /// this gate "needs a towing brace" sprayed onto every uncertified room (its spec gate is just
    /// "≥2 sealed tiles").
    /// </summary>
    private static bool AdvisesUnbuildableTowing(SpecDiagnosis d, string currentSpec, int tileCount) =>
        d.Missing.Any(m => m.Trigger == "TIsTowingBraceInstalled")
        && !(currentSpec == "Airlock" && tileCount >= 7);

    /// <summary>
    /// Deliberate, ship-defining builds are not value advice: a nav station (the Bridge specs
    /// require nothing else, so every ≥4-tile room "could be a bridge", but a ship wants one
    /// bridge, not a console per closet) and a docking port (an airlock is placed exactly where
    /// the ship mates, not sprinkled for value). Exempt from the VALUE hints only — the near-miss
    /// diagnostics still show Bridge/Airlock lines, so a room being built as one reads correctly.
    /// </summary>
    private static readonly string[] DeliberateBuildTriggers =
        ["TIsNavStationInstalled", "TIsDockSysInstalled"];

    private static bool AdvisesDeliberateBuild(SpecDiagnosis d) =>
        d.Missing.Any(m => DeliberateBuildTriggers.Contains(m.Trigger, StringComparer.Ordinal));

    private static string Describe(SpecDiagnosis d)
    {
        var parts = new List<string>(2);
        if (d.Missing.Count > 0)
            parts.Add("needs " + string.Join(", ", d.Missing.Select(m =>
                m.Count == 1 ? TriggerNoun(m.Trigger) : $"{TriggerNoun(m.Trigger)} ×{m.Count}")));
        if (d.ForbiddenParts.Count > 0)
            parts.Add("remove " + string.Join(", ", d.ForbiddenParts
                .GroupBy(p => p, StringComparer.Ordinal)
                .Select(g => g.Count() == 1 ? g.Key : $"{g.Key} ×{g.Count()}")));
        return $"{d.Spec.Friendly}: {string.Join(" · ", parts)}";
    }

    /// <summary>
    /// The optional value hints: for every sealed room — <b>including empty ones</b> — the
    /// higher-value specs its shape allows, what they need, and the broker-sell gain on the
    /// room's current contents. A candidate must beat the current spec on <b>both</b> value
    /// modifier and <c>nPriority</c>: certification takes the highest-priority match, so
    /// items added for a lower-priority spec would change nothing. Uncertified rooms show
    /// their two closest candidates (ranked easiest-first); certified rooms show one upgrade
    /// and only when it is ≤3 items away (a "replace your bridge with a reactor" hint is
    /// data-true but not advice). Rooms are ordered by gain, then size.
    /// </summary>
    private static IReadOnlyList<RoomOpportunity> Opportunities(
        ShipGrid grid, RoomPartition partition, IReadOnlyList<RoomSpecDef> specs, Catalog catalog, bool o2Active)
    {
        var byName = specs.ToDictionary(s => s.Name, StringComparer.Ordinal);
        var friendly = specs.ToDictionary(s => s.Name, s => s.Friendly, StringComparer.Ordinal);
        var o2Factor = 1 + (o2Active ? ShipValue.O2Multiplier : 0);
        var result = new List<RoomOpportunity>();

        for (var i = 0; i < partition.Rooms.Count; i++)
        {
            var room = partition.Rooms[i];
            if (room.Void) continue;
            var specName = room.RoomSpec ?? "";
            var certified = specName is not ("" or "Blank");
            var current = certified && byName.TryGetValue(specName, out var cs) ? cs : null;
            var currentMod = current?.ValueModifier ?? 1.0;
            var currentPriority = current?.Priority ?? 0;
            var partsValue = ShipValue.RoomPartsValue(room, catalog);

            var candidates = specs
                .Where(s => !s.IsBlank && s.Name != specName
                    && s.Priority > currentPriority && s.ValueModifier > currentMod)
                .Select(s => RoomCertifier.Diagnose(s, room, catalog))
                .Where(d => d.ShapeOk && !AdvisesReactorCore(d) && !AdvisesDeliberateBuild(d)
                    && !AdvisesUnbuildableTowing(d, specName, room.TileCount)
                    && (!certified || d.MissingCount <= 3))
                .OrderBy(d => d.MissingCount)
                .ThenByDescending(d => d.Spec.ValueModifier)
                .ThenByDescending(d => d.Spec.Priority)
                .Take(certified ? 1 : 2)
                .ToList();
            if (candidates.Count == 0) continue;

            double GainOf(SpecDiagnosis d) =>
                partsValue * (d.Spec.ValueModifier - currentMod) * o2Factor * ShipValue.BrokerSellFactor;

            var lines = candidates.Select(d =>
            {
                var gain = GainOf(d);
                var line = $"{Describe(d)} (×{d.Spec.ValueModifier:0.0#} room value";
                if (gain >= 1) line += $", +${gain:N0} sale price on current contents";
                return line + ")";
            }).ToList();

            result.Add(new RoomOpportunity(i,
                certified ? friendly.GetValueOrDefault(specName, specName)
                    : room.Parts.Count == 0 ? "empty" : "uncertified",
                certified, room.TileCount, GainOf(candidates[0]), lines,
                room.Tiles.Select(grid.GridToDoc).ToArray()));
        }

        return result
            .OrderByDescending(o => o.GainSell)
            .ThenByDescending(o => o.TileCount)
            .ToList();
    }

    /// <summary>Human noun for a room-spec requirement trigger — curated for the core
    /// <c>data/rooms</c> vocabulary, with a mechanical prettifier for modded triggers.</summary>
    private static string TriggerNoun(string trigger) =>
        CoreTriggerNouns.TryGetValue(trigger, out var noun) ? noun : Prettify(trigger);

    private static readonly Dictionary<string, string> CoreTriggerNouns = new(StringComparer.Ordinal)
    {
        ["TIsBedInstalled"] = "a bed",
        ["TIsStorageBinInstalled"] = "a storage bin",
        ["TIsChairInstalled"] = "a chair",
        ["TIsLightSourceInstalled"] = "a light",
        ["TIsTableInstalled"] = "a table",
        ["TIsFridge01Installed"] = "a fridge",
        ["TIsToilet"] = "a toilet",
        ["TIsSinkInstalled"] = "a sink",
        ["TIsNavStationInstalled"] = "a nav station",
        ["TIsDockSysInstalled"] = "a docking port",
        ["TIsReactorIC"] = "a reactor core",
        ["TIsTowingBraceInstalled"] = "a towing brace",
        ["TIsRoomEngineering"] = "engineering equipment (a canister/RTA, battery, charger, or RCS distributor)",
        ["TIsRoomWellnessOptionals01"] = "wellness equipment (fridge, sink, treadmill, or strength trainer)",
        ["TIsRoomRecreationOptionals"] = "recreation equipment (terminal, TV, or bartop)",
        ["TIsRoomCargo"] = "cargo storage (bin/rack)",
        ["TIsRoomCargoExterior"] = "an exterior cargo mount",
        ["TIsCanister"] = "a gas canister",
    };

    /// <summary>"TIsSomeModdedThingInstalled" → "some modded thing".</summary>
    private static string Prettify(string trigger)
    {
        var s = trigger;
        if (s.StartsWith("TIs", StringComparison.Ordinal)) s = s[3..];
        if (s.EndsWith("Installed", StringComparison.Ordinal)) s = s[..^"Installed".Length];
        var words = System.Text.RegularExpressions.Regex
            .Replace(s, "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Za-z])(?=[0-9])", " ")
            .Trim();
        return words.Length == 0 ? trigger : words.ToLowerInvariant();
    }
}
