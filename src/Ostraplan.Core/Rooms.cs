namespace Ostraplan.Core;

/// <summary>
/// A compartment: the tiles it owns, whether it is Void (open to space / unsealed)
/// and Outside (the fill reached the grid edge), its certified spec, and the
/// installed parts assigned to it. Ported from the game's Room.
/// </summary>
public sealed class RoomModel
{
    public List<int> Tiles { get; } = [];
    public bool Void { get; set; }
    public bool Outside { get; set; }
    public string RoomSpec { get; set; } = "Blank";
    public List<PlacedPart> Parts { get; } = [];

    public int TileCount => Tiles.Count;
    /// <summary>0.256 m³ per tile (hardcoded 0.25599998f); Void rooms carry no volume.</summary>
    public double Volume => Void ? 0.0 : 0.25599998 * Tiles.Count;
}

/// <summary>The room partition plus a tile→room-index map (−1 = wall / unassigned).</summary>
public sealed record RoomPartition(IReadOnlyList<RoomModel> Rooms, int[] TileRoom);

/// <summary>
/// Port of <c>Ship.CreateRooms</c> (verified 0.15.1.6). BFS flood fill over
/// non-wall, non-portal tiles with 4-connectivity (N/W/E/S — the game's
/// GetSurroundingTiles cardinal indices). Walls and portals are boundaries; a room
/// is <b>Void</b> when any member tile lacks <c>IsFloorSealed</c> or the fill reaches
/// the grid edge (a null cardinal neighbour — also marks it Outside). Portal (door)
/// tiles are then assigned to the preferred non-void adjacent room via the door's
/// <c>RoomA</c>/<c>RoomB</c> map points, matching the game's post-fill pass. Void is
/// fixed during the fill, so a door tile added afterward never voids a sealed room.
/// </summary>
public static class RoomBuilder
{
    public static RoomPartition Build(ShipGrid grid)
    {
        var n = grid.TileCount;
        var rooms = new List<RoomModel>();
        var tileRoom = new int[n];
        Array.Fill(tileRoom, -1);
        var pathChecked = new bool[n];

        for (var seed = 0; seed < n; seed++)
        {
            if (pathChecked[seed]) continue;
            // Portals never seed the fill (Ship.CreateRooms collects them separately);
            // an open portal instead joins the first room that reaches it (below) but
            // never expands. Walls belong to no room.
            if (grid.Has(seed, "IsPortal")) continue;
            if (grid.Has(seed, "IsWall")) { pathChecked[seed] = true; continue; }

            var room = new RoomModel();
            var roomIdx = rooms.Count;
            rooms.Add(room);

            var queue = new List<int> { seed };
            pathChecked[seed] = true;
            for (var qi = 0; qi < queue.Count; qi++)
            {
                var t = queue[qi];
                room.Tiles.Add(t);
                tileRoom[t] = roomIdx;
                if (!room.Void && !grid.Has(t, "IsFloorSealed")) room.Void = true;
                // a portal tile joins this room but does not propagate the fill
                // (game: flag=IsPortal → empty neighbour list), so the two sides of an
                // open door/hatch stay distinct rooms even though the opening is walkable
                if (grid.Has(t, "IsPortal")) continue;

                foreach (var nt in Cardinals(grid, t))
                {
                    if (nt < 0) { room.Void = true; room.Outside = true; continue; }   // reached the grid edge
                    if (pathChecked[nt]) continue;
                    if (grid.Has(nt, "IsWall")) continue;   // wall boundary (a closed door adds IsWall)
                    pathChecked[nt] = true;
                    queue.Add(nt);   // open-portal neighbours (IsPortal, no IsWall) are flooded here, then sink
                }
            }
        }

        AssignPortals(grid, rooms, tileRoom);
        AssignParts(grid, rooms, tileRoom);
        return new RoomPartition(rooms, tileRoom);
    }

    /// <summary>
    /// Each installed part joins the room containing its anchor (centre) tile
    /// — Ship.CreateRooms' Tile.AddToRoom pass over GetCOs(TIsInstalled). Walls
    /// anchor on their own (room-less) tile and so join no room, which is correct:
    /// certification only counts fixtures/systems sitting inside a compartment.
    /// </summary>
    private static void AssignParts(ShipGrid grid, List<RoomModel> rooms, int[] tileRoom)
    {
        foreach (var part in grid.Parts)
        {
            if (part.AnchorIndex < 0 || !part.Part.Has("IsInstalled")) continue;
            var ri = tileRoom[part.AnchorIndex];
            if (ri >= 0) rooms[ri].Parts.Add(part);
        }
    }

    /// <summary>
    /// Door tiles (IsPortal) join the preferred non-void room across the doorway.
    /// Each door's <c>RoomA</c>/<c>RoomB</c> map points sit one tile into each side;
    /// the game picks non-void RoomA, else non-void RoomB, else RoomA, else RoomB
    /// (Ship.CreateRooms' portal pass). Tiles whose door has no room on either side
    /// stay unassigned (wall / edge doors), exactly as the game leaves them.
    /// </summary>
    private static void AssignPortals(ShipGrid grid, List<RoomModel> rooms, int[] tileRoom)
    {
        foreach (var door in grid.Parts)
        {
            if (!door.Part.MapPoints.ContainsKey("RoomA") || !door.Part.MapPoints.ContainsKey("RoomB")) continue;

            var a = RoomAtMapPoint(grid, rooms, tileRoom, door, "RoomA");
            var b = RoomAtMapPoint(grid, rooms, tileRoom, door, "RoomB");
            var preferred =
                a >= 0 && !rooms[a].Void ? a :
                b >= 0 && !rooms[b].Void ? b :
                a >= 0 ? a :
                b >= 0 ? b : -1;
            if (preferred < 0) continue;

            var (wr, hr) = GridMath.Size(door.Part.Item.Width, door.Part.Item.Height, door.Rot);
            for (var r = 0; r < hr; r++)
                for (var c = 0; c < wr; c++)
                {
                    var col = door.TopLeftCol + c;
                    var row = door.TopLeftRow + r;
                    if (!grid.InBounds(col, row)) continue;
                    var idx = grid.Index(col, row);
                    if (!grid.Has(idx, "IsPortal") || tileRoom[idx] >= 0) continue;   // only unclaimed opening tiles
                    tileRoom[idx] = preferred;
                    rooms[preferred].Tiles.Add(idx);
                }
        }
    }

    /// <summary>Room index at a door's RoomA/RoomB map point (pixels around the door
    /// centre, +y up), rotated with the door — or −1 off-grid / wall.</summary>
    private static int RoomAtMapPoint(ShipGrid grid, List<RoomModel> rooms, int[] tileRoom, PlacedPart door, string key)
    {
        if (!door.Part.MapPoints.TryGetValue(key, out var mp)) return -1;
        var fRot = GridMath.Norm(-door.Rot);   // recover the game's CCW angle
        double ox = mp.X / 16.0, oy = mp.Y / 16.0;
        var (rx, ry) = fRot switch
        {
            90 => (-oy, ox),
            180 => (-ox, -oy),
            270 => (oy, -ox),
            _ => (ox, oy),
        };
        var idx = grid.TileAtWorld(door.CX + rx, door.CY + ry);
        return idx >= 0 ? tileRoom[idx] : -1;
    }

    /// <summary>N, W, E, S neighbours; −1 where the neighbour falls off the grid edge.</summary>
    private static IEnumerable<int> Cardinals(ShipGrid grid, int t)
    {
        var col = grid.Col(t);
        var row = grid.Row(t);
        yield return row > 0 ? t - grid.NCols : -1;              // N
        yield return col > 0 ? t - 1 : -1;                       // W
        yield return col < grid.NCols - 1 ? t + 1 : -1;          // E
        yield return row < grid.NRows - 1 ? t + grid.NCols : -1; // S
    }
}
