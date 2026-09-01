using System.Text.Json.Serialization;

namespace Ostraplan.Core;

/// <summary>
/// How one ship is to be obtained in game, as the user set it: which kiosks, which Special Offer slots, which
/// derelict fields, and whether it can turn up as a Shipbreaker's starting ship.
///
/// <para><b>Mutable and persistable, unlike <see cref="ShipDelivery"/>.</b> That one is the writer's input, fixed
/// at the moment of export and carrying the resolved title and description the loot needs. This is the choice
/// itself, which a control edits, a bundle file stores, and <see cref="ToDelivery"/> turns into the other. One
/// class rather than two because the export wizard and the bundle editor are asking a ship the same question, and
/// two shapes of the same answer would drift the moment a route was added to one of them.</para>
///
/// <para>The two nullable weights mean "not chosen yet", which is what lets a step fill in the game's own default
/// for a pool without overwriting a weight the user set last time.</para>
/// </summary>
public sealed class DeliveryPlan
{
    [JsonPropertyName("brokerPools")] public List<string> BrokerPools { get; set; } = [];

    /// <summary>How often the ship appears in a kiosk's stock. Null until it has been chosen.</summary>
    [JsonPropertyName("brokerWeight")] public double? BrokerWeight { get; set; }

    [JsonPropertyName("specialOfferPools")] public List<string> SpecialOfferPools { get; set; } = [];

    /// <summary>Derelict-ring pools to scatter the ship through as a wreck. World generation only: an existing
    /// save never grows one.</summary>
    [JsonPropertyName("derelictPools")] public List<string> DerelictPools { get; set; } = [];

    [JsonPropertyName("derelictWeight")] public double? DerelictWeight { get; set; }

    /// <summary>The user deliberately asked for a ship file with no way to obtain it: a modpack piece, or loot
    /// they intend to wire themselves. Distinct from having simply forgotten, which the UI refuses.</summary>
    [JsonPropertyName("noRoute")] public bool NoDeliveryRoute { get; set; }

    [JsonPropertyName("startingShip")] public bool StartingShip { get; set; }

    /// <summary>
    /// Pin the Shipbreaker start to this ship alone, dropping the vanilla salvage pods.
    ///
    /// <para>Only a single-ship export answers this here. A mod holding several ships answers it once for the mod
    /// (<see cref="BundleOptions.ExclusiveStart"/>), because the pool it pins is one pool and "only this ship"
    /// cannot be said twice in it.</para>
    /// </summary>
    [JsonPropertyName("startingShipExclusive")] public bool StartingShipExclusive { get; set; }

    [JsonPropertyName("startStation")] public string StartStation { get; set; } = "OKLG";

    [JsonPropertyName("startMortgage")] public double StartMortgage { get; set; }

    /// <summary>The weight a starting ship is offered at, read from the game's own pool. Not a user control, so
    /// it is not persisted: it is re-read from the loaded data every time.</summary>
    [JsonIgnore] public double StartWeight { get; set; } = 0.16;

    /// <summary>Whether anything at all would spawn this ship. Derived, so it is never written to a pack file:
    /// a stored copy of an answer the routes already give could only ever go stale against them.</summary>
    [JsonIgnore] public bool AnyRoute =>
        BrokerPools.Count > 0 || SpecialOfferPools.Count > 0 || DerelictPools.Count > 0 || StartingShip;

    /// <summary>The ship reaches the game only as a wreck. The game damages a derelict itself when it first loads,
    /// so baking wear on top of that is double damage nobody asked for, and an export aimed only here turns the
    /// condition off on the user's behalf unless they have set it themselves.</summary>
    [JsonIgnore] public bool DerelictOnly =>
        DerelictPools.Count > 0 && BrokerPools.Count == 0 && SpecialOfferPools.Count == 0 && !StartingShip;

    public DeliveryPlan Clone() => new()
    {
        BrokerPools = [.. BrokerPools],
        BrokerWeight = BrokerWeight,
        SpecialOfferPools = [.. SpecialOfferPools],
        DerelictPools = [.. DerelictPools],
        DerelictWeight = DerelictWeight,
        NoDeliveryRoute = NoDeliveryRoute,
        StartingShip = StartingShip,
        StartingShipExclusive = StartingShipExclusive,
        StartStation = StartStation,
        StartMortgage = StartMortgage,
        StartWeight = StartWeight,
    };

    /// <summary>
    /// The immutable form the exporter takes. <paramref name="title"/> and <paramref name="description"/> are what
    /// a Shipbreaker start shows on the encounter: the ship's in-game name where it has one, falling back to the
    /// design's name, and the design's description.
    /// </summary>
    public ShipDelivery ToDelivery(string title, string description) => new(
        BrokerPools, BrokerWeight ?? 0.05, SpecialOfferPools,
        StartingShip, StartWeight, StartStation, StartMortgage, title, description,
        StartingShipExclusive, DerelictPools, DerelictWeight ?? 0.05);
}
