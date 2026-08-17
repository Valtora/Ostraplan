using System.IO;
using System.Linq;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The two pieces of residence support that are pure and can be pinned without the game: how a RegID becomes a
/// zip entry name (<see cref="SaveZip"/>), and how a design comes to know it is a residence
/// (<see cref="DocumentKind"/> + its .oplan round trip). The save-writing behaviour that depends on these lives
/// in the SaveEdit/SaveGrant suites.
/// </summary>
public class ResidenceDocumentTests
{
    // ---- SaveZip: the entry-name substitution ----

    [Theory]
    [InlineData("J-P3HF", "ships/J-P3HF.json")]              // an ordinary RegID is untouched
    [InlineData("BCRS|RES_1", "ships/BCRS%RES_1.json")]      // an apartment
    [InlineData("VORB|Aux", "ships/VORB%Aux.json")]          // the stock sub-station, as a real save stores it
    [InlineData("A*B", "ships/A§B.json")]
    public void A_regid_is_stored_under_the_name_the_game_would_write(string regId, string entry) =>
        Assert.Equal(entry, SaveZip.ShipEntry(regId));

    [Theory]
    [InlineData("J-P3HF")]
    [InlineData("BCRS|RES_1")]
    [InlineData("MSUZ|RES_12")]
    [InlineData("A*B|C")]
    public void Encoding_a_regid_and_decoding_it_back_is_the_identity(string regId) =>
        Assert.Equal(regId, SaveZip.DecodeName(SaveZip.EncodeName(regId)));

    [Fact]
    public void A_decoded_entry_name_is_what_a_mint_has_to_compare_against()
    {
        // The hole this closes: SaveGrant builds its taken-registrations set from file names, so without the
        // decode an apartment's BCRS|RES_1 reads as BCRS%RES_1 and a mint of the same RegID looks free.
        var fromDisk = Path.GetFileNameWithoutExtension("ships/BCRS%RES_1.json");
        Assert.NotEqual("BCRS|RES_1", fromDisk);
        Assert.Equal("BCRS|RES_1", SaveZip.DecodeName(fromDisk));
    }

    [Theory]
    [InlineData("BCRS|RES_1", true, "BCRS")]
    [InlineData("VORB|Aux", true, "VORB")]
    [InlineData("J-P3HF", false, null)]
    [InlineData(null, false, null)]
    // A leading pipe is the asymmetric case, and the two answers are meant to disagree. The game hides ANY
    // RegID with a pipe in it (Ship.InitShip splits and counts, so "|RES_1" is a sub-station to it), but there
    // is no station half to hang it off, so nothing can be placed against it.
    [InlineData("|RES_1", true, null)]
    public void A_pipe_marks_a_sub_station_and_names_its_host(string? regId, bool isSub, string? station)
    {
        Assert.Equal(isSub, SaveZip.IsSubStation(regId));
        Assert.Equal(station, SaveZip.StationOf(regId));
    }

    // ---- the import-time kind guess ----

    [Theory]
    [InlineData("Station Residence", DocumentKind.Residence)]
    [InlineData("Aerostat Residence", DocumentKind.Residence)]
    [InlineData("Asteroid Residence", DocumentKind.Residence)]
    [InlineData("Surface Residence", DocumentKind.Residence)]
    [InlineData("Basic Residence", DocumentKind.Residence)]
    [InlineData("Salvage Tug", DocumentKind.Ship)]
    [InlineData("", DocumentKind.Ship)]
    [InlineData(null, DocumentKind.Ship)]
    public void A_designation_ending_in_residence_opens_as_one(string? designation, DocumentKind expected) =>
        Assert.Equal(expected, DocumentKindGuess.FromDesignation(designation));

    [Fact]
    public void A_piped_regid_beats_the_designation_either_way()
    {
        // The RegID is conclusive, not a heuristic: Ship.InitShip makes any piped RegID a hidden sub-station.
        Assert.Equal(DocumentKind.Residence, DocumentKindGuess.From("BCRS|RES_1", "Salvage Tug"));
        Assert.Equal(DocumentKind.Residence, DocumentKindGuess.From("BCRS|RES_1", null));
        // ...and without one, a ship RegID leaves the designation to decide.
        Assert.Equal(DocumentKind.Residence, DocumentKindGuess.From("J-P3HF", "Station Residence"));
        Assert.Equal(DocumentKind.Ship, DocumentKindGuess.From("J-P3HF", "Salvage Tug"));
    }

    // ---- .oplan round trip ----

    [SkippableFact]
    public void A_residence_round_trips_through_the_oplan()
    {
        var g = TestData.RequireGame();
        var doc = new ShipDocument(g.Catalog) { Kind = DocumentKind.Residence };

        var file = OplanFile.FromDocument(doc, g.Index, new OplanMeta());
        Assert.Equal("Residence", file.Kind);
        Assert.Equal(DocumentKind.Residence, file.ToDocument(g.Catalog).Doc.Kind);
    }

    [SkippableFact]
    public void A_ship_writes_no_kind_at_all_so_existing_designs_are_unchanged()
    {
        var g = TestData.RequireGame();
        var file = OplanFile.FromDocument(new ShipDocument(g.Catalog), g.Index, new OplanMeta());

        Assert.Null(file.Kind);   // omitted by the serializer, so no existing .oplan gains a field
        Assert.Equal(DocumentKind.Ship, file.ToDocument(g.Catalog).Doc.Kind);
    }

    [SkippableTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Starbase")]     // a kind written by some newer build
    public void An_absent_or_unrecognised_kind_reads_back_as_a_ship(string? stored)
    {
        var g = TestData.RequireGame();
        var file = OplanFile.FromDocument(new ShipDocument(g.Catalog), g.Index, new OplanMeta());
        file.Kind = stored;

        Assert.Equal(DocumentKind.Ship, file.ToDocument(g.Catalog).Doc.Kind);
    }

    [SkippableFact]
    public void A_residence_survives_a_write_and_reread_from_disk()
    {
        var g = TestData.RequireGame();
        var doc = new ShipDocument(g.Catalog) { Kind = DocumentKind.Residence };
        var path = Path.Combine(Path.GetTempPath(), $"ostraplan-res-{Guid.NewGuid():N}.oplan");
        try
        {
            OplanFile.FromDocument(doc, g.Index, new OplanMeta()).Save(path);
            Assert.Contains("\"kind\": \"Residence\"", File.ReadAllText(path));
            Assert.Equal(DocumentKind.Residence, OplanFile.Load(path).ToDocument(g.Catalog).Doc.Kind);
        }
        finally { File.Delete(path); }
    }

    // ---- the template-listing filter ----

    [SkippableFact]
    public void The_two_template_lists_partition_the_catalogue()
    {
        var g = TestData.RequireGame();

        var all = TemplateImport.ListShipFiles(g.Index);
        var ships = TemplateImport.ListShipFiles(g.Index, DocumentKind.Ship);
        var residences = TemplateImport.ListShipFiles(g.Index, DocumentKind.Residence);

        // Neither menu entry can hide a template from the other, and neither can show one twice.
        Assert.Equal(all.Count, ships.Count + residences.Count);
        Assert.Empty(ships.Select(s => s.Name).Intersect(residences.Select(r => r.Name), StringComparer.Ordinal));
        Assert.All(residences, r => Assert.Equal(DocumentKind.Residence, r.Kind));
        Assert.All(ships, s => Assert.Equal(DocumentKind.Ship, s.Kind));
    }

    [SkippableFact]
    public void The_apartment_list_is_exactly_what_a_real_estate_broker_sells()
    {
        var g = TestData.RequireGame();
        var expected = new[]
        {
            "ResAero01", "ResAero02", "ResBCER01", "ResBCRS01", "ResBCRS02", "ResEJDR01",
            "ResMLAB01", "ResMSUZ01", "ResMSUZ02", "ResOKLG01", "ResRyokka01",
        };

        Assert.Equal(expected, TemplateImport.ListShipFiles(g.Index, DocumentKind.Residence)
                                             .Select(e => e.Name).OrderBy(x => x, StringComparer.Ordinal));
    }

    [SkippableFact]
    public void The_listing_filter_and_the_import_guess_agree_on_stock_data()
    {
        // They use deliberately different evidence — the listing reads the station-typed self-reference loots
        // (the game's own broker test, cheap), the import reads the designation off the file — so this is what
        // catches them drifting apart. A residence template nobody sells would legitimately differ; none does.
        var g = TestData.RequireGame();

        foreach (var entry in TemplateImport.ListShipFiles(g.Index, DocumentKind.Residence))
        {
            var r = TemplateImport.LoadFile(entry.Path, g.Catalog);
            Assert.True(r.Doc.IsResidence, $"{entry.Name} is listed as a residence but does not import as one.");
        }
    }

    [SkippableFact]
    public void A_residence_entry_labels_itself_and_a_ship_does_not()
    {
        var g = TestData.RequireGame();

        var residence = TemplateImport.ListShipFiles(g.Index, DocumentKind.Residence)[0];
        Assert.EndsWith("residence", residence.OriginLabel, StringComparison.Ordinal);
        Assert.StartsWith(residence.Origin, residence.OriginLabel, StringComparison.Ordinal);

        var ship = TemplateImport.ListShipFiles(g.Index, DocumentKind.Ship)[0];
        Assert.Equal(ship.Origin, ship.OriginLabel);
    }

    // ---- against the real templates ----

    [SkippableFact]
    public void Every_stock_residence_template_opens_as_a_residence_and_no_other_ship_does()
    {
        var g = TestData.RequireGame();
        var expected = new[]
        {
            "ResAero01", "ResAero02", "ResBCER01", "ResBCRS01", "ResBCRS02", "ResEJDR01",
            "ResMLAB01", "ResMSUZ01", "ResMSUZ02", "ResOKLG01", "ResRyokka01",
        };

        var residences = new List<string>();
        foreach (var entry in TemplateImport.ListShipFiles(g.Index))
        {
            ImportResult r;
            try { r = TemplateImport.LoadFile(entry.Path, g.Catalog); } catch { continue; }
            if (r.Doc.IsResidence) residences.Add(entry.Name);
        }

        // The eleven the Real Estate brokers sell, and nothing else: the designation suffix is what separates
        // them, so a stock ship acquiring one would show up here as an over-match.
        Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                     residences.OrderBy(x => x, StringComparer.Ordinal).ToList());
    }
}
