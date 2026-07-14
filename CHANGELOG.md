# Changelog

All notable changes to Ostraplan. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/); versions are the app version
(`Help ▸ version`), which the built-in update check compares against GitHub
release tags.

Ostraplan validates ships by *porting* Ostranauts' own logic; the game version
each release was verified against is recorded in
[docs/GAME-INTERNALS.md](docs/GAME-INTERNALS.md) (currently **0.15.1.6**).

## [Unreleased]

## [0.38.0] — 2026-07-14 — device signal connections

### Added
- **Wire devices together (signal connections).** A new **Wire mode** (View menu) lets you connect signalable
  devices the way the game does: click a device to arm it as the signal source, then click another to connect them
  (click a connected one again to disconnect); the source stays armed so you can wire it to several targets.
  Connectable devices (alarms, air pumps, sensors, lights, anything `IsSignalable`) ring in violet, the armed source
  rings brightly, and existing wires draw as violet lines with a dot at the driven end. Esc or right-click cancels.
  - **Validated like the game.** A connection is legal only between two distinct **installed** signalable parts; there
    is no distance rule (the game wires by id, not proximity), so that is the whole check.
  - **Saved and exported.** Connections persist in the `.oplan` and are baked into the exported ship's
    `Electrical` GPM (`inputConnections`/`outputConnections`), so the wiring spawns with the ship.
  - Gate/threshold logic is left to the in-game signal box; Ostraplan authors plain connections only.

## [0.37.0] — 2026-07-14 — tidier toolbar

### Changed
- **The toolbar is grouped into dropdown menus.** The File, Design and View groups each collapse into a single
  menu button (matching the existing Import/Help menus), so the toolbar is far less crowded. Undo/Redo and the
  headline **Ship Rating** stay as direct buttons. **File** holds New/Open/Save/Save As/Import/Export/Update Ship
  in Save; **Design** holds Ship Info/Ship Re-skin/Snapshot/Bill of Materials; **View** holds Fit, a Symmetry
  submenu (Off/Vertical/Horizontal/Both), and the Zones/Power/Mod-overrides toggles as checkmarked items (their
  state is also visible on the canvas). All keyboard shortcuts are unchanged (F, M, Z, P, Q/E, Ctrl+Z/Y, etc.).

## [0.36.0] — 2026-07-14 — remembered view orientation

### Added
- **The design remembers its orientation.** The plan-view rotation (Q/E) is saved in the `.oplan`, so a design
  reopens in the same orientation it was saved in. New/imported designs start north-up. Rotating the view now
  marks the design as having unsaved changes (the `*`), since the orientation is part of the saved file.

## [0.35.0] — 2026-07-14 — ship info editor, guaranteed starting ship, container rotate fix

### Added
- **"Ship Info" editor.** A new toolbar button (Design group) edits the ship's in-game identity — in-game name,
  make, model, year, designation and description. The values are **saved with the design** (in the `.oplan`) and
  **pre-fill the Export dialog**, so they no longer reset to blank every export. Edits made in the Export dialog
  flow back onto the saved identity, so the two never drift.
- **Guaranteed starting ship.** When exporting as a Shipbreaker starting ship you can now choose **"Only your ship
  offered (guaranteed start)"** instead of the weighted chance. This pins the start-event pool to your ship alone
  (dropping the vanilla salvage pods, and any other mod's start ships, from that roll), so a fresh Shipbreaker
  always starts with it. The old **"Weighted chance"** (one option alongside the vanilla pods) stays the default.

### Fixed
- **Rotating an item inside a container no longer squashes its sprite.** In the container Contents window, an item
  rotated with `R` swapped its grid footprint but drew its sprite upright, so a rotated item (e.g. a tall missile
  laid flat) rendered as a stretched sliver. The sprite now turns with the footprint and fills the cell correctly.

## [0.34.0] — 2026-07-13 — clearer symmetry axes

### Changed
- **The symmetry axes are more prominent.** Thicker and brighter dashed lines, so the mirror axis (and its centre
  marker) is easy to see against the ship instead of getting lost in it.

## [0.33.0] — 2026-07-13 — smooth panning on big ships

### Fixed
- **WASD / drag panning is smooth again on large ships.** The cached ship drawing was baked in screen space, so
  **every pan frame rebuilt the whole ship** (draw-order sort + per-tile autotile + every sprite) — on a big station
  that dropped the frame rate far enough that panning stuttered and chained key presses (e.g. W then A for a
  diagonal) arrived late and felt one-directional. The cache is now baked **pan-independently** and the live pan is
  applied as a transform, so a pan frame is a single cached blit, not a rebuild. The cache still rebuilds on a zoom
  or content change (both actually change the baked geometry), and view rotation was already a transform.
- **PowerViz panning is smooth too.** The conduit overlay was re-stroked as one dashed `DrawLine` per segment (plus
  a thick glow pass) every animation frame, so panning with it on stayed laggy on a big ship. The lit and unpowered
  segment sets are now baked into **frozen pan-independent geometries** (one `DrawGeometry` per layer, rebuilt only
  on a data or zoom change) and the flow animation is throttled to ~30 fps — the whole overlay is a handful of GPU
  strokes per frame.

## [0.32.0] — 2026-07-13 — power connectors + PowerViz

### Added
- **Ghost power connector points.** While placing (or with a powered part selected), the part shows its power
  connectors as labelled badges — a lightning glyph plus **IN** (blue, where it draws power) or **OUT** (green,
  where a source feeds the network) — so input vs output reads at a glance and the marker stands out against the
  conduit flow. Rotates with the part (and stays upright), so you can orient a device to meet a conduit before you
  place it. Ported from the game's build-cursor connector sprites; the plugs come from each device's
  `data/powerinfos` `aInputPts` (resolved through the condowner's `jsonPI`).
- **PowerViz — a conduit power overlay** (toolbar **Power: On/Off**, or **`P`**). A port of the game's
  `TileUtils.GetPoweredTiles`: power floods 4-cardinally from every installed generator/battery's output over
  `IsPowerPath` tiles (conduits and powered fixtures). **Lit runs** animate a cyan flow, **orphaned runs** (conduit
  not reaching any live source) draw dim dashed red, and a **wired device with no feed** gets an amber warning
  marker on its unpowered plug — so you can confirm at a glance that everything is hooked up. The toolbar tooltip
  reports how many device plugs are unconnected. This is connectivity *visualisation*, not a power-draw simulation
  (still a non-goal): it answers "is it wired and oriented right", using the game's own network graph.

## [0.31.0] — 2026-07-12 — wear slider + zoom out further

### Added
- **A Wear slider** on both **Export** and **Update Ship in Save**, so a design can enter the game worn rather
  than pristine. It bakes per-part damage exactly the way the game wears a ship sold from a broker kiosk
  (`Ship.DamageAllCOs` → `CondOwner.BreakIn`): each installed part takes `StatDamage = uniform(0, ceiling ×
  StatDamageMax)`, so condition varies part to part. The slider picks the target **average** condition (10%–100%);
  it defaults to **~88%**, the game's own kiosk ("Used") value, and no part is ever left below **10%** condition.
  - **Note on vanilla wear:** the game's kiosk ships average ~88% condition (parts spread ~75%–100%), a lighter
    knock than folklore suggests — drag the slider left for a grungier ship, or to 100% (or untick) for pristine.
  - Export bakes the damage as each part's `aCondOverrides` (`DMGStatus` stays New, so the game keeps exactly the
    baked wear); save-edit writes it as each installed part's `StatDamage` cond, the same way the game stores it.
    The baked Ship Rating "Condition" grade reflects the applied wear. On a save-edit, wear re-rolls the condition
    of **every** installed part (replacing existing damage) — leave it unticked to preserve each part's wear.

### Changed
- **You can zoom out much further.** The zoom range now goes down to 0.125× (2 px/tile) via new 0.5× / 0.25× /
  0.125× steps, so a whole station fits on screen; panning was already unrestricted. Max zoom is unchanged (8×).

## [0.30.2] — 2026-07-12 — transforming a pasted selection no longer drifts or breaks symmetry

### Fixed
- **A group rotation no longer drifts.** Rotating a multi-part selection with a non-square bounding box used to
  creep down and to the right a little more with each turn (round-half-up re-centring of the swapped W×H box), so
  repeated rotates walked the group across the grid. The re-centring now rounds symmetrically, so a rotate and its
  inverse cancel and four turns return exactly. Odd-parity bounds still take at most a one-time half-tile offset,
  but it no longer accumulates. ([#3](https://github.com/Valtora/Ostraplan/issues/3))
- **Rotating or moving a non-symmetric selection with mirror mode on no longer warps it.** The symmetry-preserving
  rotate and move only ever made sense for a genuine mirror-partner set. Applied to an arbitrary selection (most
  visibly a fresh paste sitting on one side of the axis) they mangled it: identical parts collapsed onto each other
  under rotation, and a drag reflected the far-side parts about the axis as if it were an "invisible mirror line".
  Both edits now first check that the selection is actually symmetric about the axis; if it is not, they fall back
  to a plain group rotate (about the selection's own centre) and a rigid move.
  ([#3](https://github.com/Valtora/Ostraplan/issues/3), [#4](https://github.com/Valtora/Ostraplan/issues/4))

## [0.30.1] — 2026-07-11 — plain PNG snapshot follows the editing orientation too

### Changed
- **The plain PNG snapshot now also renders in your current view orientation**, matching the Ship Rating room
  map (0.30.0). If you've rotated the plan view with Q/E, the exported image is rotated to match instead of
  always north-up.

## [0.30.0] — 2026-07-11 — Ship Rating image follows the editing orientation

### Changed
- **The Ship Rating room map (PNG and SVG) now renders in your current editing orientation.** If you've rotated
  the plan view with Q/E, the exported image is rotated to match, so it reads the same way as your editor
  instead of always snapping back to north-up. The ship art and room tints turn together (the raster canvas
  swaps its width/height at 90°/270°, and the SVG wraps them in a rotation group); the room labels stay upright
  and re-route to the nearest edge of the rotated image so they remain readable.

## [0.29.1] — 2026-07-11 — copy/paste keeps container contents

### Changed
- **Copy/paste and duplicate now carry a container's contents.** Copying (or duplicating) a stocked container
  and pasting it reproduces the container *with* its cargo — each pasted copy gets an independent deep-clone of
  the contents (fresh item ids, marked as authored), so it exports and writes back to a save as a real stocked
  container rather than an empty one. Non-container parts are unaffected.

## [0.29.0] — 2026-07-11 — compartment fill, brush/replace hotkeys, SVG room map, self-adopting updater

### Added
- **Fill a whole compartment.** Double-click enclosed ("compartmentalized") empty space to highlight the
  entire sealed compartment, then arm a part and press **Enter** to fill it in one undo step — each tile is
  placed only where the game's CheckFit allows and a same-def part isn't already there. Areas open to space
  can't be selected, so a fill can never leak into vacuum. Esc (or any edit) clears the highlight. Reuses the
  same room flood-fill that powers zone painting.
- **Hotkeys for the two commonest edits.** **Alt+click** is now an eyedropper — arm the part under the cursor
  as the brush (the "Use as brush" action, previously right-click only). **Ctrl+R** opens "Replace with…" for
  the current selection. Both still appear on the right-click menu, now with their shortcuts shown.
- **Save the room map as SVG.** The Ship Rating room map's "Save image…" dialog now offers **SVG** alongside
  PNG: the ship sprites are embedded once as a pixel-crisp layer and every annotation (room tints, leader
  lines, labels) is written as true vectors, so the diagram stays sharp at any zoom.

### Changed
- **The updater now self-adopts, so old shortcuts never open a stale build.** Running a freshly downloaded
  newer Ostraplan.exe replaces the installed copy at `%LOCALAPPDATA%\Programs\Ostraplan`, refreshes your
  Desktop/Start-Menu shortcuts, and relaunches from there — the same pattern Ostrasort uses. Because a design
  can hold unsaved edits, it never force-kills a running copy: if the installed exe is in use it asks you to
  close it and retry rather than risking your work. Dev/`bin` launches and same-location launches are skipped.

## [0.28.0] — 2026-07-11 — symmetry-aware selection, move, rotate, delete

### Added
- **Symmetry mode now applies to editing, not just placement.** With symmetry on (M: Vertical / Horizontal /
  Both), selecting a part also selects its mirror partner(s), so a click, box-select, or flood-select grabs the
  whole symmetric group (matched by def and exact mirrored position, the way a symmetry-mode build lays them
  down). Manipulating the group keeps it symmetric: dragging moves the grabbed side by the raw delta and the far
  side by the mirrored delta (a part straddling an axis is pinned along that axis), a group rotate turns one side
  and reflects it onto its partners (so a left/right pair stays a left/right pair instead of swinging into a
  top/bottom one), and deleting removes the whole group. The live drag preview mirrors too, and a symmetric move
  commits as a single undo step. Ctrl+click still toggles a part (and its partner) out of the selection. The
  geometry (`SymmetryOps`) is unit-tested.

## [0.27.0] — 2026-07-11 — filtered box-select, reactor build hints, maneuver numbers, constructibility fix

### Fixed
- **Reactor components no longer false-flag "needs an installed Fusion Reactor Core beneath".** The
  constructibility check (which verifies the game can build a design incrementally) simulated one fixed build
  order: a coarse rank (docking → floors → walls → fixtures) then document order. Every reactor part (field
  coils, core, and each component) is a "fixture", so their relative order was just the order they appear in
  the file — and a real ship lists the components long before the coils and core they seat on, so each
  component was checked before its core existed and flagged as un-buildable. The simulation now sweeps to a
  fixed point instead: each pass places every pending part that currently fits, repeating while progress is
  made, so it finds the coils → core → component order (or any valid order) whenever one exists. This is
  general, not reactor-specific: any fixture that mounts on another fixture authored later in the file is
  affected. Parts that genuinely fit no build order are still flagged, and the modded-part trust behaviour
  (a failing modded part is trusted into the sim so its dependents don't cascade-flag) is preserved.

### Added
- **Shift+drag box-select with filter chips.** With nothing armed, holding Shift and dragging always
  rubber-bands a selection rectangle, even when the drag starts on a part (previously that would grab and
  move it, and a fully-decked ship had no empty tile to start a box-select from). When the catch spans more
  than one layer, a chip menu opens at the cursor (Floors / Walls & doors / Fixtures / Conduits, with
  counts); untick chips to prune the selection live — e.g. keep the walls without the floors under them.
  Chips combine, unlike the right-click "Select only" single-layer filter. Ctrl+Shift+drag adds to the
  existing selection.
- **The Ship Rating panel now shows the maneuver numbers.** The caption spells out the actual figures behind
  the grade: total installed mass, total RCS thrust, the graded mass ÷ thrust metric with the A–E cutoffs,
  and the true thrust-to-mass ratio (per kg and per tonne). With no RCS installed it says so and still
  reports the ship's mass.

### Changed
- **Placement failures now explain the reactor build chain.** Arming the Fusion Reactor Core over bare floor
  used to fail with a raw condition name; it now says to build the Field Coils first (and that their centre
  tile must stay open to space). Reactor components likewise point at the missing installed core. When a pose
  fails several rules at once, these staged-build hints win over the generic "needs a sealed floor beneath"
  so the actionable tip isn't buried. A forbidden floor now reads "a floor is in the way here" instead of
  "blocked by IsFloor", and under-floor overlaps report "tile is already occupied".

## [0.25.0] — 2026-07-10 — dropped the pristine margin

### Changed
- **Removed the "Pristine bonus" margin from kiosk prices.** A full code trace confirmed a designed or exported
  ship can never have pristine parts (the game only rolls pristine on used and derelict ships, and installing
  always makes a fresh non-pristine part), so the margin implied an upside a built ship can't reach. The panel
  now shows a clean sell price, buy price, and build cost, with a short note that the final in-game price can
  vary by roughly ±15% (tanks topped past their default fill, cargo, or parts not in the design). The value
  maths still prices the gas each tank starts with and excludes loose cargo, exactly as the game does.

## [0.24.0] — 2026-07-10 — pristine wording made exact

### Changed
- **Sharpened the pristine bonus explanation after a full code trace.** Verified against the game code: pristine
  is added in only two places (the random roll on used and derelict ships when they first load, and kiosk stock
  items) and removed in only one (a part taking damage). Installing a part always creates a fresh non-pristine
  part regardless of the kit it came from. The caption now says the roll only happens on used and derelict
  ships, so a ship you build or buy new sits at the base price with no reachable pristine bonus.

## [0.23.0] — 2026-07-10 — pristine bonus label + airlock hint gate

### Changed
- **The kiosk "Margin" figure is now "Pristine bonus, up to" and sits right next to the sale price.** It reads
  as an add-on to the sell figure it qualifies (with a clear gap before the buy figure), and the clearer label
  says what the number actually is: the extra sale value if parts were pristine, which the game only rolls
  onto ships it spawns.
- **Airlocks are no longer suggested as value upgrades.** Like the reactor core and the bridge, a docking port
  is a deliberate, ship-defining placement (an airlock goes exactly where the ship mates), not a room
  furnishing, so "needs a docking port" is out of the value hints. The "Nearly certifies" diagnostics still
  show Airlock lines for a room actually being built as one.

## [0.22.0] — 2026-07-10 — kiosk panel polish + bridge hint gate

### Changed
- **Kiosk price panel polish.** The margin now sits directly after the sale price (smaller type), all dollar
  figures round to whole dollars, and the Ship Rating window opens larger by default (clamped to the screen).
- **Bridge rooms are no longer suggested as value upgrades.** Like the reactor, "add a nav station" technically
  qualifies for almost every room, and a ship wants one bridge, not a console per closet. The "Nearly
  certifies" diagnostics still show Bridge lines for rooms actually being built as a bridge.
- **Corrected the pristine story (checked the install code).** Buying a part fresh from a kiosk makes the
  *item* pristine, but installing consumes the item and spawns a brand new part, which is never pristine. The
  only way an installed part gets the 25% pristine markup is a small random roll (2.5%) the game makes on
  used, damaged, and derelict ships when they first load. The margin hint now says so, prior wording claimed
  hand-installed parts were pristine, which was wrong.

## [0.21.0] — 2026-07-10 — kiosk prices + towing hint gate

### Changed
- **The Ship Rating value panel now shows kiosk prices, not abstract value.** "Estimated value / build cost /
  broker sell / broker buy" is replaced by "Sell to kiosk", "Buy from kiosk", and a "Margin" figure with a
  percentage. The margin is the honest uncertainty in the number: the game marks each pristine part up 25% (on
  its shell price, never its gas), and pristine on installed parts only comes from a small random roll on
  game-spawned ships. So the price shown is the base and the margin is the ceiling a lucky roll can reach.
- **Towing Room is only suggested for airlocks that can hold the brace.** The towing brace's own placement rule
  requires a docking-system tile beside it (it can only ever be built at a docking port) and the brace is a 7×2
  fixture, so the hint now appears only for rooms certified as Airlock with at least 7 tiles. It previously
  sprayed onto every uncertified room, since the Towing Room spec's only shape gate is "2+ sealed tiles".

## [0.20.0] — 2026-07-10 — value engine field-calibrated against live sales

### Fixed
- **The Pristine markup is gone from estimates and export bakes — spawned ships never have it.** The game
  applies its ×1.25 "Pristine" bonus per part only to a runtime condition that exactly two code paths grant:
  derelict break-in, and trader stock items. A ship spawned from an export never gets it, so Ostraplan's flat
  ×1.25 overshot real resale quotes by up to 25% and made exported ships buy high and sell low (the baked buy
  price carried the markup, the game's own recompute didn't). Verified against a real sale: the reported
  min-max build now estimates $2.14m sell vs its actual $2.3m in-game sale (was $2.65m); the remainder is the
  game's random break-in roll on used ships plus parts the live ship carries that the plan doesn't.
- **A part's value now includes the gas its def spawns with.** The game prices canister contents (mols ×
  molar mass × the data-driven price/kg) plus liquid D2O and solid He3 fuel: a full O2 RTA is ~$5,648 of
  oxygen on a $410 shell, which is why canister-heavy builds read low before. Gaseous He3 is worth $0 in the
  game's own math (its molar-mass table has no He3 entry); He3 pellets are priced.
- **Broker buy estimate corrected from 1.25× to the data's 1.2×.** Both factors are now read straight from
  the core ship brokers' conds (they buy at `DiscountBuy` 0.8×, sell at `DiscountSell` 1.2×; the
  "1.1 − break-in" haircut turns out to be derelict-only). The min-max build's buy estimate is now $3.21m
  against the observed "3m or so" (was $4.14m).

### Changed
- **Reactor rooms are no longer suggested.** "Add a reactor core" technically qualifies for every sealed room
  of 4+ tiles, which spammed both "Nearly certifies" and "Value opportunities" on every ship. A reactor is a
  ship-defining build (5×5 core, field coils, vacuum exposure), not a room furnishing, so hints never advise
  one; rooms that already contain a core still certify and report as Reactor rooms normally.

## [0.19.0] — 2026-07-10 — void-room value + opportunity Show buttons

### Fixed
- **Engines and exterior-mounted gear now count toward the broker value, matching the game.** Ostraplan valued
  void (unsealed / open-to-space) rooms at $0 on the assumption that only sealed compartments count — the game
  disagrees: neither `Room.CalculateRoomValue` nor `Ship.GetShipValue` filters void rooms, and 192 core
  templates bake real value into their void rooms (the AirRacer's unsealed engine space alone is worth $343k).
  Parts in unsealed areas are now valued at that room's modifier (×1.0, or ×1.05 for an exterior cargo space),
  which raises the estimate for any design with engines or exterior equipment. Also settles a Discord theory:
  there is no special ×3 for wall-attached items — the ×3 O2 bonus is one global flag over the whole sum, so a
  single added part merely *looks* tripled when the bonus is active.

### Changed
- **Every "Value opportunities" entry now has a Show button** that highlights exactly which room the hint is
  about on the canvas (same mechanism as the airtightness leak highlighting; one highlight at a time). Rooms
  come from the same flood-fill partition the game uses, so entries never overlap or double-count — a tank farm
  legitimately produces one Engineering Room entry per canister compartment.

## [0.18.0] — 2026-07-10 — value opportunities

### Added
- **"Value opportunities" in the Ship Rating report** — an optional, collapsed section at the bottom that shows,
  for every sealed room (including completely empty ones), the higher-value room specs its shape allows, exactly
  what to add or remove to get there, and the broker-sell gain on the room's current contents (for example, an
  empty 9-tile room plus one installed canister or battery becomes an Engineering Room at ×1.4 room value).
  Certified rooms get upgrade hints too (a Basic Quarters that is one storage bin and a chair away from Luxury
  Quarters), but only when the upgrade also outranks the current spec in certification priority — the game picks
  the highest-priority matching spec, so items added for a lower-priority spec would change nothing. The section
  also calls out the single biggest lever: when the ship has no working O2 supply, it shows what feeding an air
  pump from an installed O2 canister would add (the whole-ship ×3).

## [0.17.0] — 2026-07-10 — room membership & value law (Discord reports)

### Fixed
- **Wall-mounted items now count toward room certification and value, matching the game.** The game assigns a
  part to the room at its centre tile, but when that tile is a room-less wall tile it retries at the part's
  "use" point (decompiled `Tile.AddToRoom`) — that's how wall storage bins, sensors, antennas, coolers, and ship
  weapons participate in rooms. Ostraplan only used the centre tile, so a bin mounted on a south or east wall
  silently vanished from certification (the Discord "bins present but quarters won't certify" report) and from
  the room's broker value. Corpus certification parity improved from 2109/2148 to **2124/2148 rooms exact**
  (still 0 over-certifications).
- **The ×3 "O2 atmosphere" value bonus now requires a working O2 supply, not just a pump.** The game grants it
  only when an installed air pump has an installed O2 canister (RTA) with O2 in it at its gas-input tile;
  Ostraplan granted it for any placed air pump (the Discord "pump = valid O2 atmo?" report). One fed pump ×3s
  the value; extra pumps add nothing — that part was always game-correct. Exports now also bake the real
  `nO2PumpCount`, so a purchased design with a working O2 supply quotes the right price at the broker before its
  first full load, and "Update ship in save" refreshes the count for the edited layout.
- **"Update ship in save" now bakes the parts-based room value** (the same fix exports got in 0.7.0) instead of
  the physical room volume, so a shallow-load broker quote of an edited ship reads its real worth.
- **Report note:** an air pump embedded in the wall line contributes $0 to the ship's broker value *in the game
  too* (its room-membership fallback lands on its own wall tile) — Ostraplan matches; this is not a bug.

### Changed
- **"Nearly certifies" now tells you what's actually wrong, including blockers.** The law report used to show
  only the highest-priority spec missing items — which was almost always "Reactor room" (any ≥4-tile room is one
  reactor core short of it), while never mentioning that a *forbidden* item was parked in the room. Each
  uncertified room now lists its two closest specs ranked by how near they are, with concrete lines like
  "Basic Quarters: remove O2 Resident Tank Assembly ×2" or "Luxury Quarters: needs a chair · remove Ship Battery"
  — the exact answer to "why isn't my Luxury Quarters recognized?" (Quarters specs forbid gas canisters,
  installed RTAs, ship batteries, floor hatches, toilets, and reactor cores — in the game too.)

## [0.16.0] — 2026-07-10 — P.A.S.S. boarding spawners

### Fixed
- **Exported ships now spawn you at a proper boarding point instead of somewhere random.** A ship template
  carries hidden **spawn points** the game uses to place people: a **Boarding** point (where you appear when
  arriving by the P.A.S.S. ferry or a skywalk) and a **NotBoarding** point (where an NPC already assigned to the
  ship spawns). Ostraplan drops all system objects on import (loot spawners, fire, and these spawn points share
  the same `IsSystem` flag) and never re-created them, so every exported ship had none — arriving at your own
  Ostraplan ship dumped you at a fallback tile, frequently *outside* the hull (the "I skywalk to my ship and end
  up somewhere random on the map" reports). Export now bakes both automatically: the **Boarding** point on the
  interior tile nearest the primary airlock (the dock entry, where you'd expect to arrive), and the
  **NotBoarding** point deeper inside. No action needed beyond re-exporting an existing design to pick up the fix.
  (The **save-edit** path — "Update ship in save" — was never affected: it keeps the original ship's spawn points.
  And **nav-console modules** were and remain correctly populated — a separate, already-working mechanism.)

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
