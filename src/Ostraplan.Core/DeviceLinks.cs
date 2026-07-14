namespace Ostraplan.Core;

/// <summary>
/// A signal connection between two installed, signalable devices — the game's <c>Electrical</c> GPM wiring
/// (sensor → alarm/pump/light, a controller driving fixtures). It is <b>directional</b>: <see cref="Source"/> drives
/// <see cref="Target"/> (on export the source's <c>outputConnections</c> lists the target and the target's
/// <c>inputConnections</c> lists the source; see <see cref="ShipExport"/>). Endpoints are held by
/// <see cref="Placement.Id"/> so a link survives a move/rotate and heals across a delete+undo; a link to a placement
/// that no longer exists is inert and pruned on save/export. The game stores connections by id with <b>no</b>
/// distance/adjacency rule, so validity is just "both ends are installed signalable parts" (see <see cref="DeviceLinks"/>).
/// Gate/threshold logic is the game's own (the signal box); Ostraplan authors only the plain connection.
/// </summary>
public readonly record struct DeviceLink(Guid Source, Guid Target);

/// <summary>Validity rules and lookups for <see cref="DeviceLink"/>s — the whole "legal &amp; valid" surface for
/// device wiring, which (unlike placement) has no geometric component: a connection is legal iff both ends are
/// distinct installed parts carrying <c>IsSignalable</c>.</summary>
public static class DeviceLinks
{
    /// <summary>True when <paramref name="p"/> can take part in signal wiring — an installed part that owns an
    /// <c>Electrical</c> GPM (<see cref="PartDef.IsSignalable"/>). Uninstalled/loose forms don't wire.</summary>
    public static bool IsConnectable(ShipDocument doc, Placement p) =>
        doc.Part(p) is { IsSignalable: true } part && part.StartingConds.Contains("IsInstalled");

    /// <summary>Whether a directed connection <paramref name="source"/> → <paramref name="target"/> is legal and
    /// not already present: distinct connectable parts, and no identical existing link. Reverse (target → source)
    /// is a separate, independently-allowed connection, matching the game's directional model.</summary>
    public static bool CanConnect(ShipDocument doc, Placement source, Placement target) =>
        !ReferenceEquals(source, target)
        && IsConnectable(doc, source) && IsConnectable(doc, target)
        && !doc.Links.Contains(new DeviceLink(source.Id, target.Id));

    /// <summary>The links whose source or target is <paramref name="p"/> (for the "remove this device's wires"
    /// action and the hover highlight).</summary>
    public static IEnumerable<DeviceLink> Touching(ShipDocument doc, Placement p) =>
        doc.Links.Where(l => l.Source == p.Id || l.Target == p.Id);

    /// <summary>The links whose <b>both</b> endpoints still resolve to a placement in the document — the set that is
    /// rendered, exported and persisted (a dangling link, left by an un-undone delete, is skipped).</summary>
    public static IEnumerable<(DeviceLink Link, Placement Source, Placement Target)> Resolved(ShipDocument doc)
    {
        foreach (var l in doc.Links)
            if (doc.ById(l.Source) is { } s && doc.ById(l.Target) is { } t)
                yield return (l, s, t);
    }
}
