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
