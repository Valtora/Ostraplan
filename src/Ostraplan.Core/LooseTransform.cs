using System;
using System.Collections.Generic;
using System.Linq;

namespace Ostraplan.Core;

/// <summary>
/// Where a set of loose items would land under a group transform, and whether they may. Structure stacks freely
/// (a floor, the wall on it and the light on that all share a tile), so a group move of placements never has to
/// ask permission; the loose overlay is one item per tile (see <see cref="ShipDocument.LooseFreeAt"/>), so the
/// same move has to be checked before it is committed. This is that check, kept out of the window so it can be
/// exercised without one.
/// </summary>
public static class LooseTransform
{
    /// <summary>
    /// The destination poses for <paramref name="moving"/>, or <b>null</b> when the transform cannot be honoured:
    /// something not in the set already holds a tile it needs, or two movers would land on the same tile.
    ///
    /// <para>The movers are exempt from each other's origins, which is what lets a cluster slide one tile along
    /// (every item's target is the tile the next one is vacating). Null is deliberately all-or-nothing: dropping
    /// just the blocked items would leave a group move half-applied, with items stranded behind the rest and no
    /// single undo that puts it back.</para>
    /// </summary>
    public static List<(LooseObject Obj, int X, int Y, int Rot)>? Poses(
        ShipDocument doc, IReadOnlyList<LooseObject> moving, Func<LooseObject, (int X, int Y, int Rot)> pose)
    {
        if (moving.Count == 0) return [];
        var ids = moving.Select(o => o.Id).ToHashSet();
        var taken = new HashSet<(int, int)>();
        var poses = new List<(LooseObject, int, int, int)>(moving.Count);
        foreach (var o in moving)
        {
            var (x, y, rot) = pose(o);
            if (!doc.LooseFreeAt(x, y, ids) || !taken.Add((x, y))) return null;
            poses.Add((o, x, y, rot));
        }
        return poses;
    }

    /// <summary>The status-bar reason a transform was refused, for the window to show. One string, because it makes
    /// no difference to the reader which of the two ways it collided.</summary>
    public const string Blocked =
        "Deck items stayed put — one of them would land on a tile another loose item already holds";
}
