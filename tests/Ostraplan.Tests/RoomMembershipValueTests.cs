using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The 0.17.0 room-membership and value-law fixes, decompile-verified against 0.15.1.6 (both from
/// Discord field reports):
/// <list type="bullet">
///   <item><b>Tile.AddToRoom's "use"-point fallback</b>: a part whose anchor tile is a room-less
///   ship tile (wall-embedded — wall bins, sensors, antennas, weapons) joins the room at its "use"
///   map point instead of silently dropping out of certification and room value.</item>
///   <item><b>The O2 value bonus is not "has a pump"</b>: Ship.AddICO registers a pump only when
///   ShipStatus.GetO2UnderPump finds an installed O2 RTA with gas at the pump's GasInput tile;
///   GetShipValue then applies ×3 as a flag (never per-pump).</item>
/// </list>
/// </summary>
public class RoomMembershipValueTests
{
    // ---- use-point fallback, synthetic (no install) ----

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

    private static readonly Dictionary<string, (double X, double Y)> UseSouth =
        new() { ["use"] = (0, -16) };   // one tile south (game px, +y up)

    [Fact]
    public void A_wall_anchored_part_joins_the_room_at_its_use_point()
    {
        var cat = RoomFixtures()
            .Part("WallThing", startingConds: ["IsInstalled"], mapPoints: UseSouth)
            .Build();
        var ps = SealedBox();
        ps.Add(Fixtures.P("WallThing", 1, -1));   // anchored ON the north wall; use → (1,0), in the room

        var part = RoomBuilder.Build(ShipGrid.FromDocument(Fixtures.Doc(cat, ps.ToArray()), cat));

        var interior = Assert.Single(part.Rooms, r => !r.Void);
        Assert.Contains(interior.Parts, p => p.Part.DefName == "WallThing");
    }

    [Fact]
    public void A_wall_anchored_part_without_a_use_point_joins_no_room()
    {
        var cat = RoomFixtures()
            .Part("Mute", startingConds: ["IsInstalled"])   // no map points — GetPos("use") = its own wall tile
            .Build();
        var ps = SealedBox();
        ps.Add(Fixtures.P("Mute", 1, -1));

        var part = RoomBuilder.Build(ShipGrid.FromDocument(Fixtures.Doc(cat, ps.ToArray()), cat));

        Assert.DoesNotContain(part.Rooms, r => r.Parts.Any(p => p.Part.DefName == "Mute"));
    }

    [Fact]
    public void A_use_point_facing_empty_space_rescues_nothing()
    {
        // the game's use-point lookup (GetTileAtWorldCoords1) returns null for a tile with no
        // ship conds (TIsShipTileOrSub) — so a wall part whose use point faces OUTWARD joins
        // no room, even though Ostraplan's exterior void room nominally claims that tile
        var cat = RoomFixtures()
            .Part("Outward", startingConds: ["IsInstalled"],
                mapPoints: new Dictionary<string, (double X, double Y)> { ["use"] = (0, 16) })   // one tile north
            .Build();
        var ps = SealedBox();
        ps.Add(Fixtures.P("Outward", 1, -1));   // anchored ON the north wall; use → (1,-2), empty space

        var part = RoomBuilder.Build(ShipGrid.FromDocument(Fixtures.Doc(cat, ps.ToArray()), cat));

        Assert.DoesNotContain(part.Rooms, r => r.Parts.Any(p => p.Part.DefName == "Outward"));
    }

    // ---- use-point fallback, real data (the Discord "bins present but quarters won't certify" case) ----

    /// <summary>Sealed w×h floor room with its top-left floor tile at (0,0), walls around it, on real defs.</summary>
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

    [SkippableFact]
    public void A_south_wall_storage_bin_still_counts_as_a_room_member()
    {
        var g = TestData.RequireGame();
        Skip.If(g.Catalog.Lookup("ItmStorageBin1x101") is null, "wall bin def not present");

        // rot 180 mounts the 1×2 bin on the SOUTH wall: cells (2, h-1) floor + (2, h) wall. Its anchor
        // (centre rounds away-from-zero) is the WALL cell, so pre-fix it silently left every room.
        var ps = RealRoom(5, 4);
        ps.Add(Fixtures.P("ItmStorageBin1x101", 2, 3, 180));

        var grid = ShipGrid.FromDocument(Fixtures.Doc(g.Catalog, ps.ToArray()), g.Catalog);
        var partition = RoomBuilder.Build(grid);

        var interior = Assert.Single(partition.Rooms, r => !r.Void);
        Assert.Contains(interior.Parts, p => p.Part.DefName == "ItmStorageBin1x101");
    }

    [SkippableFact]
    public void Basic_quarters_certifies_with_a_south_wall_bin()
    {
        var g = TestData.RequireGame();
        Skip.If(g.Catalog.Lookup("ItmStorageBin1x101") is null || g.Catalog.Lookup("ItmBed01") is null
            || g.Catalog.Lookup("ItmLitCeiling1x1") is null, "quarters defs not present");
        var specs = RoomCertifier.LoadSpecs(g.Index);

        // 5×6 sealed room (30 tiles ≥ the 8 BasicQuarters needs): bed + ceiling light on the floor,
        // and the ONLY storage bin mounted on the south wall — the exact Discord failure, where the
        // bin's wall-side anchor silently removed it from the room and quarters could never certify
        var ps = RealRoom(5, 6);
        ps.Add(Fixtures.P("ItmBed01", 0, 0));                 // 3×5, anchor well inside the room
        ps.Add(Fixtures.P("ItmLitCeiling1x1", 4, 0));
        ps.Add(Fixtures.P("ItmStorageBin1x101", 3, 5, 180));  // cells (3,5) floor + (3,6) south wall

        var grid = ShipGrid.FromDocument(Fixtures.Doc(g.Catalog, ps.ToArray()), g.Catalog);
        var partition = RoomBuilder.Build(grid);
        RoomCertifier.CertifyAll(partition, specs, g.Catalog);

        var interior = Assert.Single(partition.Rooms, r => !r.Void);
        Assert.Equal("BasicQuarters", interior.RoomSpec);
    }

    // ---- the O2 bonus rule, real data ----

    /// <summary>Pump at (10,10) rot 0: 1×3 vertical, GasInput = its top cell (10,10).</summary>
    private static ShipGrid PumpGrid(Catalog cat, bool withCan, string canDef = "ItmRTAO2", int pumpRot = 0,
        (int X, int Y)? canAt = null)
    {
        var ps = new List<Placement> { Fixtures.P("ItmAirPump02OnG", 10, 10, pumpRot) };
        if (withCan) ps.Add(Fixtures.P(canDef, canAt?.X ?? 10, canAt?.Y ?? 10));
        return ShipGrid.FromDocument(Fixtures.Doc(cat, ps.ToArray()), cat);
    }

    [SkippableFact]
    public void A_pump_alone_earns_no_o2_bonus()
    {
        var g = TestData.RequireGame();
        Skip.If(g.Catalog.Lookup("ItmAirPump02OnG") is null, "pump def not present");

        Assert.Equal(0, ShipValue.CountO2Pumps(PumpGrid(g.Catalog, withCan: false), g.Catalog));
    }

    [SkippableFact]
    public void A_pump_fed_by_an_o2_rta_at_its_gas_input_qualifies()
    {
        var g = TestData.RequireGame();
        Skip.If(g.Catalog.Lookup("ItmAirPump02OnG") is null || g.Catalog.Lookup("ItmRTAO2") is null,
            "pump/RTA defs not present");

        // rot 0: GasInput is the pump's top cell (10,10); the installed O2 RTA sits on that tile
        Assert.Equal(1, ShipValue.CountO2Pumps(PumpGrid(g.Catalog, withCan: true), g.Catalog));

        // rot 180 flips the pump: GasInput lands on the bottom cell (10,12)
        Assert.Equal(1, ShipValue.CountO2Pumps(
            PumpGrid(g.Catalog, withCan: true, pumpRot: 180, canAt: (10, 12)), g.Catalog));
    }

    [SkippableFact]
    public void An_n2_rta_or_a_misplaced_can_earns_nothing()
    {
        var g = TestData.RequireGame();
        Skip.If(g.Catalog.Lookup("ItmAirPump02OnG") is null || g.Catalog.Lookup("ItmRTAN2") is null,
            "pump/RTA defs not present");

        // right tile, wrong gas: the N2 RTA is IsRTA+IsInstalled but not IsVesselO2
        Assert.Equal(0, ShipValue.CountO2Pumps(PumpGrid(g.Catalog, withCan: true, canDef: "ItmRTAN2"), g.Catalog));

        // right gas, wrong tile: the O2 RTA next to the pump instead of at its GasInput
        Assert.Equal(0, ShipValue.CountO2Pumps(PumpGrid(g.Catalog, withCan: true, canAt: (11, 10)), g.Catalog));
    }

    [SkippableFact]
    public void The_value_bonus_is_a_flag_not_a_per_pump_multiplier()
    {
        var g = TestData.RequireGame();
        Skip.If(g.Catalog.Lookup("ItmAirPump02OnG") is null || g.Catalog.Lookup("ItmRTAO2") is null,
            "pump/RTA defs not present");
        var specs = RoomCertifier.LoadSpecs(g.Index);

        ShipValueEstimate Value(int pumps)
        {
            var ps = RealRoom(6, 6);
            for (var i = 0; i < pumps; i++)
            {
                // embed each pump in the north wall: cells (x,-2)+(x,-1)+(x,0), wall row -1 in the middle;
                // GasInput at rot 180 is the bottom cell (x,0), inside the room, where the O2 RTA sits
                ps.Add(Fixtures.P("ItmAirPump02OnG", 1 + i, -2, 180));
                ps.Add(Fixtures.P("ItmRTAO2", 1 + i, 0));
            }
            var doc = Fixtures.Doc(g.Catalog, ps.ToArray());
            return ShipValue.Estimate(doc, g.Catalog, specs);
        }

        var none = Value(0);
        var one = Value(1);
        var two = Value(2);
        var three = Value(3);

        // the ×3 arrives with the first fed pump (the jump includes the RTA's own priced O2)...
        Assert.True(one.ShipValue > none.ShipValue * 2.5,
            $"one fed pump should roughly triple the value ({none.ShipValue:N0} → {one.ShipValue:N0})");
        // ...and each further pump adds only its parts' linear value — identical increments,
        // never another ×3 (the bonus is a flag on the whole sum)
        var inc2 = two.ShipValue - one.ShipValue;
        var inc3 = three.ShipValue - two.ShipValue;
        Assert.Equal(inc2, inc3, 1);
        Assert.True(inc2 < one.ShipValue - none.ShipValue,
            $"a second pump's gain ({inc2:N0}) must stay below the first's ×3 jump ({one.ShipValue - none.ShipValue:N0})");
    }

    [SkippableFact]
    public void Export_bakes_the_qualifying_pump_count()
    {
        var g = TestData.RequireGame();
        Skip.If(g.Catalog.Lookup("ItmAirPump02OnG") is null || g.Catalog.Lookup("ItmRTAO2") is null,
            "pump/RTA defs not present");
        var specs = RoomCertifier.LoadSpecs(g.Index);

        var ps = RealRoom(6, 6);
        ps.Add(Fixtures.P("ItmAirPump02OnG", 2, -2, 180));   // wall-embedded, fed from inside the room
        ps.Add(Fixtures.P("ItmRTAO2", 2, 0));
        var fed = ShipExport.Build(Fixtures.Doc(g.Catalog, ps.ToArray()), g.Catalog, specs, "Fed").Item1;
        Assert.Equal(1, fed.NO2PumpCount);

        var dry = ShipExport.Build(Fixtures.Doc(g.Catalog, RealRoom(6, 6).ToArray()), g.Catalog, specs, "Dry").Item1;
        Assert.Equal(0, dry.NO2PumpCount);
    }

    // ---- the near-miss diagnosis (synthetic) ----

    [Fact]
    public void Diagnose_reports_missing_and_forbidden_independently()
    {
        // spec: needs IsChair ×1 (bare cond triggers fall back to Has()), forbids IsCanister
        var spec = new RoomSpecDef("Quarters", "Quarters", null, -1, -1, 50, false, 2.0,
            [new RoomReq("IsBed", 1), new RoomReq("IsChair", 1)],
            [new RoomReq("IsCanister", 1)]);
        var cat = new Fixtures().Build();

        var room = new RoomModel();
        room.Tiles.AddRange(Enumerable.Range(0, 12));
        PlacedPart Member(string name, params string[] conds)
        {
            var item = new ItemDef(name, name + ".png", false, null, 0, 1, ["Blank"], [], []);
            var part = new ResolvedPart(name, name, item, conds, new Dictionary<string, double>(),
                new Dictionary<string, (double, double)>());
            return new PlacedPart(part, null, 0, 0, 0, 0, 0, 0);
        }
        room.Parts.Add(Member("Bed", "IsBed", "IsInstalled"));
        room.Parts.Add(Member("O2 Tank", "IsCanister", "IsInstalled"));

        var d = RoomCertifier.Diagnose(spec, room, cat);

        Assert.True(d.ShapeOk);
        var missing = Assert.Single(d.Missing);
        Assert.Equal("IsChair", missing.Trigger);          // the bed satisfied IsBed; the chair is still owed
        Assert.Equal(1, d.SatisfiedCount);
        var blocked = Assert.Single(d.ForbiddenParts);     // AND the tank blocks the spec — both reported
        Assert.Equal("O2 Tank", blocked);
    }
}
