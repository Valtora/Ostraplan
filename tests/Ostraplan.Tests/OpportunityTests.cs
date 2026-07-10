using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The 0.18.0 "Value opportunities" hints: for every sealed room (including empty ones), the
/// higher-value specs its shape allows, what to add/remove, and the broker-sell gain. A candidate
/// must beat the current spec on BOTH value modifier and priority — certification takes the
/// highest-priority match, so items added for a lower-priority spec would change nothing.
/// </summary>
public class OpportunityTests
{
    private static Fixtures RoomFixtures() => new Fixtures().Floor("F").Wall("W");

    /// <summary>A 3×3 sealed floor at (0,0)..(2,2) inside a wall ring.</summary>
    private static List<Placement> SealedBox()
    {
        var ps = new List<Placement>();
        for (var y = 0; y < 3; y++)
            for (var x = 0; x < 3; x++)
                ps.Add(Fixtures.P("F", x, y));
        for (var i = -1; i <= 3; i++)
        {
            ps.Add(Fixtures.P("W", i, -1));
            ps.Add(Fixtures.P("W", i, 3));
            ps.Add(Fixtures.P("W", -1, i));
            ps.Add(Fixtures.P("W", 3, i));
        }
        return ps;
    }

    [Fact]
    public void An_upgrade_must_beat_the_current_spec_on_priority_not_just_value()
    {
        var cat = RoomFixtures()
            .Part("A", startingConds: ["IsA", "IsInstalled"], basePrice: 100)
            .Trig("TA", ["IsA", "IsInstalled"])
            .Trig("TB", ["IsB", "IsInstalled"])
            .Build();
        // priority-desc, as RoomCertifier.LoadSpecs would return them
        var specs = new[]
        {
            new RoomSpecDef("Up", "Up Room", null, -1, -1, 90, false, 1.8,
                [new RoomReq("TA", 1), new RoomReq("TB", 1)], []),
            new RoomSpecDef("High", "High Room", null, -1, -1, 80, false, 1.5,
                [new RoomReq("TA", 1)], []),
            // higher VALUE than High, but LOWER priority — adding its items changes nothing
            new RoomSpecDef("Low", "Low Room", null, -1, -1, 40, false, 2.0,
                [new RoomReq("TB", 1)], []),
        };
        var ps = SealedBox();
        ps.Add(Fixtures.P("A", 1, 1));

        var report = ShipAnalysis.AnalyzeDocument(Fixtures.Doc(cat, ps.ToArray()), cat, specs);

        var interior = Assert.Single(report.Rooms, r => !r.Void);
        Assert.Equal("High", interior.Spec);
        var opp = Assert.Single(report.Opportunities);
        var line = Assert.Single(opp.Lines);
        Assert.Contains("Up Room", line);                              // the only legal upgrade
        Assert.DoesNotContain(report.Opportunities, o => o.Lines.Any(l => l.Contains("Low Room")));
        Assert.Contains("+$", line);                                   // gain on the room's contents
    }

    [Fact]
    public void An_empty_room_still_gets_a_hint()
    {
        var cat = RoomFixtures()
            .Trig("TIsBinInstalled", ["IsBin", "IsInstalled"])
            .Build();
        var specs = new[]
        {
            new RoomSpecDef("Store", "Store Room", null, 4, -1, 10, false, 1.2,
                [new RoomReq("TIsBinInstalled", 2)], []),
        };

        var report = ShipAnalysis.AnalyzeDocument(Fixtures.Doc(cat, SealedBox().ToArray()), cat, specs);

        var opp = Assert.Single(report.Opportunities);
        Assert.Equal("empty", opp.CurrentSpecFriendly);
        Assert.False(opp.Certified);
        var line = Assert.Single(opp.Lines);
        Assert.Contains("Store Room", line);
        Assert.Contains("bin ×2", line);   // the prettified trigger noun with its multiplicity
    }

    [SkippableFact]
    public void A_basic_quarters_suggests_the_luxury_upgrade()
    {
        var g = TestData.RequireGame();
        Skip.If(g.Catalog.Lookup("ItmStorageBin1x101") is null || g.Catalog.Lookup("ItmBed01") is null
            || g.Catalog.Lookup("ItmLitCeiling1x1") is null, "quarters defs not present");
        var specs = RoomCertifier.LoadSpecs(g.Index);

        var ps = new List<Placement>();
        for (var y = 0; y < 6; y++)
            for (var x = 0; x < 5; x++)
                ps.Add(Fixtures.P("ItmFloorAERO01", x, y));
        for (var i = -1; i <= 5; i++)
        {
            ps.Add(Fixtures.P("ItmWall1x1", i, -1));
            ps.Add(Fixtures.P("ItmWall1x1", i, 6));
        }
        for (var i = 0; i < 6; i++)
        {
            ps.Add(Fixtures.P("ItmWall1x1", -1, i));
            ps.Add(Fixtures.P("ItmWall1x1", 5, i));
        }
        ps.Add(Fixtures.P("ItmBed01", 0, 0));
        ps.Add(Fixtures.P("ItmLitCeiling1x1", 4, 0));
        ps.Add(Fixtures.P("ItmStorageBin1x101", 3, 5, 180));

        var report = ShipAnalysis.AnalyzeDocument(Fixtures.Doc(g.Catalog, ps.ToArray()), g.Catalog, specs);

        var quarters = Assert.Single(report.Rooms, r => r.Spec == "BasicQuarters");
        var opp = report.Opportunities.FirstOrDefault(o => o.RoomIndex == quarters.Index);
        Assert.NotNull(opp);
        Assert.True(opp!.Certified);
        var line = Assert.Single(opp.Lines);
        Assert.Contains("Luxury Quarters", line);      // 50 > 35 priority, 2.0 > 1.8 modifier
        Assert.Contains("storage bin", line);          // has 1 of the 2 bins…
        Assert.Contains("chair", line);                // …and no chair yet
    }

    [SkippableFact]
    public void Ship_defining_builds_are_never_advised()
    {
        // a reactor core, nav station (bridge), and docking port (airlock) each qualify for nearly
        // every sealed room but are deliberate, ship-defining placements, not room furnishings — all
        // three are exempt from the VALUE hints (rooms that HAVE them still certify and show as such)
        var g = TestData.RequireGame();
        var specs = RoomCertifier.LoadSpecs(g.Index);

        var ps = RealRoom(4, 4);   // an empty sealed 16-tile room: pre-exemption, Reactor topped its list
        var report = ShipAnalysis.AnalyzeDocument(Fixtures.Doc(g.Catalog, ps.ToArray()), g.Catalog, specs);

        bool Suggested(string phrase) =>
            report.Opportunities.Any(o => o.Lines.Any(l => l.Contains(phrase, StringComparison.OrdinalIgnoreCase)));

        Assert.False(Suggested("reactor core"), "reactor core should not be a value hint");
        Assert.False(Suggested("nav station"), "nav station (bridge) should not be a value hint");
        Assert.False(Suggested("docking port"), "docking port (airlock) should not be a value hint");
        // the reactor is also kept out of the near-miss diagnostics (bridge/airlock stay there)
        Assert.DoesNotContain(report.Rooms,
            r => r.NearMisses.Any(l => l.Contains("reactor core", StringComparison.OrdinalIgnoreCase)));
    }

    private static List<Placement> RealRoom(int w, int h)
    {
        var ps = new List<Placement>();
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                ps.Add(Fixtures.P("ItmFloorAERO01", x, y));
        for (var i = -1; i <= w; i++)
        {
            ps.Add(Fixtures.P("ItmWall1x1", i, -1));
            ps.Add(Fixtures.P("ItmWall1x1", i, h));
        }
        for (var i = 0; i < h; i++)
        {
            ps.Add(Fixtures.P("ItmWall1x1", -1, i));
            ps.Add(Fixtures.P("ItmWall1x1", w, i));
        }
        return ps;
    }

    [Fact]
    public void Towing_room_is_only_suggested_for_airlocks_big_enough_for_the_brace()
    {
        // the brace's placement ring requires a docking-system tile and the brace itself is 7×2,
        // so "add a towing brace" is only real advice in an airlock with room for it
        var cat = RoomFixtures()
            .Part("Dock", startingConds: ["IsDock", "IsInstalled"], basePrice: 50)
            .Trig("TDock", ["IsDock", "IsInstalled"])
            .Build();
        var specs = new[]
        {
            new RoomSpecDef("TowingRoom", "Towing Room", null, 2, -1, 85, false, 1.6,
                [new RoomReq("TIsTowingBraceInstalled", 1)], []),
            new RoomSpecDef("Airlock", "Airlock", null, 3, -1, 75, false, 1.5,
                [new RoomReq("TDock", 1)], []),
        };

        // 12-tile airlock: big enough for the brace → towing is suggested
        var big = SealedWxH(4, 3);
        big.Add(Fixtures.P("Dock", 1, 1));
        var bigReport = ShipAnalysis.AnalyzeDocument(Fixtures.Doc(cat, big.ToArray()), cat, specs);
        Assert.Equal("Airlock", Assert.Single(bigReport.Rooms, r => !r.Void).Spec);
        Assert.Contains(bigReport.Opportunities, o => o.Lines.Any(l => l.Contains("Towing Room")));

        // 4-tile airlock: certifies, but a 7-tile brace can never fit → no towing hint
        var small = SealedWxH(2, 2);
        small.Add(Fixtures.P("Dock", 0, 0));
        var smallReport = ShipAnalysis.AnalyzeDocument(Fixtures.Doc(cat, small.ToArray()), cat, specs);
        Assert.Equal("Airlock", Assert.Single(smallReport.Rooms, r => !r.Void).Spec);
        Assert.DoesNotContain(smallReport.Opportunities, o => o.Lines.Any(l => l.Contains("Towing Room")));

        // 12-tile room with NO docking port: not an airlock, so no towing hint anywhere
        var plain = SealedWxH(4, 3);
        var plainReport = ShipAnalysis.AnalyzeDocument(Fixtures.Doc(cat, plain.ToArray()), cat, specs);
        Assert.DoesNotContain(plainReport.Opportunities, o => o.Lines.Any(l => l.Contains("Towing Room")));
        Assert.DoesNotContain(plainReport.Rooms, r => r.NearMisses.Any(l => l.Contains("Towing Room")));
    }

    /// <summary>A sealed w×h floor at (0,0) inside a wall ring.</summary>
    private static List<Placement> SealedWxH(int w, int h)
    {
        var ps = new List<Placement>();
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                ps.Add(Fixtures.P("F", x, y));
        for (var i = -1; i <= w; i++)
        {
            ps.Add(Fixtures.P("W", i, -1));
            ps.Add(Fixtures.P("W", i, h));
        }
        for (var i = 0; i < h; i++)
        {
            ps.Add(Fixtures.P("W", -1, i));
            ps.Add(Fixtures.P("W", w, i));
        }
        return ps;
    }

    [Fact]
    public void Kiosk_prices_are_the_value_at_the_broker_rates_with_no_pristine_markup()
    {
        // a design's installed parts are never pristine (the game only rolls it on used/derelict
        // spawns, and installing makes a fresh part), so the value is the raw room maths — no ×1.25
        var cat = RoomFixtures()
            .Part("A", startingConds: ["IsInstalled"], basePrice: 100)
            .Build();
        var ps = SealedWxH(3, 3);
        ps.Add(Fixtures.P("A", 1, 1));

        var est = ShipValue.Estimate(Fixtures.Doc(cat, ps.ToArray()), cat, []);

        Assert.Equal(100, est.ShipValue);        // Blank room ×1.0, no O2 bonus, no pristine markup
        Assert.Equal(80, est.SellEstimate);      // kiosk buys at 0.8×
        Assert.Equal(120, est.BuyEstimate);      // kiosk sells at 1.2×
    }

    [SkippableFact]
    public void Opportunities_never_double_count_rooms_or_tiles()
    {
        // rooms are a flood-fill PARTITION: each sealed room yields at most one opportunity and
        // no tile can appear under two of them — many similar entries (e.g. a tank farm where
        // every canister compartment certifies Engineering ×1.4) are genuinely distinct rooms
        var g = TestData.RequireGame();
        var specs = RoomCertifier.LoadSpecs(g.Index);
        var files = TemplateImport.ListShipFiles(g.Index);
        var entry = files.FirstOrDefault(f => f.Name == "Vector3") ?? files.FirstOrDefault();
        Skip.If(entry is null, "no ship templates available");
        var doc = TemplateImport.LoadFile(entry!.Path, g.Catalog).Doc;

        var report = ShipAnalysis.AnalyzeDocument(doc, g.Catalog, specs);

        Assert.Equal(report.Opportunities.Count, report.Opportunities.Select(o => o.RoomIndex).Distinct().Count());
        var seen = new HashSet<(int, int)>();
        foreach (var o in report.Opportunities)
        {
            Assert.Equal(o.TileCount, o.Tiles.Count);
            foreach (var t in o.Tiles)
                Assert.True(seen.Add(t), $"tile {t} appears in two opportunity rooms");
        }
    }

    [SkippableFact]
    public void The_o2_lever_is_reported_until_a_pump_is_fed()
    {
        var g = TestData.RequireGame();
        Skip.If(g.Catalog.Lookup("ItmAirPump02OnG") is null || g.Catalog.Lookup("ItmRTAO2") is null,
            "pump/RTA defs not present");
        var specs = RoomCertifier.LoadSpecs(g.Index);

        List<Placement> Room()
        {
            var ps = new List<Placement>();
            for (var y = 0; y < 4; y++)
                for (var x = 0; x < 4; x++)
                    ps.Add(Fixtures.P("ItmFloorAERO01", x, y));
            for (var i = -1; i <= 4; i++)
            {
                ps.Add(Fixtures.P("ItmWall1x1", i, -1));
                ps.Add(Fixtures.P("ItmWall1x1", i, 4));
            }
            for (var i = 0; i < 4; i++)
            {
                ps.Add(Fixtures.P("ItmWall1x1", -1, i));
                ps.Add(Fixtures.P("ItmWall1x1", 4, i));
            }
            return ps;
        }

        var dry = ShipAnalysis.AnalyzeDocument(Fixtures.Doc(g.Catalog, Room().ToArray()), g.Catalog, specs);
        Assert.False(dry.O2BonusActive);
        Assert.True(dry.O2PotentialSell > 0,           // the floors alone carry value the ×3 would triple
            $"expected a positive O2 potential, got {dry.O2PotentialSell}");

        var ps = Room();
        ps.Add(Fixtures.P("ItmAirPump02OnG", 1, -2, 180));   // embedded in the north wall, input side in-room
        ps.Add(Fixtures.P("ItmRTAO2", 1, 0));
        var fed = ShipAnalysis.AnalyzeDocument(Fixtures.Doc(g.Catalog, ps.ToArray()), g.Catalog, specs);
        Assert.True(fed.O2BonusActive);
        Assert.Equal(0, fed.O2PotentialSell);
    }
}
