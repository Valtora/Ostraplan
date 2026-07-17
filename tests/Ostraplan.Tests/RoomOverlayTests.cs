using System.Linq;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// <see cref="RoomOverlay"/> — the RoomViz model. It is a thin, display-facing view over the same
/// <see cref="RoomBuilder"/>/<see cref="RoomCertifier"/> pass the Ship Rating and the save-edit inject use, so
/// these cover what it adds: excluding the exterior void, doc-space tiles, certification/value, and the near-miss
/// diagnosis on an uncertified room. No install.
/// </summary>
public class RoomOverlayTests
{
    /// <summary>A sealed 3×3 room walled in, with whatever extra parts the caller wants inside it.</summary>
    private static ShipDocument SealedRoom(Catalog cat, params Placement[] extra)
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
        ps.AddRange(extra);
        return Fixtures.Doc(cat, [.. ps]);
    }

    private static readonly RoomSpecDef Bunk =
        new("Bunk", "Bunk Room", null, 4, -1, 20, false, 2.0, [new RoomReq("TBed", 1)], [new RoomReq("TCan", 1)]);

    private static Catalog BunkCatalog() => new Fixtures()
        .Floor("F").Wall("W")
        .Trig("TBed", ["IsBed", "IsInstalled"])
        .Trig("TCan", ["IsCanister", "IsInstalled"])
        .Part("Bed", tileConds: ["IsObstruction"], startingConds: ["IsBed", "IsInstalled"], basePrice: 100)
        .Part("Can", tileConds: ["IsObstruction"], startingConds: ["IsCanister", "IsInstalled"], basePrice: 10)
        .Build();

    [Fact]
    public void The_exterior_void_is_excluded_so_it_cannot_tint_the_whole_plan()
    {
        var cat = new Fixtures().Floor("F").Wall("W").Build();
        var overlay = RoomOverlay.Build(SealedRoom(cat), cat, []);

        // the partition itself has the exterior; the overlay must not
        var partition = RoomBuilder.Build(ShipGrid.FromDocument(SealedRoom(cat), cat));
        Assert.Contains(partition.Rooms, r => r.Outside);
        var room = Assert.Single(overlay.Rooms);
        Assert.Equal(9, room.TileCount);
        Assert.False(room.Void);
    }

    [Fact]
    public void Tiles_are_document_coords_so_the_canvas_can_paint_them_directly()
    {
        var cat = new Fixtures().Floor("F").Wall("W").Build();
        var overlay = RoomOverlay.Build(SealedRoom(cat), cat, []);

        var room = Assert.Single(overlay.Rooms);
        var expected = from y in Enumerable.Range(0, 3) from x in Enumerable.Range(0, 3) select (x, y);
        Assert.Equal([.. expected.OrderBy(t => t.y).ThenBy(t => t.x)],
                     [.. room.Tiles.OrderBy(t => t.Y).ThenBy(t => t.X)]);
    }

    [Fact]
    public void A_certified_room_reports_its_spec_friendly_name_and_modified_value()
    {
        var cat = BunkCatalog();
        var overlay = RoomOverlay.Build(SealedRoom(cat, Fixtures.P("Bed", 1, 1)), cat, [Bunk]);

        var room = Assert.Single(overlay.Rooms);
        Assert.True(room.Certified);
        Assert.Equal("Bunk", room.Spec);
        Assert.Equal("Bunk Room", room.SpecFriendly);
        Assert.Equal(200, room.Value);        // the bed's 100 × the spec's 2.0 modifier
        Assert.Empty(room.NearMisses);        // it certifies: nothing to diagnose
    }

    [Fact]
    public void An_uncertified_room_says_what_it_is_missing()
    {
        var cat = BunkCatalog();
        var overlay = RoomOverlay.Build(SealedRoom(cat), cat, [Bunk]);   // no bed

        var room = Assert.Single(overlay.Rooms);
        Assert.False(room.Certified);
        Assert.Equal("Blank", room.Spec);
        Assert.Contains(room.NearMisses, m => m.Contains("Bunk Room") && m.Contains("needs"));
    }

    /// <summary>The silent failure this view exists to surface: every requirement met, but a member part the spec
    /// forbids (the canister parked in a quarters) keeps it Blank. The diagnosis must name the blocker.</summary>
    [Fact]
    public void An_uncertified_room_names_the_item_that_blocks_it()
    {
        var cat = BunkCatalog();
        var overlay = RoomOverlay.Build(SealedRoom(cat, Fixtures.P("Bed", 1, 1), Fixtures.P("Can", 2, 2)), cat, [Bunk]);

        var room = Assert.Single(overlay.Rooms);
        Assert.False(room.Certified);
        Assert.Contains(room.NearMisses, m => m.Contains("remove") && m.Contains("Can"));
    }

    [Fact]
    public void An_unsealed_compartment_reads_as_void()
    {
        // a walled box with no sealed floor: enclosed (so not the exterior), but Void
        var cat = new Fixtures().Floor("F").Wall("W").Fixture("Box").Build();
        var ps = new List<Placement> { Fixtures.P("Box", 0, 0) };
        for (var i = -1; i <= 1; i++)
        {
            ps.Add(Fixtures.P("W", i, -1));
            ps.Add(Fixtures.P("W", i, 1));
            ps.Add(Fixtures.P("W", -1, i));
            ps.Add(Fixtures.P("W", 1, i));
        }
        var overlay = RoomOverlay.Build(Fixtures.Doc(cat, [.. ps]), cat, []);

        var room = Assert.Single(overlay.Rooms);
        Assert.True(room.Void);
        Assert.False(room.Certified);
    }

    [Fact]
    public void An_empty_document_yields_an_empty_overlay()
    {
        var cat = new Fixtures().Floor("F").Build();
        Assert.True(RoomOverlay.Build(Fixtures.Doc(cat), cat, []).IsEmpty);
    }
}
