namespace Ostraplan.Core;

/// <summary>Where the frame a design is measured in came from. Reported alongside every anchor because the two
/// frames put world origin in different places.</summary>
public enum WorldFrame
{
    /// <summary>The frame the ship will have once Ostraplan puts it in the game. Both write paths anchor a fresh
    /// ship at the export grid's own origin, which is the bounding box minus its one-tile pad, so world origin lands
    /// just outside the top-left corner of the hull.</summary>
    AsExported,

    /// <summary>The frame the ship already has, carried in from the save or template it was imported out of. This
    /// is the honest answer for the ship the player is actually flying, and it is usually somewhere else entirely:
    /// on the shipped fleet world origin sits inside the hull 85% of the time.</summary>
    AsImported,
}

/// <summary>
/// Where world origin falls in <b>document</b> tile coords, plus where that came from.
///
/// <para>The game keeps every item at <c>vShipPos + (col, −row)</c>, so world origin is the grid tile
/// <c>(−vShipPos.x, vShipPos.y)</c>. Anything the game samples in world space rather than in grid space has to know
/// where that is: the wear shader's noise (<see cref="WearShader.PositionOffset"/>) is the reader. Micrometeoroids
/// used to be the other, when every ray aimed at world origin; since Ostranauts 1.1.0.1 they aim at a random tile
/// instead and the anchor no longer touches them (§26).</para>
///
/// <para>Centre frame, like the damage solvers (<see cref="TileFrame"/>): an integer is a tile's centre.</para>
/// </summary>
public sealed record WorldAnchor(double DocX, double DocY, WorldFrame Frame)
{
    /// <summary>
    /// Where world origin falls for this design, in document tile coords.
    ///
    /// <para>Prefers <paramref name="importedShipPos"/>, then the document's own import provenance
    /// (<see cref="ShipDocument.SourceShipPos"/>), so no caller has to remember to pass it and an imported ship
    /// cannot be measured in the wrong frame by omission. With neither, it falls back to the frame Ostraplan will
    /// write, which both export paths anchor at the grid origin: the bounding box minus its one-tile pad.</para>
    /// </summary>
    public static WorldAnchor For(ShipDocument doc, (double DocX, double DocY)? importedShipPos = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if ((importedShipPos ?? doc.SourceShipPos) is { } a)
            return new WorldAnchor(a.Item1, a.Item2, WorldFrame.AsImported);
        var b = doc.Bounds();
        return b is null
            ? new WorldAnchor(0, 0, WorldFrame.AsExported)
            : new WorldAnchor(b.Value.MinX - 1, b.Value.MinY - 1, WorldFrame.AsExported);
    }
}
