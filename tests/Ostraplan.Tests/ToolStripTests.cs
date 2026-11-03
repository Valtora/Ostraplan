using System.Threading;
using Ostraplan.App;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The tool strip down the left of the plan (#79): one button per tool a left click can use, lit for the one in use.
/// The first two hold the table it is built from; the rest drive the real window, game-free because nothing loads
/// until <c>Loaded</c>, and one at a time like every class that builds a <see cref="MainWindow"/>.
/// </summary>
[Collection(MainWindowCollection.Name)]
public class ToolStripTests
{
    [Fact]
    public void The_eraser_pick_and_hand_outrank_a_brush_and_a_brush_outranks_selecting()
    {
        Assert.Equal(PlanTool.Select, PlanTools.Active(false, false, false, false, otherMode: false));
        Assert.Equal(PlanTool.Place, PlanTools.Active(false, false, false, armed: true, otherMode: false));
        Assert.Equal(PlanTool.Pick, PlanTools.Active(false, pick: true, false, armed: true, otherMode: false));
        Assert.Equal(PlanTool.Pan, PlanTools.Active(false, false, pan: true, armed: true, otherMode: false));
        Assert.Equal(PlanTool.Erase, PlanTools.Active(erase: true, false, false, false, otherMode: false));

        // A wiring pick or a zone being painted decides the click, and the strip has no button for either.
        Assert.Null(PlanTools.Active(false, false, false, false, otherMode: true));
    }

    /// <summary>Every tool a key reaches names that key on its tooltip, which is how a strip of icons teaches the
    /// keys. The tooltips are UI text, so they follow the same rules as a tip.</summary>
    [Fact]
    public void Every_keyed_tool_names_its_key_and_every_tooltip_is_plain()
    {
        Assert.Contains("(Esc)", PlanTools.Title(PlanTool.Select));
        Assert.Contains("Space", PlanTools.Title(PlanTool.Pan));
        Assert.Contains("Alt+click", PlanTools.Title(PlanTool.Pick));
        Assert.Contains("(X)", PlanTools.Title(PlanTool.Erase));
        Assert.All(Enum.GetValues<PlanTool>(), t =>
        {
            var tip = PlanTools.Tip(t);
            Assert.EndsWith(".", tip);
            Assert.DoesNotContain('—', tip + PlanTools.Title(t));
            Assert.DoesNotContain('–', tip + PlanTools.Title(t));
        });
    }

    [Fact]
    public void The_lit_button_follows_the_tool_in_use()
    {
        RunSta(() =>
        {
            var w = new MainWindow();
            var board = w.ActiveSession.Board;
            Assert.Equal(PlanTool.Select, w.LitTool);

            w.TakeUpTool(PlanTool.Pan);
            Assert.True(board.PanTool);
            Assert.Equal(PlanTool.Pan, w.LitTool);

            // The eraser puts the hand down, and the strip follows a key as readily as a click.
            board.SetEraseMode(true);
            Assert.False(board.PanTool);
            Assert.Equal(PlanTool.Erase, w.LitTool);

            w.TakeUpTool(PlanTool.Select);
            Assert.False(board.EraseMode);
            Assert.Equal(PlanTool.Select, w.LitTool);

            w.Close();
        });
    }

    /// <summary>A brush in hand lights Place. The pick leaves it in hand until it picks a replacement, and the hand
    /// puts it down, because with the hand a drag pans and the brush has nothing left to do.</summary>
    [Fact]
    public void A_brush_lights_Place_and_survives_the_pick_but_not_the_hand()
    {
        RunSta(() =>
        {
            var w = new MainWindow();
            var board = w.ActiveSession.Board;
            var cat = new Fixtures().Floor("Floor").Build();

            board.SetArmed(cat.ByDefName["Floor"]);
            Assert.Equal(PlanTool.Place, w.LitTool);

            w.TakeUpTool(PlanTool.Pick);
            Assert.Equal(PlanTool.Pick, w.LitTool);
            Assert.NotNull(board.ArmedPart);

            w.TakeUpTool(PlanTool.Pick);   // a second click puts it down again
            Assert.Equal(PlanTool.Place, w.LitTool);

            w.TakeUpTool(PlanTool.Pan);
            Assert.Null(board.ArmedPart);
            Assert.Equal(PlanTool.Pan, w.LitTool);

            // Arming a part puts the hand down: the click is a placement again.
            board.SetArmed(cat.ByDefName["Floor"]);
            Assert.False(board.PanTool);
            Assert.Equal(PlanTool.Place, w.LitTool);

            w.Close();
        });
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
