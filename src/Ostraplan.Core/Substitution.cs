using System.IO;
using System.Text.Json.Nodes;

namespace Ostraplan.Core;

/// <summary>
/// An item in a source ship whose def Ostraplan can't resolve — the mod defining it isn't in the loaded data.
/// Carries the raw world pose rather than a tile: with no def there is no footprint, and a tile can't be derived
/// without one (<see cref="ShipGrid.TemplateTile"/> needs the width/height). A stand-in's own footprint supplies
/// that once the user picks one.
/// </summary>
public sealed record UnresolvedItem(string StrID, string DefName, double FX, double FY, double FRotation);

/// <summary>
/// Standing in for parts whose mod isn't loaded.
///
/// <para><b>Why this matters.</b> An unresolved item is invisible to Ostraplan but still present in the save: the
/// import skips it (no def ⇒ no footprint, no tile conditions) while a write-back preserves its raw <c>aItems</c>
/// entry. That is not a cosmetic gap. Rooms, the grid frame and the rating are all computed from the
/// <b>document</b>, so a missing modded <b>wall</b> lets a room flood straight through where it stands, and a
/// missing part at the hull edge shrinks the frame below the one the game rebuilds around the real item — each
/// corrupting the ship on load, exactly the way a hand-built grid frame did (see GAME-INTERNALS §8).</para>
///
/// <para><b>The stand-in model.</b> The user picks a real part to take the unresolved item's place. It is placed
/// at the original's world centre (with the stand-in's own footprint) and tagged with the original's
/// <see cref="Placement.OriginStrID"/>. On write-back the original item and CO are dropped and the stand-in is
/// written as a fresh part, so <b>what Ostraplan models is exactly what lands in the save</b> — the Law holds
/// rather than being quietly approximated.</para>
///
/// <para>The tag is also the whole persistence story. A stand-in is recognisable by its origin id naming a real
/// save item that is <b>not</b> one of the modelled originals, a combination only this flow produces. So it
/// survives an <c>.oplan</c> round-trip for free, deleting the stand-in reverts to leaving the modded item alone,
/// and moving one keeps the substitution (origin ids survive a move).</para>
/// </summary>
public static class Substitution
{
    /// <summary>
    /// Every top-level item of the source ship whose def doesn't resolve and that no stand-in covers yet.
    /// Recomputed from the save record rather than remembered, so it is correct after an <c>.oplan</c> reopen and
    /// after a stand-in is added or deleted mid-session.
    /// </summary>
    public static IReadOnlyList<UnresolvedItem> Outstanding(ShipDocument doc, SaveShipContext ctx, Catalog catalog)
    {
        var covered = StandInOriginIds(doc, ctx);
        var list = new List<UnresolvedItem>();
        foreach (var (id, node) in ctx.ItemsById)
        {
            if (covered.Contains(id) || !IsTopLevel(node)) continue;
            if (Str(node, "strName") is not { } def || catalog.Lookup(def) is not null) continue;
            list.Add(new UnresolvedItem(id, def, Dbl(node, "fX"), Dbl(node, "fY"), Dbl(node, "fRotation")));
        }
        return [.. list.OrderBy(u => u.DefName, StringComparer.Ordinal)];
    }

    /// <summary>The unresolved defs and how many items use each — the shape a report wants.</summary>
    public static IReadOnlyList<SkippedDef> OutstandingDefs(ShipDocument doc, SaveShipContext ctx, Catalog catalog) =>
        [.. Outstanding(doc, ctx, catalog)
            .GroupBy(u => u.DefName, StringComparer.Ordinal)
            .Select(g => new SkippedDef(g.Key, g.Count()))
            .OrderByDescending(s => s.Count).ThenBy(s => s.DefName, StringComparer.Ordinal)];

    /// <summary>
    /// The source item ids some placement stands in for: its <see cref="Placement.OriginStrID"/> names a real save
    /// item that is not one of the modelled originals. Only the substitution flow produces that combination — a
    /// normal imported part's origin is always in <see cref="SaveShipContext.Origins"/>.
    /// </summary>
    public static HashSet<string> StandInOriginIds(ShipDocument doc, SaveShipContext ctx)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in doc.Placements)
            if (p.OriginStrID is { } id && !ctx.Origins.ContainsKey(id) && ctx.ItemsById.ContainsKey(id))
                ids.Add(id);
        return ids;
    }

    /// <summary>
    /// What a write-back must drop for the stand-ins, as substituted original → the items parented to it
    /// (transitively). A replaced container takes its contents with it exactly as a deleted one does, so the
    /// per-root shape lets the caller both drop them and report the loss.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> StandInDrops(ShipDocument doc, SaveShipContext ctx)
    {
        var roots = StandInOriginIds(doc, ctx);
        var drops = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (roots.Count == 0) return drops;

        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (id, node) in ctx.ItemsById)
            if (ParentOf(node) is { } parent)
                (children.TryGetValue(parent, out var l) ? l : children[parent] = []).Add(id);

        foreach (var root in roots)
        {
            var kids = new List<string>();
            var queue = new Queue<string>([root]);
            while (queue.Count > 0)
                if (children.TryGetValue(queue.Dequeue(), out var direct))
                    foreach (var kid in direct) { kids.Add(kid); queue.Enqueue(kid); }
            drops[root] = kids;
        }
        return drops;
    }

    /// <summary>
    /// The stand-in placement for an unresolved item: <paramref name="defName"/> at the original's world centre,
    /// tiled by the stand-in's own footprint, tagged with the original's id. <see cref="Placement.IsGiven"/> is set
    /// — it takes the place of structure that was already on the ship, and the game re-validates nothing on load,
    /// so it must not be flagged as illegal new construction.
    /// </summary>
    public static Placement StandIn(UnresolvedItem item, string defName, Catalog catalog, SaveShipContext ctx)
    {
        var part = catalog.Lookup(defName)
            ?? throw new InvalidDataException($"'{defName}' isn't in the loaded data, so it can't stand in for anything.");
        var vx = Dbl2(ctx.ShipRecord, "vShipPos", "x");
        var vy = Dbl2(ctx.ShipRecord, "vShipPos", "y");
        var (col, row, rot) = ShipGrid.TemplateTile(
            item.FX, item.FY, item.FRotation, part.Item.Width, part.Item.Height, vx, vy);
        return new Placement
        {
            DefName = defName, X = col, Y = row, Rot = rot, IsGiven = true, OriginStrID = item.StrID,
        };
    }

    // ---- json helpers (the save record is a mutable node tree) ----

    private static bool IsTopLevel(JsonNode? n) =>
        n?["strParentID"] is null && n?["strSlotParentID"] is null;

    private static string? ParentOf(JsonNode? n) =>
        Str(n, "strParentID") ?? Str(n, "strSlotParentID");

    private static string? Str(JsonNode? n, string prop) =>
        n?[prop] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static double Dbl(JsonNode? n, string prop) =>
        n?[prop] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;

    private static double Dbl2(JsonNode? n, string a, string b) => Dbl(n?[a], b);
}
