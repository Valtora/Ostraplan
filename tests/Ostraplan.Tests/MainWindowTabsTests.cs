using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Ostraplan.App;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The main window's document tabs, exercised on the real window rather than on a session in isolation. Everything
/// here is what a launch would hit in its first second: the constructor now builds a session before anything else
/// can read the per-document state through it, and getting that order wrong is a crash on start rather than a
/// misbehaviour anybody could report.
///
/// <para>Constructing the window does not load game data — that happens on <see cref="FrameworkElement.Loaded"/>,
/// which is never raised here — so this stays game-free. STA, because it is WPF.</para>
/// </summary>
public class MainWindowTabsTests
{
    [Fact]
    public void The_window_opens_on_exactly_one_design_with_the_tab_strip_hidden()
    {
        RunSta(() =>
        {
            var w = new MainWindow();

            var strip = (Border)w.FindName("DocTabBar");
            var host = (Grid)w.FindName("CanvasHost");

            // One session, so one canvas in the host and no strip: a single-document session looks exactly as it
            // did before tabs existed.
            Assert.Single(host.Children);
            Assert.IsType<ShipCanvas>(host.Children[0]);
            Assert.Equal(Visibility.Visible, ((UIElement)host.Children[0]).Visibility);
            Assert.Equal(Visibility.Collapsed, strip.Visibility);

            w.Close();
        });
    }

    [Fact]
    public void Opening_more_designs_shows_the_strip_and_leaves_one_canvas_visible()
    {
        RunSta(() =>
        {
            var w = new MainWindow();
            var strip = (Border)w.FindName("DocTabBar");
            var host = (Grid)w.FindName("CanvasHost");
            var first = w.ActiveSession;

            var second = w.CreateSession();
            var third = w.CreateSession();
            w.ActivateSession(third);

            Assert.Equal(3, w.OpenSessions.Count);
            Assert.Equal(3, host.Children.Count);       // every open design keeps its own canvas, all in the host
            Assert.Equal(Visibility.Visible, strip.Visibility);
            AssertOnlyVisible(host, third);

            // Ctrl+Tab wraps, in both directions.
            w.CycleSession(1);
            Assert.Same(first, w.ActiveSession);
            w.CycleSession(-1);
            Assert.Same(third, w.ActiveSession);
            w.CycleSession(-1);
            Assert.Same(second, w.ActiveSession);

            w.Close();
        });
    }

    [Fact]
    public void Closing_a_tab_falls_back_to_its_neighbour_and_the_last_one_will_not_close()
    {
        RunSta(() =>
        {
            var w = new MainWindow();
            var strip = (Border)w.FindName("DocTabBar");
            var host = (Grid)w.FindName("CanvasHost");
            var first = w.ActiveSession;
            var second = w.CreateSession();
            var third = w.CreateSession();

            // None of these has a document yet, so nothing is dirty and no save prompt can appear.
            w.CloseSession(second);
            Assert.Equal(2, w.OpenSessions.Count);
            Assert.Equal(2, host.Children.Count);      // its canvas left the window with it
            Assert.Same(third, w.ActiveSession);       // the tab that took its place in the strip
            AssertOnlyVisible(host, third);

            w.CloseSession(third);
            Assert.Same(first, w.ActiveSession);
            Assert.Equal(Visibility.Collapsed, strip.Visibility);   // back to one design, back to no strip

            // The editor always has a document in it: closing the last design is refused rather than emptying it.
            w.CloseSession(first);
            Assert.Single(w.OpenSessions);
            Assert.Same(first, w.ActiveSession);

            w.Close();
        });
    }

    /// <summary>
    /// The inspector's PART name is the rename field (#30), so what it will accept has to follow the selection:
    /// a lone placement can be typed over, and anything else (nothing selected, several parts) is a plain
    /// read-only line. Focus and typing need a shown window, so this covers the wiring rather than the commit;
    /// what a commit makes of the text is <see cref="RenameTests.Typing_the_stock_name_back_means_no_name_at_all"/>.
    /// </summary>
    [Fact]
    public void The_inspector_name_is_editable_only_for_a_lone_selected_part()
    {
        RunSta(() =>
        {
            var w = new MainWindow();
            var box = (TextBox)w.FindName("InsFriendly");
            var cat = new Fixtures().Part("Rack", container: (6, 6)).Build();
            var one = new Placement { DefName = "Rack", X = 0, Y = 0 };
            var two = new Placement { DefName = "Rack", X = 2, Y = 0 };
            var doc = Fixtures.Doc(cat, one, two);

            var session = w.ActiveSession;
            session.Doc = doc;
            session.Board.SetDocument(doc);

            session.Board.SetSelection([one]);
            Assert.False(box.IsReadOnly);
            Assert.True(box.Focusable);
            Assert.Equal("Rack", box.Text);

            session.Board.SetSelection([one, two]);
            Assert.True(box.IsReadOnly);
            Assert.False(box.Focusable);
            Assert.Equal("2 parts selected", box.Text);

            session.Board.SetSelection([]);
            Assert.True(box.IsReadOnly);
            Assert.False(box.Focusable);

            w.Close();
        });
    }

    /// <summary>
    /// A tab whose name is too long for it gives way on the name and keeps its ✕ (#35). The ✕ used to be pushed
    /// clean off the end of the tab — at the app's own font a 40-character name put it 27px past a 240px tab — and
    /// with no ✕ on screen there is no way to close that design with the mouse at all.
    ///
    /// <para>The short-named tab is asserted alongside it, because the obvious fix (a Grid with a * column) makes
    /// every tab the full 240 wide.</para>
    /// </summary>
    [Fact]
    public void A_long_tab_name_gives_way_rather_than_pushing_the_close_button_out_of_the_tab()
    {
        RunSta(() =>
        {
            var w = new MainWindow();
            var strip = (StackPanel)w.FindName("DocTabStrip");

            // A real apartment's name: the game builds it as "<station> | <designation>" and both halves come from
            // stock data (K-Leg: Port Azikiwe is OKLG's public name, Asteroid Residence is ResOKLG01's).
            var second = w.CreateSession();
            second.Meta = new OplanMeta { Name = "K-Leg: Port Azikiwe | Asteroid Residence" };
            w.ActivateSession(second);

            // Nothing has laid the strip out — the window is never shown — so measure it the way its horizontally
            // scrolling ScrollViewer does, unconstrained, and let the DocTab style's MaxWidth do the clamping.
            var tabs = strip.Children.OfType<ToggleButton>().ToList();
            foreach (var t in tabs)
            {
                t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                t.Arrange(new Rect(t.DesiredSize));
            }

            var (shortTab, longTab) = (tabs[0], tabs[1]);
            Assert.True(shortTab.ActualWidth < longTab.ActualWidth,
                $"the short-named tab is {shortTab.ActualWidth} wide, as wide as the long one — tabs stopped "
                + "sizing to their content");

            // The room the tab gives its content is the ContentPresenter the template puts the row in. Past its
            // right edge the ✕ is under the tab's own border at best, and off the tab entirely at worst.
            var row = (FrameworkElement)longTab.Content;
            var slot = (FrameworkElement)VisualTreeHelper.GetParent(row);
            var close = FindClose(longTab);
            var right = close.TransformToAncestor(slot).Transform(new Point(close.ActualWidth, 0)).X;

            Assert.True(right <= slot.ActualWidth + 0.5,   // half a pixel for rounding, not for a whole ✕
                $"the ✕ ends at {right} in a tab with only {slot.ActualWidth} of room for its content");

            w.Close();
        });
    }

    /// <summary>An apartment's tab shows the designation alone. The game names a bought residence
    /// "&lt;station&gt; | &lt;designation&gt;", and the station half is both the longer half and the half every
    /// apartment at that station repeats. A ship keeps whatever it is called, pipe and all.</summary>
    [Fact]
    public void An_apartment_tab_drops_the_station_it_hangs_off()
    {
        RunSta(() =>
        {
            var w = new MainWindow();
            var session = w.ActiveSession;
            session.Meta = new OplanMeta { Name = "K-Leg: Port Azikiwe | Asteroid Residence" };
            session.Doc = Fixtures.Doc(new Fixtures().Part("Rack", container: (6, 6)).Build());

            session.Doc.Kind = DocumentKind.Residence;
            Assert.Equal("Asteroid Residence", session.TabName);

            session.Doc.Kind = DocumentKind.Ship;
            Assert.Equal("K-Leg: Port Azikiwe | Asteroid Residence", session.TabName);

            // A residence named after its station alone — no designation on the design — has nothing to drop.
            session.Doc.Kind = DocumentKind.Residence;
            session.Meta = new OplanMeta { Name = "K-Leg: Port Azikiwe" };
            Assert.Equal("K-Leg: Port Azikiwe", session.TabName);

            w.Close();
        });
    }

    /// <summary>The tab's ✕, wherever in the tab's visuals it has been put — the assertion is about where it lands,
    /// not about which panel the row happens to be built from.</summary>
    private static TextBlock FindClose(DependencyObject root) =>
        FindCloseOrNull(root) ?? throw new Xunit.Sdk.XunitException("the tab has no ✕ in it at all");

    private static TextBlock? FindCloseOrNull(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock { Text: "✕" } close) return close;
            if (FindCloseOrNull(child) is { } deeper) return deeper;
        }
        return null;
    }

    /// <summary>Exactly one canvas on screen, and it is the one belonging to <paramref name="expected"/>.</summary>
    private static void AssertOnlyVisible(Grid host, DocumentSession expected)
    {
        foreach (ShipCanvas canvas in host.Children)
            Assert.Equal(ReferenceEquals(canvas, expected.Board) ? Visibility.Visible : Visibility.Hidden,
                canvas.Visibility);
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
