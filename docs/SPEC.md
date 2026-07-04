# Ostraplan — Specification

**Version:** 0.1 (draft for review) · 2026-07-04
**Status:** Scope signed off (law / interop / mods / release). Awaiting final review of this document before implementation begins.
**Repo:** local git now; private remote `Valtora/Ostranauts-Ostraplan` at implementation kickoff; public flip planned after the Law milestone (§13).

---

## 1. Summary

Ostraplan is a Windows desktop application (WPF, .NET 10) for designing Ostranauts ships outside the game. Every buildable part appears in a searchable, categorised palette; parts are dragged onto a tile grid that mirrors the in-game grid exactly; the design is validated live with the game's own rules; finished designs are saved as shareable files, exported as spawnable data mods, or seeded from existing game ships and save games.

It is a sibling tool to Ostrasort: same stack, same "read the live install as the source of truth" philosophy, and it reuses Ostrasort's mod/load-order resolution code.

## 2. The Law

> **If you can build it in Ostraplan, you can build it in Ostranauts, and it will be a valid ship.**

- Enforcement is by **porting the game's actual validation logic** (identified and mapped from a decompile of `Assembly-CSharp.dll`, ~1.5–2.5k lines total), not by approximating it. Referencing the game DLL directly is a confirmed dead end: the relevant types are MonoBehaviours and the algorithms round-trip through the Unity engine (`Physics.RaycastAll` inside tile updates), so stubs would return silently wrong answers.
- **False positives** (Ostraplan allows, game refuses) are Law violations — highest-severity bugs. **False negatives** (Ostraplan refuses, game allows) are ordinary bugs.
- Known nuance: the game validates each placement against the ship's *current* state during construction, so in-game legality is order-dependent (floors → walls → fixtures). Ostraplan validates the *finished* layout, and additionally runs a **constructibility pass** (§6.6) that simulates a canonical build order and warns if it cannot find one.
- The Law is proven by a **parity test corpus**: the 214 core ship templates ship with the game's own computed room data and ratings baked in; the ported engine must reproduce them (§10).

## 3. Scope

### Signed-off scope decisions (2026-07-04)

| Decision | Choice |
|---|---|
| Validation depth at v1 | **Full law**: placement sockets + rooms/airtightness + room certification + Ship Rating, parity-tested before v1 |
| Interop | **All three**: export as spawnable local mod; import core/mod ship templates; import player ships from save games |
| Modded content | **Mod-aware from v1**: resolve `loading_order.json` exactly like the game; per-mod toggle in UI |
| Release | **Private first** (`Ostranauts-Ostraplan`), public flip after the Law milestone proves out in-game |

### Non-goals (v1)

- Power-network / gas-flow / thermal **simulation** (budget *summaries* from data are P4 QoL; simulating networks is not).
- Crew pathing, reachability, or gameplay simulation of any kind.
- Economy/pricing beyond a bill of materials and install-work totals.
- Multi-ship / docked-layout editing (single ship per document).
- Writing to `loading_order.json` — registration of exported mods stays with ModTools/Ostrasort (single-writer discipline; see [ostranauts-loading-order-fragile] memory / Ostrasort docs).
- Workshop publishing of designs (export produces a local mod; uploading remains the in-game flow).
- Non-Windows platforms.

## 4. Platform & tech

- `net10.0-windows`, WPF, `System.Text.Json`, xUnit. Single-file publish reusing the Ostrasort `publish.ps1` pattern (`-p:IncludeNativeLibrariesForSelfExtract=true` + published-exe self-test).
- **Read-only guarantee**: Ostraplan never modifies the game install, saves, or `loading_order.json`. Its only writes are its own documents, exports to user-chosen locations, and (explicit action) staging an exported mod folder into `Ostranauts_Data/Mods/<Name>/` — never the registration file.
- No network access, no telemetry.

## 5. Data pipeline

### 5.1 Sources (read at startup, refresh on demand)

| Data | Path | Used for |
|---|---|---|
| `data/installables/` | game install `StreamingAssets/data/` | The buildable catalog: entries with `strJobType=="install"`; tab via `strBuildType` (HULL/HVAC/POWR/SENS/CTRL/FURN/APPS/MISC); materials `aInputs`, tools `aToolCTsUse`; placed def via `strStartInstall` (~494 jobs, ~322 categorised) |
| `data/items/` | 〃 | Geometry + sockets per item (931 defs): `nCols`, `aSocketAdds/Reqs/Forbids`, `strImg`, sprite-sheet flags |
| `data/condowners/` | 〃 | Friendly names, starting conditions, install costs, stats (mass, etc.) |
| `data/rooms/` | 〃 | The 18 room-certification specs (`nMinTileSize`, `aReqs`, `aForbids`, `fValueModifier`, `bAllowVoid`, `nPriority`) |
| `data/conditions*`, `data/condtrigs/`, `data/loot/` | 〃 | The trigger/loot engine subset that socket masks and room specs are written in |
| `data/cooverlays/` | 〃 | Themed wall/floor skins over base items |
| `data/ships/` | 〃 | Template browser + parity corpus (214 files) |
| `data/colors.json`, `data/strings/` | 〃 | Damage tints (unused v1), tab display labels |
| `images/**` | game install `StreamingAssets/images/` | All sprites (~6,150 PNGs, 16 px/tile) |
| `loading_order.json` + mod folders | `Ostranauts_Data/Mods/` + Workshop paths | Effective-data resolution |

**P0 ground truth** (implemented in `Ostraplan.Core`, discovered against 0.15.1.6): `strStartInstall` names the placed *condowner* — resolved directly, or through a **cooverlay** whose `strCOBase` is the real one (the same fallback `DataHandler.LoadCO` applies; roughly half of the ~330 build-menu entries are cooverlay skins) — and that condowner's `strItemDef` names the geometry/socket item def. Tile-socket loots carry their condition payload in **`aCOs`** (`aLoots` nests further loots). State variants are themselves the menu entries: doors install as `…Open`, beds as `…Off`. One core record carries a stray `"MIS"` category — invisible in the game's own menu too, and excluded the same way.

**Docking ground truth** (investigated for the origin-marker/airlock feature): there is **no** rule tying an airlock to (0,0) — of 147 core templates with a docking port, zero place it at the origin, and the Babak carries two. The real requirement is **≥1 installed docksys** (`Ship.aDocksys` collects installed COs triggering `TIsDockSysInstalled`; none ⇒ the ship loads but can never hard-dock). The "primary" port (`Ship.PrimaryDockingPortID`, persisted as `strPrimaryDockingPortID`) is a runtime-cyclable selection defaulting to the first port. Exactly one docking part is player-buildable: `ItmDockSys03Closed` (HULL). Ostraplan therefore seeds new documents with it at the origin (movable) and shows a standing "no docking port" warning, rather than enforcing a positional rule the game doesn't have.

**The Primary Airlock convention**: `ItmDockSys02Closed` ("Primary Exterior Airlock", `strNameShort` literally "Primary Airlock") is the port every template ship carries — `IsIndestructable`, `IsShipSpecialItem`, and **no install job**, so players can neither build nor remove one; the buildable `DockSys03` is explicitly the *Secondary*. Ostraplan mirrors this: the primary resolves for documents but stays out of the palette (`Catalog.PrimaryDocksysDef`), every new/opened design owns exactly one, seeded at the origin and **locked** (no move/rotate/delete/duplicate — `ShipDocument.IsLocked`).

One positional rule **does** exist, and it is the important one: **no construction beyond an airlock's mating face**. `TileUtils.GetAirlockBounds` builds an envelope from every installed port's `DockA→DockB` arrow (condowner `mapPoints`, pixels around the item centre, +y up; DockA at the door, DockB outside the hull): on the arrow's dominant axis, the face line is the A–B midpoint, and everything beyond it is out of bounds — which is also why a blocked face can never mate with a station collar. `ProblemScan.TryGetFace` ports this exactly (verified: the port's face lands on its footprint edge). Detection ships in v0.1 as a Blocking problem; hard placement rejection joins the P1 `CheckFit` port. Note the docksys-detection subtlety: `TIsDockSysInstalled` requires **all** its conditions (`IsDockSys` + `IsInstalled`) — an any-match flags every installed part.

### 5.2 Resolution (must match the game)

- Install discovery: default Steam path → `settings.json` `strPathMods` override → manual folder picker (persisted).
- `loading_order.json`: top-level array; local folder names (optional `|edit` suffix), Workshop entries as absolute paths; parse tolerantly, never write.
- Override semantics: per `(dataType, strName)`, **later-loaded object replaces earlier, whole-object** (no merge); `aIgnorePatterns` removals honored; `core` first.
- Mod images overlay core images by relative path (**verify exact game behavior at P0**).
- Per-mod enable/disable toggle in the UI (a "vanilla planning" view); default = the game's actual load order.
- Implementation adapted from Ostrasort's `GameEnv` / `Mods` / `LoadOrderFile` (both repos are ours; copy-adapt with provenance comments rather than a shared package, at least until the code stabilises).

### 5.3 Version pinning

- The app records the game version it was **verified against** (rating cutoffs and other constants are hardcoded in `Assembly-CSharp.dll` and invisible to data diffing).
- Detect the installed game version at runtime (detection source pinned at P0 — candidates: `data/info`, main-menu string source) and show a warning banner on mismatch: *"Law verified against 0.x.y; your install is 0.x.z — validation may drift."*
- After each game patch: re-decompile (ilspycmd, ~7 s), re-check constants, re-run parity corpus, bump the verified version.

## 6. Validation engine (normative — ported from decompiled game code)

Citations are `Type.Method` in Assembly-CSharp; decompiled reference sources are regenerable on demand with `ilspycmd`.

### 6.1 Grid & tiles

- Row-major tile list, `index = row * nCols + col`, anchored at `vShipPos` (top-left, +y up) — `Ship.GetTileIndexAtWorldCoords`.
- The grid **grows dynamically** as parts are placed near the edge (mirror `TileUtils.PadTilemap`) and is **trimmed on export** (mirror `TileUtils.TrimAllSides`). Reproducing this lifecycle matters: void detection depends on the fill reaching the array edge.
- Each tile carries an accumulated condition multiset (`Tile.coProps` equivalent): a `Dictionary<string,double>` per tile.

### 6.2 Parts & footprints

- A **Part** (palette entry) = installable job + placed item def (`strStartInstall` → items + condowners entries).
- Footprint: width `W = nCols`, height `H = aSocketAdds.Count / W`.
- `aSocketAdds`: per footprint cell, a **Loot name** expanding to the conditions that cell contributes to its tile (e.g. wall → `IsObstruction`, `IsWall`; floor → `IsFloorSealed`…).
- Derived tile flags: `IsWall` → wall, `IsPortal` → door, `IsObstruction` → impassable, `IsFloorSealed` → sealed.

### 6.3 Placement check (`Item.CheckFit`)

For a candidate placement (part, anchor, rotation):

1. `aSocketReqs` / `aSocketForbids` are per-cell masks over the **(W+2)×(H+2) ring grid** (footprint plus a 1-tile border). Each cell names a Loot (or `"Blank"` = unconstrained).
2. Cell test = transient CondTrigger against that tile's accumulated conditions: **every** req condition present with count > 0, **no** forbid condition present.
3. A cell that falls **off the ship grid** passes only if it has no reqs — this is how "must attach to structure / needs floor beneath" is encoded. (With dynamic padding, "off-grid" effectively means "empty space".)
4. Rotation: 90° steps only; rotate the req/forbid ring masks and adds mask (`TileUtils.RotateTilesCW`) and swap W/H. Sprite-sheet items (walls/floors) do not rotate.
5. Snap parity: odd-dimension items centre on a tile, even-dimension on an edge (`Ship.UpdateTiles` rounding).
6. In-game-only predicates **excluded by design**: crew proximity/LOS, docked-ship `WouldConnectShips`, zone restrictions (station building).

### 6.4 Tile-condition accumulation (replaces `Ship.UpdateTiles`)

On place/remove, add/subtract the part's per-cell `aSocketAdds` conditions on the covered tiles. **Every** overlapping installed part contributes. Note: state variants (door Open vs Closed) are *different item defs with different adds* — Ostraplan places the `strStartInstall` def, exactly as the game's installer does.

### 6.5 Rooms & airtightness (`Ship.CreateRooms`)

- BFS flood fill over non-wall tiles, **4-connectivity** (N/W/E/S). Walls terminate expansion and record room adjacency. Door (portal) tiles are boundaries, assigned afterwards to the preferred non-void adjacent room via the door's `RoomA`/`RoomB` anchors.
- A room is **Void** if any member tile lacks `IsFloorSealed`, or the fill reaches the edge of the (padded) tile array (also marks it Outside).
- Volume = `0.256 m³ × tileCount` (hardcoded constant — version-pinned).
- Re-run debounced after any mutation involving a room-relevant part (the game gates on `IsCheckRoom`; Ostraplan simply debounces all mutations).

### 6.6 Constructibility pass

Optional analysis (default **on, warn-only** — it does not block saving/export): simulate placing the design in a canonical order (floors → walls/doors → everything else in palette order) running `CheckFit` incrementally; if some part never becomes placeable, warn and name it. Rationale: final-state validation can, in rare corners, accept layouts no build order reaches; this pass closes most of that gap without claiming to decide the general problem.

### 6.7 Room certification (`Room.CreateRoomSpecs` / `RoomSpec.Matches`)

Specs sorted by `nPriority` descending; **first match wins**, else `Blank`. A spec matches iff: `room.Void == bAllowVoid`; tile count within `[nMinTileSize, nMaxTileSize]` (−1 = unbounded); no member part fires any `aForbids` trigger; every `aReqs` trigger satisfied **with multiplicity** (required count consumed by matching parts' stack counts); floor-grate members ignored; only installed parts count.

### 6.8 Ship Rating (`Ship.CalculateRating`)

Six slots, displayed as slots 1–5 joined with `-`:

| Slot | Meaning | Rule (verified against current build; version-pinned) |
|---|---|---|
| 0 | Epoch | Timestamp at rating time (export: current epoch) |
| 1 | Condition A–E | Mean of `1 − damageRate` over installed parts. Pristine planner ships ⇒ **A** by construction. Cutoffs: ≤0.5 E, ≤0.8 D, ≤0.95 C, ≤0.99 B, else A |
| 2 | Room count | Number of rooms whose matched spec ≠ `Blank` |
| 3 | Maneuver | `mass / RCS-thruster count`: 0 RCS → `O`; <300 A, <500 B, <750 C, <1500 D, else E. Needs the game's mass accounting (port at P2; planner ships have empty containers, so mass = Σ installed part masses — **pin exact semantics from `GetTotalMass` at P2**) |
| 4 | Size class | Grid area `nCols×nRows`: <250 Small, <900 Medium, <1600 Lunamax, <2300 Ceresmax, <3000 Titanmax, <3700 Very Large, else Ultra Large |
| 5 | Unused | Pass-through; blank on export |

### 6.9 Law report

A dockable panel listing, live: uncertifiable rooms **with reasons** (too small / missing required items / forbidden item present, naming them); void/airtightness breaches with **leak tracing** (highlight the unsealed tiles or the escape path to open space — a genuine QoL win over hunting leaks in-game); constructibility warnings; the full rating breakdown with per-slot explanations.

## 7. UX

```
┌────────────┬──────────────────────────────┬─────────────┐
│ Palette    │ Canvas (zoom/pan)            │ Inspector   │
│ [search]   │  · 16px sprites, autotile    │ · selection │
│ HULL HVAC  │  · drag-drop, ghost green/   │ · ship stats│
│ POWR SENS  │    red with per-cell reasons │ · rating    │
│ CTRL FURN  │  · overlays: rooms / seals   │─────────────│
│ APPS MISC  │                              │ Law report  │
└────────────┴──────────────────────────────┴─────────────┘
```

- **Palette**: the 8 game tabs + "All"; incremental search over friendly name (condowners) and internal `strName`; sprite thumbnails; drag onto canvas or click-to-arm placement cursor. Modded parts appear inline with a small origin badge (mod name on hover).
- **Canvas**: place LMB (repeat-place while armed); rotate **`R`** (`Shift+R` reverse) — deliberately the same key as the game's build mode, and the general rule: **where the game has an equivalent binding, Ostraplan uses it**; cancel `Esc`; delete `Del`/eraser mode; move by dragging a selection; box-select; copy/paste of selections. Ghost preview is green/red with failing cells highlighted and a tooltip naming the unmet requirement. Pan: Space-drag/MMB; zoom: wheel, integer pixel multiples (1×–8×, NearestNeighbor — crisp pixel art). Overlays: room fill tinted per room with spec label; seal overlay; advanced socket-debug overlay.
- **Inspector**: selected part (friendly + internal name, category, footprint, materials, install work, notable conditions); ship stats (dimensions, tile count, mass, room list, live rating string).
- **Out-of-bounds overlay** (shipped v0.1): the areas beyond every docking port's mating face — the `GetAirlockBounds` envelope — render as red hazard stripes, screen-fixed scale.
- **View rotation** (shipped v0.1): `Q`/`E` rotate the plan view in 90° steps, matching the in-game camera; all input (paint, select, pan, zoom-at-cursor) is rotation-aware.
- **Problems** (shipped v0.1, ahead of the law slices): red/yellow count badges pinned to the canvas's top-right (blocking vs warning) plus a PROBLEMS section atop the inspector listing each issue with details on hover. v0.1 checks: *no docking port*, *construction beyond an airlock face*. Per-placement socket legality (P1) and room/airtightness/certification checks (P2) append to the same list as they land.
- **Undo/redo**: unbounded command stack (place/remove/move/rotate/theme), drag operations coalesced; `Ctrl+Z`/`Ctrl+Y`.
- **Documents**: New / Open / Save / Save As / Recent; autosave `.bak` alongside the document; **template browser** for "start from a Vagabond" (core + modded ships, rendered previews).
- Wall/floor **theme picker** applies cooverlay skins (P4; base skin at P0).

## 8. File formats

### 8.1 Native document — `.oplan`

Versioned JSON, single file, shareable. Contents: `formatVersion`; dependency block (verified game version + ordered mod list with Workshop IDs where known); grid dims; parts array (`strName`, x, y, rotation, theme); document meta (name, author, notes, timestamps); optional cached analysis (rooms/rating) for fast preview. Unknown fields are preserved on round-trip (forward compatibility). Opening a document with missing mods lists them and renders placeholders rather than failing.

### 8.2 Export — spawnable local mod

Produces a standard mod folder: `mod_info.json` + `data/ships/<DesignName>.json` containing `nRows`/`nCols`, `vShipPos`, `aItems` (fresh instance UUIDs), **precomputed `aRooms` and `aRating`** (the game trusts these on shallow load — broker/registry display is correct immediately — then recomputes on full load, which is our end-to-end check), minimal `shipCO` stub, `origin: "$TEMPLATE"`. Written to a user-chosen folder, or staged directly into `Ostranauts_Data/Mods/<Name>/` on explicit request. **Registration in `loading_order.json` is deliberately left to ModTools/Ostrasort** (that file's write ritual stays single-owner); the export dialog says exactly that.

### 8.3 Import

- **Templates**: any `data/ships/*.json` from core or loaded mods.
- **Save games**: pick a save zip → `ships/<RegID>.json` → strip runtime state (`aCOs` → stub, wear/damage reset, `origin` → `$TEMPLATE`) with an explicit "layout only — crew/damage/cargo are discarded" confirmation.
- Unknown item references import as placeholder tiles (listed in the Law report) instead of failing.

### 8.4 PNG snapshot

Render the current canvas (chosen zoom, optional overlays) to PNG for sharing.

## 9. Architecture

```
Ostraplan.sln
  src/Ostraplan.Core/        # no UI dependencies
    DataIndex/               # effective-data resolution (adapted from Ostrasort GameEnv/Mods/LoadOrderFile)
    Engine/                  # sockets+CheckFit, tile accumulator, rooms, specs, rating, constructibility
    Model/                   # ShipDocument, Part, command stack (undo/redo)
    Files/                   # .oplan, ship-mod export, template/save import
  src/Ostraplan.App/         # WPF: shell, palette, canvas renderer, inspector, law report
  tests/Ostraplan.Tests/     # parity corpus + engine unit tests
  publish.ps1                # single-file publish + self-test (Ostrasort pattern)
```

- **Renderer**: layered `DrawingVisual`s (floor / walls / items / ghost / overlays), dirty-region redraw, `SpriteIndex` with lazy-loaded, decoded-once bitmaps (~6k PNGs on disk; only used ones decoded).
- **Analysis threading**: rooms/certification/rating run debounced on a worker; UI stays responsive; results stamped with a document revision to discard stale runs.
- **Wall/floor autotiling**: sprite-sheet items (`bHasSpriteSheet` + `ctSpriteSheet`) select a sheet region by neighbour connectivity; core wall sheet is 64×64 = 4×4 grid of 16 px tiles, consistent with a 4-bit N/E/S/W autotile — **verify exact index mapping from decompiled `Tile`/`TileUtils` sprite code at P0**.

## 10. Testing & the parity gate

1. **Parity corpus (the Law gate)**: for every core `data/ships` template — recomputed room partition equals stored `aRooms` (tile sets, `roomSpec`, `bVoid`), and rating slots 2/4 (plus 1 for pristine ships, 3 where mass is computable) equal stored `aRating`. Target 100%; any exclusion must be named and justified in the test file. Large station files (up to 35 MB) double as performance tests.
2. **Engine unit tests**: socket semantics per socket type; rotation of masks; snap parity; flood-fill edge cases (door corners, unsealed single tile, grid-edge escape); spec-matcher multiplicity/priority; constructibility ordering.
3. **In-game E2E (manual, user-driven)**: export → register via existing tooling → load in game → compare the game's logged rating and MODS-screen status against Ostraplan's prediction. Ostraplan ships a short checklist for this; test runs and result reporting are done by the user.
4. **Performance budgets**: `CheckFit` for ghost feedback < 16 ms; full re-analysis < 250 ms typical ships, < 2 s for station-scale imports; cold start (index + palette) < 3 s.

## 11. Roadmap

| Phase | Version | Delivers | Acceptance |
|---|---|---|---|
| **P0 — SHIPPED 2026-07-04** | 0.1 | Everything originally planned (mod-aware index, palette, canvas, sprites/autotile, `.oplan`, undo/redo, zoom/pan) **plus** a full editing suite and several law elements pulled forward — see the delivered-beyond-plan note below | Met — real parts render game-correctly and round-trip; 23 tests incl. two visual smoke renders |
| P1 Placement law | 0.2 | Full `Item.CheckFit` port on top of the already-shipped tile-condition accumulator: complete `CondTrigger` semantics (count multiplicity, nested `aTriggers`, `bAND` — the current presence-only subset must remain autotile-compatible), ring-grid reqs/forbids over (W+2)×(H+2), the off-grid rule, snap parity; live green/red ghost with per-cell failure reasons; **hard placement rejection on every path** (click, paint, box/hollow fill, symmetry mirrors, moves, rotations) including the airlock envelope, which today only warns; `ProblemScan` gains per-placement legality entries so designs that become illegal after an edit are listed; constructibility pass (canonical-order re-simulation, warn-only) | Cannot place anything the game would refuse (spot-checked in-game); envelope violations are unplaceable, not just flagged |
| P2 **Law milestone** | 0.3 | Rooms/airtightness flood fill, certification, rating, law report + leak tracing; parity suite against the 214 core templates | Parity corpus green |
| P3 Interop | 0.4 | Template browser/import; export-as-mod (precomputed `aRooms`/`aRating`); save-game import. The primary-airlock lock carries over for free (it keys on the def name) | An exported design spawns in-game with matching rating; a save ship imports cleanly |
| P4 QoL | 0.5+ | Bill of materials + install-work totals; cooverlay theme picker; PNG snapshot export; power/O₂ budget summaries; per-mod palette toggles; copy/paste; polish | — |
| v1.0 | 1.0 | Hardening, docs, in-game E2E checklist run; **public-flip decision point** | — |

**Delivered beyond plan in v0.1** (three QoL iterations on top of the foundation): drag-paint, Shift box fill and Ctrl+Shift hollow fill (strokes = one undo step); symmetry mode (M: V/H/Both, mirrored positions *and* rotations); right-click context menu (duplicate/rotate/delete, composite undo); smooth WASD panning; Q/E plan-view rotation with rotation-aware input throughout; grid visible at all zooms; gold origin marker; toolbar tooltips + F1 help modal; app icon. Most significantly, the **docking ground truth was researched and shipped early**: the Primary Airlock convention (one per ship, seeded at the origin, locked, outside the palette), the `GetAirlockBounds` construction envelope rendered as red hazard stripes, and the `ProblemScan` engine with canvas badges + inspector panel (checks so far: *no docking port*, *construction beyond an airlock*). The P1 prerequisite "tile-condition accumulator" also shipped in v0.1 — it is what drives faithful autotiling.

## 12. Risks & mitigations

| Risk | Mitigation |
|---|---|
| Tile-condition aggregation fidelity (every overlapping part contributes; state variants are distinct defs) | Port the accumulator exactly; parity corpus; socket-debug overlay for diagnosis |
| Order-dependent legality vs final-state validation | Constructibility pass (§6.6), warn-only |
| Void/edge semantics tied to grid pad/trim lifecycle | Mirror `PadTilemap`/`TrimAllSides` explicitly; flood-fill edge-case unit tests |
| Room-spec matching subtleties (priority, multiplicity, exclusions) | Direct port + per-spec unit tests + corpus |
| Version drift: rating cutoffs & constants hardcoded in the DLL | Version pinning + banner (§5.3); re-decompile check each patch (ilspycmd, seconds) |
| Mass accounting for maneuver grade | Port `GetTotalMass` semantics at P2; planner ships are empty/pristine, which removes most complexity |
| Sprite-sheet/autotile mapping wrong | Pin mapping from decompiled sprite code at P0; visual diff against in-game screenshots |

## 13. Distribution & IP hygiene

- Private repo `Valtora/Ostranauts-Ostraplan` during development.
- Written for a clean public flip later: engine code is **our own reimplementation of observed behavior** (no pasted decompiler output); each engine file carries a header noting the game version its behavior was verified against; **zero game assets** in the repo or releases — Ostraplan reads data and sprites from the player's own install at runtime, exactly like Ostrasort.
- Public-flip checklist: hygiene audit of engine files; README credits Blue Bottle Games and states the fan-tool/asset policy; repo rename or fresh public repo `Ostraplan` + exe releases, mirroring Ostrasort.

## 14. Open questions (tracked, none blocking)

1. ~~Sprite-sheet index mapping~~ **Resolved (P0):** mask bits N=8 / W=4 / E=2 / S=1 over `ctSpriteSheet`-triggered cardinal neighbours (`Item.SetSpriteSheetIndex`), the fixed 16-entry `Item.SpriteSheetIndices` table, and cell rows counted from the texture *bottom* (`GetMaterialSheet` UV offsets) — WPF flips the row. Ported in `Autotile.cs`.
2. ~~Mod image override semantics~~ **Resolved (P0):** the game prepends each loaded mod to its image search list (`DataHandler.LoadMod` → `aModPaths.Insert(0, …)`), so the latest-loaded mod wins; `DataIndex` mirrors this with later-source-wins path indexing.
3. ~~Game-version detection~~ **Resolved (P0):** `Application.version` sits as a plain ASCII string inside `Ostranauts_Data/globalgamemanagers` (same technique as Ostrasort's `GameEnv`).
4. Exact `GetTotalMass` semantics for the maneuver grade (P2).
5. One-click "deploy + register" integration (post-v1; likely by invoking Ostrasort `--headless`/ModTools rather than writing `loading_order.json` ourselves).
6. `.oplan` file association + icon (P4).

---

*Prepared from the 2026-07-04 feasibility investigation: live-data survey (items/installables/rooms/ships/images), ship-serialization analysis (templates + save games), and a full ilspycmd decompile of `Assembly-CSharp.dll` (placement, rooms, rating, data-loading paths mapped).*
