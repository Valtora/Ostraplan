using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The damage heat map, and the anchor provenance that decides which frame a strike is measured in.
/// </summary>
public class DamageOverlayTests
{
    private static Catalog Cat() => new Fixtures()
        .Part("Wall", startingConds: ["IsInstalled", "IsWall"],
              condValues: new Dictionary<string, double> { ["StatDamageMax"] = 10 })
        .Part("WallDmg", startingConds: ["IsInstalled", "IsWall", "IsDamaged"],
              condValues: new Dictionary<string, double> { ["StatDamageMax"] = 20 })
        .Part("Big", w: 2, h: 2, startingConds: ["IsInstalled"],
              condValues: new Dictionary<string, double> { ["StatDamageMax"] = 10 })
        .BreakPair("Wall", "WallDmg")
        .Build();

    [Fact]
    public void An_untouched_ship_draws_nothing()
    {
        var cat = Cat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Wall", 0, 0));

        Assert.True(DamageOverlay.Build(doc, new DamageState()).IsEmpty);
    }

    [Fact]
    public void Only_damaged_parts_appear_and_the_worst_leads()
    {
        var cat = Cat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Wall", 0, 0), Fixtures.P("Wall", 1, 0), Fixtures.P("Wall", 2, 0));
        var state = new DamageState();

        // One part scratched, one destroyed, one untouched.
        state.Apply(doc.Placements[0], "Wall", 5, cat);
        state.Apply(doc.Placements[1], "Wall", 10, cat);     // breaks into WallDmg
        state.Apply(doc.Placements[1], "WallDmg", 20, cat);  // and then is gone

        var ov = DamageOverlay.Build(doc, state);

        Assert.Equal(2, ov.Parts.Count);
        Assert.Equal(1, ov.Destroyed);
        Assert.Equal(0, ov.WorstCondition, 6);
        // Worst first, so a list of these leads with what actually broke.
        Assert.True(ov.Parts[0].Destroyed);
        Assert.Equal(doc.Placements[1].Id, ov.Parts[0].PlacementId);
        Assert.Equal(1 - 5.0 / 30.0, ov.Parts[1].Condition, 6);
    }

    [Fact]
    public void The_three_states_are_counted_apart_rather_than_as_one_damaged_total()
    {
        var cat = Cat();
        var doc = Fixtures.Doc(cat,
            Fixtures.P("Wall", 0, 0), Fixtures.P("Wall", 1, 0), Fixtures.P("Wall", 2, 0), Fixtures.P("Wall", 3, 0));
        var state = new DamageState();

        state.Apply(doc.Placements[0], "Wall", 5, cat);      // chipped: still the wall that was drawn
        state.Apply(doc.Placements[1], "Wall", 10, cat);     // broken: a WallDmg stands here now
        state.Apply(doc.Placements[2], "Wall", 10, cat);
        state.Apply(doc.Placements[2], "WallDmg", 20, cat);  // destroyed: nothing stands here
        // Placement 3 is untouched and must not appear at all.

        var ov = DamageOverlay.Build(doc, state);

        Assert.Equal(3, ov.Parts.Count);
        Assert.Equal(1, ov.Chipped);
        Assert.Equal(1, ov.Broken);
        Assert.Equal(1, ov.Destroyed);
        // The figure the old single "damaged" count could not give: how much of the ship is no longer what it was.
        Assert.Equal(2, ov.ChangedForm);

        var byId = ov.Parts.ToDictionary(p => p.PlacementId, p => p.Grade);
        Assert.Equal(DamageGrade.Chipped, byId[doc.Placements[0].Id]);
        Assert.Equal(DamageGrade.Broken, byId[doc.Placements[1].Id]);
        Assert.Equal(DamageGrade.Destroyed, byId[doc.Placements[2].Id]);
    }

    [Fact]
    public void A_changed_part_carries_both_the_form_it_was_and_the_form_it_is()
    {
        var cat = Cat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Wall", 0, 0));
        var state = new DamageState();
        state.Apply(doc.Placements[0], "Wall", 10, cat);

        var part = Assert.Single(DamageOverlay.Build(doc, state).Parts);

        // Naming only the current form leaves a list of changes that cannot say what was lost.
        Assert.Equal("Wall", part.OriginalDef);
        Assert.Equal("WallDmg", part.CurrentDef);
        Assert.True(part.ChangedForm);
        Assert.False(part.Destroyed);
    }

    [Fact]
    public void A_multi_tile_part_tints_its_whole_body()
    {
        var cat = Cat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Big", 3, 4));
        var state = new DamageState();
        state.Apply(doc.Placements[0], "Big", 5, cat);

        var part = Assert.Single(DamageOverlay.Build(doc, state).Parts);

        Assert.Equal(4, part.Tiles.Count());
        Assert.Contains((3, 4), part.Tiles);
        Assert.Contains((4, 5), part.Tiles);
    }

    [SkippableFact]
    public void A_tank_tints_the_tank_and_not_the_deck_around_it()
    {
        var g = TestData.RequireGame();
        // The LHe canisters are the widest gap between socket and object in stock data: a 3×3 tank sitting in a
        // 7×7 clearance. Tinting the socket painted 49 tiles of deck for a part that absorbs on one cell, so a
        // single dead tank read as though the whole bay had gone.
        var doc = Fixtures.Doc(g.Catalog, Fixtures.P("ItmCanisterLHe02", 10, 10));
        Skip.IfNot(doc.Placements.Count == 1, "ItmCanisterLHe02 not in this install");
        var state = new DamageState();
        state.Apply(doc.Placements[0], "ItmCanisterLHe02", 5, g.Catalog);

        var part = Assert.Single(DamageOverlay.Build(doc, state).Parts);

        Assert.Equal(9, part.Tiles.Count());
        // Offset into the socket, not anchored at its corner: the object sits in the middle of its clearance.
        Assert.DoesNotContain((10, 10), part.Tiles);
        Assert.Contains((13, 13), part.Tiles);
    }

    [Fact]
    public void The_overlay_names_the_form_the_part_is_in_now()
    {
        var cat = Cat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Wall", 0, 0));
        var state = new DamageState();
        state.Apply(doc.Placements[0], "Wall", 10, cat);

        var part = Assert.Single(DamageOverlay.Build(doc, state).Parts);

        // The tile no longer holds what the design names, and a tooltip has to say so.
        Assert.Equal("WallDmg", part.CurrentDef);
        Assert.Equal(1, part.Stages);
        Assert.Equal("Wall", doc.Placements[0].DefName);
    }

    [Fact]
    public void An_authored_design_falls_back_to_the_export_frame()
    {
        var cat = Cat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Wall", 5, 5));

        Assert.Null(doc.SourceShipPos);
        Assert.Equal(StrikeFrame.AsExported, MicrometeoroidStrike.AnchorFor(doc).Frame);
    }

    [Fact]
    public void An_imported_design_uses_its_own_anchor_without_being_asked()
    {
        var cat = Cat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Wall", 5, 5));
        // What TemplateImport records: the source ship's vShipPos, in document coords.
        doc.SourceShipPos = (7, 11);

        var anchor = MicrometeoroidStrike.AnchorFor(doc);

        // No caller has to remember to pass it, which is what stops an imported ship being measured in the frame
        // it will only have once it is exported.
        Assert.Equal(StrikeFrame.AsImported, anchor.Frame);
        Assert.Equal(7, anchor.DocX, 6);
        Assert.Equal(11, anchor.DocY, 6);
    }

    // ---- the projected hull ----

    [Fact]
    public void Projecting_a_run_gives_the_hull_the_strike_left()
    {
        var cat = Cat();
        var doc = Fixtures.Doc(cat,
            Fixtures.P("Wall", 0, 0), Fixtures.P("Wall", 1, 0), Fixtures.P("Wall", 2, 0));
        var state = new DamageState();

        state.Apply(doc.Placements[0], "Wall", 5, cat);      // chipped: unchanged in the projection
        state.Apply(doc.Placements[1], "Wall", 10, cat);     // broken: stands as WallDmg
        state.Apply(doc.Placements[2], "Wall", 10, cat);
        state.Apply(doc.Placements[2], "WallDmg", 20, cat);  // destroyed: not there at all

        var after = state.Project(doc);

        Assert.Equal(2, after.Placements.Count);
        Assert.Equal("Wall", after.Placements[0].DefName);
        Assert.Equal("WallDmg", after.Placements[1].DefName);
        Assert.DoesNotContain(after.Placements, p => p.X == 2);

        // A projection, never an edit: the design the user is working on is untouched, because wear is not part of
        // a design and must not reach the .oplan.
        Assert.Equal(3, doc.Placements.Count);
        Assert.All(doc.Placements, p => Assert.Equal("Wall", p.DefName));
    }

    [Fact]
    public void An_untouched_run_projects_the_ship_it_was_given()
    {
        var cat = Cat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Wall", 0, 0), Fixtures.P("Big", 3, 4));

        var after = new DamageState().Project(doc);

        Assert.Equal(doc.Placements.Count, after.Placements.Count);
        Assert.Equal(
            doc.Placements.Select(p => (p.DefName, p.X, p.Y)).OrderBy(t => t.X),
            after.Placements.Select(p => (p.DefName, p.X, p.Y)).OrderBy(t => t.X));
    }
}
