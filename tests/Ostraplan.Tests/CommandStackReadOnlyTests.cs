using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The undo stack's read-only switch (#74), the backstop behind a locked tab. Whatever route an edit takes to the
/// stack, a locked one leaves the document exactly as it was and says it refused, so the window can explain why.
/// </summary>
public class CommandStackReadOnlyTests
{
    private static (Catalog Cat, ShipDocument Doc) Design()
    {
        var cat = new Fixtures().Floor("Floor").Build();
        return (cat, new ShipDocument(cat));
    }

    [Fact]
    public void A_locked_stack_runs_nothing_and_says_so()
    {
        var (_, doc) = Design();
        var stack = new CommandStack { ReadOnly = true };
        var refused = 0;
        var changed = 0;
        stack.Refused += () => refused++;
        stack.StateChanged += () => changed++;

        stack.Push(doc, new PlaceCommand(new Placement { DefName = "Floor", X = 0, Y = 0 }));

        Assert.Empty(doc.Placements);
        Assert.False(stack.CanUndo);
        Assert.False(stack.Dirty);
        Assert.Equal(1, refused);
        Assert.Equal(0, changed);
    }

    [Fact]
    public void Undo_and_redo_are_refused_too_and_the_history_survives_the_lock()
    {
        var (_, doc) = Design();
        var stack = new CommandStack();
        stack.Push(doc, new PlaceCommand(new Placement { DefName = "Floor", X = 0, Y = 0 }));
        stack.Push(doc, new PlaceCommand(new Placement { DefName = "Floor", X = 1, Y = 0 }));
        stack.Undo(doc);
        Assert.Single(doc.Placements);

        var refused = 0;
        stack.Refused += () => refused++;
        stack.ReadOnly = true;
        stack.Undo(doc);
        stack.Redo(doc);
        Assert.Single(doc.Placements);
        Assert.Equal(2, refused);

        // Unlocked, the history is where it was: nothing was thrown away by locking.
        stack.ReadOnly = false;
        stack.Redo(doc);
        Assert.Equal(2, doc.Placements.Count);
        stack.Undo(doc);
        stack.Undo(doc);
        Assert.Empty(doc.Placements);
    }

    [Fact]
    public void An_empty_history_refuses_nothing()
    {
        var (_, doc) = Design();
        var stack = new CommandStack { ReadOnly = true };
        var refused = 0;
        stack.Refused += () => refused++;
        stack.Undo(doc);   // Ctrl+Z with nothing to undo is not an attempt to edit
        stack.Redo(doc);
        Assert.Equal(0, refused);
    }
}
