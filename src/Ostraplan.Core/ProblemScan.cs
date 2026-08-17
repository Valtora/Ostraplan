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

    /// <summary>
    /// The game's docking-port class marker. Carried by every Secondary Exterior Airlock
    /// (<c>ItmDockSys03*</c>); absent from the Primary (<c>ItmDockSys02*</c>). It decides where
    /// <c>Ship.AddCO</c> files a port in <c>aDocksys</c>, and so which port bounds construction
    /// (see <see cref="BoundingPort"/>).
    /// </summary>
    public const string TypeBCond = "IsTypeB";

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

        AddDuplicatePrimaryWarning(doc, catalog, problems);

        // Exactly ONE port bounds construction, and only the Primary ever does — see BoundingPort.
        if (BoundingPort(doc, catalog) is { } port
            && TryGetFace(doc.Part(port)!, port, out var axisY, out var dir, out var face))
        {
            var part = doc.Part(port)!;
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
                    $"first at ({sample!.Value.X},{sample.Value.Y}). The game forbids building past the primary " +
                    "airlock's face (Item.CheckFit), and a blocked face cannot mate with a station collar."));
        }

        AddBlockedPortWarnings(doc, catalog, ports, problems);
        AddLegalityProblems(doc, problems);
        AddWalkabilityWarnings(doc, catalog, problems);

        return problems;
    }

    /// <summary>
    /// More than one PRIMARY-class port. A ship owns exactly one: <c>Ship.AddCO</c> files every non-TypeB port with
    /// <c>aDocksys.Insert(0, …)</c>, so a second one displaces the first at index 0 and silently becomes the port
    /// that bounds construction and that a station collar mates to.
    ///
    /// <para>Ostraplan used to CREATE this. It recognised the primary airlock by the def name
    /// <c>ItmDockSys02Closed</c> alone, so a ship whose airlock had been pried open in game
    /// (<c>ItmDockSys02Open</c>) read as having none, and reopening the design seeded a fresh one at the origin.
    /// That moved the written grid frame and left the ship unable to dock. The seeding is condition-based now, but
    /// designs saved while it was not still carry the stray port, and it has to be deleted by hand — hence a named,
    /// tile-highlighted Blocking problem rather than a silent repair.</para>
    /// </summary>
    private static void AddDuplicatePrimaryWarning(ShipDocument doc, Catalog catalog, List<Problem> problems)
    {
        var primaries = doc.Placements.Where(p => catalog.IsPrimaryDocksys(doc.Part(p))).ToList();
        if (primaries.Count < 2) return;

        // Which one WINS is the useful part, and it is rarely the one the user means: Insert(0, …) puts the
        // LAST-registered non-TypeB port at the head of aDocksys, so a port added after the ship's real airlock
        // displaces it. Name every candidate and say which the game would take, rather than guessing an intruder.
        var cells = primaries.SelectMany(p =>
        {
            var (w, h) = doc.FootprintOf(p);
            return from r in Enumerable.Range(0, h) from c in Enumerable.Range(0, w) select (p.X + c, p.Y + r);
        }).ToList();
        var listed = string.Join(", ", primaries.Select(p => $"{doc.Part(p)?.Friendly ?? p.DefName} at ({p.X},{p.Y})"));
        var winner = BoundingPort(doc, catalog) is { } w2
            ? $"{doc.Part(w2)?.Friendly ?? w2.DefName} at ({w2.X},{w2.Y})" : null;

        problems.Add(new Problem(ProblemSeverity.Blocking,
            $"{primaries.Count} primary docking ports",
            $"A ship can only have one. Every primary port is registered at the head of Ship.aDocksys, so the " +
            $"last one loaded wins and becomes the port that bounds construction and that a station collar mates " +
            $"to, which moves the ship relative to its dock. Found: {listed}." +
            (winner is null ? "" : $" The game would dock by {winner}.") +
            " Delete all but the one your ship actually docks by. An older Ostraplan added a stray port at the " +
            "origin when it could not recognise an airlock that had been pried open in game, so if one of these " +
            "sits at (0,0) away from the hull, that is the one to remove.",
            cells));
    }

    /// <summary>The dismiss key for the blocked mating-face warning.</summary>
    public const string BlockedPortAlertKey = "blocked-mating-face";

    /// <summary>
    /// New construction parked in a port's <b>mating corridor</b> — the strip directly ahead of its face, as wide
    /// as the port itself — so that port can never take a station collar.
    ///
    /// <para>Only the ports the envelope does <b>not</b> cover are checked. The bounding port (see
    /// <see cref="BoundingPort"/>, always the Primary when one exists) already refuses this outright through
    /// <c>Item.CheckFit</c> and is reported above as Blocking. Every other port bounds nothing in game, which is
    /// what lets a towing brace land in front of a Secondary with the ghost showing green (issue #29). The brace
    /// is the case that surfaced it: its <c>aSocketReqs</c> carries a single <c>TILDockSys</c> cell, so a
    /// one-point requirement that <b>every</b> rotation can satisfy at some offset, three of the four leaving the
    /// brace across or against the airlock.</para>
    ///
    /// <para><b>Warning, not Blocking, and the corridor rather than the whole half-plane.</b> The game genuinely
    /// permits it (re-read against <c>Item.CheckFit</c> on 1.0.0.9: the build path calls it with
    /// <c>GUIInventory.Selected == null</c>, which is exactly the branch that skips the crew proximity/LOS gate,
    /// so nothing else applies), and a Secondary facing into the hull is a legitimate internal docking bay whose
    /// half-plane covers most of the ship. Bounding the flag laterally to the port's own width keeps the claim to
    /// what it can actually justify, that a collar has no room to mate. Imported structure is skipped exactly as
    /// it is for the Blocking rule: the game never re-validates existing hull.</para>
    /// </summary>
    private static void AddBlockedPortWarnings(
        ShipDocument doc, Catalog catalog, List<Placement> ports, List<Problem> problems)
    {
        var bounding = BoundingPort(doc, catalog);
        foreach (var port in ports)
        {
            if (ReferenceEquals(port, bounding)) continue;   // already covered, as a Blocking envelope breach
            if (doc.Part(port) is not { } portPart) continue;
            if (!TryGetFace(portPart, port, out var axisY, out var dir, out var face)) continue;

            // the collar mates across the port's full width, so only that strip can block it
            var (pw, ph) = doc.FootprintOf(port);
            var (lo, hi) = axisY ? (port.X, port.X + pw - 1) : (port.Y, port.Y + ph - 1);

            var cells = new List<(int X, int Y)>();
            var names = new List<string>();
            foreach (var q in doc.Placements)
            {
                if (q.IsGiven || ReferenceEquals(q, port)) continue;
                var (w, h) = doc.FootprintOf(q);
                var hit = false;
                for (var r = 0; r < h; r++)
                    for (var c = 0; c < w; c++)
                    {
                        var (x, y) = (q.X + c, q.Y + r);
                        if ((axisY ? x : y) < lo || (axisY ? x : y) > hi) continue;   // outside the corridor
                        if (((axisY ? y : x) + 0.5 - face) * dir <= 0.01) continue;   // inboard of the face
                        cells.Add((x, y));
                        hit = true;
                    }
                if (hit && doc.Part(q) is { } qPart) names.Add(qPart.Friendly);
            }

            if (cells.Count == 0) continue;
            var distinct = names.Distinct().ToList();
            var listed = string.Join(", ", distinct.Take(4)) + (distinct.Count > 4 ? ", …" : "");
            problems.Add(new Problem(ProblemSeverity.Warning,
                $"\"{portPart.Friendly}\" at ({port.X},{port.Y}) is blocked and cannot dock",
                $"{cells.Count} tile(s) sit ahead of its mating face ({listed}), so no station collar can reach it. " +
                "The game allows this — only the primary airlock's face bounds construction — so it is advice, not " +
                "a block. A part that should be mounted on the airlock (a towing brace) usually just needs to share " +
                "the airlock's rotation; otherwise move it inboard, or Dismiss if this port is a deliberate internal " +
                "bay (highlighted tiles show what is in the way).",
                cells, DismissKey: BlockedPortAlertKey));
        }
    }

    /// <summary>The dismiss key for the unreachable-devices warning.</summary>
    public const string UnreachableAlertKey = "unreachable-devices";

    /// <summary>The dismiss key for the isolated-compartment warning.</summary>
    public const string IsolatedAlertKey = "isolated-compartments";

    /// <summary>
    /// Live crew-access advice from <see cref="WalkNetwork"/>: fittings no crew member could operate, and interior
    /// floor that is walled off from the rest of the ship.
    ///
    /// <para>All of it is <b>Warning</b>, never Blocking. None of it makes a ship invalid — the game will happily
    /// spawn a design whose cooler you cannot reach — so it is advice a planner should surface and the user should
    /// be able to dismiss, exactly like the unsealed-compartment alert. The walk analysis runs with the defaults
    /// (interior only, Forbid zones respected); the View-menu switches change only the overlay, so the report does
    /// not quietly re-interpret the ship behind the user.</para>
    /// </summary>
    private static void AddWalkabilityWarnings(ShipDocument doc, Catalog catalog, List<Problem> problems)
    {
        var grid = ShipGrid.FromDocument(doc, catalog);
        if (grid.TileCount <= 1) return;
        var walk = WalkNetwork.Build(grid, catalog, WalkOptions.Default, WalkNetwork.ForbiddenTiles(doc, grid));

        var unreachable = walk.Unreachable.ToList();
        if (unreachable.Count > 0)
        {
            // Counted per kind: a bare list of names reads as "one of each" when it is usually a dozen of one,
            // and the count is what tells you whether it is a real problem or one awkward fitting.
            var kinds = unreachable
                .GroupBy(d => d.Friendly)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key)
                .ToList();
            var listed = string.Join(", ", kinds.Take(6)) + (kinds.Count > 6 ? ", …" : "");
            // Hull-mounted kit (rotors, external cargo pods) is reached on a spacewalk and is excluded upstream by
            // WalkResult.Unreachable; what is left here genuinely cannot be operated by anyone, suited or not.
            var why = unreachable.Any(d => d.Reason == WalkBlock.SightBlocked)
                ? " Some are in range but out of sight, which the game also refuses."
                : "";
            problems.Add(new Problem(ProblemSeverity.Warning,
                $"{unreachable.Count} device{(unreachable.Count == 1 ? "" : "s")} cannot be reached",
                $"No crew member can stand where the game requires to operate: {listed}.{why} " +
                "The devices themselves are highlighted; clear a walkable tile within range of each, " +
                "or Dismiss to hide this alert.",
                [.. unreachable.SelectMany(d => d.BodyTiles).Distinct().Select(grid.GridToDoc)],
                DismissKey: UnreachableAlertKey));
        }

        // Interior floor the crew cannot walk to from the main body. Only worth saying when there IS a main body
        // to be cut off from, and single tiles are usually a niche a design meant to leave (under a fixture).
        var main = walk.LargestZone;
        if (main < 0) return;
        var isolated = walk.Zones.Where(z => !z.Exterior && z.Index != main && z.TileCount > 1).ToList();
        if (isolated.Count == 0) return;

        var cut = isolated.Sum(z => z.TileCount);
        problems.Add(new Problem(ProblemSeverity.Warning,
            $"{isolated.Count} sealed-off compartment{(isolated.Count == 1 ? "" : "s")}",
            $"{cut} walkable tile(s) in {isolated.Count} area(s) have no route to the main body of the ship " +
            "(crew would have to EVA). A stuck door counts: an unpowered, locked or damaged closed door is a solid " +
            "wall to pathing, unlike a powered one. Use Show to highlight them, or Dismiss to hide this alert.",
            [.. isolated.SelectMany(z => z.Tiles).Select(grid.GridToDoc)],
            DismissKey: IsolatedAlertKey));
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
        var advisories = new List<(Placement P, FitResult Res)>();   // placed legally, but a soft req is unmet (see CheckFit.SoftReqs)
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
                    if (res.Advisory is not null) advisories.Add((p, res));
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

        // Soft-requirement advisories (e.g. an overhead light with no adjacent power conduit): the part is placed —
        // the game's own spawned ships do the same — but the interactive builder's rule is unmet, so it is a single
        // dismissible Warning rather than a block. See CheckFit.SoftReqs / issue #11.
        var advisoryGroups = new Dictionary<string, (List<(int, int)> Cells, List<string> Parts)>(StringComparer.Ordinal);
        foreach (var (p, res) in advisories)
        {
            var reason = res.Advisory!;
            if (!advisoryGroups.TryGetValue(reason, out var g)) advisoryGroups[reason] = g = ([], []);
            if (res.AdvisoryCells is not null) g.Cells.AddRange(res.AdvisoryCells);
            g.Parts.Add(doc.Part(p)!.Friendly);
        }
        foreach (var (reason, g) in advisoryGroups)
        {
            var distinct = g.Parts.Distinct().ToList();
            var names = string.Join(", ", distinct.Take(6)) + (distinct.Count > 6 ? ", …" : "");
            problems.Add(new Problem(ProblemSeverity.Warning,
                $"{reason} — {g.Parts.Count} part{(g.Parts.Count == 1 ? "" : "s")}",
                $"These place and spawn just as the game's own ships do, but the in-game interactive builder wouldn't " +
                $"let a crew build them there: {names}. Run a POWR conduit onto the adjoining tile to satisfy the " +
                "builder, or Dismiss if you are exporting a spawned design (highlighted tiles show where a conduit is wanted).",
                g.Cells, DismissKey: SoftReqAlertKey));
        }
    }

    /// <summary>The dismiss key for soft-requirement advisories (e.g. an overhead light with no adjacent conduit).</summary>
    public const string SoftReqAlertKey = "soft-requirement-advisory";


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
    /// The <b>one</b> installed port whose mating face bounds new construction, or null when the
    /// design has none. This is <c>Ship.aDocksys.FirstOrDefault()</c> — the single port
    /// <c>Item.CheckFit</c> derives its envelope from. Every other port bounds nothing.
    ///
    /// <para><b>Which port that is is decided by <c>IsTypeB</c>, not by position.</b>
    /// <c>Ship.AddCO</c> files a non-TypeB port with <c>aDocksys.Insert(0, …)</c> and a TypeB port
    /// with <c>aDocksys.Add(…)</c>, so a non-TypeB port always sorts ahead of every TypeB one. In
    /// core data the only installed non-TypeB port is the <b>Primary</b> Exterior Airlock
    /// (<c>ItmDockSys02Closed</c>/<c>02Open</c>); every <b>Secondary</b> (<c>ItmDockSys03*</c>) is
    /// TypeB. So the Primary bounds construction and a Secondary never does — which is what makes
    /// an internal docking bay (a Secondary facing into the hull) legal in game.</para>
    ///
    /// <para>Registration order only breaks ties within a class: <c>Insert(0, …)</c> means the
    /// <i>last</i> non-TypeB port registered lands at index 0, while <c>Add(…)</c> means the
    /// <i>first</i> TypeB port does. We read <see cref="ShipDocument.Placements"/> as that order
    /// (it is the order an export emits <c>aItems</c>, which is the order the game spawns and
    /// registers them). Both ties are unreachable in practice: core ships carry exactly one
    /// Primary, and Ostraplan seeds exactly one per document.</para>
    /// </summary>
    public static Placement? BoundingPort(ShipDocument doc, Catalog catalog)
    {
        Placement? nonTypeB = null, typeB = null;
        foreach (var p in doc.Placements)
        {
            var part = doc.Part(p);
            if (part is null || !IsDocksys(part, catalog)) continue;
            if (part.StartingConds.Contains(TypeBCond)) typeB ??= p;   // Add(…): first registered leads
            else nonTypeB = p;                                          // Insert(0, …): last registered leads
        }
        return nonTypeB ?? typeB;
    }

    /// <summary>
    /// The port's mating face, from its DockA/DockB map points (pixels around
    /// the item centre, +y up; DockA sits at the door, DockB outside the hull).
    /// The face line is the A-B midpoint on the dominant axis and everything
    /// beyond it (toward B) is out of bounds - the exact envelope Item.CheckFit
    /// derives for the bounding port (see <see cref="BoundingPort"/>).
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
