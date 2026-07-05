namespace Ostraplan.Core;

/// <summary>One line of the bill: a buildable part type and how many are placed, with the raw install
/// kit(s) it consumes. The kit is the part's own uninstalled form (install <c>aInputs</c>, 1:1 with the
/// part — e.g. <c>ItmWallAERO01</c> consumes <c>TIsWallAERO01Uninstalled</c>), so the count is both the
/// number of parts and the number of kits needed.</summary>
public sealed record BomLine(string DefName, string Friendly, string Category, int Count, IReadOnlyList<string> Kits);

/// <summary>
/// The bill of materials for a set of placements: how many of each buildable part is placed (= how many
/// install kits are needed to build it). Non-buildable parts — raw hull, ship systems that ship on a
/// template with no install job, the fixed primary airlock — carry no install kit and can't be built by
/// the player, so they are reported only as a tally, not as buildable lines.
/// </summary>
public sealed record Bom(
    IReadOnlyList<BomLine> Lines,   // one row per buildable part def, sorted by category then name
    int BuildableCount,             // total placed buildable parts (sum of the line counts)
    int NonBuildableCount)          // placed parts with no install kit (raw/given structure, the airlock)
{
    public int DistinctParts => Lines.Count;
    public int TotalParts => BuildableCount + NonBuildableCount;
}

/// <summary>
/// Builds a <see cref="Bom"/> from placed parts by counting each part's install kit. Pure and
/// data-only: it reads the catalog's per-part install inputs, it does not simulate anything.
/// </summary>
public static class BillOfMaterials
{
    /// <summary>The bill for the given placements (a selection, or the whole ship).</summary>
    public static Bom Compute(ShipDocument doc, IEnumerable<Placement> placements)
    {
        var lines = new Dictionary<string, (PartDef Part, int Count)>(StringComparer.Ordinal);
        var nonBuildable = 0;

        foreach (var p in placements)
        {
            var part = doc.Part(p);
            if (part is null || part.Inputs.Length == 0)   // unresolved, raw/given, or the fixed airlock
            {
                nonBuildable++;
                continue;
            }
            if (lines.TryGetValue(p.DefName, out var cur)) lines[p.DefName] = (cur.Part, cur.Count + 1);
            else lines[p.DefName] = (part, 1);
        }

        var rows = lines.Values
            .Select(v => new BomLine(v.Part.DefName, v.Part.Friendly, v.Part.Category, v.Count, v.Part.Inputs))
            .OrderBy(r => Array.IndexOf(Catalog.Categories, r.Category))   // palette tab order; -1 (none) sorts first
            .ThenBy(r => r.Friendly, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new Bom(rows, rows.Sum(r => r.Count), nonBuildable);
    }

    /// <summary>The bill for the whole ship.</summary>
    public static Bom ComputeAll(ShipDocument doc) => Compute(doc, doc.Placements);
}
