using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// How much of what a canister or tank holds: the capacity model, the shared gas budget, and the three places a
/// fill has to reach — the analysis grid, the mod export and a save write-back.
///
/// <para>The capacity arithmetic is a port, so the game-gated half checks it against the game's own numbers:
/// the shipped fill of a canister should be exactly what the ideal gas law says its shell holds.</para>
/// </summary>
public class ContainerFillTests
{
    private static readonly IReadOnlyList<RoomSpecDef> NoSpecs = [];

    private const string O2 = "StatGasMolO2";
    private const string N2 = "StatGasMolN2";

    /// <summary>The game's ordinary canister shell: 0.787 m³ at 41,400 kPa and 293 K.</summary>
    private static Catalog TankCat() => new Fixtures()
        .Tank("Can", mols: 13373)
        .Tank("Torch", gas: "N2", mols: 0.0001, volume: 40.4, pressureMax: 500, temp: 4,
              bulk: new Dictionary<string, double> { ["StatLiqD2O"] = 44722.8 })
        .Part("Rock", startingConds: ["IsInstalled"])
        .Build();

    // ---- the capacity model (game-free) ----

    [Fact]
    public void Capacity_is_the_ideal_gas_law_at_the_defs_own_temperature()
    {
        var cat = TankCat();
        var spec = ContainerFill.Describe(cat.Lookup("Can"), cat)!;

        // n = PV/RT — 41,400 kPa × 0.787 m³ / (0.008314 × 293 K)
        Assert.Equal(41400 * 0.787 / (Atmosphere.GasConstant * 293), spec.MaxMols, 6);
        // and the shipped 13,373 mol is that capacity to within a rounding, which is the whole point: the game's
        // own "full" IS the pressure rating (the shell computes 13,375.11 and the def carries 13,373)
        Assert.Equal(13373, spec.MaxMols, 2.2);
        // the round trip back to pressure is the game's Run(): n·R·T/V
        Assert.Equal(41400, spec.PressureFor(spec.MaxMols), 6);
    }

    [Fact]
    public void A_cold_container_holds_far_more_moles_at_the_same_pressure()
    {
        var cat = new Fixtures()
            .Tank("Warm", volume: 10, pressureMax: 500, temp: 293)
            .Tank("Cold", volume: 10, pressureMax: 500, temp: 4)
            .Build();
        var warm = ContainerFill.Describe(cat.Lookup("Warm"), cat)!;
        var cold = ContainerFill.Describe(cat.Lookup("Cold"), cat)!;

        // temperature divides into the capacity, which is why the cryogenic shells hold numbers that look absurd
        // next to an RTA's, and why a single hardcoded capacity would have been wrong
        Assert.True(cold.MaxMols > warm.MaxMols * 70);
        Assert.Equal(500 * 10 / (Atmosphere.GasConstant * 4), cold.MaxMols, 6);
    }

    [Fact]
    public void A_part_that_holds_nothing_has_no_fill_to_describe()
    {
        var cat = TankCat();
        Assert.Null(ContainerFill.Describe(cat.Lookup("Rock"), cat));
        Assert.Null(ContainerFill.Describe(null, cat));
    }

    [Fact]
    public void Bulk_payloads_are_their_own_lines_capped_at_what_the_def_ships()
    {
        var cat = TankCat();
        var spec = ContainerFill.Describe(cat.Lookup("Torch"), cat)!;

        var d2o = Assert.Single(spec.BulkLines);
        Assert.Equal("StatLiqD2O", d2o.Cond);
        Assert.Equal(44722.8, d2o.Stock);
        Assert.Equal(44722.8, d2o.Max);       // the def's own load is the only capacity the game publishes
        Assert.False(d2o.IsGas);
    }

    [Fact]
    public void A_fuel_tank_is_offered_no_gas_at_all()
    {
        var cat = TankCat();
        var spec = ContainerFill.Describe(cat.Lookup("Torch"), cat)!;

        // it is built around one reactant, which the reactor matches by exact condowner name — anything else in
        // it is weight the drive cannot use, so the eight-gas menu has no business here
        Assert.Empty(spec.GasLines);
        Assert.False(spec.HasGas);
        Assert.Equal(0, spec.MaxMols);        // and it reports no capacity nothing can be put into
        Assert.All(spec.Lines, l => Assert.False(l.IsGas));

        // it also cannot be talked into holding gas the long way round
        Assert.Empty(ContainerFill.Clamp(new Dictionary<string, double> { [O2] = 5000 }, spec));
    }

    [Fact]
    public void A_fuel_tank_with_an_empty_payload_is_still_a_fuel_tank()
    {
        // Ship's Water's waste tanks declare StatLiqH2OWaste at 0: nothing to fill, no capacity published, and
        // the test for "is a fuel tank" is the declaration rather than the amount — so it is not handed the gases
        var cat = new Fixtures()
            .Tank("Waste", bulk: new Dictionary<string, double> { ["StatLiqH2OWaste"] = 0 })
            .Build();

        Assert.Null(ContainerFill.Describe(cat.Lookup("Waste"), cat));
    }

    [Fact]
    public void A_temperature_is_not_a_payload()
    {
        var cat = new Fixtures()
            .Tank("Cryo", bulk: new Dictionary<string, double> { ["StatSolidHe3"] = 5216, ["StatSolidTemp"] = 4 })
            .Build();
        var spec = ContainerFill.Describe(cat.Lookup("Cryo"), cat)!;

        // StatSolidTemp starts with the bulk prefix but is degrees, not cargo
        Assert.Equal(["StatSolidHe3"], spec.BulkLines.Select(l => l.Cond));
    }

    // ---- the shared budget (game-free) ----

    [Fact]
    public void Clamp_holds_the_gas_lines_to_one_shared_budget()
    {
        var cat = TankCat();
        var spec = ContainerFill.Describe(cat.Lookup("Can"), cat)!;

        // ask for a full tank of each of two gases: twice what the shell can take
        var asked = new Dictionary<string, double> { [O2] = spec.MaxMols, [N2] = spec.MaxMols };
        var got = ContainerFill.Clamp(asked, spec);

        Assert.Equal(spec.MaxMols, ContainerFill.TotalMols(got), 6);
        Assert.Equal(got[O2], got[N2], 6);                 // scaled down together, keeping the mix
        Assert.Equal(spec.PressureMaxKPa, spec.PressureFor(ContainerFill.TotalMols(got)), 6);
    }

    [Fact]
    public void Clamp_caps_a_bulk_line_on_its_own_and_drops_the_unknown()
    {
        var cat = TankCat();
        var spec = ContainerFill.Describe(cat.Lookup("Torch"), cat)!;

        var got = ContainerFill.Clamp(new Dictionary<string, double>
        {
            ["StatLiqD2O"] = 99_999,       // over its own ceiling
            ["StatLiqNonsense"] = 500,     // not a line this tank has
            [O2] = -12,                    // negative
        }, spec);

        Assert.Equal(44722.8, got["StatLiqD2O"]);
        Assert.False(got.ContainsKey("StatLiqNonsense"));
        Assert.False(got.ContainsKey(O2));
    }

    [Fact]
    public void Total_moles_ignores_the_games_own_running_sum()
    {
        var fill = new Dictionary<string, double> { [O2] = 100, [N2] = 50, ["StatGasMolTotal"] = 9999 };
        Assert.Equal(150, ContainerFill.TotalMols(fill));
    }

    [Fact]
    public void Mass_and_value_come_from_the_games_own_tables()
    {
        var fill = new Dictionary<string, double> { [O2] = 1000, ["StatLiqD2O"] = 10 };

        // gas mass is mols × molar mass; the bulk payload is already kilograms and is not gas
        Assert.Equal(1000 * ShipValue.MolarMass("O2"), ContainerFill.GasMassKg(fill), 9);

        // a synthetic catalog has no GasPrices table, so nothing is priced — the point is that it reads the
        // table rather than a constant, so a mod retuning prices is followed
        Assert.Equal(0, ContainerFill.Value(fill, new Fixtures().Build()));
    }

    [Fact]
    public void Offerable_falls_back_to_the_codes_own_list_when_there_is_no_conditions_data()
    {
        // a synthetic catalog declares no conditions at all; refusing every gas there would leave an empty
        // editor, so the fallback is what the game's code can handle
        Assert.Equal(ContainerFill.KnownGases.Length, ContainerFill.Offerable(new Fixtures().Build()).Count);
    }

    // ---- the analysis grid (game-free) ----

    [Fact]
    public void Emptying_a_tank_changes_what_every_analysis_sees()
    {
        var cat = TankCat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Can", 0, 0));
        var p = doc.Placements[0];

        Assert.Equal(13373, ShipGrid.FromDocument(doc, cat).Parts[0].Part.StartingCondValues[O2]);

        // an EMPTY map is "this holds nothing", not "use the def" — without the zero here the def's own
        // 13,373 mol would survive being emptied and the tank would still price and fly as full
        new SetFillCommand(p, null, new Dictionary<string, double>()).Do(doc);
        Assert.Equal(0, ShipGrid.FromDocument(doc, cat).Parts[0].Part.StartingCondValues[O2]);

        new SetFillCommand(p, null, new Dictionary<string, double> { [N2] = 500 }).Do(doc);
        var vals = ShipGrid.FromDocument(doc, cat).Parts[0].Part.StartingCondValues;
        Assert.Equal(0, vals[O2]);        // the species it shipped with is gone
        Assert.Equal(500, vals[N2]);      // and one it never had is there
    }

    [Fact]
    public void A_fill_survives_an_undo_and_a_form_swap()
    {
        var cat = TankCat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Can", 0, 0));
        var p = doc.Placements[0];
        var stack = new CommandStack();

        var fill = new Dictionary<string, double> { [O2] = 4000 };
        stack.Push(doc, new SetFillCommand(p, null, fill));
        Assert.Equal(4000, p.Fill![O2]);
        stack.Undo(doc);
        Assert.Null(p.Fill);
        stack.Redo(doc);
        Assert.Equal(4000, p.Fill![O2]);

        // uninstalling a tank does not empty it
        Assert.Equal(4000, p.Restate("Can", 0).Fill![O2]);
    }

    // ---- .oplan (game-free) ----

    [Fact]
    public void Oplan_round_trips_a_fill_and_keeps_empty_distinct_from_stock()
    {
        var cat = TankCat();
        var tmp = Path.Combine(Path.GetTempPath(), $"ostraplan-test-{Guid.NewGuid():N}.oplan");
        try
        {
            var file = new OplanFile
            {
                Parts =
                [
                    new OplanPart { Def = "Can", X = 0, Y = 0, Fill = new() { [O2] = 1234.5, [N2] = 10 } },
                    new OplanPart { Def = "Can", X = 2, Y = 0, Fill = [] },   // deliberately emptied
                    new OplanPart { Def = "Can", X = 4, Y = 0 },              // never touched
                ],
            };
            file.Save(tmp);
            var (back, missing) = OplanFile.Load(tmp).ToDocument(cat);

            Assert.Empty(missing);
            Assert.Equal(1234.5, back.Placements[0].Fill![O2]);
            Assert.Equal(10, back.Placements[0].Fill![N2]);
            Assert.Empty(back.Placements[1].Fill!);       // emptied, and still emptied
            Assert.Null(back.Placements[2].Fill);         // stock, and still stock
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Oplan_omits_a_fill_for_a_tank_left_at_stock()
    {
        var cat = TankCat();
        var tmp = Path.Combine(Path.GetTempPath(), $"ostraplan-test-{Guid.NewGuid():N}.oplan");
        try
        {
            var doc = Fixtures.Doc(cat, Fixtures.P("Can", 0, 0));
            new OplanFile { Parts = [new OplanPart { Def = "Can", X = 0, Y = 0 }] }.Save(tmp);
            Assert.DoesNotContain("\"fill\"", File.ReadAllText(tmp));
            _ = doc;
        }
        finally { File.Delete(tmp); }
    }

    // ---- export (game-free) ----

    [Fact]
    public void Export_writes_a_fill_as_absolute_cond_overrides_including_an_explicit_zero()
    {
        var cat = TankCat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Can", 0, 0));
        doc.Placements[0].Fill = new Dictionary<string, double> { [N2] = 500 };

        var (ship, _, _) = ShipExport.Build(doc, cat, NoSpecs, "Tanker");
        var item = Assert.Single(ship.AItems);
        var byCond = item.ACondOverrides!.ToDictionary(o => o.CondName, o => o.Amount);

        Assert.Equal(500, byCond[N2]);
        // O2 has to be written as an explicit 0: an override SETS the amount (ApplyOverrideCondsToCO →
        // SetCondAmount), and without this entry the def's own 13,373 mol would come back on spawn
        Assert.Equal(0, byCond[O2]);
        Assert.All(item.ACondOverrides!, o => Assert.False(o.NegativeValue));
    }

    [Fact]
    public void Export_leaves_a_stock_tank_alone()
    {
        var cat = TankCat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Can", 0, 0));
        var (ship, _, _) = ShipExport.Build(doc, cat, NoSpecs, "Tanker");
        Assert.Null(Assert.Single(ship.AItems).ACondOverrides);
    }

    [Fact]
    public void Export_wear_and_a_fill_do_not_erase_each_other()
    {
        // a damageable tank: wear and a fill both want to write this part's aCondOverrides, and before they
        // shared a list whichever ran second wiped the other out
        var cat = new Fixtures()
            .Part("Can", tileConds: ["IsFixture", "IsObstruction"], startingConds: ["IsAirtight", "IsInstalled"],
                  condValues: new Dictionary<string, double>
                  {
                      ["StatVolume"] = 0.787, ["StatGasPressureMax"] = 41400, ["StatGasTemp"] = 293,
                      [O2] = 13373, ["StatDamageMax"] = 4,
                  })
            .Build();

        var doc = Fixtures.Doc(cat, Fixtures.P("Can", 0, 0));
        doc.Placements[0].Fill = new Dictionary<string, double> { [O2] = 1000 };

        var (ship, _, _) = ShipExport.Build(doc, cat, NoSpecs, "Worn tanker",
            wear: new WearOptions(true, 0.5, Seed: 9));
        var byCond = Assert.Single(ship.AItems).ACondOverrides!.ToDictionary(o => o.CondName, o => o.Amount);

        Assert.Equal(1000, byCond[O2]);          // the fill survived the wear pass
        Assert.True(byCond["StatDamage"] > 0);   // and the wear survived the fill
    }

    // ---- save write-back (game-free) ----

    [Fact]
    public void SaveEdit_merges_fill_overrides_with_whatever_the_save_already_wrote()
    {
        var cat = TankCat();
        var item = new JsonObject
        {
            ["strID"] = "abc",
            ["aCondOverrides"] = new JsonArray(
                new JsonObject { ["CondName"] = "StatDamage", ["Chance"] = 1.0, ["Amount"] = 2.0 },
                new JsonObject { ["CondName"] = O2, ["Chance"] = 1.0, ["Amount"] = 99.0 }),
        };

        SaveEdit.SetFillOverrides(item, new Dictionary<string, double> { [N2] = 700 }, cat.Lookup("Can"), cat);

        var byCond = ((JsonArray)item["aCondOverrides"]!)
            .Cast<JsonObject>()
            .ToDictionary(o => (string)o["CondName"]!, o => (double)o["Amount"]!);

        Assert.Equal(2.0, byCond["StatDamage"]);   // the save's own wear is untouched
        Assert.Equal(700, byCond[N2]);
        Assert.Equal(0, byCond[O2]);               // our stale 99 replaced by the emptied line
    }

    [Fact]
    public void SaveEdit_writes_nothing_for_a_part_that_holds_nothing()
    {
        var cat = TankCat();
        var item = new JsonObject { ["strID"] = "abc" };
        SaveEdit.SetFillOverrides(item, new Dictionary<string, double> { [O2] = 5 }, cat.Lookup("Rock"), cat);
        Assert.Null(item["aCondOverrides"]);
    }

    // ---- against the game's own data ----

    [SkippableFact]
    public void Every_core_canister_ships_at_or_under_its_own_pressure_rating()
    {
        var g = TestData.RequireGame();
        var checkedAny = false;

        foreach (var def in new[] { "ItmRTAO2", "ItmRTAN2", "ItmRTACO2", "ItmCanister01", "ItmCanisterO2Small" })
        {
            if (g.Catalog.Lookup(def) is not { } part) continue;
            var spec = ContainerFill.Describe(part, g.Catalog)!;
            var stock = ContainerFill.TotalMols(spec.Stock);
            checkedAny = true;

            Assert.True(stock <= spec.MaxMols + 1e-6,
                $"{def} ships with {stock} mol against a {spec.MaxMols} mol rating");
        }
        Assert.True(checkedAny, "no core canister resolved");
    }

    [SkippableFact]
    public void A_full_O2_RTA_is_exactly_the_ideal_gas_law_on_its_own_shell()
    {
        var g = TestData.RequireGame();
        var part = g.Catalog.Lookup("ItmRTAO2");
        Skip.If(part is null, "ItmRTAO2 not in this install");

        var spec = ContainerFill.Describe(part, g.Catalog)!;
        // the def carries 13,373 mol and the shell computes 13,375.11 — the devs rounded, and a gap that small
        // is the evidence that the capacity really is PV/RT rather than a number picked by hand
        Assert.Equal(13373, spec.Stock["StatGasMolO2"]);
        Assert.Equal(13373, spec.MaxMols, 2.2);
    }

    [SkippableFact]
    public void Only_the_gas_species_the_data_declares_are_offered()
    {
        var g = TestData.RequireGame();
        var offered = ContainerFill.Offerable(g.Catalog);

        Assert.Contains("O2", offered);
        Assert.Contains("N2", offered);
        Assert.Contains("CO2", offered);
        Assert.Equal("O2", offered[0]);   // the useful one leads

        // H2, H2O and He2 are in the game's own FluidStrings list but core data declares no StatGasMol* condition
        // for them, and CondOwner.AddCondAmount gives up on an undeclared cond — so they cannot be stored at all
        foreach (var inert in new[] { "H2", "H2O", "He2" })
        {
            Skip.IfNot(g.Catalog.DeclaredConds.Count > 0, "no conditions data loaded");
            Assert.False(g.Catalog.DeclaredConds.Contains("StatGasMol" + inert),
                $"StatGasMol{inert} is declared now — the offerable set has changed, re-check ContainerFill");
            Assert.DoesNotContain(inert, offered);
        }
    }

    [SkippableFact]
    public void The_real_fuel_tanks_offer_their_own_reactant_and_nothing_else()
    {
        var g = TestData.RequireGame();
        var checkedAny = false;

        foreach (var (def, cond, amount) in new[]
                 {
                     ("ItmCanisterLH02", "StatLiqD2O", 44722.8),
                     ("ItmCanisterLHe02", "StatSolidHe3", 5216.0),
                     ("ItmCanisterLHe01", "StatLiqHe", 1304.0),
                 })
        {
            if (g.Catalog.Lookup(def) is not { } part) continue;
            var spec = ContainerFill.Describe(part, g.Catalog)!;
            checkedAny = true;

            var line = spec.BulkLines.Single(l => l.Cond == cond);
            Assert.Equal(amount, line.Stock, 1.0);
            Assert.Equal(amount, line.Max, 1.0);
            // no oxygen menu on a deuterium tank: the reactor matches its tanks by exact condowner name, so
            // anything else in one is weight the drive cannot use
            Assert.Empty(spec.GasLines);
            Assert.False(spec.HasGas);
        }
        Assert.True(checkedAny, "no core fuel tank resolved");
    }

    [SkippableFact]
    public void No_core_or_modded_def_carries_both_a_real_gas_load_and_a_bulk_payload()
    {
        var g = TestData.RequireGame();

        // The rule above only costs nothing because this holds: the three cryogenic tanks carry a 0.0001 mol
        // token of N2 so the container initialises, and nothing else pairs the two. If a patch or a mod ships a
        // tank that genuinely holds both, this fails and the rule needs revisiting rather than quietly hiding gas.
        foreach (var part in g.Catalog.Parts)
        {
            var vals = part.StartingCondValues;
            if (!vals.Keys.Any(k => k.StartsWith("StatLiq", StringComparison.Ordinal)
                                 || (k.StartsWith("StatSolid", StringComparison.Ordinal) && !k.EndsWith("Temp", StringComparison.Ordinal))))
                continue;

            var gas = vals.Where(kv => ContainerFill.IsGasMol(kv.Key)).Sum(kv => kv.Value);
            Assert.True(gas < 1.0, $"{part.DefName} carries {gas} mol of gas alongside a bulk payload");
        }
    }

    [SkippableFact]
    public void Draining_an_O2_RTA_takes_its_gas_value_off_the_ship()
    {
        var g = TestData.RequireGame();
        Skip.If(g.Catalog.Lookup("ItmRTAO2") is null, "ItmRTAO2 not in this install");

        var doc = new ShipDocument(g.Catalog);
        var p = new Placement { DefName = "ItmRTAO2", X = 0, Y = 0 };
        new PlaceCommand(p).Do(doc);

        var full = ShipValue.PartValue(ShipGrid.FromDocument(doc, g.Catalog).Parts[0].Part, g.Catalog);
        new SetFillCommand(p, null, new Dictionary<string, double>()).Do(doc);
        var empty = ShipValue.PartValue(ShipGrid.FromDocument(doc, g.Catalog).Parts[0].Part, g.Catalog);

        // a full O2 RTA carries roughly $5,600 of oxygen on a $410 shell, so the gap is most of its worth
        Assert.True(full - empty > 1000, $"draining the tank moved value by only {full - empty}");
        Assert.Equal(410, empty, 1.0);
    }
}
