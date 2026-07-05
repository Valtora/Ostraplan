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
    public required int NCols { get; init; }
    public required int NRows { get; init; }
    public required double VShipPosX { get; init; }
    public required double VShipPosY { get; init; }
    public required IReadOnlyList<TemplateItem> Items { get; init; }
    public required IReadOnlyList<StoredRoom> Rooms { get; init; }
    public required IReadOnlyList<string> Rating { get; init; }

    public bool HasBakedRating => Rating.Count >= 5 && Rating.Skip(1).Any(s => !string.IsNullOrEmpty(s));

    /// <summary>Every ship object in one ships/*.json file (array-wrapped; non-ship files yield nothing).</summary>
    public static IEnumerable<ShipTemplate> ParseFile(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException) { yield break; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray())
                    if (Parse(el) is { } ship) yield return ship;
            }
            else if (Parse(root) is { } single) yield return single;
        }
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

        return new ShipTemplate
        {
            Name = Json.Str(e, "strName") ?? "",
            Designation = Json.Str(e, "designation"),
            NCols = Json.Int(e, "nCols"),
            NRows = Json.Int(e, "nRows"),
            VShipPosX = px,
            VShipPosY = py,
            Items = items,
            Rooms = rooms,
            Rating = Json.StrArray(e, "aRating"),
        };
    }
}
