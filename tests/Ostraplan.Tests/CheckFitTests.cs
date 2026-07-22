using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The placement law (Item.CheckFit port) on a hand-built catalog, so each socket
/// rule is isolated. Real-game-data Law cases live in <see cref="GameDataTests"/>.
/// </summary>
public class CheckFitTests
{
    private const string B = "Blank";

    // socket loots -> the conditions they mark on / test against a tile
    private static readonly LootDef[] Loots =
    [
        new("Floor", ["IsFloor", "IsFloorSealed"], []),        // a req: sealed floor present
        new("Wall", ["IsWall"], []),                           // a req: wall present
        new("Obstruction", ["IsObstruction"], []),             // a forbid: tile occupied
        new("FloorAdds", ["IsFloor", "IsFloorSealed"], []),
        new("WallAdds", ["IsWall", "IsObstruction"], []),      // like TILWallAdds: seals the tile as obstruction
        new("FixtureAdds", ["IsFixture", "IsObstruction"], []),
        new("Conduit", ["IsPowerConduit"], []),                // a SOFT req/add: what an overhead light wants adjacent
    ];

    // 1x1 sealed floor: no requirements, no forbids -> lays anywhere. Adds sealed floor.
    private static readonly PartDef Floor = Part("Floor", 1, ["FloorAdds"],
        [B, B, B, B, B, B, B, B, B],
        [B, B, B, B, B, B, B, B, B]);

    // 1x1 wall: free-standing, but forbids an obstruction on its own tile (idx 4) -> no stacking.
    private static readonly PartDef Wall = Part("Wall", 1, ["WallAdds"],
        [B, B, B, B, B, B, B, B, B],
        [B, B, B, B, "Obstruction", B, B, B, B]);

    // 1x1 fixture: needs a sealed floor beneath (idx 4) and a wall to the EAST (idx 5),
    // and its own tile clear of obstruction (idx 4). Adds fixture+obstruction (self-conflict on re-check).
    private static readonly PartDef Fixture = Part("Fixture", 1, ["FixtureAdds"],
        [B, B, B, B, "Floor", "Wall", B, B, B],
        [B, B, B, B, "Obstruction", B, B, B, B]);

    // 1x1 conduit: free-standing, adds a power conduit to its own tile (like ItmConduit00).
    private static readonly PartDef Conduit = Part("Conduit", 1, ["Conduit"],
        [B, B, B, B, B, B, B, B, B],
        [B, B, B, B, B, B, B, B, B]);

    // 1x1 overhead light: SOFT-requires a power conduit to the NORTH (idx 1) — the ItmLitCeiling1x1 shape.
    // A soft req records an advisory when unmet but never blocks the pose (CheckFit.SoftReqs / issue #11).
    private static readonly PartDef Light = Part("Light", 1, ["FixtureAdds"],
        [B, "Conduit", B, B, B, B, B, B, B],
        [B, B, B, B, B, B, B, B, B]);

    private static PartDef Part(string name, int w, string[] adds, string[] reqs, string[] forbids) => new(
        name, name, "HULL", "core",
        new ItemDef(name, "", false, null, 0, w, adds, reqs, forbids),
        null, [], [], [], new Dictionary<string, double>(), new Dictionary<string, (double, double)>());

    private static Catalog Cat(params PartDef[] parts) => new()
    {
        Parts = parts,
        ByDefName = parts.ToDictionary(p => p.DefName),
        Loots = Loots.ToDictionary(l => l.Name),
        Triggers = new Dictionary<string, CondTriggerDef>(),
        Warnings = [],
    };

    private static ShipDocument Doc(Catalog cat, params Placement[] placements)
    {
        var doc = new ShipDocument(cat);
        foreach (var p in placements) new PlaceCommand(p).Do(doc);   // Do bypasses CheckFit: seed any state
        return doc;
    }

    [Fact]
    public void No_requirement_means_free_standing_on_empty_space()
    {
        var doc = Doc(Cat(Wall));
        Assert.True(CheckFit.Check(doc, Wall, 5, 5, 0).Ok);
    }

    [Fact]
    public void A_requirement_fails_on_empty_space()
    {
        var doc = Doc(Cat(Floor, Wall, Fixture));
        var res = CheckFit.Check(doc, Fixture, 0, 0, 0, includeEnvelope: false);
        Assert.False(res.Ok);
        Assert.Equal("needs a sealed floor beneath", res.Reason);   // off-ship: the floor req can't be met
    }

    [Fact]
    public void A_forbid_blocks_an_obstructed_tile()
    {
        // one wall marks (0,0) as obstruction; a second wall on the same tile is refused
        var doc = Doc(Cat(Wall), new Placement { DefName = "Wall", X = 0, Y = 0 });
        var res = CheckFit.Check(doc, Wall, 0, 0, 0, includeEnvelope: false);
        Assert.False(res.Ok);
        Assert.Equal("tile is already occupied", res.Reason);
        Assert.Contains((0, 0), res.FailedCells);
    }

    [Fact]
    public void Fixture_needs_both_a_floor_beneath_and_an_adjacent_wall()
    {
        var doc = Doc(Cat(Floor, Wall, Fixture), new Placement { DefName = "Floor", X = 0, Y = 0 });

        var noWall = CheckFit.Check(doc, Fixture, 0, 0, 0, includeEnvelope: false);
        Assert.False(noWall.Ok);
        Assert.Equal("needs a wall alongside", noWall.Reason);
        Assert.Contains((1, 0), noWall.FailedCells);   // the empty east cell

        new PlaceCommand(new Placement { DefName = "Wall", X = 1, Y = 0 }).Do(doc);
        Assert.True(CheckFit.Check(doc, Fixture, 0, 0, 0, includeEnvelope: false).Ok);
    }

    [Fact]
    public void Rotation_rotates_the_socket_ring()
    {
        // CW90 carries the wall requirement from the east border to the south border
        var doc = Doc(Cat(Floor, Wall, Fixture),
            new Placement { DefName = "Floor", X = 0, Y = 0 },
            new Placement { DefName = "Wall", X = 0, Y = 1 });   // south of (0,0), y is down

        Assert.True(CheckFit.Check(doc, Fixture, 0, 0, 90, includeEnvelope: false).Ok);    // rotated: wall south, present
        Assert.False(CheckFit.Check(doc, Fixture, 0, 0, 0, includeEnvelope: false).Ok);    // unrotated: wall east, absent
    }

    [Fact]
    public void Placed_part_revalidates_only_when_its_own_contribution_is_excluded()
    {
        var doc = Doc(Cat(Floor, Wall, Fixture),
            new Placement { DefName = "Floor", X = 0, Y = 0 },
            new Placement { DefName = "Wall", X = 1, Y = 0 });
        var fixture = new Placement { DefName = "Fixture", X = 0, Y = 0 };
        new PlaceCommand(fixture).Do(doc);   // (0,0) now carries the fixture's own IsObstruction

        // without self-exclusion the fixture trips its own obstruction forbid...
        Assert.False(CheckFit.Check(doc, Fixture, 0, 0, 0, self: null, includeEnvelope: false).Ok);
        // ...with it excluded, the placement is legal, as it must be
        Assert.True(CheckFit.Check(doc, Fixture, 0, 0, 0, self: fixture, includeEnvelope: false).Ok);
        // and the exclusion is fully restored afterwards
        Assert.True(doc.Conds.At(0, 0)!.ContainsKey("IsObstruction"));
    }

    [Fact]
    public void A_soft_requirement_places_with_an_advisory_instead_of_blocking()
    {
        var doc = Doc(Cat(Floor, Wall, Fixture, Conduit, Light));

        // No conduit anywhere: the light still PLACES (Ok), but carries an advisory naming the unmet soft req
        // and points at the north cell where a conduit is wanted — no cell is a hard failure.
        var noConduit = CheckFit.Check(doc, Light, 0, 0, 0, includeEnvelope: false);
        Assert.True(noConduit.Ok);
        Assert.Equal("no power conduit adjacent", noConduit.Advisory);
        Assert.Contains((0, -1), noConduit.AdvisoryCells!);   // idx 1 -> north of (0,0)
        Assert.Empty(noConduit.FailedCells);

        // Drop a conduit on that north tile and the advisory clears — a fully-legal, unflagged placement.
        new PlaceCommand(new Placement { DefName = "Conduit", X = 0, Y = -1 }).Do(doc);
        var wired = CheckFit.Check(doc, Light, 0, 0, 0, includeEnvelope: false);
        Assert.True(wired.Ok);
        Assert.Null(wired.Advisory);
    }

    [Fact]
    public void A_soft_requirement_advisory_rotates_with_the_part()
    {
        // The conduit sits SOUTH of the light; a 180° turn carries the north req (idx 1) to the south, clearing
        // the advisory. Unrotated the req still faces north (empty), so the light places but stays flagged.
        var doc = Doc(Cat(Floor, Wall, Fixture, Conduit, Light),
            new Placement { DefName = "Conduit", X = 0, Y = 1 });   // south of (0,0), y is down

        Assert.Null(CheckFit.Check(doc, Light, 0, 0, 180, includeEnvelope: false).Advisory);   // rotated: req faces south, satisfied
        Assert.NotNull(CheckFit.Check(doc, Light, 0, 0, 0, includeEnvelope: false).Advisory);   // unrotated: req faces north, unmet
    }

    [Fact]
    public void Ring_cells_beyond_a_docking_face_are_unplaceable()
    {
        var doc = Doc(DockCat(Primary, Floor), new Placement { DefName = "Primary", X = 0, Y = 0 });

        var beyond = CheckFit.Check(doc, Floor, 2, -2, 0, includeEnvelope: true);
        Assert.False(beyond.Ok);
        Assert.Equal("beyond the airlock's mating face", beyond.Reason);

        Assert.True(CheckFit.Check(doc, Floor, 2, 5, 0, includeEnvelope: true).Ok);    // inside the hull
        Assert.True(CheckFit.Check(doc, Floor, 2, -2, 0, includeEnvelope: false).Ok);  // envelope off: geometry ignored
    }

    /// <summary>
    /// Issue #5. Item.CheckFit bounds construction by aDocksys.FirstOrDefault() alone, and Ship.AddCO
    /// files every TypeB port BEHIND every non-TypeB one — so a Secondary airlock never bounds while a
    /// Primary is present, and building "past" one (an internal docking bay) is legal.
    /// </summary>
    [Fact]
    public void A_secondary_airlock_does_not_bound_construction_when_a_primary_is_present()
    {
        var doc = Doc(DockCat(Primary, Secondary, Floor),
            new Placement { DefName = "Primary", X = 0, Y = 0 },      // face at doc y = 0
            new Placement { DefName = "Secondary", X = 0, Y = 10 });  // face at doc y = 10

        Assert.Equal("Primary", doc.Part(ProblemScan.BoundingPort(doc, doc.Catalog)!)!.DefName);
        Assert.True(CheckFit.Check(doc, Floor, 2, 5, 0, includeEnvelope: true).Ok);    // beyond the SECONDARY's face: legal
        Assert.False(CheckFit.Check(doc, Floor, 2, -2, 0, includeEnvelope: true).Ok);  // beyond the PRIMARY's face: still refused
    }

    /// <summary>
    /// The flip side of issue #5: TypeB is not "never bounds", it is "sorts last". With no Primary to
    /// outrank it, a lone Secondary IS aDocksys[0] and does bound.
    /// </summary>
    [Fact]
    public void A_lone_secondary_airlock_bounds_construction()
    {
        var doc = Doc(DockCat(Secondary, Floor), new Placement { DefName = "Secondary", X = 0, Y = 0 });

        Assert.False(CheckFit.Check(doc, Floor, 2, -2, 0, includeEnvelope: true).Ok);
        Assert.True(CheckFit.Check(doc, Floor, 2, 5, 0, includeEnvelope: true).Ok);
    }

    // 7x2 docking port, DockA (0,8)px / DockB (0,24)px: outward arrow up, so the mating face lands on the
    // port's own doc y and everything above it (smaller y) is beyond. IsTypeB marks a Secondary airlock.
    private static readonly PartDef Primary = Dock("Primary", typeB: false);
    private static readonly PartDef Secondary = Dock("Secondary", typeB: true);

    private static PartDef Dock(string name, bool typeB) => new(
        name, name, "HULL", "core",
        new ItemDef(name, "", false, null, 0, 7, [.. Enumerable.Repeat("FloorAdds", 14)], [], []),
        null, [], [],
        typeB ? ["IsDockSys", "IsInstalled", ProblemScan.TypeBCond] : ["IsDockSys", "IsInstalled"],
        new Dictionary<string, double>(),
        new Dictionary<string, (double, double)> { ["DockA"] = (0, 8), ["DockB"] = (0, 24) });

    private static Catalog DockCat(params PartDef[] parts) => new()
    {
        Parts = parts,
        ByDefName = parts.ToDictionary(p => p.DefName),
        Loots = Loots.ToDictionary(l => l.Name),
        Triggers = new Dictionary<string, CondTriggerDef>
        {
            [ProblemScan.DocksysTrigger] = new(ProblemScan.DocksysTrigger, ["IsDockSys", "IsInstalled"], [], false),
        },
        Warnings = [],
    };
}
