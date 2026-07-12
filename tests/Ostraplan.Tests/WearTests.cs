using System.Text.Json.Nodes;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The wear feature (0.31): the <see cref="WearModel"/> port of the game's kiosk "Used" damage pass, and its two
/// application paths — per-part <c>aCondOverrides</c> on export, per-CO <c>StatDamage</c> on save-edit. All
/// game-free (synthetic catalogs), so the damage maths and the JSON shapes are pinned without the install.
/// </summary>
public class WearTests
{
    private static readonly IReadOnlyList<RoomSpecDef> NoSpecs = [];

    // ---- WearModel (pure) ----

    [Fact]
    public void Vanilla_constants_match_the_decompiled_kiosk_pass()
    {
        // DamageAllCOs(0.33) × the 0.75 pristine factor = 0.2475 ceiling; mean condition = 1 − 0.2475/2.
        Assert.Equal(0.2475, WearModel.VanillaUsedCeiling, 6);
        Assert.Equal(0.87625, WearModel.VanillaUsedCondition, 6);
        Assert.True(WearOptions.Vanilla.Enabled);
        Assert.Equal(WearModel.VanillaUsedCondition, WearOptions.Vanilla.TargetCondition, 6);
        Assert.False(WearOptions.Pristine.Enabled);
    }

    [Theory]
    [InlineData(1.0, 0.0)]     // pristine target → no damage
    [InlineData(0.876, 0.248)] // vanilla
    [InlineData(0.5, 1.0)]
    [InlineData(0.1, 1.8)]
    public void CeilingFor_maps_target_condition_to_a_uniform_ceiling(double target, double expected)
    {
        Assert.Equal(expected, WearModel.CeilingFor(target), 3);
    }

    [Fact]
    public void SampleDamageRate_never_exceeds_the_10pct_condition_floor()
    {
        var rng = new Random(1);
        for (var i = 0; i < 10_000; i++)
        {
            var rate = WearModel.SampleDamageRate(rng, ceiling: 2.0);   // a heavier-than-max ceiling
            Assert.InRange(rate, 0.0, WearModel.MaxDamageRate);          // condition stays ≥ 10%
        }
    }

    [Fact]
    public void DamageAmount_scales_by_health_pool_and_is_zero_for_an_undamageable_part()
    {
        var rng = new Random(2);
        var ceiling = WearModel.CeilingFor(WearModel.VanillaUsedCondition);
        for (var i = 0; i < 1000; i++)
            Assert.InRange(WearModel.DamageAmount(rng, ceiling, statDamageMax: 4.0), 0.0, WearModel.VanillaUsedCeiling * 4.0);
        Assert.Equal(0.0, WearModel.DamageAmount(rng, ceiling, statDamageMax: 0.0));
    }

    [Fact]
    public void GradeFor_grades_the_mean_condition_over_all_parts()
    {
        Assert.Equal("A", WearModel.GradeFor([]));                    // no parts → pristine
        Assert.Equal("A", WearModel.GradeFor([0.0, 0.0]));           // all pristine
        Assert.Equal("E", WearModel.GradeFor([0.9, 0.9]));           // mean condition 0.1 → E
        Assert.Equal("C", WearModel.GradeFor([0.12, 0.12]));         // mean condition 0.88 → C (vanilla-ish)
    }

    // ---- export path: per-part aCondOverrides ----

    private static Catalog WearCat() => new Fixtures()
        .Part("Panel", startingConds: ["IsInstalled"], condValues: new Dictionary<string, double> { ["StatDamageMax"] = 4.0 })
        .Part("Rock", startingConds: ["IsInstalled"])   // no StatDamageMax → undamageable, never gets an override
        .Build();

    private static ShipDocument PanelShip(Catalog cat) => Fixtures.Doc(cat,
        Fixtures.P("Panel", 0, 0), Fixtures.P("Panel", 1, 0), Fixtures.P("Panel", 2, 0),
        Fixtures.P("Panel", 0, 1), Fixtures.P("Panel", 1, 1), Fixtures.P("Panel", 2, 1));

    [Fact]
    public void Export_without_wear_bakes_no_damage_and_grade_A()
    {
        var cat = WearCat();
        var (ship, rating, _) = ShipExport.Build(PanelShip(cat), cat, NoSpecs, "Pristine", wear: WearOptions.Pristine);

        Assert.All(ship.AItems, it => Assert.Null(it.ACondOverrides));
        Assert.Equal("A", rating.Condition);
        Assert.Equal("A", ship.ARating[1]);
    }

    [Fact]
    public void Export_with_vanilla_wear_damages_each_part_within_the_kiosk_ceiling()
    {
        var cat = WearCat();
        var wear = new WearOptions(true, WearModel.VanillaUsedCondition, Seed: 123);
        var (ship, _, _) = ShipExport.Build(PanelShip(cat), cat, NoSpecs, "Worn", wear: wear);

        var damaged = 0;
        foreach (var it in ship.AItems)
        {
            if (it.ACondOverrides is null) continue;
            var ov = Assert.Single(it.ACondOverrides);
            Assert.Equal("StatDamage", ov.CondName);
            Assert.InRange(ov.Amount, 0.0, WearModel.VanillaUsedCeiling * 4.0);   // ceiling × StatDamageMax
            damaged++;
        }
        Assert.True(damaged > 0, "vanilla wear should damage at least some parts");
    }

    [Fact]
    public void Export_wear_is_deterministic_for_a_fixed_seed()
    {
        var cat = WearCat();
        var wear = new WearOptions(true, 0.6, Seed: 77);
        var a = ShipExport.Build(PanelShip(cat), cat, NoSpecs, "A", wear: wear).Ship;
        var b = ShipExport.Build(PanelShip(cat), cat, NoSpecs, "B", wear: wear).Ship;

        var da = a.AItems.Select(i => i.ACondOverrides?[0].Amount ?? 0).ToArray();
        var db = b.AItems.Select(i => i.ACondOverrides?[0].Amount ?? 0).ToArray();
        Assert.Equal(da, db);
    }

    [Fact]
    public void Heavy_wear_drops_the_baked_rating_grade_below_A()
    {
        var cat = WearCat();
        var wear = new WearOptions(true, 0.3, Seed: 5);
        var (_, rating, _) = ShipExport.Build(PanelShip(cat), cat, NoSpecs, "Grungy", wear: wear);
        Assert.NotEqual("A", rating.Condition);
    }

    // ---- save-edit path: per-CO StatDamage ----

    private static JsonObject Item(string id, string name, double fx, double fy) => new()
    { ["strID"] = id, ["strName"] = name, ["fX"] = fx, ["fY"] = fy, ["fRotation"] = 0.0 };

    private static SaveShipContext PanelContext(Catalog cat)
    {
        var items = new JsonArray(Item("a", "Panel", 100, 200));
        var cos = new JsonArray(new JsonObject
        {
            ["strID"] = "a", ["strCODef"] = "Panel", ["bAlive"] = true,
            ["aConds"] = new JsonArray("StatDamageMax=1.0x4", "IsPristine=1.0x1"),
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
            Origins = new Dictionary<string, OriginPart> { ["a"] = new OriginPart(0, 0, 0, []) },
            ItemsById = items.Select(n => n!.AsObject()).ToDictionary(o => (string)o["strID"]!, o => (JsonNode)o),
            CosById = cos.Select(n => n!.AsObject()).ToDictionary(o => (string)o["strID"]!, o => (JsonNode)o),
            Epoch = 0,
        };
    }

    private static string[] Conds(JsonObject ship, string coId) =>
        ((JsonArray)((JsonArray)ship["aCOs"]!).Select(n => n!.AsObject()).Single(o => (string)o["strID"]! == coId)["aConds"]!)
            .Select(x => (string)x!).ToArray();

    private static string NewCoId(JsonObject ship) =>
        ((JsonArray)ship["aCOs"]!).Select(n => (string)n!["strID"]!).First(id => id != "a");

    [Fact]
    public void Save_edit_without_wear_leaves_existing_damage_untouched()
    {
        var cat = new Fixtures().Part("Panel", startingConds: ["IsInstalled"],
            condValues: new Dictionary<string, double> { ["StatDamageMax"] = 4.0 }).Build();
        var doc = Fixtures.Doc(cat, new Placement { DefName = "Panel", X = 0, Y = 0, OriginStrID = "a" });

        var (ship, _) = SaveEdit.BuildInjectedShip(doc, PanelContext(cat), cat, NoSpecs, wear: WearOptions.Pristine);

        var conds = Conds(ship, "a");
        Assert.Contains("IsPristine=1.0x1", conds);                    // kept verbatim
        Assert.DoesNotContain(conds, c => c.StartsWith("StatDamage=") && !c.StartsWith("StatDamageMax="));
    }

    [Fact]
    public void Save_edit_with_wear_damages_kept_and_new_parts_and_strips_pristine()
    {
        var cat = new Fixtures().Part("Panel", startingConds: ["IsInstalled"],
            condValues: new Dictionary<string, double> { ["StatDamageMax"] = 4.0 }).Build();
        var doc = Fixtures.Doc(cat,
            new Placement { DefName = "Panel", X = 0, Y = 0, OriginStrID = "a" },   // kept
            new Placement { DefName = "Panel", X = 1, Y = 0 });                     // new

        var wear = new WearOptions(true, 0.5, Seed: 9);
        var (ship, _) = SaveEdit.BuildInjectedShip(doc, PanelContext(cat), cat, NoSpecs, wear: wear);

        foreach (var id in new[] { "a", NewCoId(ship) })
        {
            var conds = Conds(ship, id);
            var dmg = Assert.Single(conds, c => c.StartsWith("StatDamage=") && !c.StartsWith("StatDamageMax="));
            var amount = LootDef.CondAmount(dmg);
            Assert.InRange(amount, 0.0, WearModel.MaxDamageRate * 4.0);   // never past the 10% floor
            Assert.DoesNotContain("IsPristine=1.0x1", conds);            // pristine markup removed on damage
        }
        Assert.NotEqual("A", (string)((JsonArray)ship["aRating"]!)[1]!);  // baked grade reflects the wear
    }
}
