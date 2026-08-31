namespace Ostraplan.Core;

/// <summary>
/// The reactor's <c>knobBus</c> — what the core does with the batteries wired to it. The game's own labels, from
/// <c>GUI_REACTOR_PWRBUS</c>: "OFF: Reactor has no interaction with connected batteries / BATT: Reactor drains
/// connected batteries as starter power used during ignition / CHRG: Reactor charges connected batteries, as long
/// as reactor is running with a functioning MHD is attached and MHD toggle is on".
/// </summary>
public enum ReactorPowerBus
{
    /// <summary>OFF. <c>FusionIC.Update</c> reads this as the core being down: every module it drives is forced
    /// off with it.</summary>
    Off = 0,

    /// <summary>BATT — draw starter power off the batteries. What ignition needs, since the capacitors charge
    /// from the bus.</summary>
    Batt = 1,

    /// <summary>CHRG — charge the batteries off the MHD. What a ship spawning with its reactor already running
    /// is set to: 34 of the 57 reactor panels the stock ships author.</summary>
    Chrg = 2,
}

/// <summary>
/// The reactor's <c>knobPump</c> — the core purge, which pumps the core down to the vacuum ignition needs. The
/// game's own labels, from <c>GUI_REACTOR_COREPURGE</c>: "RGH: Rough purge removes low pressure contents or slowly
/// removes high-pressure contents / TRB: Turbo purge removes higher pressure contents". Both need a working core
/// pump attached to the core; <c>FusionIC</c> pumps down to 0.35 on RGH and to 0.10 on TRB.
/// </summary>
public enum ReactorCorePurge
{
    Off = 0,
    Rough = 1,
    Turbo = 2,
}

/// <summary>
/// What the designer set on a reactor's own control panel (<c>GUIReactor</c>, the <c>ReactorIC</c> prop map): the
/// two knobs, the torch-thrust toggle, the eight switches of the ignition sequence, and the two sliders. These are
/// the keys a player would set by opening the reactor in game, and the same ones the stock ships author on a
/// template so the ship spawns with its core already lit.
///
/// <para><b>They are not cosmetic.</b> <c>FusionIC.Update</c> — the reactor simulation, not the panel — reads
/// every one of them off <c>COSelf.GetGPMInfo("Panel A", …)</c> on each tick, so a core whose panel says
/// <c>knobBus 0</c> is a cold core whatever else the design says. The panel instance is <b>always</b> "Panel A":
/// both <c>FusionIC</c> and <c>Ship.GetReactorGPMValue</c> hardcode that name, and all thirteen stock defs
/// declaring the panel use it.</para>
///
/// <para>Unlike <see cref="DeviceSettings"/> there is no per-def gate on any of this. The game's own panel greys a
/// switch out on what is <b>attached to the core at runtime</b> (lasers, cryo pumps, field coils, an MHD), which a
/// design has no way to know and which <c>FusionIC</c> re-checks itself every tick: a switch set with the module
/// missing is turned back off rather than breaking anything.</para>
/// </summary>
public sealed record ReactorSettings
{
    /// <summary>The power bus knob (<c>knobBus</c>).</summary>
    public ReactorPowerBus Bus { get; init; } = ReactorPowerBus.Off;

    /// <summary>The core purge knob (<c>knobPump</c>).</summary>
    public ReactorCorePurge Purge { get; init; } = ReactorCorePurge.Off;

    /// <summary>Torch thrust (<c>knobRatio</c>): off sends the whole reaction to the MHD as electricity, on sends
    /// 95% of it out of the rear field coil as thrust. Written by the panel's own thrust toggle, which is why the
    /// key is a knob rather than a checkbox, and why <c>FusionIC</c> coerces anything that is not 1 to 0.</summary>
    public bool TorchThrust { get; init; }

    /// <summary>Laser Alignment (<c>chkAlign</c>). Required before ignition.</summary>
    public bool LaserAlign { get; init; }

    /// <summary>Forward Field Coil (<c>chkCoilFwd</c>).</summary>
    public bool CoilForward { get; init; }

    /// <summary>Rear Field Coil (<c>chkCoilRear</c>) — the one that guides plasma out as torch thrust.</summary>
    public bool CoilRear { get; init; }

    /// <summary>Cryo Pump (<c>chkCryo</c>).</summary>
    public bool Cryo { get; init; }

    /// <summary>Fuel Regulator (<c>chkFuelReg</c>). Required before ignition.</summary>
    public bool FuelRegulator { get; init; }

    /// <summary>Core Ignition (<c>chkIgnition</c>).</summary>
    public bool Ignition { get; init; }

    /// <summary>Magnetohydrodynamic generator (<c>chkMHDOn</c>) — needed for the bus to charge batteries.</summary>
    public bool Mhd { get; init; }

    /// <summary>Pellet Feeder (<c>chkPellet</c>). Required before ignition.</summary>
    public bool PelletFeed { get; init; }

    /// <summary>The Cycle slider (<c>slidCycle</c>), 0 to 1: how far the rear drive aperture is open, which is the
    /// torch's throttle (<c>FusionIC</c> multiplies it by the thrust ratio into <c>StatICThrustThrottle</c>).</summary>
    public double Cycle { get; init; }

    /// <summary>The Flow slider (<c>slidFlow</c>), 0 to 1: how fast fuel pellets are fed into the core, lerped
    /// between the idle rate and the feeder's maximum.</summary>
    public double Flow { get; init; }

    /// <summary>Everything as the <c>ReactorIC</c> template itself declares it: bus off, purge off, no switch set,
    /// both sliders at zero. A cold core, which is what a reactor spawns as unless a design says otherwise.</summary>
    public static readonly ReactorSettings Default = new();

    public bool IsDefault => Equals(Default);

    /// <summary>This object, or null when it is the default — the form the document and the <c>.oplan</c> store,
    /// so an untouched reactor costs nothing.</summary>
    public ReactorSettings? OrNull() => IsDefault ? null : this;

    /// <summary>Settings forced into range: both sliders to 0..1, both knobs to a position the game's own knob
    /// has. A design can arrive from an <c>.oplan</c> or an imported ship, so nothing downstream should have to
    /// defend itself against a cycle of 40.</summary>
    public ReactorSettings Clamped() => this with
    {
        Bus = Enum.IsDefined(Bus) ? Bus : ReactorPowerBus.Off,
        Purge = Enum.IsDefined(Purge) ? Purge : ReactorCorePurge.Off,
        Cycle = ClampUnit(Cycle),
        Flow = ClampUnit(Flow),
    };

    private static double ClampUnit(double v) => double.IsFinite(v) ? Math.Clamp(v, 0.0, 1.0) : 0.0;

    /// <summary>
    /// The authored keys, as a flat alternating key/value array in the template's own key order.
    ///
    /// <para>Only the authored keys, never the whole panel. The prefab, friendly name, sub-point and monitored
    /// cond are template constants the game materialises from <c>data/guipropmaps</c> on spawn, before
    /// <c>Ship.CreatePart</c> merges the item's own panel over it per key, so a partial panel is both safe and
    /// better than baking a copy of a game template into every exported ship.</para>
    /// </summary>
    public IReadOnlyList<object?> ToPanelKeys() =>
    [
        "knobBus", ((int)Bus).ToString(System.Globalization.CultureInfo.InvariantCulture),
        "knobPump", ((int)Purge).ToString(System.Globalization.CultureInfo.InvariantCulture),
        "knobRatio", TorchThrust ? "1" : "0",
        "chkAlign", Bool(LaserAlign),
        "chkCoilFwd", Bool(CoilForward),
        "chkCoilRear", Bool(CoilRear),
        "chkCryo", Bool(Cryo),
        "chkFuelReg", Bool(FuelRegulator),
        "chkIgnition", Bool(Ignition),
        "chkMHDOn", Bool(Mhd),
        "chkPellet", Bool(PelletFeed),
        "slidCycle", Num(Cycle),
        "slidFlow", Num(Flow),
    ];

    /// <summary>The same key/value pairs as <see cref="ToPanelKeys"/>, in tuple form — what the save write-back
    /// takes. One source for both, so an export and a save edit cannot drift apart.</summary>
    public IReadOnlyList<(string Key, string Value)> ToPanelPairs()
    {
        var flat = ToPanelKeys();
        var pairs = new List<(string, string)>(flat.Count / 2);
        for (var i = 0; i + 1 < flat.Count; i += 2)
            pairs.Add(((string)flat[i]!, (string)flat[i + 1]!));
        return pairs;
    }

    /// <summary>The game writes a panel bool as C# <c>bool.ToString()</c> output, so capitalised. Read back
    /// case-insensitively all the same (see <see cref="FromPanel"/>), since the template's own defaults are
    /// lowercase.</summary>
    private static string Bool(bool value) => value ? "True" : "False";

    /// <summary>A slider value as the panel carries it. <c>FusionIC</c> reads these through
    /// <c>Convert.ToDouble</c>, which is culture-sensitive on the game's side too, so the invariant form is the
    /// only one that round-trips on a machine with a comma decimal separator.</summary>
    private static string Num(double value) =>
        value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The keys that make a resolved panel a reactor panel. Read rather than assumed: a stock station
    /// authors this panel on <c>ItmReactorIC02Ignition</c>, whose condowner does not declare it at all.</summary>
    internal static readonly string[] Keys =
        ["knobBus", "knobPump", "knobRatio", "chkAlign", "chkCoilFwd", "chkCoilRear", "chkCryo",
         "chkFuelReg", "chkIgnition", "chkMHDOn", "chkPellet", "slidCycle", "slidFlow"];

    /// <summary>
    /// Read a reactor's settings off one resolved panel's keys, or null when the panel is not a reactor's. A
    /// missing key reads as the template's own default (off, false, zero), which is what the game would use.
    /// </summary>
    public static ReactorSettings? FromPanel(IReadOnlyDictionary<string, string?> keys)
    {
        if (!Keys.Any(keys.ContainsKey)) return null;
        return new ReactorSettings
        {
            Bus = (ReactorPowerBus)Knob(keys, "knobBus", 2),
            Purge = (ReactorCorePurge)Knob(keys, "knobPump", 2),
            // FusionIC.Update coerces any ratio that is not exactly 1 to 0, so anything else is torch off.
            TorchThrust = Knob(keys, "knobRatio", 1) == 1,
            LaserAlign = Flag(keys, "chkAlign"),
            CoilForward = Flag(keys, "chkCoilFwd"),
            CoilRear = Flag(keys, "chkCoilRear"),
            Cryo = Flag(keys, "chkCryo"),
            FuelRegulator = Flag(keys, "chkFuelReg"),
            Ignition = Flag(keys, "chkIgnition"),
            Mhd = Flag(keys, "chkMHDOn"),
            PelletFeed = Flag(keys, "chkPellet"),
            Cycle = Num(keys, "slidCycle"),
            Flow = Num(keys, "slidFlow"),
        }.Clamped();

        static int Knob(IReadOnlyDictionary<string, string?> keys, string key, int max) =>
            keys.GetValueOrDefault(key) is { Length: > 0 } raw
            && int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var v)
            && v >= 0 && v <= max ? v : 0;

        static bool Flag(IReadOnlyDictionary<string, string?> keys, string key) =>
            keys.GetValueOrDefault(key) is { Length: > 0 } raw && bool.TryParse(raw, out var v) && v;

        static double Num(IReadOnlyDictionary<string, string?> keys, string key) =>
            keys.GetValueOrDefault(key) is { Length: > 0 } raw
            && double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0.0;
    }
}
