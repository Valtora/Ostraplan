namespace Ostraplan.Core;

/// <summary>One labelled figure on an item's info panel.</summary>
/// <param name="Label">What the game calls it.</param>
/// <param name="Value">The figure, already formatted with its unit.</param>
/// <param name="Desc">The line under it, or null. Carries the game's <c>[us]</c> grammar token verbatim.</param>
/// <param name="Color">Name into the colour table, or null.</param>
public sealed record InfoFigure(string Label, string Value, string? Desc = null, string? Color = null);

/// <summary>
/// What the container view shows about one item, assembled the way the game's own mega tool tip assembles it, plus
/// the raw conditions a save editor needs (#37).
///
/// <para><b>Faithful means small.</b> The game's item tool tip is five modules and only two of them carry much:
/// the name/description block and the value. Its <c>NumberModule</c> renders conditions declaring
/// <c>nDisplayType == 1</c>, and <b>four</b> conditions in stock data do, so an ordinary crate shows none. A panel
/// that filled itself with every <c>Stat*</c> on the def would be showing something the game deliberately does
/// not, which is why <see cref="RawConds"/> is separate and labelled as the extra that it is.</para>
///
/// <para>Built here rather than in the window so it can be tested without one, and so the ordering stays in one
/// place: the game's module order is name, value, figures, gas.</para>
/// </summary>
/// <param name="Name">What to show as the title: the item's own name where it has one, else the def's.</param>
/// <param name="StockName">The def's own name, for the rename box to offer as the way back.</param>
/// <param name="DefName">The internal def name, which is the only stable handle when save editing.</param>
/// <param name="Desc">The def's flavour description, or null.</param>
/// <param name="Factions">Friendly faction names this item belongs to. Empty for most items.</param>
/// <param name="Price">Base price, or null when the def declares none (the game hides the module then).</param>
/// <param name="Figures">The <c>nDisplayType == 1</c> conditions this item actually carries.</param>
/// <param name="Gases">Gas payload lines, and the pressure, when the item holds any.</param>
/// <param name="RawConds">Every <c>Stat*</c> the def declares, sorted. Ostraplan's own addition.</param>
/// <param name="Renameable">Whether the rename box should be offered. False only when the def is unresolvable.</param>
public sealed record CargoInfo(
    string Name,
    string StockName,
    string DefName,
    string? Desc,
    IReadOnlyList<string> Factions,
    double? Price,
    IReadOnlyList<InfoFigure> Figures,
    IReadOnlyList<InfoFigure> Gases,
    IReadOnlyList<InfoFigure> RawConds,
    bool Renameable)
{
    /// <summary>
    /// Assemble the panel for one cargo item.
    /// </summary>
    /// <param name="item">The item. Its own condition values are the def's: a save's per-instance amounts are not
    /// carried on the cargo tree, so what is shown is what the def declares, which is what a design describes.</param>
    /// <param name="doc">The design, for the faction name table it carries (see
    /// <see cref="ShipDocument.FactionNames"/>).</param>
    public static CargoInfo For(CargoItem item, ShipDocument doc)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(doc);

        var catalog = doc.Catalog;
        var def = catalog.Lookup(item.DefName);
        var stock = def?.Friendly ?? item.DefName;
        var vals = def?.StartingCondValues ?? new Dictionary<string, double>();

        // The game's NumberModule, in its own order: whatever the def declares that is also a display-type-1
        // condition. Four qualify on stock data, so this is empty for nearly everything.
        var figures = new List<InfoFigure>();
        foreach (var (cond, amount) in vals.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            if (catalog.CondDisplay.TryGetValue(cond, out var d))
                figures.Add(new InfoFigure(d.Friendly ?? cond, d.Format(amount), d.Desc, d.Color));

        // The GasModule: the species this container actually holds, plus the pressure the game derives from them.
        // ContainerFill already knows both, and reusing it keeps the fill editor and this panel from disagreeing.
        var gases = new List<InfoFigure>();
        if (def is not null && ContainerFill.Describe(def, catalog) is { } spec)
        {
            foreach (var line in spec.Lines)
            {
                var amount = spec.Stock.GetValueOrDefault(line.Cond);
                if (amount <= 0) continue;
                gases.Add(new InfoFigure(line.Label, Amount(amount) + (line.IsGas ? " mol" : " kg")));
            }
            if (spec.HasGas && gases.Count > 0)
                gases.Add(new InfoFigure("Pressure", Amount(spec.PressureFor(ContainerFill.TotalMols(spec.Stock))) + " kPa"));
        }

        // Ostraplan's own: the def's raw stats, which the game's panel hides and a save editor wants. Kept apart
        // from Figures so the panel can say which half is the game's.
        var raw = vals
            .Where(kv => kv.Key.StartsWith("Stat", StringComparison.Ordinal))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new InfoFigure(kv.Key, Amount(kv.Value)))
            .ToList();

        return new CargoInfo(
            Name: item.CustomName ?? stock,
            StockName: stock,
            DefName: item.DefName,
            Desc: def?.Desc,
            Factions: item.Factions.Select(doc.FactionName).ToList(),
            Price: def is { BasePrice: > 0 } ? def.BasePrice : null,
            Figures: figures,
            Gases: gases,
            RawConds: raw,
            Renameable: Rename.CanRename(def));
    }

    /// <summary>A raw amount, trimmed of trailing zeroes — the same shape the ship inspector's stats use, so the
    /// two read alike.</summary>
    private static string Amount(double v) =>
        v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
