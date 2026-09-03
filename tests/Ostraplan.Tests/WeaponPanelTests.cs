using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// A weapon's page of the Weapons MFD (#51): the firing group it answers to, whether it waits to be fired by
/// hand, and a cannon's target select — and the four places an authored page has to reach (the document, the
/// <c>.oplan</c>, the mod export and a save write-back).
///
/// <para>The group is a plain condition amount, so the numbering is the part most easily got wrong: the game
/// stores 0..8 and shows 1..9. The game-gated half checks that and the stock defaults against the live data, so a
/// patch that renumbers a weapon fails here rather than in someone's ship.</para>
/// </summary>
public class WeaponPanelTests
{
    private static readonly IReadOnlyList<RoomSpecDef> NoSpecs = [];

    /// <summary>One of each weapon class, at the stock arcs and groups: a PDC (85°, group 2), a mass thrower
    /// (20°, no group cond at all), a missile launcher (360°, group 1) and a plain fixture for contrast.</summary>
    private static Catalog WeaponCat() => new Fixtures()
        .Weapon("Pdc")
        .Weapon("Thrower", type: WeaponPanel.MassThrowerCond, group: null, arc: 20, range: 90000)
        .Weapon("Launcher", type: WeaponPanel.MissileLauncherCond, group: 1, arc: 360, range: 0)
        .Weapon("LauncherOff", type: WeaponPanel.MissileLauncherCond, group: 1, arc: 0, range: 0)
        .Part("Crate", startingConds: ["IsInstalled"])
        .Build();

    // ---- the numbering (game-free) ----

    /// <summary>
    /// The one arithmetic fact the whole feature rests on. <c>MFDWeaponSelect</c> and
    /// <c>MFDWeaponDetails.PopulateMFD</c> both print <c>RoundToInt(GetCondAmount(...)) + 1</c>, and
    /// <c>WeaponsSystem.ShootManual</c> matches <c>firingGroup == amount + 1</c> against the 1..9 the
    /// <c>CommandFireGroup</c> keys send. So the stored amount is one less than the number on the button.
    /// </summary>
    [Fact]
    public void Groups_are_stored_zero_based_and_shown_one_based()
    {
        Assert.Equal(1, WeaponPanel.ToDisplay(WeaponPanel.MinGroup));
        Assert.Equal(9, WeaponPanel.ToDisplay(WeaponPanel.MaxGroup));
        Assert.Equal(9, WeaponPanel.GroupCount);
        foreach (var stored in WeaponPanel.AllGroups)
            Assert.Equal(stored, WeaponPanel.FromDisplay(WeaponPanel.ToDisplay(stored)));
    }

    [Fact]
    public void A_group_outside_the_nine_is_clamped_rather_than_written_out()
    {
        var cat = WeaponCat();
        var pdc = cat.Lookup("Pdc");
        Assert.Equal(8, new WeaponSettings { Group = 40 }.ClampTo(pdc).Group);
        Assert.Equal(0, new WeaponSettings { Group = -3 }.ClampTo(pdc).Group);
        Assert.Null(new WeaponSettings { Group = null }.ClampTo(pdc).Group);
    }

    // ---- what a def says (game-free) ----

    [Fact]
    public void A_weapon_is_recognised_by_its_condition_not_by_its_name()
    {
        var cat = WeaponCat();
        Assert.True(WeaponPanel.IsWeapon(cat.Lookup("Pdc")));
        Assert.True(WeaponPanel.IsWeapon(cat.Lookup("Thrower")));
        Assert.False(WeaponPanel.IsWeapon(cat.Lookup("Crate")));
        Assert.False(WeaponPanel.IsWeapon((PartDef?)null));
    }

    [Fact]
    public void The_defs_own_group_is_what_an_untouched_weapon_shows()
    {
        var cat = WeaponCat();
        Assert.Equal(2, WeaponPanel.DefaultGroup(cat.Lookup("Pdc")));         // displayed 3
        Assert.Equal(1, WeaponPanel.DefaultGroup(cat.Lookup("Launcher")));    // displayed 2
    }

    /// <summary>
    /// Eleven of the twelve mass-thrower defs declare no <c>IsShipWeaponFiringGroup</c> at all.
    /// <c>CondOwner.GetCondAmount</c> returns 0 for a condition an owner does not carry, so the game really does
    /// read every one of them as group 1 — this is the game's answer, not a fallback of ours.
    /// </summary>
    [Fact]
    public void A_def_that_declares_no_group_reads_as_the_first_one()
    {
        var cat = WeaponCat();
        Assert.Equal(0, WeaponPanel.DefaultGroup(cat.Lookup("Thrower")));
        Assert.Equal(1, WeaponPanel.ToDisplay(WeaponPanel.DefaultGroup(cat.Lookup("Thrower"))));
    }

    [Fact]
    public void Only_a_cannon_is_offered_a_target_select()
    {
        var cat = WeaponCat();
        Assert.True(WeaponPanel.OffersTargetMode(cat.Lookup("Pdc")));
        Assert.False(WeaponPanel.OffersTargetMode(cat.Lookup("Launcher")));

        // set on a launcher anyway, it is dropped: NavModWeaponsControl and ActivateDefenseSystems only read
        // those conds down paths a cannon reaches, so it would be a fact nothing could ever act on
        var onLauncher = new WeaponSettings { TargetMode = PdcTargetMode.Ships }.ClampTo(cat.Lookup("Launcher"));
        Assert.Equal(PdcTargetMode.All, onLauncher.TargetMode);
        Assert.Equal(PdcTargetMode.Ships,
            new WeaponSettings { TargetMode = PdcTargetMode.Ships }.ClampTo(cat.Lookup("Pdc")).TargetMode);
    }

    [Fact]
    public void Each_weapon_class_is_named_from_its_own_type_cond()
    {
        var cat = WeaponCat();
        Assert.Equal(WeaponClass.PointDefence, WeaponPanel.Classify(cat.Lookup("Pdc")));
        Assert.Equal(WeaponClass.MassThrower, WeaponPanel.Classify(cat.Lookup("Thrower")));
        Assert.Equal(WeaponClass.MissileLauncher, WeaponPanel.Classify(cat.Lookup("Launcher")));
        Assert.Equal(WeaponClass.Unknown, WeaponPanel.Classify(cat.Lookup("Crate")));
    }

    // ---- where it points (game-free) ----

    /// <summary>
    /// The facing convention, derived rather than asserted. The game fires along
    /// <c>ship.fRot + rad(item.fLastRotation)</c> with angle 0 along world +Y; an export writes
    /// <c>fRotation = Norm(-Rot)</c> and negates y because the document is y-down. Running that whole chain per
    /// rotation and reading the resulting document-space direction back is what pins the mapping, so a change to
    /// either half of the coordinate transform breaks this rather than silently mislabelling a beam.
    /// </summary>
    [Theory]
    [InlineData(0, WeaponFacing.Fore)]
    [InlineData(90, WeaponFacing.Starboard)]
    [InlineData(180, WeaponFacing.Aft)]
    [InlineData(270, WeaponFacing.Port)]
    public void A_beam_points_the_way_the_export_would_aim_it(int rot, WeaponFacing expected)
    {
        // what the export writes, and what the game then makes of it
        var fRotation = GridMath.Norm(-rot);
        var radians = fRotation * Math.PI / 180.0;
        var (worldX, worldY) = (-Math.Sin(radians), Math.Cos(radians));   // 0 rad is world +Y
        var (docX, docY) = (worldX, -worldY);                             // the export's y flip, inverted

        var fromGeometry =
            Math.Abs(docY) > Math.Abs(docX) ? docY < 0 ? WeaponFacing.Fore : WeaponFacing.Aft
            : docX > 0 ? WeaponFacing.Starboard : WeaponFacing.Port;

        Assert.Equal(expected, fromGeometry);
        Assert.Equal(expected, WeaponPanel.Facing(WeaponCat().Lookup("Pdc"), rot));
    }

    /// <summary>
    /// A launcher covers the whole circle, so it has no side to be sorted under — and neither does its <b>off</b>
    /// def, which declares no arc at all while its running form declares 360. The palette installs the off state,
    /// so treating "no arc stated" as anything but "any bearing" would file a design's launchers under a heading
    /// their live counterparts do not have.
    /// </summary>
    [Fact]
    public void A_weapon_with_no_side_is_not_given_one()
    {
        var cat = WeaponCat();
        Assert.True(WeaponPanel.IsOmnidirectional(cat.Lookup("Launcher")));
        Assert.True(WeaponPanel.IsOmnidirectional(cat.Lookup("LauncherOff")));
        Assert.False(WeaponPanel.IsOmnidirectional(cat.Lookup("Pdc")));

        Assert.Equal(WeaponFacing.Any, WeaponPanel.Facing(cat.Lookup("Launcher"), 90));
        Assert.Equal(WeaponFacing.Any, WeaponPanel.Facing(cat.Lookup("LauncherOff"), 90));
    }

    // ---- what gets written (game-free) ----

    [Fact]
    public void An_untouched_weapon_writes_nothing()
    {
        var cat = WeaponCat();
        Assert.Empty(WeaponPanel.Overrides(null, cat.Lookup("Pdc")));
        Assert.Empty(WeaponPanel.Overrides(WeaponPanel.Stock(cat.Lookup("Pdc")), cat.Lookup("Pdc")));
        Assert.Empty(WeaponPanel.Overrides(new WeaponSettings { Group = 2 }, cat.Lookup("Pdc")));   // its own default
    }

    [Fact]
    public void Only_the_conds_that_moved_are_written()
    {
        var cat = WeaponCat();
        var overrides = WeaponPanel.Overrides(new WeaponSettings { Group = 5 }, cat.Lookup("Pdc"));
        var (cond, amount) = Assert.Single(overrides);
        Assert.Equal(WeaponPanel.GroupCond, cond);
        Assert.Equal(5, amount);
    }

    /// <summary>
    /// The target select is one tri-state stored as two flags. The full picture always states both — half of it is
    /// not a state — while what actually gets written is only the flag that moved off the def, since the other is
    /// already where the def puts it. A save that has the other flag set is handled where it arises, by clearing
    /// this panel's conds before restating them (see the write-back tests below).
    /// </summary>
    [Fact]
    public void A_target_select_is_two_flags_but_only_the_one_that_moved_is_written()
    {
        var cat = WeaponCat();
        var picture = WeaponPanel.CondValues(new WeaponSettings { TargetMode = PdcTargetMode.Ships }, cat.Lookup("Pdc"));
        Assert.Equal(1, picture[WeaponPanel.ShipsOnlyCond]);
        Assert.Equal(0, picture[WeaponPanel.NonShipsCond]);   // stated, and stated as off

        var written = WeaponPanel.Overrides(new WeaponSettings { TargetMode = PdcTargetMode.Ships }, cat.Lookup("Pdc"))
                                 .ToDictionary(o => o.Cond, o => o.Amount);
        var (cond, amount) = Assert.Single(written);
        Assert.Equal(WeaponPanel.ShipsOnlyCond, cond);
        Assert.Equal(1, amount);
    }

    /// <summary>Switching a cannon from one restriction to the other has to take the old flag off the save, or it
    /// would carry both at once — a state the game's own page can never produce.</summary>
    [Fact]
    public void Switching_a_target_select_takes_the_old_flag_off()
    {
        var cat = WeaponCat();
        var co = new JsonObject
        {
            ["strID"] = "abc",
            ["aConds"] = new JsonArray($"{WeaponPanel.NonShipsCond}=1.0x1", "IsInstalled=1.0x1"),
        };

        SaveEdit.SetWeaponConds(co, new WeaponSettings { TargetMode = PdcTargetMode.Ships }, cat.Lookup("Pdc"));

        var conds = ((JsonArray)co["aConds"]!).Select(n => (string)n!).ToList();
        Assert.Contains($"{WeaponPanel.ShipsOnlyCond}=1.0x1", conds);
        Assert.DoesNotContain(conds, c => c.StartsWith($"{WeaponPanel.NonShipsCond}=", StringComparison.Ordinal));
    }

    /// <summary>
    /// The deliberate departure from <see cref="DeviceSettings.Applicable"/>: a mass thrower's def declares no
    /// firing group, and it is given one anyway. <c>CondOwner.AddCondAmount</c> falls back to
    /// <c>DataHandler.GetCond</c> when the owner's own map has no such cond, so the game accepts it — and a ship's
    /// main gun that could not be assigned to a group would be a hole in the feature, not a safeguard.
    /// </summary>
    [Fact]
    public void A_mass_thrower_can_be_given_a_group_its_def_never_declared()
    {
        var cat = WeaponCat();
        var (cond, amount) = Assert.Single(WeaponPanel.Overrides(new WeaponSettings { Group = 4 }, cat.Lookup("Thrower")));
        Assert.Equal(WeaponPanel.GroupCond, cond);
        Assert.Equal(4, amount);
    }

    [Fact]
    public void A_part_that_is_not_a_weapon_is_never_written()
    {
        var cat = WeaponCat();
        Assert.Empty(WeaponPanel.Overrides(new WeaponSettings { Group = 4 }, cat.Lookup("Crate")));
    }

    // ---- what gets read back (game-free) ----

    [Fact]
    public void A_weapon_sitting_at_its_defs_own_values_reads_back_as_nothing_authored()
    {
        var cat = WeaponCat();
        Assert.Null(WeaponPanel.FromConds(new Dictionary<string, double>(), cat.Lookup("Pdc")));
        Assert.Null(WeaponPanel.FromConds(
            new Dictionary<string, double> { [WeaponPanel.GroupCond] = 2 }, cat.Lookup("Pdc")));
    }

    [Fact]
    public void A_group_the_player_set_in_game_reads_back_as_theirs()
    {
        var cat = WeaponCat();
        var read = WeaponPanel.FromConds(
            new Dictionary<string, double>
            {
                [WeaponPanel.GroupCond] = 6,
                [WeaponPanel.ManualCond] = 1,
                [WeaponPanel.NonShipsCond] = 1,
            }, cat.Lookup("Pdc"));

        Assert.NotNull(read);
        Assert.Equal(6, read.Group);
        Assert.True(read.Manual);
        Assert.Equal(PdcTargetMode.NonShips, read.TargetMode);
    }

    // ---- the document (game-free) ----

    [Fact]
    public void Setting_a_weapon_back_to_its_defs_group_stores_nothing()
    {
        var cat = WeaponCat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Pdc", 0, 0));
        var p = doc.Placements[0];

        new SetWeaponSettingsCommand(p, null, new WeaponSettings { Group = 5 }).Do(doc);
        Assert.Equal(5, p.Weapon!.Group);

        new SetWeaponSettingsCommand(p, p.Weapon, new WeaponSettings { Group = 2 }).Do(doc);
        Assert.Null(p.Weapon);   // 2 IS the def's group, so the design has nothing to say
    }

    [Fact]
    public void Undo_puts_the_previous_page_back()
    {
        var cat = WeaponCat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Pdc", 0, 0));
        var p = doc.Placements[0];

        var before = p.Weapon;
        var cmd = new SetWeaponSettingsCommand(p, before, new WeaponSettings { Group = 7, Manual = true });
        cmd.Do(doc);
        Assert.Equal(7, p.Weapon!.Group);
        cmd.Undo(doc);
        Assert.Null(p.Weapon);
    }

    /// <summary>A cannon copied into a mirrored blister arrives already grouped, and one uninstalled and put back
    /// keeps its group — the same rule the fill, the name and the painted condition follow.</summary>
    [Fact]
    public void A_copy_and_a_restate_both_carry_the_page()
    {
        var cat = WeaponCat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Pdc", 0, 0));
        var p = doc.Placements[0];
        new SetWeaponSettingsCommand(p, null, new WeaponSettings { Group = 4, Manual = true }).Do(doc);

        Assert.Equal(4, p.CopyAt(5, 5).Weapon!.Group);
        Assert.True(p.CopyAt(5, 5).Weapon!.Manual);
        Assert.Equal(4, p.Restate("Pdc", p.Rot).Weapon!.Group);
    }

    // ---- the .oplan (game-free) ----

    /// <summary>A page written to the file and read back is the page that was authored — with the group written
    /// the way a player says it, one-based, so a hand-edited file reads like the nav console.</summary>
    [Fact]
    public void A_page_round_trips_through_the_file_as_the_number_a_player_says()
    {
        var cat = WeaponCat();
        var tmp = Path.Combine(Path.GetTempPath(), $"ostraplan-test-{Guid.NewGuid():N}.oplan");
        try
        {
            new OplanFile
            {
                Parts =
                [
                    new OplanPart
                    {
                        Def = "Pdc", X = 0, Y = 0,
                        Weapon = new OplanWeapon { Group = 9, Manual = true, Target = "NonShips" },
                    },
                    new OplanPart { Def = "Pdc", X = 2, Y = 0 },   // never touched
                ],
            }.Save(tmp);

            var (back, missing) = OplanFile.Load(tmp).ToDocument(cat);

            Assert.Empty(missing);
            var w = back.Placements[0].Weapon;
            Assert.NotNull(w);
            Assert.Equal(8, w.Group);   // the file says 9, the game stores 8
            Assert.True(w.Manual);
            Assert.Equal(PdcTargetMode.NonShips, w.TargetMode);
            Assert.Null(back.Placements[1].Weapon);
        }
        finally { File.Delete(tmp); }
    }

    /// <summary>The file is hand-editable, so a group the game does not have is brought into range rather than
    /// carried into an export that would write it onto a real ship. An unreadable target select reads as All
    /// rather than silently disarming a ship's point defence.</summary>
    [Fact]
    public void A_hand_edited_page_is_normalised_on_open()
    {
        var cat = WeaponCat();
        var tmp = Path.Combine(Path.GetTempPath(), $"ostraplan-test-{Guid.NewGuid():N}.oplan");
        try
        {
            new OplanFile
            {
                Parts =
                [
                    new OplanPart { Def = "Pdc", X = 0, Y = 0, Weapon = new OplanWeapon { Group = 40 } },
                    new OplanPart { Def = "Pdc", X = 2, Y = 0, Weapon = new OplanWeapon { Target = "sideways" } },
                    // a page that only restates the def is not a page: 3 displayed IS this def's own group
                    new OplanPart { Def = "Pdc", X = 4, Y = 0, Weapon = new OplanWeapon { Group = 3 } },
                ],
            }.Save(tmp);

            var (back, _) = OplanFile.Load(tmp).ToDocument(cat);

            Assert.Equal(WeaponPanel.MaxGroup, back.Placements[0].Weapon!.Group);
            Assert.Null(back.Placements[1].Weapon);   // All is the def's own, so nothing was authored
            Assert.Null(back.Placements[2].Weapon);
        }
        finally { File.Delete(tmp); }
    }

    /// <summary>The write side needs the real data index for its mod list, so it is checked against the install:
    /// a design with a page writes one, and a design without writes no field at all.</summary>
    [SkippableFact]
    public void The_file_carries_a_page_only_when_there_is_one()
    {
        var g = TestData.RequireGame();
        var doc = new ShipDocument(g.Catalog);
        var pdc = new Placement { DefName = "ItmShipWeaponPDC01", X = 0, Y = 0 };
        new PlaceCommand(pdc).Do(doc);

        var tmp = Path.Combine(Path.GetTempPath(), $"ostraplan-test-{Guid.NewGuid():N}.oplan");
        try
        {
            OplanFile.FromDocument(doc, g.Index, new OplanMeta()).Save(tmp);
            Assert.DoesNotContain("\"weapon\"", File.ReadAllText(tmp));

            new SetWeaponSettingsCommand(pdc, null, new WeaponSettings { Group = 5 }).Do(doc);
            OplanFile.FromDocument(doc, g.Index, new OplanMeta()).Save(tmp);
            var text = File.ReadAllText(tmp);

            Assert.Contains("\"weapon\"", text);
            Assert.Contains("\"group\": 6", text);   // stored 5, and a player calls it 6
        }
        finally { File.Delete(tmp); }
    }

    // ---- the mod export (game-free) ----

    [Fact]
    public void Export_writes_a_group_as_a_cond_override()
    {
        var cat = WeaponCat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Pdc", 0, 0));
        new SetWeaponSettingsCommand(doc.Placements[0], null, new WeaponSettings { Group = 5 }).Do(doc);

        var (ship, _, _) = ShipExport.Build(doc, cat, NoSpecs, "Gunship");
        var o = Assert.Single(Assert.Single(ship.AItems).ACondOverrides!);

        Assert.Equal(WeaponPanel.GroupCond, o.CondName);
        Assert.Equal(5, o.Amount);
        Assert.Equal(1.0, o.Chance);
        Assert.False(o.NegativeValue);
    }

    [Fact]
    public void Export_leaves_a_weapon_nobody_touched_alone()
    {
        var cat = WeaponCat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Pdc", 0, 0));
        var (ship, _, _) = ShipExport.Build(doc, cat, NoSpecs, "Gunship");
        Assert.Null(Assert.Single(ship.AItems).ACondOverrides);
    }

    /// <summary>Wear, a fill and a weapon page all want the same part's <c>aCondOverrides</c>. They accumulate
    /// into one list for exactly this reason.</summary>
    [Fact]
    public void Export_wear_and_a_weapon_page_do_not_erase_each_other()
    {
        var cat = new Fixtures()
            .Weapon("Pdc", extraValues: new Dictionary<string, double> { ["StatDamageMax"] = 8 })
            .Build();
        var doc = Fixtures.Doc(cat, Fixtures.P("Pdc", 0, 0));
        new SetWeaponSettingsCommand(doc.Placements[0], null, new WeaponSettings { Group = 6 }).Do(doc);

        var (ship, _, _) = ShipExport.Build(doc, cat, NoSpecs, "Worn gunship",
            wear: new WearOptions(true, 0.5, Seed: 9));
        var byCond = Assert.Single(ship.AItems).ACondOverrides!.ToDictionary(o => o.CondName, o => o.Amount);

        Assert.Equal(6, byCond[WeaponPanel.GroupCond]);
        Assert.True(byCond["StatDamage"] > 0);
    }

    /// <summary>A mod Ostraplan exported has to reopen as the design it was: the group lives in the item's cond
    /// overrides, which is the only place a template can carry it.</summary>
    [Fact]
    public void A_group_survives_an_export_and_a_reimport()
    {
        var cat = WeaponCat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Pdc", 0, 0), Fixtures.P("Thrower", 3, 0));
        new SetWeaponSettingsCommand(doc.Placements[0], null, new WeaponSettings { Group = 7 }).Do(doc);
        new SetWeaponSettingsCommand(doc.Placements[1], null, new WeaponSettings { Group = 3 }).Do(doc);

        var (ship, _, _) = ShipExport.Build(doc, cat, NoSpecs, "Gunship");
        var json = System.Text.Json.JsonSerializer.Serialize(new[] { ship });
        var template = ShipTemplate.ParseFile(json).Single();
        var back = TemplateImport.FromTemplate(template, cat).Doc;

        var groups = back.Placements
            .Where(p => WeaponPanel.IsWeapon(cat.Lookup(p.DefName)))
            .ToDictionary(p => p.DefName, p => p.Weapon?.Group);

        Assert.Equal(7, groups["Pdc"]);
        Assert.Equal(3, groups["Thrower"]);
    }

    // ---- the save write-back (game-free) ----

    [Fact]
    public void SaveEdit_merges_a_page_with_whatever_the_save_already_wrote()
    {
        var cat = WeaponCat();
        var item = new JsonObject
        {
            ["strID"] = "abc",
            ["aCondOverrides"] = new JsonArray(
                new JsonObject { ["CondName"] = "StatDamage", ["Chance"] = 1.0, ["Amount"] = 2.0 },
                new JsonObject { ["CondName"] = WeaponPanel.GroupCond, ["Chance"] = 1.0, ["Amount"] = 4.0 }),
        };

        SaveEdit.SetWeaponOverrides(item, new WeaponSettings { Group = 6 }, cat.Lookup("Pdc"));

        var byCond = ((JsonArray)item["aCondOverrides"]!)
            .Cast<JsonObject>()
            .ToDictionary(o => (string)o["CondName"]!, o => (double)o["Amount"]!);

        Assert.Equal(2.0, byCond["StatDamage"]);   // the save's own wear is untouched
        Assert.Equal(6, byCond[WeaponPanel.GroupCond]);   // our stale 4 replaced, not doubled up
    }

    /// <summary>Putting a weapon back to its def's group has to <b>remove</b> the save's override, or the design
    /// says one thing and the ship keeps doing another.</summary>
    [Fact]
    public void SaveEdit_clears_a_stale_override_when_a_weapon_goes_back_to_stock()
    {
        var cat = WeaponCat();
        var item = new JsonObject
        {
            ["strID"] = "abc",
            ["aCondOverrides"] = new JsonArray(
                new JsonObject { ["CondName"] = WeaponPanel.GroupCond, ["Chance"] = 1.0, ["Amount"] = 4.0 }),
        };

        SaveEdit.SetWeaponOverrides(item, null, cat.Lookup("Pdc"));

        Assert.Null(item["aCondOverrides"]);   // no entries is no field, as the game writes it
    }

    /// <summary>
    /// The item's overrides are the shallow-state channel and cannot carry a page on their own: on a full load
    /// <c>ApplyOverrideCondsToCO</c> routes through <c>AddCondAmount</c>, which returns immediately for any CO
    /// built from save data. So the page goes on the CO too — and the <c>DEFAULT</c> marker has to go with it,
    /// since it expands by appending the def's own conds and would give the def's group the last word.
    /// </summary>
    [Fact]
    public void SaveEdit_writes_a_group_onto_the_co_expanding_a_default_marker_that_would_outrank_it()
    {
        var cat = WeaponCat();
        var co = new JsonObject { ["strID"] = "abc", ["aConds"] = new JsonArray("DEFAULT") };

        SaveEdit.SetWeaponConds(co, new WeaponSettings { Group = 6 }, cat.Lookup("Pdc"));

        var conds = ((JsonArray)co["aConds"]!).Select(n => (string)n!).ToList();
        Assert.DoesNotContain("DEFAULT", conds);
        Assert.Contains($"{WeaponPanel.GroupCond}=1.0x6", conds);
        Assert.Single(conds, c => c.StartsWith($"{WeaponPanel.GroupCond}=", StringComparison.Ordinal));
        Assert.Contains($"{WeaponPanel.ArcAngleCond}=1.0x85", conds);   // the rest of the def came through
    }

    [Fact]
    public void SaveEdit_replaces_a_kept_cos_own_group_and_leaves_the_rest()
    {
        var cat = WeaponCat();
        var co = new JsonObject
        {
            ["strID"] = "abc",
            ["aConds"] = new JsonArray($"{WeaponPanel.GroupCond}=1.0x4", "StatDamage=1.0x2", "IsInstalled=1.0x1"),
        };

        SaveEdit.SetWeaponConds(co, new WeaponSettings { Group = 6 }, cat.Lookup("Pdc"));

        var conds = ((JsonArray)co["aConds"]!).Select(n => (string)n!).ToList();
        Assert.Contains("StatDamage=1.0x2", conds);
        Assert.Contains("IsInstalled=1.0x1", conds);
        Assert.Contains($"{WeaponPanel.GroupCond}=1.0x6", conds);
        Assert.DoesNotContain($"{WeaponPanel.GroupCond}=1.0x4", conds);
    }

    /// <summary>A weapon back at its stock group leaves the save's own entry gone rather than restated, and the
    /// expansion puts the def's group back where the marker used to promise it.</summary>
    [Fact]
    public void SaveEdit_returns_a_kept_weapon_to_stock_by_dropping_the_saves_entry()
    {
        var cat = WeaponCat();
        var co = new JsonObject
        {
            ["strID"] = "abc",
            ["aConds"] = new JsonArray($"{WeaponPanel.GroupCond}=1.0x4", "IsInstalled=1.0x1"),
        };

        SaveEdit.SetWeaponConds(co, null, cat.Lookup("Pdc"));

        var conds = ((JsonArray)co["aConds"]!).Select(n => (string)n!).ToList();
        Assert.DoesNotContain(conds, c => c.StartsWith($"{WeaponPanel.GroupCond}=", StringComparison.Ordinal));
        Assert.Contains("IsInstalled=1.0x1", conds);
    }

    /// <summary>Neither side has anything to say, so the record is not touched at all — a hundred explicit conds
    /// on every weapon in the save, in exchange for nothing, is not a trade worth making.</summary>
    [Fact]
    public void SaveEdit_leaves_an_untouched_weapon_completely_alone()
    {
        var cat = WeaponCat();
        var co = new JsonObject { ["strID"] = "abc", ["aConds"] = new JsonArray("DEFAULT") };

        SaveEdit.SetWeaponConds(co, null, cat.Lookup("Pdc"));

        Assert.Equal(["DEFAULT"], ((JsonArray)co["aConds"]!).Select(n => (string)n!));
    }

    [Fact]
    public void SaveEdit_writes_nothing_onto_a_part_that_is_not_a_weapon()
    {
        var cat = WeaponCat();
        var item = new JsonObject { ["strID"] = "abc" };
        SaveEdit.SetWeaponOverrides(item, new WeaponSettings { Group = 5 }, cat.Lookup("Crate"));
        Assert.Null(item["aCondOverrides"]);
    }

    // ---- against the live data ----

    /// <summary>
    /// The stock groups, which are compiled into nobody's head and drift with the data. Displayed, these are PDC
    /// 3, missile launcher 2, decoy launcher 4 — and a mass thrower 1, because its def declares no group at all.
    /// </summary>
    [SkippableFact]
    public void The_stock_defaults_are_what_the_shipped_data_declares()
    {
        var g = TestData.RequireGame();

        Assert.Equal(2, WeaponPanel.DefaultGroup(g.Catalog.Lookup("ItmShipWeaponPDC01")));
        Assert.Equal(1, WeaponPanel.DefaultGroup(g.Catalog.Lookup("ItmShipWeaponMissileLauncher01")));
        Assert.Equal(3, WeaponPanel.DefaultGroup(g.Catalog.Lookup("ItmShipWeaponDecoyLauncher01")));
        Assert.Equal(0, WeaponPanel.DefaultGroup(g.Catalog.Lookup("ItmShipWeaponMassThrower01")));

        // and the arcs the editor sorts and labels by
        Assert.Equal(85, WeaponPanel.ArcAngle(g.Catalog.Lookup("ItmShipWeaponPDC01")));
        Assert.Equal(360, WeaponPanel.ArcAngle(g.Catalog.Lookup("ItmShipWeaponMissileLauncher01")));
        Assert.True(WeaponPanel.IsOmnidirectional(g.Catalog.Lookup("ItmShipWeaponDecoyLauncher01")));
        Assert.False(WeaponPanel.IsOmnidirectional(g.Catalog.Lookup("ItmShipWeaponMassThrower01")));
    }

    /// <summary>
    /// No weapon def in the game declares a firing mode or a target select — they exist only as global condition
    /// definitions the MFD creates at runtime. That is why "absent means default" is the normal case for those
    /// two rather than a quirk, and why the editor authors them the same way it authors a mass thrower's group.
    /// </summary>
    [SkippableFact]
    public void No_shipped_weapon_declares_a_firing_mode_or_a_target_select()
    {
        var g = TestData.RequireGame();
        var weapons = g.Catalog.Parts.Where(WeaponPanel.IsWeapon).ToList();

        Assert.NotEmpty(weapons);
        Assert.All(weapons, w =>
        {
            Assert.False(WeaponPanel.DefaultManual(w));
            Assert.Equal(PdcTargetMode.All, WeaponPanel.DefaultTargetMode(w));
        });
    }

    /// <summary>
    /// The whole chain on real data: import a shipped gunship, group its fore cannons, export the mod, and check
    /// the overrides landed on those items and nowhere else. The synthetic tests above prove the arithmetic; this
    /// proves it against the def names, cond values and rotations the game actually ships.
    /// </summary>
    [SkippableFact]
    public void A_real_gunship_exports_the_groups_it_was_given()
    {
        var g = TestData.RequireGame();
        var doc = TestData.Template(g, "Pequod Titan Refit");

        var fore = doc.Placements
            .Where(p => WeaponPanel.Classify(g.Catalog.Lookup(p.DefName)) == WeaponClass.PointDefence
                        && WeaponPanel.Facing(g.Catalog.Lookup(p.DefName), p.Rot) == WeaponFacing.Fore)
            .ToList();
        Assert.Equal(2, fore.Count);

        var stack = new CommandStack();
        foreach (var p in fore)
            stack.Push(doc, new SetWeaponSettingsCommand(p, p.Weapon, new WeaponSettings { Group = 0 }));

        var (ship, _, _) = ShipExport.Build(doc, g.Catalog, NoSpecs, "Pequod");

        var grouped = ship.AItems
            .Where(i => i.ACondOverrides?.Any(o => o.CondName == WeaponPanel.GroupCond) == true)
            .ToList();

        Assert.Equal(2, grouped.Count);   // the two we set, and nothing else on a 3,000-item hull
        Assert.All(grouped, i =>
        {
            Assert.Equal("ItmShipWeaponPDC01", i.StrName);
            Assert.Equal(0, i.ACondOverrides!.Single(o => o.CondName == WeaponPanel.GroupCond).Amount);
        });

        // and undoing puts the ship back to shipping nothing at all
        while (stack.CanUndo) stack.Undo(doc);
        var (clean, _, _) = ShipExport.Build(doc, g.Catalog, NoSpecs, "Pequod");
        Assert.DoesNotContain(clean.AItems,
            i => i.ACondOverrides?.Any(o => o.CondName == WeaponPanel.GroupCond) == true);
    }

    /// <summary>
    /// A real gunship: the Pequod Titan Refit carries thirteen weapons of all four classes, with its eight PDCs
    /// laid two to a side. It is the check that the facing derivation survives a real hull's coordinates, and that
    /// an imported ship arrives carrying nothing authored — no core template overrides a firing group, so every
    /// weapon in the game spawns at its def's own.
    /// </summary>
    [SkippableFact]
    public void A_real_gunships_weapons_are_found_sided_and_arrive_unauthored()
    {
        var g = TestData.RequireGame();
        var doc = TestData.Template(g, "Pequod Titan Refit");

        var weapons = doc.Placements
            .Where(p => WeaponPanel.IsWeapon(g.Catalog.Lookup(p.DefName)))
            .ToList();

        Assert.Equal(13, weapons.Count);
        Assert.All(weapons, w => Assert.Null(w.Weapon));   // the game ships none of this authored

        var pdcs = weapons.Where(p => WeaponPanel.Classify(g.Catalog.Lookup(p.DefName)) == WeaponClass.PointDefence)
                          .GroupBy(p => WeaponPanel.Facing(g.Catalog.Lookup(p.DefName), p.Rot))
                          .ToDictionary(x => x.Key, x => x.Count());

        Assert.Equal(2, pdcs[WeaponFacing.Fore]);
        Assert.Equal(2, pdcs[WeaponFacing.Aft]);
        Assert.Equal(2, pdcs[WeaponFacing.Port]);
        Assert.Equal(2, pdcs[WeaponFacing.Starboard]);

        // both launcher families cover the circle, so they sort under no heading at all
        Assert.All(weapons.Where(p => WeaponPanel.Classify(g.Catalog.Lookup(p.DefName))
                                      is WeaponClass.MissileLauncher or WeaponClass.DecoyLauncher),
            p => Assert.Equal(WeaponFacing.Any, WeaponPanel.Facing(g.Catalog.Lookup(p.DefName), p.Rot)));
    }
}
