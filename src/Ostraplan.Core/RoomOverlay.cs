namespace Ostraplan.Core;

/// <summary>
/// One compartment as the Room Viz draws it: the flood-fill partition the game would compute, its certified
/// spec, its size and worth, and — for a room that certifies as nothing — the ready-to-show reasons why
/// (<see cref="ShipAnalysis.NearMisses"/>: what to add, and which member item blocks it).
/// <see cref="Tiles"/> are in <b>document</b> coords so the canvas can paint them directly.
/// </summary>
public sealed record RoomVizRoom(
    int Index, string Spec, string SpecFriendly, bool Void,
    int TileCount, double Value,
    IReadOnlyList<(int X, int Y)> Tiles,
    IReadOnlyList<string> NearMisses)
{
    /// <summary>A room that matched a real spec (Blank is the fallback, not a certification).</summary>
    public bool Certified => Spec != "Blank";
}

/// <summary>
/// The Room Viz model: every <b>interior</b> compartment of the design, certified exactly as the game would
/// (<see cref="RoomBuilder"/> + <see cref="RoomCertifier"/>). This is the same partition the Ship Rating and the
/// save-edit inject bake, surfaced live on the plan — the room half of what <c>PowerNetwork</c> does for conduits.
///
/// <para>The exterior void is excluded: its fill reaches the grid edge, so it spans the entire plan and would
/// simply tint everything. That is not a loss of information — the interesting case, a compartment open to space,
/// shows up precisely as an interior room <b>missing</b> from this overlay (its fill joined the exterior), and the
/// leak tiles themselves already have a dedicated visualisation via <see cref="ShipAnalysis.FindBreaches"/>.</para>
/// </summary>
public sealed record RoomOverlay(IReadOnlyList<RoomVizRoom> Rooms)
{
    public static readonly RoomOverlay Empty = new([]);

    public bool IsEmpty => Rooms.Count == 0;

    /// <summary>Certify the document's compartments for display. Pure and off-thread safe (takes a snapshot's
    /// worth of document); costs one grid build + flood fill, the same as the problem scan.</summary>
    public static RoomOverlay Build(ShipDocument doc, Catalog catalog, IReadOnlyList<RoomSpecDef> specs)
    {
        var grid = ShipGrid.FromDocument(doc, catalog);
        var partition = RoomBuilder.Build(grid);
        RoomCertifier.CertifyAll(partition, specs, catalog);

        var valueModifiers = specs.ToDictionary(s => s.Name, s => s.ValueModifier, StringComparer.Ordinal);
        var friendlyOf = specs.ToDictionary(s => s.Name, s => s.Friendly, StringComparer.Ordinal);

        var rooms = new List<RoomVizRoom>();
        for (var i = 0; i < partition.Rooms.Count; i++)
        {
            var r = partition.Rooms[i];
            if (r.Outside) continue;   // the exterior void — see the type remarks
            var certified = r.RoomSpec != "Blank";
            rooms.Add(new RoomVizRoom(
                i, r.RoomSpec,
                friendlyOf.GetValueOrDefault(r.RoomSpec, r.RoomSpec),
                r.Void, r.TileCount,
                ShipValue.RoomValueOf(r, valueModifiers, catalog),
                r.Tiles.Select(grid.GridToDoc).ToArray(),
                certified ? [] : ShipAnalysis.NearMisses(r, specs, catalog)));
        }
        return new RoomOverlay(rooms);
    }
}
