using System.IO;
using System.Linq;
using Ostraplan.Core;
using Xunit;
using Xunit.Abstractions;

namespace Ostraplan.Tests;

public class RatingTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    [Fact]
    public void Condition_maneuver_size_cutoffs_are_pinned()
    {
        // condition A-E (mean of 1-damageRate)
        Assert.Equal("E", Rating.ConditionGrade(0.5));
        Assert.Equal("D", Rating.ConditionGrade(0.5001));
        Assert.Equal("D", Rating.ConditionGrade(0.8));
        Assert.Equal("C", Rating.ConditionGrade(0.8001));
        Assert.Equal("C", Rating.ConditionGrade(0.95));
        Assert.Equal("B", Rating.ConditionGrade(0.96));
        Assert.Equal("B", Rating.ConditionGrade(0.99));
        Assert.Equal("A", Rating.ConditionGrade(0.991));
        Assert.Equal("A", Rating.ConditionGrade(1.0));

        // maneuver mass/RCS
        Assert.Equal("O", Rating.ManeuverGrade(0));
        Assert.Equal("A", Rating.ManeuverGrade(1));
        Assert.Equal("A", Rating.ManeuverGrade(299.9));
        Assert.Equal("B", Rating.ManeuverGrade(300));
        Assert.Equal("C", Rating.ManeuverGrade(500));
        Assert.Equal("D", Rating.ManeuverGrade(750));
        Assert.Equal("E", Rating.ManeuverGrade(1500));
        Assert.Equal("E", Rating.ManeuverGrade(9999));

        // size class by grid area
        Assert.Equal("Small", Rating.SizeClass(249));
        Assert.Equal("Medium", Rating.SizeClass(250));
        Assert.Equal("Lunamax", Rating.SizeClass(900));
        Assert.Equal("Ceresmax", Rating.SizeClass(1600));
        Assert.Equal("Titanmax", Rating.SizeClass(2300));
        Assert.Equal("Very Large", Rating.SizeClass(3000));
        Assert.Equal("Very Large", Rating.SizeClass(3699));
        Assert.Equal("Ultra Large", Rating.SizeClass(3700));
    }

    [Fact]
    public void Rating_parity_on_baked_rating_templates()
    {
        if (TestData.Game is not { } g) return;
        var resolver = new PartResolver(g.Index);
        var specs = RoomCertifier.LoadSpecs(g.Index);

        var dir = Path.Combine(g.Env.CoreDataDir, "ships");
        var checkedShips = 0;
        foreach (var path in Directory.EnumerateFiles(dir, "*.json"))
        {
            foreach (var ship in ShipTemplate.ParseFile(File.ReadAllText(path)))
            {
                if (!ship.HasBakedRating) continue;
                checkedShips++;

                var grid = ShipGrid.FromTemplate(ship, resolver, g.Catalog);
                var rooms = RoomBuilder.Build(grid);
                RoomCertifier.CertifyAll(rooms, specs, g.Catalog);
                var mine = Rating.Calculate(grid, rooms, g.Catalog);

                var stored = ship.Rating;
                _out.WriteLine($"{ship.Name}: stored=[{string.Join(",", stored)}]  mine=[cond={mine.Condition} rooms={mine.RoomCount} manv={mine.Maneuver} size={mine.Size}]  area={grid.NCols * grid.NRows}");

                // Slot 4 (size class) is purely geometric — must match exactly. "Babak Refit"
                // is excluded: its baked aRating is a verbatim copy of the base Babak's (same
                // string), stale after the refit grew the hull from 37×95 to 37×101, so its
                // stored size no longer matches its own grid.
                if (ship.Name != "Babak Refit")
                    Assert.Equal(stored[4], mine.Size);

                // Slot 2 (room count): the game counts contained/pre-populated cargo the
                // top-level loader can't (see certification parity), so recomputed count is a
                // lower bound on these cargo-laden derelicts. Never over-counts.
                Assert.True(int.Parse(mine.RoomCount) <= int.Parse(stored[2]),
                    $"room count {mine.RoomCount} exceeds stored {stored[2]}");

                // Slots 1 (condition) and 3 (maneuver) depend on runtime damage / full cargo
                // mass a layout-only import doesn't carry; both baked-rating templates are
                // damaged derelicts, so these are not asserted against the corpus (unit-tested above).
            }
        }
        Assert.True(checkedShips >= 2, $"expected the 2 baked-rating templates, saw {checkedShips}");
    }
}
