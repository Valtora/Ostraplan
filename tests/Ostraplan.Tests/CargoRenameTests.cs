using System;
using System.Collections.Generic;
using System.Linq;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// Renaming an item inside a container (#37): the tree edit, and the name reaching the game through an export.
/// The game renames anything that is not a person, a round in a locker included, which is why this is not gated on
/// the item having a container of its own.
/// </summary>
public class CargoRenameTests
{
    private static readonly IReadOnlyList<RoomSpecDef> NoSpecs = [];

    private static Catalog Cat() => new Fixtures()
        .Part("Crate", startingConds: ["IsInstalled", "IsContainer"], container: (4, 4))
        .Part("Pouch", startingConds: ["IsContainer"], container: (2, 2))
        .Part("Round")
        .Build();

    private static CargoItem Item(string id, string def, params CargoItem[] children) =>
        new(id, def, def, false, children) { Authored = true, Stack = 1 };

    // ---- the tree edit ----

    [Fact]
    public void Renaming_reaches_an_item_nested_several_containers_deep()
    {
        // The caller has an id, not a path, so the walk has to find it wherever it is.
        var tree = new[] { Item("crate", "Crate", Item("pouch", "Pouch", Item("round", "Round"))) };

        var next = CargoEdit.Rename(tree, "round", "Poison Bullet");

        Assert.NotNull(next);
        Assert.Equal("Poison Bullet", next!.Single().Children.Single().Children.Single().CustomName);
    }

    [Fact]
    public void Renaming_leaves_everything_else_alone()
    {
        var tree = new[] { Item("crate", "Crate", Item("a", "Round"), Item("b", "Round")) };

        var next = CargoEdit.Rename(tree, "a", "Spent")!;

        Assert.Equal("Spent", next.Single().Children[0].CustomName);
        Assert.Null(next.Single().Children[1].CustomName);
        Assert.Null(next.Single().CustomName);
    }

    [Fact]
    public void Clearing_a_name_puts_the_item_back_to_its_defs()
    {
        var tree = new[] { Item("crate", "Crate", Item("a", "Round") with { CustomName = "Spent" }) };

        var next = CargoEdit.Rename(tree, "a", null);

        Assert.NotNull(next);
        Assert.Null(next!.Single().Children.Single().CustomName);
    }

    [Fact]
    public void A_no_op_rename_reports_nothing_changed()
    {
        // So the caller can skip pushing an undo step for a rename that said nothing new.
        var tree = new[] { Item("crate", "Crate", Item("a", "Round") with { CustomName = "Spent" }) };

        Assert.Null(CargoEdit.Rename(tree, "a", "Spent"));
        Assert.Null(CargoEdit.Rename(tree, "nobody", "Spent"));
    }

    [Fact]
    public void A_typed_name_is_cleaned_the_way_every_other_rename_is()
    {
        // Trimmed, capped, and an all-whitespace box means "no name" rather than a name of spaces.
        var tree = new[] { Item("crate", "Crate", Item("a", "Round")) };

        Assert.Equal("Spent", CargoEdit.Rename(tree, "a", "  Spent  ")!.Single().Children.Single().CustomName);
        Assert.Null(CargoEdit.Rename(tree, "a", "   "));
    }

    // ---- reaching the game ----

    [Fact]
    public void A_named_item_exports_its_rename_panel()
    {
        var cat = Cat();
        var doc = new ShipDocument(cat);
        var crate = new Placement { DefName = "Crate", X = 0, Y = 0 };
        crate.Cargo = [Item("a", "Round") with { CustomName = "Poison Bullet" }];
        doc.Add(crate);

        var (ship, _, _) = ShipExport.Build(doc, cat, NoSpecs, "Armoury");

        var round = ship.AItems.Single(i => i.StrName == "Round");
        var panel = Assert.Single(round.AGPMSettings!, s => s.StrName == Rename.Panel);
        Assert.Equal(new object?[] { Rename.NameKey, "Poison Bullet" }, panel.DictGUIPropMap);
    }

    [Fact]
    public void An_unnamed_item_exports_no_panel_at_all()
    {
        var cat = Cat();
        var doc = new ShipDocument(cat);
        var crate = new Placement { DefName = "Crate", X = 0, Y = 0 };
        crate.Cargo = [Item("a", "Round")];
        doc.Add(crate);

        var (ship, _, _) = ShipExport.Build(doc, cat, NoSpecs, "Armoury");

        Assert.Null(ship.AItems.Single(i => i.StrName == "Round").AGPMSettings);
    }

    [Fact]
    public void A_nested_container_carries_its_own_name_and_so_does_what_is_in_it()
    {
        // The organising case the issue gives: a crate labelled for its purpose, holding a pouch labelled for
        // its own, holding a round labelled for itself.
        var cat = Cat();
        var doc = new ShipDocument(cat);
        var crate = new Placement { DefName = "Crate", X = 0, Y = 0, CustomName = "Electrical" };
        crate.Cargo =
        [
            Item("pouch", "Pouch", Item("round", "Round") with { CustomName = "Poison Bullet" })
                with { CustomName = "Fuses" },
        ];
        doc.Add(crate);

        var (ship, _, _) = ShipExport.Build(doc, cat, NoSpecs, "Stores");

        string NameOf(string def) =>
            (string)ship.AItems.Single(i => i.StrName == def)
                .AGPMSettings!.Single(s => s.StrName == Rename.Panel).DictGUIPropMap![1]!;

        Assert.Equal("Electrical", NameOf("Crate"));
        Assert.Equal("Fuses", NameOf("Pouch"));
        Assert.Equal("Poison Bullet", NameOf("Round"));
    }
}
