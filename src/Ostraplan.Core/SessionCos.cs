using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ostraplan.Core;

/// <summary>
/// The session record's <c>aCOs</c> array, read and edited without parsing the record.
///
/// <para><b>Why this exists.</b> A condowner save entry does not have to live in the record of the ship whose item
/// it belongs to. The game keeps one global registry, <c>DataHandler.dictCOSaves</c>, and fills it from the
/// <c>aCOs</c> of every record it loads (<c>Ship.InitShip</c> copies <c>json.aCOs</c> in and then nulls it). On
/// save it writes the COs of the ship the player is standing on into that ship's record and everything else into
/// the session record. So a ship the player is not aboard has a record full of items whose COs are somewhere
/// else entirely — in a real save, 7686 items against 2 COs, with all 7686 in the session record.</para>
///
/// <para><b>Why it is done on bytes.</b> The session record is the largest thing in a save, tens of MB. Round
/// tripping it through a parser would rewrite every number in the file to whatever the serialiser's formatting
/// produces, which is an enormous blast radius for deleting some array elements — the same reasoning as
/// <c>SaveGrant.InsertShipOwner</c>. <see cref="RemoveInto"/> therefore copies the record through verbatim and
/// omits byte ranges, so every byte it does not delete is the byte the game wrote.</para>
/// </summary>
internal static class SessionCos
{
    /// <summary>The condowner entries in <paramref name="entryName"/> whose <c>strID</c> is in
    /// <paramref name="wanted"/>, as mutable nodes. Only the wanted ones are materialised, so the cost is the scan
    /// rather than the record.</summary>
    public static Dictionary<string, JsonNode> Read(ZipArchive zip, string entryName, IReadOnlySet<string> wanted)
    {
        var found = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        if (wanted.Count == 0 || zip.GetEntry(entryName) is not { } entry) return found;

        var bytes = ReadAll(entry);
        foreach (var (start, end) in Elements(bytes))
        {
            var span = bytes.AsSpan(start, end - start);
            if (IdOf(span) is not { } id || !wanted.Contains(id) || found.ContainsKey(id)) continue;
            if (JsonNode.Parse(span.ToArray()) is { } node) found[id] = node;
        }
        return found;
    }

    /// <summary>
    /// Copy the session record from <paramref name="entryName"/> into <paramref name="dst"/>, omitting every
    /// <c>aCOs</c> element whose <c>strID</c> is in <paramref name="remove"/>. Returns how many were dropped.
    ///
    /// <para>Everything outside the array is copied byte for byte, and so is every surviving element. The only
    /// bytes this writes itself are the separators between survivors, taken from the record's own first separator
    /// so they come out identical to what was there. Trying instead to cut each dropped element together with one
    /// adjacent separator does not work: a run of dropped elements at the front of the array leaves a leading
    /// comma. Dropping every element leaves the array's brackets with whitespace between them, still valid.</para>
    /// </summary>
    public static int RemoveInto(ZipArchive zip, string entryName, IReadOnlySet<string> remove, Stream dst)
    {
        var entry = zip.GetEntry(entryName)
            ?? throw new InvalidDataException($"'{entryName}' is not in the save.");
        var bytes = ReadAll(entry);

        var spans = Elements(bytes);
        var kept = spans
            .Where(s => IdOf(bytes.AsSpan(s.Start, s.End - s.Start)) is not { } id || !remove.Contains(id))
            .ToList();

        if (kept.Count == spans.Count)   // nothing to do: hand the record straight back
        {
            dst.Write(bytes, 0, bytes.Length);
            return 0;
        }

        var (first, last) = (spans[0], spans[^1]);
        var (sepStart, sepLength) = spans.Count > 1 ? (first.End, spans[1].Start - first.End) : (0, 0);

        dst.Write(bytes, 0, first.Start);
        for (var i = 0; i < kept.Count; i++)
        {
            if (i > 0) dst.Write(bytes, sepStart, sepLength);
            dst.Write(bytes, kept[i].Start, kept[i].End - kept[i].Start);
        }
        dst.Write(bytes, last.End, bytes.Length - last.End);
        return spans.Count - kept.Count;
    }

    // ---- the scan ----

    /// <summary>The byte range of each element of the session object's own <c>aCOs</c> array. Nesting is counted by
    /// hand rather than read off <c>CurrentDepth</c>, so "a direct property of the session object" means exactly
    /// that and never a nested <c>aCOs</c> somewhere below it.</summary>
    private static List<(int Start, int End)> Elements(byte[] bytes)
    {
        var found = new List<(int, int)>();
        var offset = Bom(bytes);
        var reader = new Utf8JsonReader(bytes.AsSpan(offset), new JsonReaderOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });

        var depth = 0;
        var objectDepth = -1;   // the depth *inside* the first object, which is the session record itself
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    depth++;
                    if (objectDepth < 0 && reader.TokenType == JsonTokenType.StartObject) objectDepth = depth;
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    depth--;
                    break;
                case JsonTokenType.PropertyName when depth == objectDepth && reader.ValueTextEquals("aCOs"u8):
                    if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) break;
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        var start = (int)reader.TokenStartIndex + offset;
                        reader.Skip();
                        found.Add((start, (int)reader.BytesConsumed + offset));
                    }
                    return found;   // one aCOs on the session object; nothing after it can be another
            }
        }
        return found;
    }

    /// <summary>The <c>strID</c> of one condowner element, read straight off its bytes. Null when it has none.</summary>
    private static string? IdOf(ReadOnlySpan<byte> element)
    {
        var reader = new Utf8JsonReader(element, new JsonReaderOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        var depth = 0;
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    depth++;
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    depth--;
                    break;
                case JsonTokenType.PropertyName when depth == 1 && reader.ValueTextEquals("strID"u8):
                    return reader.Read() && reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
        }
        return null;
    }

    private static int Bom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;

    private static byte[] ReadAll(ZipArchiveEntry entry)
    {
        using var buffer = new MemoryStream(checked((int)Math.Min(entry.Length, int.MaxValue)));
        using (var s = entry.Open()) s.CopyTo(buffer);
        return buffer.ToArray();
    }
}
