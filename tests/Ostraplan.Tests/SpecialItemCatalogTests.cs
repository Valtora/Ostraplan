using System.Linq;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>The non-buildable installed structure behind the palette's SPECIAL tab. Install-gated.</summary>
public class SpecialItemCatalogTests
{
    [SkippableFact]
    public void SpecialItems_covers_asteroids_signs_and_station_fixtures()
    {
        // The three families issue #18 asked for. None of them has an install job, so before the SPECIAL tab the
        // only way to get one into a design was to copy it out of a ship template.
        var g = TestData.RequireGame();
        var names = g.Catalog.SpecialItems.Select(p => p.DefName).ToHashSet();

        Assert.Contains("ItmFloorRock02", names);            // asteroid: stony rock core
        Assert.Contains("ItmFloorIce02", names);             // asteroid: ice core
        Assert.Contains("ItmWallRock011x1", names);          // asteroid: regolith wall
        Assert.Contains("ItmFloorLabelAirlock01", names);    // sign
        Assert.Contains("ItmFloorLabelEagle", names);        // emblem
        Assert.Contains("ItmKiosk01", names);                // station utility: refuel kiosk
        Assert.Contains("ItmKioskTransit03", names);         // station utility: transit lift
    }

    [SkippableFact]
    public void SpecialItems_is_disjoint_from_the_buildable_palette()
    {
        // The tab exists to add what the build tabs can't reach. Anything already buildable — or registered by
        // hand into ByDefName, like the primary airlock and each door's closed counterpart — must not double up.
        var g = TestData.RequireGame();

        Assert.NotEmpty(g.Catalog.SpecialItems);
        Assert.DoesNotContain(g.Catalog.SpecialItems, p => g.Catalog.ByDefName.ContainsKey(p.DefName));
        Assert.DoesNotContain(g.Catalog.SpecialItems, p => p.DefName == Catalog.PrimaryDocksysDef);
    }

    [SkippableFact]
    public void SpecialItems_excludes_loose_cargo_runtime_states_and_unnamed_defs()
    {
        var g = TestData.RequireGame();
        var names = g.Catalog.SpecialItems.Select(p => p.DefName).ToHashSet();

        // loose cargo belongs to the ITEMS tab; the two universes split on IsInstalled
        Assert.All(g.Catalog.SpecialItems, p => Assert.Contains("IsInstalled", p.StartingConds));
        Assert.Empty(names.Intersect(g.Catalog.LooseItems.Select(p => p.DefName)));

        // a damaged/patched/off/locked def is a runtime state of a part the palette already offers, not a part
        Assert.DoesNotContain("ItmWall1x1Dmg", names);
        Assert.DoesNotContain("ItmFloorGrate01Patch", names);
        Assert.DoesNotContain("ItmDoor01ClosedLocked", names);

        // a def the data never named is a dev/test artefact — Friendly would show its internal name
        Assert.DoesNotContain("ItmNormalsTest", names);
        Assert.DoesNotContain("ItmStrut1x1", names);
        Assert.All(g.Catalog.SpecialItems, p => Assert.NotEqual(p.DefName, p.Friendly));
    }

    [SkippableFact]
    public void SpecialItems_place_and_analyse_like_any_other_part()
    {
        // A SPECIAL entry is an ordinary installed placement: it resolves through Lookup, carries real geometry,
        // and goes through the placement law. Nothing about the tab is a special case downstream.
        var g = TestData.RequireGame();
        var rock = g.Catalog.SpecialItems.FirstOrDefault(p => p.DefName == "ItmFloorRock021x1");
        Skip.If(rock is null, "ItmFloorRock021x1 not in this install.");

        Assert.Same(g.Catalog.Lookup("ItmFloorRock021x1"), g.Catalog.Lookup("ItmFloorRock021x1"));   // cached
        Assert.NotNull(rock!.SpriteAbs);
        Assert.True(rock.Item.Width > 0 && rock.Item.Height > 0);

        var doc = new ShipDocument(g.Catalog);
        Assert.True(CheckFit.Check(doc, rock, 0, 0, 0, includeEnvelope: false).Ok);
    }
}
