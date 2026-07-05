using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>The theme picker: a ship-wide re-skin of walls/floors to a chosen cooverlay variant.
/// Needs real defs, so these no-op without the install.</summary>
public class ThemeOpsTests
{
    private const string Wall = "ItmWall1x1";
    private const string Floor = "ItmFloorGrate01";

    private static Placement Place(ShipDocument doc, string def, int x, int y)
    {
        var p = new Placement { DefName = def, X = x, Y = y };
        new PlaceCommand(p).Do(doc);
        return p;
    }

    private static string? OtherSkin(Catalog cat, int layer, int w, int h, string not) =>
        ReplaceOps.CompatibleTargets(cat, (layer, w, h)).FirstOrDefault(t => t.DefName != not)?.DefName;

    [Fact]
    public void Reskin_swaps_every_wall_ship_wide_in_one_step()
    {
        if (TestData.Game is not { } g || !g.Catalog.ByDefName.ContainsKey(Wall)) return;
        if (OtherSkin(g.Catalog, Catalog.LayerWall, 1, 1, Wall) is not { } skin) return;

        var doc = new ShipDocument(g.Catalog);
        Place(doc, Wall, 0, 0);
        Place(doc, Wall, 1, 0);
        Place(doc, Wall, 2, 0);

        var reskin = ThemeOps.BuildReskin(doc, skin, null);
        Assert.NotNull(reskin);

        var changed = 0;
        doc.Changed += () => changed++;
        reskin!.Value.Cmd.Do(doc);

        Assert.Equal(1, changed);                                   // one coalesced notification for the batch
        Assert.Equal(3, doc.Placements.Count);
        Assert.All(doc.Placements, p => Assert.Equal(skin, p.DefName));
    }

    [Fact]
    public void Reskin_applies_walls_and_floors_together_and_leaves_others()
    {
        if (TestData.Game is not { } g || !g.Catalog.ByDefName.ContainsKey(Wall) || !g.Catalog.ByDefName.ContainsKey(Floor)) return;
        if (OtherSkin(g.Catalog, Catalog.LayerWall, 1, 1, Wall) is not { } wallSkin) return;
        if (OtherSkin(g.Catalog, Catalog.LayerFloor, 1, 1, Floor) is not { } floorSkin) return;
        // a fixture (some non-wall, non-floor 1×1 part) to prove it's untouched
        var fixture = g.Catalog.Parts.FirstOrDefault(p => g.Catalog.RenderLayer(p) == Catalog.LayerFixture && p.Item is { Width: 1, Height: 1 });

        var doc = new ShipDocument(g.Catalog);
        var w = Place(doc, Wall, 0, 0);
        var f = Place(doc, Floor, 1, 0);
        Placement? fx = fixture is null ? null : Place(doc, fixture.DefName, 2, 0);

        var reskin = ThemeOps.BuildReskin(doc, wallSkin, floorSkin);
        Assert.NotNull(reskin);
        reskin!.Value.Cmd.Do(doc);

        Assert.DoesNotContain(w, doc.Placements);
        Assert.DoesNotContain(f, doc.Placements);
        Assert.Contains(doc.Placements, p => p.DefName == wallSkin && (p.X, p.Y) == (0, 0));
        Assert.Contains(doc.Placements, p => p.DefName == floorSkin && (p.X, p.Y) == (1, 0));
        if (fx is not null) Assert.Contains(fx, doc.Placements);    // fixture kept its def and identity
    }

    [Fact]
    public void Reskin_is_a_no_op_when_all_parts_already_match()
    {
        if (TestData.Game is not { } g || !g.Catalog.ByDefName.ContainsKey(Wall)) return;

        var doc = new ShipDocument(g.Catalog);
        Place(doc, Wall, 0, 0);
        Place(doc, Wall, 1, 0);

        Assert.Null(ThemeOps.BuildReskin(doc, Wall, null));         // reskin to the def they already are
        Assert.Null(ThemeOps.BuildReskin(doc, null, null));         // nothing chosen
    }

    [Fact]
    public void Same_class_placements_ignore_the_locked_primary_airlock()
    {
        if (TestData.Game is not { } g || !g.Catalog.ByDefName.ContainsKey(Wall)) return;

        var doc = new ShipDocument(g.Catalog);
        Place(doc, Catalog.PrimaryDocksysDef, 0, 0);               // locked, HULL — never re-skinned
        var w = Place(doc, Wall, 5, 0);

        var walls = ThemeOps.SameClassPlacements(doc, Wall);
        Assert.Contains(w, walls);
        Assert.DoesNotContain(walls, p => p.DefName == Catalog.PrimaryDocksysDef);
    }

    [Fact]
    public void Reskin_reverses_cleanly_as_one_undo()
    {
        if (TestData.Game is not { } g || !g.Catalog.ByDefName.ContainsKey(Wall)) return;
        if (OtherSkin(g.Catalog, Catalog.LayerWall, 1, 1, Wall) is not { } skin) return;

        var doc = new ShipDocument(g.Catalog);
        Place(doc, Wall, 0, 0);
        Place(doc, Wall, 1, 0);

        var reskin = ThemeOps.BuildReskin(doc, skin, null);
        Assert.NotNull(reskin);
        reskin!.Value.Cmd.Do(doc);
        Assert.All(doc.Placements, p => Assert.Equal(skin, p.DefName));

        reskin.Value.Cmd.Undo(doc);
        Assert.Equal(2, doc.Placements.Count);
        Assert.All(doc.Placements, p => Assert.Equal(Wall, p.DefName));
    }
}
