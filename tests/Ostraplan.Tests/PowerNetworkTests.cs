using System.Linq;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// Unit tests for <see cref="PowerNetwork"/> — the port of the game's <c>GetPoweredTiles</c>. The synthetic cases
/// exercise the flood/connectivity algorithm precisely (a source floods IsPowerPath tiles from its PowerOutput; a
/// device is connected when its input tile lands on the live set); a real-data case guards the powerinfos/jsonPI
/// resolution that feeds it.
/// </summary>
public class PowerNetworkTests
{
    // A game-free catalog with a power source, conduit, a wired device, and an override-off source. Every
    // power-carrying part contributes IsPowerPath the way TILPowerConduit / TILPowerFixtureAdds do in real data.
    private static Catalog PowerCatalog()
    {
        var f = new Fixtures();
        f.Part("Gen", tileConds: ["IsPowerPath"], startingConds: ["IsPowerGen", "IsInstalled"],
            category: "POWR", powerOutput: (0, 0));
        f.Part("GenOff", tileConds: ["IsPowerPath"], startingConds: ["IsPowerGen", "IsInstalled", "IsOverrideOff"],
            category: "POWR", powerOutput: (0, 0));
        f.Part("Cond", tileConds: ["IsPowerConduit", "IsPowerPath"], category: "POWR");
        f.Part("Dev", tileConds: ["IsPowerPath"], startingConds: ["IsInstalled"],
            category: "FURN", powerInputs: [(0, 0)]);
        return f.Build();
    }

    private static PowerResult Analyze(Catalog cat, ShipDocument doc) =>
        PowerNetwork.Build(ShipGrid.FromDocument(doc, cat), cat);

    [Fact]
    public void Generator_powers_a_conduit_run_to_a_device()
    {
        var cat = PowerCatalog();
        var doc = Fixtures.Doc(cat,
            Fixtures.P("Gen", 0, 0), Fixtures.P("Cond", 1, 0), Fixtures.P("Cond", 2, 0), Fixtures.P("Dev", 3, 0));

        var r = Analyze(cat, doc);

        // 4 tiles in a cardinal run → 3 lit segments, no orphaned runs
        Assert.Equal(3, r.PoweredSegments.Count);
        Assert.Empty(r.UnpoweredSegments);
        // the device is fed
        var dev = Assert.Single(r.Devices);
        Assert.True(dev.Connected);
        Assert.Empty(r.Unconnected);
    }

    [Fact]
    public void An_orphaned_conduit_run_is_unpowered()
    {
        var cat = PowerCatalog();
        var doc = Fixtures.Doc(cat,
            Fixtures.P("Gen", 0, 0), Fixtures.P("Cond", 1, 0),          // live: gen + one conduit
            Fixtures.P("Cond", 5, 0), Fixtures.P("Cond", 6, 0));        // orphan: two conduits, no source

        var r = Analyze(cat, doc);

        Assert.Single(r.PoweredSegments);      // gen ↔ conduit
        Assert.Single(r.UnpoweredSegments);    // the two orphan conduits
        Assert.Contains(r.UnpoweredSegments, s => s != default);
    }

    [Fact]
    public void A_device_off_the_power_path_reads_unconnected()
    {
        var cat = PowerCatalog();
        var doc = Fixtures.Doc(cat,
            Fixtures.P("Gen", 0, 0),           // a live source, but no conduit reaches the device
            Fixtures.P("Dev", 5, 0));

        var r = Analyze(cat, doc);

        var dev = Assert.Single(r.Devices);
        Assert.False(dev.Connected);
        Assert.Single(r.Unconnected);
        Assert.NotEmpty(PowerNetwork.ToOverlay(ShipGrid.FromDocument(doc, cat), r).UnconnectedPlugs);
    }

    [Fact]
    public void An_override_off_source_feeds_nothing()
    {
        var cat = PowerCatalog();
        var doc = Fixtures.Doc(cat,
            Fixtures.P("GenOff", 0, 0), Fixtures.P("Cond", 1, 0), Fixtures.P("Dev", 2, 0));

        var r = Analyze(cat, doc);

        Assert.Empty(r.PoweredSegments);
        Assert.Empty(r.PoweredTiles);
        Assert.NotEmpty(r.UnpoweredSegments);   // the conduit+device path exists but is dead
        Assert.False(Assert.Single(r.Devices).Connected);
    }

    [Fact]
    public void Overlay_projects_segments_into_document_coordinates()
    {
        var cat = PowerCatalog();
        var doc = Fixtures.Doc(cat, Fixtures.P("Gen", 0, 0), Fixtures.P("Cond", 1, 0));
        var grid = ShipGrid.FromDocument(doc, cat);

        var ov = PowerNetwork.ToOverlay(grid, PowerNetwork.Build(grid, cat));

        // the single lit segment connects doc tiles (0,0) and (1,0), in either order
        var seg = Assert.Single(ov.Powered);
        var ends = new[] { seg.A, seg.B };
        Assert.Contains((0, 0), ends);
        Assert.Contains((1, 0), ends);
    }

    [SkippableFact]
    public void Real_catalog_resolves_power_connectors()
    {
        var g = TestData.RequireGame();

        // a battery is a source: it carries a PowerOutput feed point
        Assert.True(g.Catalog.ByDefName.TryGetValue("ItmBattery02", out var batt), "ItmBattery02 missing");
        Assert.NotNull(batt!.PowerOutputPoint);

        // powered devices resolve their input plugs through jsonPI → powerinfos → aInputPts → map points
        var wired = g.Catalog.Parts.Count(p => p.PowerInputPoints.Count > 0);
        Assert.True(wired >= 10, $"only {wired} buildable parts resolved power-input points");
        Assert.Contains(g.Catalog.Parts, p => p.PowerOutputPoint is not null);
    }
}
