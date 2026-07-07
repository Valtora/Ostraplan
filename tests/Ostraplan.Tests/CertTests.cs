using System.Collections.Generic;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>Room certification + CondTrigger evaluator unit tests. The synthetic ones
/// are deterministic (no install); the real-data ones no-op without the game.</summary>
public class CertTests
{
    private static readonly ItemDef Dummy = new("i", "", false, null, 0, 1, [], [], []);

    private static PlacedPart Part(params string[] conds)
    {
        var rp = new ResolvedPart("p", "p", Dummy, conds,
            new Dictionary<string, double>(), new Dictionary<string, (double, double)>());
        return new PlacedPart(rp, null, 0, 0, 0, 0, 0, 0);
    }

    private static RoomModel Room(int tiles, bool isVoid, params PlacedPart[] parts)
    {
        var r = new RoomModel { Void = isVoid };
        for (var i = 0; i < tiles; i++) r.Tiles.Add(i);
        r.Parts.AddRange(parts);
        return r;
    }

    private static Catalog CatalogWith(params CondTriggerDef[] trigs) => new()
    {
        Parts = [],
        ByDefName = new Dictionary<string, PartDef>(),
        Loots = new Dictionary<string, LootDef>(),
        Triggers = new List<CondTriggerDef>(trigs).ToDictionary(t => t.Name),
        Warnings = [],
    };

    [SkippableFact]
    public void Certifier_requires_multiplicity()
    {
        var cat = CatalogWith(new CondTriggerDef("TChair", ["IsChair", "IsInstalled"], [], false));
        var specs = new[]
        {
            new RoomSpecDef("Lounge", "Lounge", null, 4, -1, 20, false, 1.5, [new RoomReq("TChair", 4)], []),
        };
        var chair = () => Part("IsChair", "IsInstalled");

        Assert.Equal("Blank", RoomCertifier.Certify(Room(10, false, chair(), chair(), chair()), specs, cat));   // 3 chairs
        Assert.Equal("Lounge", RoomCertifier.Certify(Room(10, false, chair(), chair(), chair(), chair()), specs, cat)); // 4
    }

    [SkippableFact]
    public void Certifier_honors_priority_then_forbids_then_size_then_void()
    {
        var cat = CatalogWith(
            new CondTriggerDef("TBed", ["IsBed", "IsInstalled"], [], false),
            new CondTriggerDef("TReactor", ["IsReactor"], [], false));
        // higher-priority Quarters requires a bed but forbids a reactor; lower-priority Dorm just needs a bed
        var specs = new[]
        {
            new RoomSpecDef("Quarters", "Quarters", null, 8, -1, 50, false, 2, [new RoomReq("TBed", 1)], [new RoomReq("TReactor", 1)]),
            new RoomSpecDef("Dorm", "Dorm", null, 4, -1, 30, false, 1.5, [new RoomReq("TBed", 1)], []),
        };

        // bed alone in a big room → highest priority that matches = Quarters
        Assert.Equal("Quarters", RoomCertifier.Certify(Room(10, false, Part("IsBed", "IsInstalled")), specs, cat));
        // add a reactor → Quarters forbidden, falls to Dorm
        Assert.Equal("Dorm", RoomCertifier.Certify(Room(10, false, Part("IsBed", "IsInstalled"), Part("IsReactor")), specs, cat));
        // too small for Quarters (needs 8) but fits Dorm (needs 4)
        Assert.Equal("Dorm", RoomCertifier.Certify(Room(5, false, Part("IsBed", "IsInstalled")), specs, cat));
        // void room matches neither (both require non-void)
        Assert.Equal("Blank", RoomCertifier.Certify(Room(10, true, Part("IsBed", "IsInstalled")), specs, cat));
    }

    [SkippableFact]
    public void Certifier_ignores_floor_grate_members()
    {
        var cat = CatalogWith(new CondTriggerDef("TChair", ["IsChair"], [], false),
                              new CondTriggerDef("TBad", ["IsBad"], [], false));
        var specs = new[] { new RoomSpecDef("R", "R", null, 1, -1, 10, false, 1, [new RoomReq("TChair", 1)], [new RoomReq("TBad", 1)]) };
        // a floor-grate carrying the forbidden cond is skipped, so the room still certifies
        Assert.Equal("R", RoomCertifier.Certify(Room(4, false, Part("IsChair"), Part("IsBad", "IsFloorGrate")), specs, cat));
    }

    [SkippableFact]
    public void CondEval_nested_OR_and_forbids_on_real_triggers()
    {
        var g = TestData.RequireGame();
        bool Fires(string trig, params string[] conds) =>
            g.Catalog.Triggers.TryGetValue(trig, out var ct) && CondEval.Triggered(ct, conds, g.Catalog);

        // TIsRoomCargo = OR(TIsStorageBinInstalled, TIsRackInstalled); each is bAND with IsInstalled
        Assert.True(Fires("TIsRoomCargo", "IsRack", "IsInstalled"));
        Assert.True(Fires("TIsRoomCargo", "IsStorageBin", "IsInstalled"));
        Assert.False(Fires("TIsRoomCargo", "IsRack"));                 // not installed
        Assert.False(Fires("TIsRoomCargo", "IsInstalled"));            // neither rack nor bin

        // TIsChairInstalled = [IsChair, IsInstalled] forbid IsDamaged
        Assert.True(Fires("TIsChairInstalled", "IsChair", "IsInstalled"));
        Assert.False(Fires("TIsChairInstalled", "IsChair", "IsInstalled", "IsDamaged"));  // forbidden
        Assert.False(Fires("TIsChairInstalled", "IsChair"));                              // needs IsInstalled
    }
}
