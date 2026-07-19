# The `.oplan` file format

`.oplan` is Ostraplan's **native document format**: a single, human-readable JSON
file describing one ship design. It is small, diff-friendly, and safe to share.

This document is the reference for the on-disk shape. The authority is the
serializer, `Ostraplan.Core/OplanFile.cs` — if the two ever disagree, the code
wins.

## Design goals

- **Self-contained but asset-free.** An `.oplan` records a design by *referencing*
  game defs by their `strName`; it embeds **no** game data or art. Sprites,
  footprints, friendly names, and everything else are re-resolved from the local
  install (and any mods) when the file is opened. This is what keeps the format
  tiny and legal to share.
- **Forward-compatible.** Every object preserves unknown fields on round-trip (via
  `JsonExtensionData`), so a file written by a newer build survives being opened
  and re-saved by an older one. New optional fields are added *additively* without a
  format-version bump; the version bumps only for a breaking change.
- **Deterministic.** No cached analysis (rooms, rating, materials) is stored —
  those are recomputed from the parts on open, so a stored copy could only go stale.
  The grid has no stored dimensions either; the tile plane is unbounded and derived
  from the parts.

> **`.oplan` is not the export format.** Exporting a design produces a *spawnable
> data mod* in the game's own `data/ships` shape (with rooms and rating baked in) —
> a different, self-contained artifact. See
> [GAME-INTERNALS.md §17](GAME-INTERNALS.md#17-ship-serialization-templates-and-saves).
> An `.oplan` is the editable working document; a save-edit `.oplan` additionally
> *links back* to a save rather than embedding it (see [Save-edit designs](#save-edit-designs)).

## Top-level structure

A complete file, with every section populated:

```json
{
  "formatVersion": 1,
  "viewRot": 0,
  "game": {
    "versionAtSave": "0.15.1.6",
    "versionVerified": "0.15.1.6"
  },
  "mods": [
    { "name": "Ship's Water", "entry": "ShipsWater|edit" }
  ],
  "meta": {
    "name": "Vagabond+",
    "author": "",
    "notes": "",
    "created": "2026-07-06T00:00:00Z",
    "modified": "2026-07-19T12:00:00Z",
    "publicName": "Wayfarer",
    "make": "Prakis",
    "model": "Vagabond",
    "year": "2145",
    "designation": "II",
    "description": "A refitted hauler."
  },
  "source": {
    "saveName": "Cold Open",
    "regId": "J-P3HF"
  },
  "parts": [
    { "def": "ItmWall01", "x": 3, "y": 2, "rot": 0, "given": false },
    { "def": "ItmLocker01", "x": 5, "y": 4, "rot": 90, "given": true, "origin": "a1b2c3d4-…",
      "cargo": [
        { "def": "ItmFoodRation", "strId": "…", "authored": true, "x": 0, "y": 0, "rot": 0, "stack": 4, "isStack": true }
      ]
    }
  ],
  "zones": [
    {
      "name": "Cargo",
      "color": [0.2, 0.6, 0.9, 0.4],
      "tileConds": ["IsZoneStockpile"],
      "tiles": [[3, 2], [4, 2], [4, 3]]
    }
  ],
  "looseObjects": [
    { "def": "ItmWrench", "x": 6, "y": 4, "rot": 0, "qty": 1 }
  ],
  "links": [
    { "src": 0, "tgt": 1 }
  ],
  "dismissedAlerts": []
}
```

**Serialization notes**

- Written with `WriteIndented = true` (2-space indent) and
  `DefaultIgnoreCondition = WhenWritingNull`: a `null` field is **omitted**, but an
  empty **array** is written (`"zones": []`, `"links": []`, …). So a minimal
  from-scratch design still carries empty `mods` / `zones` / `looseObjects` /
  `links` / `dismissedAlerts` arrays, and omits `source` (null) and any per-part
  `origin` / `cargo` that is null.
- Property order follows the field order below (`formatVersion`, `viewRot`, `game`,
  `mods`, `meta`, `source`, `parts`, `zones`, `looseObjects`, `links`,
  `dismissedAlerts`).
- Rotations are one of `0`, `90`, `180`, `270`, normalized on load.

## Field reference

### Root

| Field | Type | Meaning |
|---|---|---|
| `formatVersion` | int | Current **1**. A file whose version is **greater** than the build supports is **refused** (not silently mis-read). |
| `viewRot` | int | The plan-view orientation (`Q`/`E` rotation, a 90° step) the design was last saved in, so it reopens the same way. Defaults to `0` (north-up). Additive since v1. |
| `game` | object | The game versions in play at save time (below). |
| `mods` | array | The design's dependency manifest (below). |
| `meta` | object | Name, author, notes, and the ship's in-game identity (below). |
| `source` | object / absent | Present **only** for a design imported from a save for editing (below). Absent for from-scratch, template, and layout-only designs. |
| `parts` | array | The whole design, in draw/overlap order (below). |
| `zones` | array | Painted crew/trade zones (below). Additive since v1. |
| `looseObjects` | array | Loose floor cargo (below). Additive since v1. |
| `links` | array | Device signal connections (below). Additive since v1. |
| `dismissedAlerts` | array of string | Problem-warning keys the user dismissed, so a dismissed warning stays dismissed across reopens. Additive since v1. |

Unknown fields at **every** level are preserved on round-trip.

### `game`

| Field | Type | Meaning |
|---|---|---|
| `versionAtSave` | string | The installed game version when the file was saved. |
| `versionVerified` | string | The game version Ostraplan's Law was proven against (`GameEnv.VerifiedGameVersion`) at save time. A mismatch on open is advisory only. |

### `mods`

An **ordered dependency manifest**: every non-core data source loaded when the file
was saved. It auto-loads nothing — it records what the design needs, and drives the
missing-mods check on open.

| Field | Type | Meaning |
|---|---|---|
| `name` | string | Friendly label (the mod's display name). |
| `entry` | string | The mod's `loading_order.json` form — a local folder name (optionally `\|edit`) or a Workshop path. |

### `meta`

| Field | Type | Meaning |
|---|---|---|
| `name` | string | The document name (defaults to `"Untitled ship"`). |
| `author` | string | Free text. |
| `notes` | string | Free text. |
| `created` / `modified` | UTC datetime | Timestamps; `modified` is stamped on every save. |
| `publicName` | string | The ship's in-game display name (transponder/comms/broker). Blank means the exporter falls back to the design name. |
| `make`, `model`, `year`, `designation`, `description` | string | The ship's in-game identity flavour, edited in the **Ship Info** dialog and used to pre-fill the export dialog. |

The identity fields (`publicName` … `description`) are additive since v1 and default
to `""`, so a design that never set them round-trips exactly as before.

### `parts`

The design itself, in draw order (array order is preserved). Each entry:

| Field | Type | Meaning |
|---|---|---|
| `def` | string | The placed def's `strName`. Resolved against the catalog on open. |
| `x`, `y` | int | Top-left tile of the (rotated) footprint, in document coordinates (unbounded, may be negative). |
| `rot` | int | `0` / `90` / `180` / `270`. |
| `given` | bool | Imported (pre-existing) structure, exempt from the placement-law scan until moved. `false` for parts you placed. |
| `origin` | string / absent | Save-edit only: the source save item's `strID`, used to write structural edits back to the right item. Absent otherwise. |
| `cargo` | array / absent | A full snapshot of this container's contents, present **only** when its cargo was edited in the inventory editor. Un-edited containers omit it and re-read their contents from the linked save on open. |

**Cargo snapshot node** (`cargo[]`, recursive via `children`):

| Field | Type | Meaning |
|---|---|---|
| `def` | string | The contained item's `strName`. |
| `strId` | string | The item's save/local id. |
| `authored` | bool | Whether the item was authored in Ostraplan (vs read from the save). |
| `slotted` | bool | In a named slot rather than the free inventory grid. |
| `slot` | string / absent | The slot name when `slotted`. |
| `x`, `y`, `rot` | int | Grid cell + rotation within the container. |
| `stack` | int | Stacked count (≥ 1). |
| `isStack` | bool | Whether this node is a stack head. |
| `children` | array / absent | Nested contents (a container inside a container). |

Friendly name and grid footprint are **not** stored on cargo nodes — they are
re-resolved from the def on load.

### `zones`

Painted crew/trade zones. Tiles are stored as document `[x, y]` **coordinate**
pairs (not flat indices), because the document plane is unbounded and can be
negative; they are projected to the game's flat indices only at export/save-edit
time.

| Field | Type | Meaning |
|---|---|---|
| `name` | string | Zone name (made unique per ship on export). |
| `color` | `[r, g, b, a]` | Four doubles in `0..1`. |
| `tileConds` | array of string | The zone's type conds (`IsZoneStockpile` = Haul, `IsZoneBarter`, `IsZoneForbid`, content `IsZoneTrigger`/`IsZoneSpawn`/…). A zone can carry several. |
| `categoryConds` | array of string / absent | An item filter (stockpile) or a `Trigger*` (content zone). |
| `personSpec`, `targetPSpec` | string / absent | Owner / target person-specs for content zones. |
| `triggerOnOwner` | bool | Content-zone flag. |
| `tiles` | array of `[x, y]` | The covered tiles, in document coordinates. |

### `looseObjects`

Loose floor cargo (items resting on a tile, not installed structure). Only the def
and pose are stored; sprite, footprint, and friendly name are re-resolved on load.
One per tile — a later duplicate at the same tile overwrites.

| Field | Type | Meaning |
|---|---|---|
| `def` | string | The item's `strName`. |
| `x`, `y`, `rot` | int | Tile pose in document coordinates. |
| `qty` | int | Stacked count (≥ 1). Absent or `0` in an older file means a single item. |

### `links`

Device signal connections (the game's `Electrical` wiring). Parts have no stable id
in the file, but `parts` array order is preserved, so each link is a directed pair
of **indices into `parts`**.

| Field | Type | Meaning |
|---|---|---|
| `src` | int | Index of the source (driving) part in `parts`. |
| `tgt` | int | Index of the target (driven) part in `parts`. |

A link whose either endpoint was dropped on load (a missing-mod part, below) is
skipped, so a stale index can never wire the wrong parts.

## Opening a file

- **Version gate.** A `formatVersion` higher than the build supports is a hard
  refuse with a clear message; it is never partially read.
- **Missing defs / mods.** Each part is resolved against the catalog by `def`. A
  part whose def is **not loaded** (typically a modded part whose mod isn't enabled)
  is **not placed** — it is collected and reported, and the design is held
  **read-only** until the mods are enabled, so nothing is silently dropped. Loose
  objects and link endpoints whose defs/parts are missing are likewise dropped
  rather than guessed. (In the app you can then enable the mods and reopen, or
  confirm the drop and continue.)
- **Everything else is rebuilt.** Zones, loose items, links, dismissed alerts, and
  any edited-container cargo snapshots are restored; rooms, rating, and materials
  are recomputed.

## Save-edit designs

A design imported from a save *for editing* (rather than as a layout copy) carries
two extra pieces so structural edits can be written back into the save without
disturbing anything else:

- the top-level **`source`** block — the save folder name (`saveName`) and the ship
  RegID (`regId`) — enough to re-locate the ship and rebuild the write-back context
  on reopen; and
- a per-part **`origin`** (the source item's `strID`) on every imported part.

The live per-item state (crew, cargo, wear, power/gas, ship name, world position) is
**not** embedded — it is re-read from the referenced save on reopen. So a save-edit
`.oplan` is faithful *as a layout* on its own, and reconstructs the live ship for
write-back only while its save is present. To keep a standalone, shareable ship with
no save dependency, **export** the design instead.

---

*See also: [usage.md](usage.md) (using Ostraplan) and
[GAME-INTERNALS.md](GAME-INTERNALS.md) (how the game stores ships, and the export
format).*
