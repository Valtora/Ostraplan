namespace Ostraplan.Core;

/// <summary>Which of the ship's four answers a consequence belongs to. The order is the order they are worth
/// reading in: air first, because it is the one that kills a crew in minutes.</summary>
public enum FalloutKind
{
    /// <summary>A compartment that held air and no longer does.</summary>
    Air,

    /// <summary>Something the crew can no longer walk to, or a piece of the ship they can no longer walk into.</summary>
    Access,

    /// <summary>A device that was fed by a live power run and is not any more.</summary>
    Power,

    /// <summary>A row of the ship's own diagnostic that has gone from working to not.</summary>
    System,
}

/// <summary>One thing the strike did to the <b>ship</b>, as against to a part.</summary>
/// <param name="Cells">Document tiles to highlight, empty when the consequence is not about a place.</param>
public sealed record DamageConsequence(
    FalloutKind Kind, string Title, string Detail, IReadOnlyList<(int X, int Y)> Cells);

/// <summary>What a run of strikes cost the ship, worst kind first.</summary>
public sealed record DamageFalloutReport(IReadOnlyList<DamageConsequence> Consequences)
{
    public static readonly DamageFalloutReport Empty = new([]);

    public bool IsEmpty => Consequences.Count == 0;
}

/// <summary>One compartment the intact ship had, as the baseline remembers it.</summary>
/// <param name="Name">The zone the compartment sits in, when the design names one, else null. A design's own
/// zone names are the only words a plan has for a place, and they are the user's own.</param>
/// <param name="Tiles">Grid tile indices, in the baseline's frame.</param>
/// <param name="DocTiles">The same tiles in document coords, for highlighting.</param>
public sealed record BaselineRoom(string? Name, IReadOnlyList<int> Tiles, IReadOnlyList<(int X, int Y)> DocTiles);

/// <summary>
/// The intact ship's answers, computed once and held for the life of a Simulate session.
///
/// <para>The frame is the important part. Every set here is expressed in <b>one</b> grid, the intact ship's, and
/// the damaged hull is measured in that same frame through <see cref="ShipGrid.FromDocumentFramed"/>. Without that
/// the two grids would be sized to their own bounding boxes, a strike that removes a part at the edge would shift
/// the origin, and every tile index on both sides would be talking about a different tile.</para>
/// </summary>
public sealed record DamageBaseline(
    int OriginCol, int OriginRow, int NCols, int NRows,
    IReadOnlyList<BaselineRoom> SealedRooms,
    IReadOnlySet<string> ReachableDevices,
    IReadOnlySet<int> MainBodyTiles,
    IReadOnlySet<string> PoweredDevices,
    IReadOnlyList<DiagnosticRow> Systems);

/// <summary>
/// What a strike did to the ship, as against to its parts.
///
/// <para><b>Why this is not the design warning scan.</b> The consequences of a hit were originally read by running
/// <see cref="ProblemScan"/> over the damaged hull and subtracting the problems the design already had, and it did
/// not work: a design warning is written to be read once, so it aggregates ("2 unsealed compartments") and carries
/// a merged cell list, and a set difference over that has no stable thing to compare. One new breach changes the
/// count in the title and the tiles underneath it, so the whole aggregate reads as new and the report says the
/// compartments that were already open are the strike's doing. That is exactly how it was reported: "I have no
/// clue how your extra warning logic works but it's clearly not working on deltas."</para>
///
/// <para><b>Every comparison here is against a thing with an identity that survives the strike.</b> A compartment
/// is a set of tiles in a fixed frame, a device is a placement id, a system is one of the game's own sixteen
/// diagnostic rows by name. None of them is a rendered string and none of them changes because something else
/// changed, so a strike can only ever add what it actually caused.</para>
///
/// <para><b>Rooms are defined against the undamaged ship, not re-derived from the wreck.</b> Flood-filling the
/// damaged hull gives compartments that cannot be matched to the ones the design had: a lost wall merges two
/// rooms into one and renumbers everything after it. So the intact partition is what names a compartment, and the
/// only question asked of the damaged hull is whether those tiles still hold air.</para>
///
/// <para>Nothing here is a simulation. Each of the four answers is a static property of a layout, already ported
/// and already used elsewhere in the tool, asked once of a hull that has had parts taken out of it. What happens
/// <i>next</i> (the venting, the fire, the reactor cooking off) is still out of scope, and still would be.</para>
/// </summary>
public static class DamageFallout
{
    /// <summary>How many parts a list-shaped consequence names before it starts counting instead. A strike that
    /// cuts a wing off can unreach a hundred fittings, and a hundred lines is not a report. The remainder is
    /// always stated, never dropped.</summary>
    private const int NamedLimit = 8;

    /// <summary>
    /// Measure the intact ship. Null for a design with no grid at all, which has nothing a strike could cost it.
    ///
    /// <para>Call once per Simulate session and keep the result: it cannot change while the window is open,
    /// because a strike never edits the document and the document cannot be edited from behind the window.</para>
    ///
    /// <para><paramref name="intact"/> must share placement ids with whatever is later handed to
    /// <see cref="Compare"/>. A pristine <see cref="DamageState.Project"/> of the live document is the way to get
    /// an independent copy that does; a <see cref="ShipDocument.Snapshot"/> is not, because it mints new ones.</para>
    /// </summary>
    public static DamageBaseline? Baseline(ShipDocument intact, Catalog catalog)
    {
        ArgumentNullException.ThrowIfNull(intact);
        ArgumentNullException.ThrowIfNull(catalog);

        var grid = ShipGrid.FromDocument(intact, catalog);
        if (grid.TileCount == 0) return null;

        var partition = RoomBuilder.Build(grid);
        var walk = WalkNetwork.Build(grid, catalog, null, WalkNetwork.ForbiddenTiles(intact, grid));
        var power = PowerNetwork.Build(grid, catalog);
        var systems = ShipDiagnostics.SystemRows(grid, catalog, Propulsion.Estimate(intact, grid, catalog));

        var rooms = new List<BaselineRoom>();
        foreach (var room in partition.Rooms)
        {
            // Only a compartment that actually held air can lose it. A void room is already open, and reporting
            // it would put the design's own faults back in the strike's column, which is the thing this exists to
            // stop doing.
            if (room.Void) continue;
            var docTiles = room.Tiles.Select(grid.GridToDoc).ToList();
            rooms.Add(new BaselineRoom(ZoneNameFor(intact, docTiles), [.. room.Tiles], docTiles));
        }

        var main = walk.LargestZone;
        return new DamageBaseline(
            (int)grid.VShipPosX, (int)grid.VShipPosY, grid.NCols, grid.NRows,
            rooms,
            walk.Devices.Where(d => d.Reachable).Select(d => d.Part.StrID ?? "").Where(s => s.Length > 0)
                .ToHashSet(StringComparer.Ordinal),
            main >= 0 ? walk.Zones[main].Tiles.ToHashSet() : [],
            power.Devices.Where(d => d.Connected).Select(d => d.Part.StrID ?? "").Where(s => s.Length > 0)
                .ToHashSet(StringComparer.Ordinal),
            systems);
    }

    /// <summary>
    /// What the damage has cost the ship, measured against <paramref name="baseline"/>.
    ///
    /// <para>Takes the <b>projected</b> hull (<see cref="DamageState.Project"/>) rather than a document and a
    /// state, so the caller can build the projection on its own thread and hand over something nothing else
    /// holds. It must be a projection of the very document the baseline was taken from: a part is matched between
    /// the two hulls by its <see cref="Placement.Id"/>, which a projection carries across and a
    /// <see cref="ShipDocument.Snapshot"/> does not.</para>
    ///
    /// <para>Runs four analyses over that hull, so it belongs off the UI thread on anything but a small
    /// design.</para>
    /// </summary>
    public static DamageFalloutReport Compare(ShipDocument projected, Catalog catalog, DamageBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(projected);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(baseline);

        // The baseline's frame, not the wreck's own: see the note on DamageBaseline.
        var grid = ShipGrid.FromDocumentFramed(
            projected, catalog, baseline.OriginCol, baseline.OriginRow, baseline.NCols, baseline.NRows);

        var found = new List<DamageConsequence>();
        AddAir(grid, baseline, found);
        AddAccess(grid, catalog, projected, baseline, found);
        AddPower(grid, catalog, baseline, found);
        AddSystems(grid, catalog, projected, baseline, found);
        return found.Count == 0 ? DamageFalloutReport.Empty : new DamageFalloutReport(found);
    }

    // ---- air ----

    /// <summary>Compartments the intact ship sealed and the damaged one does not.</summary>
    private static void AddAir(ShipGrid grid, DamageBaseline baseline, List<DamageConsequence> found)
    {
        var partition = RoomBuilder.Build(grid);
        foreach (var room in baseline.SealedRooms)
        {
            // The compartment has lost its air the moment any of its tiles falls in a room the damaged hull reads
            // as void, which covers both ways it can happen: a floor holed under it, or a wall lost so the fill
            // escapes to space. A tile that has become a WALL belongs to no room and is not a leak, which is why
            // this asks about the rooms the tiles are in rather than about the tiles.
            var vented = false;
            foreach (var t in room.Tiles)
            {
                var ri = partition.TileRoom[t];
                if (ri < 0 || !partition.Rooms[ri].Void) continue;
                vented = true;
                break;
            }
            if (!vented) continue;

            found.Add(new DamageConsequence(
                FalloutKind.Air,
                room.Name is { } name ? $"\"{name}\" is open to space" : $"A {room.Tiles.Count}-tile compartment is open to space",
                "It held air before the hit and does not now.",
                room.DocTiles));
        }
    }

    // ---- access ----

    /// <summary>Fittings the crew can no longer walk up to, and parts of the ship they can no longer walk into.</summary>
    private static void AddAccess(
        ShipGrid grid, Catalog catalog, ShipDocument projected, DamageBaseline baseline,
        List<DamageConsequence> found)
    {
        var walk = WalkNetwork.Build(grid, catalog, null, WalkNetwork.ForbiddenTiles(projected, grid));

        // Devices that were operable on foot and are not any more. A part that was destroyed outright is not here
        // to be counted, and the changes list already says it is gone: this is about what SURVIVED the hit and
        // can no longer be used, which is the half a part count cannot reach.
        var lost = walk.Devices
            .Where(d => !d.Reachable && d.Part.StrID is { } id && baseline.ReachableDevices.Contains(id))
            .ToList();
        if (lost.Count > 0)
            found.Add(new DamageConsequence(
                FalloutKind.Access,
                $"{Count(lost.Count, "fitting")} can no longer be reached",
                NameThem(lost.Select(d => d.Friendly)),
                [.. lost.SelectMany(d => d.BodyTiles).Distinct().Select(grid.GridToDoc)]));

        // Deck that is still walkable but no longer joined to the main body. A stuck door counts, because an
        // unpowered or damaged closed door is a solid wall to pathing.
        var main = walk.LargestZone;
        var reachable = main >= 0 ? walk.Zones[main].Tiles.ToHashSet() : [];
        var cut = baseline.MainBodyTiles
            .Where(t => t < walk.Walkable.Count && walk.Walkable[t] && !reachable.Contains(t))
            .ToList();
        if (cut.Count > 0)
            found.Add(new DamageConsequence(
                FalloutKind.Access,
                $"{Count(cut.Count, "walkable tile")} cut off from the rest of the ship",
                "Still standable, but no route back to the main body: the crew would have to EVA.",
                [.. cut.Select(grid.GridToDoc)]));
    }

    // ---- power ----

    /// <summary>Devices that were on a live run and have lost it, to a cut conduit or a dead source.</summary>
    private static void AddPower(
        ShipGrid grid, Catalog catalog, DamageBaseline baseline, List<DamageConsequence> found)
    {
        var power = PowerNetwork.Build(grid, catalog);
        var lost = power.Devices
            .Where(d => !d.Connected && d.Part.StrID is { } id && baseline.PoweredDevices.Contains(id))
            .ToList();
        if (lost.Count == 0) return;

        found.Add(new DamageConsequence(
            FalloutKind.Power,
            $"{Count(lost.Count, "device")} lost power",
            NameThem(lost.Select(d => d.Part.Part.Friendly)),
            [.. lost.SelectMany(d => d.InputTiles).Distinct().Where(t => t >= 0).Select(grid.GridToDoc)]));
    }

    // ---- systems ----

    /// <summary>
    /// Rows of the game's own ship diagnostic that have gone from working to not: the reactor and its two
    /// reactants, the thrusters and their distributor and reaction mass, backup power, and life support's pumps,
    /// stores, heat and cool.
    ///
    /// <para>Read off <see cref="ShipDiagnostics"/> rather than from a list of critical parts written here,
    /// because that page is the one place the <b>game</b> enumerates the systems a working ship is expected to
    /// carry. A hand-written list would be a second opinion about it that could only ever drift.</para>
    /// </summary>
    private static void AddSystems(
        ShipGrid grid, Catalog catalog, ShipDocument projected, DamageBaseline baseline,
        List<DamageConsequence> found)
    {
        var after = ShipDiagnostics.SystemRows(grid, catalog, Propulsion.Estimate(projected, grid, catalog));
        var before = baseline.Systems.ToDictionary(r => r.Name, StringComparer.Ordinal);

        foreach (var row in after)
        {
            if (row.State != DiagState.Bad) continue;
            if (!before.TryGetValue(row.Name, out var was) || was.State != DiagState.Good) continue;
            found.Add(new DamageConsequence(
                FalloutKind.System,
                // The captions are the console's own, and they carry their colon: "REACTOR:" reads as a label
                // rather than a sentence, so it is trimmed here and nowhere else.
                $"{row.Name.TrimEnd(':')} now reads {row.Value}",
                row.Note ?? $"It read {was.Value} before the hit.",
                []));
        }
    }

    // ---- shared ----

    /// <summary>The zone a set of tiles sits in, by whichever named zone covers most of them, or null when none
    /// does. A zone is the only name a design gives a place, so it is the only name a compartment can have.</summary>
    private static string? ZoneNameFor(ShipDocument doc, IReadOnlyList<(int X, int Y)> docTiles)
    {
        string? best = null;
        var bestHits = 0;
        foreach (var zone in doc.Zones)
        {
            if (string.IsNullOrWhiteSpace(zone.Name)) continue;
            var hits = docTiles.Count(zone.Tiles.Contains);
            if (hits <= bestHits) continue;
            bestHits = hits;
            best = zone.Name;
        }
        return best;
    }

    /// <summary>Name what fits and count the rest. Never truncates without saying so.</summary>
    private static string NameThem(IEnumerable<string> names)
    {
        var all = names.ToList();
        if (all.Count <= NamedLimit) return string.Join(", ", all) + ".";
        return string.Join(", ", all.Take(NamedLimit)) + $", and {all.Count - NamedLimit} more.";
    }

    private static string Count(int n, string singular) => $"{n} {(n == 1 ? singular : singular + "s")}";
}
