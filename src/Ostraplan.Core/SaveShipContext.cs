using System.Text.Json.Nodes;

namespace Ostraplan.Core;

/// <summary>
/// A lightweight reference to the save a design was imported from — enough to re-locate the ship on
/// reopen: the save's folder name and the player ship's RegID (its record is <c>ships/&lt;RegId&gt;.json</c>).
/// This is what the .oplan persists; the heavy <see cref="SaveShipContext"/> is rebuilt from it against the
/// chosen save when the design is injected.
/// </summary>
public sealed record SaveSourceRef(string SaveName, string RegId);

/// <summary>
/// An original structural part from the source save, as the diff needs it: the doc-space pose it imported
/// at (to detect a move), and the strIDs of the contained-cargo subtree hanging off it (preserved with the
/// part, or dropped-with-warning if the part is deleted, on inject).
/// </summary>
public sealed record OriginPart(int X, int Y, int Rot, IReadOnlyList<string> CargoIds);

/// <summary>
/// Everything retained from a save's player ship so an edited design can be written back into a <b>copy</b>
/// of that save (Phase 2) without losing crew, cargo, world position or ship identity. Structural parts live
/// on the grid as <see cref="Placement"/>s tagged with <see cref="Placement.OriginStrID"/>; this context is
/// what those tags resolve to.
///
/// <para>The model (verified against a real save, 2952 items ↔ 2957 COs): every <c>aItems</c> entry is 1:1
/// with an <c>aCOs</c> entry by <c>strID</c> — that CO carries the item's live state (wear, power, gas,
/// inventory, door state). Cargo and equipment are sub-objects parented onto items or crew by
/// <c>strParentID</c>/<c>strSlotParentID</c>. Preserving a part = keeping its item entry, its CO entry, and
/// its cargo subtree; a newly-added part needs no CO at all (the game builds a default one from the def on
/// load). Nothing here is written in Phase 1 — the diff only reads <see cref="Origins"/>.</para>
/// </summary>
public sealed class SaveShipContext
{
    /// <summary>The originating save (folder name + ship RegID) — this is what the .oplan persists.</summary>
    public required SaveSourceRef Source { get; init; }

    /// <summary>The data zip the ship was read from (re-resolvable from <see cref="Source"/> on reopen).</summary>
    public required string ZipPath { get; init; }

    /// <summary>The player character CO's <c>strID</c> (the session record's <c>strPlayerCO</c>). Its
    /// <c>StatUSD</c> cond is the authoritative money balance, and — for the player's own ship — that CO is crew
    /// in this ship record, so the save-edit cost deduction rewrites it here. Null if the record couldn't be
    /// read or the player CO isn't on this ship (then the edit-cost deduction is unavailable).</summary>
    public string? PlayerCoId { get; init; }

    /// <summary>The save's current game epoch (<c>objSystem.dfEpoch</c>) — stamped onto tickers baked into
    /// injected/healed device COs so they fire on load. 0 if it couldn't be read (the ticker still fires, just
    /// immediately rather than after one period).</summary>
    public double Epoch { get; init; }

    /// <summary>The player-ship record as a mutable node. Phase 2 rewrites only its structural arrays
    /// (<c>aItems</c>/<c>aCOs</c>/<c>aRooms</c>/<c>aRating</c> + grid fields) and preserves the rest verbatim.</summary>
    public required JsonNode ShipRecord { get; init; }

    /// <summary>Structural (grid-placed) part <c>strID</c> → its imported pose + cargo subtree. The keys are
    /// exactly the non-null <see cref="Placement.OriginStrID"/>s of the imported document, so a no-op diff
    /// classifies every part as kept.</summary>
    public required IReadOnlyDictionary<string, OriginPart> Origins { get; init; }

    /// <summary>Every <c>aItems</c> entry by <c>strID</c> (structural + contained cargo), as live nodes into
    /// <see cref="ShipRecord"/> — for Phase 2's verbatim writes.</summary>
    public required IReadOnlyDictionary<string, JsonNode> ItemsById { get; init; }

    /// <summary>Every <c>aCOs</c> entry by <c>strID</c> — the 1:1 live state for each item, plus the handful of
    /// crew and loot-spawner COs that have no item — for Phase 2's CO filtering.</summary>
    public required IReadOnlyDictionary<string, JsonNode> CosById { get; init; }

    /// <summary>Each structural part's contained-cargo tree, keyed by its origin <c>strID</c> (the same keys as
    /// <see cref="Origins"/>). A fresh import attaches these to the placements directly; on <c>.oplan</c> reopen
    /// the throwaway import doc is discarded, so the app re-attaches from this map by matching
    /// <see cref="Placement.OriginStrID"/>. Drives the inventory viewer.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<CargoItem>> CargoByOrigin { get; init; } =
        new Dictionary<string, IReadOnlyList<CargoItem>>();
}
