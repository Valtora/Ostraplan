using System.Collections.Generic;
using System.Linq;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// A painted condition reaching the game. The brush is only worth anything if the figure survives the export, and
/// the rule that makes it awkward is that an authored value has to travel whether or not the whole-ship wear pass
/// is armed — which is the opposite of how the wear pass itself behaves.
/// </summary>
public class PaintedConditionExportTests
{
    private static readonly IReadOnlyList<RoomSpecDef> NoSpecs = [];

    private static Catalog Cat() => new Fixtures()
        .Part("Wall", startingConds: ["IsInstalled", "IsWall"],
            condValues: new Dictionary<string, double> { ["StatDamageMax"] = 40 })
        .Part("Sys", startingConds: ["IsInstalled", "IsSystem"],
            condValues: new Dictionary<string, double> { ["StatDamageMax"] = 40 })
        .Part("Crate", startingConds: [],
            condValues: new Dictionary<string, double> { ["StatDamageMax"] = 10 })
        .Build();

    private static double? StatDamageOf(ExportedItem item) =>
        item.ACondOverrides?.FirstOrDefault(o => o.CondName == "StatDamage")?.Amount;

    [Fact]
    public void A_painted_part_exports_its_damage_even_with_the_wear_pass_off()
    {
        // The case the whole feature turns on: a deliberately battered station exported pristine still has to
        // arrive battered, because the designer said so part by part rather than asking for a roll.
        var cat = Cat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Wall", 0, 0));
        doc.Placements[0].Condition = 0.25;

        var (ship, _, _) = ShipExport.Build(doc, cat, NoSpecs, "Battered", wear: WearOptions.Pristine);

        // 75% of a 40-point pool.
        Assert.Equal(30.0, StatDamageOf(Assert.Single(ship.AItems))!.Value, 3);
    }

    [Fact]
    public void An_unpainted_part_still_exports_clean_when_the_wear_pass_is_off()
    {
        var cat = Cat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Wall", 0, 0));

        var (ship, _, _) = ShipExport.Build(doc, cat, NoSpecs, "Clean", wear: WearOptions.Pristine);

        Assert.Null(StatDamageOf(Assert.Single(ship.AItems)));
    }

    [Fact]
    public void A_painted_part_beats_the_roll_and_an_unpainted_one_takes_it()
    {
        var cat = Cat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Wall", 0, 0), Fixtures.P("Wall", 2, 0));
        doc.Placements[0].Condition = 0.10;

        var (ship, _, _) = ShipExport.Build(doc, cat, NoSpecs, "Mixed",
            wear: new WearOptions(true, 0.9, Seed: 3));

        var items = ship.AItems.ToList();
        Assert.Equal(36.0, StatDamageOf(items[0])!.Value, 3);   // 90% of 40, exactly as painted

        // The unpainted one took the roll instead, which at a 0.9 target is a small figure but not the painted one.
        var rolled = StatDamageOf(items[1]);
        if (rolled is { } r) Assert.NotEqual(36.0, r, 3);
    }

    [Fact]
    public void A_painted_part_survives_a_repair_pass()
    {
        // "Repair everything" is a statement about the parts the designer did not speak for. One that was painted
        // deliberately is not swept up by it.
        var cat = Cat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Wall", 0, 0));
        doc.Placements[0].Condition = 0.5;

        var (ship, _, _) = ShipExport.Build(doc, cat, NoSpecs, "Repaired", wear: WearOptions.Repaired);

        Assert.Equal(20.0, StatDamageOf(Assert.Single(ship.AItems))!.Value, 3);
    }

    [Fact]
    public void A_system_part_is_left_alone_however_it_was_painted()
    {
        // The game never damages an IsSystem part, so writing a StatDamage onto one would claim a condition the
        // game will not honour. Paint.CanWear refuses it in the editor; the export refuses it too.
        var cat = Cat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Sys", 0, 0));
        doc.Placements[0].Condition = 0.2;

        var (ship, _, _) = ShipExport.Build(doc, cat, NoSpecs, "Systems", wear: WearOptions.Pristine);

        Assert.Null(StatDamageOf(Assert.Single(ship.AItems)));
    }

    [Fact]
    public void A_painted_deck_item_exports_on_its_stack_head()
    {
        // A pile is worn as a pile, which is where the game keeps it and how a rename already lands.
        var cat = Cat();
        var doc = new ShipDocument(cat);
        doc.Add(Fixtures.P("Wall", 0, 0));
        doc.AddLoose(new LooseObject { DefName = "Crate", X = 1, Y = 0, Quantity = 1, Condition = 0.3 });

        var (ship, _, _) = ShipExport.Build(doc, cat, NoSpecs, "Cluttered", wear: WearOptions.Pristine);

        var crate = ship.AItems.Single(i => i.StrName == "Crate");
        Assert.Equal(7.0, StatDamageOf(crate)!.Value, 3);   // 70% of a 10-point pool
    }

    [Fact]
    public void An_unpainted_deck_item_carries_no_override()
    {
        var cat = Cat();
        var doc = new ShipDocument(cat);
        doc.Add(Fixtures.P("Wall", 0, 0));
        doc.AddLoose(new LooseObject { DefName = "Crate", X = 1, Y = 0, Quantity = 1 });

        var (ship, _, _) = ShipExport.Build(doc, cat, NoSpecs, "Tidy", wear: WearOptions.Pristine);

        Assert.Null(StatDamageOf(ship.AItems.Single(i => i.StrName == "Crate")));
    }
}
