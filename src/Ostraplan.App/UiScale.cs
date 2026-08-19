using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// App-wide UI scaling: everything Ostraplan draws, magnified by a single factor the user sets in Settings.
///
/// <para>It exists for the high-DPI-at-100% case. A 27" 4K monitor run at 100% Windows scaling gives a
/// DPI-aware WPF app a 3840×2160 logical surface, so 11pt text renders about a third the physical size it does
/// on a 1080p panel. Windows' own scaling is the real fix, but plenty of people run at 100% deliberately and
/// want one app bigger rather than the desktop rescaled.</para>
///
/// <para><b>How.</b> A <see cref="ScaleTransform"/> as the <see cref="FrameworkElement.LayoutTransform"/> of each
/// window's root element. A layout transform is measured through, so the whole tree lays itself out at the larger
/// size rather than being blown up as a bitmap: text, vectors and the canvas all rasterise at the final scale and
/// stay crisp. It reaches every window, including the code-built dialogs, through a class handler on
/// <see cref="FrameworkElement.LoadedEvent"/> — no window has to know this class exists.</para>
///
/// <para><b>Window size.</b> Scaling the content of a fixed-size dialog would clip it, so a window's declared size
/// and its Min/Max constraints scale with it, clamped to the work area (a 200% scale must not produce a window
/// bigger than the screen) and re-centred on whatever it was centred on. Windows that size to their content need
/// none of that: they follow their content on their own.</para>
///
/// <para><b>Below 100% a window's box does not shrink.</b> The point of setting the scale under 100% is to fit
/// more into the window you already have, so only the Min constraints come down with it (a window whose content
/// needed 900px now needs 720, and must be allowed to go there). A declared size and the Max constraints scale by
/// <c>max(scale, 1)</c> instead: the main window opens at the size it was laid out for and spends the difference
/// on canvas rather than handing it back to the desktop, and a fixed-size report keeps its box and shows more
/// rows. The dialogs where a proportionally smaller window <i>is</i> the right answer are almost all
/// <see cref="SizeToContent"/> ones, which get there on their own.</para>
///
/// <para><b>The popup layer.</b> A popup renders in its own top-level window, so whether it picks the scale up
/// depends on where its content sits in the visual tree. A dropdown declared inside a control's template — a
/// ComboBox's list, a MenuItem's submenu — is a visual descendant of the element that owns it and inherits the
/// transform for free. A <see cref="ContextMenu"/> or a <see cref="ToolTip"/> is not: it is attached to a
/// placement target, which positions it but does not put it under that target in the tree, so it opened at 100%
/// however large the rest of the app was. <see cref="Install"/> therefore scales those two on
/// <see cref="ContextMenu.OpenedEvent"/> / <see cref="ToolTip.OpenedEvent"/> — the earliest per-open moment,
/// since <see cref="FrameworkElement.LoadedEvent"/> never fires for either. Scaling one after it opens is safe:
/// WPF re-fits a popup to the work area when its size changes, so a menu opened at the bottom of the screen
/// still lands on screen. Submenus need nothing of their own; they inherit from the menu that opened them.</para>
/// </summary>
public static class UiScale
{
    /// <summary>The live scale, 1.0 = 100%. Set through <see cref="Install"/> and <see cref="Apply"/>.</summary>
    public static double Scale { get; private set; } = UiScaling.Default;

    /// <summary>Each window's pre-scale size and constraints, captured the first time it is scaled so that a later
    /// change scales from the original rather than compounding. Weak, so a closed window is collectable.</summary>
    private static readonly ConditionalWeakTable<Window, Metrics> Bases = new();

    private static bool _installed;

    /// <summary>The size a window asked for before any scaling. <see cref="double.NaN"/> width/height means the
    /// window sizes to its content and must be left to do so.</summary>
    private sealed record Metrics(double Width, double Height, double MinWidth, double MinHeight, double MaxWidth, double MaxHeight)
    {
        public static Metrics Of(Window w) => new(w.Width, w.Height, w.MinWidth, w.MinHeight, w.MaxWidth, w.MaxHeight);
    }

    /// <summary>
    /// Set the startup scale and hook every window the app opens from here on, plus the two popup kinds that
    /// don't inherit it. Called once, from <see cref="App.OnStartup"/>, before the first window is created.
    /// </summary>
    public static void Install(double scale)
    {
        Scale = UiScaling.Clamp(scale);
        if (_installed) return;
        _installed = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => { if (sender is Window w) ApplyTo(w, resize: true); }));

        // Per open rather than once per instance: a menu is reopened over and over, and the scale may have moved
        // in Settings since the last time. Reading the live Scale here is also why nothing has to track them.
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(ScalePopup));
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler(ScalePopup));
    }

    /// <summary>Scale a popup that has just opened. See the popup-layer note on the class.</summary>
    private static void ScalePopup(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement popup) return;
        popup.LayoutTransform = Scale == UiScaling.Default ? Transform.Identity : Frozen(Scale);
    }

    /// <summary>
    /// Change the scale and re-scale what is already open. The content transform is refreshed on every window;
    /// the window <i>size</i> is only refreshed for the secondary windows, because the main window is one the
    /// user has sized and placed themselves and resizing it under them while they drag a slider is worse than
    /// leaving it (its own layout re-flows to the new scale either way).
    /// </summary>
    public static void Apply(double scale)
    {
        var next = UiScaling.Clamp(scale);
        if (Math.Abs(next - Scale) < 0.0001) return;
        Scale = next;

        if (Application.Current is not { } app) return;
        foreach (var w in app.Windows.OfType<Window>())
            ApplyTo(w, resize: !ReferenceEquals(w, app.MainWindow));
    }

    /// <summary>Scale one window: its content always, its size only when asked and only when it has a size of its
    /// own to scale.</summary>
    private static void ApplyTo(Window w, bool resize)
    {
        if (w.Content is not FrameworkElement root) return;

        root.LayoutTransform = Scale == UiScaling.Default ? Transform.Identity : Frozen(Scale);
        if (!resize) return;

        var b = Bases.GetValue(w, Metrics.Of);
        var work = SystemParameters.WorkArea;

        // What a window's own box scales by. Never below 1: shrinking the box under 100% would hand the reclaimed
        // space back to the desktop, which is the opposite of what setting the scale down is for. See the class note.
        var grow = Math.Max(Scale, UiScaling.Default);

        // Constraints scale whatever the sizing mode: a size-to-content window is still held by its Max*. The floor
        // follows the content all the way down, so a smaller layout can actually be dragged smaller.
        if (b.MinWidth > 0) w.MinWidth = Math.Min(b.MinWidth * Scale, work.Width);
        if (b.MinHeight > 0) w.MinHeight = Math.Min(b.MinHeight * Scale, work.Height);
        if (double.IsFinite(b.MaxWidth)) w.MaxWidth = Math.Min(b.MaxWidth * grow, work.Width);
        if (double.IsFinite(b.MaxHeight)) w.MaxHeight = Math.Min(b.MaxHeight * grow, work.Height);

        // Per dimension, because SizeToContent is per dimension: the common "fixed width, height follows the
        // content" dialog still needs its width scaled or the content is squeezed into the old column. A window
        // the user has maximised has no size of its own to scale at all.
        if (w.WindowState == WindowState.Normal)
        {
            var stc = w.SizeToContent;
            if (stc is not (SizeToContent.Width or SizeToContent.WidthAndHeight) && double.IsFinite(b.Width))
                w.Width = Math.Min(b.Width * grow, work.Width);
            if (stc is not (SizeToContent.Height or SizeToContent.WidthAndHeight) && double.IsFinite(b.Height))
                w.Height = Math.Min(b.Height * grow, work.Height);
        }

        if (Scale != UiScaling.Default) Recentre(w);
    }

    /// <summary>
    /// Put a resized window back where its <see cref="Window.WindowStartupLocation"/> said it should be. WPF
    /// centred it before Loaded ran, against the size it no longer has, so a scaled dialog would otherwise sit
    /// low and right of centre — and at a large scale, partly off screen. Runs after the resize has been laid
    /// out, which is the first moment the real size is known.
    /// </summary>
    private static void Recentre(Window w)
    {
        if (w.WindowStartupLocation == WindowStartupLocation.Manual || w.WindowState != WindowState.Normal) return;

        w.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (w.WindowState != WindowState.Normal || w.ActualWidth <= 0) return;

            var work = SystemParameters.WorkArea;
            var over = w.WindowStartupLocation == WindowStartupLocation.CenterOwner
                       && w.Owner is { WindowState: WindowState.Normal, ActualWidth: > 0 } owner
                ? new Rect(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight)
                : work;

            w.Left = Clamp(over.Left + (over.Width - w.ActualWidth) / 2, work.Left, work.Right - w.ActualWidth);
            w.Top = Clamp(over.Top + (over.Height - w.ActualHeight) / 2, work.Top, work.Bottom - w.ActualHeight);
        });
    }

    /// <summary>Math.Clamp, but tolerant of a window larger than the space it is being fitted into (which happens
    /// at a large scale on a small screen) — there, pin it to the near edge rather than throwing.</summary>
    private static double Clamp(double value, double min, double max) =>
        max < min ? min : Math.Clamp(value, min, max);

    private static Transform Frozen(double scale)
    {
        var t = new ScaleTransform(scale, scale);
        t.Freeze();
        return t;
    }
}
