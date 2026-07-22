namespace Ostraplan.Core;

/// <summary>
/// Outcome of a placement legality test: whether the pose fits, the world tiles
/// that failed (for the red ghost / hazard tint), and a one-line human reason.
///
/// <para><see cref="Advisory"/>/<see cref="AdvisoryCells"/> carry a <b>soft</b> outcome: the pose is
/// legal (<see cref="Ok"/> stays true) but an unmet soft requirement is worth flagging — the amber
/// "places, but …" ghost and a dismissible ProblemScan warning. See <see cref="CheckFit.SoftReqs"/>.</para>
/// </summary>
public sealed record FitResult(
    bool Ok, IReadOnlyList<(int X, int Y)> FailedCells, string? Reason,
    string? Advisory = null, IReadOnlyList<(int X, int Y)>? AdvisoryCells = null)
{
    public static readonly FitResult Legal = new(true, [], null);
}

/// <summary>
/// Port of <c>Item.CheckFit</c> (Assembly-CSharp, verified against game 0.15.1.6):
/// can this part occupy this pose given the ship's accumulated tile conditions?
///
/// <para>Model (traced from CheckFit + CondTrigger.Triggered + Loot.GetLootNames):</para>
/// <list type="bullet">
///   <item>Footprint is W×H from the item; <c>aSocketReqs</c>/<c>aSocketForbids</c>
///   are per-cell loot names over the (W+2)×(H+2) <b>ring</b> (footprint + 1-tile
///   border); <c>aSocketAdds</c> covers only the W×H footprint. "Blank"/empty/
///   unresolved = unconstrained.</item>
///   <item>Each ring cell builds a throwaway AND-CondTrigger from the loots'
///   expanded condition names and tests it against the tile: <b>every</b> req
///   condition present (count &gt; 0) and <b>no</b> forbid condition present.
///   Presence-only — CheckFit never reaches CondTrigger's count/nested/OR paths.</item>
///   <item>A ring cell with no accumulated conditions (off-ship / empty space)
///   therefore fails any requirement and passes a forbid-only cell — the game's
///   "must attach to structure / needs floor beneath" encoding.</item>
///   <item>Rotation rotates the ring masks (TileUtils.RotateTilesCW ≡
///   <see cref="GridMath.Rotate"/>); sheet items (walls/floors) never rotate.</item>
///   <item>Envelope (opt-in): no ring cell may fall beyond the mating face of the
///   <b>single</b> bounding docking port (<c>aDocksys.FirstOrDefault()</c>, resolved by
///   <see cref="ProblemScan.BoundingPort"/>), derived once before the ring loop exactly
///   as the game does. Only the Primary airlock ever bounds; a Secondary (TypeB) bounds
///   nothing, which is what makes an internal docking bay legal.</item>
/// </list>
///
/// <para>Excluded by design (in-game only, cannot occur in a planner): crew
/// proximity/LOS (GUIInventory selection), docked-ship WouldConnectShips, and
/// station-zone (JsonZone) restrictions.</para>
/// </summary>
public static class CheckFit
{
    /// <summary>
    /// Check <paramref name="part"/> at top-left tile (<paramref name="x"/>,
    /// <paramref name="y"/>) with rotation <paramref name="rot"/>.
    /// Pass <paramref name="self"/> when re-validating an already-placed part so
    /// its own tile contribution is excluded (walls/fixtures add IsObstruction and
    /// forbid it on their own footprint — otherwise every placed part fails itself).
    /// </summary>
    public static FitResult Check(ShipDocument doc, PartDef part, int x, int y, int rot,
        Placement? self = null, bool includeEnvelope = true)
    {
        var item = part.Item;
        var effRot = item.HasSpriteSheet ? 0 : GridMath.Norm(rot);   // sheet items never rotate
        var (rw, rh, reqs) = GridMath.Rotate(item.SocketReqs, item.Width + 2, item.Height + 2, effRot);
        var (_, _, forbids) = GridMath.Rotate(item.SocketForbids, item.Width + 2, item.Height + 2, effRot);
        var envelope = includeEnvelope ? BoundingFace(doc) : null;   // derived once, like the game (see Check's remarks)

        // re-validating a placed part: lift its own conditions so it doesn't fail
        // against itself, restore them no matter what (UI thread only; no re-entrancy
        // since TileConds.Apply raises no events).
        var selfItem = self is not null ? doc.Part(self)?.Item : null;
        if (selfItem is not null) doc.Conds.Apply(self!, selfItem, -1);
        try
        {
            var failed = new List<(int, int)>();
            var advisoryCells = new List<(int, int)>();   // legal, but a soft req (e.g. a light's power conduit) is unmet
            string? reason = null;
            string? advisory = null;
            var reasonRank = int.MaxValue;   // staged-build reasons outrank generic ones (see CellPasses)

            for (var r = 0; r < rh; r++)
                for (var c = 0; c < rw; c++)
                {
                    // ring cell (r,c) -> world tile: footprint interior sits at c,r in 1..W/1..H
                    var wx = x - 1 + c;
                    var wy = y - 1 + r;

                    if (envelope is { } env && Beyond(env, wx, wy))
                    {
                        failed.Add((wx, wy));
                        if (reasonRank == int.MaxValue) { reason = "beyond the airlock's mating face"; reasonRank = GenericRank; }
                        continue;
                    }

                    var idx = r * rw + c;
                    var reqConds = idx < reqs.Length ? doc.Catalog.LootConds(reqs[idx]) : [];
                    var forbidConds = idx < forbids.Length ? doc.Catalog.LootConds(forbids[idx]) : [];
                    if (reqConds.Count == 0 && forbidConds.Count == 0) continue;   // unconstrained

                    if (!CellPasses(doc.Conds, reqConds, forbidConds, wx, wy, out var why, out var rank, out var softWhy))
                    {
                        failed.Add((wx, wy));
                        if (rank < reasonRank) { reason = why; reasonRank = rank; }
                    }
                    else if (softWhy is not null)   // cell is legal, but a soft req is missing — record an advisory
                    {
                        advisoryCells.Add((wx, wy));
                        advisory ??= softWhy;
                    }
                }

            if (failed.Count == 0)
                return advisory is null ? FitResult.Legal : new FitResult(true, [], null, advisory, advisoryCells);
            return new FitResult(false, failed, reason, advisory, advisoryCells.Count > 0 ? advisoryCells : null);
        }
        finally
        {
            if (selfItem is not null) doc.Conds.Apply(self!, selfItem, +1);
        }
    }

    // Reason priority: when several cells fail, prefer a staged-build reason ("build the Field
    // Coils first") over a generic one ("needs a sealed floor beneath") — the generic cell usually
    // fails too, and first-cell-wins would bury the actionable tip.
    private const int StagedRank = 0;
    private const int GenericRank = 1;
    private static readonly HashSet<string> StagedConds = new(StringComparer.Ordinal)
    {
        "IsFusionFieldCoilsFixture", "IsFusionReactorCoreFixture",
    };

    /// <summary>
    /// Required conditions that record an <b>advisory</b> when unmet instead of blocking the pose.
    /// <c>IsPowerConduit</c> is the only one: among all 331 buildable parts it is required (in
    /// <c>aSocketReqs</c>) exclusively by the overhead ceiling lights (<c>ItmLitCeiling1x1*</c>).
    /// The game's <b>interactive</b> builder (<c>Item.CheckFit</c>, which reads <c>GUIInventory.Selected</c>
    /// and crew line-of-sight) only lets a crew hang a ceiling light on a power conduit — but every
    /// dev-authored / spawned ship (e.g. the core Baleen: 31 ceiling lights, 0 adjacent conduits) drops
    /// them freely and wires them through the electrical (GPM) graph, bypassing CheckFit entirely. A planner
    /// produces spawn-placed ships, so we mirror the spawn path: the light places and the missing conduit is
    /// surfaced as a soft advisory rather than a hard failure. See issue #11.
    /// </summary>
    private static readonly HashSet<string> SoftReqs = new(StringComparer.Ordinal) { "IsPowerConduit" };

    /// <summary>Every req condition present, no forbid condition present (CondTrigger.Triggered, bAND path).
    /// A missing <see cref="SoftReqs"/> condition does not fail the cell; it reports via <paramref name="softWhy"/>.</summary>
    private static bool CellPasses(TileConds conds, IReadOnlyList<string> reqConds, IReadOnlyList<string> forbidConds,
        int x, int y, out string? why, out int rank, out string? softWhy)
    {
        why = null;
        rank = GenericRank;
        softWhy = null;
        var at = conds.At(x, y);   // null == off-ship / empty tile (empty entries are pruned, never a non-null empty dict)
        string? missingReq = null;
        foreach (var rc in reqConds)
            if (at is null || !at.ContainsKey(rc))
            {
                // A soft req (e.g. a light's power conduit) is advisory-only: note it, but don't fail the cell.
                if (SoftReqs.Contains(rc)) { softWhy ??= ReasonForReq(rc); continue; }
                // A staged-build cond names the missing prerequisite part; the loots that carry one
                // list generic conds (IsFixture, IsFloor, …) first, so first-missing would hide it.
                if (StagedConds.Contains(rc))
                {
                    why = ReasonForReq(rc);
                    rank = StagedRank;
                    return false;
                }
                missingReq ??= rc;
            }
        if (missingReq is not null)
        {
            why = ReasonForReq(missingReq);
            return false;
        }
        if (at is not null)
        {
            // A SEALED-FLOOR surface is a valid base to build on and stand on — even when that floor is a FLOOR
            // FIXTURE, e.g. an under-floor storage bin / rack (ItmRackUnder01, ItmStorageBinFloor…). Those tag
            // their walkable tiles IsFloorSealed + IsFixture but never IsObstruction, and the game lets you build
            // on them (and reach adjacent fixtures across them). The common TILObstruction forbid mask lists
            // IsFixture, so without this a fixture placed on — or reaching over — such a floor false-flags as
            // "already occupied". So IsFixture doesn't block on a sealed floor; a genuine IsObstruction still does.
            var floorFixtureOk = at.ContainsKey("IsFloorSealed");
            foreach (var fc in forbidConds)
            {
                if (floorFixtureOk && fc == "IsFixture") continue;
                if (at.ContainsKey(fc))
                {
                    why = ReasonForForbid(fc);
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// The mating face bounding new construction, or null when nothing bounds it (no installed port, or a
    /// port whose DockA/DockB give no direction — the game leaves its bounds infinite in both those cases).
    /// Only the single bounding port is consulted: a design with a Secondary airlock facing into the hull
    /// is legal, because the game never derives an envelope from it (<see cref="ProblemScan.BoundingPort"/>).
    /// </summary>
    private static (bool AxisY, int Dir, double Face)? BoundingFace(ShipDocument doc)
    {
        if (ProblemScan.BoundingPort(doc, doc.Catalog) is not { } p) return null;
        if (!ProblemScan.TryGetFace(doc.Part(p)!, p, out var axisY, out var dir, out var face)) return null;
        return (axisY, dir, face);
    }

    /// <summary>True if the tile lies beyond the bounding mating face (ring-inclusive, as the game tests it).</summary>
    private static bool Beyond((bool AxisY, int Dir, double Face) env, int x, int y) =>
        (((env.AxisY ? y : x) + 0.5) - env.Face) * env.Dir > 0.01;

    // Friendly reasons for the ghost / problem grouping. Socket masks lean on a
    // small vocabulary of tile conditions; anything unmapped falls back to the raw
    // condition so the reason is never empty.
    private static string ReasonForReq(string cond) => cond switch
    {
        "IsFloor" or "IsFloorSealed" => "needs a sealed floor beneath",
        "IsWall" => "needs a wall alongside",
        "IsHull" => "needs hull structure",
        "IsPortal" => "needs a doorway",
        "IsPowerConduit" => "no power conduit adjacent",   // soft advisory (see SoftReqs) — overhead lights mount on a POWR conduit
        "IsFusionFieldCoilsFixture" =>
            "needs installed Fusion Field Coils beneath — build the Field Coils first (their centre tile must stay open to space)",
        "IsFusionReactorCoreFixture" =>
            "needs an installed Fusion Reactor Core beneath — build the core on its Field Coils first",
        _ => $"needs {cond}",
    };

    private static string ReasonForForbid(string cond) => cond switch
    {
        "IsObstruction" or "IsFixture" or "IsFixtureExt" or "IsItemTile" or "IsFloorFlex" or "IsSubTile" => "tile is already occupied",
        "IsWall" => "blocked by a wall",
        "IsFloor" or "IsFloorSealed" => "a floor is in the way here — this tile must stay unfloored",
        _ => $"blocked by {cond}",
    };
}
