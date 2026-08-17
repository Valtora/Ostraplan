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
/// <para>The model: every <c>aItems</c> entry is 1:1 with an <c>aCOs</c> entry by <c>strID</c> — that CO carries
/// the item's live state (wear, power, gas, inventory, door state). Cargo and equipment are sub-objects parented
/// onto items or crew by <c>strParentID</c>/<c>strSlotParentID</c>. Preserving a part = keeping its item entry,
/// its CO entry, and its cargo subtree; a newly-added part needs no CO at all (the game builds a default one from
/// the def on load). Nothing here is written in Phase 1 — the diff only reads <see cref="Origins"/>.</para>
///
/// <para><b>The CO is not necessarily in this record.</b> The pairing is by <c>strID</c> through the game's one
/// global registry, <c>DataHandler.dictCOSaves</c>, and the save writer partitions it: the COs of the ship the
/// player is standing on go into that ship's record, and every other ship's go into the session record. So a ship
/// the player is away from reads back as items with no COs at all. <see cref="CosById"/> is therefore the union of
/// both, and <see cref="RelocatedCoIds"/> names the ones that came from the session record — the inject writes
/// them into the ship record and the writer takes them out of the session record, which is the shape the game
/// itself produces for a ship the player is aboard. See <see cref="SessionCos"/>.</para>
/// </summary>
public sealed class SaveShipContext
{
    /// <summary>The originating save (folder name + ship RegID) — this is what the .oplan persists.</summary>
    public required SaveSourceRef Source { get; init; }

    /// <summary>The data zip the ship was read from (re-resolvable from <see cref="Source"/> on reopen).</summary>
    public required string ZipPath { get; init; }

    /// <summary>The player character CO's <c>strID</c> (the session record's <c>strPlayerCO</c>). Its
    /// <c>StatUSD</c> cond is the authoritative money balance. Null if the session record couldn't be read.</summary>
    public string? PlayerCoId { get; init; }

    /// <summary>
    /// The RegID of the ship record that actually <b>holds</b> the player CO — which is not necessarily the ship
    /// being edited. A character's CO entry lives in the record for whatever they were standing on when the game
    /// saved: their own ship while aboard it, but the station's record while docked, and another of their ships
    /// when they are on that one instead. Null when the CO couldn't be located anywhere.
    ///
    /// <para>This is what makes the cost deduction independent of where the player happens to be standing. Equal
    /// to <see cref="SaveSourceRef.RegId"/> in the aboard case, where the deduction rides along in the ship record
    /// the inject already rewrites; anything else and the writer patches that second record too (see
    /// <c>SaveEdit.PatchPlayerBalance</c>).</para>
    /// </summary>
    public string? PlayerCoRegId { get; init; }

    /// <summary>The player's credit balance, resolved once at import from wherever
    /// <see cref="PlayerCoRegId"/> says the CO lives — reading it can mean parsing a second (large) ship record,
    /// so it is not re-derived on demand. Null when there is no balance to deduct from, which is what disables
    /// the edit-cost option in the UI.</summary>
    public double? PlayerBalance { get; init; }

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
    /// crew and loot-spawner COs that have no item — for Phase 2's CO filtering. The union of this ship's record
    /// and, for the items it does not cover, the session record (see <see cref="RelocatedCoIds"/>).</summary>
    public required IReadOnlyDictionary<string, JsonNode> CosById { get; init; }

    /// <summary>The session record's entry name, so the writer can edit it. Null when the session record could not
    /// be read, which also means <see cref="RelocatedCoIds"/> is empty.</summary>
    public string? SessionEntryName { get; init; }

    /// <summary>The <c>strID</c>s whose CO was found in the session record rather than in this ship's, in the
    /// order the session record holds them. The inject writes these into the ship record, so the writer must
    /// delete them from the session record or the same <c>strID</c> would be defined twice and
    /// <c>dictCOSaves</c> would take whichever record loaded last. Empty in the usual case — the player standing
    /// on the ship being edited — and then nothing touches the session record at all.</summary>
    public IReadOnlyList<string> RelocatedCoIds { get; init; } = [];

    /// <summary>Each structural part's contained-cargo tree, keyed by its origin <c>strID</c> (the same keys as
    /// <see cref="Origins"/>). A fresh import attaches these to the placements directly; on <c>.oplan</c> reopen
    /// the throwaway import doc is discarded, so the app re-attaches from this map by matching
    /// <see cref="Placement.OriginStrID"/>. Drives the inventory viewer.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<CargoItem>> CargoByOrigin { get; init; } =
        new Dictionary<string, IReadOnlyList<CargoItem>>();
}
