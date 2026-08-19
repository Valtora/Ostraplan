using System.Threading;
using Ostraplan.App;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// One <see cref="DocumentSession"/> per open design (per document tab). What is worth pinning is the isolation:
/// before tabs, every one of these lived as a single field on the main window, so a second document sharing any of
/// them would be a design editing itself in two places. The change-notification detach is here for the same reason —
/// a session left subscribed to a document it no longer holds is how a background tab's edit ends up refreshing the
/// chrome of the one on screen.
///
/// <para>Game-free (a synthetic <see cref="Fixtures"/> catalogue), but STA: a session owns a WPF canvas, and a WPF
/// control can only be built on an STA thread.</para>
/// </summary>
public class DocumentSessionTests
{
    private static readonly Catalog Cat = new Fixtures().Build();

    private static DocumentSession Session() => new() { Board = new ShipCanvas() };

    private static Placement Wall(int x, int y) => new() { DefName = "TestWall", X = x, Y = y };

    [Fact]
    public void Two_sessions_keep_separate_documents_and_undo_history()
    {
        RunSta(() =>
        {
            var a = Session();
            var b = Session();
            a.Doc = new ShipDocument(Cat);
            b.Doc = new ShipDocument(Cat);

            a.Stack.Push(a.Doc, new PlaceCommand(Wall(0, 0)));
            a.Stack.Push(a.Doc, new PlaceCommand(Wall(1, 0)));

            Assert.Equal(2, a.Doc.Placements.Count);
            Assert.Empty(b.Doc.Placements);      // the edit landed in one design only
            Assert.True(a.Stack.CanUndo);
            Assert.False(b.Stack.CanUndo);       // and in one history only
            Assert.True(a.Dirty);
            Assert.False(b.Dirty);

            // Undo in one is not undo in the other: this is the whole reason the stack moved onto the session.
            a.Stack.Undo(a.Doc);
            Assert.Single(a.Doc.Placements);
            Assert.Empty(b.Doc.Placements);
            Assert.NotSame(a.Board, b.Board);
        });
    }

    [Fact]
    public void A_sessions_unsaved_state_is_its_own()
    {
        RunSta(() =>
        {
            var a = Session();
            var b = Session();
            a.Doc = new ShipDocument(Cat);
            b.Doc = new ShipDocument(Cat);

            // StateDirty is the non-command half of unsaved (ship identity, view orientation), so it has to be per
            // design too, or renaming one ship stars the other.
            a.StateDirty = true;
            Assert.True(a.Dirty);
            Assert.False(b.Dirty);
        });
    }

    [Fact]
    public void The_tab_label_is_the_file_name_once_there_is_a_file()
    {
        RunSta(() =>
        {
            var s = Session();
            s.Doc = new ShipDocument(Cat);
            s.Meta = new OplanMeta { Name = "Vagabond+" };
            Assert.Equal("Vagabond+", s.DisplayName);   // never saved: the design name

            s.Doc.FilePath = @"D:\ships\Kestrel.oplan";
            Assert.Equal("Kestrel", s.DisplayName);     // saved: the file, which is what Ctrl+S writes
        });
    }

    [Fact]
    public void Detaching_a_document_stops_that_sessions_notifications()
    {
        RunSta(() =>
        {
            var s = Session();
            var doc = new ShipDocument(Cat);
            var fired = 0;

            s.Doc = doc;
            s.DocChanged = () => fired++;
            doc.Changed += s.DocChanged;

            new PlaceCommand(Wall(0, 0)).Do(doc);
            Assert.Equal(1, fired);

            s.DetachDoc();
            new PlaceCommand(Wall(1, 0)).Do(doc);
            Assert.Equal(1, fired);   // the old document can go on changing; this session is no longer listening
        });
    }

    /// <summary>
    /// The clipboard is app-wide, not per session, so a selection copied in one tab pastes into another. What this
    /// pins is that the payload carries nothing of the design it came from: the same clipboard pasted into two
    /// designs has to give each its own placements and its own cargo identity, or editing one ship would reach into
    /// the other. The cloning is done at paste time for exactly that reason.
    /// </summary>
    [Fact]
    public void One_clipboard_pastes_into_two_designs_without_them_sharing_anything()
    {
        var cargo = new CargoItem("original-id", "TestRation", "Ration Bar", false, []);
        List<(string, int, int, int, IReadOnlyList<CargoItem>)> clip =
        [
            ("TestLocker", 0, 0, 0, [cargo]),
            ("TestWall", 1, 0, 90, []),
        ];

        var a = new ShipDocument(Cat);
        var b = new ShipDocument(Cat);

        // The same copy, pasted at a different spot in each design. A paste anchors on the cursor, so the
        // anchor is taken verbatim and nothing is added to it — there is no nudge to reason about any more.
        var intoA = MainWindow.ClipboardClones(clip, (10, 5));
        var intoB = MainWindow.ClipboardClones(clip, (-3, 0));
        foreach (var p in intoA) new PlaceCommand(p).Do(a);
        foreach (var p in intoB) new PlaceCommand(p).Do(b);

        // Relative offsets kept, anchored where each paste asked.
        Assert.Equal((10, 5), (intoA[0].X, intoA[0].Y));
        Assert.Equal((11, 5), (intoA[1].X, intoA[1].Y));
        Assert.Equal((-3, 0), (intoB[0].X, intoB[0].Y));
        Assert.Equal(90, intoB[1].Rot);

        // Nothing shared: not the placements, not the cargo, and not the ids the save write-back keys on.
        Assert.NotSame(intoA[0], intoB[0]);
        Assert.NotSame(intoA[0].Cargo[0], intoB[0].Cargo[0]);
        Assert.NotEqual(intoA[0].Cargo[0].StrID, intoB[0].Cargo[0].StrID);
        Assert.NotEqual("original-id", intoA[0].Cargo[0].StrID);   // and neither reuses the copied item's identity
        Assert.NotEqual("original-id", intoB[0].Cargo[0].StrID);
        Assert.Equal("Ration Bar", intoB[0].Cargo[0].Friendly);    // everything else survives the clone

        Assert.Equal(2, a.Placements.Count);
        Assert.Equal(2, b.Placements.Count);
    }

    /// <summary>A session owns a WPF canvas, and a WPF control can only be constructed on an STA thread.</summary>
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
