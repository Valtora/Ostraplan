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
    public void A_multi_tile_part_tints_its_whole_footprint()
    {
        var cat = Cat();
        var doc = Fixtures.Doc(cat, Fixtures.P("Big", 3, 4));
        var state = new DamageState();
        state.Apply(doc.Placements[0], "Big", 5, cat);

        var part = Assert.Single(DamageOverlay.Build(doc, state).Parts);

        Assert.Equal(4, part.Tiles.Count);
        Assert.Contains((3, 4), part.Tiles);
        Assert.Contains((4, 5), part.Tiles);
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
}
