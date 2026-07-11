namespace Ostraplan.Core;

/// <summary>
/// Outcome of a placement legality test: whether the pose fits, the world tiles
/// that failed (for the red ghost / hazard tint), and a one-line human reason.
/// </summary>
public sealed record FitResult(bool Ok, IReadOnlyList<(int X, int Y)> FailedCells, string? Reason)
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
///   <item>Envelope (opt-in): no ring cell may fall beyond a docking port's mating
///   face. The game bounds only the first port; Ostraplan bounds <b>all</b> ports
///   (ring-inclusive) — provably never allows what the game refuses, identical to
///   the game on the single-port ships that are the norm.</item>
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

        // re-validating a placed part: lift its own conditions so it doesn't fail
        // against itself, restore them no matter what (UI thread only; no re-entrancy
        // since TileConds.Apply raises no events).
        var selfItem = self is not null ? doc.Part(self)?.Item : null;
        if (selfItem is not null) doc.Conds.Apply(self!, selfItem, -1);
        try
        {
            var failed = new List<(int, int)>();
            string? reason = null;
            var reasonRank = int.MaxValue;   // staged-build reasons outrank generic ones (see CellPasses)

            for (var r = 0; r < rh; r++)
                for (var c = 0; c < rw; c++)
                {
                    // ring cell (r,c) -> world tile: footprint interior sits at c,r in 1..W/1..H
                    var wx = x - 1 + c;
                    var wy = y - 1 + r;

                    if (includeEnvelope && BeyondAnyFace(doc, wx, wy))
                    {
                        failed.Add((wx, wy));
                        if (reasonRank == int.MaxValue) { reason = "beyond the airlock's mating face"; reasonRank = GenericRank; }
                        continue;
                    }

                    var idx = r * rw + c;
                    var reqConds = idx < reqs.Length ? doc.Catalog.LootConds(reqs[idx]) : [];
                    var forbidConds = idx < forbids.Length ? doc.Catalog.LootConds(forbids[idx]) : [];
                    if (reqConds.Count == 0 && forbidConds.Count == 0) continue;   // unconstrained

                    if (!CellPasses(doc.Conds, reqConds, forbidConds, wx, wy, out var why, out var rank))
                    {
                        failed.Add((wx, wy));
                        if (rank < reasonRank) { reason = why; reasonRank = rank; }
                    }
                }

            return failed.Count == 0 ? FitResult.Legal : new FitResult(false, failed, reason);
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

    /// <summary>Every req condition present, no forbid condition present (CondTrigger.Triggered, bAND path).</summary>
    private static bool CellPasses(TileConds conds, IReadOnlyList<string> reqConds, IReadOnlyList<string> forbidConds,
        int x, int y, out string? why, out int rank)
    {
        why = null;
        rank = GenericRank;
        var at = conds.At(x, y);   // null == off-ship / empty tile (empty entries are pruned, never a non-null empty dict)
        string? missingReq = null;
        foreach (var rc in reqConds)
            if (at is null || !at.ContainsKey(rc))
            {
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

    /// <summary>True if the tile lies beyond any installed docking port's mating face (all ports, ring-inclusive).</summary>
    private static bool BeyondAnyFace(ShipDocument doc, int x, int y)
    {
        foreach (var p in doc.Placements)
        {
            var part = doc.Part(p);
            if (part is null || !ProblemScan.IsDocksys(part, doc.Catalog)) continue;
            if (!ProblemScan.TryGetFace(part, p, out var axisY, out var dir, out var face)) continue;
            var center = (axisY ? y : x) + 0.5;
            if ((center - face) * dir > 0.01) return true;
        }
        return false;
    }

    // Friendly reasons for the ghost / problem grouping. Socket masks lean on a
    // small vocabulary of tile conditions; anything unmapped falls back to the raw
    // condition so the reason is never empty.
    private static string ReasonForReq(string cond) => cond switch
    {
        "IsFloor" or "IsFloorSealed" => "needs a sealed floor beneath",
        "IsWall" => "needs a wall alongside",
        "IsHull" => "needs hull structure",
        "IsPortal" => "needs a doorway",
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
