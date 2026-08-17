using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// Finding an owned apartment in a save. The picker used to read the player CO's <c>aMyShips</c> and nothing
/// else, which can never contain an apartment (the game's <c>CondOwner.ClaimShip</c> refuses the claim for a
/// station, so the broker's registration only reaches <c>objSystem.dictShipOwners</c>); these pin the union of
/// both registries, and that adding the second one changes nothing for a save with no apartment in it.
///
/// <para>Synthetic zips throughout: the shapes under test are the two ownership registries and the entry-name
/// substitution, none of which need real game data.</para>
/// </summary>
public class ResidenceSaveImportTests
{
    private const string PlayerCo = "Ada Vance";

    [Fact]
    public void An_apartment_known_only_to_the_owner_registry_is_listed_and_flagged()
    {
        var zip = Save(
            session: Session(onShip: "J-P3HF", owners: [("J-P3HF", PlayerCo), ("BCRS|RES_1", PlayerCo)]),
            ships:
            [
                ("J-P3HF", Ship("J-P3HF", "Vagabond", "Salvage Tug", myShips: ["J-P3HF"])),
                ("BCRS%RES_1", Ship("BCRS|RES_1", "Ring Station | Station Residence", "Station Residence")),
            ]);
        try
        {
            var listed = SaveImport.ListPlayerShips(zip);

            var apartment = Assert.Single(listed, s => s.RegId == "BCRS|RES_1");
            Assert.True(apartment.Owned);
            Assert.True(apartment.IsResidence);
            Assert.Equal("Ring Station | Station Residence", apartment.Name);
            Assert.Contains("Station Residence", apartment.Sub);   // the record was found under the % name

            // The vessel still leads: aMyShips order is preserved and the registry-only entry appends.
            Assert.Equal(["J-P3HF", "BCRS|RES_1"], listed.Select(s => s.RegId));
            Assert.False(listed[0].IsResidence);
        }
        finally { File.Delete(zip); }
    }

    [Fact]
    public void A_save_with_no_apartment_lists_exactly_what_it_did_before()
    {
        // The regression guard for every existing user: dictShipOwners credits the player the same vessel
        // aMyShips already had, so the union must not duplicate it or reorder anything.
        var zip = Save(
            session: Session(onShip: "J-P3HF", owners: [("J-P3HF", PlayerCo), ("BCRS", "BCRSCiv")]),
            ships: [("J-P3HF", Ship("J-P3HF", "Vagabond", "Salvage Tug", myShips: ["J-P3HF"]))]);
        try
        {
            var listed = SaveImport.ListPlayerShips(zip);

            var only = Assert.Single(listed);
            Assert.Equal("J-P3HF", only.RegId);
            Assert.True(only.Owned);
            Assert.True(only.Current);
            Assert.False(only.IsResidence);
        }
        finally { File.Delete(zip); }
    }

    [Fact]
    public void Somebody_elses_property_in_the_registry_is_not_claimed_for_the_player()
    {
        // dictShipOwners covers every ship in the system, most of them an NPC's, so it has to be filtered by
        // owner rather than read wholesale. An unfiltered read would hand the player the whole station list.
        var zip = Save(
            session: Session(onShip: "J-P3HF", owners:
            [
                ("J-P3HF", PlayerCo), ("BCRS|RES_4", "Sen Lu"), ("BCRS", "BCRSCiv"), ("HQCH", "UNREGISTERED"),
            ]),
            ships: [("J-P3HF", Ship("J-P3HF", "Vagabond", "Salvage Tug", myShips: ["J-P3HF"]))]);
        try
        {
            var listed = SaveImport.ListPlayerShips(zip);

            Assert.Equal(["J-P3HF"], listed.Select(s => s.RegId));
            Assert.DoesNotContain(listed, s => s.IsResidence);
        }
        finally { File.Delete(zip); }
    }

    [Fact]
    public void An_apartment_the_player_is_standing_in_is_owned_rather_than_an_unsupported_current_ship()
    {
        // Before the registry read this was the ONLY way an apartment appeared at all, and it appeared as the
        // not-owned "current ship" fallback, which the picker presents as editable-but-unsupported.
        var zip = Save(
            session: Session(onShip: "BCRS|RES_1", owners: [("J-P3HF", PlayerCo), ("BCRS|RES_1", PlayerCo)]),
            ships:
            [
                ("BCRS%RES_1", Ship("BCRS|RES_1", "Ring Station | Station Residence", "Station Residence",
                                    myShips: ["J-P3HF"])),
                ("J-P3HF", Ship("J-P3HF", "Vagabond", "Salvage Tug")),
            ]);
        try
        {
            var apartment = Assert.Single(SaveImport.ListPlayerShips(zip), s => s.RegId == "BCRS|RES_1");

            Assert.True(apartment.Owned);
            Assert.True(apartment.Current);
            Assert.True(apartment.IsResidence);
        }
        finally { File.Delete(zip); }
    }

    [Fact]
    public void A_truncated_owner_registry_is_ignored_rather_than_read_as_an_unowned_regid()
    {
        var zip = Save(
            session: """
                     [{"strShip":"J-P3HF","strPlayerCO":"Ada Vance",
                       "objSystem":{"dfEpoch":0,"dictShipOwners":["J-P3HF","Ada Vance","BCRS|RES_9"]}}]
                     """,
            ships: [("J-P3HF", Ship("J-P3HF", "Vagabond", "Salvage Tug", myShips: ["J-P3HF"]))]);
        try
        {
            Assert.Equal(["J-P3HF"], SaveImport.ListPlayerShips(zip).Select(s => s.RegId));
        }
        finally { File.Delete(zip); }
    }

    // ---- synthetic save building ----

    private static string Session(string onShip, (string Reg, string Owner)[] owners)
    {
        var flat = string.Join(",", owners.SelectMany(o => new[] { Quote(o.Reg), Quote(o.Owner) }));
        // The closing braces are split across lines because two adjacent '}' in a $$ raw string read as an
        // interpolation close, not as content. JSON does not care where the newline falls.
        return $$"""
                 [{"strShip":{{Quote(onShip)}},"strPlayerCO":{{Quote(PlayerCo)}},
                   "objSystem":{"dfEpoch":0,"dictShipOwners":[{{flat}}]}
                 }]
                 """;
    }

    private static string Ship(string regId, string publicName, string designation, string[]? myShips = null)
    {
        var mine = string.Join(",", (myShips ?? []).Select(Quote));
        return $$"""
                 [{"strName":{{Quote(regId)}},"strRegID":{{Quote(regId)}},
                   "publicName":{{Quote(publicName)}},"designation":{{Quote(designation)}},
                   "make":"","model":"","nCols":8,"nRows":8,"vShipPos":{"x":0,"y":0},
                   "aItems":[{"strName":"ItmWall01","fX":0,"fY":0,"fRotation":0,"strID":"a"}],
                   "aCOs":[{"strID":{{Quote(PlayerCo)}},"aMyShips":[{{mine}}]}]}]
                 """;
    }

    /// <summary>A save zip: the session record plus <c>ships/&lt;name&gt;.json</c> per entry. Ship names are
    /// given <b>as stored</b> (already % substituted), so the tests exercise the real on-disk shape.</summary>
    private static string Save(string session, (string EntryName, string Text)[] ships)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ostraplan-res-save-{Guid.NewGuid():N}.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Write(zip, $"{PlayerCo}.json", session);
            foreach (var (name, text) in ships) Write(zip, $"ships/{name}.json", text);
        }
        return path;

        static void Write(ZipArchive zip, string entry, string text)
        {
            using var s = new StreamWriter(zip.CreateEntry(entry).Open(), new UTF8Encoding(false));
            s.Write(text);
        }
    }

    private static string Quote(string s) => $"\"{s}\"";
}
