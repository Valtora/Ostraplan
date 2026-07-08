# Changelog

All notable changes to Ostraplan. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/); versions are the app version
(`Help ▸ version`), which the built-in update check compares against GitHub
release tags.

Ostraplan validates ships by *porting* Ostranauts' own logic; the game version
each release was verified against is recorded in
[docs/GAME-INTERNALS.md](docs/GAME-INTERNALS.md) (currently **0.15.1.6**).

## [Unreleased]

### Added
- **Make Loose Item / Install item.** Right-click a placed fixture to uninstall it
  into its loose (packaged) form on the tile, or re-install a loose one — the two
  directions of the game's own install/uninstall jobs. Eligibility is data-driven,
  so only genuinely uninstallable fixtures qualify (raw hull, walls and the fixed
  airlock never do). The swap keeps tile, rotation and any cargo, is one undo step,
  and conserves an item's baked contents (a gas canister stays charged); an install
  that no longer fits is flagged in Problems rather than blocked. Placing *arbitrary*
  loose inventory (tools, food, consumables) remains a separate, not-yet-built flow.

## [0.6.0] — 2026-07-07 — first public release

The first public build of Ostraplan. Consolidates the full editing suite, the
complete validation Law, interop (export/import), live-ship save editing, and the
container/cargo viewer and editor — plus in-app bug reporting and an activity log.

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
- **Containers & cargo:** view any container's contents on the grid (right-click ▸
  **View contents**), drill into nesting, and add / remove / rearrange loose
  cargo — carried through Export and save write-back.
- **Report a Bug** (Help menu) opens a pre-filled GitHub issue with diagnostics; an
  on-disk **activity log** records your actions for troubleshooting; and the app
  **version** now shows in the title bar.

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

### Docs
- Public-facing README, a usage guide ([docs/usage.md](docs/usage.md)), and this
  changelog.
- SPEC reconciled with the code — notably the `.oplan` format
  ([SPEC §8.1](docs/SPEC.md)) and a new save-edit round-trip section
  ([SPEC §8.5](docs/SPEC.md)); dropped/again-planned items corrected.

### Known limitations
- **Ship Zones aren't drawn yet.** Any ship you import or export will lose or move
  its zones, so they need to be deleted and redrawn. This is under active
  development and will be addressed in an update over the coming weeks.

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
