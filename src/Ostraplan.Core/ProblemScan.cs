namespace Ostraplan.Core;

public enum ProblemSeverity { Warning, Blocking }

/// <summary>
/// A design issue. <see cref="Cells"/> are the world tiles to hazard-tint on the
/// canvas (socket-legality and constructibility problems); null for ship-level
/// problems (no docking port) and envelope breaches, which the red stripes cover.
/// </summary>
public sealed record Problem(ProblemSeverity Severity, string Title, string Detail,
    IReadOnlyList<(int X, int Y)>? Cells = null);

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
        AddAirtightnessWarning(doc, catalog, problems);   // live, without pressing Ship Rating
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

        AddLegalityProblems(doc, problems);

        // constructibility only matters once the finished design is otherwise legal:
        // it catches the rare layout that validates whole yet no floors-first order
        // can actually build (§6.6). A design already flagged blocking is skipped.
        if (problems.All(p => p.Severity != ProblemSeverity.Blocking))
            AddConstructibilityProblem(doc, problems);

        return problems;
    }

    /// <summary>
    /// Every placed part re-checked against Item.CheckFit (sockets only — the
    /// envelope is reported above), grouped by failure reason so a rough edit that
    /// breaks many parts stays one scannable entry per cause. The part's own tile
    /// contribution is excluded (CheckFit's <c>self</c>) or it would fail itself.
    /// </summary>
    private static void AddLegalityProblems(ShipDocument doc, List<Problem> problems)
    {
        var groups = new Dictionary<string, (List<(int, int)> Cells, List<string> Parts)>(StringComparer.Ordinal);
        foreach (var p in doc.Placements)
        {
            if (doc.IsLocked(p)) continue;             // the seeded primary airlock isn't user-fixable
            var part = doc.Part(p);
            if (part is null) continue;                // missing-def parts are surfaced by the open dialog
            var res = CheckFit.Check(doc, part, p.X, p.Y, p.Rot, self: p, includeEnvelope: false);
            if (res.Ok) continue;
            var reason = res.Reason ?? "illegal placement";
            if (!groups.TryGetValue(reason, out var g)) groups[reason] = g = ([], []);
            g.Cells.AddRange(res.FailedCells);
            g.Parts.Add(part.Friendly);
        }

        foreach (var (reason, g) in groups)
        {
            var distinct = g.Parts.Distinct().ToList();
            var names = string.Join(", ", distinct.Take(6)) + (distinct.Count > 6 ? ", …" : "");
            problems.Add(new Problem(ProblemSeverity.Blocking,
                $"{reason} — {g.Parts.Count} part{(g.Parts.Count == 1 ? "" : "s")}",
                $"The game's Item.CheckFit would refuse placement here: {names}. Adjust the layout so each part's " +
                "socket requirements are met (highlighted tiles show where the rule breaks).",
                g.Cells));
        }
    }

    /// <summary>
    /// Simulate a canonical build order (docking ports → floors → walls/doors →
    /// the rest) into a scratch ship with incremental CheckFit; warn naming the
    /// first part that never becomes placeable. Ordering is stable within a rank.
    /// </summary>
    private static void AddConstructibilityProblem(ShipDocument doc, List<Problem> problems)
    {
        var scratch = new ShipDocument(doc.Catalog);
        // the primary airlock (and any locked part) is a ship given, not built: seed it
        // first and unchecked so its conditions and mating face bound the sim throughout
        foreach (var p in doc.Placements.Where(doc.IsLocked))
            scratch.Add(new Placement { DefName = p.DefName, X = p.X, Y = p.Y, Rot = p.Rot });

        var ordered = doc.Placements
            .Where(p => !doc.IsLocked(p) && doc.Part(p) is not null)
            .OrderBy(p => BuildRank(doc.Catalog, doc.Part(p)!));
        foreach (var p in ordered)
        {
            var part = doc.Part(p)!;
            var res = CheckFit.Check(scratch, part, p.X, p.Y, p.Rot, self: null, includeEnvelope: true);
            if (!res.Ok)
            {
                problems.Add(new Problem(ProblemSeverity.Warning,
                    $"May not be constructible: {part.Friendly}",
                    $"\"{part.Friendly}\" at ({p.X},{p.Y}) can't be placed in a floors→walls→fixtures order " +
                    $"({res.Reason}). The finished layout is legal, but the game builds incrementally, so an in-game " +
                    "build may stall here — usually a fixture walled in with no valid placement sequence.",
                    res.FailedCells));
                return;   // name the first blocker (§6.6)
            }
            scratch.Add(new Placement { DefName = p.DefName, X = p.X, Y = p.Y, Rot = p.Rot });
        }
    }

    /// <summary>Canonical build phase from what a part contributes to its own tiles.</summary>
    private static int BuildRank(Catalog catalog, PartDef part)
    {
        if (IsDocksys(part, catalog)) return 0;   // ports define the envelope; seed them first
        var conds = part.Item.SocketAdds.SelectMany(catalog.LootConds).ToHashSet(StringComparer.Ordinal);
        if (conds.Contains("IsFloor") || conds.Contains("IsFloorSealed")) return 1;
        if (conds.Contains("IsWall") || conds.Contains("IsPortal")) return 2;
        return 3;
    }

    /// <summary>
    /// A live warning (count only — the Ship Rating report has the tile-level detail and
    /// highlight) when the design has compartments that aren't sealed: floor that isn't
    /// enclosed by walls (open to space) or an enclosed room missing a sealed floor.
    /// </summary>
    private static void AddAirtightnessWarning(ShipDocument doc, Catalog catalog, List<Problem> problems)
    {
        var breaches = ShipAnalysis.Airtightness(doc, catalog);
        if (breaches.Count == 0) return;

        var open = breaches.Count(b => b.OpenToSpace);
        var holes = breaches.Count - open;
        var tiles = breaches.Sum(b => b.Tiles.Count);
        var kinds = new List<string>();
        if (open > 0) kinds.Add($"{open} open to space (not walled in)");
        if (holes > 0) kinds.Add($"{holes} missing a sealed floor");
        problems.Add(new Problem(ProblemSeverity.Warning,
            $"{breaches.Count} unsealed compartment{(breaches.Count == 1 ? "" : "s")}",
            $"{string.Join(", ", kinds)} — {tiles} tile{(tiles == 1 ? "" : "s")} in total. Run Ship Rating to see and highlight them."));
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
