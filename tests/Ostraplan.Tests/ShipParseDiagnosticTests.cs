using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// A ship record that won't parse has to say <b>why</b>.
///
/// <para>Every one of these files was written by the game, so a parse failure means something unusual is in it, and
/// the reason used to be discarded: <c>ShipTemplate.ParseFile</c> swallowed the <c>JsonException</c> and the import
/// reported "could not be parsed" with nothing behind it. A user with a save Ostraplan won't open then has no way
/// to find out what it objected to, and neither has anyone helping them. These tests pin the diagnostic itself,
/// because it is the whole value of the change.</para>
///
/// <para>Pure: no game install, no real save.</para>
/// </summary>
public class ShipParseDiagnosticTests
{
    private const string Ship = """[{"strName":"Kestrel","nCols":3,"nRows":3,"aItems":[{"strName":"ItmWall1x1","fX":0,"fY":0}]}]""";

    // ---- the happy path stays silent ----

    [Fact]
    public void A_real_ship_parses_with_no_failure_reported()
    {
        var ships = ShipTemplate.ParseFileChecked(Ship, out var failure);

        Assert.Null(failure);
        Assert.Equal("Kestrel", Assert.Single(ships).Name);
    }

    // ---- invalid JSON ----

    [Fact]
    public void Invalid_json_reports_the_position_and_an_excerpt_with_a_caret()
    {
        // a truncated record: the array never closes, which is what an interrupted or clipped save looks like
        var truncated = Ship[..^12];

        var ships = ShipTemplate.ParseFileChecked(truncated, out var failure);

        Assert.Empty(ships);
        Assert.NotNull(failure);
        Assert.Contains("Line 1, position", failure);
        Assert.Contains("^", failure);
    }

    /// <summary>
    /// The position is reported 1-based. System.Text.Json counts both from zero, and a diagnostic that is off by one
    /// against every text editor a user might open the file in is worse than no position at all.
    /// </summary>
    [Fact]
    public void The_reported_position_is_one_based()
    {
        var ships = ShipTemplate.ParseFileChecked("\n\n  oops", out var failure);

        Assert.Empty(ships);
        Assert.NotNull(failure);
        Assert.Contains("Line 3, position 3", failure);
    }

    /// <summary>
    /// A raw control character inside a string is one of the few things that makes a game-written save invalid, and
    /// it is invisible in an excerpt that prints it as-is. It has to come out escaped, or the reported fault looks
    /// like ordinary text.
    /// </summary>
    [Fact]
    public void A_control_character_in_a_string_is_shown_escaped()
    {
        var withNul = "[{\"strName\":\"Kes\u0000trel\",\"nCols\":3,\"aItems\":[]}]";

        var ships = ShipTemplate.ParseFileChecked(withNul, out var failure);

        Assert.Empty(ships);
        Assert.NotNull(failure);
        Assert.Contains("\\u0000", failure);
        Assert.DoesNotContain('\0', failure);
    }

    /// <summary>
    /// The parser counts <b>bytes</b> into the line and the text is UTF-16, so any non-ASCII ahead of the fault
    /// makes the two diverge. Used as a char index, the excerpt would point past the fault by one column per extra
    /// byte — on a save with a crew name in it, at the exact moment the excerpt is what the user is reading.
    /// </summary>
    [Fact]
    public void A_multibyte_character_before_the_fault_does_not_shift_the_caret()
    {
        // "Ærik" costs 2 UTF-8 bytes for the Æ, so byte position and char index differ from here on
        const string json = """{"strName":"Ærik","nCols":3,"aItems":oops}""";

        ShipTemplate.ParseFileChecked(json, out var failure);

        Assert.NotNull(failure);
        var caret = failure.Split('\n')[^1];
        var excerpt = failure.Split('\n')[^2];
        Assert.Equal('o', excerpt[caret.IndexOf('^')]);
    }

    // ---- valid JSON that simply isn't a ship ----

    [Fact]
    public void Valid_json_that_holds_no_ship_says_what_it_found_instead()
    {
        var ships = ShipTemplate.ParseFileChecked("""[{"strName":"Kestrel","nRows":3,"aItems":[]}]""", out var failure);

        Assert.Empty(ships);
        Assert.NotNull(failure);
        Assert.Contains("valid JSON", failure);
        Assert.Contains("no nCols", failure);
        Assert.Contains("strName", failure);     // the fields it does carry, so a wrong file is recognisable
    }

    [Fact]
    public void An_empty_array_is_reported_as_empty_rather_than_as_a_bad_ship()
    {
        ShipTemplate.ParseFileChecked("[]", out var failure);

        Assert.Contains("empty array", failure);
    }

    [Fact]
    public void An_aItems_that_is_not_an_array_is_named_as_the_reason()
    {
        ShipTemplate.ParseFileChecked("""{"nCols":3,"aItems":null}""", out var failure);

        Assert.Contains("aItems", failure);
        Assert.Contains("null", failure);
    }

    // ---- the save import path, end to end over a synthetic zip ----

    /// <summary>A save zip with the given entries, written to a temp file the caller deletes.</summary>
    private static string SaveZip(params (string Name, string Text)[] entries)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ostraplan-parse-{Guid.NewGuid():N}.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            foreach (var (name, text) in entries)
            {
                using var s = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                s.Write(text);
            }
        return path;
    }

    private static Catalog EmptyCat() => new()
    {
        Parts = [],
        ByDefName = new Dictionary<string, PartDef>(),
        Loots = new Dictionary<string, LootDef>(),
        Triggers = new Dictionary<string, CondTriggerDef>(),
        Warnings = [],
    };

    [Fact]
    public void A_ship_record_that_wont_parse_names_the_entry_and_the_reason()
    {
        var zip = SaveZip(
            ("Pilot.json", """{"strShip":"H-ABC","strPlayerCO":"Pilot"}"""),
            ("ships/H-ABC.json", Ship[..^12]));
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => SaveImport.ImportPlayerShip(zip, EmptyCat()));

            Assert.Contains("'H-ABC' could not be parsed", ex.Message);
            Assert.Contains("ships/H-ABC.json", ex.Message);
            Assert.Contains("Line 1, position", ex.Message);
        }
        finally { File.Delete(zip); }
    }

    /// <summary>
    /// A damaged character record and a save that simply has none produce the same "couldn't find the player's ship"
    /// today. They want opposite responses from the user, so the reason has to come with it.
    /// </summary>
    [Fact]
    public void A_damaged_character_record_is_reported_rather_than_passed_over()
    {
        var zip = SaveZip(
            ("Pilot.json", """{"strShip":"H-ABC","""),
            ("ships/H-ABC.json", Ship));
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => SaveImport.ImportPlayerShip(zip, EmptyCat()));

            Assert.Contains("Couldn't find the player's ship", ex.Message);
            Assert.Contains("Pilot.json", ex.Message);
            Assert.Contains("Line 1, position", ex.Message);
        }
        finally { File.Delete(zip); }
    }

    [Fact]
    public void A_readable_record_that_names_no_ship_is_reported_as_such()
    {
        var zip = SaveZip(("Pilot.json", """{"strPlayerCO":"Pilot"}"""));
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => SaveImport.ImportPlayerShip(zip, EmptyCat()));

            Assert.Contains("carries no strShip", ex.Message);
        }
        finally { File.Delete(zip); }
    }

    [Fact]
    public void A_save_zip_with_no_top_level_record_says_so()
    {
        var zip = SaveZip(("ships/H-ABC.json", Ship));
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => SaveImport.ImportPlayerShip(zip, EmptyCat()));

            Assert.Contains("no top-level record", ex.Message);
        }
        finally { File.Delete(zip); }
    }

    // ---- the save folder's data zip ----

    /// <summary>
    /// A save folder Ostranauts wrote holds exactly one zip, but a user who has extracted or backed one up beside it
    /// leaves two, and taking whichever the filesystem lists first reads the wrong archive — which then reports a
    /// perfectly good save as having no player ship.
    /// </summary>
    [Fact]
    public void The_save_folder_zip_is_the_one_named_after_the_folder()
    {
        var root = Directory.CreateTempSubdirectory("ostraplan-saves-").FullName;
        try
        {
            var save = Directory.CreateDirectory(Path.Combine(root, "my save")).FullName;
            // "a-backup.zip" sorts first, so this fails whenever the folder is read in enumeration order
            File.Move(SaveZip(("ships/H-ABC.json", Ship)), Path.Combine(save, "a-backup.zip"));
            File.Move(SaveZip(("ships/H-ABC.json", Ship)), Path.Combine(save, "my save.zip"));

            var entry = Assert.Single(SaveImport.ListSaves(new GameEnv
            {
                GameRoot = root, DiscoveredVia = "test", StreamingAssetsDir = root, ModsDir = root,
                SavesDirOverride = root,
            }));

            Assert.Equal("my save.zip", Path.GetFileName(entry.ZipPath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
