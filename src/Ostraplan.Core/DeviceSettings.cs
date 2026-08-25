namespace Ostraplan.Core;

/// <summary>
/// A device's three-position bus knob (<c>nKnobBus</c> on its control panel) — the game's own manual override,
/// read by <c>GasPump.UpdateRemote</c> and applied as conditions before anything else is considered.
/// </summary>
public enum DeviceBusMode
{
    /// <summary>Forced off. Grants <c>IsOverrideOff</c>, which stops the device dead and also makes
    /// <c>Powered.Run</c> shut it down, so it draws nothing either.</summary>
    Off = 0,

    /// <summary>Follow the signal. Grants neither override cond, so the device runs when the sensor it follows is
    /// tripped — and, if it follows nothing, never (see <see cref="SensorLink"/>). The default, and what 1,405 of
    /// the 1,758 stock devices carrying the key are set to.</summary>
    Auto = 1,

    /// <summary>Forced on. Grants <c>IsOverrideOn</c>, which runs the device regardless of any sensor. This is the
    /// only way an <b>unwired</b> pump, contaminant scrubber, heater or cooler ever runs.</summary>
    On = 2,
}

/// <summary>
/// What the designer set on a device's own control panel: which way its bus knob is turned and which of its
/// optional modes are on. These are the game's authored panel keys (<c>nKnobBus</c>, <c>bTurbo</c>,
/// <c>bReverse</c>, <c>bSlowMode</c>), the ones a player would set by opening the device in game; everything else
/// on the panel is a template constant supplied by the def.
///
/// <para>The sensor a device follows is <b>not</b> here. It is a relationship between two placements, so it lives
/// in <see cref="SensorLink"/> where a move, a delete and an undo can keep it honest, exactly as a
/// <see cref="DeviceLink"/> does.</para>
///
/// <para>A mode is only meaningful where the def declares the matching condition, which is how the game gates its
/// own controls (<c>GUIAirPump.LoadCOStats</c> hides a checkbox whose cond is absent). Authoring one anyway is not
/// merely useless: <c>GasPump.UpdateRemote</c> grants <c>IsTurboOn</c> from <c>bTurbo</c> without checking, while
/// the rate multiplier it then reads off <c>IsTurbo</c> is zero on a def that does not declare it, so the pump
/// stops. <see cref="Applicable"/> is the gate, and the export applies it.</para>
/// </summary>
public sealed record DeviceSettings
{
    /// <summary>Which way the bus knob is turned. <see cref="DeviceBusMode.Auto"/> unless the designer moved it.</summary>
    public DeviceBusMode Bus { get; init; } = DeviceBusMode.Auto;

    /// <summary>Turbo mode (<c>bTurbo</c>) — a higher throughput at a higher power draw. Meaningful only on a def
    /// declaring <c>IsTurbo</c>, which no stock 1.0.0.13 def does.</summary>
    public bool Turbo { get; init; }

    /// <summary>Reverse mode (<c>bReverse</c>) — pump the other way. Stock: <c>ItmAirPump02</c> only.</summary>
    public bool Reverse { get; init; }

    /// <summary>Slow mode (<c>bSlowMode</c>) — a tenth of the rate. Stock: <c>ItmAirPump02</c> only.</summary>
    public bool Slow { get; init; }

    /// <summary>Everything at its default: bus on auto, no mode set. The document stores null rather than one of
    /// these, so a design only carries settings somebody actually changed.</summary>
    public static readonly DeviceSettings Default = new();

    public bool IsDefault => Equals(Default);

    /// <summary>This settings object, or null when it is the default — the form the document and the
    /// <c>.oplan</c> store, so an untouched device costs nothing.</summary>
    public DeviceSettings? OrNull() => IsDefault ? null : this;

    /// <summary>
    /// Whether <paramref name="cond"/>'s mode may be authored on <paramref name="part"/>: only where the def
    /// declares it, matching the gate the game's own panel applies. Pass one of
    /// <see cref="DevicePanels.TurboCond"/>, <see cref="DevicePanels.ReverseCond"/>,
    /// <see cref="DevicePanels.SlowCond"/>.
    /// </summary>
    public static bool Applicable(PartDef? part, string cond) =>
        part is not null && part.StartingConds.Contains(cond);

    /// <summary>This settings object with every mode the def does not offer cleared — what the export writes, and
    /// what a settings edit is normalised through so a re-skin to a def without turbo cannot leave a stale flag
    /// behind.</summary>
    public DeviceSettings ClampTo(PartDef? part) => this with
    {
        Turbo = Turbo && Applicable(part, DevicePanels.TurboCond),
        Reverse = Reverse && Applicable(part, DevicePanels.ReverseCond),
        Slow = Slow && Applicable(part, DevicePanels.SlowCond),
    };
}
