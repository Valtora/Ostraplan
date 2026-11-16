namespace Ostraplan.Core;

/// <summary>
/// Where a gas vessel can be fed from: the tiles of a tank or canister that an RCS regulator's or an air pump's
/// gas input can land on and find it. The counterpart of <see cref="PartDef.GasInputPoints"/>, drawn on the tank
/// so the two can be lined up.
///
/// <para>The game finds a vessel by raycasting at the gas input's map point (<c>GetCOsAtWorldCoords1</c>), and an
/// item's collider is its sprite, so a vessel is found anywhere on its <see cref="Catalog.BodyBox"/>: the whole
/// footprint of a 1×1 RTA, the 3×3 middle of the big tanks. Every stock part with a gas input stands its own body
/// on floor beside the vessel it feeds from (the Hydra's centre needs floor and refuses an obstruction; the Kang
/// "2202"'s other half the same), so its input can only reach a body tile with an orthogonal neighbour off the
/// body. That leaves out the middle of a 3×3 tank, which no stock regulator can reach, and it is why a port is an
/// edge tile rather than any body tile.</para>
/// </summary>
public static class GasPorts
{
    /// <summary>True for a part a gas input can draw from: airtight, rated for pressure, with a volume, and not a
    /// person or a system object (<c>TIsRCSValidInput</c> requires <c>IsAirtight</c> and forbids <c>IsHuman</c> and
    /// <c>IsSystem</c>; the pressure and volume are what make it hold gas at all).</summary>
    public static bool IsVessel(PartDef part) =>
        part.StartingConds.Contains("IsAirtight")
        && !part.StartingConds.Contains("IsHuman") && !part.StartingConds.Contains("IsSystem")
        && part.StartingCondValues.GetValueOrDefault("StatGasPressureMax") > 0
        && part.StartingCondValues.GetValueOrDefault("StatVolume") > 0;

    /// <summary>
    /// The vessel's port tiles at <paramref name="rot"/>, as offsets from its rotated footprint's top-left: the
    /// body tiles with at least one orthogonal neighbour outside the body. Empty for a part that is not a vessel.
    /// </summary>
    public static IReadOnlyList<(int X, int Y)> Tiles(PartDef part, Catalog catalog, int rot = 0)
    {
        if (!IsVessel(part)) return [];
        var (bx, by, w, h) = catalog.BodyBox(part, rot);
        var ports = new List<(int X, int Y)>();
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                if (x == 0 || y == 0 || x == w - 1 || y == h - 1)
                    ports.Add((bx + x, by + y));
        return ports;
    }
}
