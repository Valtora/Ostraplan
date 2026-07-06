namespace Ostraplan.Core;

/// <summary>A ship's estimated worth: its exact pristine parts value, and rough broker resale/purchase prices.
/// Only <see cref="BaseValue"/> is exact; the broker figures are estimates (the real price is rolled per-kiosk
/// and shifts with faction standing).</summary>
public sealed record ShipValueEstimate(double BaseValue, double SellEstimate, double BuyEstimate);

/// <summary>
/// Estimates what a ship is worth, the way the game values one. The exact figure is the <b>pristine parts
/// value</b> — the sum of every part's <c>StatBasePrice</c> — which is what <c>Ship.GetPartsValue()</c> returns
/// for an undamaged ship and what the game stamps as <c>fSpawnPrice</c>. Ostraplan designs are pristine, so no
/// condition factor applies. The broker resale/purchase figures are <b>estimates</b>: a broker buys your ship
/// below its value and sells one to you above it (their margin), but the exact discount is rolled per-kiosk from
/// loot tables and shifts with your faction standing, so no fixed number is correct.
/// </summary>
public static class ShipValue
{
    /// <summary>The Pristine condition price multiplier — a fixed game constant in the kiosk price formula.</summary>
    public const double PristineMarkup = 1.25;

    /// <summary>Representative: a broker buys a ship from you below its parts value. Estimate only.</summary>
    public const double BrokerSellFactor = 0.80;

    /// <summary>Representative: a broker sells a ship to you at (at least) the Pristine markup over parts value.
    /// Estimate only.</summary>
    public const double BrokerBuyFactor = PristineMarkup;

    public static ShipValueEstimate Estimate(ShipDocument doc, Catalog catalog)
    {
        double baseValue = 0;
        foreach (var p in doc.Placements)
            baseValue += catalog.Lookup(p.DefName)?.BasePrice ?? 0;
        return new ShipValueEstimate(baseValue, baseValue * BrokerSellFactor, baseValue * BrokerBuyFactor);
    }
}
