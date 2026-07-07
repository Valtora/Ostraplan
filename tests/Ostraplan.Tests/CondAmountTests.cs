using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>The starting-cond magnitude parse. The game's cond string is "Name=chancexAMOUNT" —
/// the amount is AFTER the 'x' (GetCondAmount returns fCount = the after-x number), the before-x
/// value is the apply chance. The parse must match, or every magnitude (mass, power) reads as the
/// chance (1.0).</summary>
public class CondAmountTests
{
    [Theory]
    [InlineData("StatMass=1.0x45.0", 45.0)]      // real form: chance 1.0, amount 45
    [InlineData("StatPower=1.0x500.0", 500.0)]   // reactor output
    [InlineData("StatPowerMax=1.0x80.96", 80.96)]
    [InlineData("IsWall=1.0x1", 1.0)]
    [InlineData("StatFoo=1.0x4-6", 4.0)]          // range -> low end
    [InlineData("-StatBar=1.0x5", -5.0)]          // leading '-' negates
    [InlineData("IsPresent", 1.0)]                // bare name = present
    public void Amount_is_the_number_after_x(string entry, double expected)
        => Assert.Equal(expected, LootDef.CondAmount(entry), 3);

    [SkippableFact]
    public void Starting_cond_values_hold_real_magnitudes_not_the_chance()
    {
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.TryGetValue("ItmWall1x1", out var wall)) return;
        // ItmWall1x1 carries "StatMass=1.0x24.0" — the parsed magnitude must be 24, not the 1.0 chance.
        Assert.Equal(24.0, wall.StartingCondValues.GetValueOrDefault("StatMass"), 3);
    }
}
