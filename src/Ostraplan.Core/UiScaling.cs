namespace Ostraplan.Core;

/// <summary>
/// The bounds of Ostraplan's UI scale (<see cref="AppSettings.UiScale"/>). The scale itself is applied by the app
/// (<c>Ostraplan.App.UiScale</c>, a layout transform on every window); what lives here is the policy every caller
/// has to agree on: the range the Settings slider offers, and the clamp that turns any stored or typed value into
/// a usable one.
///
/// <para>The floor is 100%: below it the app would render smaller than it was laid out for, and its fixed-size
/// dialogs start clipping. The ceiling is 200%, which is what a 27" 4K monitor run at 100% Windows scaling needs
/// to read like a 1080p one.</para>
/// </summary>
public static class UiScaling
{
    public const double Default = 1.0;
    public const double Min = 1.0;
    public const double Max = 2.0;

    /// <summary>The granularity of the Settings slider, and what <see cref="Clamp"/> snaps to (5%).</summary>
    public const double Step = 0.05;

    /// <summary>Bring any value (a stored setting, a typed box) into range and onto a <see cref="Step"/> boundary.
    /// A non-finite value is treated as unset and returns <see cref="Default"/>.</summary>
    public static double Clamp(double scale)
    {
        if (!double.IsFinite(scale)) return Default;
        var snapped = Math.Round(scale / Step, MidpointRounding.AwayFromZero) * Step;
        return Math.Round(Math.Clamp(snapped, Min, Max), 2);
    }

    /// <summary>"125%" — how a scale is written wherever the user sees it.</summary>
    public static string Percent(double scale) =>
        (scale * 100).ToString("0", System.Globalization.CultureInfo.CurrentCulture) + "%";
}
