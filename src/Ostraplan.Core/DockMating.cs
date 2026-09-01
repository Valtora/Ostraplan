namespace Ostraplan.Core;

/// <summary>One cell of the receiver that an incoming cell cannot share, and the incoming cell that hit it.
/// <paramref name="DocTile"/> is the incoming cell's document tile when the incoming ship is a design, which is
/// what lets the canvas highlight the part of <i>your</i> hull that is in the way.</summary>
public sealed record DockBlock(
    (int X, int Y) ReceiverCell, (int X, int Y) IncomingCell, (int X, int Y)? DocTile,
    string ReceiverDef, string IncomingDef);

/// <summary>One port pair and whether the two hulls clear each other at the pose that mates them.</summary>
/// <param name="Blocks">Empty when they mate. Otherwise every colliding cell, so the reason is showable rather
/// than merely reportable.</param>
public sealed record DockMate(
    DockPort ReceiverPort, DockPort IncomingPort, bool Mates, IReadOnlyList<DockBlock> Blocks,
    DockPoseTransform? Pose);

/// <summary>The full cross product of one design's ports against another's — the shape
/// <c>GetAvailableDockingPorts(incoming, earlyOut: false)</c> returns.</summary>
public sealed record DockReport(DockShip Receiver, DockShip Incoming, IReadOnlyList<DockMate> Pairs)
{
    public bool AnyMate => Pairs.Any(p => p.Mates);

    public DockMate? For(DockPort receiverPort, DockPort incomingPort) =>
        Pairs.FirstOrDefault(p => p.ReceiverPort.ItemId == receiverPort.ItemId
                               && p.IncomingPort.ItemId == incomingPort.ItemId);
}

/// <summary>
/// Whether two ships can hard-dock, ported from <c>Ship.GetAvailableDockingPorts</c> and the
/// <c>GridUtils</c> overlay beneath it (verified against Ostranauts 1.0.0.13).
///
/// <para><b>There is no port compatibility table.</b> Docking legality is purely geometric: rotate the incoming
/// ship so its port faces ours, step it one tile off along that face, lay its whole grid over ours, and refuse
/// if any cell collides. <c>IsTypeB</c>, which is what separates a Primary airlock from a Secondary, decides
/// only which port bounds construction (GAME-INTERNALS §6) and takes no part in this at all.</para>
///
/// <para><b>The Blank halo is the rule that bites.</b> Every item stamps <c>"Blank"</c> into its eight empty
/// neighbours, and a Blank cell collides with anything that is not itself Blank or a docking port. So two hulls
/// must stay a full tile apart everywhere, and the only place they may close up is the seam, where each collar
/// lands on the other's halo. The two exceptions in <c>AllowedToOverlap</c> exist for exactly that seam; the
/// collars do <b>not</b> interpenetrate.</para>
///
/// <para><b>The check is directional.</b> The receiver's cells are the ones tested, and an incoming cell falling
/// outside the receiver's declared bounds is skipped rather than refused. Ostraplan puts the current design on
/// the <b>incoming</b> side, because that is the way round a player meets it: you fly your design in to a
/// station or another hull.</para>
/// </summary>
public static class DockMating
{
    /// <summary>Every port pair between two ships. Both lists may be empty (42 stock templates carry no port at
    /// all), which is a report with no rows rather than a failure.</summary>
    public static DockReport Cross(DockShip receiver, DockShip incoming)
    {
        var pairs = new List<DockMate>(receiver.Ports.Count * incoming.Ports.Count);
        foreach (var rp in receiver.Ports)
            foreach (var ip in incoming.Ports)
                pairs.Add(Mate(receiver, incoming, rp, ip));
        return new DockReport(receiver, incoming, pairs);
    }

    /// <summary>
    /// One port pair. Rotates and offsets the incoming grid onto the receiver's
    /// (<c>GetIncomingDockRotation</c> + <c>CreateOffset</c>) and overlays it (<c>CanOverlayBOnA</c>).
    /// </summary>
    public static DockMate Mate(DockShip receiver, DockShip incoming, DockPort receiverPort, DockPort incomingPort)
    {
        if (!TryIncomingRotation(receiverPort.Rotation, incomingPort.Rotation, out var rotation, out var dockOffset))
            return new DockMate(receiverPort, incomingPort, false, [], null);

        var t = CreateOffset(receiverPort.Anchor, incomingPort.Anchor,
            incoming.Grid.Height, incoming.Grid.Width, rotation, dockOffset);
        var blocks = Overlay(receiver, incoming, t);
        return new DockMate(receiverPort, incomingPort, blocks.Count == 0, blocks,
            new DockPoseTransform(t.Rotation, t.OffsetRow, t.OffsetCol, incoming.Grid.Height, incoming.Grid.Width));
    }

    /// <summary>
    /// <c>GridUtils.GetIncomingDockRotation</c>: the CCW turn that leaves the incoming port facing ours, plus
    /// the one-tile step along that face which keeps the two collars adjacent rather than coincident.
    ///
    /// <para>The game spins its <c>while</c> loop until the two rotations agree, which never terminates for a
    /// rotation that is not a multiple of 90. Four turns is every angle that can ever match, so a fifth would be
    /// that hang; it is reported as "cannot mate" instead.</para>
    /// </summary>
    private static bool TryIncomingRotation(
        double receiverRot, double incomingRot, out int rotation, out (int X, int Y) dockOffset)
    {
        var target = (receiverRot + 180) % 360;
        dockOffset =
            Math.Abs(target - 270) < 0.1 ? (-1, 0)
            : Math.Abs(target - 90) < 0.1 ? (1, 0)
            : Math.Abs(target - 180) < 0.1 ? (0, 1)
            : (0, -1);

        var current = incomingRot;
        rotation = 0;
        for (var turn = 0; turn < 4; turn++)
        {
            if (Math.Abs(current - target) <= 0.01) return true;
            current = (current + 90) % 360;
            rotation += 90;
        }
        return false;
    }

    /// <summary><c>GridUtils.CreateOffset</c>: where the rotated incoming grid sits so its port lands one tile
    /// beyond ours. Row and column are the game's own order (row is y).</summary>
    private static (int Rotation, int OffsetRow, int OffsetCol) CreateOffset(
        (int X, int Y) portUs, (int X, int Y) portIncoming, int incomingHeight, int incomingWidth,
        int rotation, (int X, int Y) dockOffset)
    {
        var (row, col) = Turn(portIncoming.Y, portIncoming.X, incomingHeight, incomingWidth, rotation);
        return (rotation, portUs.Y - row + dockOffset.Y, portUs.X - col + dockOffset.X);
    }

    /// <summary>
    /// <c>GridUtils.CanOverlayBOnA</c>: lay every occupied cell of the incoming grid onto the receiver's and
    /// collect what collides.
    ///
    /// <para>An incoming cell landing outside the receiver's declared bounds is <b>skipped</b>, not refused,
    /// which is what lets a ship hang off the edge of a station. Combined with <see cref="DockGrid"/>'s
    /// left-edge quirk it is also the whole of the check's asymmetry.</para>
    /// </summary>
    private static IReadOnlyList<DockBlock> Overlay(
        DockShip receiver, DockShip incoming, (int Rotation, int OffsetRow, int OffsetCol) t)
    {
        var a = receiver.Grid;
        var b = incoming.Grid;
        var blocks = new List<DockBlock>();

        foreach (var ((bx, by), incomingCell) in b.Cells)
        {
            var (row, col) = Turn(by, bx, b.Height, b.Width, t.Rotation);
            row += t.OffsetRow;
            col += t.OffsetCol;
            if (row < 0 || row >= a.Height || col < 0 || col >= a.Width) continue;
            if (a[col, row] is not { } receiverCell) continue;
            if (AllowedToOverlap(receiverCell, incomingCell)) continue;
            blocks.Add(new DockBlock((col, row), (bx, by), incoming.DocTileOf(bx, by),
                receiverCell.DefName, incomingCell.DefName));
        }

        // A dictionary walk has no defined order, and a blocked-cell list that reshuffles between runs would
        // make the report's highlight jump about for no reason the user can see.
        blocks.Sort((l, r) => l.ReceiverCell.Y != r.ReceiverCell.Y
            ? l.ReceiverCell.Y.CompareTo(r.ReceiverCell.Y)
            : l.ReceiverCell.X.CompareTo(r.ReceiverCell.X));
        return blocks;
    }

    /// <summary>
    /// <c>GridUtils.AllowedToOverlap</c>, verbatim. Blank on Blank, Blank taking a port, and a port landing on
    /// Blank. Everything else collides, port on port included.
    /// </summary>
    private static bool AllowedToOverlap(DockCell cell, DockCell incomingCell) =>
        (cell.IsBlank && (incomingCell.IsBlank || incomingCell.IsDockSys))
        || (cell.IsDockSys && incomingCell.IsBlank);

    /// <summary>The game's <c>StepRot90CCW</c> chain, which rotates a (row, col) inside an h-by-w grid and swaps
    /// the dimensions on each quarter turn.</summary>
    internal static (int Row, int Col) Turn(int row, int col, int h, int w, int rotation)
    {
        switch (((rotation % 360) + 360) % 360)
        {
            case 90:
                return Step(row, col, h);
            case 180:
            {
                var (r, c) = Step(row, col, h);
                return Step(r, c, w);
            }
            case 270:
            {
                var (r, c) = Step(row, col, h);
                (r, c) = Step(r, c, w);
                return Step(r, c, h);
            }
            default:
                return (row, col);
        }

        static (int Row, int Col) Step(int r, int c, int h) => (c, h - 1 - r);
    }
}
