using System.Text.Json;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The load-time mend for raw control characters inside JSON string literals (DataIndex's third parse attempt).
/// Core game data writes multi-line <c>strDesc</c> values with real line breaks inside the quotes, which is invalid
/// per the spec and which the game's own parser accepts; before this, eight core files were dropped on a stock
/// 1.0.0.7 install and took twelve referenced interactions with them.
/// </summary>
public class DataRepairTests
{
    /// <summary>Mend, then parse, and read a string property back out — the parsed value is the whole point.</summary>
    private static string RoundTrip(string json, string property)
    {
        var mended = DataIndex.EscapeControlCharsInStrings(json);
        Assert.NotNull(mended);
        using var doc = JsonDocument.Parse(mended!);
        return doc.RootElement.GetProperty(property).GetString()!;
    }

    [Fact]
    public void A_raw_line_break_inside_a_string_is_escaped_and_keeps_its_value()
    {
        // exactly the shape core writes: "strDesc" : "first line<CRLF><CRLF>second line"
        var json = "{ \"strDesc\" : \"At long last.\r\n\r\nBefore you, a golden sphere.\" }";

        Assert.Equal("At long last.\r\n\r\nBefore you, a golden sphere.", RoundTrip(json, "strDesc"));
    }

    [Theory]
    [InlineData('\n')]
    [InlineData('\r')]
    [InlineData('\t')]
    [InlineData('\b')]
    [InlineData('\f')]
    [InlineData('\v')]        // vertical tab: no shorthand escape in this mend, so it goes out as \u000b
    [InlineData('\u001f')]  // the last control character the spec forbids unescaped
    public void Every_forbidden_control_character_survives_the_mend(char control)
    {
        Assert.Equal($"a{control}b", RoundTrip($"{{ \"v\" : \"a{control}b\" }}", "v"));
    }

    [Fact]
    public void Control_characters_outside_a_string_are_left_alone()
    {
        // the file's own newlines and indentation are legal whitespace: nothing to mend, so nothing is rewritten
        Assert.Null(DataIndex.EscapeControlCharsInStrings("{\r\n\t\"a\" : 1,\r\n\t\"b\" : 2\r\n}"));
    }

    [Fact]
    public void A_file_that_is_already_valid_is_never_rewritten()
    {
        Assert.Null(DataIndex.EscapeControlCharsInStrings("""{ "strDesc" : "already \r\n escaped properly" }"""));
    }

    [Fact]
    public void An_escaped_quote_does_not_end_the_string()
    {
        // if \" were read as a close, the line break after it would look like it sat outside a string and be missed
        var json = "{ \"v\" : \"he said \\\"go\\\" then\nleft\" }";

        Assert.Equal("he said \"go\" then\nleft", RoundTrip(json, "v"));
    }

    [Fact]
    public void An_escaped_backslash_at_the_end_of_a_string_does_not_swallow_the_closing_quote()
    {
        // "a\\" closes; reading that second backslash as an escape would run the string on into the next key
        var json = "{ \"path\" : \"a\\\\\", \"v\" : \"x\ny\" }";

        Assert.Equal(@"a\", RoundTrip(json, "path"));
        Assert.Equal("x\ny", RoundTrip(json, "v"));
    }

    [Fact]
    public void A_line_break_between_two_strings_is_not_mistaken_for_one_inside_them()
    {
        Assert.Null(DataIndex.EscapeControlCharsInStrings("{\n  \"a\" : \"one\",\n  \"b\" : \"two\"\n}"));
    }

    [Fact]
    public void Mending_cannot_rescue_json_that_is_broken_some_other_way()
    {
        // a missing brace is still a missing brace: the mend is validated by re-parsing, so a bad file still fails
        var mended = DataIndex.EscapeControlCharsInStrings("{ \"v\" : \"a\nb\" ");

        Assert.NotNull(mended);
        // ThrowsAny: the reader raises the derived JsonReaderException, which is what the loader's catch sees too
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(mended!));
    }

    [Fact]
    public void The_mend_only_ever_adds_escapes_and_never_touches_structure()
    {
        var json = "{ \"v\" : \"a\nb\", \"n\" : 42 }";
        var mended = DataIndex.EscapeControlCharsInStrings(json)!;

        Assert.Equal(json.Replace("\n", "\\n"), mended);
    }
}
