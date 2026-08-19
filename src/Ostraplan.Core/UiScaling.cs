namespace Ostraplan.Core;

/// <summary>
/// The bounds of Ostraplan's UI scale (<see cref="AppSettings.UiScale"/>). The scale itself is applied by the app
/// (<c>Ostraplan.App.UiScale</c>, a layout transform on every window); what lives here is the policy every caller
/// has to agree on: the range the Settings slider offers, and the clamp that turns any stored or typed value into
/// a usable one.
///
/// <para>The ceiling is 200%, which is what a 27" 4K monitor run at 100% Windows scaling needs to read like a
/// 1080p one. The floor is 80%, for the opposite screen: below 100% the app lays itself out smaller than it was
/// designed for, which is how you fit two of its windows side by side, or its sidebars and toolbar into less of
/// a laptop panel. It is a conservative floor on purpose — the app's body text is 11pt, and 80% of that is about
/// as small as it can be set before it stops being readable at a normal viewing distance.</para>
/// </summary>
public static class UiScaling
{
    public const double Default = 1.0;
    public const double Min = 0.8;
    public const double Max = 2.0;

    /// <summary>The granularity of the Settings slider, and what <see cref="Clamp"/> snaps to (5%).</summary>
    public const double Step = 0.05;

    /// <summary>Bring any value (a stored setting, a typed box) into range and onto a <see cref="Step"/> boundary.
    /// A non-finite or non-positive value is treated as unset and returns <see cref="Default"/>.
    ///
    /// <para>Zero is the case that matters: a settings file written before the key existed deserializes to 0, not
    /// to the property's initializer, so it reaches here as a real number. It has to be caught before the clamp
    /// rather than by it — while the floor was 100% the clamp happened to produce the right answer, and it stops
    /// doing so the moment the floor moves, which would land every upgrading user at the floor.</para></summary>
    public static double Clamp(double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0) return Default;
        var snapped = Math.Round(scale / Step, MidpointRounding.AwayFromZero) * Step;
        return Math.Round(Math.Clamp(snapped, Min, Max), 2);
    }

    /// <summary>"125%" — how a scale is written wherever the user sees it.</summary>
    public static string Percent(double scale) =>
        (scale * 100).ToString("0", System.Globalization.CultureInfo.CurrentCulture) + "%";
}
