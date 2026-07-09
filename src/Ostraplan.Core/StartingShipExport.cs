using System.Text.Json.Nodes;

namespace Ostraplan.Core;

/// <summary>The JSON fragments a starting-ship export contributes, split by the data file each lands in.
/// All are complete objects the game merges by <c>strName</c> (whole-object for loot, additive for the
/// lifeevent/interaction arrays).</summary>
public sealed record StartingShipFragments(
    IReadOnlyList<JsonObject> LootObjects,
    IReadOnlyList<JsonObject> Lifeevents,
    IReadOnlyList<JsonObject> Interactions);

/// <summary>
/// Makes an exported ship a possible outcome of a fresh <b>Shipbreaker</b> career start, reusing the
/// exact chain the game already ships (verified against <c>CGEncShipSalvagePod*</c>): the career's
/// <c>aEventsShip</c> rolls the <c>CGEncShipbreakerShipEvents</c> loot pool → picks one <c>…Intro</c>
/// (which is <b>both</b> a lifeevent and an interaction) → the interaction offers "Take Ship" / "keep
/// looking" → "Take" grants the ship via the lifeevent's <c>strShipRewards</c> and standard starting gear
/// via the interaction's <c>aLootItms</c>. There is <b>no</b> true ship-picker in vanilla chargen, so this
/// adds the ship as a weighted option alongside the ~7 core salvage pods, not a guaranteed pick.
/// </summary>
public static class StartingShipExport
{
    /// <summary>The core interaction reused as the "keep looking / not yet" branch — a generic
    /// "Continue Career" decline that every core ship intro already points at.</summary>
    public const string ContinueInteraction = "CGEncShipSalvagePodCont";

    /// <summary>The core loadout granting a Shipbreaker's standard starting gear (suit, tools…). Shared by
    /// every core shipbreaker start, so reusing it keeps a custom start identical to vanilla but for the ship.</summary>
    public const string StarterLoadout = "ItmShipbreakerLoadout";

    /// <summary>The loot pool the Shipbreaker/OKLG careers roll for their starting-ship event.</summary>
    public const string ShipEventsPool = "CGEncShipbreakerShipEvents";

    /// <summary>
    /// Build every fragment for making <paramref name="shipName"/> a weighted Shipbreaker starting option.
    /// <paramref name="eventsPool"/> is the current effective <see cref="ShipEventsPool"/> as a mutable clone
    /// (from <see cref="KioskExport.ClonePoolOrDefault"/>) — the intro is appended to it. <paramref name="station"/>
    /// is the ATC the player begins docked at (<c>strStartATC</c>, e.g. "OKLG"); <paramref name="mortgage"/> is the
    /// debt they start owing (pre-fill from the broker buy estimate). <paramref name="title"/>/<paramref name="desc"/>
    /// are the encounter's shown title/body.
    /// </summary>
    public static StartingShipFragments Build(
        JsonObject eventsPool, string shipName, double weight, string station, double mortgage,
        string title, string desc)
    {
        var token = Token(shipName);
        var intro = "CGEnc" + token + "Intro";
        var take = "CGEnc" + token + "Take";
        var reward = "CGEnc" + token + "Reward";

        // --- loot ---
        // The reward pool: the actual ship grant (strShipRewards on the Take lifeevent points here).
        var rewardPool = new JsonObject
        {
            ["strName"] = reward,
            ["aCOs"] = new JsonArray(LootList.FormatEntry(shipName, 1.0)),
            ["aLoots"] = new JsonArray(),
            ["strType"] = "ship",
        };
        // Append the intro as a weighted option in the shipbreaker events pool (preserving the core options).
        var eventsOverride = KioskExport.AppendShipToPool(eventsPool, intro, weight);
        // Empty no-op trigger loots the interactions' LootCTsUs reference (mirrors core exactly — a missing
        // reference would log a load warning).
        var introTrigger = EmptyTrigger(intro);
        var takeTrigger = EmptyTrigger(take);

        // --- lifeevents ---
        var introEvent = new JsonObject
        {
            ["strName"] = intro,
            ["strInteraction"] = intro,
            ["fCashRewardMin"] = 0.0,
            ["fStartATCRange"] = 0.0,
            ["strStartATC"] = "",
            ["strShipRewards"] = "",
        };
        var takeEvent = new JsonObject
        {
            ["strName"] = take,
            ["strInteraction"] = take,
            ["fCashRewardMin"] = 0.0,
            ["fShipMortgage"] = mortgage,
            ["bShipOwned"] = true,
            ["fShipDmgMax"] = 0.0,   // pristine: an Ostraplan design spawns undamaged (core used 0.45 for a used pod)
            ["fStartATCRange"] = 0.0,
            ["strStartATC"] = station,
            ["strShipRewards"] = reward,
        };

        // --- interactions ---
        var introInteraction = new JsonObject
        {
            ["strName"] = intro,
            ["strTitle"] = title,
            ["strDesc"] = desc,
            ["aInverse"] = new JsonArray(ContinueInteraction, take),
            ["fDuration"] = 0.0,
            ["strThemType"] = "Self",
            ["LootCondsUs"] = null,
            ["LootCTsUs"] = intro,
        };
        var takeInteraction = new JsonObject
        {
            ["strName"] = take,
            ["strTitle"] = "Take Ship",
            ["strDesc"] = $"The {shipName} is yours. Time to get out there and find your future.",
            ["fDuration"] = 0.0,
            ["strThemType"] = "Self",
            ["aLootItms"] = new JsonArray("addus," + StarterLoadout),
            ["LootCTsUs"] = take,
        };

        return new StartingShipFragments(
            [rewardPool, eventsOverride, introTrigger, takeTrigger],
            [introEvent, takeEvent],
            [introInteraction, takeInteraction]);
    }

    /// <summary>An empty <c>strType:"trigger"</c> loot — the no-op shape core's <c>LootCTsUs</c> targets point at.</summary>
    private static JsonObject EmptyTrigger(string name) => new()
    {
        ["strName"] = name,
        ["aCOs"] = new JsonArray(),
        ["aLoots"] = new JsonArray(),
        ["strType"] = "trigger",
    };

    /// <summary>A strName-safe token from the ship name: letters and digits only, never empty. Used to build the
    /// unique <c>CGEnc&lt;Token&gt;Intro/Take/Reward</c> strNames for this ship's chargen chain.</summary>
    public static string Token(string shipName)
    {
        var chars = shipName.Where(char.IsLetterOrDigit).ToArray();
        return chars.Length == 0 ? "Ship" : new string(chars);
    }
}
