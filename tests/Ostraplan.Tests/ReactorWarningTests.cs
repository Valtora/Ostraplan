using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The more-than-one-reactor warning (#72). The game files every core matching <c>TIsReactorICNAVUsable</c> into
/// <c>Ship.aCores</c> and then runs the ship from <c>aCores[0]</c> alone, so a second core is advice worth giving,
/// and what it counts has to be what the game counts: not the station power generators and not the modules.
/// </summary>
public class ReactorWarningTests
{
    private static PartDef Part(string name, int w, int h, params string[] conds) => new(
        name, name, "POWR", "core",
        new ItemDef(name, "", false, null, 0, w, [.. Enumerable.Repeat("L", w * h)], [], []),
        null, [], [], conds,
        new Dictionary<string, double>(),
        new Dictionary<string, (double, double)>());

    // Geometry as ProblemScanTests' Secondary port, so the design has a port and scans past "No docking port".
    private static PartDef Dock() => new(
        "Dock", "Dock", "HULL", "core",
        new ItemDef("Dock", "", false, null, 0, 7, [.. Enumerable.Repeat("L", 14)], [], []),
        null, [], [],
        ["IsDockSys", "IsInstalled"],
        new Dictionary<string, double>(),
        new Dictionary<string, (double, double)> { ["DockA"] = (0, 8), ["DockB"] = (0, 24) });

    private static Catalog Cat() => new()
    {
        Parts = [],
        ByDefName = new[]
        {
            Dock(),
            Part("Core", 5, 5, "IsFusionReactorCore", "IsInstalled"),
            Part("Generator", 3, 3, "IsReactorIC02", "IsInstalled"),   // a station's IC02: not a ship reactor
            Part("Laser", 1, 1, "IsFusionCoreModule", "IsInstalled"),  // one of the core's own modules
        }.ToDictionary(p => p.DefName),
        Loots = new Dictionary<string, LootDef> { ["L"] = new("L", ["IsX"], []) },
        Triggers = new Dictionary<string, CondTriggerDef>
        {
            [ProblemScan.DocksysTrigger] = new(ProblemScan.DocksysTrigger, ["IsDockSys", "IsInstalled"], [], false),
            [Propulsion.ReactorTrigger] = new(Propulsion.ReactorTrigger, ["IsFusionReactorCore", "IsInstalled"], [], false),
        },
        Warnings = [],
    };

    private static ShipDocument Doc(Catalog cat, params Placement[] placements)
    {
        var doc = new ShipDocument(cat);
        new PlaceCommand(new Placement { DefName = "Dock", X = 0, Y = 0 }).Do(doc);
        foreach (var p in placements) new PlaceCommand(p).Do(doc);
        return doc;
    }

    private static Problem? Warning(ShipDocument doc, Catalog cat) =>
        ProblemScan.Scan(doc, cat).SingleOrDefault(p => p.DismissKey == ProblemScan.MultipleReactorsAlertKey);

    [Fact]
    public void One_core_is_what_a_ship_is_meant_to_have()
    {
        var cat = Cat();
        Assert.Null(Warning(Doc(cat, new Placement { DefName = "Core", X = 0, Y = 5 }), cat));
    }

    [Fact]
    public void A_second_core_is_a_dismissible_warning_naming_both()
    {
        var cat = Cat();
        var doc = Doc(cat,
            new Placement { DefName = "Core", X = 0, Y = 5 },
            new Placement { DefName = "Core", X = 10, Y = 5 });

        var problem = Warning(doc, cat);
        Assert.NotNull(problem);
        Assert.Equal(ProblemSeverity.Warning, problem.Severity);   // the game builds it; it just runs it badly
        Assert.Equal("2 fusion reactor cores", problem.Title);
        Assert.Contains("(0,5)", problem.Detail);
        Assert.Contains("(10,5)", problem.Detail);
        Assert.Contains((0, 5), problem.Cells!);                   // both footprints are highlighted, whole
        Assert.Contains((14, 9), problem.Cells!);
        Assert.Equal(50, problem.Cells!.Count);
    }

    [Fact]
    public void Station_generators_and_core_modules_are_not_counted_as_reactors()
    {
        var cat = Cat();
        var doc = Doc(cat,
            new Placement { DefName = "Core", X = 0, Y = 5 },
            new Placement { DefName = "Generator", X = 10, Y = 5 },
            new Placement { DefName = "Generator", X = 14, Y = 5 },
            new Placement { DefName = "Laser", X = 20, Y = 5 },
            new Placement { DefName = "Laser", X = 21, Y = 5 });

        Assert.Null(Warning(doc, cat));
        Assert.False(ProblemScan.IsReactorCore(cat.ByDefName["Generator"], cat));
        Assert.False(ProblemScan.IsReactorCore(cat.ByDefName["Laser"], cat));
        Assert.True(ProblemScan.IsReactorCore(cat.ByDefName["Core"], cat));
    }

    /// <summary>Against the real trigger and the real defs: the core counts in each installed state the game ships,
    /// and the IC02 station generators that a station carries by the dozen do not.</summary>
    [SkippableTheory]
    [InlineData("ItmFusionReactorCore01Off", true)]
    [InlineData("ItmFusionReactorCore01Ignition", true)]
    [InlineData("ItmReactorIC02Off", false)]
    [InlineData("ItmReactorIC02IgnitionMini", false)]
    public void The_real_trigger_counts_the_core_and_not_the_station_generators(string def, bool reactor)
    {
        var g = TestData.RequireGame();
        var part = g.Catalog.Lookup(def);
        Skip.If(part is null, $"{def} is not in the loaded data");
        Assert.Equal(reactor, ProblemScan.IsReactorCore(part, g.Catalog));
    }
}
