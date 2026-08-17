using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// Putting a designed residence into a save: the registration, the station it attaches to, the price a broker
/// would charge, the situation that pins it, and the ownership half that differs from a vessel's.
///
/// <para>Everything here is reconstructed from <c>GUIShipBroker.OnPurchaseConfirm</c> rather than from an
/// observed purchased apartment, so these tests pin <b>what Ostraplan writes</b>, which is the thing an in-game
/// test can then agree or disagree with. They are not evidence the game accepts it.</para>
/// </summary>
public class ResidenceGrantTests
{
    // ---- the registration ----

    [Fact]
    public void The_first_residence_at_a_station_is_RES_1()
    {
        Assert.Equal("BCRS|RES_1", ResidenceGrant.MintRegId(new HashSet<string>(), "BCRS"));
    }

    [Fact]
    public void Minting_counts_past_the_residences_already_there()
    {
        // The game scans upward from 1 for the first free index rather than appending, so a gap is reused.
        var taken = new HashSet<string>(["BCRS|RES_1", "BCRS|RES_2", "BCRS|RES_4"]);
        Assert.Equal("BCRS|RES_3", ResidenceGrant.MintRegId(taken, "BCRS"));
    }

    [Fact]
    public void Another_stations_residences_do_not_shift_the_index()
    {
        var taken = new HashSet<string>(["MSUZ|RES_1", "MSUZ|RES_2", "OKLG|RES_1"]);
        Assert.Equal("BCRS|RES_1", ResidenceGrant.MintRegId(taken, "BCRS"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("BCRS|RES_1")]     // hanging a residence off a residence would mint BCRS|RES_1|RES_1
    [InlineData("BCRS|")]
    public void A_station_registration_that_cannot_host_a_residence_is_refused(string station) =>
        Assert.Throws<ArgumentException>(() => ResidenceGrant.MintRegId(new HashSet<string>(), station));

    // ---- the price ----

    [Fact]
    public void The_broker_price_is_the_summed_room_values_times_ten()
    {
        // GUIShipBroker.SetupApartments sums jsonShip.aRooms[].roomValue straight off the record: no O2
        // multiplier, no re-derivation. Void rooms are included, exactly as the game's loop includes them.
        var ship = new JsonObject
        {
            ["aRooms"] = new JsonArray(
                new JsonObject { ["roomValue"] = 98437.0, ["bVoid"] = false },
                new JsonObject { ["roomValue"] = 31470.0, ["bVoid"] = true },
                new JsonObject { ["roomValue"] = 9763.19974136353, ["bVoid"] = false }),
        };

        Assert.Equal((98437.0 + 31470.0 + 9763.19974136353) * 10, ResidenceGrant.Price(ship), 3);
    }

    [Fact]
    public void A_record_with_no_rooms_is_priced_at_nothing_rather_than_throwing()
    {
        Assert.Equal(0, ResidenceGrant.Price(new JsonObject()));
        Assert.Equal(0, ResidenceGrant.Price(new JsonObject { ["aRooms"] = new JsonArray() }));
    }

    // ---- the homeowner condition ----

    [Fact]
    public void Buying_makes_the_player_a_homeowner_at_that_station()
    {
        var co = new JsonObject { ["aConds"] = new JsonArray("IsPlayer=1.0x1") };

        Assert.True(ResidenceGrant.GrantHomeownerCond(co, "BCRS"));
        Assert.Contains("IsHomeownerBCRS=1.0x1", Conds(co));
    }

    [Fact]
    public void An_existing_homeowner_is_not_granted_it_twice()
    {
        // A second apartment at the same station must not stack the cond: the transit gate reads presence, and
        // a duplicate would be a save state the game never writes.
        var co = new JsonObject { ["aConds"] = new JsonArray("IsHomeownerBCRS=1.0x1") };

        Assert.False(ResidenceGrant.GrantHomeownerCond(co, "BCRS"));
        Assert.Single(Conds(co), c => c.StartsWith("IsHomeownerBCRS", StringComparison.Ordinal));
    }

    [Fact]
    public void The_cond_is_matched_by_name_not_by_the_whole_string()
    {
        // Conds are stored "Name=MagnitudexAmount", so a magnitude that differs must still count as present.
        var co = new JsonObject { ["aConds"] = new JsonArray("IsHomeownerBCRS=2.0x3") };
        Assert.False(ResidenceGrant.GrantHomeownerCond(co, "BCRS"));
    }

    [Fact]
    public void A_co_with_no_conds_array_gains_one()
    {
        var co = new JsonObject();
        Assert.True(ResidenceGrant.GrantHomeownerCond(co, "MSUZ"));
        Assert.Equal(["IsHomeownerMSUZ=1.0x1"], Conds(co));
    }

    [Fact]
    public void Homeowner_conds_are_per_station()
    {
        var co = new JsonObject { ["aConds"] = new JsonArray("IsHomeownerBCRS=1.0x1") };
        Assert.True(ResidenceGrant.GrantHomeownerCond(co, "MSUZ"));
        Assert.Equal(2, Conds(co).Count);
    }

    // ---- the situation ----

    [Fact]
    public void A_residence_shares_its_stations_position_and_body_orbit()
    {
        var station = Station("BCRS", transit: true) with
        {
            Anchor = new GrantAnchor(2.6, -1.4, 0.001, 0.002, "Ceres", BoLocked: true, SizeMetres: 400)
            {
                BoOffsetX = 0.5, BoOffsetY = -0.25,
            },
        };

        var situ = ResidenceSitu(station, epoch: 12345.0);

        // Placed ON the station, not near it: the coordinates and the reference body are copied outright.
        Assert.Equal(2.6, (double)situ["vPosx"]!);
        Assert.Equal(-1.4, (double)situ["vPosy"]!);
        Assert.Equal("Ceres", (string)situ["boPORShip"]!);
        Assert.Equal(0.001, (double)situ["vVelX"]!);

        // The body offset is the station's, not zero: sharing an absolute position without sharing the offset
        // would separate the two as soon as the body moved.
        Assert.Equal(0.5, (double)situ["vBOOffsetx"]!);
        Assert.Equal(-0.25, (double)situ["vBOOffsety"]!);

        // The two flags that make the game read the record as a station rather than a vessel.
        Assert.True((bool)situ["bIsBO"]!);
        Assert.True((bool)situ["bBOLocked"]!);

        // Without aPathRecentX the list is never built and StarSystem.UpdateShip throws every frame, taking the
        // whole simulation down. Seeded at the station's own position, at the save's clock.
        Assert.Equal(12345.0, (double)situ["aPathRecentT"]![0]!);
        Assert.Equal(2.6, (double)situ["aPathRecentX"]![0]!);
        Assert.Equal(-1.4, (double)situ["aPathRecentY"]![0]!);
    }

    // ---- the name ----

    [Theory]
    [InlineData("Ring Station", "Station Residence", "Ring Station | Station Residence")]
    [InlineData("Ring Station", "", "Ring Station | My Design")]        // falls back to the design name
    [InlineData("Ring Station", null, "Ring Station | My Design")]
    public void A_residence_is_named_the_way_the_broker_names_one(string station, string? designation, string expected) =>
        Assert.Equal(expected, PublicName(Station(station, transit: true), designation, "My Design"));

    // ---- listing stations out of a save ----

    [SkippableFact]
    public void Only_bIsBO_ships_are_offered_and_sub_modules_are_not()
    {
        var g = TestData.RequireGame();
        var zip = SaveWith(
            ("J-P3HF", regId: "J-P3HF", isBo: false, name: "Vagabond"),          // the player's vessel
            ("BCRS", regId: "BCRS", isBo: true, name: "Ring Station"),           // a station
            ("BCRS%RES_1", regId: "BCRS|RES_1", isBo: true, name: "an apartment"),   // a sub-module
            ("HQCH", regId: "HQCH", isBo: true, name: "Hab Quarters"));
        try
        {
            var stations = ResidenceGrant.ListStations(zip, g.Index);

            Assert.Equal(["BCRS", "HQCH"], stations.Select(s => s.RegId).OrderBy(x => x, StringComparer.Ordinal));
            Assert.DoesNotContain(stations, s => SaveZip.IsSubStation(s.RegId));
            Assert.Equal("Ring Station", stations.Single(s => s.RegId == "BCRS").DisplayName);
        }
        finally { File.Delete(zip); }
    }

    [SkippableFact]
    public void A_station_the_game_has_no_residence_route_to_is_listed_but_flagged()
    {
        // The real case: vanilla MVOL places a Real Estate kiosk and has no "MVOL|" transit node, so an
        // apartment bought there is owned and unreachable. It is still offered, because a mod may add the route
        // and refusing outright would be Ostraplan overruling the user's data.
        var g = TestData.RequireGame();
        var zip = SaveWith(
            ("J-P3HF", regId: "J-P3HF", isBo: false, name: "Vagabond"),
            ("BCRS", regId: "BCRS", isBo: true, name: "Ring Station"),
            ("MVOL", regId: "MVOL", isBo: true, name: "Mercury Volanus"));
        try
        {
            var stations = ResidenceGrant.ListStations(zip, g.Index);

            Assert.True(stations.Single(s => s.RegId == "BCRS").HasTransitRoute);
            Assert.False(stations.Single(s => s.RegId == "MVOL").HasTransitRoute);
            Assert.Equal("BCRS|", stations.Single(s => s.RegId == "BCRS").TransitNodeName);

            // Routed stations sort ahead of stranded ones, so the default choice is never the broken one.
            Assert.Equal("BCRS", stations[0].RegId);
        }
        finally { File.Delete(zip); }
    }

    [SkippableFact]
    public void The_station_the_player_is_standing_on_is_offered_first()
    {
        var g = TestData.RequireGame();
        var zip = SaveWith(
            playerShip: "MSUZ",
            ("BCRS", regId: "BCRS", isBo: true, name: "Ring Station"),
            ("MSUZ", regId: "MSUZ", isBo: true, name: "Suzhou"));
        try
        {
            Assert.Equal("MSUZ", ResidenceGrant.ListStations(zip, g.Index)[0].RegId);
        }
        finally { File.Delete(zip); }
    }

    // ---- helpers ----

    private static ResidenceStation Station(string name, bool transit) =>
        new(name, name, new GrantAnchor(0, 0, 0, 0, null, false, 0), transit);

    private static JsonObject ResidenceSitu(ResidenceStation station, double epoch) =>
        ResidenceGrant.BuildSitu(station, epoch);

    private static string PublicName(ResidenceStation station, string? designation, string designName) =>
        ResidenceGrant.PublicName(station, designation, designName);

    private static List<string> Conds(JsonObject co) =>
        [.. (co["aConds"] as JsonArray ?? []).Select(n => (string)n!)];

    private static string SaveWith(params (string Entry, string regId, bool isBo, string name)[] ships) =>
        SaveWith("J-P3HF", ships);

    /// <summary>A save zip holding a session record and one entry per ship. Built by concatenation rather than
    /// raw string literals: the JSON here is brace-dense, and a $$"""…""" hole beside a literal '}}' does not
    /// parse.</summary>
    private static string SaveWith(string playerShip, params (string Entry, string regId, bool isBo, string name)[] ships)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ostraplan-res-grant-{Guid.NewGuid():N}.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Write(zip, "Ada.json",
                "[{\"strShip\":\"" + playerShip + "\",\"strPlayerCO\":\"Ada\","
                + "\"objSystem\":{\"dfEpoch\":0,\"dictShipOwners\":[]}}]");

            foreach (var (entry, regId, isBo, name) in ships)
                Write(zip, "ships/" + entry + ".json",
                    "[{\"strName\":\"" + regId + "\",\"strRegID\":\"" + regId + "\","
                    + "\"publicName\":\"" + name + "\",\"nCols\":8,\"nRows\":8,\"aItems\":[],"
                    + "\"objSS\":{\"bIsBO\":" + (isBo ? "true" : "false") + ",\"bBOLocked\":true,"
                    + "\"boPORShip\":\"Ceres\",\"vPosx\":1.5,\"vPosy\":2.5,"
                    + "\"vBOOffsetx\":0.1,\"vBOOffsety\":0.2,\"vVelX\":0,\"vVelY\":0,\"size\":100}}]");
        }
        return path;

        static void Write(ZipArchive zip, string entry, string text)
        {
            using var s = new StreamWriter(zip.CreateEntry(entry).Open(), new UTF8Encoding(false));
            s.Write(text);
        }
    }
}
