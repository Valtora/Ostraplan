namespace Ostraplan.Core;

/// <summary>
/// A <b>breaker connection</b> between two installed devices — the game's <c>Electrical</c> GPM graph. It is
/// <b>directional</b>: <see cref="Source"/> drives <see cref="Target"/> (on export the source's
/// <c>outputConnections</c> lists the target and the target's <c>inputConnections</c> lists the source; see
/// <see cref="ShipExport"/>). Endpoints are held by <see cref="Placement.Id"/> so a link survives a move/rotate
/// and heals across a delete+undo; a link to a placement that no longer exists is inert and pruned on
/// save/export.
///
/// <para>This is one of the game's <b>two</b> signal channels and the narrower one. Only a breaker box creates
/// it (<c>GUIBreaker.SetInput</c> → <c>Electrical.SetUpConnection</c>), and what it does is switch the driven
/// device on and off: the target gains <c>IsConnected</c>/<c>IsSignalledOn</c> and is held shut down while
/// connected-but-unsignalled. A sensor driving a pump is the <b>other</b> channel — see
/// <see cref="SensorLink"/> — and wiring one here does nothing at all in game, which is why
/// <see cref="DeviceLinks.CanConnect"/> no longer allows it.</para>
///
/// <para>The stored model has <b>no</b> distance or adjacency rule; validity is entirely a question of what the
/// two defs declare (see <see cref="DeviceLinks"/>). Gate/threshold logic is the game's own and out of
/// scope.</para>
/// </summary>
public readonly record struct DeviceLink(Guid Source, Guid Target);

/// <summary>
/// A <b>sensor connection</b>: <see cref="Source"/> is the alarm or thermostat that <see cref="Target"/> follows.
/// This is the game's other signal channel, and the one nearly every stock ship uses — 1,780 of them across
/// <c>data/ships</c>, an O2 alarm driving an air pump or a thermostat driving a cooler.
///
/// <para>It is stored quite differently from a <see cref="DeviceLink"/>: not in the <c>Electrical</c> GPM at all,
/// but as a single <c>strInput01</c> key on the <b>driven device's own</b> control panel, which
/// <c>GasPump.UpdateRemote</c> and <c>Heater.UpdateRemote</c> read back into the CO they test each tick. A device
/// with no sensor tests <i>itself</i> for a condition only a tripped alarm ever carries
/// (<c>IsReadyPumpAir</c>, <c>DcGasTemp01</c>/<c>DcGasTemp03</c>), so it never runs — an unwired pump, contaminant
/// scrubber, heater or cooler is dead unless a player forces it on by hand. That is what makes this link the
/// thing a design has to carry rather than a refinement. (The CO2 scrubber is the exception: its gas-respire
/// names no signal trigger, so it falls back to the blank one and runs regardless.)</para>
///
/// <para>Each device follows at most <b>one</b> sensor — there is a single <c>strInput01</c> — so adding a second
/// link to the same target replaces the first (see <see cref="SensorLinks.Replacing"/>).</para>
/// </summary>
public readonly record struct SensorLink(Guid Source, Guid Target);

/// <summary>
/// Which of the game's two signal channels a wire belongs to. The editor never asks the user: a part can source
/// exactly one of them (a breaker box drives, a sensor is followed), so arming a source picks the channel.
/// </summary>
public enum WireChannel
{
    /// <summary>The <c>Electrical</c> GPM graph — a breaker box switching devices on and off (see
    /// <see cref="DeviceLink"/>).</summary>
    Breaker,

    /// <summary>A sensor a device follows, through that device's own <c>strInput01</c> (see
    /// <see cref="SensorLink"/>). This is the channel that makes pumps, scrubbers, heaters and coolers run.</summary>
    Sensor,
}

/// <summary>
/// Which end of a connection a wiring gesture was started from. Both are natural: from an alarm or a signal box
/// you are choosing what it <b>drives</b>, and from a pump you are choosing the sensor it <b>follows</b>. The
/// direction of the resulting link is the same either way; this only says which half the user already had.
/// </summary>
public enum WireEnd
{
    /// <summary>The gesture started at the driving end — a signal box, or a sensor.</summary>
    Driver,

    /// <summary>The gesture started at the driven end — a pump, scrubber, heater or cooler.</summary>
    Driven,
}

/// <summary>Validity rules and lookups for <see cref="DeviceLink"/>s — breaker wiring. Like placement law this is
/// a port of the game's own rule, and like the rest of the wiring model it has no geometric component: what
/// matters is which control panels the two defs declare.</summary>
public static class DeviceLinks
{
    /// <summary>
    /// True when <paramref name="p"/> can be the <b>source</b> of a breaker connection: an installed part
    /// declaring a <c>GUIBreaker</c> panel. That class is the only thing in the game that calls
    /// <c>Electrical.SetUpConnection</c>, and the stock ships bear it out — across all of <c>data/ships</c> the
    /// only def ever appearing as the source of an <c>outputConnections</c> entry is <c>ItmElectricalBox01</c>.
    ///
    /// <para>Ostraplan used to allow any signalable part here, which let a design draw an alarm-drives-pump link
    /// on this channel. Such a link is inert: a device propagates a signal only when its own gate result
    /// <i>changes</i>, and a source with no inputs of its own resolves true once at load and never changes again.
    /// Worse, the target then loads holding an unsignalled input connection, which raises <c>IsSignalOff</c> and
    /// shuts it down. So the narrower rule authors strictly more working ships.</para>
    /// </summary>
    public static bool CanSource(ShipDocument doc, Placement p) =>
        doc.Part(p) is { } part
        && part.StartingConds.Contains("IsInstalled")
        && DevicePanels.BreakerPanel(doc.Catalog, part) is not null;

    /// <summary>
    /// True when <paramref name="target"/> may be driven by <paramref name="source"/>'s breaker: it satisfies the
    /// breaker panel's <c>strValidCOTrigger01</c>. Stock names <c>TIsSignalOpen</c> there, which is
    /// <c>IsSignalable</c> + <c>IsInstalled</c> — the rule Ostraplan once applied to every part on every channel,
    /// now read from the panel that actually declares it.
    /// </summary>
    public static bool CanTarget(ShipDocument doc, Placement source, Placement target) =>
        doc.Part(source) is { } sourcePart
        && DevicePanels.BreakerPanel(doc.Catalog, sourcePart) is { } panel
        && doc.Part(target) is { } targetPart
        && DevicePanels.Satisfies(doc.Catalog, targetPart, panel.ValidSourceTrigger);

    /// <summary>True when <paramref name="p"/> can take part in breaker wiring at either end — what the editor
    /// rings while the channel is live.</summary>
    public static bool IsConnectable(ShipDocument doc, Placement p) =>
        CanSource(doc, p) || IsSignalOpen(doc, p);

    /// <summary>The stock breaker target rule (<c>TIsSignalOpen</c>) as a standalone test, for ringing candidate
    /// targets before any source is armed. The real check once a source exists is
    /// <see cref="CanTarget"/>, which reads the trigger off that source's own panel.</summary>
    public static bool IsSignalOpen(ShipDocument doc, Placement p) =>
        doc.Part(p) is { IsSignalable: true } part && part.StartingConds.Contains("IsInstalled");

    /// <summary>Whether a directed connection <paramref name="source"/> → <paramref name="target"/> is legal and
    /// not already present: distinct parts, a breaker source, a target its panel admits, and no identical
    /// existing link. Reverse is a separate, independently-allowed connection, matching the game's directional
    /// model.</summary>
    public static bool CanConnect(ShipDocument doc, Placement source, Placement target) =>
        !ReferenceEquals(source, target)
        && CanSource(doc, source)
        && CanTarget(doc, source, target)
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

/// <summary>Validity rules and lookups for <see cref="SensorLink"/>s — a sensor driving a device. The rule comes
/// entirely from the driven device's own control panel: it must offer a sensor input, and the sensor must satisfy
/// that panel's <c>strValidCOTrigger01</c> (<c>TIsAlarm2</c> on pumps and scrubbers, so any alarm;
/// <c>TIsAlarmTemp</c> on heaters and coolers, so the thermostat alone).</summary>
public static class SensorLinks
{
    /// <summary>True when <paramref name="p"/> can be <b>driven</b> by a sensor: an installed part whose def
    /// declares a panel with a <c>strInput01</c> socket.</summary>
    public static bool CanTarget(ShipDocument doc, Placement p) => Panel(doc, p) is not null;

    /// <summary>The sensor-input panel <paramref name="p"/> declares, or null when it takes no sensor. Null for an
    /// uninstalled form: a pump in a crate wires to nothing.</summary>
    public static DevicePanel? Panel(ShipDocument doc, Placement p) =>
        doc.Part(p) is { } part && part.StartingConds.Contains("IsInstalled")
            ? DevicePanels.SensorPanel(doc.Catalog, part)
            : null;

    /// <summary>True when <paramref name="source"/> may drive <paramref name="target"/>: <paramref name="target"/>
    /// takes a sensor and <paramref name="source"/> satisfies its panel's trigger. The source must be installed
    /// too — the game's selector lists condition owners aboard the ship, and an alarm in a locker is not one.</summary>
    public static bool CanDrive(ShipDocument doc, Placement source, Placement target) =>
        !ReferenceEquals(source, target)
        && Panel(doc, target) is { } panel
        && doc.Part(source) is { } sourcePart
        && sourcePart.StartingConds.Contains("IsInstalled")
        && DevicePanels.Satisfies(doc.Catalog, sourcePart, panel.ValidSourceTrigger);

    /// <summary>True when <paramref name="p"/> could drive <b>something</b> on this channel — it satisfies the
    /// sensor trigger of at least one device kind in the catalog. Used to ring candidate sources before a target
    /// is picked; the real check once a target exists is <see cref="CanDrive"/>. Catalog-wide rather than
    /// document-wide on purpose: an alarm is a sensor whether or not this particular design has a pump yet.</summary>
    public static bool CanSource(ShipDocument doc, Placement p) =>
        doc.Part(p) is { } part
        && part.StartingConds.Contains("IsInstalled")
        && doc.Catalog.IsSensorSource(part);

    /// <summary>True when <paramref name="p"/> can take part in sensor wiring at either end.</summary>
    public static bool IsConnectable(ShipDocument doc, Placement p) => CanTarget(doc, p) || CanSource(doc, p);

    /// <summary>The sensor currently driving <paramref name="target"/>, or null. A device follows at most one.</summary>
    public static SensorLink? Driving(ShipDocument doc, Placement target) =>
        doc.SensorLinks.Cast<SensorLink?>().FirstOrDefault(l => l!.Value.Target == target.Id);

    /// <summary>The link a new <paramref name="link"/> would displace, since a device follows at most one sensor.
    /// Null when the target is currently unwired. The command layer removes it in the same undo step.</summary>
    public static SensorLink? Replacing(ShipDocument doc, SensorLink link) =>
        doc.SensorLinks.Cast<SensorLink?>().FirstOrDefault(l => l!.Value.Target == link.Target && l.Value != link);

    /// <summary>The links whose source or target is <paramref name="p"/>.</summary>
    public static IEnumerable<SensorLink> Touching(ShipDocument doc, Placement p) =>
        doc.SensorLinks.Where(l => l.Source == p.Id || l.Target == p.Id);

    /// <summary>The links whose <b>both</b> endpoints still resolve to a placement — the set rendered, exported
    /// and persisted.</summary>
    public static IEnumerable<(SensorLink Link, Placement Source, Placement Target)> Resolved(ShipDocument doc)
    {
        foreach (var l in doc.SensorLinks)
            if (doc.ById(l.Source) is { } s && doc.ById(l.Target) is { } t)
                yield return (l, s, t);
    }
}
