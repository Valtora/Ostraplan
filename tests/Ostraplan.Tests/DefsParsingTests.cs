using System.Text.Json;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The typed views over the raw game JSON (<c>Defs</c>) — the foundation every loaded ship and part is built on,
/// so a parsing regression cascades everywhere. Exercised directly against synthetic JSON, no install. Includes
/// the decompile-verified <see cref="LootDef.CondAmount"/> "magnitude is after the x" rule (the bug that once made
/// the maneuver rating sum part counts instead of kilograms).
/// </summary>
public class DefsParsingTests
{
    private static JsonElement El(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void ItemDef_derives_footprint_from_nCols_and_socket_adds()
    {
        var item = ItemDef.Parse(El("""{ "strName":"X", "nCols":2, "aSocketAdds":["a","a","a","a","a","a"] }"""));
        Assert.Equal("X", item.Name);
        Assert.Equal(2, item.Width);    // nCols
        Assert.Equal(3, item.Height);   // adds.Length / width = 6 / 2
    }

    [Fact]
    public void ItemDef_defaults_to_one_by_one_when_dimensions_are_absent()
    {
        var item = ItemDef.Parse(El("""{ "strName":"X" }"""));
        Assert.Equal(1, item.Width);
        Assert.Equal(1, item.Height);
    }

    [Theory]
    [InlineData("StatMass=1.0x45", 45)]     // magnitude is AFTER the x; the number before it is the apply chance
    [InlineData("IsSystem=1.0x1", 1)]
    [InlineData("Cond=2.0x3", 3)]           // chance 2.0 is not the amount
    [InlineData("IsWall", 1)]               // a bare name reads as present = 1
    [InlineData("StatX=1.0x4-6", 4)]        // a range takes the low end (deterministic)
    [InlineData("-StatY=1.0x5", -5)]        // a leading minus negates
    public void CondAmount_reads_the_magnitude_after_the_x(string entry, double expected) =>
        Assert.Equal(expected, LootDef.CondAmount(entry));

    [Theory]
    [InlineData("IsWall=1.0x1", "IsWall")]
    [InlineData("StatMass=1.0x45", "StatMass")]
    [InlineData("IsBare", "IsBare")]
    public void CondName_strips_the_equation(string entry, string expected) =>
        Assert.Equal(expected, LootDef.CondName(entry));

    [Fact]
    public void LootDef_parses_cond_names_and_nested_loots()
    {
        var loot = LootDef.Parse(El("""{ "strName":"L", "aCOs":["IsWall=1.0x1","IsObstruction=1.0x1"], "aLoots":["Nested"] }"""));
        Assert.Equal("L", loot.Name);
        Assert.Equal(new[] { "IsWall", "IsObstruction" }, loot.Conds);
        Assert.Equal(new[] { "Nested" }, loot.Loots);
    }

    [Fact]
    public void CondOwnerDef_parses_container_grid_stack_limit_and_cond_amounts()
    {
        var co = CondOwnerDef.Parse(El("""
        {
          "strName":"Locker", "strItemDef":"ItmLocker",
          "aStartingConds":["IsContainer=1.0x1","StatMass=1.0x12","StatBasePrice=1.0x250"],
          "nContainerWidth":4, "nContainerHeight":3, "strContainerCT":"CTStorable", "nStackLimit":8
        }
        """));
        Assert.Equal("Locker", co.Name);
        Assert.Equal("ItmLocker", co.ItemDefName);
        Assert.Contains("IsContainer", co.StartingCondNames);
        Assert.Equal(12, co.StartingCondValues["StatMass"]);
        Assert.Equal(250, co.StartingCondValues["StatBasePrice"]);
        Assert.Equal(4, co.ContainerW);
        Assert.Equal(3, co.ContainerH);
        Assert.Equal("CTStorable", co.ContainerCT);
        Assert.Equal(8, co.StackLimit);
    }

    [Fact]
    public void CondTriggerDef_parses_reqs_forbids_and_the_and_flag()
    {
        var t = CondTriggerDef.Parse(El("""{ "strName":"TX", "aReqs":["IsA=1.0x1","IsB"], "aForbids":["IsC"], "bAND":false }"""));
        Assert.Equal("TX", t.Name);
        Assert.Equal(new[] { "IsA", "IsB" }, t.Reqs);
        Assert.Equal(new[] { "IsC" }, t.Forbids);
        Assert.False(t.BAnd);
    }

    [Fact]
    public void CondTriggerDef_defaults_the_and_flag_to_true()
    {
        var t = CondTriggerDef.Parse(El("""{ "strName":"TY", "aReqs":["IsA"] }"""));
        Assert.True(t.BAnd);
    }
}
