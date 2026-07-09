# Changelog

All notable changes to Ostraplan. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/); versions are the app version
(`Help ▸ version`), which the built-in update check compares against GitHub
release tags.

Ostraplan validates ships by *porting* Ostranauts' own logic; the game version
each release was verified against is recorded in
[docs/GAME-INTERNALS.md](docs/GAME-INTERNALS.md) (currently **0.15.1.6**).

## [Unreleased]

## [0.10.0] — 2026-07-09 — operational-state build defaults

### Changed
- **Powered fixtures build in their operational (On) state, not Off.** Ostranauts
  installs most devices switched **off** (the state a ship's rating never counts and
  that a player must turn on after loading). Ostraplan already did this for RCS
  thrusters; it now does it for **every** device with a clean operational counterpart
  — coolers, heaters, scrubbers, chargers, alarms, sensors, reactors, weapons, plus
  furniture — so a design's rating reflects reality and an exported ship spawns with
  its systems working. Devices whose "on" state is genuinely ambiguous (a colour/alert
  alarm, a transponder, the fusion reactor's startup sequence, an open/closed vent) are
  left exactly as the game installs them.

## [0.9.0] — 2026-07-09 — ship zones, faithful cargo & one-click install

### Added
- **Optional one-click install.** Ostraplan can copy itself to a fixed per-user home
  (`%LOCALAPPDATA%\Programs\Ostraplan`) and create Desktop and Start Menu shortcuts, so
  you have one place to keep and launch it instead of hunting for the downloaded exe.
  It offers this once on first run and otherwise stays out of the way; you can trigger
  it any time from **Help ▾ ▸ Install Ostraplan / shortcuts**. No admin rights, nothing
  written outside your user profile, and deleting that folder uninstalls it. (This fixed
  home is also where a future built-in updater would drop new builds.)
- **Ship zones — drawn, editable, and preserved on round-trip.** Ostranauts'
  crew/trade zones (Haul, Barter, Forbid, and the content trigger/spawn zones) now
  survive import → export and import → save-edit instead of being dropped or
  silently relocated, and you can create and manage them in the planner. A new
  **Zones** panel (right inspector) lists them; **+ Add** makes one and arms it for
  painting; **click a zone to paint** its tiles with the same tools as parts
  (drag to add, **Ctrl**-drag to erase, **Shift**-drag a box, **double-click**
  fills an enclosed room), each stroke one undo step. **Edit** sets the name, type
  (Haul/Barter/Forbid as independent toggles, matching the in-game editor), target
  role, colour, and — under Advanced — content-zone fields (encounter triggers,
  owner/target person-specs). A **Zones** toolbar button (or **Z**) toggles the
  overlay. Zones persist in the `.oplan`, export into `data/ships` `aZones`, and are
  re-projected into the correct tiles whenever the grid grows on save write-back.
  *Why it matters:* dropping zones broke player storage/no-go setups and, on
  authored station/quest ships, the scripted encounters wired to trigger zones.

### Changed
- **The Problems list is now an expandable list with a "View" button.** Each problem
  collapses to its title (click to expand the detail), and issues with a location get
  a **View** button that pans and zooms the canvas straight to the offending tiles, so
  a flagged part is easy to find on a big ship.
- **The update check now interrupts on launch.** When a newer GitHub release
  exists, Ostraplan raises a modal on startup (**Download Latest Version**, which
  opens the release page, or **Not Now**) instead of only revealing the toolbar
  Update button quietly. The button still stays as a persistent reminder after
  you dismiss the modal, and the modal shows on every launch while a newer
  version is out, so a release is never missed. (Mirrors the same change in
  Ostrasort.)
- **The add-to-container quantity control is clearer and capacity-aware.** The quantity
  field no longer hides its own number behind a clear "×" button, and it now has −/+
  steppers and shows how many of the selected item still fit ("of N"). The value is
  clamped to what the container can actually hold, so you can't enter a quantity that
  would just be rejected, and the picker says "container full" when there's no room.

### Fixed
- **"Make Loose Item" now works on walls, floors, and conduits — and keeps their
  theme.** These are placed as themed skins (a Testudo wall, an Aero floor), and only
  the plain base part carries an uninstall recipe, so the loosen action was silently
  unavailable on any skinned wall/floor/conduit. It is now offered, and loosening a
  themed part yields the matching themed loose item (a Testudo loose wall, not a
  generic one), mirroring what the game drops when you uninstall it.
- **Nav-console modules and themed loose walls/floors now show up in the container
  add-picker.** When you added items to a container, the picker drew only from plain
  condowners and skipped cooverlay skins entirely — so a nav console offered none of
  its actual modules ("nothing inside them"), and floors/walls showed a single generic
  "Floor (Loose)" instead of every themed variant you can store in game. The add-picker
  universe now also includes cooverlay skins (resolved through their base), so nav
  modules (Controls, Flight Dynamics, Map, …) and the full set of themed loose
  walls/floors are offered. Each container still narrows the list to what it accepts.
- **Exported inventories now spawn exactly as authored — right contents, right counts,
  right stacks.** A design's authored cargo (items packed into storage racks, bays,
  weapons, and every other container) survives being spawned from an exported
  `data/ships` mod, at the quantities you set. A `data/ships` file loads as a *template*,
  and the game silently drops contained items that aren't carried the way a save carries
  them, refilling the container from its default loot instead — so filled racks and bays
  came back empty, and a weapon loaded with two stacks of five rounds came back with only
  a couple. Export now writes each contained item (and the modules it injects into a nav
  console) with the same per-instance data a save uses: a "keep me" marker that also
  suppresses the container's default loot (so a stocked weapon gets exactly the ammo you
  authored and nothing extra), plus stack data so a ×N stack rebuilds at the right count
  instead of collapsing. (The save-edit path already handled this and was unaffected.)
- **Fixtures on floor-storage items no longer false-flag as "already occupied".** An
  under-floor storage bin or rack (e.g. ItmRackUnder01, the floor bins) provides a
  walkable sealed-floor surface that the game lets you build on and reach across. Its
  tiles carry IsFixture, which the placement law's obstruction mask lists, so a rack
  placed on — or whose access tile fell on — such a floor was wrongly flagged. A sealed
  floor is now treated as a valid build/stand surface (a genuine obstruction still
  blocks), matching the game.

## [0.7.0] — 2026-07-08 — loose items & reliable symmetry

### Changed
- **Symmetry now previews every mirror.** With symmetry on, the placement ghost
  shows the cursor part *and* each of its mirror copies, green where the mirror
  will land and red (offending tiles tinted) where the placement law refuses it.
  Previously only the cursor part was previewed and a mirror that didn't fit was a
  silent no-op, which read as "symmetry only works most of the time" — especially on
  large ships, where mirrors more often land on structure that isn't symmetric yet.
  The mirror geometry (reflection + rotation) was also lifted out of the canvas into
  a pure, unit-tested unit, so it can't silently drift.
- **RCS thrusters are built in their ON state.** The game installs an RCS cluster
  Off (and its maneuver rating doesn't count an Off thruster), so a designed ship
  used to read maneuver "O" and you'd have to power each thruster by hand after
  loading in game. Ostraplan now builds the identical On variant, so a design shows
  a real maneuver grade and an exported ship's thrusters work on spawn. (Imported
  ships keep whatever state they were saved in.)

### Added
- **Make Loose Item / Install item.** Right-click a placed fixture to uninstall it
  into its loose (packaged) form on the tile, or re-install a loose one — the two
  directions of the game's own install/uninstall jobs. Eligibility is data-driven,
  so only genuinely uninstallable fixtures qualify (raw hull, walls and the fixed
  airlock never do). The swap keeps tile, rotation and any cargo, is one undo step,
  and conserves an item's baked contents (a gas canister stays charged); an install
  that no longer fits is flagged in Problems rather than blocked. Placing *arbitrary*
  loose inventory (tools, food, consumables) remains a separate, not-yet-built flow.

### Fixed
- **Exported ships now carry their real broker value.** Export baked each room's
  physical *volume* into `roomValue` instead of the game's parts-based room value, so
  a spawned design read as nearly worthless at a broker until the game recomputed it
  on full load. It now bakes the same parts value the game does (and that Ostraplan
  already shows in the inspector).

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
