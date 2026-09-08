using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ostraplan.Core;

/// <summary>How the main window opens (<see cref="AppSettings.WindowOpenAs"/>).</summary>
public enum WindowOpenAs
{
    /// <summary>At the size, position and maximised state it was last closed at. The default.</summary>
    Last,

    /// <summary>Maximised every launch, whatever it was closed at. The restore-down size is still remembered, so
    /// un-maximising gives back the window that was there rather than the app's shipped default.</summary>
    Maximised,
}

/// <summary>
/// The main window's geometry, remembered between sessions (#69). The app applies it
/// (<c>MainWindow.RestoreWindowPlacement</c>); what lives here is the policy every caller has to agree on, which is
/// the same split <see cref="UiScaling"/> makes.
///
/// <para><b>Why a stored rectangle needs checking at all.</b> A desktop is not the same shape from one launch to
/// the next. A laptop closed while docked to a second monitor opens on its own panel with that monitor's whole
/// coordinate range gone, and a window restored to where it was is then off the edge of everything, with no title
/// bar to drag it back by. <see cref="FitTo"/> is the answer to that: it takes the desktop as it is now and returns
/// either a placement that lands on it or null, and null means open where the app would have opened anyway.</para>
///
/// <para><b>It nudges as little as it can.</b> The position is either kept exactly or refused outright, rather than
/// being slid onto the nearest screen. The desktop is measured as the <i>bounding box</i> of every monitor, which
/// on an L-shaped arrangement contains dead space no window can sit in, so a nudge computed against it is a guess
/// that can be worse than what it replaced. The one exception is the top edge, which is clamped down onto the
/// desktop: a window whose title bar is above the desktop cannot be dragged, and that is not recoverable by hand.</para>
/// </summary>
public sealed class WindowPlacement
{
    /// <summary>The smallest window a restore may produce, whatever is in the file. Below this the editor's own
    /// chrome does not fit, and a hand-edited or corrupt settings file must not be able to ask for it.</summary>
    public const double MinWidth = 900;
    public const double MinHeight = 600;

    /// <summary>How much of the window has to land on the desktop, in each axis, for the stored position to be
    /// used. Enough to leave a grabbable piece of title bar rather than a sliver.</summary>
    public const double MinVisible = 120;

    [JsonPropertyName("left")] public double Left { get; set; }
    [JsonPropertyName("top")] public double Top { get; set; }
    [JsonPropertyName("width")] public double Width { get; set; }
    [JsonPropertyName("height")] public double Height { get; set; }

    /// <summary>True when the window was maximised. The other four are then its restore-down bounds, which is what
    /// makes un-maximising after a restore give back the window the user had.</summary>
    [JsonPropertyName("maximised")] public bool Maximised { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>
    /// This placement brought onto the desktop as it now is, or null when there is nothing usable in it and the
    /// window should open where it would have without any of this.
    /// </summary>
    /// <param name="screenLeft">The desktop's left edge (WPF: <c>SystemParameters.VirtualScreenLeft</c>). Negative
    /// on a multi-monitor desktop with a screen to the left of the primary one.</param>
    /// <param name="screenTop">The desktop's top edge.</param>
    /// <param name="screenWidth">The desktop's full width across every monitor.</param>
    /// <param name="screenHeight">The desktop's full height.</param>
    public WindowPlacement? FitTo(double screenLeft, double screenTop, double screenWidth, double screenHeight)
    {
        if (!double.IsFinite(Left) || !double.IsFinite(Top)) return null;
        if (!double.IsFinite(Width) || !double.IsFinite(Height)) return null;
        if (Width <= 0 || Height <= 0) return null;   // a settings file written before this existed reads as 0,0
        if (!double.IsFinite(screenWidth) || !double.IsFinite(screenHeight)) return null;
        if (screenWidth <= 0 || screenHeight <= 0) return null;

        // The floor first, then the ceiling: on a screen smaller than the floor the screen wins, because a window
        // larger than the desktop cannot be worked with at all.
        var width = Math.Min(Math.Max(Width, MinWidth), screenWidth);
        var height = Math.Min(Math.Max(Height, MinHeight), screenHeight);

        // Below the desktop's top edge, never above it. See the class note on why this is the only nudge.
        var top = Math.Max(Top, screenTop);

        var visibleX = Math.Min(Left + width, screenLeft + screenWidth) - Math.Max(Left, screenLeft);
        var visibleY = Math.Min(top + height, screenTop + screenHeight) - Math.Max(top, screenTop);
        if (visibleX < Math.Min(MinVisible, width) || visibleY < Math.Min(MinVisible, height)) return null;

        return new WindowPlacement
        {
            Left = Left, Top = top, Width = width, Height = height, Maximised = Maximised,
        };
    }

    /// <summary>Parse the stored <see cref="AppSettings.WindowOpenAs"/> name. Stored by name like the surface and
    /// spawner modes, so a value from a newer build reads as the default rather than as whatever integer lands in
    /// range.</summary>
    public static WindowOpenAs ParseOpenAs(string? name) =>
        Enum.TryParse<WindowOpenAs>(name, ignoreCase: true, out var parsed) ? parsed : WindowOpenAs.Last;
}
