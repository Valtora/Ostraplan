namespace Ostraplan.Core;

/// <summary>One room in the law report: its certification and size.</summary>
public sealed record RoomInfo(
    int Index, string Spec, string SpecFriendly, bool Void, bool Outside,
    int TileCount, double Volume, string? NearMiss);

/// <summary>
/// An airtightness breach — a compartment that is not sealed against space, and the
/// document tiles to highlight. Two kinds:
/// <list type="bullet">
///   <item><see cref="OpenToSpace"/>: floor the player laid that is <b>not enclosed by
///   walls</b> (its fill reaches the exterior), so it can never hold atmosphere — the
///   tiles are its exposed sealed-floor cells.</item>
///   <item>otherwise: an enclosed compartment missing a <b>sealed floor</b> on some tiles
///   — the tiles are the holes.</item>
/// </list>
/// </summary>
public sealed record Breach(bool OpenToSpace, IReadOnlyList<(int X, int Y)> Tiles, int RoomTileCount);

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
    /// every tile. Anything else with the player's floor in it is a breach: floor that
    /// escaped to the exterior (<see cref="Breach.OpenToSpace"/>, e.g. missing walls / a
    /// wall gap), or an enclosed compartment with unsealed holes. Tiles are document coords.
    /// </summary>
    public static List<Breach> FindBreaches(ShipGrid grid, RoomPartition partition)
    {
        var breaches = new List<Breach>();
        foreach (var room in partition.Rooms)
        {
            if (!room.Void) continue;   // sealed compartment — good
            var tiles = room.Outside
                ? room.Tiles.Where(t => grid.Has(t, "IsFloorSealed")).Select(grid.GridToDoc).ToArray()    // floors open to space
                : room.Tiles.Where(t => !grid.Has(t, "IsFloorSealed")).Select(grid.GridToDoc).ToArray();  // holes in an enclosed room
            if (tiles.Length > 0)
                breaches.Add(new Breach(room.Outside, tiles, room.TileCount));
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
