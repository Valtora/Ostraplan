namespace Ostraplan.Core;

/// <summary>One room in the law report: its certification and size.</summary>
public sealed record RoomInfo(
    int Index, string Spec, string SpecFriendly, bool Void, bool Outside,
    int TileCount, double Volume, string? NearMiss);

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

/// <summary>The full "Ship Rating" analysis: the six-slot rating, room detail, and breaches.</summary>
public sealed record AnalysisReport(ShipRating Rating, IReadOnlyList<RoomInfo> Rooms, IReadOnlyList<Breach> Breaches, int PartCount)
{
    public IEnumerable<RoomInfo> Certified => Rooms.Where(r => r.Spec != "Blank");
    public IEnumerable<RoomInfo> Uncertifiable => Rooms.Where(r => r is { Spec: "Blank", Void: false, NearMiss: not null });
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
            var near = r is { RoomSpec: "Blank", Void: false } && r.Parts.Count > 0 ? NearMiss(r, specs, catalog) : null;
            rooms.Add(new RoomInfo(i, r.RoomSpec, friendly.GetValueOrDefault(r.RoomSpec, r.RoomSpec),
                r.Void, r.Outside, r.TileCount, r.Volume, near));
        }

        progress?.Report(("Done", 1.0));
        return new AnalysisReport(rating, rooms, FindBreaches(grid, partition), doc.Placements.Count);
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

    /// <summary>The highest-priority spec this room is the right shape for but is missing items for.</summary>
    private static string? NearMiss(RoomModel room, IReadOnlyList<RoomSpecDef> specs, Catalog catalog)
    {
        foreach (var spec in specs)
        {
            if (spec.IsBlank) continue;
            var fail = RoomCertifier.Matches(spec, room, catalog);
            if (fail is not null && fail.Reason.StartsWith("missing required", StringComparison.Ordinal))
                return $"{spec.Friendly}: {fail.Reason}";
        }
        return null;
    }
}
