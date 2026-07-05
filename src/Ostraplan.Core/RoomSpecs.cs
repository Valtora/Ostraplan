using System.Text.Json;

namespace Ostraplan.Core;

/// <summary>A room-spec requirement/forbid: a condtrigger name and how many matching
/// parts it needs (the "xN" multiplicity in "TIsChairInstalled=1.0x4").</summary>
public sealed record RoomReq(string Trigger, int Count);

/// <summary>
/// One entry from <c>data/rooms</c> (JsonRoomSpec). A room certifies as the highest-
/// priority spec it matches (<see cref="RoomCertifier"/>).
/// </summary>
public sealed record RoomSpecDef(
    string Name, string Friendly, string? Icon, int MinTileSize, int MaxTileSize,
    int Priority, bool AllowVoid, double ValueModifier,
    IReadOnlyList<RoomReq> Reqs, IReadOnlyList<RoomReq> Forbids)
{
    public bool IsBlank => Name == "Blank";

    public static RoomSpecDef Parse(JsonElement e) => new(
        Json.Str(e, "strName") ?? "",
        Json.Str(e, "strNameFriendly") ?? "",
        Json.Str(e, "strIconName"),
        Json.Int(e, "nMinTileSize", -1),
        Json.Int(e, "nMaxTileSize", -1),
        Json.Int(e, "nPriority"),
        Json.Bool(e, "bAllowVoid"),
        Json.Dbl(e, "fValueModifier", 1.0),
        Json.StrArray(e, "aReqs").Select(ParseReq).ToArray(),
        Json.StrArray(e, "aForbids").Select(ParseReq).ToArray());

    /// <summary>"TIsChairInstalled=1.0x4" -> (TIsChairInstalled, 4). The value between
    /// '=' and 'x' is the chance (1.0 for specs); the number after 'x' is the required
    /// count. A range "x4-6" takes the low end; a bare name defaults to count 1.</summary>
    private static RoomReq ParseReq(string entry)
    {
        var name = LootDef.CondName(entry);
        var count = 1;
        var x = entry.IndexOf('x');
        if (x >= 0 && x + 1 < entry.Length)
        {
            var tail = entry[(x + 1)..];
            var dash = tail.IndexOf('-');
            if (dash >= 0) tail = tail[..dash];
            if (int.TryParse(tail.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var c)) count = c;
        }
        return new RoomReq(name, count);
    }
}

/// <summary>Why a room failed to certify as a given spec (for the law report).</summary>
public sealed record CertFailure(string Spec, string Reason);

/// <summary>
/// Port of <c>Room.CreateRoomSpecs</c> / <c>RoomSpec.Matches</c> (verified 0.15.1.6):
/// a room certifies as the highest-<c>nPriority</c> spec that matches, else Blank.
/// A spec matches iff <c>bAllowVoid == room.Void</c>, tile count within
/// [min,max] (−1 = unbounded), no member part fires any <c>aForbids</c> trigger, and
/// every <c>aReqs</c> trigger is satisfied with multiplicity (each match consumes one
/// part; installed parts stack-count 1). Floor-grate members are ignored; only
/// installed parts (already the room's <see cref="RoomModel.Parts"/>) count.
/// </summary>
public static class RoomCertifier
{
    /// <summary>Load and priority-sort the room specs from the effective data.</summary>
    public static IReadOnlyList<RoomSpecDef> LoadSpecs(DataIndex index) =>
        index.Type("rooms")
             .Select(kv => RoomSpecDef.Parse(kv.Value.El))
             .OrderByDescending(s => s.Priority)
             .ToList();

    /// <summary>Set <see cref="RoomModel.RoomSpec"/> for every room in the partition.</summary>
    public static void CertifyAll(RoomPartition partition, IReadOnlyList<RoomSpecDef> specs, Catalog catalog)
    {
        foreach (var room in partition.Rooms)
            room.RoomSpec = Certify(room, specs, catalog);
    }

    /// <summary>The spec name a room certifies as (highest priority that matches, else "Blank").</summary>
    public static string Certify(RoomModel room, IReadOnlyList<RoomSpecDef> specsByPriorityDesc, Catalog catalog,
        ICollection<string>? notes = null)
    {
        foreach (var spec in specsByPriorityDesc)
        {
            if (spec.IsBlank) continue;               // Blank is the fallback, not a match
            if (Matches(spec, room, catalog, notes) is null) return spec.Name;
        }
        return "Blank";
    }

    /// <summary>Null if the spec matches the room, else the first failing reason.</summary>
    public static CertFailure? Matches(RoomSpecDef spec, RoomModel room, Catalog catalog, ICollection<string>? notes = null)
    {
        if (spec.Reqs.Count == 0) return new CertFailure(spec.Name, "spec has no requirements");
        if (spec.AllowVoid != room.Void)
            return new CertFailure(spec.Name, room.Void ? "room is open to space (void)" : "spec requires a void room");
        var n = room.Tiles.Count;
        if (spec.MinTileSize != -1 && n < spec.MinTileSize)
            return new CertFailure(spec.Name, $"too small ({n} tiles, needs {spec.MinTileSize})");
        if (spec.MaxTileSize != -1 && n > spec.MaxTileSize)
            return new CertFailure(spec.Name, $"too large ({n} tiles, max {spec.MaxTileSize})");

        // consume required counts across the room's installed parts; reject on any forbid
        var need = spec.Reqs.Select(r => new RoomReq(r.Trigger, r.Count)).ToList();
        foreach (var part in room.Parts)
        {
            if (part.Part.Has("IsFloorGrate")) continue;   // floor grates don't count (game skip)
            foreach (var fb in spec.Forbids)
                if (Fires(fb.Trigger, part, catalog, notes))
                    return new CertFailure(spec.Name, $"forbidden item present: \"{part.Part.Friendly}\" ({fb.Trigger})");
            for (var i = need.Count - 1; i >= 0; i--)
            {
                if (Fires(need[i].Trigger, part, catalog, notes)) need[i] = need[i] with { Count = need[i].Count - 1 };
                if (need[i].Count <= 0) need.RemoveAt(i);
            }
        }

        if (need.Count == 0) return null;
        var missing = string.Join(", ", need.Select(r => r.Count == 1 ? r.Trigger : $"{r.Trigger} ×{r.Count}"));
        return new CertFailure(spec.Name, $"missing required: {missing}");
    }

    /// <summary>Does a condtrigger (or bare condition) fire on a part's condition set?</summary>
    private static bool Fires(string trigger, PlacedPart part, Catalog catalog, ICollection<string>? notes) =>
        catalog.Triggers.TryGetValue(trigger, out var ct)
            ? CondEval.Triggered(ct, part.Part.CondSet, catalog, notes)
            : part.Part.Has(trigger);
}
