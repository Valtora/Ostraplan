using System.IO;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

public class EngineTests
{
    private static Catalog Fake(PartDef[]? parts = null, LootDef[]? loots = null, CondTriggerDef[]? trigs = null) => new()
    {
        Parts = parts ?? [],
        ByDefName = (parts ?? []).ToDictionary(p => p.DefName),
        Loots = (loots ?? []).ToDictionary(l => l.Name),
        Triggers = (trigs ?? []).ToDictionary(t => t.Name),
        Warnings = [],
    };

    private static PartDef Part(string name, int w, int h, string loot = "L") => new(
        name, name, "HULL", "core",
        new ItemDef(name, "", false, null, 0, w, [.. Enumerable.Repeat(loot, w * h)], [], []),
        null, [], [], [], new Dictionary<string, double>(), new Dictionary<string, (double, double)>());

    [Fact]
    public void Rotate_cw_once_maps_cells_correctly()
    {
        // [a b c]      [d a]
        // [d e f]  ->  [e b]
        //              [f c]
        var (w, h, cells) = GridMath.Rotate(["a", "b", "c", "d", "e", "f"], 3, 2, 90);
        Assert.Equal((2, 3), (w, h));
        Assert.Equal(["d", "a", "e", "b", "f", "c"], cells);
    }

    [Fact]
    public void Rotate_full_circle_is_identity()
    {
        string[] cells = ["a", "b", "c", "d", "e", "f"];
        var (w, h, rotated) = GridMath.Rotate(cells, 3, 2, 360);
        Assert.Equal((3, 2), (w, h));
        Assert.Equal(cells, rotated);
    }

    [Fact]
    public void Autotile_table_is_a_bijection_on_the_4x4_sheet()
    {
        var cells = Enumerable.Range(0, 16).Select(m => Autotile.Cell(m, 4, 4)).ToHashSet();
        Assert.Equal(16, cells.Count);
    }

    [Fact]
    public void Autotile_known_cells_match_the_game_tables()
    {
        // values traced through Item.SpriteSheetIndices + GetMaterialSheet's
        // bottom-up UV rows, expressed top-left as WPF crops
        Assert.Equal((1, 3), Autotile.Cell(Autotile.Mask(false, false, false, false), 4, 4));   // isolated
        Assert.Equal((1, 1), Autotile.Cell(Autotile.Mask(true, true, true, true), 4, 4));       // cross
        Assert.Equal((0, 3), Autotile.Cell(Autotile.Mask(false, true, true, false), 4, 4));     // horizontal
        Assert.Equal((2, 3), Autotile.Cell(Autotile.Mask(true, false, false, true), 4, 4));     // vertical
        Assert.Equal((0, 0), Autotile.Cell(Autotile.Mask(false, false, true, true), 4, 4));     // E+S corner
    }

    [Fact]
    public void TileConds_accumulate_and_reverse()
    {
        var cat = Fake([Part("X", 2, 2)], [new LootDef("L", ["IsX"], [])], [new CondTriggerDef("TX", ["IsX"], [], false)]);
        var doc = new ShipDocument(cat);
        var place = new PlaceCommand(new Placement { DefName = "X", X = 0, Y = 0, Rot = 0 });

        place.Do(doc);
        Assert.True(doc.Conds.TriggeredByName("TX", 1, 1));
        Assert.False(doc.Conds.TriggeredByName("TX", 2, 0));

        place.Undo(doc);
        Assert.False(doc.Conds.TriggeredByName("TX", 1, 1));
        Assert.Null(doc.Conds.At(0, 0));
    }

    [Fact]
    public void Draw_order_layers_floor_under_wall_under_fixture_under_conduit()
    {
        var cat = Fake(
            [Part("F", 1, 1, "FloorLoot"), Part("W", 1, 1, "WallLoot"),
             Part("X", 1, 1, "FixLoot"), Part("C", 1, 1, "CondLoot")],
            [new LootDef("FloorLoot", ["IsFloorSealed"], []), new LootDef("WallLoot", ["IsWall"], []),
             new LootDef("FixLoot", ["IsFixture"], []), new LootDef("CondLoot", ["IsPowerConduit"], [])]);
        var doc = new ShipDocument(cat);
        // scrambled insertion order: the render layer must re-sort them regardless
        foreach (var def in new[] { "X", "C", "F", "W" })
            new PlaceCommand(new Placement { DefName = def, X = 0, Y = 0 }).Do(doc);

        Assert.Equal(["F", "W", "X", "C"], doc.DrawOrder().Select(p => p.DefName));
        Assert.Equal("C", doc.HitTest(0, 0)!.DefName);   // topmost layer wins the hit-test
    }

    [Fact]
    public void HitTestStack_lists_covering_parts_topmost_first()
    {
        var cat = Fake(
            [Part("F", 1, 1, "FloorLoot"), Part("X", 1, 1, "FixLoot"), Part("C", 1, 1, "CondLoot")],
            [new LootDef("FloorLoot", ["IsFloorSealed"], []), new LootDef("FixLoot", ["IsFixture"], []),
             new LootDef("CondLoot", ["IsPowerConduit"], [])]);
        var doc = new ShipDocument(cat);
        foreach (var def in new[] { "F", "X", "C" })
            new PlaceCommand(new Placement { DefName = def, X = 0, Y = 0 }).Do(doc);

        Assert.Equal(["C", "X", "F"], doc.HitTestStack(0, 0).Select(p => p.DefName));   // conduit, fixture, floor
        Assert.Empty(doc.HitTestStack(9, 9));
    }

    [Fact]
    public void Under_floor_loot_is_subtile_without_a_solid_body()
    {
        var cat = Fake(loots:
        [
            new LootDef("Deck", ["IsFixture", "IsSubTile", "IsObstruction"], []),   // TIL2DeckAdds: the visible tank body
            new LootDef("Subfloor", ["IsSubTile"], []),                             // TILSubfloorAdds: under-floor storage
            new LootDef("Wall", ["IsWall", "IsObstruction"], []),
        ]);
        Assert.True(cat.IsUnderFloorLoot("Subfloor"));    // sub-floor, no obstruction -> under-floor reservation
        Assert.False(cat.IsUnderFloorLoot("Deck"));       // has an obstruction -> above-floor body
        Assert.False(cat.IsUnderFloorLoot("Wall"));
        Assert.False(cat.IsUnderFloorLoot("Blank"));
        Assert.False(cat.IsUnderFloorLoot(null));
    }

    [Fact]
    public void Trigger_forbids_block()
    {
        var cat = Fake([Part("X", 1, 1)],
            [new LootDef("L", ["IsX"], [])],
            [new CondTriggerDef("TNoX", [], ["IsX"], false)]);
        var doc = new ShipDocument(cat);
        Assert.True(doc.Conds.TriggeredByName("TNoX", 0, 0));
        new PlaceCommand(new Placement { DefName = "X", X = 0, Y = 0 }).Do(doc);
        Assert.False(doc.Conds.TriggeredByName("TNoX", 0, 0));
    }

    [Fact]
    public void Command_stack_undo_redo_and_dirty_tracking()
    {
        var cat = Fake([Part("X", 1, 1)], [new LootDef("L", ["IsX"], [])]);
        var doc = new ShipDocument(cat);
        var stack = new CommandStack();

        Assert.False(stack.Dirty);
        stack.Push(doc, new PlaceCommand(new Placement { DefName = "X", X = 0, Y = 0 }));
        stack.Push(doc, new PlaceCommand(new Placement { DefName = "X", X = 1, Y = 0 }));
        Assert.Equal(2, doc.Placements.Count);
        Assert.True(stack.Dirty);

        stack.Undo(doc);
        Assert.Single(doc.Placements);
        stack.Redo(doc);
        Assert.Equal(2, doc.Placements.Count);

        stack.MarkSaved();
        Assert.False(stack.Dirty);
        stack.Undo(doc);
        Assert.True(stack.Dirty);
        stack.Redo(doc);
        Assert.False(stack.Dirty);
    }

    [Fact]
    public void Rotate_command_keeps_center_and_round_trips()
    {
        var cat = Fake([Part("X", 3, 1)], [new LootDef("L", ["IsX"], [])]);
        var doc = new ShipDocument(cat);
        var stack = new CommandStack();
        var p = new Placement { DefName = "X", X = 0, Y = 0 };
        stack.Push(doc, new PlaceCommand(p));

        stack.Push(doc, new RotateCommand(doc, p, 90));
        Assert.Equal((1, -1, 90), (p.X, p.Y, p.Rot));
        Assert.Equal((1, 3), doc.FootprintOf(p));

        stack.Undo(doc);
        Assert.Equal((0, 0, 0), (p.X, p.Y, p.Rot));
    }

    [Fact]
    public void Composite_command_is_one_undo_step()
    {
        var cat = Fake([Part("X", 1, 1)], [new LootDef("L", ["IsX"], [])]);
        var doc = new ShipDocument(cat);
        var stack = new CommandStack();
        stack.Push(doc, new PlaceCommand(new Placement { DefName = "X", X = 0, Y = 0 }));

        Placement[] clones = [new() { DefName = "X", X = 1, Y = 1 }, new() { DefName = "X", X = 2, Y = 2 }];
        stack.Push(doc, new CompositeCommand(clones.Select(c => (IDocCommand)new PlaceCommand(c)).ToList()));
        Assert.Equal(3, doc.Placements.Count);

        stack.Undo(doc);   // the whole duplicate batch reverts as one step
        Assert.Single(doc.Placements);
        stack.Redo(doc);
        Assert.Equal(3, doc.Placements.Count);
    }

    [Fact]
    public void Dismissed_alerts_persist_through_oplan_load_and_mutate_correctly()
    {
        var cat = Fake([Part("X", 1, 1)], [new LootDef("L", ["IsX"], [])]);

        // load side: an .oplan carrying dismissed keys restores them onto the document
        var (doc, _) = new OplanFile { DismissedAlerts = ["unsealed-compartments"] }.ToDocument(cat);
        Assert.True(doc.IsAlertDismissed("unsealed-compartments"));
        Assert.Single(doc.DismissedAlerts);

        // mutators: add dedups, restore clears
        Assert.True(doc.DismissAlert("other"));
        Assert.False(doc.DismissAlert("other"));
        Assert.Equal(2, doc.DismissedAlerts.Count);
        Assert.True(doc.RestoreAlerts());
        Assert.Empty(doc.DismissedAlerts);
        Assert.False(doc.RestoreAlerts());
    }

    [Fact]
    public void Oplan_persists_ship_identity_metadata()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"ostraplan-test-{Guid.NewGuid():N}.oplan");
        try
        {
            var file = new OplanFile
            {
                Meta = new OplanMeta
                {
                    Name = "Vagabond+", PublicName = "The Wanderer", Make = "Okuda",
                    Model = "VG-2", Year = "2231", Designation = "Salvage Tug", Description = "A trusty hauler.",
                },
            };
            file.Save(tmp);

            var loaded = OplanFile.Load(tmp);
            Assert.Equal("The Wanderer", loaded.Meta.PublicName);
            Assert.Equal("Okuda", loaded.Meta.Make);
            Assert.Equal("VG-2", loaded.Meta.Model);
            Assert.Equal("2231", loaded.Meta.Year);
            Assert.Equal("Salvage Tug", loaded.Meta.Designation);
            Assert.Equal("A trusty hauler.", loaded.Meta.Description);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Oplan_persists_view_orientation()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"ostraplan-test-{Guid.NewGuid():N}.oplan");
        try
        {
            new OplanFile { ViewRot = 270 }.Save(tmp);
            Assert.Equal(270, OplanFile.Load(tmp).ViewRot);
            Assert.Equal(0, new OplanFile().ViewRot);   // default is north-up
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Oplan_round_trip_preserves_unknown_fields_and_reports_missing_defs()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"ostraplan-test-{Guid.NewGuid():N}.oplan");
        var tmp2 = tmp + ".resaved";
        try
        {
            File.WriteAllText(tmp, """
            {
              "formatVersion": 1,
              "futureField": { "keep": true },
              "meta": { "name": "T", "futureMeta": 42 },
              "parts": [
                { "def": "X", "x": 1, "y": 2, "rot": 90, "futurePart": 3 },
                { "def": "NotInCatalog", "x": 0, "y": 0, "rot": 0 }
              ]
            }
            """);

            var file = OplanFile.Load(tmp);
            var cat = Fake([Part("X", 1, 1)], [new LootDef("L", ["IsX"], [])]);
            var (doc, missing) = file.ToDocument(cat);

            Assert.Single(doc.Placements);
            Assert.Equal((1, 2, 90), (doc.Placements[0].X, doc.Placements[0].Y, doc.Placements[0].Rot));
            Assert.Single(missing);
            Assert.Equal("NotInCatalog", missing[0].Def);

            file.Save(tmp2);
            var text = File.ReadAllText(tmp2);
            Assert.Contains("futureField", text);
            Assert.Contains("futureMeta", text);
            Assert.Contains("futurePart", text);
        }
        finally
        {
            File.Delete(tmp);
            File.Delete(tmp2);
        }
    }

    [Fact]
    public void Oplan_refuses_newer_format_versions()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"ostraplan-test-{Guid.NewGuid():N}.oplan");
        try
        {
            File.WriteAllText(tmp, """{ "formatVersion": 99, "parts": [] }""");
            Assert.Throws<InvalidDataException>(() => OplanFile.Load(tmp));
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}
