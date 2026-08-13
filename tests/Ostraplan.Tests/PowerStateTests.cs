using Ostraplan.Core;
using Xunit;
using Xunit.Abstractions;

namespace Ostraplan.Tests;

/// <summary>
/// The switched-on / switched-off state map behind the "Switch on" and "Switch off" actions. Needs the real defs,
/// so these no-op without the install.
/// </summary>
public class PowerStateTests(ITestOutputHelper output)
{
    [SkippableFact]
    public void Toggles_a_device_whose_on_state_is_a_colour_variant()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(g.Catalog.Lookup("ItmTransponder01Off") is not null, "no transponder in this install");

        // The reason this feature exists. The transponder's on-state is OnR, which the palette's own
        // PreferPoweredState cannot name, so it is placed Off and was previously unreachable.
        Assert.Equal("ItmTransponder01OnR", g.Catalog.PowerToggle("ItmTransponder01Off"));
        Assert.Equal("ItmTransponder01Off", g.Catalog.PowerToggle("ItmTransponder01OnR"));
    }

    [SkippableFact]
    public void Toggles_a_plain_on_off_device_both_ways()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(g.Catalog.Lookup("ItmHeavyLiftRotor01Off") is not null, "no lift rotor in this install");

        Assert.Equal("ItmHeavyLiftRotor01On", g.Catalog.PowerToggle("ItmHeavyLiftRotor01Off"));
        Assert.Equal("ItmHeavyLiftRotor01Off", g.Catalog.PowerToggle("ItmHeavyLiftRotor01On"));
    }

    [SkippableFact]
    public void An_alarm_offers_only_its_nominal_state_never_an_alert()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(g.Catalog.Lookup("ItmAlarmO2Off") is not null, "no O2 alarm in this install");

        // Green, not Red or Yellow: a design must not be authorable mid-alarm.
        Assert.Equal("ItmAlarmO2OnG", g.Catalog.PowerToggle("ItmAlarmO2Off"));
        Assert.Equal("ItmAlarmCO2OnG", g.Catalog.PowerToggle("ItmAlarmCO2Off"));

        // and the thermostat's nominal state is White, which no colour-name rule would have guessed
        Assert.Equal("ItmAlarmTempOnW", g.Catalog.PowerToggle("ItmAlarmTempOff"));
    }

    [SkippableFact]
    public void An_alert_state_still_switches_off()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(g.Catalog.Lookup("ItmAlarmO2OnR") is not null, "no O2 alarm in this install");

        // Reached by importing a ship whose alarm was sounding. It must be switchable off even though it is not
        // a state the user can switch back to.
        Assert.Equal("ItmAlarmO2Off", g.Catalog.PowerToggle("ItmAlarmO2OnR"));
    }

    [SkippableFact]
    public void Structure_and_loose_items_have_no_power_state()
    {
        var g = TestData.RequireGame();

        Assert.Null(g.Catalog.PowerToggle("ItmWall1x1"));
        Assert.Null(g.Catalog.PowerToggle("ItmFloorGrate01"));
        Assert.Null(g.Catalog.PowerToggle("ItmTransponder01Loose"));   // carries IsOff but is not installed
        Assert.Null(g.Catalog.PowerToggle("NotADefAtAll"));
    }

    [SkippableFact]
    public void Every_mapping_round_trips_and_lands_on_a_real_part()
    {
        var g = TestData.RequireGame();

        // Sweep the condowner data rather than the palette: the palette carries the On form of anything
        // PreferPoweredState could name, so the Off states this feature exists for are not in it.
        int toggled = 0, offToOn = 0;
        foreach (var name in g.Index.Type("condowners").Keys)
        {
            if (g.Catalog.Lookup(name) is not { } def) continue;
            if (g.Catalog.PowerToggle(name) is not { } peer) continue;
            toggled++;

            var peerDef = g.Catalog.Lookup(peer);
            Assert.NotNull(peerDef);                                       // never points at a def that isn't there
            Assert.NotEqual(name, peer);
            Assert.Equal(def.Item.Width, peerDef!.Item.Width);             // same tiles either way
            Assert.Equal(def.Item.Height, peerDef.Item.Height);
            Assert.NotEqual(def.StartingConds.Contains("IsOff"), peerDef.StartingConds.Contains("IsOff"));

            // Switching on never lands in an alert state, and off -> on -> off returns where it started. A sole
            // on-state may still carry a parenthetical ("XPDR Antenna (Active)", "Power Switch (On)"); what must
            // never be reachable is a state that means something is wrong.
            if (def.StartingConds.Contains("IsOff"))
            {
                offToOn++;
                Assert.DoesNotContain("(Alert)", peerDef.Friendly);
                Assert.DoesNotContain("(Warning)", peerDef.Friendly);
                Assert.DoesNotContain("(Too ", peerDef.Friendly);
                Assert.Equal(name, g.Catalog.PowerToggle(peer));
            }
        }

        output.WriteLine($"{toggled} defs carry a power-state counterpart ({offToOn} switchable on)");
        Assert.True(offToOn > 40, $"expected the stock device set to switch on, got {offToOn}");
    }
}
