using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// A cooverlay skin's stats are its base condowner's plus the signed deltas in the skin's <c>strCondLoot</c>
/// (the game's <c>COOverlay.Init</c> → <c>Loot.ApplyCondLoot</c>, run on every spawn). Every branded metal wall
/// skins <c>ItmWall1x1</c> (24 kg), but their loots shift the mass per brand — so Ostraplan must show the
/// per-brand figure a player would actually build, not the flat base. Reproduces the in-game values (matching
/// the wiki and captured-ship readings). Runs only against the live install.
/// </summary>
public class CondLootOverlayTests
{
    [SkippableTheory]
    [InlineData("ItmWallMSSLFWhite", 20.0)]   // Mobile Space Systems "Light Framework": 24 - 4
    [InlineData("ItmWallTSDO44", 25.0)]        // Testudo "44 Series": 24 + 1
    [InlineData("ItmWallTSDO01", 25.0)]        // Testudo: 24 + 1
    [InlineData("ItmWallRYOB01", 28.0)]        // Ryokka "B-01": 24 + 4
    [InlineData("ItmWallLDPH01", 48.0)]        // Langdon-Phillips "Glory": 24 + 24 (heaviest)
    [InlineData("ItmWallVHRB", 27.0)]          // Van Hummel "Banner": 24 + 3
    public void Branded_wall_mass_is_base_plus_cooverlay_loot_delta(string skinDef, double expectedMass)
    {
        var g = TestData.RequireGame();
        var part = g.Catalog.Lookup(skinDef);
        Skip.If(part is null, $"'{skinDef}' not present in this install's data.");
        Assert.Equal(expectedMass, part!.StartingCondValues.GetValueOrDefault("StatMass"), 3);
    }

    [SkippableFact]
    public void Cooverlay_loot_applies_price_install_and_brand_flags_not_just_mass()
    {
        var g = TestData.RequireGame();
        var mss = g.Catalog.Lookup("ItmWallMSSLFWhite");
        Skip.If(mss is null, "ItmWallMSSLFWhite not present.");
        // CNDOLWallMSSLFWhite: StatBasePrice +65 (21 -> 86), -StatInstallProgressMax x150 (600 -> 450),
        // adds IsMSS/IsWhite, -IsHiddenInv (2 -> 1). This is exactly a real save's baked MSS wall.
        Assert.Equal(86.0, mss!.StartingCondValues.GetValueOrDefault("StatBasePrice"), 3);
        Assert.Equal(450.0, mss.StartingCondValues.GetValueOrDefault("StatInstallProgressMax"), 3);
        Assert.Contains("IsMSS", mss.StartingConds);
        Assert.Contains("IsWhite", mss.StartingConds);
    }

    [SkippableFact]
    public void Unskinned_base_wall_keeps_its_own_stats()
    {
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.TryGetValue("ItmWall1x1", out var wall)) return;
        Assert.Equal(24.0, wall.StartingCondValues.GetValueOrDefault("StatMass"), 3);   // base is untouched
    }
}
