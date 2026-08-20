using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The damage chain: what a part breaks into, and how much punishment it takes on the way.
///
/// <para>Both damage solvers (§26) price a hit against one of two ceilings, and getting the pair the wrong way
/// round is the failure mode that matters. A part's own <see cref="Catalog.Health"/> is what breaks it into its
/// next form; the chained <see cref="Catalog.MaxHealth"/> is what destroys it outright. A wall is 15 to damage and
/// 45 to destroy, so a solver reading only the first understates the ship badly and one reading only the second
/// never shows a damaged wall at all.</para>
///
/// <para>Almost all of this is game-gated: <see cref="Catalog.BreakForms"/> is read out of the condowners'
/// <c>aUpdateCommands</c> and walked through real loots and interactions, and there is nothing to check against a
/// synthetic catalog beyond the walk terminating. The exact figures below were read off the 1.0.0.11 decompile by
/// hand (<c>DataCO.GetMaxHealth</c>) and are what pin the port.</para>
/// </summary>
public class DamageChainTests
{
    // ---- the declaration ----

    [SkippableFact]
    public void A_destructable_part_declares_its_break_on_its_own_condowner()
    {
        var g = TestData.RequireGame();

        // aUpdateCommands: "Destructable,StatDamage,ACTWall1x1Dmg,StatDamageMax,1.0"
        Assert.Equal("ItmWall1x1Dmg", g.Catalog.BreakForm("ItmWall1x1"));
        Assert.Equal("ItmFloorGrate01Dmg", g.Catalog.BreakForm("ItmFloorGrate01"));
        Assert.True(g.Catalog.IsDestructable("ItmWall1x1"));
    }

    [SkippableFact]
    public void Breaking_is_not_the_inverse_of_repairing()
    {
        var g = TestData.RequireGame();

        // The two tables come from different data and must not be derived from each other. Repair is built from
        // data/installables jobs; damage is declared on the condowner. They agree on a plain wall and part company
        // as soon as a part's states carry lock or power with them (§12).
        Assert.Equal("ItmWall1x1Dmg", g.Catalog.BreakForm("ItmWall1x1"));
        Assert.Equal("ItmWall1x1", g.Catalog.RepairForm("ItmWall1x1Dmg"));

        // A door normalises on repair but not on break: the undamage job that would map ItmDoor01ClosedOnLocked
        // back to an unlocked door is deliberately not a repair, and nothing breaks INTO a locked door either.
        Assert.Null(g.Catalog.RepairForm("ItmDoor01ClosedOnLocked"));
    }

    // ---- the two ceilings ----

    [SkippableTheory]
    // def, its own pool (breaks here), the whole chain (destroyed here)
    [InlineData("ItmWall1x1", 15, 45)]
    [InlineData("ItmFloorGrate01", 15, 45)]
    [InlineData("ItmDoor01Closed", 20, 80)]
    [InlineData("ItmBattery02", 10, 40)]
    [InlineData("ItmCapacitor01", 5, 13)]
    [InlineData("ItmReactorIC02Off", 120, 123)]
    public void The_chain_totals_what_the_game_totals(string def, double health, double maxHealth)
    {
        var g = TestData.RequireGame();

        Assert.Equal(health, g.Catalog.Health(def), 3);
        Assert.Equal(maxHealth, g.Catalog.MaxHealth(def), 3);
    }

    [SkippableFact]
    public void A_break_that_names_more_than_one_thing_ends_the_chain()
    {
        var g = TestData.RequireGame();

        // ItmReactorIC03Ignition breaks through ACTReactorIC03DamageExplode, whose mode-switch loot names TWO
        // condowners — the wreck and SysExplosionFusion. The game requires exactly one and abandons the walk when
        // it finds otherwise, so an ignited reactor is worth its own 25 and no more. Reading the wreck's pool in
        // anyway would make the reactor look twice as tough as it is, which is the opposite of the answer the
        // person asking about reactor components near the hull needs.
        Assert.Null(g.Catalog.BreakForm("ItmReactorIC03Ignition"));
        Assert.Equal(25, g.Catalog.Health("ItmReactorIC03Ignition"), 3);
        Assert.Equal(25, g.Catalog.MaxHealth("ItmReactorIC03Ignition"), 3);
    }

    [SkippableFact]
    public void A_themed_wall_breaks_into_the_same_theme()
    {
        var g = TestData.RequireGame();

        // Same shape as the repair skin case: the damaged themed wall is only ever the right-hand side of one of
        // ItmWallAERO01's mapModeSwitches pairs, so it has to be recovered by breaking the base and re-skinning
        // forward. Breaking a Testudo wall into a generic one would re-skin the ship behind the user's back.
        var broken = g.Catalog.BreakForm("ItmWallAERO01");
        Assert.Equal("ItmWallAERO01Dmg", broken);
        Assert.Equal(g.Catalog.MaxHealth("ItmWall1x1"), g.Catalog.MaxHealth("ItmWallAERO01"), 3);
    }

    [SkippableFact]
    public void A_loose_part_is_worth_its_own_pool_and_no_chain()
    {
        var g = TestData.RequireGame();

        // GetMaxHealth returns Health outright for anything not IsInstalled, however much its break chain would
        // otherwise add. A crate in the hold does not soak a missile the way a bulkhead does.
        Assert.Equal(g.Catalog.Health("ItmWall1x1Loose"), g.Catalog.MaxHealth("ItmWall1x1Loose"), 3);
    }

    // ---- corpus-wide invariants ----

    [SkippableFact]
    public void Every_break_target_resolves_and_no_chain_runs_away()
    {
        var g = TestData.RequireGame();
        var cat = g.Catalog;

        Assert.NotEmpty(cat.BreakForms);
        foreach (var (from, to) in cat.BreakForms)
        {
            Assert.NotEqual(from, to);
            // The build only keeps a target it could resolve, so anything in the table must look up.
            Assert.NotNull(cat.Lookup(to));
            // A chain can only ever add, and it must terminate: MaxHealth returning at all is the cycle guard
            // doing its job, since the game's own walk would spin here.
            Assert.True(cat.MaxHealth(from) >= cat.Health(from));
        }
    }

    [SkippableFact]
    public void Most_installed_damageable_parts_chain_past_their_first_break()
    {
        var g = TestData.RequireGame();
        var cat = g.Catalog;

        var installed = cat.Parts
            .Where(p => p.StartingConds.Contains("IsInstalled") && cat.IsDestructable(p.DefName))
            .ToList();
        var chained = installed.Count(p => cat.MaxHealth(p.DefName) > cat.Health(p.DefName) + 0.001);

        // Measured on stock 1.0.0.11. These are a drift alarm rather than a law: if a patch changes how many parts
        // carry a break chain the solvers' figures move with it, and that is worth a failing test rather than a
        // quietly different heat map.
        Assert.True(installed.Count > 300, $"only {installed.Count} installed damageable parts");
        Assert.True(chained > installed.Count / 2, $"only {chained} of {installed.Count} chain past the first break");
    }

    // ---- the game-free half ----

    [Fact]
    public void A_catalog_with_no_damage_data_answers_zero_rather_than_throwing()
    {
        // Synthetic catalogs carry no aUpdateCommands, so every part is indestructible. The solvers have to cope:
        // a part with no pool absorbs nothing and a strike passes through it.
        var cat = new Fixtures().Part("Wall", startingConds: ["IsInstalled", "IsWall"]).Build();

        Assert.Empty(cat.BreakForms);
        Assert.Null(cat.BreakForm("Wall"));
        Assert.False(cat.IsDestructable("Wall"));
        Assert.Equal(0, cat.Health("Wall"), 3);
        Assert.Equal(0, cat.MaxHealth("Wall"), 3);
    }
}
