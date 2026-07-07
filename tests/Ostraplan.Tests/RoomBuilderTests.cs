using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// <see cref="RoomBuilder"/> partitioning on hand-built grids: the sealed/void distinction, the exterior void
/// (reaching the padded edge), and a wall splitting one compartment into two. These isolate the flood-fill law
/// away from the full-corpus parity gate. No install.
/// </summary>
public class RoomBuilderTests
{
    private static RoomPartition Partition(Catalog cat, params Placement[] ps) =>
        RoomBuilder.Build(ShipGrid.FromDocument(Fixtures.Doc(cat, ps), cat));

    [Fact]
    public void An_unsealed_tile_is_void_and_reaches_the_outside()
    {
        var cat = new Fixtures().Fixture("Box").Build();   // IsFixture/IsObstruction, no IsFloorSealed
        var part = Partition(cat, Fixtures.P("Box", 0, 0));

        Assert.All(part.Rooms, r => Assert.True(r.Void));        // nothing is sealed
        Assert.Contains(part.Rooms, r => r.Outside);            // the fill reached the padded edge
    }

    [Fact]
    public void A_sealed_floor_box_is_one_interior_room_plus_the_exterior_void()
    {
        var cat = new Fixtures().Floor("F").Wall("W").Build();
        var ps = new List<Placement>();
        for (var y = 0; y < 3; y++)
            for (var x = 0; x < 3; x++)
                ps.Add(Fixtures.P("F", x, y));          // 3×3 sealed floor
        for (var i = -1; i <= 3; i++)                    // ring of walls around it
        {
            ps.Add(Fixtures.P("W", i, -1));
            ps.Add(Fixtures.P("W", i, 3));
            ps.Add(Fixtures.P("W", -1, i));
            ps.Add(Fixtures.P("W", 3, i));
        }

        var part = Partition(cat, ps.ToArray());

        var interior = Assert.Single(part.Rooms, r => !r.Void);
        Assert.Equal(9, interior.TileCount);
        Assert.Contains(part.Rooms, r => r.Outside);   // the void exterior still forms outside the walls
    }

    [Fact]
    public void A_dividing_wall_splits_one_compartment_into_two_sealed_rooms()
    {
        var cat = new Fixtures().Floor("F").Wall("W").Build();
        var ps = new List<Placement>();
        for (var x = 0; x < 5; x++) ps.Add(Fixtures.P("F", x, 0));   // a 5-wide corridor
        ps.Add(Fixtures.P("W", 2, 0));                                // ...split in the middle
        for (var x = -1; x <= 5; x++)                                 // sealed top and bottom
        {
            ps.Add(Fixtures.P("W", x, -1));
            ps.Add(Fixtures.P("W", x, 1));
        }
        ps.Add(Fixtures.P("W", -1, 0));                              // and both ends
        ps.Add(Fixtures.P("W", 5, 0));

        var part = Partition(cat, ps.ToArray());

        var sealedRooms = part.Rooms.Where(r => !r.Void).ToList();
        Assert.Equal(2, sealedRooms.Count);
        Assert.All(sealedRooms, r => Assert.Equal(2, r.TileCount));   // {0,1} and {3,4}
    }
}
