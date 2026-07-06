namespace Ostraplan.Core;

/// <summary>
/// The standard set of navigation modules Ostraplan installs into every nav console it exports or injects.
/// A built/placed console (<c>ItmStationNav</c>) is only an empty frame — the game's flight interface is
/// assembled from separate hot-swappable module items (the <c>ItmNavMod*</c> cooverlays) contained inside it,
/// so a console with no modules spawns blank. Ostraplan models just the console as a placed part and drops
/// contained sub-objects on import, so it must add the modules itself. Modules attach exactly as a real ship
/// template carries them (see <c>Babak.json</c>): a separate item whose <c>strParentID</c> is the console's
/// <c>strID</c>, sharing the console's coordinates. The console's own screen-layout GUI-prop-map
/// (<c>NavModConfig</c>) comes from its def, so only the physical modules are needed.
/// </summary>
public static class NavConsole
{
    /// <summary>
    /// The "fully drivable" module loadout, in a stable order: the game's basic drop-pod set
    /// (<c>ItmNavStationModsRandomPod</c>) plus the flight/nav modules a real ship (<c>Babak</c>) carries, minus
    /// the combat/weapons modules. All names verified against <c>data/cooverlays/cooverlays_navmods.json</c>. On a
    /// template load the game defaults each module's CO + GUI-prop-map from its def; the save-edit path bakes them.
    /// </summary>
    public static readonly IReadOnlyList<string> StandardModules =
    [
        // basic "pod" loadout (data/loot ItmNavStationModsRandomPod)
        "ItmNavModControls", "ItmNavModMap", "ItmNavModEngineMode", "ItmNavModDiagnostics",
        "ItmNavModWarnings", "ItmNavModTransponder", "ItmNavModTimeZoom", "ItmNavModControlToggle",
        // + the flight/nav modules that make the console actually drivable
        "ItmNavModFlightDynamics", "ItmNavModCoursePlot", "ItmNavModTargetData",
        "ItmNavModDisplayControls", "ItmNavModSensorsMFD", "ItmNavModReserves",
    ];

    /// <summary>The starting condition that marks a part as a navigation console — data-driven detection, so a
    /// modded or variant console is recognised too rather than hard-coding the base-game <c>ItmStationNav</c> name.</summary>
    private const string NavStationCond = "IsNavStation";

    /// <summary>A placed part is a nav console (and so gets <see cref="StandardModules"/> installed) when its def
    /// carries the <see cref="NavStationCond"/> starting condition.</summary>
    public static bool IsConsole(PartDef? def) =>
        def is not null && System.Array.IndexOf(def.StartingConds, NavStationCond) >= 0;

    /// <inheritdoc cref="IsConsole(PartDef?)"/>
    public static bool IsConsole(ResolvedPart? part) => part is not null && part.Has(NavStationCond);
}
