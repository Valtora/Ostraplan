# Changelog

All notable changes to Ostraplan. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/); versions are the app version
(`Help ▸ version`), which the built-in update check compares against GitHub
release tags.

Ostraplan validates ships by *porting* Ostranauts' own logic; the game version
each release was verified against is recorded in
[docs/GAME-INTERNALS.md](docs/GAME-INTERNALS.md) (currently **0.15.1.6**).

## [0.5.0] — 2026-07-06 — first public release

The first public build. Consolidates the full editing suite, the complete
validation Law, interop (export/import), and live-ship save editing, plus the
public-release housekeeping below.

### Added
- **The full Law:** placement sockets (`Item.CheckFit`), room/airtightness
  flood-fill (`Ship.CreateRooms`), room certification (`RoomSpec.Matches`), and
  the six-slot **Ship Rating** (`Ship.CalculateRating`), all ported from the game
  and parity-tested against its own baked room/rating data.
- **Law report** with air-leak tracing, and a live **Problems** list.
- **Interop:** export a design as a spawnable local mod; import a core/modded ship
  template or your own ship from a save.
- **Edit your live ship:** import it with its identity and write structural edits
  back into a **copy** of the save — crew, cargo, world position and ship identity
  preserved, the original untouched.
- **Editing suite:** drag-paint, box/hollow fill, symmetry mirror, flood-select,
  "Replace with…", ship-wide wall/floor **re-skin**, group rotate, copy/paste, and
  unbounded undo/redo.
- **Bill of materials** (install-kit counts), **PNG snapshot**, light/dark
  **theming**, and a GitHub **update check**.

### Changed
- **Missing-mod designs now open read-only.** Opening an `.oplan` while a mod it
  depends on isn't loaded names the missing parts *and their mods*, shows a
  standing "MISSING MODS" warning, and blocks saving until the mods are enabled
  (verify with [Ostrasort](https://github.com/Valtora/Ostrasort)). Previously the
  missing parts were dropped from the view and a later save lost them for good —
  and building over where they belonged could silently break the ship.
- **Save-edit designs are clearly linked to their save.** The import dialog and
  docs now spell out that an `.oplan` from a save references the ship's live state
  (crew/cargo/wear) rather than embedding it, and that Export is the way to a
  save-independent, shareable ship.
- Version aligned to the roadmap (`0.5.0`), so the update check and Help window
  report correctly.

### Docs
- Public-facing README, a usage guide ([docs/usage.md](docs/usage.md)), and this
  changelog.
- SPEC reconciled with the code — notably the `.oplan` format
  ([SPEC §8.1](docs/SPEC.md)) and a new save-edit round-trip section
  ([SPEC §8.5](docs/SPEC.md)); dropped/again-planned items corrected.

## Development history (pre-public milestones)

These shipped internally on the road to the public release.

### 0.4 — Interop — 2026-07-05
Export as a spawnable mod (the game's `data/ships` shape with precomputed
rooms/rating), template import, and save-game import. Round-trip verified
(`doc → export → re-parse → rebuild` reproduces the same tiles, rooms and rating).

### 0.3 — The Law: rooms, certification & rating — 2026-07-05
Rooms/airtightness, room certification, and Ship Rating, reached from the Ship
Rating button and the law report. Parity: rooms 188/192, certification 2109/2148
rooms exact with zero over-certifications of a real compartment.

### 0.2 — Placement law — 2026-07-04
`Item.CheckFit` ported onto the tile-condition accumulator: ring-grid
reqs/forbids, the off-ship rule, mask rotation, and hard rejection at the single
placement choke point, plus the airlock construction envelope.

### 0.1 — Foundation — 2026-07-04
Mod-aware data index, the palette over the game's eight build tabs, the sprite
canvas with game-exact autotiling, drag-paint/box-fill/symmetry, undo/redo, zoom
and pan, the `.oplan` format, and the Primary Airlock convention with its
construction-envelope hazard overlay.
