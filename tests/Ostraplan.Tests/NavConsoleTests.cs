using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The nav-console module loadout Ostraplan injects into an otherwise-empty console (a built console is a bare
/// frame — its interface is separate module items). A wrong or short list spawns a blank/undrivable console, so
/// the set is pinned here. Data-driven detection (the <c>IsNavStation</c> cond) is exercised too. No install.
/// </summary>
public class NavConsoleTests
{
    [Fact]
    public void IsConsole_detects_the_nav_station_cond_and_ignores_others()
    {
        var console = new Fixtures().Part("Nav", startingConds: ["IsNavStation"]).Get("Nav");
        var wall = new Fixtures().Wall("W").Get("W");

        Assert.True(NavConsole.IsConsole(console));
        Assert.False(NavConsole.IsConsole(wall));
        Assert.False(NavConsole.IsConsole((PartDef?)null));
    }

    [Fact]
    public void Standard_modules_are_a_stable_distinct_set_of_nav_modules()
    {
        var mods = NavConsole.StandardModules;
        Assert.Equal(14, mods.Count);
        Assert.Equal(mods.Count, mods.Distinct().Count());          // no accidental duplicate
        Assert.All(mods, m => Assert.StartsWith("ItmNavMod", m));   // every entry is a nav module def
    }

    [Fact]
    public void Standard_modules_are_drivable_but_carry_no_weapons()
    {
        var mods = NavConsole.StandardModules;
        // the modules that make a console actually flyable
        Assert.Contains("ItmNavModControls", mods);
        Assert.Contains("ItmNavModFlightDynamics", mods);
        Assert.Contains("ItmNavModCoursePlot", mods);
        Assert.Contains("ItmNavModSensorsMFD", mods);
        // deliberately no combat/weapon modules in the exported set
        Assert.DoesNotContain(mods, m =>
            m.Contains("Weapon") || m.Contains("Combat") || m.Contains("Turret") || m.Contains("Gun"));
    }
}
