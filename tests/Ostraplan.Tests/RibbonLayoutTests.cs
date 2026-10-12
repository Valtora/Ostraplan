using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Ostraplan.App;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The ribbon's one-row or two-row arrangement (#49), run on the real window's XAML at every width a screen could
/// give it, and the tooltips that name its icons. The window is never shown, so the ribbon grid is measured and arranged directly and
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]   // Settings ▸ Toolbar labels puts names beside the icons, and every button widens
    public void The_arrangement_settles_at_every_width(bool labels)
    {
        RunSta(() =>
        {
            var w = new MainWindow();
            w.ApplyToolbarLabels(labels);
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

    /// <summary>
    /// Most of the toolbar is icons, so a tooltip is the only thing that says what a button is. Every control has one,
    /// and every icon opens its tooltip with its own name. The title is also what keeps an icon toggle's tooltip from
    /// being a bare string, which the toggle styles' string template would otherwise draw as an empty icon.
    /// </summary>
    [Fact]
    public void Every_toolbar_control_explains_itself()
    {
        RunSta(() =>
        {
            var w = new MainWindow();
            var buttons = Logical((DependencyObject)w.FindName("Ribbon")).OfType<ButtonBase>().ToList();
            Assert.True(buttons.Count > 20, $"Found only {buttons.Count} toolbar buttons.");

            foreach (var b in buttons)
            {
                Assert.False(string.IsNullOrWhiteSpace(ToolIcon.TipText(b)), $"{b.Name} has no tooltip.");
                if (ToolIcon.GetIcon(b) is null) continue;

                var name = (string)b.Content;
                Assert.True(ToolIcon.GetTitle(b)?.StartsWith(name) == true,
                    $"{b.Name} shows an icon but its tooltip does not open with \"{name}\".");
                Assert.IsAssignableFrom<Panel>(b.ToolTip);
            }
            w.Close();
        });
    }

    private static IEnumerable<DependencyObject> Logical(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var d in Logical(child)) yield return d;
        }
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
