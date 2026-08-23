using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The container view's item info and rename (#37): what the panel is made of, and the custom name and faction
/// membership riding on a cargo item through the <c>.oplan</c>.
/// </summary>
public class CargoInfoTests
{
    private static Catalog Cat() => new Fixtures()
        .Part("Crate", startingConds: ["IsContainer"], container: (4, 4),
            condValues: new Dictionary<string, double> { ["StatMass"] = 12, ["StatBasePrice"] = 90 })
        .Part("Round", condValues: new Dictionary<string, double> { ["StatMass"] = 0.02 })
        .Build();

    private static ShipDocument Doc(Catalog cat) => new(cat);

    private static CargoItem Item(string def, string? name = null, params string[] factions) =>
        new(Guid.NewGuid().ToString(), def, null, false, []) { CustomName = name, Factions = factions };

    // ---- the panel ----

    [Fact]
    public void The_title_is_the_items_own_name_where_it_has_one()
    {
        var cat = Cat();
        var doc = Doc(cat);

        var named = CargoInfo.For(Item("Round", "Poison Bullet"), doc);
        Assert.Equal("Poison Bullet", named.Name);
        Assert.Equal("Round", named.StockName);   // the way back, for the rename box

        var plain = CargoInfo.For(Item("Round"), doc);
        Assert.Equal("Round", plain.Name);
        Assert.Equal("Round", plain.StockName);
    }

    [Fact]
    public void Price_is_shown_only_when_the_def_declares_one()
    {
        // The game destroys its ValueModule outright on a def with no StatBasePrice rather than printing zero.
        var cat = Cat();
        var doc = Doc(cat);

        Assert.Equal(90, CargoInfo.For(Item("Crate"), doc).Price!.Value, 3);
        Assert.Null(CargoInfo.For(Item("Round"), doc).Price);
    }

    [Fact]
    public void An_ordinary_item_carries_no_game_figures_at_all()
    {
        // The whole reason the raw list exists. Four conditions in stock data are display-type 1, and a crate
        // declares none of them, so the game's own panel shows a name, a description and a price.
        var cat = Cat();
        var info = CargoInfo.For(Item("Crate"), Doc(cat));

        Assert.Empty(info.Figures);
        Assert.NotEmpty(info.RawConds);   // ...but Ostraplan's own section still has something to say
    }

    [Fact]
    public void The_raw_section_is_the_defs_own_stats_sorted()
    {
        var cat = Cat();
        var info = CargoInfo.For(Item("Crate"), Doc(cat));

        Assert.Equal(["StatBasePrice", "StatMass"], info.RawConds.Select(f => f.Label).ToArray());
        Assert.Equal("12", info.RawConds.Single(f => f.Label == "StatMass").Value);
    }

    // ---- factions ----

    [Fact]
    public void Factions_resolve_through_the_documents_own_table()
    {
        // The names exist only in the save the design came from, so the document carries them. This is the Ceres
        // case: an item off a station reads as that station's.
        var cat = Cat();
        var doc = Doc(cat);
        doc.LoadFactionNames(new Dictionary<string, string>
        {
            ["CCRECiv"] = "Coalition of Ceres Resource Extraction",
        });

        var info = CargoInfo.For(Item("Round", null, "CCRECiv"), doc);
        Assert.Equal(["Coalition of Ceres Resource Extraction"], info.Factions.ToArray());
    }

    [Fact]
    public void An_unknown_faction_falls_back_to_its_own_id()
    {
        // A design drawn from scratch has no table. Showing the id beats showing nothing, and it is still the
        // handle a save editor would search on.
        var cat = Cat();
        var info = CargoInfo.For(Item("Round", null, "OKLGCorp"), Doc(cat));

        Assert.Equal(["OKLGCorp"], info.Factions.ToArray());
    }

    [Fact]
    public void Most_items_belong_to_no_faction()
    {
        var cat = Cat();
        Assert.Empty(CargoInfo.For(Item("Round"), Doc(cat)).Factions);
    }

    // ---- the .oplan round trip ----

    [Fact]
    public void A_cargo_name_and_its_factions_survive_a_save_and_reopen()
    {
        var cat = Cat();
        var tmp = Path.Combine(Path.GetTempPath(), $"ostraplan-test-{Guid.NewGuid():N}.oplan");
        try
        {
            new OplanFile
            {
                Parts =
                [
                    new OplanPart
                    {
                        Def = "Crate", X = 0, Y = 0,
                        Cargo =
                        [
                            new OplanCargo
                            {
                                Def = "Round", StrID = "a1", Authored = true, Stack = 1,
                                Name = "Poison Bullet", Factions = ["CCRECiv"],
                            },
                        ],
                    },
                ],
                Factions = new Dictionary<string, string> { ["CCRECiv"] = "Coalition of Ceres Resource Extraction" },
            }.Save(tmp);

            var (doc, missing) = OplanFile.Load(tmp).ToDocument(cat);

            Assert.Empty(missing);
            var round = doc.Placements[0].Cargo.Single();
            Assert.Equal("Poison Bullet", round.CustomName);
            Assert.Equal(["CCRECiv"], round.Factions.ToArray());
            Assert.Equal("Coalition of Ceres Resource Extraction", doc.FactionName("CCRECiv"));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void An_unnamed_unaffiliated_design_writes_neither_field()
    {
        // Both have to stay absent for the overwhelming majority of cargo, or every .oplan grows two lines per
        // item for something almost no design uses.
        var tmp = Path.Combine(Path.GetTempPath(), $"ostraplan-test-{Guid.NewGuid():N}.oplan");
        try
        {
            new OplanFile
            {
                Parts =
                [
                    new OplanPart
                    {
                        Def = "Crate", X = 0, Y = 0,
                        Cargo = [new OplanCargo { Def = "Round", StrID = "a1", Authored = true, Stack = 1 }],
                    },
                ],
            }.Save(tmp);

            // Read back rather than grepped: "name" also appears on the document's own meta block, so the text
            // test would pass for the wrong reason and fail for another.
            var reloaded = OplanFile.Load(tmp);
            var cargo = reloaded.Parts.Single().Cargo!.Single();
            Assert.Null(cargo.Name);
            Assert.Null(cargo.Factions);
            Assert.Null(reloaded.Factions);
            // The extension-data bag is where an unknown field would land, so an empty one proves neither was
            // written under some other spelling.
            Assert.True(cargo.Extra is null or { Count: 0 });
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Only_the_factions_the_cargo_references_are_written()
    {
        // A save carries a few hundred factions, nearly all of them the per-person ones the game mints as it
        // goes. Writing those into every design would bloat the file with names nothing in it names.
        var cat = Cat();
        var doc = Doc(cat);
        doc.LoadFactionNames(new Dictionary<string, string>
        {
            ["CCRECiv"] = "Coalition of Ceres Resource Extraction",
            ["SomePerson"] = "Chinasa Swanson",
        });
        var crate = new Placement { DefName = "Crate", X = 0, Y = 0 };
        crate.Cargo = [Item("Round", null, "CCRECiv")];
        doc.Add(crate);

        var g = TestData.Game;
        Skip.If(g is null, "needs the game for the mod manifest");
        var file = OplanFile.FromDocument(doc, g!.Value.Index, new OplanMeta());

        Assert.NotNull(file.Factions);
        Assert.True(file.Factions!.ContainsKey("CCRECiv"));
        Assert.False(file.Factions.ContainsKey("SomePerson"));
    }

    // ---- the game's own data ----

    [SkippableFact]
    public void Exactly_the_display_type_one_conditions_are_offered()
    {
        var g = TestData.RequireGame();

        // A drift alarm rather than a law: if a patch marks more conditions display-type 1 the panel grows with
        // it, and that is worth a failing test rather than a quietly different tool tip.
        Assert.NotEmpty(g.Catalog.CondDisplay);
        Assert.Contains("StatGasTemp", g.Catalog.CondDisplay.Keys);
        Assert.True(g.Catalog.CondDisplay.Count < 20,
            $"{g.Catalog.CondDisplay.Count} display-type-1 conditions; the panel was built for a handful");

        // And the format is the game's: three decimals with the unit appended.
        var temp = g.Catalog.CondDisplay["StatGasTemp"];
        Assert.Equal("293.000K", temp.Format(293));
    }
}
