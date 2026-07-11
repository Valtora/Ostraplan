namespace Ostraplan.Core;

/// <summary>
/// A ship's rating, the game's six slots. Displayed as slots 1–5 joined with "-"
/// (Ship.GetRatingString); slot 0 (epoch) and slot 5 (unused) are omitted from display.
/// <see cref="Mass"/> (kg) and <see cref="RcsThrust"/> (summed StatThrustStrength) carry the
/// raw numbers behind the Maneuver grade so the UI can show the actual ratio.
/// </summary>
public sealed record ShipRating(
    string Epoch, string Condition, string RoomCount, string Maneuver, string Size, string Slot5,
    double Mass = 0, double RcsThrust = 0)
{
    public string Display => string.Join("-",
        new[] { Condition, RoomCount, Maneuver, Size, Slot5 }.Where(s => !string.IsNullOrEmpty(s)));
}

/// <summary>
/// Port of <c>Ship.CalculateRating</c> (verified 0.15.1.6; cutoffs hardcoded in the DLL
/// — re-verify after a game patch).
/// <list type="bullet">
///   <item><b>Condition</b> A–E: mean of <c>Clamp01(1 − damageRate)</c> over installed
///   parts. A planner ship is pristine (damageRate 0) ⇒ mean 1 ⇒ <b>A</b> by construction.</item>
///   <item><b>Room count</b>: rooms whose certified spec ≠ Blank.</item>
///   <item><b>Maneuver</b>: <c>mass / fRCSCount</c>, where fRCSCount sums StatThrustStrength
///   over installed RCS clusters (TIsRCSClusterAudioEmitter) and mass sums StatMass over
///   installed parts. 0 RCS ⇒ O.</item>
///   <item><b>Size class</b>: grid area <c>nCols·nRows</c>.</item>
/// </list>
/// </summary>
public static class Rating
{
    public const string RcsTrigger = "TIsRCSClusterAudioEmitter";

    public static ShipRating Calculate(ShipGrid grid, RoomPartition rooms, Catalog catalog)
    {
        var roomCount = rooms.Rooms.Count(r => !RoomSpecIsBlank(r.RoomSpec));

        double mass = 0, rcs = 0;
        catalog.Triggers.TryGetValue(RcsTrigger, out var rcsTrig);
        foreach (var p in grid.Parts)
        {
            if (!p.Part.Has("IsInstalled")) continue;
            mass += p.Part.StartingCondValues.GetValueOrDefault("StatMass");
            if (rcsTrig is not null && CondEval.Triggered(rcsTrig, p.Part.CondSet, catalog))
                rcs += p.Part.StartingCondValues.TryGetValue("StatThrustStrength", out var t) ? t : 1.0;
        }

        var maneuver = rcs == 0 ? "O" : ManeuverGrade(mass / rcs);
        return new ShipRating("", "A", roomCount.ToString(), maneuver, SizeClass(grid.NCols * grid.NRows), "", mass, rcs);
    }

    private static bool RoomSpecIsBlank(string spec) => string.IsNullOrEmpty(spec) || spec == "Blank";

    /// <summary>≤0.5 E, ≤0.8 D, ≤0.95 C, ≤0.99 B, else A.</summary>
    public static string ConditionGrade(double n) =>
        n <= 0.5 ? "E" : n <= 0.8 ? "D" : n <= 0.95 ? "C" : n <= 0.99 ? "B" : "A";

    /// <summary>0→O, &lt;300 A, &lt;500 B, &lt;750 C, &lt;1500 D, else E.</summary>
    public static string ManeuverGrade(double n) =>
        n <= 0 ? "O" : n < 300 ? "A" : n < 500 ? "B" : n < 750 ? "C" : n < 1500 ? "D" : "E";

    /// <summary>&lt;250 Small … &lt;3000 Titanmax, [3000,3700) Very Large, ≥3700 Ultra Large.</summary>
    public static string SizeClass(int area) =>
        area <= 0 ? "" :
        area < 250 ? "Small" :
        area < 900 ? "Medium" :
        area < 1600 ? "Lunamax" :
        area < 2300 ? "Ceresmax" :
        area < 3000 ? "Titanmax" :
        area >= 3700 ? "Ultra Large" : "Very Large";
}
