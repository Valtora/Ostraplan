using System;
using System.Linq;
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

    /// <summary>
    /// Knowing a skin's real conds is not enough: they have to reach the save. A save-loaded CO can never run the
    /// skin's cond loot — <c>CondOwner.SetData</c> sets <c>bFreezeConds</c> the moment it has save data, and
    /// <c>DataHandler.GetCondOwner</c> attaches the <c>COOverlay</c> and calls <c>Init</c> only after that, where
    /// <c>ApplyCondLoot</c> reaches the CO through <c>ParseCondEquation</c> / <c>AddCondAmount</c> and both return
    /// immediately while frozen (<c>Ship.PostGameLoad</c> clears the flag far too late). So <c>aConds</c> =
    /// <c>["DEFAULT"]</c> resolves through <c>GetCondOwnerDef</c> to the skin's BASE condowner and the part arrives
    /// wearing the shared base's flat stats: an MSS wall at ItmWall1x1's 21 credits and 24 kg with no brand conds.
    /// Reported from a player's save in 1.1.0. The conds must be written out in full.
    /// </summary>
    [SkippableFact]
    public void A_skins_conds_are_written_into_a_save_in_full_never_as_the_default_marker()
    {
        var g = TestData.RequireGame();
        var mss = g.Catalog.Lookup("ItmWallMSSLFWhite");
        Skip.If(mss is null, "ItmWallMSSLFWhite not present.");
        Assert.Equal("CNDOLWallMSSLFWhite", mss!.SkinCondLoot);

        var co = SaveEdit.SynthesizeCo("ItmWallMSSLFWhite", "ID-1", g.Catalog, "REG", 0);
        var conds = ((System.Text.Json.Nodes.JsonArray)co["aConds"]!).Select(n => (string?)n).ToList();
        Assert.DoesNotContain("DEFAULT", conds);
        Assert.Contains("IsMSS=1.0x1", conds);
        Assert.Contains("IsWhite=1.0x1", conds);
        Assert.Contains("StatBasePrice=1.0x86", conds);       // not the base's 21
        Assert.Contains("StatMass=1.0x20", conds);            // not the base's 24
        Assert.Contains("IsWall1x1=1.0x1", conds);            // the base's own conds still come through
    }

    /// <summary>The same for the skins whose loot swaps a CONDITION the skin's own interaction tests for. A
    /// software textbook is <c>ItmBookStudyEngElectronic01</c> plus a loot that trades
    /// <c>IsStudyMaterialEngElectronic</c> for <c>IsStudyMaterialEngSoftware</c>. <c>COOverlay.Init</c> swaps the
    /// interaction from a plain list, so that half survives the freeze and the book offers
    /// <c>ACTStudySkillEngSoftware</c> — whose <c>CTTestThem</c> is <c>TIsStudyMaterialEngSoftware</c>, a
    /// condition the frozen loot never added. The book reaches the player with no way to read it at all.</summary>
    [SkippableFact]
    public void A_skin_that_swaps_a_condition_its_own_interaction_tests_for_keeps_that_condition()
    {
        var g = TestData.RequireGame();
        var book = g.Catalog.Lookup("ItmBookStudyEngSoftware01");
        Skip.If(book is null, "ItmBookStudyEngSoftware01 not present.");
        Assert.Contains("ACTStudySkillEngSoftware", book!.InteractionNames);

        var co = SaveEdit.SynthesizeCo("ItmBookStudyEngSoftware01", "ID-1", g.Catalog, "REG", 0);
        var conds = ((System.Text.Json.Nodes.JsonArray)co["aConds"]!).Select(n => (string?)n).ToList();
        Assert.Contains("IsStudyMaterialEngSoftware=1.0x1", conds);
        Assert.DoesNotContain(conds, c => c!.StartsWith("IsStudyMaterialEngElectronic", StringComparison.Ordinal));
    }

    /// <summary>A plain condowner still gets the marker. It is what the game itself writes for a part whose conds
    /// match its def, it keeps a record small, and it is the only shape the no-op write-back guarantee was ever
    /// asserted against.</summary>
    [SkippableFact]
    public void A_plain_def_still_gets_the_default_marker()
    {
        var g = TestData.RequireGame();
        var wall = g.Catalog.Lookup("ItmWall1x1");
        Skip.If(wall is null, "ItmWall1x1 not present.");
        Assert.Null(wall!.SkinCondLoot);
        Assert.Equal(["DEFAULT"], wall.SavedConds);
    }

    /// <summary>
    /// The same freeze eats <c>CondOwner.SetUpBehaviours</c>, which is the last line of <c>SetData</c> and is where
    /// every part in the game gets <c>IsDamageable</c>, <c>IsDestructable</c> and its progress ceilings — no def
    /// declares the first two at all. A game-built part carries them because the backfill ran once, on a CO that
    /// was not yet frozen, and the save has recorded them ever since. A written part has to say them itself or it
    /// cannot be damaged by a hit, is not picked up as an explosion target, and finishes a repair on the first
    /// tick because an absent ceiling reads as zero.
    /// </summary>
    [SkippableFact]
    public void A_written_part_carries_the_conds_the_game_backfills_on_spawn()
    {
        var g = TestData.RequireGame();
        var wall = g.Catalog.Lookup("ItmWallMSSLFWhite");
        Skip.If(wall is null, "ItmWallMSSLFWhite not present.");
        Assert.DoesNotContain("IsDamageable", wall!.StartingConds);        // the def really does not declare it
        Assert.DoesNotContain("StatRepairProgressMax", wall.StartingConds);

        var conds = ((System.Text.Json.Nodes.JsonArray)
            SaveEdit.SynthesizeCo("ItmWallMSSLFWhite", "ID-1", g.Catalog, "REG", 0)["aConds"]!)
            .Select(n => (string?)n).ToList();
        Assert.Contains("IsDamageable=1.0x1", conds);
        Assert.Contains("IsDestructable=1.0x1", conds);
        Assert.Contains("StatRepairProgressMax=1.0x1000", conds);
        // the def declares its own install/uninstall ceilings, so the backfill leaves those alone
        Assert.Single(conds, c => c!.StartsWith("StatInstallProgressMax=", StringComparison.Ordinal));
        Assert.Contains("StatInstallProgressMax=1.0x450", conds);          // the skin's, not the backfill's 1000
    }

    /// <summary>The game's two early returns are part of the rule, not an optimisation. A part with no damage pool
    /// gets the ceilings and nothing else; one that is neither installed nor solid has no physical presence to hit
    /// and stops before <c>IsDamageable</c>.</summary>
    [Fact]
    public void The_backfill_stops_where_the_game_stops()
    {
        var cat = new Fixtures()
            .Part("NoPool", tileConds: [], startingConds: ["IsInstalled"])
            .Part("Ghost", tileConds: [], startingConds: ["StatDamageMax"],
                  condValues: new System.Collections.Generic.Dictionary<string, double> { ["StatDamageMax"] = 5 })
            .Part("System", tileConds: [], startingConds: ["StatDamageMax", "IsSystem", "IsInstalled"],
                  condValues: new System.Collections.Generic.Dictionary<string, double> { ["StatDamageMax"] = 5 })
            .Build();

        // no damage pool: ceilings only
        var noPool = cat.Lookup("NoPool")!.BehaviourConds;
        Assert.DoesNotContain("IsDamageable=1.0x1", noPool);
        Assert.DoesNotContain("IsDestructable=1.0x1", noPool);
        Assert.Contains("StatRepairProgressMax=1.0x1000", noPool);

        // a pool but no presence: destructable, not damageable
        var ghost = cat.Lookup("Ghost")!.BehaviourConds;
        Assert.Contains("IsDestructable=1.0x1", ghost);
        Assert.DoesNotContain("IsDamageable=1.0x1", ghost);

        // IsSystem stops at the ceilings — loot spawners and fire are effects, not parts
        var sys = cat.Lookup("System")!.BehaviourConds;
        Assert.DoesNotContain("IsDestructable=1.0x1", sys);
        Assert.DoesNotContain("IsDamageable=1.0x1", sys);
    }
}
