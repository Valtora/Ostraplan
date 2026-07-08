namespace Ostraplan.Core;

/// <summary>An RGBA overlay colour, each component 0..1 — the game's <c>zoneColor</c>.</summary>
public readonly record struct ZoneColor(double R, double G, double B, double A)
{
    /// <summary>The vanilla teal cargo-zone tint — a sane default for a freshly-created zone.</summary>
    public static ZoneColor Default => new(0.239, 0.741, 0.659, 1.0);
}

/// <summary>The editable, non-tile fields of a <see cref="ShipZone"/> — snapshotted before/after by
/// <c>SetZoneMetaCommand</c> so a rename / recolour / type / role / advanced edit is one undo step.</summary>
public sealed record ZoneMeta(
    string Name, ZoneColor Color, IReadOnlyList<string> TileConds, IReadOnlyList<string> CategoryConds,
    string? PersonSpec, string? TargetPSpec, bool TriggerOnOwner);

/// <summary>
/// A ship zone: a painted set of tiles tagged with zone conditions that change crew AI and trade
/// behaviour on those tiles (the game's <c>JsonZone</c>). Unlike rooms — which the game re-derives by
/// airtight flood-fill on every load and therefore self-heal — zones are <b>authored data trusted
/// verbatim</b> by the loader, so the tool must preserve them exactly and re-project their tile
/// indices whenever the grid frame changes.
///
/// <para>Tiles are held in <b>document</b> tile coordinates (the same plane as
/// <see cref="Placement.X"/>/<see cref="Placement.Y"/>), <b>not</b> the game's flat row-major indices.
/// The flat indices are derived only at write time against the target grid frame (see
/// <see cref="ZoneGeometry"/>), so moving or reframing the ship keeps every zone on the right cells —
/// this is what fixes the export/save-edit relocation bug.</para>
/// </summary>
public sealed class ShipZone
{
    // ---- the zone-type conditions (painted onto each covered tile) ----
    public const string CondHaul = "IsZoneStockpile";        // "Haul"
    public const string CondBarter = "IsZoneBarter";         // "Barter"
    public const string CondForbid = "IsZoneForbid";         // "Forbid" (no-go)
    public const string CondTrigger = "IsZoneTrigger";       // encounter trigger (target role)
    public const string CondTriggerOwner = "IsZoneTriggerOwner";
    public const string CondSpawn = "IsZoneSpawn";

    /// <summary>Stable identity across edits (commands mutate the zone in place, never swap it), so the
    /// canvas active-zone reference and panel rows stay valid — mirrors <see cref="Placement.Id"/>.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    public string Name { get; set; } = "";
    public ZoneColor Color { get; set; } = ZoneColor.Default;

    /// <summary>The zone-type conds (<c>IsZoneStockpile</c>/<c>IsZoneBarter</c>/<c>IsZoneForbid</c>/
    /// <c>IsZoneTrigger</c>/…). A zone can carry several (the vanilla "cargo" zone is Haul+Barter).</summary>
    public List<string> TileConds { get; set; } = [];

    /// <summary>For a trigger zone, the <c>Trigger*</c> name (→ <c>zone_triggers</c> → an encounter); for a
    /// stockpile zone, an item-condition filter. Opaque condition strings — round-tripped verbatim.</summary>
    public List<string> CategoryConds { get; set; } = [];

    /// <summary>The zone owner PersonSpec (e.g. <c>ZonePlayer</c>, or a content spec like <c>FlotillaBroker</c>).</summary>
    public string? PersonSpec { get; set; }

    /// <summary>The target-role PersonSpec (<c>ZoneCaptain</c>/<c>ZoneCrew</c>/<c>ZoneCaptainAndCrew</c>, or a content spec).</summary>
    public string? TargetPSpec { get; set; }

    public bool TriggerOnOwner { get; set; }

    /// <summary>The covered tiles, in <b>document</b> coordinates.</summary>
    public HashSet<(int X, int Y)> Tiles { get; set; } = [];

    public bool IsHaul => TileConds.Contains(CondHaul);
    public bool IsBarter => TileConds.Contains(CondBarter);
    public bool IsForbid => TileConds.Contains(CondForbid);
    public bool IsTrigger => TileConds.Contains(CondTrigger) || TileConds.Contains(CondTriggerOwner);

    /// <summary>The editable non-tile fields as a snapshot (for command before/after).</summary>
    public ZoneMeta Meta => new(Name, Color, [.. TileConds], [.. CategoryConds], PersonSpec, TargetPSpec, TriggerOnOwner);

    /// <summary>Overwrite the editable non-tile fields from a snapshot (undo/redo, editor apply).</summary>
    public void ApplyMeta(ZoneMeta m)
    {
        Name = m.Name;
        Color = m.Color;
        TileConds = [.. m.TileConds];
        CategoryConds = [.. m.CategoryConds];
        PersonSpec = m.PersonSpec;
        TargetPSpec = m.TargetPSpec;
        TriggerOnOwner = m.TriggerOnOwner;
    }
}

/// <summary>
/// The one place that converts between the game's flat row-major tile indices (<c>col + row*nCols</c>,
/// the space <c>aZones.aTiles</c> and <c>aRooms.aTiles</c> share) and Ostraplan's document tile
/// coordinates. Import decodes against the imported ship's own grid (whose origin coincides with the
/// document origin); export / save-edit encode against whatever frame those writers already use for the
/// parts, so zones and parts always agree.
/// </summary>
public static class ZoneGeometry
{
    /// <summary>Game flat index → document tile. Valid on import, where doc tile (0,0) == game grid tile (0,0).</summary>
    public static (int X, int Y) IndexToDoc(int index, int nCols) => (index % nCols, index / nCols);

    /// <summary>Document tile → game flat index in a frame with document-coord origin
    /// (<paramref name="originCol"/>,<paramref name="originRow"/>) and size <paramref name="nCols"/>×<paramref name="nRows"/>,
    /// or -1 when the tile falls outside that frame (the caller must drop those, never emit them — one
    /// out-of-range index makes the game drop that zone and every zone after it).</summary>
    public static int DocToIndex(int docX, int docY, int originCol, int originRow, int nCols, int nRows)
    {
        int col = docX - originCol, row = docY - originRow;
        if (col < 0 || col >= nCols || row < 0 || row >= nRows) return -1;
        return col + row * nCols;
    }
}
