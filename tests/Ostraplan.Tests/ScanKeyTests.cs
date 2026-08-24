using System.Threading;
using System.Windows;
using Ostraplan.App;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The editor skips the off-thread analysis when the <see cref="ScanKey"/> matches the last one that finished.
/// These pin the cases where it must NOT match, because a wrong skip leaves an overlay with nothing to draw and
/// nothing on screen to say why.
///
/// <para>They build the key through <see cref="ScanKey.For"/>, the same call the window makes, so a key that
/// stops covering something fails here rather than passing against a copy of the old logic.</para>
/// </summary>
public class ScanKeyTests
{
    private static void RunSta(Action a)
    {
        Exception? err = null;
        var t = new Thread(() => { try { a(); } catch (Exception e) { err = e; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (err is not null) throw err;
    }

    private static (ShipDocument Doc, ShipCanvas Board, AppSettings Settings) Editor()
    {
        var cat = new Fixtures().Floor("Floor").Wall("Wall").Build();
        var doc = Fixtures.Doc(cat, Fixtures.P("Floor", 0, 0), Fixtures.P("Wall", 0, 1));
        var board = new ShipCanvas();
        board.SetDocument(doc);
        return (doc, board, new AppSettings());
    }

    /// <summary>
    /// The regression. Switching an overlay off throws its result away, and only switching one ON schedules a
    /// scan, so the key taken when it comes back must differ from the one recorded while it was last on. It did
    /// not: every term matched, the scan was skipped as redundant, and Light Viz came back with no lights.
    /// </summary>
    [Theory]
    [InlineData("light")]
    [InlineData("power")]
    [InlineData("rooms")]
    [InlineData("walk")]
    [InlineData("access")]
    public void Toggling_an_overlay_off_and_on_does_not_match_the_scan_taken_while_it_was_on(string overlay)
    {
        RunSta(() =>
        {
            var (doc, board, settings) = Editor();
            void Set(bool on)
            {
                switch (overlay)
                {
                    case "light": board.SetShowLight(on); break;
                    case "power": board.SetShowPower(on); break;
                    case "rooms": board.SetShowRooms(on); break;
                    case "walk": board.SetShowWalk(on); break;
                    default: board.SetShowAccess(on); break;
                }
            }

            Set(true);
            var whileOn = ScanKey.For(doc, board, settings);   // the scan that ran and was remembered

            Set(false);
            Set(true);
            Assert.NotEqual(whileOn, ScanKey.For(doc, board, settings));
        });
    }

    /// <summary>WalkViz and Access read one analysis, so dropping it takes both switches. Turning one off while
    /// the other still wants it keeps the result, and the key stays put rather than forcing a pointless
    /// re-analysis.</summary>
    [Fact]
    public void Walk_and_Access_only_discard_when_neither_wants_the_analysis()
    {
        RunSta(() =>
        {
            var (doc, board, settings) = Editor();
            board.SetShowWalk(true);
            board.SetShowAccess(true);
            var both = ScanKey.For(doc, board, settings);

            board.SetShowAccess(false);   // WalkViz still holds it, so nothing is discarded
            board.SetShowAccess(true);
            Assert.Equal(both, ScanKey.For(doc, board, settings));
        });
    }

    [Fact]
    public void An_edit_the_analysis_can_see_does_not_match()
    {
        RunSta(() =>
        {
            var (doc, board, settings) = Editor();
            var before = ScanKey.For(doc, board, settings);
            new PlaceCommand(Fixtures.P("Floor", 4, 4)).Do(doc);
            Assert.NotEqual(before, ScanKey.For(doc, board, settings));
        });
    }

    [Fact]
    public void An_edit_it_cannot_see_matches()
    {
        RunSta(() =>
        {
            var (doc, board, settings) = Editor();
            var before = ScanKey.For(doc, board, settings);
            new SetCustomNameCommand(doc.Placements[0], null, "Deck plate").Do(doc);
            Assert.Equal(before, ScanKey.For(doc, board, settings));
        });
    }

    /// <summary>The walk pass reads these off settings rather than off the design, so changing one has to
    /// re-analyse a design that has not otherwise moved.</summary>
    [Fact]
    public void Changing_a_walk_setting_does_not_match()
    {
        RunSta(() =>
        {
            var (doc, board, settings) = Editor();
            board.SetShowWalk(true);
            var before = ScanKey.For(doc, board, settings);
            settings.WalkIncludeExterior = !settings.WalkIncludeExterior;
            Assert.NotEqual(before, ScanKey.For(doc, board, settings));
        });
    }

    /// <summary>Two designs are never the same scan, however alike they look: the overlays land on the canvas the
    /// scan was taken for.</summary>
    [Fact]
    public void A_different_design_never_matches()
    {
        RunSta(() =>
        {
            var (doc, board, settings) = Editor();
            var cat = new Fixtures().Floor("Floor").Wall("Wall").Build();
            var twin = Fixtures.Doc(cat, Fixtures.P("Floor", 0, 0), Fixtures.P("Wall", 0, 1));
            Assert.NotEqual(ScanKey.For(doc, board, settings), ScanKey.For(twin, board, settings));
        });
    }
}
