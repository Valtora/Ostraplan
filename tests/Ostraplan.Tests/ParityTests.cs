using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ostraplan.Core;
using Xunit;
using Xunit.Abstractions;

namespace Ostraplan.Tests;

/// <summary>
/// The Law gate: recompute rooms/certification/rating for every core ship template
/// and compare against the game's own baked <c>aRooms</c>/<c>aRating</c>. All core
/// templates carry <c>aRooms</c> (192-ship rooms + certification gate); only a
/// couple carry <c>aRating</c>. No-ops without the install.
/// </summary>
public class ParityTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    private static IEnumerable<(string File, ShipTemplate Ship)> CoreShips(GameEnv env)
    {
        var dir = Path.Combine(env.CoreDataDir, "ships");
        if (!Directory.Exists(dir)) yield break;
        foreach (var path in Directory.EnumerateFiles(dir, "*.json"))
        {
            string text;
            try { text = File.ReadAllText(path); } catch { continue; }
            foreach (var ship in ShipTemplate.ParseFile(text))
                if (ship.Rooms.Count > 0)
                    yield return (Path.GetFileName(path), ship);
        }
    }

    /// <summary>
    /// Templates whose baked room data a faithful port cannot (and should not) reproduce
    /// — each an exotic ship, none affecting the Law (interior-compartment certification
    /// and rating). Named + justified per the parity-gate rule.
    /// </summary>
    internal static readonly Dictionary<string, string> RoomExclusions = new()
    {
        ["Coffin.json"] = "malformed template: stored aRooms tile indices (rows 0-7) are inconsistent with item positions (rows 1-13); no port can reproduce them",
        ["Ostrich A8R.json"] = "aero slant-wall hull: the game files an ItmWallAero1x2Slant wall tile into the adjacent void room; a stricter-than-game discrepancy (walls don't certify, cannot be a Law false positive)",
        ["ResAero01.json"] = "aero slant-wall hull: same ItmWallAero1x2Slant quirk as Ostrich A8R (wall tile filed into a Blank room)",
        ["Vector2.json"] = "interceptor airlock: the game separates the airlock opening from the exterior void by a rule 4-connectivity flood does not reproduce; affects only which Blank/void region owns a few exterior tiles",
    };

    [Fact]
    public void Rooms_parity_across_all_core_templates()
    {
        if (TestData.Game is not { } g) return;
        var resolver = new PartResolver(g.Index);

        int total = 0, passed = 0;
        var unexpected = new List<string>();
        var excludedSeen = new List<string>();
        foreach (var (file, ship) in CoreShips(g.Env))
        {
            total++;
            var grid = ShipGrid.FromTemplate(ship, resolver, g.Catalog);
            var partition = RoomBuilder.Build(grid);
            var diff = RoomParity.Compare(grid, partition, ship, out _);
            if (diff is null) passed++;
            else if (RoomExclusions.ContainsKey(file)) excludedSeen.Add($"{file}: {diff}");
            else unexpected.Add($"{file} ({ship.Name}): {diff}");
        }

        _out.WriteLine($"rooms parity: {passed}/{total} core templates match ({excludedSeen.Count} known exclusions)");
        foreach (var f in excludedSeen) _out.WriteLine("  (excluded) " + f);
        foreach (var f in unexpected) _out.WriteLine("  FAIL " + f);
        Assert.True(unexpected.Count == 0, $"{unexpected.Count} templates failed rooms parity unexpectedly (see test output)");
        // guard the exclusion list against silent bit-rot: every excluded ship must still be failing
        Assert.True(excludedSeen.Count == RoomExclusions.Count,
            $"only {excludedSeen.Count}/{RoomExclusions.Count} excluded templates still fail — prune RoomExclusions");
    }

    [Fact]
    public void Certification_parity_across_all_core_templates()
    {
        if (TestData.Game is not { } g) return;
        var resolver = new PartResolver(g.Index);
        var specs = RoomCertifier.LoadSpecs(g.Index);
        Assert.True(specs.Count >= 15, $"only {specs.Count} room specs loaded");
        var priority = specs.ToDictionary(s => s.Name, s => s.Priority);
        int Prio(string spec) => priority.GetValueOrDefault(spec, 0);

        int checkedRooms = 0, matchedRooms = 0, underCert = 0, exteriorVoid = 0;
        var unexpected = new List<string>();
        foreach (var (file, ship) in CoreShips(g.Env))
        {
            if (RoomExclusions.ContainsKey(file)) continue;   // rooms don't match → can't compare specs

            var grid = ShipGrid.FromTemplate(ship, resolver, g.Catalog);
            var partition = RoomBuilder.Build(grid);
            RoomCertifier.CertifyAll(partition, specs, g.Catalog);
            if (RoomParity.Compare(grid, partition, ship, out var map) is not null) continue;   // covered by rooms test

            foreach (var (m, s) in map)
            {
                checkedRooms++;
                var mine = partition.Rooms[m].RoomSpec;
                var stored = ship.Rooms[s].RoomSpec;
                if (mine == stored) { matchedRooms++; continue; }

                // Two documented corpus-only differences, neither reachable for an
                // Ostraplan-authored design (which has no contained cargo and a bounded interior):
                //  • under-certification of a real compartment — the game counts contained /
                //    slotted / pre-populated cargo (GetCOs bSubObjects) that the top-level aItems
                //    loader can't fully resolve, so a room can miss a required-part count.
                //  • exterior over-claim — the recomputed Void/Outside room merges the far empty
                //    margin the game leaves unroomed, so it can aggregate enough exterior cargo to
                //    read CargoRoomExterior where the game's bounded exterior reads Blank.
                var voidRoom = partition.Rooms[m].Void || ship.Rooms[s].Void;
                if (Prio(mine) < Prio(stored)) underCert++;                 // never over-certifies a compartment
                else if (voidRoom) exteriorVoid++;                          // exterior-void aggregation
                else unexpected.Add($"{file} ({ship.Name}): room {m} ({partition.Rooms[m].TileCount} tiles, void={partition.Rooms[m].Void}) OVER-certified '{mine}' (prio {Prio(mine)}) vs stored '{stored}' (prio {Prio(stored)})");
            }
        }

        _out.WriteLine($"certification parity: {matchedRooms}/{checkedRooms} rooms exact; " +
            $"{underCert} under-certified (contained cargo), {exteriorVoid} exterior-void aggregation, {unexpected.Count} unexpected");
        foreach (var f in unexpected.Take(30)) _out.WriteLine("  FAIL " + f);
        // The Law-relevant guarantee: Ostraplan never OVER-certifies a real (non-void) compartment.
        Assert.True(unexpected.Count == 0, $"{unexpected.Count} compartments over-certified vs the game (see test output)");
        Assert.True(matchedRooms >= checkedRooms * 0.97,
            $"only {matchedRooms}/{checkedRooms} rooms certify exactly — below the 97% corpus floor");
    }
}

/// <summary>Partition-equality check between recomputed rooms and a template's baked aRooms.</summary>
internal static class RoomParity
{
    /// <summary>Null if the room partition (tile sets) and Void flags match; else a one-line
    /// diff. On success <paramref name="my2st"/> maps each recomputed room index to its
    /// stored counterpart (for spec comparison).</summary>
    public static string? Compare(ShipGrid grid, RoomPartition partition, ShipTemplate ship, out Dictionary<int, int> my2st)
    {
        my2st = [];
        var n = grid.TileCount;

        // stored tile → room index
        var stored = new int[n];
        System.Array.Fill(stored, -1);
        for (var s = 0; s < ship.Rooms.Count; s++)
            foreach (var t in ship.Rooms[s].TileIndices)
                if (t >= 0 && t < n) stored[t] = s;

        var mine = partition.TileRoom;

        // Per-tile bijection over NON-PORTAL tiles: mine and stored must induce the same
        // compartment partition. Two classes of tile are compared leniently, neither of
        // which affects the Law (compartment certification + rating):
        //  • Door/hatch (portal) tiles — the game files a single opening tile into one of
        //    its two adjacent rooms by a subtle, item-dependent RoomA/RoomB rule; which
        //    side owns it changes no compartment.
        //  • Exterior tiles my Void/Outside room over-claims — the game does not bother
        //    to room the far empty margin around a small ship (its Outside room is bounded
        //    by trim); the Outside room is Blank and never counts toward the rating, so its
        //    exact extent is irrelevant. My interior compartments must still match exactly.
        var st2my = new Dictionary<int, int>();
        for (var t = 0; t < n; t++)
        {
            if (grid.Has(t, "IsPortal")) continue;   // opening tile — filed leniently
            int m = mine[t], s = stored[t];
            if (m >= 0 && s < 0)
            {
                if (!partition.Rooms[m].Void)   // an interior (sealed) tile the game rooms differently — a real defect
                    return $"tile #{t} [{grid.Col(t)},{grid.Row(t)}] in my interior room {m}, unassigned by the game";
                continue;   // exterior tile my Outside room over-claims — harmless
            }
            if (m < 0 && s >= 0)
                return $"tile #{t} [{grid.Col(t)},{grid.Row(t)}] roomed by the game (stored {s}), unassigned by me";
            if (m < 0) continue;   // both unroomed (walls)
            if (my2st.TryGetValue(m, out var es) && es != s)
                return $"my room {m} maps to stored {es} and {s} (rooms merged/split at tile #{t})";
            if (st2my.TryGetValue(s, out var em) && em != m)
                return $"stored room {s} maps to my {em} and {m} (rooms merged/split at tile #{t})";
            my2st[m] = s; st2my[s] = m;
        }

        // Void parity on matched rooms
        foreach (var (m, s) in my2st)
            if (partition.Rooms[m].Void != ship.Rooms[s].Void)
                return $"void mismatch: my room {m} Void={partition.Rooms[m].Void}, stored room {s} bVoid={ship.Rooms[s].Void} ({partition.Rooms[m].TileCount} tiles)";

        return null;
    }
}
