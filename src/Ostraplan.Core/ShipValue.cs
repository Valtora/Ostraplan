namespace Ostraplan.Core;

/// <summary>A ship's worth: the parts <see cref="BuildCost"/> (what the parts cost to assemble), the
/// game's room-based <see cref="ShipValue"/> (what a kiosk values it at), and the kiosk
/// <see cref="SellEstimate"/>/<see cref="BuyEstimate"/> prices at the core broker rates (0.8× / 1.2×).
/// The estimate covers exactly what the design carries — installed parts plus the gas their tanks start
/// with — and excludes loose cargo (which the game also leaves out of hull value) and any Pristine markup
/// (unreachable on installed parts; see <see cref="ShipValue"/>).</summary>
public sealed record ShipValueEstimate(
    double BuildCost, double ShipValue, double SellEstimate, double BuyEstimate);

/// <summary>
/// Values a ship the way the game does. Two very different numbers:
/// <list type="bullet">
///   <item><b>Build cost</b> = Σ <c>StatBasePrice</c> over every part — the parts value
///   (<c>Ship.GetPartsValue</c>/<c>fSpawnPrice</c>). What it costs to assemble.</item>
///   <item><b>Ship value</b> = what a <b>broker</b> prices it at (<c>Ship.GetShipValue</c>): each installed part's
///   <c>GetBasePrice()</c> (base price + the value of any gas/fuel it holds) summed per room, each room × its
///   <c>ValueModifier</c> (Reactor 1.6, Wellness 1.9, Luxury Quarters 2.0…), the whole thing × 3 when the ship
///   has a <b>working O2 supply</b> — an air pump fed by an installed O2 canister (see <see cref="CountO2Pumps"/>).
///   A part that belongs to no room — one embedded in the wall line with no reachable "use" tile, like the air
///   pump itself — contributes nothing, exactly as in-game (room value only sums room members).</item>
/// </list>
/// <para><b>No Pristine markup — it is unreachable on a designed ship.</b> <c>GetBasePrice</c> adds ×1.25 only
/// to a CO carrying the runtime <c>IsPristine</c> cond, and there are exactly two grants (verified 0.24.0):
/// <c>Ship.BreakIn</c> (first Edit-load of Derelict/Damaged/Used ships — a 2.5% roll per solid undamaged part,
/// 25% with the bonus-derelict flag) and <c>Trader.AddNewItems</c> (kiosk stock <b>items</b>). Installing
/// consumes the item and spawns a fresh CO from the def (which never carries <c>IsPristine</c>), so an installed
/// part is never pristine — and a ship you build or export is not a used/derelict spawn, so it earns no roll
/// either. A design's installed parts are therefore uniformly markup-free; applying ×1.25 across the board (as
/// Ostraplan did before 0.20.0) overshot resale by up to 25% and made exported ships buy high / sell low, and a
/// "pristine bonus" margin (0.20.0–0.24.0) implied a ceiling a built ship can't reach, so both are gone.</para>
/// <para>The broker factors are data (core ship-broker kiosks' conds, <c>loot.json</c>): they buy your ship at
/// <c>DiscountBuy</c> = 0.8× its value and sell to you at <c>DiscountSell</c> = 1.2× (a non-derelict sale is
/// <c>GetShipValue × DiscountBuy</c> exactly — <c>GUIShipBroker.GetQuotedPrice</c> applies no other factor).
/// The estimate includes the gas a part's tank starts with but excludes loose cargo (the game leaves cargo out
/// of hull value too), so a live ship carrying extra/fuller tanks or modded parts can sell for a little more.</para>
/// </summary>
public static class ShipValue
{
    /// <summary>The Pristine markup <c>GetBasePrice</c> applies per part carrying the runtime <c>IsPristine</c>
    /// cond. Reference only: a designed/exported ship's installed parts never carry it (see class remarks), so
    /// this is deliberately NOT applied to the estimate or the export bake.</summary>
    public const double PristineMarkup = 1.25;

    /// <summary>Added to the base 1.0 when the ship has a qualifying O2 pump, i.e. the value ×3
    /// (game: nO2PumpCount &gt; 0 / aO2AirPumps non-empty).</summary>
    public const double O2Multiplier = 2.0;

    /// <summary>The core ship brokers' <c>DiscountBuy</c> cond (data-verified, loot.json): a broker buys a
    /// ship from you at 0.8× its value.</summary>
    public const double BrokerSellFactor = 0.80;

    /// <summary>The core ship brokers' <c>DiscountSell</c> cond (data-verified, loot.json): a broker sells a
    /// ship to you at 1.2× its value.</summary>
    public const double BrokerBuyFactor = 1.2;

    /// <summary>Molar masses in kg/mol — the hardcoded switch in <c>GasContainer.GetGasMass</c> (0.15.1.6).
    /// A gas missing here (notably He3) weighs 0 in-game too, so its gaseous form carries no value; solid He3
    /// and liquid D2O are priced separately by GetBasePrice (see <see cref="PartValue"/>).</summary>
    private static readonly IReadOnlyDictionary<string, double> MolarMassKgPerMol = new Dictionary<string, double>(StringComparer.Ordinal)
    {
        ["H2"] = 0.0020158999999999997,
        ["He2"] = 0.008005204,
        ["CH4"] = 0.016042999999999998,
        ["NH3"] = 0.017030999999999998,
        ["H2O"] = 0.0180153,
        ["N2"] = 0.0280134,
        ["O2"] = 0.0319988,
        ["CO2"] = 0.04401,
        ["H2SO4"] = 0.0980785,
        ["CO"] = 0.02801,
        ["Smoke"] = 0.0980785,
    };

    /// <summary>Installed air pump (IsAirPump + IsInstalled) — the trigger Ship.AddICO gates pumps on.</summary>
    public const string PumpTrigger = "TIsAirPump02Installed";

    /// <summary>Installed O2 RTA canister (IsVesselO2 + IsRTA + IsInstalled) — the can that must feed the pump.</summary>
    public const string O2CanTrigger = "TIsRTAO2Installed";

    public static ShipValueEstimate Estimate(ShipDocument doc, Catalog catalog, IReadOnlyList<RoomSpecDef> specs)
    {
        var grid = ShipGrid.FromDocument(doc, catalog);
        var partition = RoomBuilder.Build(grid);
        RoomCertifier.CertifyAll(partition, specs, catalog);
        return Estimate(grid, partition, catalog, specs);
    }

    /// <summary>Value an already-built grid/partition (shared with the export bake).</summary>
    public static ShipValueEstimate Estimate(ShipGrid grid, RoomPartition partition, Catalog catalog,
        IReadOnlyList<RoomSpecDef> specs)
    {
        double buildCost = 0;
        foreach (var p in grid.Parts)
            buildCost += p.Part.StartingCondValues.GetValueOrDefault("StatBasePrice");

        // room-based value = Σ_rooms (Σ member GetBasePrice) × room ValueModifier, then ×3 for a fed O2 pump
        var modifier = specs.ToDictionary(s => s.Name, s => s.ValueModifier, StringComparer.Ordinal);
        double roomsValue = 0;
        foreach (var room in partition.Rooms)
            roomsValue += RoomValueOf(room, modifier, catalog);
        var shipValue = roomsValue * (1 + (CountO2Pumps(grid, catalog) > 0 ? O2Multiplier : 0));

        return new ShipValueEstimate(buildCost, shipValue,
            shipValue * BrokerSellFactor, shipValue * BrokerBuyFactor);
    }

    /// <summary>
    /// Port of the game's O2-pump registration (<c>Ship.AddICO</c> → <c>ShipStatus.GetO2UnderPump</c>,
    /// decompiled 0.15.1.6): a pump qualifies only when it is an installed air pump
    /// (<see cref="PumpTrigger"/>) with an installed O2 RTA canister (<see cref="O2CanTrigger"/>) that
    /// actually holds O2 (<c>StatGasMolO2</c> &gt; 0) at one of its <c>GasInput</c> map-point tiles.
    /// A pump alone — or one feeding an N2/empty can — earns nothing, and only the running (OnG)
    /// pump state even carries a GasInput point (the Off pump can never qualify). GetShipValue then
    /// applies ×3 when <b>any</b> pump qualifies: the bonus is a flag, so a second pump adds nothing.
    /// The count itself is what a template bakes as <c>nO2PumpCount</c>.
    /// </summary>
    public static int CountO2Pumps(ShipGrid grid, Catalog catalog)
    {
        var count = 0;
        foreach (var pump in grid.Parts)
        {
            if (!Fires(PumpTrigger, pump, catalog)) continue;
            foreach (var (key, px) in pump.Part.MapPoints)
            {
                // game: mapPoint.Key.IndexOf("GasInput") >= 0
                if (!key.Contains("GasInput", StringComparison.Ordinal)) continue;
                var tile = grid.MapPointTile(pump, px);
                if (tile < 0) continue;
                // GetCOsAtWorldCoords1: the can occupying that tile (RTA cans are 1×1, so anchor == tile)
                if (grid.Parts.Any(can => can.AnchorIndex == tile
                    && Fires(O2CanTrigger, can, catalog)
                    && can.Part.StartingCondValues.GetValueOrDefault("StatGasMolO2") > 0))
                {
                    count++;
                    break;
                }
            }
        }
        return count;
    }

    private static bool Fires(string trigger, PlacedPart part, Catalog catalog) =>
        catalog.Triggers.TryGetValue(trigger, out var ct)
            ? CondEval.Triggered(ct, part.Part.CondSet, catalog)
            : part.Part.Has(trigger);

    /// <summary>
    /// The broker value of a single room — Σ (part <c>GetBasePrice()</c> × the room's value modifier), the
    /// game's <c>Room.CalculateRoomValue</c>. <b>Void rooms count too</b>: neither CalculateRoomValue nor
    /// GetShipValue filters on <c>bVoid</c>, and 192 baked core void rooms carry non-zero <c>roomValue</c>
    /// (the AirRacer's unsealed space is worth $343k — its engines) — so engines and exterior-mounted gear
    /// are valued at the void room's modifier (Blank ×1.0, or CargoRoomExterior ×1.05). Zeroing them (as
    /// Ostraplan did before 0.19.0) undercounts any ship with parts outside sealed rooms. Shared by
    /// <see cref="Estimate(ShipDocument, Catalog, IReadOnlyList{RoomSpecDef})"/> and the export bake so a
    /// spawned design's shallow-load broker value equals the game's own full-load recompute — the mismatch
    /// between the two was the "buy at one price, resell at another" whipsaw.
    /// </summary>
    public static double RoomValueOf(RoomModel room, IReadOnlyDictionary<string, double> valueModifiers, Catalog catalog) =>
        RoomPartsValue(room, catalog) * valueModifiers.GetValueOrDefault(room.RoomSpec, 1.0);

    /// <summary>The room's member-parts value before any room modifier — Σ <see cref="PartValue"/>.
    /// What a room's certification multiplies; the value-opportunity hints scale this by
    /// (target modifier − current modifier) to price an upgrade.</summary>
    public static double RoomPartsValue(RoomModel room, Catalog catalog)
    {
        double value = 0;
        foreach (var part in room.Parts)
            value += PartValue(part.Part, catalog);
        return value;
    }

    /// <summary>
    /// One part's <c>CondOwner.GetBasePrice()</c> as it evaluates on a pristine-condition spawn (no damage
    /// scaling, no IsPristine, no market modifier): <c>StatBasePrice</c> (falling back to <c>StatMass</c>
    /// when zero) <b>plus the value of the gas and fuel the def starts with</b> — per
    /// <c>GasContainer.GetTotalGasValue</c>, each <c>StatGasMol&lt;gas&gt;</c> × molar mass × the data-driven
    /// price/kg (<see cref="Catalog.GasPrices"/>), and the two fuel lines <c>StatLiqD2O</c> × price("H2") and
    /// <c>StatSolidHe3</c> × price("He3"). An O2 RTA spawns full (13,373 mol ≈ $5,648 of O2 on top of its
    /// $410 shell), so ignoring contents visibly undercounted canister-heavy builds.
    /// </summary>
    public static double PartValue(ResolvedPart part, Catalog catalog)
    {
        var conds = part.StartingCondValues;
        var value = ShellValue(part);
        foreach (var (cond, amount) in conds)
        {
            if (!cond.StartsWith("StatGasMol", StringComparison.Ordinal) || cond == "StatGasMolTotal") continue;
            var gas = cond["StatGasMol".Length..];
            value += catalog.GasPrices.GetValueOrDefault(gas)
                * MolarMassKgPerMol.GetValueOrDefault(gas) * amount;
        }
        value += conds.GetValueOrDefault("StatLiqD2O") * catalog.GasPrices.GetValueOrDefault("H2");
        value += conds.GetValueOrDefault("StatSolidHe3") * catalog.GasPrices.GetValueOrDefault("He3");
        return value;
    }

    /// <summary>A part's shell price alone — <c>StatBasePrice</c> falling back to <c>StatMass</c>, before any
    /// gas the tank holds (which <see cref="PartValue"/> adds on top).</summary>
    private static double ShellValue(ResolvedPart part)
    {
        var value = part.StartingCondValues.GetValueOrDefault("StatBasePrice");
        return value != 0 ? value : part.StartingCondValues.GetValueOrDefault("StatMass");
    }
}
