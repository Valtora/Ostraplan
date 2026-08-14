using System.Text.Json.Nodes;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// "Repair All": the two different things a damaged part can mean, and the two different fixes.
///
/// <para>A part can be <b>broken as a def</b> — a damaged wall, a patched hull plate, a wrecked alarm — which is a
/// fact about the layout, travels in the <c>.oplan</c>, and is fixed in the editor by swapping the def
/// (<see cref="Repair"/>). Or it can carry accumulated <c>StatDamage</c> against its health pool, which exists only
/// on a save's condition owners and is cleared on the way in by <see cref="WearOptions.Repaired"/>. The two are
/// independent, and a ship imported out of a real save usually has both.</para>
///
/// <para>The mapping half is game-gated: it is read out of <c>data/installables</c>, and the thing most worth
/// pinning is that the game files two <i>different</i> jobs under <c>strJobType: "repair"</c> and only one of them
/// is a repair.</para>
/// </summary>
public class RepairTests
{
    private static readonly IReadOnlyList<RoomSpecDef> NoSpecs = [];

    private static Catalog BrokenCat() => new Fixtures()
        .Part("Wall", startingConds: ["IsInstalled", "IsWall"])
        .Part("WallDmg", startingConds: ["IsInstalled", "IsWall", "IsDamaged"])
        .Part("Alarm", startingConds: ["IsInstalled"])
        .Part("AlarmDmg", startingConds: ["IsInstalled", "IsDamaged"])
        .Part("Rock", startingConds: ["IsInstalled"])
        .RepairPair("WallDmg", "Wall")
        .RepairPair("AlarmDmg", "Alarm")
        .Build();

    // ---- what counts as broken ----

    [Fact]
    public void Repairable_finds_the_broken_parts_and_leaves_everything_else_alone()
    {
        var cat = BrokenCat();
        var doc = Fixtures.Doc(cat,
            Fixtures.P("WallDmg", 0, 0), Fixtures.P("Wall", 1, 0),
            Fixtures.P("AlarmDmg", 2, 0), Fixtures.P("Rock", 3, 0));

        var repairs = Repair.RepairableAll(doc);

        Assert.Equal(2, repairs.Count);
        Assert.Equal(["Wall", "Alarm"], repairs.Select(r => r.Target).ToArray());
        // an intact part has no repair form at all, which is what keeps "Repair" off an undamaged ship's menu
        Assert.Empty(Repair.Repairable(doc, [doc.Placements[1], doc.Placements[3]]));
    }

    [Fact]
    public void A_part_fixed_to_the_ship_is_never_repaired()
    {
        // the primary airlock is the one part the editor refuses to rebuild; a repair has to respect that too
        var cat = new Fixtures()
            .Part(Catalog.PrimaryDocksysDef, startingConds: ["IsInstalled"])
            .Part("Airlock", startingConds: ["IsInstalled"])
            .RepairPair(Catalog.PrimaryDocksysDef, "Airlock")
            .Build();
        var doc = Fixtures.Doc(cat, Fixtures.P(Catalog.PrimaryDocksysDef, 0, 0));

        Assert.Empty(Repair.RepairableAll(doc));
    }

    // ---- the swap ----

    [Fact]
    public void Repairing_keeps_the_tile_rotation_name_and_cargo()
    {
        var cat = BrokenCat();
        var doc = Fixtures.Doc(cat, new Placement
        {
            DefName = "AlarmDmg", X = 3, Y = 4, Rot = 90, CustomName = "Port Alarm",
        });

        var swap = FormSwap.BuildSwap(doc, Repair.RepairableAll(doc))!.Value;
        swap.Cmd.Do(doc);

        var fixedUp = Assert.Single(doc.Placements);
        Assert.Equal("Alarm", fixedUp.DefName);
        Assert.Equal((3, 4, 90), (fixedUp.X, fixedUp.Y, fixedUp.Rot));
        Assert.Equal("Port Alarm", fixedUp.CustomName);
    }

    [Fact]
    public void Repairing_a_ships_own_part_is_a_state_change_so_the_edit_cost_prices_it_as_a_move()
    {
        var cat = BrokenCat();
        var doc = Fixtures.Doc(cat, new Placement { DefName = "WallDmg", X = 0, Y = 0, OriginStrID = "a" });

        var swap = FormSwap.BuildSwap(doc, Repair.RepairableAll(doc))!.Value;
        swap.Cmd.Do(doc);

        var fixedUp = Assert.Single(doc.Placements);
        // the def changed, so the save's item record can't be reused (OriginStrID goes) — but the player still owns
        // the thing, and SwappedFromStrID is what stops EditCost billing a repair as newly conjured material
        Assert.Null(fixedUp.OriginStrID);
        Assert.Equal("a", fixedUp.SwappedFromStrID);
        Assert.Equal("WallDmg", fixedUp.SwappedFromDef);
    }

    [Fact]
    public void Repair_all_is_one_undo_step()
    {
        var cat = BrokenCat();
        var doc = Fixtures.Doc(cat,
            Fixtures.P("WallDmg", 0, 0), Fixtures.P("WallDmg", 1, 0), Fixtures.P("AlarmDmg", 2, 0));
        var stack = new CommandStack();

        stack.Push(doc, FormSwap.BuildSwap(doc, Repair.RepairableAll(doc))!.Value.Cmd);
        Assert.All(doc.Placements, p => Assert.DoesNotContain("Dmg", p.DefName));

        stack.Undo(doc);
        Assert.Equal(2, doc.Placements.Count(p => p.DefName == "WallDmg"));
        Assert.Single(doc.Placements, p => p.DefName == "AlarmDmg");
    }

    // ---- the wear half: clearing StatDamage on the way into a save ----

    [Fact]
    public void Repaired_is_an_armed_pass_at_full_condition_and_pristine_is_not()
    {
        Assert.True(WearOptions.Repaired.Enabled);
        Assert.True(WearOptions.Repaired.IsRepair);
        // the distinction that matters on an update: pristine leaves existing damage, repaired clears it
        Assert.False(WearOptions.Pristine.IsRepair);
        Assert.False(WearOptions.Vanilla.IsRepair);
    }

    private static Catalog PanelCat() => new Fixtures()
        .Part("Panel", startingConds: ["IsInstalled"],
              condValues: new Dictionary<string, double> { ["StatDamageMax"] = 4.0 })
        .Part("Fixed", startingConds: ["IsInstalled", "IsSystem"],
              condValues: new Dictionary<string, double> { ["StatDamageMax"] = 4.0 })
        .Build();

    /// <summary>A save whose two parts BOTH already carry damage — one ordinary, one <c>IsSystem</c>. The system
    /// part is the interesting one: the wear roll deliberately never touches it, so a repair that reused the roll
    /// would leave its damage behind.</summary>
    private static SaveShipContext DamagedContext()
    {
        var items = new JsonArray(
            new JsonObject { ["strID"] = "a", ["strName"] = "Panel", ["fX"] = 100.0, ["fY"] = 200.0, ["fRotation"] = 0.0 },
            new JsonObject { ["strID"] = "b", ["strName"] = "Fixed", ["fX"] = 101.0, ["fY"] = 200.0, ["fRotation"] = 0.0 });
        var cos = new JsonArray(
            new JsonObject
            {
                ["strID"] = "a", ["strCODef"] = "Panel", ["bAlive"] = true,
                ["aConds"] = new JsonArray("StatDamageMax=1.0x4", "StatDamage=1.0x3"),
            },
            new JsonObject
            {
                ["strID"] = "b", ["strCODef"] = "Fixed", ["bAlive"] = true,
                ["aConds"] = new JsonArray("StatDamageMax=1.0x4", "StatDamage=1.0x2"),
            });
        return new SaveShipContext
        {
            Source = new SaveSourceRef("TestSave", "H-ABC"),
            ZipPath = @"C:\dummy\TestSave\TestSave.zip",
            ShipRecord = new JsonObject
            {
                ["strName"] = "Test", ["strRegID"] = "H-ABC",
                ["nCols"] = 6.0, ["nRows"] = 6.0,
                ["vShipPos"] = new JsonObject { ["x"] = 100.0, ["y"] = 200.0 },
                ["aItems"] = items, ["aCOs"] = cos, ["aCrew"] = new JsonArray(),
            },
            Origins = new Dictionary<string, OriginPart> { ["a"] = new OriginPart(0, 0, 0, []), ["b"] = new OriginPart(1, 0, 0, []) },
            ItemsById = items.Select(n => n!.AsObject()).ToDictionary(o => (string)o["strID"]!, o => (JsonNode)o),
            CosById = cos.Select(n => n!.AsObject()).ToDictionary(o => (string)o["strID"]!, o => (JsonNode)o),
            Epoch = 0,
        };
    }

    private static string[] Conds(JsonObject ship, string coId) =>
        ((JsonArray)((JsonArray)ship["aCOs"]!).Select(n => n!.AsObject()).Single(o => (string)o["strID"]! == coId)["aConds"]!)
            .Select(x => (string)x!).ToArray();

    private static bool HasDamage(JsonObject ship, string coId) =>
        Conds(ship, coId).Any(c => c.StartsWith("StatDamage=", StringComparison.Ordinal));

    private static ShipDocument DamagedShip(Catalog cat) => Fixtures.Doc(cat,
        new Placement { DefName = "Panel", X = 0, Y = 0, OriginStrID = "a" },
        new Placement { DefName = "Fixed", X = 1, Y = 0, OriginStrID = "b" });

    [Fact]
    public void Repair_clears_every_installed_parts_damage_including_the_ones_wear_skips()
    {
        var cat = PanelCat();
        var (ship, _) = SaveEdit.BuildInjectedShip(
            DamagedShip(cat), DamagedContext(), cat, NoSpecs, wear: WearOptions.Repaired);

        Assert.False(HasDamage(ship, "a"));
        Assert.False(HasDamage(ship, "b"));            // IsSystem: skipped by the roll, cleared by the repair
        Assert.Contains("StatDamageMax=1.0x4", Conds(ship, "a"));   // the health pool itself is untouched
    }

    [Fact]
    public void Repair_bakes_the_condition_grade_as_A()
    {
        var cat = PanelCat();
        var (ship, _) = SaveEdit.BuildInjectedShip(
            DamagedShip(cat), DamagedContext(), cat, NoSpecs, wear: WearOptions.Repaired);

        Assert.Equal("A", (string)((JsonArray)ship["aRating"]!)[1]!);
    }

    [Fact]
    public void Keeping_the_condition_still_leaves_the_damage_exactly_where_it_was()
    {
        var cat = PanelCat();
        var (ship, _) = SaveEdit.BuildInjectedShip(
            DamagedShip(cat), DamagedContext(), cat, NoSpecs, wear: WearOptions.Pristine);

        // the contrast that makes Repair worth having: an unarmed pass is NOT a full-condition ship
        Assert.Contains("StatDamage=1.0x3", Conds(ship, "a"));
        Assert.Contains("StatDamage=1.0x2", Conds(ship, "b"));
    }

    // ---- the mapping, off the real data ----

    [SkippableFact]
    public void Repair_forms_come_from_the_games_own_repair_jobs()
    {
        var g = TestData.RequireGame();

        Assert.Equal("ItmWall1x1", g.Catalog.RepairForm("ItmWall1x1Dmg"));
        Assert.Equal("ItmWall1x1", g.Catalog.RepairForm("ItmWall1x1Patch"));     // a patched wall is repairable too
        Assert.Equal("ItmFloorGrate01", g.Catalog.RepairForm("ItmFloorGrate01Dmg"));
        Assert.Null(g.Catalog.RepairForm("ItmWall1x1"));                          // already intact
    }

    [SkippableFact]
    public void An_undamage_job_is_not_a_repair_however_its_job_type_reads()
    {
        var g = TestData.RequireGame();

        // Both files declare strJobType "repair". The undamage jobs only grind off StatDamage and hand the same def
        // back — except for twelve door/dock entries whose loot DOES differ, and those are the trap: reading them as
        // repairs would make "Repair All" silently unlock every locked door and switch off every powered one.
        Assert.Null(g.Catalog.RepairForm("ItmDoor01ClosedOnLocked"));
        Assert.Null(g.Catalog.RepairForm("ItmDockSys02Open"));
        // and the dev-only reset jobs (strJobType "reset") are not repairs either
        Assert.Null(g.Catalog.RepairForm("ItmCrate01"));
    }

    [SkippableFact]
    public void A_themed_wall_is_repaired_into_the_same_theme()
    {
        var g = TestData.RequireGame();

        // The damaged themed wall is not a cooverlay of its own: it is a mode of ItmWallAERO01, reachable only as
        // the right-hand side of one of its mapModeSwitches pairs. Repairing it into a generic ItmWall1x1 would
        // re-skin the ship behind the user's back.
        Assert.Equal("ItmWallAERO01", g.Catalog.RepairForm("ItmWallAERO01Dmg"));
        Assert.Equal("ItmWallAERO01", g.Catalog.RepairForm("ItmWallAERO01Patch"));
    }

    [SkippableFact]
    public void A_repaired_part_is_never_still_broken_or_switched_off()
    {
        var g = TestData.RequireGame();

        Assert.NotEmpty(g.Catalog.RepairForms);
        foreach (var (broken, working) in g.Catalog.RepairForms)
        {
            var target = g.Catalog.Lookup(working);
            Skip.If(target is null, $"'{working}' does not resolve");
            Assert.DoesNotContain("IsDamaged", target!.StartingConds);
            Assert.NotEqual(broken, working);
        }

        // the game's repair jobs hand back the Off state; Ostraplan prefers the powered one, exactly as it does
        // when installing (a repaired ship whose devices are all off is not what anyone means by "repaired")
        var alarm = g.Catalog.RepairForm("ItmAlarmSmokeDmg");
        Assert.NotNull(alarm);
        Assert.DoesNotContain("IsOff", g.Catalog.Lookup(alarm!)!.StartingConds);
    }
}
