# Ostraplan — Specification

**Version:** 0.6 · updated 2026-07-12
**Status:** P0 foundation + P1 placement law + **P2 Law milestone** (rooms/airtightness, certification, Ship Rating + law report) + **P3 Interop** (export as spawnable mod · template import · save-game import) shipped (2026-07-05). **P4 QoL** shipped 2026-07-05: flood-select, Replace, big-ship performance, PNG snapshot, the **cooverlay theme picker** and the **bill of materials**. Two backlog items were **dropped**: per-mod palette toggles (managing mods is out of Ostraplan's scope) and power/O₂ budgets (per-device draw and gas rates aren't in the data — an honest balance would need network simulation, a non-goal) (§11). **P5 Save-Edit Phases 1–2** shipped 2026-07-06: import your live ship for editing, and write the edited structure back into a **copy** of the save (crew/cargo/identity preserved, original untouched) — engine + "Update Ship in Save…" UI; only Phase 3 hardening + in-game E2E remain (§8.5, §11).
**Repo:** [`Valtora/Ostraplan`](https://github.com/Valtora/Ostraplan) — public, MIT-licensed (§13).

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
| Modded content | **Mod-aware from v1**: resolve `loading_order.json` exactly like the game (per-mod UI toggle later **dropped** — §11) |
| Release | **Private first** (`Valtora/Ostraplan`), public flip after the Law milestone proves out in-game |

### Non-goals (v1)

- Power-network / gas-flow / thermal **simulation**. (Static power/O₂ budget *summaries* were scoped as P4 QoL but **dropped**: the game authors no per-device power draw or gas throughput — consumers carry only `IsPowered`, and `StatGasMol*` are stored contents, not rates — so a generation-vs-draw balance would need to simulate the network, which this is not.) Power *connectivity visualisation* — which conduit runs are live and whether a device is wired — **is** in scope and shipped (PowerViz, §7); it reuses the game's own reachability graph and models no draw/generation balance.
- Crew pathing, reachability, or gameplay simulation of any kind.
- Economy/pricing beyond the bill of materials (part-kit counts, §7).
- Multi-ship / docked-layout editing (single ship per document).
- Writing to `loading_order.json` — registration of exported mods stays with ModTools/Ostrasort (single-writer discipline; see [ostranauts-loading-order-fragile] memory / Ostrasort docs).
- Workshop publishing of designs (export produces a local mod; uploading remains the in-game flow).
- Non-Windows platforms.

## 4. Platform & tech

- `net10.0-windows`, WPF, `System.Text.Json`, xUnit. Single-file publish reusing the Ostrasort `publish.ps1` pattern (`-p:IncludeNativeLibrariesForSelfExtract=true` + published-exe self-test).
- **Theming**: light/dark chrome via WPF's Fluent `ThemeMode` (applied app-level in `App.OnStartup`) plus a `ThemeManager` brush palette pushed into `Application.Resources` as `DynamicResource` keys — mirroring Ostrasort. A "Theme: System / Light / Dark" picker persists to settings (default System). The **ship canvas stays dark always** (sprites are drawn for dark space); only the chrome themes.
- **Read-only guarantee**: Ostraplan never modifies the game install, saves, or `loading_order.json`. Its only writes are its own documents, exports to user-chosen locations, and (explicit action) staging an exported mod folder into `Ostranauts_Data/Mods/<Name>/` — never the registration file.
- The only network access is an optional GitHub **releases/latest** update check (on launch, quietly, and on demand from the Help window — mirroring Ostrasort). No telemetry. The download itself stays manual (the prompt opens the release page), but Ostraplan is **self-adopting** (`Updater.cs`, mirroring Ostrasort): running a freshly downloaded newer exe replaces the installed copy at `%LOCALAPPDATA%\Programs\Ostraplan`, refreshes the existing Desktop/Start-Menu shortcuts (`SelfInstall.RefreshShortcuts`), and relaunches from there, so an old shortcut never opens a stale binary. Unlike Ostrasort it never force-kills a running copy (a design can hold unsaved edits) — a locked installed exe prompts a close-and-retry instead.

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
- ~~Per-mod enable/disable toggle in the UI (a "vanilla planning" view)~~ — **dropped** (managing mods is out of Ostraplan's scope; §11). Ostraplan always resolves the game's actual load order.
- Implementation adapted from Ostrasort's `GameEnv` / `Mods` / `LoadOrderFile` (both repos are ours; copy-adapt with provenance comments rather than a shared package, at least until the code stabilises).

### 5.3 Version pinning

- The app records the game version it was **verified against** (`GameEnv.VerifiedGameVersion`; rating cutoffs and other constants are hardcoded in `Assembly-CSharp.dll` and invisible to data diffing).
- The installed game version is detected at runtime and shown in the status bar (`Game 0.x.y`). Ostraplan does **not** compare it against the verified version (the version mismatch banner was removed in v0.42). The Law changes rarely, so the verified version is reviewed and updated manually per game patch rather than nagging on every version bump.
- After a game patch that touches the Law: re-decompile (ilspycmd), re-check constants, re-run the parity corpus, bump `VerifiedGameVersion`.

## 6. Validation engine (normative — ported from decompiled game code)

Citations are `Type.Method` in Assembly-CSharp; decompiled reference sources are regenerable on demand with `ilspycmd`. **The full reverse-engineering reference — every decompiled algorithm, the loot/condition vocabulary, the data-model gotchas, and what is ported / deferred / excluded — lives in [GAME-INTERNALS.md](GAME-INTERNALS.md).** This section is the normative summary; that file is the working detail.

### 6.1 Grid & tiles

- Row-major tile list, `index = row * nCols + col`, anchored at `vShipPos` (top-left, +y up) — `Ship.GetTileIndexAtWorldCoords`.
- The grid **grows dynamically** as parts are placed near the edge (mirror `TileUtils.PadTilemap`) and is **trimmed on export** (mirror `TileUtils.TrimAllSides`). Reproducing this lifecycle matters: void detection depends on the fill reaching the array edge.
- Each tile carries an accumulated condition multiset (`Tile.coProps` equivalent): a `Dictionary<string,double>` per tile.

### 6.2 Parts & footprints

- A **Part** (palette entry) = installable job + placed item def (`strStartInstall` → items + condowners entries).
- **Socket/placement footprint:** width `W = nCols`, height `H = aSocketAdds.Count / W` (`Item.SetData`). Drives CheckFit, tile accumulation, and the ghost/selection extent.
- **Visual sprite size is separate:** `vScale = round(texturePx / 16)` tiles (`Item.SetData`) — usually equal to the footprint, but the large fuel tanks are a **7×7 socket grid with a 3×3 sprite** (outer ring = abstracted sub-floor storage, `TILSubfloorAdds`; center = the tank, `TIL2DeckAdds`). Render the sprite at `vScale` centered on the footprint; **keep the footprint at the socket grid** (the game requires the full 7×7 floor pad — shrinking it is a Law false positive). See [GAME-INTERNALS §4](GAME-INTERNALS.md).
- `aSocketAdds`: per footprint cell, a **Loot name** expanding to the conditions that cell contributes to its tile (e.g. wall → `IsObstruction`, `IsWall`; floor → `IsFloorSealed`…).
- Derived tile flags: `IsWall` → wall, `IsPortal` → door, `IsObstruction` → impassable, `IsFloorSealed` → sealed, `IsSubTile` → sub-floor (walkable storage).

### 6.3 Placement check (`Item.CheckFit`) — **ported (P1)**

For a candidate placement (part, anchor, rotation). Full trace + worked examples in [GAME-INTERNALS §5](GAME-INTERNALS.md):

1. `aSocketReqs` / `aSocketForbids` are per-cell masks over the **(W+2)×(H+2) ring grid** (footprint plus a 1-tile border). Each cell names a Loot (or `"Blank"` = unconstrained).
2. Cell test is **presence-only**: CheckFit builds a throwaway AND-`CondTrigger` of the loots' expanded condition names, so only the presence path runs — **every** req condition present (count > 0), **no** forbid condition present. Count multiplicity, nested `aTriggers`, and `bAND=false` are *unreachable from placement* and deferred to §6.7 (room certification); autotile's presence-only trigger is left untouched.
3. A cell that falls **off the ship grid** passes only if it has no reqs — this is how "must attach to structure / needs floor beneath" is encoded. ("Off-grid" = a tile with no accumulated conditions.)
4. Rotation: 90° steps; rotate the req/forbid ring masks and adds mask and swap W/H — `GridMath.Rotate(_, W+2, H+2)` reproduces `TileUtils.RotateTilesCW` exactly (verified). Sprite-sheet items (walls/floors) do not rotate.
5. **Airlock envelope** is a hard bound: no ring cell may fall beyond any docking port's mating face (§7 / `ProblemScan.TryGetFace`). The game bounds `aDocksys[0]`; Ostraplan bounds **all ports, ring-inclusive** — provably no false positive.
6. **Self-exclusion:** re-validating an already-placed part first subtracts its own tile contribution (walls/fixtures add `IsObstruction` and forbid it on their own footprint) — `CheckFit.Check(…, self)`.
7. In-game-only predicates **excluded by design**: crew proximity/LOS, docked-ship `WouldConnectShips`, and station-*build* zone restrictions (whether you're allowed to build on a station-owned tile). Ship **zones as data** (`aZones`) are a separate concern and *are* modelled — see §6.10.

**Enforcement:** new placement is hard-blocked at the single `ShipCanvas.TryPlacePose` choke point (click/paint/box/hollow/symmetry); moves, rotations and duplicates into an illegal spot are *allowed but flagged* (grouped by reason, offending tiles hazard-tinted); the ghost shows green/red with per-cell failures + a status-bar reason.

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
| 1 | Condition A–E | Mean of `1 − damageRate` over installed parts. Pristine planner ships ⇒ **A** by construction; a **worn** export/inject (§6.12) grades to the applied wear. Cutoffs: ≤0.5 E, ≤0.8 D, ≤0.95 C, ≤0.99 B, else A |
| 2 | Room count | Number of rooms whose matched spec ≠ `Blank` |
| 3 | Maneuver | `mass / RCS-thruster count`: 0 RCS → `O`; <300 A, <500 B, <750 C, <1500 D, else E. Needs the game's mass accounting (port at P2; planner ships have empty containers, so mass = Σ installed part masses — **pin exact semantics from `GetTotalMass` at P2**) |
| 4 | Size class | Grid area `nCols×nRows`: <250 Small, <900 Medium, <1600 Lunamax, <2300 Ceresmax, <3000 Titanmax, <3700 Very Large, else Ultra Large |
| 5 | Unused | Pass-through; blank on export |

### 6.9 Law report

A dockable panel listing, live: uncertifiable rooms **with reasons** (too small / missing required items / forbidden item present, naming them); void/airtightness breaches with **leak tracing** (highlight the unsealed tiles or the escape path to open space — a genuine QoL win over hunting leaks in-game); constructibility warnings; the full rating breakdown with per-slot explanations.

### 6.10 Ship zones (`aZones`) — authored data, not validated

Zones are the game's painted crew/trade areas (a `JsonZone[]` on the ship): a bag of
tiles tagged with `aTileConds` (`IsZoneStockpile`=Haul, `IsZoneBarter`, `IsZoneForbid`,
and content `IsZoneTrigger`/`IsZoneSpawn`/…), plus a name, colour, `categoryConds`
(a `Trigger*` for a trigger zone / an item filter for a stockpile) and owner/target
person-specs. Unlike rooms — which the game **re-derives** by airtight flood-fill on
every load and therefore self-heal — zones are **trusted verbatim** (`Ship.SetZoneData`
only bounds-checks; the first out-of-range tile drops that zone *and every zone after
it*). So zones **do not self-heal**: this is the whole bug when a tool round-trips a ship.

- **Tile model.** `aTiles` are flat row-major indices `col + row·nCols` — the same space
  as `aRooms.aTiles`. Ostraplan holds a zone's tiles as **document coordinates** (like a
  `Placement`) and projects to flat indices **only at write time**, in whatever grid frame
  the parts use, dropping out-of-frame tiles so a stale index can't cascade-drop a zone.
  This makes reframing (a grown grid on save-edit) automatic and correct.
- **Round-trip.** Import parses `aZones` → document zones; **export** writes them into
  `data/ships` `aZones`; **save-edit** re-emits them re-projected into the injected grid
  (replacing the old verbatim copy that silently relocated them); the **`.oplan`**
  persists them (`zones`, tiles as `[x,y]` pairs). The transient `aOldTiles`/legacy `ranks`
  are never emitted; names are made unique per ship.
- **Authoring** (see §7): create/paint/edit/delete via the Zones panel + canvas overlay;
  the three player types plus Advanced content fields. Zones the user didn't author (a
  station's trigger zones) round-trip untouched. Model lives in `ShipZone`/`ZoneGeometry`
  (Core); it contributes **no** tile conditions, rooms or rating.

### 6.11 Loose items (`LooseObject`) — floor cargo, not structure

Loose items are cargo dropped straight onto a ship tile (food, ammo, clothing, tools,
books, a personal effect) — the game's own top-level `aItems` entries that aren't
installed structure. Like zones they are a **non-structural overlay**: they carry no tile
conditions and take no part in `CheckFit`, rooms, airtightness or rating; they only render
and export. Model lives in `LooseObject`/`LoosePlacement` (Core).

- **The law** (`LoosePlacement`). An item may rest on a **floor tile** (any of
  `IsFloor`/`IsFloorSealed`/`IsFloorFlex`), **one per tile**, or go into a **container**
  covering that tile that accepts it (`ContainerFilter` + `CargoEdit.MaxAddable`>0). This
  mirrors how the game stores loose cargo and blocks items floating in vacuum or clipped
  inside walls. The **ITEMS** palette tab is the whole loose universe (`Catalog.LooseItems`,
  renderable ones); arming one and clicking drops it (a container under the cursor wins,
  else the floor). Right-click a placed item for **Change Quantity** (a stackable item,
  clamped to its `StackLimit`) and **Delete**.
- **Quantity/stacking.** A floor item carries a `Quantity` (1..`StackLimit`). It exports as
  a single top-level item when 1, and as a **stack** when >1 — a top-level head plus
  `Quantity−1` members parented to it (pristine marker + `bForceLoad`) and a head CO whose
  `aStack` lists the members, the same shape `EmitContained` bakes for container cargo.
- **Round-trip.** Export bakes each as a **free-standing, parentless** top-level `aItems`
  entry at its tile (the loader keeps a top-level item unconditionally — no `bForceLoad`/
  marker gate, unlike parented cargo); the **`.oplan`** persists them (`looseObjects`,
  additive at format v1, so older builds preserve them via `Extra`). Container drops route
  through the existing cargo tree (`CargoEdit` + `SetCargoCommand`).

### 6.12 Wear (`WearModel`) — optional per-part damage

A faithful port of how the game wears a ship sold from a broker kiosk (`Ship.DamageAllCOs` → `CondOwner.BreakIn`),
so a design can be exported / injected in a used state rather than pristine. Each damageable installed part
(`CondOwner`) carries `StatDamageMax = M` (health pool, from the def) and accumulates `StatDamage ∈ [0, M]`; its
condition is `1 − StatDamage/M`, and the Ship Rating Condition slot (§6.8) is the mean of `clamp01(1 −
StatDamage/StatDamageMax)` over installed parts.

- **The game's kiosk pass** is `DamageAllCOs(0.33)`: per part `StatDamage = uniform(0, 0.33·M)`, then ×0.75 because
  a fresh part is `IsPristine` — so the effective ceiling is **0.2475** and the mean condition is **≈ 87.6%**
  (parts spread ~75%–100%, overall grade ~C). `IsSystem` and `StatDamageMax = 0` parts are never damaged. Damage
  is per-part, not per-tile.
- **Ostraplan generalises** the single 0.33 knob to a target **average condition** `C`: the uniform ceiling is
  `2·(1 − C)` (mean damage `1 − C`), giving vanilla at `C ≈ 0.876` and grungier ships as `C` drops. A hard floor
  keeps every part ≥ **10%** condition however heavy the target (the kiosk pass never approaches it; a custom
  setting could). A fixed seed makes a roll reproducible.
- **Export** (§8.2) bakes the damage as each top-level part's `aCondOverrides` (`StatDamage`), with `DMGStatus`
  left **New** so the game keeps exactly the baked wear (a New ship runs no break-in pass of its own). **Save-edit**
  (§8.5) writes it as each installed part's `StatDamage` cond — the way the game itself stores per-part wear — and
  re-rolls **every** installed part (kept + new), overriding existing damage; `DMGStatus` stays New + `bPrefill`.
  Both bake the resulting Condition grade into `aRating`. Off by default in the engine (`WearOptions.Enabled`); the
  UI arms it (on, at the vanilla value) — see §7.

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

- **Palette**: the 8 game tabs + "All", plus an **Items** tab for loose floor cargo (§6.11); incremental search over friendly name (condowners) and internal `strName`; sprite thumbnails; drag onto canvas or click-to-arm placement cursor. Modded parts appear inline with a small origin badge (mod name on hover).
- **Canvas**: place LMB (repeat-place while armed); rotate **`R`** (`Shift+R` reverse) — deliberately the same key as the game's build mode, and the general rule: **where the game has an equivalent binding, Ostraplan uses it**; **flip a selection `H`** (horizontal, left↔right) / `Shift+H` (vertical, up↔down); cancel `Esc`; delete `Del`/eraser mode; move by dragging a selection; box-select (`Shift+drag` box-selects even starting on a part, then filter chips at the cursor prune the catch by layer — e.g. walls without their floors); copy/paste of selections. **Use as brush** with **`Alt+click`** (eyedropper — arm the part under the cursor) and **Replace with…** with **`Ctrl+R`** (both also on the right-click menu). **Fill a compartment**: double-click enclosed empty space to highlight the whole sealed compartment, then arm a part and press **`Enter`** to fill it (one undo step, CheckFit-gated per tile, `Esc` to cancel — reuses the room flood-fill; open-to-space areas can't be selected, so a fill never leaks). Ghost preview is green/red with failing cells highlighted and a tooltip naming the unmet requirement. Pan: Space-drag/MMB (unrestricted); zoom: wheel, integer pixel multiples (0.125×–8×, i.e. 2–128 px/tile, NearestNeighbor — crisp pixel art; the sub-1× steps frame a whole station). Overlays: room fill tinted per room with spec label; seal overlay; advanced socket-debug overlay.
  - **Symmetry mode drives editing, not just placement** (`SymmetryOps`, unit-tested): with symmetry on (M: V/H/Both), selecting a part also selects its mirror partner(s) — matched by def + exact mirrored top-left, how a symmetry-mode build lays them down — so a click or box-select grabs the whole symmetric group. The symmetry-preserving edits engage **only when the selection is actually a mirror set** about the axis (`Symmetry.IsSymmetricSet`: every part's mirror pose is occupied by a selected part of the same def, on-axis parts self-match); an arbitrary selection that merely straddles the axis (most visibly a fresh paste on one side) is not a partner set and is manipulated as a plain group instead — else its parts would collapse onto each other under rotation or reflect about the axis under a drag. When it is a genuine set, manipulating it keeps it symmetric: a move applies the raw drag delta on the grabbed side and the mirrored delta on the far side (a part straddling an axis is pinned along it); a group rotate turns the canonical primary side about its own centre and reflects the result onto its partners (rather than spinning the combined bounds, which would swing a left/right pair into a top/bottom one); delete removes the whole group (it's all selected). Moves commit as one `SetPosesCommand`; the live drag preview mirrors too.
  - **Flip is a Law-safe selection mirror** (`GroupFlip`, unit-tested): the game's ship format carries no mirror/flip field (only a rotation) and build mode cannot mirror a chiral part, so a true visual mirror of one asymmetric part isn't buildable. Ostraplan instead reflects the whole *arrangement* about its bounding-box centre and remaps each part's rotation to its reflected 0/90/180/270 (the same reflection the symmetry mode uses — `rot' = 360 − rot` horizontally, `180 − rot` vertically), so every emitted pose is a real, buildable rotation. Sheet walls/floors move to their mirrored tile but keep rotation 0 (they auto-tile). One undo step; place-and-flag like a group rotate.
- **Inspector**: selected part (friendly + internal name, category, footprint, base value, materials, source); ship stats (dimensions, tile count, mass, room list, live rating string). A **STATS** block surfaces the raw game figures the game keeps hidden — **Mass** (kg), **Health** (`StatDamageMax`, the durability pool the game never shows as a number), install/dismantle/uninstall/repair **work**, power, volume, pressure, thrust, armor (only those present, friendly-labelled) — plus an **All game data (raw)** expander (every numeric `Stat*` cond verbatim) and a **Conditions (flags)** expander (every non-stat starting cond). All read `PartDef.StartingCondValues`/`StartingConds` already in memory (the Base Value source), so no extra loading; the panel scrolls when detail runs long (shipped v0.39).
- **Out-of-bounds overlay** (shipped v0.1): the areas beyond every docking port's mating face — the `GetAirlockBounds` envelope — render as red hazard stripes, screen-fixed scale.
- **View rotation** (shipped v0.1): `Q`/`E` rotate the plan view in 90° steps, matching the in-game camera; all input (paint, select, pan, zoom-at-cursor) is rotation-aware.
- **Problems** (shipped v0.1, ahead of the law slices): red/yellow count badges pinned to the canvas's top-right (blocking vs warning) plus a PROBLEMS section atop the inspector listing each issue with details on hover. v0.1 checks: *no docking port*, *construction beyond an airlock face*. Per-placement socket legality (P1) and room/airtightness/certification checks (P2) append to the same list as they land. Each problem with tiles gets a **View** button that pans/zooms to them. The **unsealed-compartment** (airtightness) warning is findable straight from the sidebar (v0.41): it carries its **leak points** as the problem's cells, so a **Show** button highlights them on the canvas and focuses the view (no need to open the Ship Rating report), and it is **dismissible** — a **Dismiss** button hides it (and drops it from the warning badge), a **Restore Alerts** button under the list brings dismissed warnings back, and dismissals **persist in the `.oplan`** (`ShipDocument.DismissedAlerts`, keyed by `Problem.DismissKey` so a dismissal survives edits; the mechanism generalises to other warnings).
- **Power visualisation** (shipped v0.32): two features over the game's own power model. (1) **Ghost connector points** — a powered part draws its connectors as labelled badges (a lightning glyph + **IN**/**OUT**) while armed (and when selected): **blue IN** input plugs (where it draws from the conduit, its `data/powerinfos` `aInputPts` resolved through the condowner `jsonPI`) and a **green OUT** `PowerOutput` feed (sources), rotated with the part (upright labels) so it can be oriented to meet a conduit before placing. Mirrors the game's build-cursor connector sprites. (2) **PowerViz** (`ShipCanvas.ShowPower`, toolbar **Power: On/Off** or **`P`**) — a port of `TileUtils.GetPoweredTiles` (`PowerNetwork.cs`): power floods 4-cardinally from every installed generator/battery's `PowerOutput` tile over `IsPowerPath` tiles; **lit** runs animate a cyan flow, **orphaned** runs draw dim dashed red, and a wired device whose plug isn't reached gets an **amber warning marker**. Computed off-thread in the debounced scan only while the overlay is on. This is connectivity *visualisation*, not the power-draw simulation that stays a non-goal (§3): no per-device amounts/tickers, just "is it wired to a live source and oriented right", from the game's own graph.
- **Device signal connections** (shipped v0.38): wire signalable devices together the way the game's rewire tool does, over the game's own `Electrical` GPM model (see [GAME-INTERNALS](GAME-INTERNALS.md) "Device signal connections"). A **Wire mode** (`ShipCanvas.WireMode`, View menu) drives on-canvas authoring: click a signalable installed device to arm it as the signal **source**, then click another to **connect** (or a connected one to **disconnect**); the source stays armed to wire it to several targets, Esc/right-click cancels. Connectable devices ring violet, the armed source rings brightly, and each link renders as a violet line from source to target with a dot at the driven end. The connection is **directional** (source drives target) and **id-based with no geometry** — validity (`DeviceLinks.CanConnect`) is just "two distinct installed `IsSignalable` parts", the whole rule the game enforces. Links persist in the `.oplan` as (source, target) part-index pairs (`OplanLink`, dangling pairs pruned) and export bakes each wired part's `inputConnections`/`outputConnections` into its `Electrical` GPM (`ShipExport.WireDeviceLinks`), so the wiring spawns with the ship. **Gate/threshold logic is out of scope** — that is the in-game signal box's job; Ostraplan authors plain connections only. Model in `DeviceLink`/`DeviceLinks` (Core); contributes no tile conditions, rooms or rating.
- **Modded overrides** (shipped v0.15): Ostraplan's Law is a port of the **core** game's logic (`PartDef.IsModded` = `Origin != "core"`), so it is authoritative for vanilla parts but only best-effort for modded ones (a mod can add its own conditions or BepInEx code). Two consequences: (1) a **modded** part flagged illegal is a **Warning**, not a Blocking problem ("modded part may not fit — verify in-game"), and it is trusted into the constructibility simulation so parts built on it don't cascade-flag; core failures stay Blocking. (2) A persisted **"Mod overrides"** toolbar toggle (`AppSettings.AllowModdedOverrides`, default off) lets the placement choke point (`ShipCanvas.TryPlacePose`) place a modded part that fails `CheckFit` instead of hard-blocking it — the armed ghost shows **amber** ("placing against the rules, flagged") rather than red. **Core parts are always hard-blocked** regardless of the toggle: the Law's "if it builds in Ostraplan it builds in Ostranauts" promise stays intact for vanilla.
- **Undo/redo**: unbounded command stack (place/remove/move/rotate/theme), drag operations coalesced; `Ctrl+Z`/`Ctrl+Y`.
- **Documents**: New / Open / Save / Save As / Recent; **template browser** for "start from a Vagabond" (core + modded ships, rendered previews). A design opened while a mod it depends on isn't loaded is held **read-only** — the missing parts and their mods are named on open, the chrome shows a standing "MISSING MODS" warning, and saving is blocked until the mods are enabled (verify with Ostrasort), so those parts can't be silently dropped and you can't build over where they belong. See §8.1.
- Wall/floor **re-skin** (shipped P4): the "Ship Re-skin…" toolbar button opens a two-column dialog to pick a wall skin and/or floor skin; every placed wall/floor re-skins ship-wide in one undo step (a bulk `ReplaceOps` swap over each skin's render-layer+footprint class). Sprites/names only — rooms, airtightness and rating are untouched. (Named "Ship Re-skin" so it isn't confused with the app's light/dark theme.)
- **Find and Replace All…** (context menu): select one or more copies of the exact same part, then swap *every* copy of it anywhere in the ship — not just the selection — for a chosen compatible part (same render layer + footprint, containers excluded, same rule as "Replace with…"). One undo step; locked matches are counted in the picker but skipped by the swap. Sits beside "Replace with…", which stays scoped to the current selection only.
- **Ship Info** (shipped v0.35): a "Ship Info…" toolbar button (Design group) opens a dialog editing the ship's in-game identity — in-game name (`publicName`), make, model, year, designation, description. These live on the design's `meta` (§8.1), so they **persist in the `.oplan`** and **pre-fill the export dialog** rather than being retyped every export; edits made in the export dialog flow back onto the saved identity. Metadata only: no effect on layout, rooms or rating.
- **Toolbar** (shipped; grouped into dropdown menus v0.37): to stay uncluttered the groups collapse into **File ▾** (New/Open/Save/Save As/Import ▸/Export/Update Ship in Save), **Design ▾** (Ship Info/Ship Re-skin/Snapshot/Bill of Materials) and **View ▾** (Fit, a Symmetry submenu, and the Zones/Power/Mod-override toggles as checkmarked items). Undo/Redo and the accent **Ship Rating** stay as direct buttons; the app theme picker and Help ▾ sit on the right, with an Update button that appears when a newer release exists. Toggle state that used to show on the button caption now reads from the menu checkmarks (and the on-canvas overlays). Each dropdown item shows its **keyboard shortcut** on the right (v0.40, `InputGestureText`), standard app-menu style, so the bindings are discoverable from the menus.
- **Keyboard shortcuts** (comprehensive as of v0.40): file/edit — `Ctrl+N/O/S`, `Ctrl+Shift+S` (Save As), `Ctrl+Z`/`Ctrl+Y` (+`Ctrl+Shift+Z` redo), `Ctrl+C/V/D`, `Ctrl+A` (select all), `Ctrl+R` (replace), `Del`; actions — `Ctrl+E` (export), `Ctrl+I` (Ship Info), `Ctrl+B` (Bill of Materials); view — `F` (fit), `+`/`−` (keyboard zoom), `Q`/`E` (rotate view), `W A S D` (pan), `M` (symmetry), `Z` (zones), `P` (power); build — `R`/`Shift+R` (rotate), `H`/`Shift+H` (flip), `Enter` (fill compartment), `Alt+click` (eyedropper), `Esc` (cancel), `F1` (help). Handled at the single `MainWindow.OnPreviewKeyDown` choke point (text boxes exempt).
- **Help / controls window** (shipped): a three-column table — Function · Keybinding · What it does — with headers and zebra striping, listing the full shortcut set above, plus the app version and a "Check for updates" button.
- **Bill of materials** (shipped P4): the "Materials…" toolbar button opens a report counting each buildable part's install kit — its own uninstalled form (install `aInputs`, 1:1 with the part), so the part count is the kit count. Grouped by build tab, scoped to the current selection when one is active else the whole ship, with a "Copy list" button. Non-buildable structure (raw hull, fixed systems, the primary airlock) is tallied apart.

## 8. File formats

### 8.1 Native document — `.oplan`

Versioned JSON, single file, shareable — the exact on-disk shape (`OplanFile`):

```json
{
  "formatVersion": 1,
  "game":   { "versionAtSave": "0.15.1.6", "versionVerified": "0.15.1.6" },
  "mods":   [ { "name": "Ship's Water", "entry": "ShipsWater|edit" } ],
  "meta":   { "name": "Vagabond+", "author": "", "notes": "",
              "publicName": "", "make": "", "model": "", "year": "", "designation": "", "description": "",
              "created": "2026-07-06T00:00:00Z", "modified": "2026-07-06T00:00:00Z" },
  "source": { "saveName": "Cold Open", "regId": "J-P3HF" },
  "parts":  [ { "def": "ItmWall01", "x": 3, "y": 2, "rot": 0, "given": false, "origin": null } ]
}
```

- **`formatVersion`** — current `1`. A file from a newer format is refused, not silently mis-read.
- **`game`** — `versionAtSave` (the install version when the file was saved) and `versionVerified` (the game version Ostraplan's Law was proven against, `GameEnv.VerifiedGameVersion`). A mismatch is advisory only.
- **`mods`** — an ordered **dependency manifest**: every non-core data source at save time, each a friendly name + its `loading_order` entry (local folder or Workshop path). It auto-loads nothing; it records what the design needs and drives the missing-mods check on open.
- **`meta`** — name, author, notes, the ship's in-game identity (`publicName`, `make`, `model`, `year`, `designation`, `description` — edited in the **Ship Info** dialog, §7, and used to pre-fill the export dialog; all default `""` and are additive since format v1), and UTC `created` / `modified` timestamps.
- **`source`** — present **only** for a design imported from a save *for editing*: the save folder name + ship RegID, enough to re-locate the ship and rebuild the write-back context on reopen (§8.5). Absent for from-scratch / template / layout designs.
- **`parts`** — the whole design, in draw/overlap order (array order is preserved on round-trip). Each part is the placed def (`strName`), its top-left tile `x` / `y` (of the rotated footprint), `rot` ∈ {0, 90, 180, 270}, `given` (imported structure, exempt from the placement-law scan until moved — §6.3), and `origin` (the source save item's `strID` for save-edit identity; `null` otherwise). Re-skins, replaces and door toggles are captured as a changed `def` — there is no separate "theme" field.
- **Unknown fields at every level are preserved** on round-trip (`JsonExtensionData`), so a newer file survives an older build.

**What is deliberately *not* stored** (and why it needn't be):

- **No grid dimensions** — the tile plane is unbounded and derived from the parts; it grows and trims dynamically (§6.1), so the parts alone fix the geometry.
- **No cached rooms/rating** — analysis is recomputed deterministically on open and on export, so a stored copy could only go stale.
- **No embedded save state** — see faithfulness below.

**Faithfulness.** For a **from-scratch, template, or layout-only save** design, an `.oplan` recreates the ship exactly: every part's def, pose, rotation and given-ness, in draw order, including the locked Primary Airlock. Reopening elsewhere needs the same mods enabled — they are listed in `mods`, and any part whose def is not loaded is reported on open with the design held **read-only** until the mods are enabled (verify with Ostrasort), so nothing is ever silently dropped. For a **save-edit** design the file captures the full editable layout plus each part's save-identity (`origin`) and the save reference (`source`); the **live per-item state — crew, cargo, wear, power/gas, ship name and world position — is not embedded, but re-read from the referenced save** on reopen. A save-edit `.oplan` is therefore faithful *as a layout* on its own, and reconstructs the live ship for write-back only while its save is present. **To keep a standalone, shareable ship detached from any save, Export it** (§8.2) — the exported mod is fully self-contained.

### 8.2 Export — spawnable local mod

Produces a standard mod folder: `mod_info.json` + `data/ships/<DesignName>.json` containing `nRows`/`nCols`, `vShipPos`, `aItems` (fresh instance UUIDs), **precomputed `aRooms` and `aRating`** (the game trusts these on shallow load — broker/registry display is correct immediately — then recomputes on full load, which is our end-to-end check), minimal `shipCO` stub, `origin: "$TEMPLATE"`. Written to a user-chosen folder, or staged directly into `Ostranauts_Data/Mods/<Name>/` on explicit request. A **Wear** slider (§6.12) can bake per-part damage into the ship (default on, at the vanilla ~88% used-ship condition) so it spawns worn; the damage rides on each part's `aCondOverrides` and the baked `aRating` Condition grade follows. **Registration in `loading_order.json` is deliberately left to ModTools/Ostrasort** (that file's write ritual stays single-owner); the export dialog says exactly that.

**Ship identity.** The dialog also sets the in-game `publicName` and the `make`/`model`/`year`/`designation`/`description` flavor fields. `publicName` matters: decompiled `Ship.InitShip` re-rolls a random `GetShipName()` on **every** spawn unless the on-disk value is a real string (not `null`/`""`/`"$TEMPLATE"`), so a blank export loses the ship's identity each spawn — export writes the typed name through. The blank-field fallback is context-aware (`ShipExport.ResolvePublicName`): a **new** ship takes the design name (a stable identity), a **replacement** (below) keeps `"$TEMPLATE"` so each spawned copy still gets its own generated name like the original template; a literal `"$TEMPLATE"` typed by the user is treated as blank. `strRegID` (the transponder "callsign") is **not** controllable from a data mod — `StarSystem.SpawnShip` overwrites it with a freshly-minted ID before `InitShip` runs, always (two live ships can't share a RegID) — so the dialog doesn't expose it. `objSS` position is a **small nonzero** placeholder, never exact `(0,0)`: the loot-spawn path (kiosk/Special-Offer/starting-ship) doesn't reposition a template the way import does, and `(0,0)` around "Sol" is the star's own origin (the "spawns inside the sun" bug).

**Replace an existing ship.** Optionally, the export targets an existing (core or mod) ship by `strName`: the exported ship object is keyed to that name (`ExportOptions.ReplaceTarget` → the ship's `strName`), so — loaded after core — the game's whole-object override swaps the design in for the original **everywhere it spawns** (brokers, derelicts, missions). The picker lists ship files (`TemplateImport.ListShipFiles`); the real override key is the primary ship's `strName` parsed from the chosen file (`TemplateImport.ResolveShipStrName`, robust to a filename≠strName mod/multi-ship file), pre-selected to the imported ship for the import→retrofit→replace loop. It is **layout-only** (the original's contained cargo/crew loadout isn't carried — import drops sub-objects, §8.3) and affects **new spawns**, not already-instantiated ships in a save. This is the same `strName` that the delivery loot references, so replace + kiosk compose (adding a replaced ship to a kiosk it's already in is a dedup no-op via `LootList.Append`). The **mod name** (`mod_info.json strName` + folder) is separate from the ship (`ExportOptions.ModName` → `ShipExport.ResolveModName`): a replacement defaults to `"{target} - Replaced via Ostraplan"` so the mod reads distinctly from the ship it overrides, a new ship's mod defaults to the ship name, and either is freely editable in the dialog.

**Obtainability (optional delivery).** The export can also make the ship reachable in a playthrough by writing loot/chargen data — full-object overrides / additive entries the game merges by `strName`, resolved against the **effective** (mod-aware) pools so other ship mods' ships are preserved:
- **Broker kiosks** (`RandomShipBroker{OKLG,BCER,BCRS,Venus,VORB}`): append the ship as one more weighted alternative to the pool's single `aCOs` pick (`KioskExport.AppendShipToPool` — never a second array element, which would roll a second ship), at an editable weight. `data/loot/loot.json`.
- **Special Offer** (`RandomShipBrokerSpecialOffer{,VENC,VNCA,VORB}`): overwrite the single-ship pick (`PinShipToPool`). `data/loot/loot.json`.
- **Starting ship** (`StartingShipExport`): a weighted option in the **Shipbreaker** career's `CGEncShipbreakerShipEvents` roll — an `…Intro`/`…Take` lifeevent+interaction pair (modeled on core `CGEncShipSalvagePod*`, reusing the core "continue career" branch and `ItmShipbreakerLoadout` starting gear) plus a `…Reward` ship loot naming the design, an editable mortgage/start-ATC. There is no true chargen ship-picker in vanilla, so by default this is a weighted chance, not a guaranteed pick. A **"Only your ship offered (guaranteed start)"** option (`ShipDelivery.StartingShipExclusive`) instead **pins** the events pool to this ship alone (via `KioskExport.PinShipToPool`), so a fresh Shipbreaker always starts with it — this drops the vanilla salvage pods (and any other mod's start ships) from that roll. `data/loot` + `data/lifeevents` + `data/interactions`.
Where another ship mod overrides the same pool, whole-object load semantics would drop one side; the dialog directs the user to Ostrasort's `--patch` (its per-item-union merge for shop/kiosk pools).

**One-click Ostrasort hand-off.** When staged into the game Mods folder, the export can invoke Ostrasort headlessly (`OstrasortLauncher`): `--apply` (registers the folder — `BuildSuggestion` rule 4 adds unregistered local mods — and sorts the order), then `--patch` when the export touched loot. Ostraplan detects `Ostrasort.exe` (remembered path → conventional locations → one-time picker, persisted in `AppSettings.OstrasortPath`) and passes its already-resolved `--game`/`--mods` paths. It **still never writes `loading_order.json` itself** — this only drives the tool that owns it (§14 open question #5, resolved).

### 8.3 Import (shipped 2026-07-05)

Both paths reuse one forward mapping (`ShipGrid.TemplateTile`, the inverse of export) and resolve **every placed def on demand** through `Catalog.Lookup` — the editor's palette is buildable-only, but ~half of a real ship's tiles are non-buildable defs (raw hull, RCS/sensors, abstract tiles); those resolve to a category-"—" `PartDef` (out of the palette, but rendered and analysed). **Contained/slotted sub-objects** (`strParentID`/`strSlotParentID` — cargo, tools, modules) are dropped: only the top-level layout is read, so import is inherently "layout only".

- **Templates**: a searchable browser over `data/ships/*.json` from core + every loaded mod (later source wins a filename clash). Multi-ship batch files import their largest ship.
- **Save games**: a save folder holds a `<name>.zip` whose `ships/<RegID>.json` are JsonShip records. The **player's** ship is found via the character record's `strShip` (the RegID it points at) — *not* `saveInfo.shipName`, which is a renamed display name (`publicName`) that matches no `strName`. Layout-only by construction (runtime state, wear and damage are simply never read); gated behind an explicit "crew, cargo, modules, wear and damage are discarded" confirmation.
- **Unresolved defs** (a modded def whose mod isn't loaded) are **skipped and named in the import report** (matching the `.oplan` "missing parts" behaviour), not fatal and not placeholder tiles.

### 8.4 PNG / SVG snapshot

Render the current canvas (chosen zoom, optional overlays) to PNG for sharing. The **Ship Rating room map** (the room-annotated snapshot) additionally exports as **SVG**: the ship sprites are embedded once as a pixel-crisp base64 PNG layer and every annotation (per-room tint, leader lines, labels) is emitted as true vectors (`ShipCanvas.RenderRatingSnapshotSvg`), so the diagram stays sharp at any zoom. The "Save image…" dialog offers PNG and SVG side by side. Both the PNG and the SVG render in the **current editing orientation** (the Q/E plan-view rotation): content is drawn unrotated and then turned by `ViewRot` as a wrapping transform (`OrientOutput`/`SvgTransform` — the raster swaps output width/height at 90°/270°, the SVG uses a rotation group), while the room labels stay upright and re-route to the nearest edge of the rotated image.

### 8.5 Save-edit round-trip (import for editing → write back to a copy)

A design imported *for editing* (Import ▸ "Your ship, for editing") carries, beyond the layout, the identity needed to write structural edits back into the save without disturbing anything else:

- **Import** (`SaveEditImport`) reads one ship record and builds the same layout-only document as a plain save import, but additionally tags each placed part with its source item `strID` (`Placement.OriginStrID`), stamps the document's `SourceSave`, and retains a `SaveShipContext` — the parsed ship record plus `strID`-keyed maps of every item, CO and cargo subtree.
- **`.oplan` persistence** — the per-part `origin` and the `source` block (§8.1). The heavy context is *not* serialized; on reopen it is rebuilt on demand by re-locating the ship in the referenced save (`SaveEditImport.RelocateContext`).
- **Write-back** (`SaveEdit.BuildInjectedShip` → `WriteCopy`, the "Update Ship in Save…" action) diffs the edited layout against the retained originals (kept / moved / new / deleted — `ShipDiff`), then rebuilds the ship record: kept parts verbatim, moved parts repositioned with their cargo (in the original `vShipPos` frame), new parts as fresh entries (a pristine CO synthesized per new part so a save load doesn't skip them), deleted parts and their cargo dropped; `aCOs` filtered, `aRooms`/`aRating` and the grid recomputed (the grid grows for new parts, never shrinks below the original). It hard-validates, then duplicates the save folder (zip renamed, `saveInfo` updated) and splices only `ships/<RegID>.json`. The **default is a copy**, leaving the original untouched; **overwriting in place** is an explicit opt-in that first copies the whole save to a separate, loadable **backup save in the Saves folder** — beside the save, never inside it, so deleting a broken edit can't take its backup with it — then edits the original. A **Wear** slider (§6.12) can additionally re-roll the condition of **every** installed part to a chosen average (default on, at the vanilla ~88%), overriding existing per-part damage; unticked, each kept part keeps its in-save wear and new parts stay pristine.

Because the live state lives in the save, a save-edit `.oplan` needs that save present to write back; Export (§8.2) is the route to a save-independent, shareable artifact. Phase 3 (edge-case hardening + owner-driven in-game E2E) is the remaining work (§11).

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

- **Renderer**: single-pass `OnRender` painting placements in **z-layer order** — the game leaves `nLayer=0` on every item (it Y-sorts sprites over a floor tile-layer), so Ostraplan ranks each part by the conditions it contributes (floor < wall/door < fixtures < power conduit; `Catalog.RenderLayer` → `ShipDocument.DrawOrder`) and hit-tests topmost-first (`HitTestStack` drives the right-click layer picker). Non-sheet sprites draw at their own `vScale` size centered on the footprint (§6.2); `SpriteCache` holds lazy-loaded, frozen bitmaps (~6k PNGs on disk; only used ones decoded). See [GAME-INTERNALS §6](GAME-INTERNALS.md). The at-rest ship is a **frozen `DrawingGroup` cache** baked **pan- and rotation-independently** (both are applied as `OnRender` transforms) at the current zoom, so a pan/rotate frame is one cached blit and only a **zoom or content** change rebuilds it — panning a station stays smooth (v0.33; earlier the cache was screen-space and every pan frame rebuilt the whole ship).
- **Analysis threading**: rooms/certification/rating run debounced on a worker; UI stays responsive; results stamped with a document revision to discard stale runs.
- **Wall/floor autotiling** (ported, `Autotile.cs`): sprite-sheet items (`bHasSpriteSheet` + `ctSpriteSheet`) select a sheet cell from the 4 cardinal neighbours whose tile conds trigger `ctSpriteSheet` — mask bits N=8/W=4/E=2/S=1 → the fixed 16-entry `Item.SpriteSheetIndices` table → a cell whose rows count from the texture *bottom* (Unity UV; WPF flips). Core wall sheet is 64×64 = a 4×4 grid of 16 px tiles. Exact constants — see [GAME-INTERNALS §6](GAME-INTERNALS.md).

## 10. Testing & the parity gate

1. **Parity corpus (the Law gate)** — ground truth as it actually ships: all **192** core ship objects carry baked `aRooms` (a 192-ship rooms **and** certification gate); only **2** (Babak / Babak Refit, both damaged derelicts) carry `aRating`. Achieved: recomputed room partition equals stored `aRooms` (tile sets + `bVoid`, portal-tile filing and exterior-void over-claim compared leniently — neither affects the Law) for **188/192** (4 named exotic exclusions); recomputed `roomSpec` matches for **2124/2148** rooms with **0 over-certifications** of a real compartment (the 24 diffs are two documented corpus artifacts — contained-cargo undercount, exterior CargoRoomExterior aggregation — that can't reach an authored design; the 0.17.0 `"use"`-point membership fallback recovered 15 wall-embedded-part rooms); rating size-slot exact on the base Babak, room count bounded by the baked `aRooms` (the Babaks' `aRating` room slot is a stale 18 vs their own `aRooms`' 20), cutoffs unit-pinned. Large station files (up to 35 MB) double as performance tests.
2. **Engine unit tests**: socket semantics per socket type; rotation of masks; snap parity; flood-fill edge cases (door corners, unsealed single tile, grid-edge escape); spec-matcher multiplicity/priority; constructibility ordering.
3. **In-game E2E (manual, user-driven)**: export → register via existing tooling → load in game → compare the game's logged rating and MODS-screen status against Ostraplan's prediction. Ostraplan ships a short checklist for this; test runs and result reporting are done by the user.
4. **Performance budgets**: `CheckFit` for ghost feedback < 16 ms; full re-analysis < 250 ms typical ships, < 2 s for station-scale imports; cold start (index + palette) < 3 s.

## 11. Roadmap

| Phase | Version | Delivers | Acceptance |
|---|---|---|---|
| **P0 — SHIPPED 2026-07-04** | 0.1 | Everything originally planned (mod-aware index, palette, canvas, sprites/autotile, `.oplan`, undo/redo, zoom/pan) **plus** a full editing suite and several law elements pulled forward — see the delivered-beyond-plan note below | Met — real parts render game-correctly and round-trip; 23 tests incl. two visual smoke renders |
| **P1 — SHIPPED 2026-07-04** | 0.2 | `Item.CheckFit` ported on the already-shipped tile-condition accumulator: ring-grid reqs/forbids over (W+2)×(H+2), the off-ship rule, rotation of the ring masks, snap parity. **Cell test is presence-only** — traced through `CheckFit`→`CondTrigger.Triggered` on a throwaway AND-trigger of the socket loots' expanded condition names, so count-multiplicity / nested-`aTriggers` / `bAND=false` are unreachable from placement and deferred to P2 (room certification); autotile's `Triggered` is untouched. **Hard rejection** in the single `TryPlacePose` choke point covers click/paint/box/hollow-fill/symmetry, plus the **airlock envelope** (all ports, ring-inclusive — provably never allows what the game refuses). **Moves/rotations/duplicates into an illegal spot are allowed but flagged** (deletes too): `ProblemScan` re-checks each placed part with its own contribution excluded (walls/fixtures add `IsObstruction` and forbid it on their own footprint), grouped by reason, and hazard-tints the offending tiles. Live green/red ghost with per-cell failure highlights + a reason in the status bar. Constructibility pass (canonical floors→walls→rest re-sim, warn-only, only when the final design is otherwise legal). | Met — cannot place anything the game would refuse; envelope violations are unplaceable; 36 tests incl. real-data bed/wall/door/envelope Law cases + a hazard-tint smoke render. In-game spot-check pending (user-driven). |
| **P2 Law milestone — SHIPPED 2026-07-05** | 0.3 | Rooms/airtightness flood fill (`CreateRooms`), room certification (`RoomSpec.Matches` + the reachable `CondTrigger.Triggered`), Ship Rating (`CalculateRating`), and the on-demand **Ship Rating** button → progress → modal law report with unsealed-tile leak highlighting. Parity-first: a headless ship-template loader (exact `TLTileCoords`/CCW coordinate port) validated against the game's baked `aRooms`/`aRating`. | Met — **rooms parity 188/192** (4 named exotic-ship exclusions); **certification 2109/2148 rooms exact with 0 over-certifications** of a real compartment (the 39 diffs are two documented corpus-only artifacts: contained-cargo undercount + exterior over-claim); **rating** size-slot exact on the base Babak + every cutoff unit-pinned. 55 tests green. In-game E2E spot-check pending (user-driven). |
| **P3 Interop — SHIPPED 2026-07-05** | 0.4 | **Export as a spawnable local mod** (`data/ships/<Name>.json` in the game's JsonShip shape — full universal field set + minimal `shipCO`, precomputed `aRooms`/`aRating`, reversed centre/CCW coordinates, fresh GUIDs, `origin:"$TEMPLATE"`; a mod folder + `mod_info.json`, staged into `Mods/` or a chosen folder, **never** `loading_order.json`). **Template import** (searchable core+mod `data/ships` browser; the shared `ShipGrid.TemplateTile` forward mapping; any placed def resolved on demand via `Catalog.Lookup`, so the ~half of a ship that is non-buildable renders + analyses; contained sub-objects dropped; unresolved defs reported). **Save-game import** (the player's ship, found via the character record's `strShip`, layout-only behind a confirmation). The primary-airlock lock carries over for free (it keys on the def name). | **Met** — a `doc → export → ShipTemplate.Parse → ShipGrid.FromTemplate` round-trip reproduces the same tiles/rooms/rating across rotations; import reproduces the game's baked interior compartments on real templates + a closed export↔import identity loop + an import→export→re-parse capstone on real ships; save import identifies the player ship and strips runtime state. **Import verified in-game** (a real save ship imports clean with the correct rating; in-game testing surfaced and fixed placement-law false positives on given structure, conduit autotiling, and buried-part selection). 81 tests green. The **export-spawn** E2E (register a mod, spawn it, confirm the rating) remains user-driven. |
| **P4 QoL — SHIPPED 2026-07-05** | 0.5 | (A) **double-click flood-select** — 4-connected magic wand over same-def 1×1 tiles (seed from a lone selected part else topmost; Ctrl adds; the eyedropper moved to a right-click "Use as brush"); (B) **"Replace with…"** — swap a same-kind selection (same render layer + footprint) for a compatible buildable part via a thumbnail picker, one undo step, place-and-flag; (C) **big-ship performance** — batched box-fill (a 50×50 fill was ~2,500 problem scans ≈ 40 s → one scan), off-thread debounced problem scan reading `ShipDocument.Snapshot`, cached static-ship `DrawingGroup` reused across band/fill-drag frames, and a tile→parts spatial index (`PlacementsAt`) so hit-test/overlap stop scanning every part (a 2,500-lookup probe 333 ms → 0.2 ms); (D) **PNG snapshot** export of the ship (sprites only, sized to bounds + margin); (E) **cooverlay theme picker** — bulk ship-wide wall/floor re-skin via `ThemeOps` (§7); (F) **bill of materials** — install-kit counts via `BillOfMaterials` (§7). Along the way, a latent maneuver-mass bug was fixed (cond magnitudes were read from before the `x` — the apply chance — instead of after it). **Dropped:** per-mod palette toggles (managing mods is out of scope) and power/O₂ budgets (no per-device draw or gas rates in the data — an honest balance needs simulation, a non-goal). | Met — 112 tests green; publish smoke passes. In-game confirmation of the editor UX is owner-driven. |
| **P5 Save-Edit Phases 1–2 — SHIPPED 2026-07-06** | 0.5 | Edit your live in-game ship out-of-game and write it back into a **copy** of the save (never the original), preserving crew/cargo/position/identity (§8.5). **Phase 1 — identity + context + diff:** `Placement.OriginStrID` (init-only; preserved across move/rotate/group-rotate, dropped by duplicate/paste/paint/mirror/def-changing replace-re-skin/layout import) + `ShipDocument.SourceSave`; an **import-for-editing** path (`SaveEditImport`) that tags each placed part with its save `strID` and retains a full `SaveShipContext` (parsed ship record + `strID`-keyed `Origins`/`ItemsById`/`CosById` maps + per-part cargo subtree); `.oplan` persistence of `origin` per part + the `source` save ref; and the pure `ShipDiff` engine. **Phase 2 — inject to a copy:** `SaveEdit.BuildInjectedShip` rebuilds the retained record from the diff (kept verbatim, moved repositioned in the original `vShipPos` frame + cargo shifted, new = fresh no-CO entry, deleted + cargo dropped; `aCOs` filtered; `aRooms`/`aRating`/grid recomputed; grid never shrinks below original, grows for new parts, crew `nDestTile` recomputed on growth), hard-validates, then `WriteCopy` duplicates the save folder (zip renamed, `saveInfo` name updated) and splices only `ships/<RegID>.json`. UI: Import ▸ "Your ship, for editing", the "Update Ship in Save…" action (cargo-loss warn+confirm, overwrite-after-confirm, change report), and on-demand context re-location for a reopened `.oplan`. | Met — 128 tests green (14 new); publish smoke passes. Verified against the live install's newest player ship (writing only to a throwaway temp copy): import→context has 1:1 item↔CO identity; a no-op inject **round-trips every item**; add/delete change the item+CO counts correctly; the copy is a well-formed save and the **original is byte-for-byte untouched**. **Phase 3** (harden: moved-container-cargo & crew-`nDestTile` edge cases, occupied-deleted-floor semantics, owner-driven in-game E2E) still to come. |
| v1.0 | 1.0 | Continued hardening and owner-driven in-game E2E across the save-edit and cargo features | — |

**Delivered beyond plan in v0.1** (three QoL iterations on top of the foundation): drag-paint, Shift box fill and Ctrl+Shift hollow fill (strokes = one undo step); symmetry mode (M: V/H/Both, mirrored positions *and* rotations); right-click context menu (duplicate/rotate/delete, composite undo); smooth WASD panning; Q/E plan-view rotation with rotation-aware input throughout; grid visible at all zooms; gold origin marker; toolbar tooltips + F1 help modal; app icon. Most significantly, the **docking ground truth was researched and shipped early**: the Primary Airlock convention (one per ship, seeded at the origin, locked, outside the palette), the `GetAirlockBounds` construction envelope rendered as red hazard stripes, and the `ProblemScan` engine with canvas badges + inspector panel (checks so far: *no docking port*, *construction beyond an airlock*). The P1 prerequisite "tile-condition accumulator" also shipped in v0.1 — it is what drives faithful autotiling.

## 12. Risks & mitigations

| Risk | Mitigation |
|---|---|
| Tile-condition aggregation fidelity (every overlapping part contributes; state variants are distinct defs) | Port the accumulator exactly; parity corpus; socket-debug overlay for diagnosis |
| Order-dependent legality vs final-state validation | Constructibility pass (§6.6), warn-only |
| Void/edge semantics tied to grid pad/trim lifecycle | Mirror `PadTilemap`/`TrimAllSides` explicitly; flood-fill edge-case unit tests |
| Room-spec matching subtleties (priority, multiplicity, exclusions) | Direct port + per-spec unit tests + corpus |
| Version drift: rating cutoffs & constants hardcoded in the DLL | Version pinning (§5.3), reviewed and re-decompiled manually per game patch (ilspycmd, seconds) |
| Mass accounting for maneuver grade | Port `GetTotalMass` semantics at P2; planner ships are empty/pristine, which removes most complexity |
| Sprite-sheet/autotile mapping wrong | Pin mapping from decompiled sprite code at P0; visual diff against in-game screenshots |

## 13. Distribution & IP hygiene

- Repo `Valtora/Ostraplan` — **public**, MIT-licensed, mirroring Ostrasort.
- Written for a clean public flip: engine code is **our own reimplementation of observed behavior** (no pasted decompiler output); each engine file carries a header noting the game version its behavior was verified against; **zero game assets** in the repo or releases — Ostraplan reads data and sprites from the player's own install at runtime, exactly like Ostrasort. `GAME-INTERNALS.md` documents *observed* behavior and cites game methods by `Type.Method`; it commits **no** decompiler output (regenerate on demand, §GAME-INTERNALS).
- Distributed as a self-contained `Ostraplan.exe` on GitHub Releases (built by `publish.ps1`), mirroring Ostrasort. MIT `LICENSE`; the README credits Blue Bottle Games and states the fan-tool / asset policy; `docs/usage.md` + `CHANGELOG.md` are the user-facing docs.

## 14. Open questions (tracked, none blocking)

1. ~~Sprite-sheet index mapping~~ **Resolved (P0):** mask bits N=8 / W=4 / E=2 / S=1 over `ctSpriteSheet`-triggered cardinal neighbours (`Item.SetSpriteSheetIndex`), the fixed 16-entry `Item.SpriteSheetIndices` table, and cell rows counted from the texture *bottom* (`GetMaterialSheet` UV offsets) — WPF flips the row. Ported in `Autotile.cs`.
2. ~~Mod image override semantics~~ **Resolved (P0):** the game prepends each loaded mod to its image search list (`DataHandler.LoadMod` → `aModPaths.Insert(0, …)`), so the latest-loaded mod wins; `DataIndex` mirrors this with later-source-wins path indexing.
3. ~~Game-version detection~~ **Resolved (P0):** `Application.version` sits as a plain ASCII string inside `Ostranauts_Data/globalgamemanagers` (same technique as Ostrasort's `GameEnv`).
4. Exact `GetTotalMass` semantics for the maneuver grade (P2).
5. ~~One-click "deploy + register" integration~~ **Resolved:** the export dialog's "Register with Ostrasort" hands the staged mod to `Ostrasort.exe --headless --apply` (then `--patch` when loot was touched) via `OstrasortLauncher`, resolving the exe by remembered-path → conventional-locations → one-time picker; Ostraplan still never writes `loading_order.json` itself (§8.2).
6. `.oplan` file association + icon (P4).

---

*Prepared from the 2026-07-04 feasibility investigation: live-data survey (items/installables/rooms/ships/images), ship-serialization analysis (templates + save games), and a full ilspycmd decompile of `Assembly-CSharp.dll` (placement, rooms, rating, data-loading paths mapped).*
