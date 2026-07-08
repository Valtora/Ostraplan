using System.Linq;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>The loose-cargo catalog + container filter for the inventory editor's add-picker. Install-gated.</summary>
public class LooseItemCatalogTests
{
    [SkippableFact]
    public void LooseItems_includes_cargo_and_excludes_installed_structure()
    {
        var g = TestData.RequireGame();

        Assert.NotEmpty(g.Catalog.LooseItems);
        var names = g.Catalog.LooseItems.Select(p => p.DefName).ToHashSet();
        Assert.Contains("ItmScrapAluminum", names);   // loose cargo
        Assert.DoesNotContain("ItmWall1x1", names);    // installed structure (carries IsInstalled)
    }

    [SkippableFact]
    public void LooseItems_includes_cooverlay_skins_themed_floors_walls_and_nav_modules()
    {
        // the add-picker universe must reach cooverlay skins, not just condowners: themed loose walls/floors and
        // the nav-console modules that exist ONLY as cooverlays. Enumerating condowners alone missed all of these.
        var g = TestData.RequireGame();
        var names = g.Catalog.LooseItems.Select(p => p.DefName).ToHashSet();

        Assert.Contains("ItmFloorAERO01Loose", names);     // a themed loose floor (cooverlay, strCOBase a loose base)
        Assert.Contains("ItmNavModControls", names);       // a nav-console module (cooverlay-only)
        // more than the single generic "Floor (Loose)" the condowner-only universe used to offer
        Assert.True(names.Count(n => n.StartsWith("ItmFloor") && n.EndsWith("Loose")) > 1);
    }

    [SkippableFact]
    public void Nav_console_offers_its_named_modules()
    {
        // "Nav Consoles plopped in Ostraplan have nothing inside them" — the console's contents are cooverlay
        // modules, which the condowner-only universe excluded, so the picker was empty. It should now offer them.
        var g = TestData.RequireGame();
        var console = g.Catalog.Lookup("ItmStationNav");
        if (console is null) return;
        Assert.Equal("TIsFitContainerNavMod", console.ContainerCT);

        var accepted = ContainerFilter.AcceptedBy(g.Catalog, console).Select(i => i.DefName).ToHashSet();
        Assert.Contains("ItmNavModControls", accepted);
        Assert.Contains("ItmNavModFlightDynamics", accepted);
    }

    [SkippableFact]
    public void Container_filter_accepts_loose_cargo_and_rejects_installed_structure()
    {
        var g = TestData.RequireGame();

        var backpack = g.Catalog.Lookup("ItmBackpack01");
        var scrap = g.Catalog.Lookup("ItmScrapAluminum");
        var wall = g.Catalog.Lookup("ItmWall1x1");
        if (backpack is null || scrap is null || wall is null) return;
        Assert.NotNull(backpack.ContainerCT);   // the container names a filter

        Assert.True(ContainerFilter.Accepts(g.Catalog, backpack, scrap));    // loose cargo fits
        Assert.False(ContainerFilter.Accepts(g.Catalog, backpack, wall));    // installed structure doesn't (IsInstalled)

        // the offered list is a non-empty subset of the universe and never contains installed structure
        var accepted = ContainerFilter.AcceptedBy(g.Catalog, backpack);
        Assert.NotEmpty(accepted);
        Assert.True(accepted.Count <= g.Catalog.LooseItems.Count);
        Assert.DoesNotContain(accepted, i => i.DefName == "ItmWall1x1");
    }
}
