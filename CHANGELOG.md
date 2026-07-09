# Changelog

All notable changes to Ostraplan. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/); versions are the app version
(`Help ▸ version`), which the built-in update check compares against GitHub
release tags.

Ostraplan validates ships by *porting* Ostranauts' own logic; the game version
each release was verified against is recorded in
[docs/GAME-INTERNALS.md](docs/GAME-INTERNALS.md) (currently **0.15.1.6**).

## [Unreleased]

## [0.15.0] — 2026-07-10 — flip a selection · reactor core fix · modded overrides

### Added
- **Modded parts can now break the placement rules (with a warning), instead of being silently blocked.** A new
  **"Mod overrides"** toolbar toggle (off by default, remembered): when on, a modded part may be placed where
  Ostraplan's rules say it doesn't fit — it lands and is flagged as a **warning** in Problems ("modded part may
  not fit — verify in-game"), rather than being hard-blocked. The reason: Ostraplan's Law is a port of the *base
  game's* logic, so it's authoritative for vanilla parts but only best-effort for modded ones (a mod can add its
  own conditions or code). **Core parts stay fully enforced.** The armed ghost shows amber (not red) when a
  modded part will place via the override, and any modded part flagged illegal — however it got there — is now a
  yellow warning rather than a red blocker, and is trusted into the build check so parts placed on it don't
  cascade-flag.
- **Flip a selection horizontally or vertically** (`H` / `Shift+H`, also on the right-click menu). Mirrors the
  selected parts about the selection's centre — `H` left↔right, `Shift+H` up↔down — with each part reflecting
  its position and snapping its rotation to a real 0/90/180/270 (so the result is always buildable; the game's
  ship format has no mirror field, only a rotation). Walls and floors auto-tile rather than turn, so they move
  but keep their orientation. One undo step, and (like a group rotate) an illegal landing is allowed but
  flagged, not blocked.

### Fixed
- **The fusion reactor core now builds as the real part instead of a hollow copy.** The palette was building
  `ItmFusionReactorCore01On` — a glow-state *item* the game ships with no condowner, so it carried an internal
  name, **0 mass, 0 value, and none of its `IsFusionReactorCore` conditions**. Placement still worked (its
  sockets are identical, which is why this hid), but the reactor counted as weightless in the maneuver rating,
  contributed nothing to room value or the bill of materials, and exported broken. `PreferPoweredState` now
  only swaps a device to an operational counterpart that is a real condowner, so the reactor core builds as the
  installable `…Off` form (417 kg, priced, correctly named). The other reactor parts (field coils, laser array,
  core/cryo pumps, pellet feeder, MHD generator) already swapped to real operational condowners and are
  unaffected. Rebuild any reactor design to pick up the correct mass/value.

### Fixed
- **A bought ship now docks at the station instead of stranding out in the system.** Exports now bake
  `aDockingPorts` (the installed docking-port item ids) and `strPrimaryDockingPortID`, which core ship
  templates carry but Ostraplan omitted. The game only rebuilds those from the ship's items on a *full*
  load; a broker ship is spawned and (on some paths) docked while still *shallow*-loaded, and a shallow
  ship reads its ports straight from the file. With the fields missing, a purchased Ostraplan ship exposed
  zero open docking ports (`Ship.GetOpenDockingPorts`), so the game could not mate it to the station and
  left it drifting at its `objSS` (hundreds of millions of km away), where it was also absent from the
  P.A.S.S. ferry list. Verified against decompiled `GUIShipBroker.OnPurchaseConfirm` / `CrewSim.DockShip`
  and the game's `Ship` load path. Re-export an existing design to pick up the fix.

## [0.14.1] — 2026-07-09 — internal test hardening

### Changed
- **Internal:** made the save-edit placement-law immunity test deterministic. It previously imported
  whatever ship was in your newest save game and asserted a placement outcome against it, so ordinary
  play could make it fail spuriously; it now runs on a synthetic ship. No app-facing change.

## [0.14.0] — 2026-07-09 — friendlier replacement-mod names

### Changed
- **Export dialog notes the Special Offer "$0" quirk.** In-game the ship-broker's Special Offer slot always
  lists at "$0" (the real price shows on the Confirm dialog) — it's hardcoded in the game, not a pricing
  error, and confirmed by decompile: the ship's baked value is correct (the Confirm price proves it). The
  dialog now says so and points you at a regular broker kiosk for a visible list price.

### Added
- **Replacement mods get a clearer name.** Exporting a design that replaces an existing ship now names the
  **mod** distinctly from the ship it overrides — defaulting to `"{replaced ship} - Replaced via Ostraplan"`
  instead of reusing the replaced ship's own name (which read confusingly, as if the mod *were* that ship).
  A new **Mod name** field in the export dialog shows this default and lets you rename the mod to whatever
  you like; it auto-follows the ship name for a normal (non-replacement) export, and stops auto-updating
  once you type your own. The mod's name is now fully separate from the ship's in-game identity.

## [0.13.0] — 2026-07-09 — in-game availability, ship identity & vanilla-ship replacement

### Added
- **Find and Replace All…** context menu action. Select one or more copies of the same part and swap
  every copy of it anywhere in the ship — not just the current selection — for a chosen compatible part,
  in one undo step. Uses the same compatibility rule as "Replace with…" (same render layer + footprint,
  containers excluded), so a bulk swap can't turn a floor into a fixture or a wall into a door. Locked
  matches are counted in the picker but skipped by the swap.
- **Replace a vanilla (or modded) ship.** The export dialog can now tick "Replace an existing ship" and
  pick any core or mod ship: the export takes over that ship's identity (`strName`), so — loaded after
  core — the game spawns your design in its place everywhere (brokers, derelicts, missions). Pairs with
  the import flow for the "retrofit an existing hull with installed parts mods" workflow: import a
  vanilla ship, edit it with modded parts, and export it back over the original. The picker pre-selects
  the ship you imported. Caveats shown in the dialog: structure only (the original's cargo/crew loadout
  isn't carried over), and it affects new spawns, not ships already in a save. A replacement keeps the
  vanilla varied-naming behaviour unless you set an explicit in-game name.
- **Get your ship in-game, from the export dialog.** Exporting a design can now make it directly
  obtainable in a playthrough — no more hand-editing `loot.json` (which players broke into CTDs and
  infinite-ship loops). Tick any of:
  - **Ship broker kiosks** (OKLG / BCER / BCRS / Venus / VORB) — the ship joins that station's normal
    broker stock at an editable weight (defaulting to the pool's average, so it shows up about as often
    as a stock ship). The whole effective pool is preserved, so ships from other mods survive.
  - **Special Offer** (the free-ship-when-you-own-nothing slot, per station variant) — pins the slot to
    your ship.
  - **Starting ship** — offers the ship as a weighted option in a fresh **Shipbreaker** career start
    (alongside the vanilla salvage pods), with an editable start station and mortgage (pre-filled from
    the broker buy estimate). Built on the game's own `CGEncShipSalvagePod*` chain, so it needs no other
    mod. Note: vanilla chargen has no true ship *picker*, so this is a weighted chance, not a guaranteed
    choice.
  The export writes the extra `data/loot`, `data/lifeevents` and `data/interactions` files itself; where
  another ship mod touches the same kiosk pool, Ostrasort's `--patch` merges them (the dialog says so).
- **Ship identity fields in the export dialog.** Set the ship's in-game **name** (its `publicName`) plus
  **make / model / year / designation / description** — the same flavor fields core ships and mods like
  Ithalan's Additional Ships carry. The in-game name is now kept sticky (see the fix below).
- **One-click register with Ostrasort.** When staging into the game's Mods folder, tick "Register with
  Ostrasort" and the export hands off to Ostrasort headlessly — it registers the mod in
  `loading_order.json` (`--apply`) and, if the export touched any kiosk loot, merges conflicts with other
  ship mods (`--patch`). Ostraplan finds Ostrasort automatically (or asks once and remembers the path),
  and still never writes `loading_order.json` itself. Resolves SPEC §14 open question #5.

### Fixed
- **Exported ships no longer spawn inside the sun.** The exported orbital position defaulted to Sol's
  exact `(0,0)` origin; the kiosk/Special-Offer/starting-ship spawn path (unlike template import) does not
  reposition a template, so the ship materialised in the centre of the star. It now carries a small
  nonzero position like every core template does.
- **A custom in-game ship name now sticks across spawns.** Export hardcoded `publicName` to `"$TEMPLATE"`,
  which makes the game re-roll a random name every spawn; a real name typed in the dialog is now written
  through and kept. (The registry `strRegID` / "callsign" is *not* settable from a data mod — the game
  always mints a fresh one on spawn — so the dialog doesn't pretend to control it.)

## [0.12.0] — 2026-07-09 — optional save backup

### Added
- **Optional backup when updating a ship in a save.** The "Update ship in save" dialog's
  in-place write now has a **Back up the original save first** checkbox (ticked by
  default). Untick it to write straight into the save without spawning a backup copy —
  handy when iterating on a ship so you don't accumulate a pile of backup saves. Ticked
  stays the safe default (with the confirmation and result messages adapting to the
  choice); a copy write still never touches the original.

## [0.11.0] — 2026-07-09 — ITEMS palette: loose cargo on ships

### Added
- **ITEMS palette tab — drop loose cargo onto ships.** A new **ITEMS** tab lists
  every loose item in the game (food, ammo, clothing, tools, books, brushes, scrap,
  personal effects — the whole loose universe). Arm one and click to drop it: onto a
  **floor tile** (it rests on the deck, one item per tile) or into a **container**
  under the cursor that accepts it (same fit rules as the inventory editor). A live
  green/red ghost shows whether the drop will land. **Right-click** a dropped item for
  its menu — **Change Quantity…** (stack a stackable item up to its per-item limit) and
  **Delete**; left-click selects it (details in the inspector), **Del** removes it.
  Loose items and their stack counts persist in the `.oplan`, spawn in the ship both when
  you **export a mod** and when you **update a ship in a save** (a stack becomes a proper
  stack head + members with a CO each), and appear in the PNG snapshot. *Why it matters:*
  designs can now be provisioned — a stocked galley, a loaded ammo locker, scattered
  salvage — not just built empty.

### Fixed
- **All textbook and toothbrush variants now appear** in the loose-item picker (10
  textbooks, 6 toothbrushes), not just one of each. These are metadata skins over a
  shared base item; the container add-picker already surfaces them since 0.9.0, and the
  new ITEMS tab lists the full set.

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
