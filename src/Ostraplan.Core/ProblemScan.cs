namespace Ostraplan.Core;

public enum ProblemSeverity { Warning, Blocking }

public sealed record Problem(ProblemSeverity Severity, string Title, string Detail);

/// <summary>
/// Design checks the planner can already evaluate honestly. More join as the
/// law slices land: per-placement socket legality (P1), room detection,
/// airtightness and certification (P2).
/// </summary>
public static class ProblemScan
{
    public const string DocksysTrigger = "TIsDockSysInstalled";

    public static List<Problem> Scan(ShipDocument doc, Catalog catalog)
    {
        var problems = new List<Problem>();
        var ports = doc.Placements.Where(p => IsDocksys(doc.Part(p), catalog)).ToList();

        if (ports.Count == 0)
        {
            problems.Add(new Problem(ProblemSeverity.Blocking, "No docking port",
                "Without an installed docking port the game's Ship.aDocksys stays empty and the ship can never " +
                "hard-dock. Add one from the HULL tab (Secondary Exterior Airlock)."));
            return problems;
        }

        foreach (var port in ports)
        {
            var part = doc.Part(port)!;
            if (!TryGetFace(part, port, out var axisY, out var dir, out var face)) continue;

            var blocked = 0;
            (int X, int Y)? sample = null;
            foreach (var q in doc.Placements)
            {
                var (w, h) = doc.FootprintOf(q);
                for (var r = 0; r < h; r++)
                    for (var c = 0; c < w; c++)
                    {
                        var center = axisY ? q.Y + r + 0.5 : q.X + c + 0.5;
                        if ((center - face) * dir > 0.01)
                        {
                            blocked++;
                            sample ??= (q.X + c, q.Y + r);
                        }
                    }
            }

            if (blocked > 0)
                problems.Add(new Problem(ProblemSeverity.Blocking, "Construction beyond the airlock",
                    $"{blocked} tile(s) lie beyond the mating face of \"{part.Friendly}\" at ({port.X},{port.Y}) — " +
                    $"first at ({sample!.Value.X},{sample.Value.Y}). The game forbids building past an airlock's " +
                    "face (TileUtils.GetAirlockBounds), and a blocked face cannot mate with a station collar."));
        }

        return problems;
    }

    /// <summary>
    /// ALL of the trigger's required conditions must be present - matching any
    /// one would hit IsInstalled and flag every part as a docking port.
    /// </summary>
    public static bool IsDocksys(PartDef? part, Catalog catalog) =>
        part is not null
        && catalog.Triggers.TryGetValue(DocksysTrigger, out var ct)
        && ct.Reqs.Length > 0
        && ct.Reqs.All(part.StartingConds.Contains);

    /// <summary>
    /// The port's mating face, from its DockA/DockB map points (pixels around
    /// the item centre, +y up; DockA sits at the door, DockB outside the hull).
    /// The face line is the A-B midpoint on the dominant axis and everything
    /// beyond it (toward B) is out of bounds - the exact envelope
    /// TileUtils.GetAirlockBounds derives per port.
    /// </summary>
    public static bool TryGetFace(PartDef part, Placement p, out bool axisY, out int dir, out double face)
    {
        axisY = true;
        dir = 0;
        face = 0;
        if (!part.MapPoints.TryGetValue("DockA", out var a) || !part.MapPoints.TryGetValue("DockB", out var b))
            return false;

        var (w, h) = (part.Item.Width, part.Item.Height);
        var (ax, ay) = Transform(a, w, h, p.Rot);
        var (bx, by) = Transform(b, w, h, p.Rot);
        var (vx, vy) = (bx - ax, by - ay);
        if (Math.Abs(vx) < 0.01 && Math.Abs(vy) < 0.01) return false;

        axisY = Math.Abs(vy) >= Math.Abs(vx);
        dir = axisY ? Math.Sign(vy) : Math.Sign(vx);
        face = (axisY ? ay + by : ax + bx) / 2 + (axisY ? p.Y : p.X);
        return true;
    }

    /// <summary>px around item centre (+y up) -> tile coords in the rotated footprint (top-left origin, +y down).</summary>
    private static (double X, double Y) Transform((double X, double Y) px, int w, int h, int rot)
    {
        var pt = (X: w / 2.0 + px.X / 16.0, Y: h / 2.0 - px.Y / 16.0);
        return GridMath.Norm(rot) switch
        {
            90 => (h - pt.Y, pt.X),
            180 => (w - pt.X, h - pt.Y),
            270 => (pt.Y, w - pt.X),
            _ => pt,
        };
    }
}
