using System.Text.Json;

namespace Ostraplan.Core;

/// <summary>One placed item in a saved ship: its condowner name, world centre
/// (fX,fY), and rotation. Positions are the item transform's centre — see
/// <see cref="ShipGrid"/> for the centre → top-left-tile conversion.
/// <para><see cref="Contained"/> marks a sub-object held inside another item
/// (<c>strParentID</c>) or slotted into it (<c>strSlotParentID</c>) — cargo, tools,
/// installed modules. These are not laid on the grid (they carry no wall/floor conds);
/// an import drops them (layout only).</para></summary>
public sealed record TemplateItem(string DefName, double FX, double FY, double FRotation, string? StrID, bool Contained = false);

/// <summary>A room as the game computed and baked it into the template: the tile
/// indices it owns (row-major into nCols×nRows), its certified spec, and void flag.
/// This is the parity ground truth.</summary>
public sealed record StoredRoom(IReadOnlyList<int> TileIndices, string RoomSpec, bool Void);

/// <summary>A zone exactly as stored on the ship (<c>aZones</c>): its flat row-major tile indices
/// (same index space as <see cref="StoredRoom"/>) plus the verbatim fields. Converted to a
/// document-coordinate <see cref="ShipZone"/> at import time.</summary>
public sealed record StoredZone(
    string Name, IReadOnlyList<int> Tiles, IReadOnlyList<string> TileConds, IReadOnlyList<string> CategoryConds,
    string? PersonSpec, string? TargetPSpec, bool TriggerOnOwner, ZoneColor Color);

/// <summary>
/// A parsed <c>data/ships</c> template (JsonShip). Ship files are top-level JSON
/// arrays; the ship object is an element with <c>nCols</c>+<c>aItems</c>. Carries
/// the grid, the placed items, and the game's baked <c>aRooms</c>/<c>aRating</c>
/// (present on ~all templates for rooms, only a couple for rating).
/// </summary>
public sealed class ShipTemplate
{
    public required string Name { get; init; }
    public required string? Designation { get; init; }
    /// <summary>The player-visible name (<c>publicName</c>). "$TEMPLATE" for stock templates;
    /// a real name (e.g. "Charon") for a ship from a save.</summary>
    public string? PublicName { get; init; }
    public required int NCols { get; init; }
    public required int NRows { get; init; }
    public required double VShipPosX { get; init; }
    public required double VShipPosY { get; init; }
    public required IReadOnlyList<TemplateItem> Items { get; init; }
    public required IReadOnlyList<StoredRoom> Rooms { get; init; }
    public required IReadOnlyList<string> Rating { get; init; }
    /// <summary>The ship's painted zones (<c>aZones</c>), verbatim. Empty on the many ships that carry none.</summary>
    public IReadOnlyList<StoredZone> Zones { get; init; } = [];

    public bool HasBakedRating => Rating.Count >= 5 && Rating.Skip(1).Any(s => !string.IsNullOrEmpty(s));

    /// <summary>Every ship object in one ships/*.json file (array-wrapped; non-ship files yield nothing).</summary>
    public static IEnumerable<ShipTemplate> ParseFile(string json) => ParseFileChecked(json, out _);

    /// <summary>
    /// <see cref="ParseFile"/>, but saying <b>why</b> when it comes back empty: either the text isn't valid JSON
    /// (with the parser's complaint, the position, and an excerpt — see <see cref="JsonDiagnostic"/>), or it parsed
    /// and holds nothing ship-shaped.
    ///
    /// <para><paramref name="failure"/> is null on success. Callers that report a failed import use this rather than
    /// <see cref="ParseFile"/>, because "the ship could not be parsed" with the reason discarded leaves a user with
    /// a broken save and no way to find out what broke it.</para>
    /// </summary>
    public static IReadOnlyList<ShipTemplate> ParseFileChecked(string json, out string? failure)
    {
        failure = null;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException ex)
        {
            failure = JsonDiagnostic.Describe(ex, json);
            return [];
        }

        using (doc)
        {
            var root = doc.RootElement;
            var ships = new List<ShipTemplate>();
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray())
                    if (Parse(el) is { } ship) ships.Add(ship);
            }
            else if (Parse(root) is { } single) ships.Add(single);

            if (ships.Count == 0) failure = NoShipHere(root);
            return ships;
        }
    }

    /// <summary>Why a file that is valid JSON still yielded no ship — what was actually in it, so the user can tell
    /// a wrong file from a damaged one.</summary>
    private static string NoShipHere(JsonElement root)
    {
        const string what = "A ship is an object carrying nCols and an aItems array.";
        return root.ValueKind switch
        {
            JsonValueKind.Array when root.GetArrayLength() == 0 =>
                $"The text is valid JSON but holds an empty array. {what}",
            JsonValueKind.Array =>
                $"The text is valid JSON but none of its {root.GetArrayLength()} element(s) is a ship. {what} " +
                $"The first element {Shape(root[0])}.",
            JsonValueKind.Object =>
                $"The text is valid JSON but the object in it is not a ship. {what} It {Shape(root)}.",
            _ => $"The text is valid JSON but is a bare {root.ValueKind.ToString().ToLowerInvariant()}, not a ship. {what}",
        };
    }

    /// <summary>A one-line description of an element that was expected to be a ship: which of the two required
    /// fields it has, and what it does carry.</summary>
    private static string Shape(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return $"is a {e.ValueKind.ToString().ToLowerInvariant()}, not an object";

        var hasCols = e.TryGetProperty("nCols", out _);
        var items = e.TryGetProperty("aItems", out var a) ? a.ValueKind : (JsonValueKind?)null;
        var missing = !hasCols && items is null
            ? "has neither nCols nor aItems"
            : !hasCols ? "has no nCols"
            : items is null ? "has no aItems"
            : $"has an aItems that is a {items.Value.ToString().ToLowerInvariant()}, not an array";

        var keys = e.EnumerateObject().Take(8).Select(p => p.Name).ToList();
        return keys.Count == 0
            ? $"{missing} (it has no fields at all)"
            : $"{missing}; its fields start {string.Join(", ", keys)}";
    }

    /// <summary>Parse a single ship object, or null if it isn't ship-shaped.</summary>
    public static ShipTemplate? Parse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty("nCols", out _) || !e.TryGetProperty("aItems", out var itemsEl)
            || itemsEl.ValueKind != JsonValueKind.Array) return null;

        var (px, py) = (0.0, 0.0);
        if (e.TryGetProperty("vShipPos", out var pos) && pos.ValueKind == JsonValueKind.Object)
            (px, py) = (Json.Dbl(pos, "x"), Json.Dbl(pos, "y"));

        var items = new List<TemplateItem>();
        foreach (var it in itemsEl.EnumerateArray())
        {
            var def = Json.Str(it, "strName");
            if (string.IsNullOrEmpty(def)) continue;
            var contained = Json.Str(it, "strParentID") is { Length: > 0 } || Json.Str(it, "strSlotParentID") is { Length: > 0 };
            items.Add(new TemplateItem(def!, Json.Dbl(it, "fX"), Json.Dbl(it, "fY"),
                Json.Dbl(it, "fRotation"), Json.Str(it, "strID"), contained));
        }

        var rooms = new List<StoredRoom>();
        if (e.TryGetProperty("aRooms", out var roomsEl) && roomsEl.ValueKind == JsonValueKind.Array)
            foreach (var r in roomsEl.EnumerateArray())
                rooms.Add(new StoredRoom(Json.IntArray(r, "aTiles"),
                    Json.Str(r, "roomSpec") ?? "Blank", Json.Bool(r, "bVoid")));

        var zones = new List<StoredZone>();
        if (e.TryGetProperty("aZones", out var zonesEl) && zonesEl.ValueKind == JsonValueKind.Array)
            foreach (var z in zonesEl.EnumerateArray())
                zones.Add(new StoredZone(
                    Json.Str(z, "strName") ?? "",
                    Json.IntArray(z, "aTiles"),
                    Json.StrArray(z, "aTileConds"),
                    Json.StrArray(z, "categoryConds"),
                    Json.Str(z, "strPersonSpec"),
                    Json.Str(z, "strTargetPSpec"),
                    Json.Bool(z, "bTriggerOnOwner"),
                    ParseZoneColor(z)));

        return new ShipTemplate
        {
            Name = Json.Str(e, "strName") ?? "",
            Designation = Json.Str(e, "designation"),
            PublicName = Json.Str(e, "publicName"),
            NCols = Json.Int(e, "nCols"),
            NRows = Json.Int(e, "nRows"),
            VShipPosX = px,
            VShipPosY = py,
            Items = items,
            Rooms = rooms,
            Rating = Json.StrArray(e, "aRating"),
            Zones = zones,
        };
    }

    /// <summary>Read a zone's <c>zoneColor</c> {r,g,b,a} object; alpha defaults to 1 when absent, and a
    /// missing/blank colour falls back to <see cref="ZoneColor.Default"/>.</summary>
    private static ZoneColor ParseZoneColor(JsonElement z)
    {
        if (!z.TryGetProperty("zoneColor", out var c) || c.ValueKind != JsonValueKind.Object)
            return ZoneColor.Default;
        var a = c.TryGetProperty("a", out _) ? Json.Dbl(c, "a") : 1.0;
        return new ZoneColor(Json.Dbl(c, "r"), Json.Dbl(c, "g"), Json.Dbl(c, "b"), a);
    }
}
