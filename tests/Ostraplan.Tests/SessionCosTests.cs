using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The session record's condowner array, read and cut on its bytes.
///
/// <para>This is the half of the edit path that cannot be checked by eye: it deletes byte ranges out of the
/// largest file in a save without parsing it, so the separators have to come out with the right elements or the
/// record stops being JSON. The cases below are the ones where that goes wrong — the first element, the last, two
/// in a row, and all of them — plus the guarantee the byte approach exists for, that every byte it does not delete
/// is the byte the game wrote.</para>
/// </summary>
public class SessionCosTests
{
    private const string Entry = "Ada.json";

    /// <summary>A session record shaped like a real one: four condowners, a number whose formatting no serialiser
    /// would reproduce, and a nested <c>aCOs</c> that must stay invisible (only the session object's own counts).</summary>
    private const string Record = """
        [
          {
            "strShip" : "J-1",
            "strPlayerCO" : "Ada",
            "objSystem" : { "dfEpoch" : 1.5, "dictShips" : [ "J-1" ] },
            "aCOs"    : [
              {
                "strID" : "a",
                "fVal"  : 1.10000000000001
              },
              {
                "strID" : "b",
                "aConds" : [ "X=1.0x2" ]
              },
              {
                "strID" : "c",
                "nested" : { "aCOs" : [ { "strID" : "buried" } ] }
              },
              {
                "strID" : "d"
              }
            ],
            "tail" : "kept"
          }
        ]
        """;

    [Fact]
    public void Only_the_wanted_condowners_are_materialised()
    {
        using var zip = Archive();

        var read = SessionCos.Read(zip, Entry, new HashSet<string>(["a", "d", "nope"]));

        Assert.Equal(["a", "d"], read.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(1.10000000000001, (double)read["a"]!["fVal"]!, 14);
    }

    [Fact]
    public void A_nested_aCOs_is_not_the_session_records_own()
    {
        using var zip = Archive();
        Assert.Empty(SessionCos.Read(zip, Entry, new HashSet<string>(["buried"])));
    }

    [Fact]
    public void Reading_nothing_never_opens_the_record()
    {
        using var zip = Archive();
        Assert.Empty(SessionCos.Read(zip, "no such entry.json", new HashSet<string>(["a"])));
        Assert.Empty(SessionCos.Read(zip, Entry, new HashSet<string>()));
    }

    [Theory]
    [InlineData(new string[0], new[] { "a", "b", "c", "d" })]
    [InlineData(new[] { "a" }, new[] { "b", "c", "d" })]                  // the first: its separator follows it
    [InlineData(new[] { "d" }, new[] { "a", "b", "c" })]                  // the last: its separator precedes it
    [InlineData(new[] { "b" }, new[] { "a", "c", "d" })]
    [InlineData(new[] { "a", "b" }, new[] { "c", "d" })]                  // two in a row, from the front
    [InlineData(new[] { "c", "d" }, new[] { "a", "b" })]                  // two in a row, to the end
    [InlineData(new[] { "a", "c" }, new[] { "b", "d" })]
    [InlineData(new[] { "a", "b", "c", "d" }, new string[0])]             // all of them: an empty array, still valid
    [InlineData(new[] { "nope" }, new[] { "a", "b", "c", "d" })]
    public void Cutting_condowners_leaves_valid_json_holding_exactly_the_rest(string[] remove, string[] expected)
    {
        using var zip = Archive();
        using var outStream = new MemoryStream();

        var dropped = SessionCos.RemoveInto(zip, Entry, new HashSet<string>(remove), outStream);

        var text = Encoding.UTF8.GetString(outStream.ToArray());
        var session = (JsonNode.Parse(text) as JsonArray)![0]!;   // throws if the cut broke the JSON
        var ids = (session["aCOs"] as JsonArray)!.Select(c => (string)c!["strID"]!);

        Assert.Equal(expected, ids);
        Assert.Equal(expected.Length == 4 ? 0 : 4 - expected.Length, dropped);

        // Everything the cut did not delete is byte-identical, which is the whole reason this is done without a
        // parser: a number no serialiser would print back the same way, and a nested object, both survive verbatim
        // for as long as the element around them does.
        Assert.Equal(expected.Contains("a"), text.Contains("1.10000000000001", StringComparison.Ordinal));
        Assert.Equal(expected.Contains("c"), text.Contains("\"strID\" : \"buried\"", StringComparison.Ordinal));
        Assert.Contains("\"tail\" : \"kept\"", text, StringComparison.Ordinal);
        Assert.Equal("J-1", (string)session["strShip"]!);
    }

    [Fact]
    public void A_byte_order_mark_survives_the_cut()
    {
        using var zip = Archive(bom: true);
        using var outStream = new MemoryStream();

        SessionCos.RemoveInto(zip, Entry, new HashSet<string>(["b"]), outStream);

        var bytes = outStream.ToArray();
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var session = (JsonNode.Parse(Encoding.UTF8.GetString(bytes[3..])) as JsonArray)![0]!;
        Assert.Equal(["a", "c", "d"], (session["aCOs"] as JsonArray)!.Select(c => (string)c!["strID"]!));
    }

    private static ZipArchive Archive(bool bom = false)
    {
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        using (var w = new StreamWriter(zip.CreateEntry(Entry).Open(), new UTF8Encoding(bom)))
            w.Write(Record);
        buffer.Position = 0;
        return new ZipArchive(buffer, ZipArchiveMode.Read);
    }
}
