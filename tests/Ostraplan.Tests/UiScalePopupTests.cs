using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ostraplan.App;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// UI scale reaching the popup layer (issue #25: the right-click menu opened at 100% however large the rest of the
/// app was).
/// <para>
/// The scale is a <see cref="FrameworkElement.LayoutTransform"/> on each window's root, and a popup renders in its
/// own top-level window. Whether it inherits depends on where its content sits in the visual tree: a dropdown
/// declared in a control's template is a visual descendant of that control and inherits for free, but a
/// <see cref="ContextMenu"/> is attached to a <i>placement target</i>, which positions it without putting it under
/// that target — so it inherits nothing. <see cref="UiScale.Install"/> scales those on open instead.
/// </para>
/// Everything here needs STA: a WPF control can only be built on an STA thread.
/// </summary>
public class UiScalePopupTests
{
    private static ContextMenu Menu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Something" });
        return menu;
    }

    /// <summary>The scale factor a popup's own transform applies, or 1 when it carries none.</summary>
    private static double ScaleOf(FrameworkElement popup)
    {
        var t = popup.LayoutTransform;
        return t is null ? 1.0 : t.Value.M11;
    }

    [Fact]
    public void An_opened_context_menu_carries_the_ui_scale()
    {
        RunSta(() =>
        {
            UiScale.Install(1.5);
            var menu = Menu();
            Assert.Equal(1.0, ScaleOf(menu));   // nothing applied until it opens

            menu.IsOpen = true;
            Assert.Equal(1.5, ScaleOf(menu), 3);
            menu.IsOpen = false;
        });
    }

    [Fact]
    public void Reopening_the_same_menu_picks_up_a_scale_changed_since()
    {
        RunSta(() =>
        {
            // The menus the inventory viewer builds live on their host element and are reopened, so a per-instance
            // hook would pin the scale they had the first time the user right-clicked.
            UiScale.Install(1.5);
            var menu = Menu();
            menu.IsOpen = true;
            Assert.Equal(1.5, ScaleOf(menu), 3);
            menu.IsOpen = false;

            UiScale.Apply(2.0);
            menu.IsOpen = true;
            Assert.Equal(2.0, ScaleOf(menu), 3);
            menu.IsOpen = false;
        });
    }

    [Fact]
    public void At_one_hundred_percent_a_menu_carries_no_transform_at_all()
    {
        RunSta(() =>
        {
            UiScale.Install(1.0);
            var menu = Menu();
            menu.IsOpen = true;
            Assert.Same(Transform.Identity, menu.LayoutTransform);
            menu.IsOpen = false;
        });
    }

    [Fact]
    public void A_tooltip_is_scaled_the_same_way()
    {
        RunSta(() =>
        {
            // Same defect, same cure: a tooltip is attached to a placement target too.
            UiScale.Install(1.75);
            var tip = new ToolTip { Content = "Something" };
            tip.IsOpen = true;
            Assert.Equal(1.75, ScaleOf(tip), 3);
            tip.IsOpen = false;
        });
    }

    /// <summary>WPF controls can only be constructed on an STA thread. Runs one at a time: the scale is process-wide
    /// state, so overlapping these would have them read each other's.</summary>
    private static void RunSta(Action action)
    {
        lock (Gate)
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

    private static readonly Lock Gate = new();
}
