namespace Ostraplan.Core;

/// <summary>
/// The pure geometry behind symmetric placement: a pose plus its mirror copies across a vertical and/or
/// horizontal axis. Extracted from the canvas so the reflection/rotation math is unit-testable without WPF — it
/// is the part that has to be exactly right for symmetry to be trustworthy on every ship size and rotation.
///
/// <para>Reflection is about the <b>centre</b> of the axis tile (<paramref name="cx"/>/<paramref name="cy"/>),
/// matching the drawn axis line: a part's span reflects as a whole, so its rotated footprint <paramref name="w"/>×
/// <paramref name="h"/> is unchanged and its new top-left is <c>2·centre − (topLeft + size − 1)</c>. Mirrored
/// rotations follow the axis: a vertical axis flips East/West (<c>rot' = 360 − rot</c>), a horizontal axis flips
/// North/South (<c>rot' = 180 − rot</c>), both is a 180° turn — all normalised to {0,90,180,270}. A 90°/270° pose
/// keeps its footprint dimensions under every mirror, so one <paramref name="w"/>×<paramref name="h"/> drives all
/// copies.</para>
///
/// <para>Poses are emitted <b>cursor-first</b>. Copies that coincide with an earlier one (a part sitting on an
/// axis mirrors onto itself) are emitted as duplicates by design — the caller dedups by pose so the preview and
/// the placement agree exactly.</para>
/// </summary>
public static class Symmetry
{
    /// <summary>
    /// The pose <c>(x, y, rot)</c> for a part with rotated footprint <paramref name="w"/>×<paramref name="h"/>,
    /// followed by its mirror copies about the axis tile (<paramref name="cx"/>, <paramref name="cy"/>) for
    /// whichever of <paramref name="vertical"/> / <paramref name="horizontal"/> are active. With both false this
    /// is just the original pose (symmetry off), so callers can route every placement through it unconditionally.
    /// </summary>
    public static IEnumerable<(int X, int Y, int Rot)> Poses(
        int x, int y, int rot, int w, int h, int cx, int cy, bool vertical, bool horizontal)
    {
        yield return (x, y, rot);
        if (!vertical && !horizontal) yield break;

        var mx = 2 * cx - (x + w - 1);   // reflect the whole span; footprint width is unchanged by the mirror
        var my = 2 * cy - (y + h - 1);
        if (vertical) yield return (mx, y, GridMath.Norm(360 - rot));
        if (horizontal) yield return (x, my, GridMath.Norm(180 - rot));
        if (vertical && horizontal) yield return (mx, my, GridMath.Norm(rot + 180));
    }
}
