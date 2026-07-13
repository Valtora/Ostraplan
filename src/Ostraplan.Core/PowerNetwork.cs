namespace Ostraplan.Core;

/// <summary>
/// A powered device on the network: the placement, the grid tiles its power-input map points land on, and whether
/// any of them sits on a live (source-fed) power path. <see cref="Connected"/> false = a wired device that is not
/// hooked up to a generator/battery.
/// </summary>
public sealed record PowerDevice(PlacedPart Part, bool Connected, IReadOnlyList<int> InputTiles);

/// <summary>
/// The ship's power network as a set of tile-pair segments (for drawing) plus per-device connectivity. Segment
/// endpoints are grid tile indices; <see cref="PoweredSegments"/> are lit (reachable from a live source),
/// <see cref="UnpoweredSegments"/> are orphaned conduit runs.
/// </summary>
public sealed record PowerResult(
    IReadOnlyList<(int A, int B)> PoweredSegments,
    IReadOnlyList<(int A, int B)> UnpoweredSegments,
    IReadOnlySet<int> PoweredTiles,
    IReadOnlyList<PowerDevice> Devices)
{
    public static readonly PowerResult Empty =
        new([], [], new HashSet<int>(), []);

    /// <summary>True when the ship has any power path or wired device at all — i.e. PowerViz has something to show.</summary>
    public bool HasNetwork => PoweredSegments.Count > 0 || UnpoweredSegments.Count > 0 || Devices.Count > 0;

    /// <summary>Wired devices that are not reached by any live source.</summary>
    public IEnumerable<PowerDevice> Unconnected => Devices.Where(d => !d.Connected);
}

/// <summary>
/// The power network expressed in <b>document tile coordinates</b>, ready to draw: segment endpoints are tiles
/// (drawn centre-to-centre) and <see cref="UnconnectedPlugs"/> are the plug cells of wired devices with no live
/// feed. A flat, UI-free payload the canvas renders directly without holding the grid. Built by
/// <see cref="PowerNetwork.ToOverlay"/>.
/// </summary>
public sealed record PowerOverlay(
    IReadOnlyList<((int X, int Y) A, (int X, int Y) B)> Powered,
    IReadOnlyList<((int X, int Y) A, (int X, int Y) B)> Unpowered,
    IReadOnlyList<(int X, int Y)> UnconnectedPlugs)
{
    public static readonly PowerOverlay Empty = new([], [], []);

    public bool IsEmpty => Powered.Count == 0 && Unpowered.Count == 0 && UnconnectedPlugs.Count == 0;
}

/// <summary>
/// Port of the game's <c>TileUtils.GetPoweredTiles</c> (verified 0.15.1.6). Power flows from installed sources
/// (<c>IsPowerGen</c> / <c>IsPowerStorage</c> / <c>IsRechargingContainer</c>, not <c>IsOverrideOff</c>) that carry a
/// <c>PowerOutput</c> map point: a 4-cardinal BFS from each source's output tile spreads over tiles carrying
/// <c>IsPowerPath</c> (conduits via <c>TILPowerConduit</c>, powered fixtures via <c>TILPowerFixtureAdds</c>). Tiles
/// the flood reaches are powered; the segments walked are the lit runs. Remaining <c>IsPowerPath</c> tiles the flood
/// never reaches are orphaned (unpowered) runs. A wired device is hooked up when one of its input-point tiles is
/// powered — its own footprint carries <c>IsPowerPath</c>, so being on the live set is exactly "connected".
///
/// This is connectivity visualisation, not the game's per-tick power-draw simulation (a non-goal); no amounts,
/// tickers or override toggles are modelled beyond the source's static <c>IsOverrideOff</c> state.
/// </summary>
public static class PowerNetwork
{
    private static readonly string[] SourceConds = ["IsPowerGen", "IsPowerStorage", "IsRechargingContainer"];

    public static PowerResult Build(ShipGrid grid, Catalog catalog)
    {
        var powered = new HashSet<int>();
        var poweredSegs = new HashSet<(int, int)>();

        // 1. Flood from every installed power source's output tile over connected IsPowerPath tiles.
        foreach (var part in grid.Parts)
        {
            if (catalog.Lookup(part.Part.DefName) is not { } def) continue;
            if (def.PowerOutputPoint is not { } outPt) continue;
            if (!def.StartingConds.Contains("IsInstalled")) continue;
            if (def.StartingConds.Contains("IsOverrideOff")) continue;
            if (!SourceConds.Any(def.StartingConds.Contains)) continue;

            var seed = grid.MapPointTile(part, outPt);
            if (seed < 0) continue;

            // BFS. The seed (the source's output tile) is powered; it only spreads if it is itself on a power path.
            var queue = new Queue<int>();
            var seen = new HashSet<int> { seed };
            powered.Add(seed);
            queue.Enqueue(seed);
            while (queue.Count > 0)
            {
                var t = queue.Dequeue();
                if (!grid.Has(t, "IsPowerPath")) continue;
                foreach (var nt in Cardinals(grid, t))
                {
                    if (nt < 0 || !grid.Has(nt, "IsPowerPath")) continue;
                    poweredSegs.Add(Seg(t, nt));
                    if (seen.Add(nt)) { powered.Add(nt); queue.Enqueue(nt); }
                }
            }
        }

        // 2. Any IsPowerPath tile the flood never reached is an orphaned run — flood those separately.
        var unpoweredSegs = new HashSet<(int, int)>();
        var offSeen = new HashSet<int>();
        for (var t0 = 0; t0 < grid.TileCount; t0++)
        {
            if (powered.Contains(t0) || offSeen.Contains(t0) || !grid.Has(t0, "IsPowerPath")) continue;
            var queue = new Queue<int>();
            offSeen.Add(t0);
            queue.Enqueue(t0);
            while (queue.Count > 0)
            {
                var t = queue.Dequeue();
                foreach (var nt in Cardinals(grid, t))
                {
                    if (nt < 0 || powered.Contains(nt) || !grid.Has(nt, "IsPowerPath")) continue;
                    unpoweredSegs.Add(Seg(t, nt));
                    if (offSeen.Add(nt)) queue.Enqueue(nt);
                }
            }
        }

        // 3. Classify each wired device (a part with resolved power-input points) as connected or not.
        var devices = new List<PowerDevice>();
        foreach (var part in grid.Parts)
        {
            if (catalog.Lookup(part.Part.DefName) is not { PowerInputPoints.Count: > 0 } def) continue;
            var inputTiles = def.PowerInputPoints
                .Select(pt => grid.MapPointTile(part, pt))
                .Where(t => t >= 0)
                .ToArray();
            var connected = inputTiles.Any(powered.Contains);
            devices.Add(new PowerDevice(part, connected, inputTiles));
        }

        return new PowerResult(
            poweredSegs.ToArray(), unpoweredSegs.ToArray(), powered, devices);
    }

    /// <summary>Project a <see cref="PowerResult"/> into document-coordinate segments/plugs for the canvas.</summary>
    public static PowerOverlay ToOverlay(ShipGrid grid, PowerResult result)
    {
        (int, int) Doc(int idx) => grid.GridToDoc(idx);
        ((int X, int Y), (int X, int Y)) Segment((int A, int B) s) => (Doc(s.A), Doc(s.B));

        var plugs = result.Unconnected
            .SelectMany(d => d.InputTiles)
            .Distinct()
            .Select(Doc)
            .ToArray();

        return new PowerOverlay(
            result.PoweredSegments.Select(Segment).ToArray(),
            result.UnpoweredSegments.Select(Segment).ToArray(),
            plugs);
    }

    /// <summary>A segment keyed order-independently so the two BFS directions record it once.</summary>
    private static (int, int) Seg(int a, int b) => a < b ? (a, b) : (b, a);

    /// <summary>N, W, E, S neighbours; −1 off the grid edge (mirrors the game's cardinal GetSurroundingTiles).</summary>
    private static IEnumerable<int> Cardinals(ShipGrid grid, int t)
    {
        var col = grid.Col(t);
        var row = grid.Row(t);
        yield return row > 0 ? t - grid.NCols : -1;
        yield return col > 0 ? t - 1 : -1;
        yield return col < grid.NCols - 1 ? t + 1 : -1;
        yield return row < grid.NRows - 1 ? t + grid.NCols : -1;
    }
}
