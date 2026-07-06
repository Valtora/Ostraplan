namespace Ostraplan.Core;

/// <summary>A ship's worth: the exact pristine <see cref="BuildCost"/> (what the parts cost to build), the
/// game's room-based <see cref="ShipValue"/> (what a broker values it at), and rough broker
/// <see cref="SellEstimate"/>/<see cref="BuyEstimate"/> prices. The build cost is exact; the broker figures are
/// estimates (the real modifier is rolled per-kiosk and shifts with faction standing) and exclude fuel/cargo.</summary>
public sealed record ShipValueEstimate(double BuildCost, double ShipValue, double SellEstimate, double BuyEstimate);

/// <summary>
/// Values a ship the way the game does. Two very different numbers:
/// <list type="bullet">
///   <item><b>Build cost</b> = Σ <c>StatBasePrice</c> over every part — the pristine parts value
///   (<c>Ship.GetPartsValue</c>/<c>fSpawnPrice</c>). What it costs to assemble.</item>
///   <item><b>Ship value</b> = what a <b>broker</b> prices it at (<c>Ship.GetShipValue</c>): each installed part's
///   base price × 1.25 (Pristine) summed per room, each room × its <c>ValueModifier</c> (Reactor 1.6, Wellness
///   1.9, Luxury Quarters 2.0…), the whole thing × 3 when the ship has an O2 pump. Much larger than the build
///   cost.</item>
/// </list>
/// The broker then buys from you at ~0.8× and sells to you at ~1.25× that value; those factors are
/// representative (real ones are per-kiosk conds). Verified against a save: Charon's Σ roomValue $1.20M × 3 =
/// $3.60M ship value, in-game sell $2.88M = ×0.8. The estimate is a <b>dry</b> hull value — it excludes fuel and
/// cargo, which a spawned/exported design doesn't carry.
/// </summary>
public static class ShipValue
{
    /// <summary>The Pristine condition price multiplier — a fixed game constant.</summary>
    public const double PristineMarkup = 1.25;

    /// <summary>Added to the base 1.0 when the ship has an O2 pump, i.e. the value ×3 (game: nO2PumpCount &gt; 0).</summary>
    public const double O2Multiplier = 2.0;

    /// <summary>Representative: a broker buys a ship from you at ~0.8× its value.</summary>
    public const double BrokerSellFactor = 0.80;

    /// <summary>Representative: a broker sells a ship to you at ~1.25× its value.</summary>
    public const double BrokerBuyFactor = 1.25;

    public static ShipValueEstimate Estimate(ShipDocument doc, Catalog catalog, IReadOnlyList<RoomSpecDef> specs)
    {
        double buildCost = 0;
        var hasO2Pump = false;
        foreach (var p in doc.Placements)
        {
            if (catalog.Lookup(p.DefName) is not { } part) continue;
            buildCost += part.BasePrice;
            if (part.StartingConds.Contains("IsAirPump")) hasO2Pump = true;
        }

        // room-based value = Σ_rooms (Σ installed part base×1.25) × room ValueModifier, then ×3 for an O2 pump
        var grid = ShipGrid.FromDocument(doc, catalog);
        var partition = RoomBuilder.Build(grid);
        RoomCertifier.CertifyAll(partition, specs, catalog);
        var modifier = specs.ToDictionary(s => s.Name, s => s.ValueModifier, StringComparer.Ordinal);

        double roomsValue = 0;
        foreach (var room in partition.Rooms)
        {
            if (room.Void) continue;
            var vm = modifier.GetValueOrDefault(room.RoomSpec, 1.0);
            foreach (var part in room.Parts)
                roomsValue += part.Part.StartingCondValues.GetValueOrDefault("StatBasePrice") * PristineMarkup * vm;
        }
        var shipValue = roomsValue * (1 + (hasO2Pump ? O2Multiplier : 0));

        return new ShipValueEstimate(buildCost, shipValue, shipValue * BrokerSellFactor, shipValue * BrokerBuyFactor);
    }
}
