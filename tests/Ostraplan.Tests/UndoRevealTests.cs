using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// What an undo or redo points at afterwards (#76): <see cref="ChangeSet"/> sorts a step's parts into the ones to
/// select and the tiles to mark, and the stack steps through several edits at once for the history list. All of it is
/// read by the window after the step, so these hold the document-side answer the canvas draws from.
/// </summary>
public class UndoRevealTests
{
    private static ShipDocument Design() =>
        new(new Fixtures().Floor("Floor").Wall("Wall").Part("Rack", w: 2, h: 1).Build());

    [Fact]
    public void An_undone_placement_is_marked_where_it_was_and_a_redone_one_is_selected()
    {
        var doc = Design();
        var stack = new CommandStack();
        var rack = new Placement { DefName = "Rack", X = 4, Y = 2 };
        stack.Push(doc, new PlaceCommand(rack));

        var undone = stack.Undo(doc);
        var gone = ChangeSet.Of(undone).Resolve(doc);
        Assert.Empty(gone.Parts);
        Assert.Equal([(4, 2), (5, 2)], gone.Marked.OrderBy(t => t.X));   // the whole 2x1 footprint, not the anchor
        Assert.Equal(gone.Marked.ToHashSet(), gone.Covered.ToHashSet());

        var redone = stack.Redo(doc);
        var back = ChangeSet.Of(redone).Resolve(doc);
        Assert.Same(rack, Assert.Single(back.Parts));
        Assert.Empty(back.Marked);
        Assert.Equal(2, back.Covered.Count);
    }

    [Fact]
    public void A_settings_edit_points_at_its_part_though_nothing_on_the_plan_moved()
    {
        var doc = Design();
        var stack = new CommandStack();
        var wall = Fixtures.Place(doc, "Wall", 7, 7);
        stack.Push(doc, new SetCustomNameCommand(wall, null, "Bulkhead"));

        var view = ChangeSet.Of(stack.Undo(doc)).Resolve(doc);
        Assert.Same(wall, Assert.Single(view.Parts));
        Assert.Empty(view.Marked);
        Assert.Equal([(7, 7)], view.Covered);
    }

    [Fact]
    public void A_zone_stroke_marks_only_the_tiles_that_changed_hands()
    {
        var doc = Design();
        var zone = new ShipZone { Name = "Hold" };
        var before = new HashSet<(int X, int Y)> { (0, 0), (1, 0) };
        var after = new HashSet<(int X, int Y)> { (1, 0), (2, 0), (3, 0) };

        var set = ChangeSet.Of([new SetZoneTilesCommand(zone, before, after)]);
        var view = set.Resolve(doc);
        Assert.Empty(view.Parts);
        Assert.Equal([(0, 0), (2, 0), (3, 0)], view.Marked.OrderBy(t => t.X));   // (1, 0) was in the zone both times
    }

    [Fact]
    public void Wiring_points_at_both_ends()
    {
        var doc = Design();
        var a = Fixtures.Place(doc, "Wall", 0, 0);
        var b = Fixtures.Place(doc, "Wall", 5, 0);
        var stack = new CommandStack();
        stack.Push(doc, new AddLinkCommand(new DeviceLink(a.Id, b.Id)));

        var view = ChangeSet.Of(stack.Undo(doc)).Resolve(doc);
        Assert.Equal([a, b], view.Parts.OrderBy(p => p.X));
    }

    [Fact]
    public void A_loose_item_is_selected_or_marked_like_a_part()
    {
        var doc = Design();
        var stack = new CommandStack();
        var crate = new LooseObject { DefName = "Floor", X = 3, Y = 3 };
        stack.Push(doc, new PlaceLooseCommand(crate));

        var gone = ChangeSet.Of(stack.Undo(doc)).Resolve(doc);
        Assert.Empty(gone.Loose);
        Assert.Equal([(3, 3)], gone.Marked);

        var back = ChangeSet.Of(stack.Redo(doc)).Resolve(doc);
        Assert.Same(crate, Assert.Single(back.Loose));
        Assert.Empty(back.Marked);
    }

    [Fact]
    public void A_jump_down_the_history_takes_every_step_to_the_one_picked_and_shows_them_together()
    {
        var doc = Design();
        var stack = new CommandStack();
        var first = new Placement { DefName = "Floor", X = 0, Y = 0 };
        var second = new Placement { DefName = "Floor", X = 10, Y = 0 };
        var third = new Placement { DefName = "Floor", X = 20, Y = 0 };
        foreach (var p in new[] { first, second, third }) stack.Push(doc, new PlaceCommand(p));

        Assert.Equal(3, stack.UndoSteps.Count);
        Assert.Same(third, ((PlaceCommand)stack.UndoSteps[0]).Placement);   // the next undo first

        var changed = 0;
        var applied = new List<CommandAction>();
        stack.StateChanged += () => changed++;
        stack.Applied += (_, action) => applied.Add(action);

        var undone = stack.Undo(doc, 2);
        Assert.Equal([third, second], undone.Cast<PlaceCommand>().Select(c => c.Placement));
        Assert.Same(first, Assert.Single(doc.Placements));
        Assert.Equal(1, changed);                                          // one refresh for the jump
        Assert.Equal([CommandAction.Undo, CommandAction.Undo], applied);   // but every step is still logged
        Assert.Equal(2, stack.RedoSteps.Count);
        Assert.Same(second, ((PlaceCommand)stack.RedoSteps[0]).Placement);

        var view = ChangeSet.Of(undone).Resolve(doc);
        Assert.Equal([(10, 0), (20, 0)], view.Marked.OrderBy(t => t.X));

        Assert.Equal(2, stack.Redo(doc, 5).Count);   // asking for more than there is takes what there is
        Assert.Equal(3, doc.Placements.Count);
    }

    [Fact]
    public void A_locked_stack_takes_no_steps_and_hands_back_none()
    {
        var doc = Design();
        var stack = new CommandStack();
        stack.Push(doc, new PlaceCommand(new Placement { DefName = "Floor", X = 0, Y = 0 }));
        stack.ReadOnly = true;

        Assert.Empty(stack.Undo(doc, 3));
        Assert.Single(doc.Placements);
    }
}
