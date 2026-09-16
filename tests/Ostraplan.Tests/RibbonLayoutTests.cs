using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Ostraplan.App;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The ribbon's one-row or two-row arrangement (#49), run on the real window's XAML at every width a screen could
/// give it. The window is never shown, so the ribbon grid is measured and arranged directly and
/// <see cref="MainWindow.RelayoutRibbon"/> is called where its SizeChanged handler would be. The button widths come
/// from the unthemed controls rather than the Fluent ones, which moves where the switch happens but not whether the
/// arrangement it picks is stable. STA, because it is WPF.
/// </summary>
public class RibbonLayoutTests
{
    /// <summary>
    /// A maximised window whose width fell in a narrow band flipped between one row and two about twice a second, and
    /// the whole app jittered with it (#75). A settled layout has to stay settled when nothing about the window changed.
    /// </summary>
    [Fact]
    public void The_arrangement_settles_at_every_width()
    {
        RunSta(() =>
        {
            var w = new MainWindow();
            var grid = (Grid)w.FindName("RibbonGrid");
            var toggles = (StackPanel)w.FindName("RibbonToggles");

            var unstable = new List<double>();
            var rowsSeen = new HashSet<int>();
            for (var width = 400.0; width <= 3000; width++)
            {
                Settle(w, grid, width);
                var before = Grid.GetRow(toggles);
                rowsSeen.Add(before);
                Pass(w, grid, width);
                if (Grid.GetRow(toggles) != before) unstable.Add(width);
            }

            Assert.Equal(2, rowsSeen.Count);   // the sweep crossed the switch, so a stable result means something
            Assert.True(unstable.Count == 0,
                $"The ribbon flips between one row and two at {unstable.Count} widths, from {unstable.FirstOrDefault()} to {unstable.LastOrDefault()}.");
            w.Close();
        });
    }

    /// <summary>
    /// With the toggles on their own row, the design's name was centred across both rows and sat on top of them (#75).
    /// </summary>
    [Fact]
    public void On_two_rows_the_design_name_sits_clear_of_the_toggles()
    {
        RunSta(() =>
        {
            var w = new MainWindow();
            var grid = (Grid)w.FindName("RibbonGrid");
            var toggles = (StackPanel)w.FindName("RibbonToggles");
            var identity = (StackPanel)w.FindName("RibbonIdentity");
            ((TextBlock)w.FindName("TxtDoc")).Text = "Untitled ship";
            var byline = (TextBlock)w.FindName("TxtDocByline");
            byline.Text = "Make Model · DESIGNATION";
            byline.Visibility = Visibility.Visible;

            Settle(w, grid, 900);
            Assert.Equal(1, Grid.GetRow(toggles));   // narrow enough that the toggles have dropped

            var name = identity.TranslatePoint(new Point(0, 0), grid);
            var row = toggles.TranslatePoint(new Point(0, 0), grid);
            Assert.True(name.Y + identity.ActualHeight <= row.Y,
                $"The name block ends at {name.Y + identity.ActualHeight} but the toggles start at {row.Y}.");
            w.Close();
        });
    }

    private static void Settle(MainWindow w, Grid grid, double width)
    {
        // Enough passes for any stable layout to reach its resting arrangement; an unstable one is still flipping.
        for (var i = 0; i < 4; i++) Pass(w, grid, width);
    }

    private static void Pass(MainWindow w, Grid grid, double width)
    {
        grid.Measure(new Size(width, double.PositiveInfinity));
        grid.Arrange(new Rect(0, 0, width, grid.DesiredSize.Height));
        w.RelayoutRibbon();
        grid.Measure(new Size(width, double.PositiveInfinity));
        grid.Arrange(new Rect(0, 0, width, grid.DesiredSize.Height));
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
