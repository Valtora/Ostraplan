namespace Ostraplan.Core;

/// <summary>One room in the law report: its certification, size/airtightness, and — for
/// a compartment that leaks — the document tiles that lack a sealed floor.</summary>
public sealed record RoomInfo(
    int Index, string Spec, string SpecFriendly, bool Void, bool Outside,
    int TileCount, double Volume, string? NearMiss, IReadOnlyList<(int X, int Y)> LeakTiles);

/// <summary>The full "Ship Rating" analysis: the six-slot rating plus every room's status.</summary>
public sealed record AnalysisReport(ShipRating Rating, IReadOnlyList<RoomInfo> Rooms, int PartCount)
{
    /// <summary>Certified compartments (non-Blank spec), best first.</summary>
    public IEnumerable<RoomInfo> Certified => Rooms.Where(r => r.Spec != "Blank");

    /// <summary>Void compartments that are not the exterior — an airtightness breach the player can fix.</summary>
    public IEnumerable<RoomInfo> Leaks => Rooms.Where(r => r is { Void: true, Outside: false } && r.TileCount > 0);

    /// <summary>Non-void rooms that stayed Blank but nearly qualified as a spec (with the reason why).</summary>
    public IEnumerable<RoomInfo> Uncertifiable => Rooms.Where(r => r is { Spec: "Blank", Void: false, NearMiss: not null });
}

/// <summary>
/// Runs the P2 engine end-to-end for the UI's on-demand "Ship Rating" action: build the
/// grid from the live document, detect rooms, certify them, compute the rating, and
/// collect the law-report detail (leaks + near-miss certifications). Reports staged
/// progress for the in-game-style progress bar; runs off the UI thread.
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
            var leaks = r is { Void: true, Outside: false }
                ? r.Tiles.Where(t => !grid.Has(t, "IsFloorSealed")).Select(grid.GridToDoc).ToArray()
                : [];
            var near = r is { RoomSpec: "Blank", Void: false } && r.Parts.Count > 0
                ? NearMiss(r, specs, catalog)
                : null;
            rooms.Add(new RoomInfo(i, r.RoomSpec, friendly.GetValueOrDefault(r.RoomSpec, r.RoomSpec),
                r.Void, r.Outside, r.TileCount, r.Volume, near, leaks));
        }

        progress?.Report(("Done", 1.0));
        return new AnalysisReport(rating, rooms, doc.Placements.Count);
    }

    /// <summary>The highest-priority spec this room is the right shape for but is missing items for
    /// — "Galley: missing required: TIsChairInstalled ×4". Null if it isn't close to any spec.</summary>
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
