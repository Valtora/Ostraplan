using System.Collections.Generic;
using System.Linq;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// <see cref="GasPorts"/>: the tiles of a tank or canister a gas input can feed it from, which the canvas draws on
/// the vessel to line up with a regulator's GAS badge. A port is a body tile beside the body's edge, because every
/// stock regulator stands its own body on floor next to the vessel.
/// </summary>
public class GasPortsTests
{
    private static readonly Dictionary<string, double> Gas = new() { ["StatGasPressureMax"] = 41400, ["StatVolume"] = 0.787 };

    private static Catalog Vessels() => new Fixtures()
        .Part("Rta", tileConds: ["IsFixture"], startingConds: ["IsInstalled", "IsAirtight"], condValues: Gas)
        // the big tanks: a 3×3 body inside a two-tile under-floor ring, so a 7×7 socket grid
        .Part("BigTank", 3, 3, tileConds: ["IsFixture", "IsObstruction"], apron: 2,
            startingConds: ["IsInstalled", "IsAirtight"], condValues: Gas)
        .Part("Crate", tileConds: ["IsFixture"], startingConds: ["IsInstalled", "IsAirtight"])   // no gas rating
        .Part("Bottle", tileConds: ["IsFixture"], startingConds: ["IsInstalled"], condValues: Gas)   // not airtight
        .Build();

    [Fact]
    public void A_one_tile_canister_is_its_own_port()
    {
        var cat = Vessels();
        Assert.Equal(new[] { (0, 0) }, GasPorts.Tiles(cat.Lookup("Rta")!, cat));
    }

    [Fact]
    public void A_big_tank_ports_on_its_body_edge_and_not_its_middle_or_ring()
    {
        var cat = Vessels();
        var ports = GasPorts.Tiles(cat.Lookup("BigTank")!, cat).ToHashSet();

        // body (2..4, 2..4) of the 7×7 grid: its eight edge tiles, never the middle, never the under-floor ring
        Assert.Equal(8, ports.Count);
        Assert.DoesNotContain((3, 3), ports);
        Assert.All(ports, p => Assert.True(p.X is >= 2 and <= 4 && p.Y is >= 2 and <= 4, $"{p} is off the body"));

        // a square body ports the same tiles whichever way it faces
        Assert.True(ports.SetEquals(GasPorts.Tiles(cat.Lookup("BigTank")!, cat, 90)));
    }

    [Fact]
    public void Only_an_airtight_part_rated_for_gas_is_a_vessel()
    {
        var cat = Vessels();
        Assert.Empty(GasPorts.Tiles(cat.Lookup("Crate")!, cat));
        Assert.Empty(GasPorts.Tiles(cat.Lookup("Bottle")!, cat));
    }

    /// <summary>The 1.1.0.0 N2 Tank and the N2 RTA against their real defs.</summary>
    [SkippableFact]
    public void The_stock_n2_vessels_port_where_the_core_ships_plumb_them()
    {
        var g = TestData.RequireGame();
        Skip.IfNot(g.Catalog.ByDefName.ContainsKey("ItmCanisterN2Big"), "no N2 Tank in this install (it arrived in 1.1.0.0)");

        var tank = g.Catalog.Lookup("ItmCanisterN2Big")!;
        var body = g.Catalog.BodyBox(tank);
        var ports = GasPorts.Tiles(tank, g.Catalog);
        Assert.Equal((3, 3), (body.W, body.H));
        Assert.Equal(8, ports.Count);
        Assert.DoesNotContain((body.X + 1, body.Y + 1), ports);

        if (g.Catalog.Lookup("ItmRTAN2") is { } rta)
            Assert.Single(GasPorts.Tiles(rta, g.Catalog));
    }
}
