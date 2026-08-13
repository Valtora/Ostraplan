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

/// <summary>One line of a retrofit bill: a buildable part type, how many the ship being retrofitted has now
/// (<paramref name="From"/>) and how many the design wants (<paramref name="To"/>). The difference is what the
/// job costs in kits, in whichever direction it falls.</summary>
public sealed record RetrofitLine(
    string DefName, string Friendly, string Category, int From, int To, IReadOnlyList<string> Kits)
{
    /// <summary>Positive when the design wants more of this part than the ship has, negative when it wants fewer.</summary>
    public int Delta => To - From;

    /// <summary>Install kits to obtain. Zero when the ship already has enough.</summary>
    public int Needed => Math.Max(0, Delta);

    /// <summary>Kits the retrofit hands back: uninstalling a part yields its own uninstalled form, which is the
    /// same kit the bill counts, so a part the design drops is material recovered rather than spent.</summary>
    public int Recovered => Math.Max(0, -Delta);

    /// <summary>The ship already has exactly what the design wants of this part, so it neither costs nor yields.</summary>
    public bool Unchanged => Delta == 0;
}

/// <summary>
/// The bill for retrofitting one ship into another: the same per-part kit counting as <see cref="Bom"/>, netted
/// against what the starting ship already carries.
///
/// <para>Netting per part type is the whole model, and it is exact for materials: install kits of one def are
/// interchangeable, so a wall that stays where it is and a wall moved across the deck cost the same nothing. What
/// the netting deliberately does <b>not</b> price is <i>labour</i> — the uninstall and re-install jobs a move still
/// costs. This is a bill of materials, not a work order.</para>
/// </summary>
public sealed record RetrofitBom(
    string FromShip,                          // display name of the ship being retrofitted
    IReadOnlyList<RetrofitLine> Lines,        // every part type on either side, in palette-tab order
    int NonBuildableFrom, int NonBuildableTo) // placed parts with no install kit, each side
{
    /// <summary>Total install kits to obtain across every line.</summary>
    public int NeededCount => Lines.Sum(l => l.Needed);

    /// <summary>Total kits the retrofit hands back.</summary>
    public int RecoveredCount => Lines.Sum(l => l.Recovered);

    /// <summary>Part types the design wants more of.</summary>
    public int AddedTypes => Lines.Count(l => l.Delta > 0);

    /// <summary>Part types the design wants fewer of.</summary>
    public int RemovedTypes => Lines.Count(l => l.Delta < 0);

    /// <summary>Part types the ship already carries in exactly the right number.</summary>
    public int UnchangedTypes => Lines.Count(l => l.Unchanged);

    /// <summary>True when every buildable part type already matches: the retrofit costs no material at all.</summary>
    public bool NoChange => NeededCount == 0 && RecoveredCount == 0;
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

    /// <summary>
    /// Net one bill against another: what it costs in kits to turn the ship <paramref name="from"/> describes into
    /// the design <paramref name="to"/> describes. Every part type on either side gets a line, including the ones
    /// that come out even, so the whole bill stays readable as a diff rather than only its changed half.
    /// </summary>
    public static RetrofitBom Retrofit(Bom from, Bom to, string fromShip)
    {
        var lines = new Dictionary<string, RetrofitLine>(StringComparer.Ordinal);

        foreach (var l in from.Lines)
            lines[l.DefName] = new RetrofitLine(l.DefName, l.Friendly, l.Category, l.Count, 0, l.Kits);

        foreach (var l in to.Lines)
            lines[l.DefName] = lines.TryGetValue(l.DefName, out var cur)
                ? cur with { To = l.Count }
                : new RetrofitLine(l.DefName, l.Friendly, l.Category, 0, l.Count, l.Kits);

        var rows = lines.Values
            .OrderBy(r => Array.IndexOf(Catalog.Categories, r.Category))   // palette tab order; -1 (none) sorts first
            .ThenBy(r => r.Friendly, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new RetrofitBom(fromShip, rows, from.NonBuildableCount, to.NonBuildableCount);
    }
}
