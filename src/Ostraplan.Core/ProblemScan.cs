namespace Ostraplan.Core;

public enum ProblemSeverity { Warning, Blocking }

/// <summary>
/// A design issue. <see cref="Cells"/> are the world tiles the problem points at — hazard-tinted on the canvas
/// for socket-legality/constructibility problems, or highlighted as leak points for an airtightness warning; null
/// for ship-level problems (no docking port). A non-null <see cref="DismissKey"/> makes the problem
/// <b>dismissible</b>: the user can hide it (and later Restore Alerts), keyed by this stable string so the
/// dismissal survives edits and persists in the <c>.oplan</c> (see <see cref="ShipDocument.DismissedAlerts"/>).
/// </summary>
public sealed record Problem(ProblemSeverity Severity, string Title, string Detail,
    IReadOnlyList<(int X, int Y)>? Cells = null, string? DismissKey = null);

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
                if (q.IsGiven) continue;   // the game bounds NEW construction, not existing hull (imported ships)
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

        return problems;
    }

    /// <summary>
    /// The user's additions checked the way the game actually validates construction: <b>incrementally</b>.
    /// The game tests each new part against the ship <i>as it is when built</i> and never re-validates existing
    /// structure, so a design is buildable iff <i>some</i> order places every part legally. We seed the existing
    /// (given/locked) ship, then build the authored parts in canonical order (docking → floors → walls →
    /// fixtures/conduits), checking each against what's built so far. A part that fits the finished layout but no
    /// build order — a wall with a fixture already mounted through it — is <b>not</b> flagged (the wall is built
    /// first); one that fits no order (a fixture with no wall, two walls stacked) is. This replaces a final-state
    /// per-part check that wrongly rejected legal fixture-through-wall / conduit-on-wall stacks. Failures are
    /// grouped by reason; a part that can't be built is not added to the scratch, so a dependent with no other
    /// support is flagged too (it genuinely can't be built).
    /// </summary>
    private static void AddLegalityProblems(ShipDocument doc, List<Problem> problems)
    {
        var scratch = new ShipDocument(doc.Catalog);
        foreach (var p in doc.Placements.Where(p => doc.IsLocked(p) || p.IsGiven))   // existing ship: not user-built
            scratch.Add(new Placement { DefName = p.DefName, X = p.X, Y = p.Y, Rot = p.Rot });

        // Core failures are hard (Blocking): the Law is a proven port of core logic. MODDED failures are only a
        // Warning: the port models the core game, and a mod can add its own conditions/behaviour that make the part
        // legal in-game — so we flag it, name it, and (unlike a core failure) TRUST it into the simulation so parts
        // built on it don't cascade-flag. This is the same distinction the "allow modded overrides" placement toggle
        // makes; a modded part flagged here got there by an override or by a move, and either way we can't be sure.
        // A part that fails now may fit once another SAME-PHASE part it depends on is down: a reactor
        // component needs its core, the core needs its field coils, and those three are all "fixtures"
        // (rank 3) — so a single ordered pass fails whenever the file lists a dependent before its base.
        // The game only needs SOME order to work, so we sweep to a fixed point: each pass places every
        // pending part that currently fits (removing it from the pool), and we repeat while any pass makes
        // progress. Placed parts leave the pool, so after the first O(N) sweep each retry only re-checks
        // the handful of still-deferred parts. What can't be placed in any reachable order is a real fault.
        Placement Clone(Placement p) => new() { DefName = p.DefName, X = p.X, Y = p.Y, Rot = p.Rot };
        var pending = doc.Placements
            .Where(p => !doc.IsLocked(p) && !p.IsGiven && doc.Part(p) is not null)
            .OrderBy(p => BuildRank(doc.Catalog, doc.Part(p)!))
            .ToList();

        var lastFail = new Dictionary<Guid, FitResult>();
        var moddedFails = new List<(Placement P, FitResult Res)>();
        while (pending.Count > 0)
        {
            var placed = false;
            var still = new List<Placement>();
            foreach (var p in pending)
            {
                var res = CheckFit.Check(scratch, doc.Part(p)!, p.X, p.Y, p.Rot, self: null, includeEnvelope: false);
                if (res.Ok)
                {
                    scratch.Add(Clone(p));
                    placed = true;
                }
                else { lastFail[p.Id] = res; still.Add(p); }
            }
            pending = still;
            if (placed) continue;   // progress this pass — another sweep may unblock more

            // Stalled. A failing MODDED part is trusted into the sim (a mod can add conditions/behaviour we
            // don't model, so it may be legal in-game) — placing it lets its dependents build rather than
            // cascade-flagging them, and it is recorded as a Warning. Sweep again in case that unblocks
            // core parts. When no modded parts remain to trust, whatever is still pending is a hard core
            // failure and we stop. (Core parts are never trusted — the Law is authoritative for vanilla.)
            var trust = pending.Where(p => doc.Part(p)!.IsModded).ToList();
            if (trust.Count == 0) break;
            foreach (var p in trust)
            {
                scratch.Add(Clone(p));
                moddedFails.Add((p, lastFail[p.Id]));
            }
            pending = pending.Where(p => !doc.Part(p)!.IsModded).ToList();
        }

        var coreGroups = new Dictionary<string, (List<(int, int)> Cells, List<string> Parts)>(StringComparer.Ordinal);
        var moddedGroups = new Dictionary<string, (List<(int, int)> Cells, List<string> Parts)>(StringComparer.Ordinal);
        void Group(Dictionary<string, (List<(int, int)> Cells, List<string> Parts)> groups, PartDef part, FitResult res)
        {
            var reason = res.Reason ?? "illegal placement";
            if (!groups.TryGetValue(reason, out var g)) groups[reason] = g = ([], []);
            g.Cells.AddRange(res.FailedCells);
            g.Parts.Add(part.Friendly);
        }
        foreach (var p in pending) Group(coreGroups, doc.Part(p)!, lastFail[p.Id]);   // hard core failures
        foreach (var (p, res) in moddedFails) Group(moddedGroups, doc.Part(p)!, res);

        foreach (var (reason, g) in coreGroups)
        {
            var distinct = g.Parts.Distinct().ToList();
            var names = string.Join(", ", distinct.Take(6)) + (distinct.Count > 6 ? ", …" : "");
            problems.Add(new Problem(ProblemSeverity.Blocking,
                $"{reason} — {g.Parts.Count} part{(g.Parts.Count == 1 ? "" : "s")}",
                $"The game builds incrementally (floors → walls → fixtures) and can't place these onto the ship at " +
                $"that step: {names}. Adjust the layout so each part has a valid build sequence (highlighted tiles " +
                "show where the rule breaks).",
                g.Cells));
        }

        foreach (var (reason, g) in moddedGroups)
        {
            var distinct = g.Parts.Distinct().ToList();
            var names = string.Join(", ", distinct.Take(6)) + (distinct.Count > 6 ? ", …" : "");
            problems.Add(new Problem(ProblemSeverity.Warning,
                $"modded part may not fit ({reason}) — {g.Parts.Count} part{(g.Parts.Count == 1 ? "" : "s")}",
                $"Ostraplan's placement rules model the core game only, so these modded parts — which can add their " +
                $"own conditions or code — may still be valid in Ostranauts: {names}. They are placed but flagged; " +
                "verify them in-game (highlighted tiles show where the core rules disagree).",
                g.Cells));
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

    /// <summary>The dismiss key for the unsealed-compartment (airtightness) warning.</summary>
    public const string UnsealedAlertKey = "unsealed-compartments";

    /// <summary>
    /// A live warning when the design has compartments that aren't sealed: floor that isn't enclosed by walls
    /// (open to space) or an enclosed room missing a sealed floor. Carries the <b>leak points</b> as its
    /// <see cref="Problem.Cells"/> (the same tiles the Ship Rating report highlights), so the sidebar can show and
    /// focus them directly; and a <see cref="Problem.DismissKey"/> so the user can dismiss it.
    /// </summary>
    private static void AddAirtightnessWarning(ShipDocument doc, Catalog catalog, List<Problem> problems)
    {
        var breaches = ShipAnalysis.Airtightness(doc, catalog);
        if (breaches.Count == 0) return;

        var open = breaches.Count(b => b.OpenToSpace);
        var holes = breaches.Count - open;
        var kinds = new List<string>();
        if (open > 0) kinds.Add($"{open} open to space (not walled in)");
        if (holes > 0) kinds.Add($"{holes} missing a sealed floor");
        var leakCells = breaches.SelectMany(b => b.Tiles).Distinct().ToList();
        problems.Add(new Problem(ProblemSeverity.Warning,
            $"{breaches.Count} unsealed compartment{(breaches.Count == 1 ? "" : "s")}",
            $"{string.Join(", ", kinds)}. Use Show to highlight the leak points on the canvas, or Dismiss to hide this alert.",
            leakCells, DismissKey: UnsealedAlertKey));
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
        var (ax, ay) = GridMath.MapPoint(a, w, h, p.Rot);
        var (bx, by) = GridMath.MapPoint(b, w, h, p.Rot);
        var (vx, vy) = (bx - ax, by - ay);
        if (Math.Abs(vx) < 0.01 && Math.Abs(vy) < 0.01) return false;

        axisY = Math.Abs(vy) >= Math.Abs(vx);
        dir = axisY ? Math.Sign(vy) : Math.Sign(vx);
        face = (axisY ? ay + by : ax + bx) / 2 + (axisY ? p.Y : p.X);
        return true;
    }

}
