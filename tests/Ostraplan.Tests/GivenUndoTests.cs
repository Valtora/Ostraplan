using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// Undoing a reposition must restore <see cref="Placement.IsGiven"/>, not just the tiles. Moving an
/// imported part re-authors it — the placement law then judges it and the bill counts it as new
/// construction — so an undo that only put the pose back left a nudge permanently baked into the design,
/// with a blocking "problem" the user could not clear. Reported from Discord: an exterior bulkhead bin
/// went red the moment it was touched and Ctrl+Z would not take it back.
/// </summary>
public class GivenUndoTests
{
    private const string B = "Blank";

    private static readonly LootDef[] Loots = [new("FixtureAdds", ["IsFixture", "IsObstruction"], [])];

    private static readonly PartDef Fixture = new(
        "Fixture", "Fixture", "FURN", "core",
        new ItemDef("Fixture", "", false, null, 0, 1, ["FixtureAdds"],
            [B, B, B, B, B, B, B, B, B], [B, B, B, B, B, B, B, B, B]),
        null, [], [], [], new Dictionary<string, double>(), new Dictionary<string, (double, double)>());

    private static ShipDocument Doc(out Placement p)
    {
        var cat = new Catalog
        {
            Parts = [Fixture],
            ByDefName = new Dictionary<string, PartDef> { ["Fixture"] = Fixture },
            Loots = Loots.ToDictionary(l => l.Name),
            Triggers = new Dictionary<string, CondTriggerDef>(),
            Warnings = [],
        };
        var doc = new ShipDocument(cat);
        p = new Placement { DefName = "Fixture", X = 3, Y = 4, IsGiven = true };
        new PlaceCommand(p).Do(doc);
        return doc;
    }

    [Fact]
    public void Undoing_a_move_restores_given_ness()
    {
        var doc = Doc(out var p);
        var cmd = new MoveCommand([p], 1, 0);

        cmd.Do(doc);
        Assert.False(p.IsGiven);   // the move itself re-authors it — that part is intended
        Assert.Equal(4, p.X);

        cmd.Undo(doc);
        Assert.Equal(3, p.X);
        Assert.True(p.IsGiven);    // …and undo takes it back
    }

    [Fact]
    public void Undoing_a_rotate_restores_given_ness()
    {
        var doc = Doc(out var p);
        var cmd = new RotateCommand(doc, p, 90);

        cmd.Do(doc);
        Assert.False(p.IsGiven);

        cmd.Undo(doc);
        Assert.Equal(0, p.Rot);
        Assert.True(p.IsGiven);
    }

    [Fact]
    public void Undoing_a_group_transform_restores_given_ness()
    {
        var doc = Doc(out var given);
        var authored = new Placement { DefName = "Fixture", X = 8, Y = 8 };
        new PlaceCommand(authored).Do(doc);

        var cmd = new SetPosesCommand([(given, 5, 5, 90), (authored, 9, 9, 90)]);
        cmd.Do(doc);
        cmd.Undo(doc);

        Assert.True(given.IsGiven);        // restored
        Assert.False(authored.IsGiven);    // a part that was never given stays authored
        Assert.Equal((3, 4), (given.X, given.Y));
        Assert.Equal((8, 8), (authored.X, authored.Y));
    }

    [Fact]
    public void A_move_that_is_kept_still_clears_given_ness()
    {
        var doc = Doc(out var p);
        new MoveCommand([p], 2, 2).Do(doc);
        Assert.False(p.IsGiven);   // the law must judge a part the user actually repositioned
    }
}
