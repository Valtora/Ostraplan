# Ostraplan — Game Internals Reference

Everything Ostraplan has learned about **how Ostranauts works internally**, gathered from decompiling `Assembly-CSharp.dll` and reading the live game data. Ostraplan's promise ("the Law") is met by *porting* this logic, never by referencing the DLL (its types are MonoBehaviours that round-trip through Unity — see [SPEC §2](SPEC.md)). This file is the durable record of that reverse-engineering: consult it when implementing a new law slice, and re-verify it after every game patch.

**Verified against game `0.15.1.6`** (`GameEnv.VerifiedGameVersion`). Rating cutoffs and other magic numbers are hardcoded in the DLL and invisible to data diffing, so they can silently drift — the version pin exists to warn about exactly that.

---

## 1. Regenerating the decompile

The decompiled source is **not** committed (IP hygiene — see [SPEC §13](SPEC.md)). Regenerate on demand (~7 s; `ilspycmd` is an installed global dotnet tool):

```powershell
ilspycmd -p -o <scratch-dir> "C:\Program Files (x86)\Steam\steamapps\common\Ostranauts\Ostranauts_Data\Managed\Assembly-CSharp.dll"
```

Line numbers below are approximate (they shift when the game updates) — cite by **`Type.Method`**, which is stable. Key files and members:

| File | Members that matter |
|---|---|
| `Item.cs` | `CheckFit` (placement law), `SetData` (footprint + sprite scale plumbing), `RotateCW`, `SetSpriteSheetIndex` (autotile) |
| `CondTrigger.cs` | `Triggered` (the trigger evaluator — full semantics; CheckFit only reaches a subset) |
| `Loot.cs` | `GetLootNames` (how a socket loot resolves to condition names) |
| `TileUtils.cs` | `RotateTilesCW`, `GetSurroundingTiles`, `PadTilemap`/`TrimTiles`, `GetAirlockBounds` |
| `Ship.cs` | `UpdateTiles` (tile-condition accumulation), `CreateRooms`, `CalculateRating` (P2, not yet ported) |

### After a game patch — re-verification checklist
1. Re-decompile; diff `CheckFit`, `SetData`, `RotateCW`, `CalculateRating` for logic changes.
2. Re-run the test suite (the `GameDataTests` assert real-data facts this file documents).
3. Bump `GameEnv.VerifiedGameVersion` once green.

---

## 2. The data model

Game data lives under `…/StreamingAssets/data/<type>/` as JSON arrays of objects keyed by `strName`. Ostraplan resolves the effective data exactly like the game — later-loaded mod object with the same `(type, strName)` replaces the earlier, whole-object (see [SPEC §5.2](SPEC.md)). Field names use Hungarian prefixes (`str`, `n`, `b`, `a`=array, `map`, `json`).

### The palette join (how a build-menu entry becomes a placeable part)

`Installables.dictJobBuildOptions` / `GUIPDA.ShowJobOptions`, mirrored in `Catalog.Build`:

```
data/installables  (strJobType == "install", strBuildType ∈ HULL HVAC POWR SENS CTRL FURN APPS MISC)
        │  strStartInstall
        ▼
data/condowners  ── or via ──▶  data/cooverlays (strCOBase → the real condowner; overlay swaps sprite + friendly name)
        │  strItemDef                                     (~half of the ~330 menu entries are cooverlay skins)
        ▼
data/items  ── geometry: nCols, aSocketAdds/Reqs/Forbids, strImg, bHasSpriteSheet, ctSpriteSheet
```

- `strStartInstall` names the **condowner** to place, resolved directly or through a cooverlay whose `strCOBase` is the real one (`DataHandler.LoadCO`'s fallback).
- State variants are separate menu entries: doors install as `…Open`, beds/appliances as `…Off`.
- A naive `items[strStartInstall]` finds only ~157 of ~330 — you must follow the condowner/cooverlay hop.

---

## 3. Loots & conditions — the tile vocabulary

Everything about placement and rooms is written in **conditions** accumulated on tiles, produced by **loots**.

### Loot mechanics (`Loot.cs`)
- A loot carries `aCOs` (its own payload: `"IsWall=1.0x1"` strings → condition **names**) and `aLoots` (nested loot names).
- `GetLootNames()` flattens `aCOs` cond-names **plus** the recursive expansion of `aLoots`. Socket masks use deterministic single-unit loots (`chance 1.0`, `count 1`), so this is a plain set-union with no randomness. Ostraplan mirrors it in `Catalog.LootConds` (used by `TileConds` accumulation and `CheckFit`).
- `"Blank"` (and any unresolved name) → empty → an **unconstrained** cell.

### Tile-condition accumulation (`Ship.UpdateTiles`, ported as `TileConds`)
Each tile holds a condition multiset (`Tile.coProps`). On place/remove, **every overlapping part** adds/subtracts its per-cell `aSocketAdds` loot's conditions (±1). Presence means "count > 0". State variants (door Open vs Closed) are *different item defs with different adds* — Ostraplan places the `strStartInstall` def, exactly as the game's installer does.

### The `TIL*` loot table (verified from `data/loot`)

Adds loots (what a part contributes to its own footprint tiles):

| Loot | Expands to |
|---|---|
| `TILFloor` | `IsFloor`, `IsFloorSealed` |
| `TILWallAdds` | `IsObstruction`, `IsWall` |
| `TILFixtureAdds` | `IsFixture`, `IsObstruction` |
| `TILExtFixtureAdds` | `IsFixture`, `IsFixtureExt`, `IsObstruction`, `IsWallDeco` |
| `TILSubfloorAdds` | `IsSubTile` *(walkable sub-floor — under-floor storage, no solid body)* |
| `TIL2DeckAdds` | `IsFixture`, `IsSubTile`, `IsObstruction` *(the visible tank body — above-floor)* |
| `TILPowerConduit` | `IsPowerConduit`, `IsPowerPath` |
| `TILPowerFixtureAdds` | `IsFixture`, `IsObstruction`, `IsPowerPath` |

Req/forbid loots (what a cell tests for) — same expansion, different intent:

| Loot | Expands to | Used as |
|---|---|---|
| `TILFloor` | `IsFloor`, `IsFloorSealed` | **req**: both must be present |
| `TILWall` | `IsWall` | **req** |
| `TILObstruction` | `IsFixture`, `IsFixtureExt`, `IsObstruction`, `IsItemTile`, `IsFloorFlex` | **forbid**: fail if *any* present |
| `TILSubfloorForbids` | `IsSubTile` | **forbid** |
| `TIL2DeckForbids` | `IsFixture`, `IsSubTile`, `IsObstruction`, `IsItemTile`, `IsWallDeco`, `IsFloorFlex` | **forbid** |

Condition vocabulary that drives Ostraplan's own logic: `IsFloor`/`IsFloorSealed`/`IsFloorFlex` (floor), `IsWall`/`IsPortal` (walls & doors), `IsObstruction` (solid/blocking), `IsFixture` (furniture/appliances), `IsSubTile` (sub-floor), `IsPowerConduit` (thin power runs), `IsDockSys`/`IsInstalled` (docking ports).

---

## 4. Footprints vs sprites — **two different sizes** (a core gotcha)

The game keeps two independent sizes for an item, and Ostraplan must not conflate them:

| Size | Formula | Source | Used for |
|---|---|---|---|
| **Socket / placement grid** | `nWidthInTiles × nHeightInTiles` = `nCols × (aSocketAdds.Count / nCols)` | `Item.SetData` (≈L424-425) | CheckFit, ghost/selection extent, tile accumulation |
| **Visual sprite size** | `vScale = round(texturePx / 16)` tiles, min 1 | `Item.SetData` (≈L438-439) | how big the sprite is drawn (centered on the footprint) |

For most parts these are equal (a 1×1 wall = 16×16 px; a 3×5 bed = 48×80 px). **The large fuel tanks are where they diverge** and it's not a data error:

- `ItmCanisterLH02` (D2O), `ItmCanisterLHe01/02` (He3): `nCols = 7`, 49 adds → **7×7 socket grid**, but a **48×48 px = 3×3 sprite**.
- The socket grid is an **abstraction of sub-floor storage**: the outer ring adds `TILSubfloorAdds` (walkable sub-floor), only the **center 3×3** adds `TIL2DeckAdds` (the solid, visible tank). `aSocketReqs` is `TILFloor` across the whole inner 7×7 — the game genuinely needs a **7×7 sealed-floor pad** to place one.

**Ostraplan's rule:** render the sprite at its own `vScale` size, centered on the footprint (`SpriteCache.SpriteTiles` + `ShipCanvas.DrawSprite`); **keep the footprint at the 7×7 socket grid** for CheckFit/selection. Shrinking the footprint to 3×3 would let Ostraplan place a tank in a gap the game refuses — a **Law false positive**, the cardinal sin. So a tank *looks* 3×3 but still reserves its true 7×7 pad, matching the game's own 7×7 build grid.

The build ghost distinguishes the two footprints (`Catalog.IsUnderFloorLoot`: a cell is **under-floor** when its adds mark `IsSubTile` **without** `IsObstruction`): the sub-floor ring is shaded apart with a dashed reservation outline, and the solid green/red validity outline hugs the above-floor body. Once *placed*, the game just shows the 3×3 tank on plain (walkable) floor, so Ostraplan does too.

---

## 5. Placement law — `Item.CheckFit` (P1, ported)

Ported to `Ostraplan.Core/CheckFit.cs`. For a candidate `(part, anchor, rotation)`:

1. **Ring grid.** `aSocketReqs` / `aSocketForbids` are per-cell loot names over the **(W+2)×(H+2) ring** (footprint + 1-tile border), row-major, border included. `aSocketAdds` covers only the W×H footprint. Ring cell `(r,c)` → world tile `(anchorX-1+c, anchorY-1+r)`.
2. **Cell test — PRESENCE ONLY.** CheckFit builds a *throwaway* `CondTrigger { aReqs = reqLoot.GetLootNames(), aForbids = forbidLoot.GetLootNames() }` (default `bAND = true`) and calls `Triggered`. Because these are trivial triggers of condition names, only the presence path runs: **every req condition present (count > 0), no forbid condition present**. The full `CondTrigger.Triggered` machinery — count multiplicity, nested `aTriggers`, `bAND = false` OR-logic, `strHigherCond`/`aLowerConds`, `fChance` — is **unreachable from placement** and is deferred to P2 (room certification). Ostraplan does *not* route through a general trigger evaluator here; it expands the loots and checks presence directly, leaving autotile's presence-only `TileConds.Triggered` untouched.
3. **Off-ship rule.** A ring cell with no accumulated conditions (empty space) **passes iff it has no requirement** — this is how "must attach to structure / needs floor beneath" is encoded. (An existing-but-empty tile behaves identically, so Ostraplan treats "no conds" uniformly.)
4. **Rotation.** 90° steps rotate the req/forbid ring masks and the adds mask. `GridMath.Rotate(cells, W+2, H+2, rot)` reproduces `TileUtils.RotateTilesCW(cells, W+2)` **exactly** (verified). **Sheet items (walls/floors, `bHasSpriteSheet`) never rotate** — `Item.RotateCW` returns early for them.
5. **Airlock envelope.** For each installed docking port, `DockA→DockB` defines a mating face; **no ring cell may fall beyond it**. The game bounds only `aDocksys.FirstOrDefault()`; Ostraplan bounds **all ports, ring-inclusive** — provably never allows what the game refuses (refusing a superset can't create a false positive), and identical to the game on the single-port ships that are the norm. Face math is `ProblemScan.TryGetFace` (see §7).
6. **Self-exclusion (re-validation).** When re-checking an *already-placed* part, its own tile contribution must be subtracted first — walls/fixtures add `IsObstruction` **and** forbid `TILObstruction` on their own footprint, so they fail against themselves otherwise. `CheckFit.Check` takes an optional `self` placement for this; the flagging scan (`ProblemScan`) uses it, live placement checks don't (the candidate isn't applied yet).
7. **Excluded by design** (in-game-only predicates that cannot exist in a planner): crew proximity/LOS (`GUIInventory.instance.Selected` + `Visibility.IsCondOwnerLOSVisible`), docked-ship `TileUtils.WouldConnectShips`, and station-zone (`JsonZone`) restrictions.

### Enforcement & flagging (Ostraplan's P1 UX decisions)
- **New placement is hard-blocked** at the single choke point `ShipCanvas.TryPlacePose` (covers click, drag-paint, box/hollow fill, symmetry mirrors — each mirror judged independently). Illegal cells are silently skipped.
- **Moves / rotations / duplicates into an illegal spot are allowed but flagged** (deletes always allowed + flagged). `ProblemScan` re-checks every placed part (self-excluded), groups blocking problems by reason, and hazard-tints the offending tiles; the ghost shows green/red with per-cell failures and a reason in the status bar.
- **Constructibility pass** (warn-only): re-simulate a canonical floors→walls→rest build order with incremental CheckFit; warn naming the first part that never becomes placeable. Runs only when the finished design is otherwise legal (locked ship-givens seeded first).

### Worked examples (real data, asserted in `GameDataTests`)
- **`ItmWall1x1`** — 1×1, `aSocketReqs` all Blank (free-standing, like the game), center forbid `TILObstruction` (won't stack on an obstruction). Sheet item (`ctSpriteSheet = TIsWall`).
- **`ItmBed01Off`** — 3×5. Reqs: `TILFloor` across the footprint + `TILWall` down the **right** border (the headboard). Forbids: `TILObstruction` on the footprint **and** the left border. Adds `TILFixtureAdds` (so it forbids the obstruction it will itself add — see self-exclusion).
- **`ItmCanisterLH02`** — 7×7 socket grid, 3×3 sprite (§4).
- **`ItmDockSys03Closed`** — the buildable "Secondary Exterior Airlock", 7×2, free-standing (all-Blank reqs).

---

## 6. Rendering

- **Z-order.** `nLayer` is `0` for **every** item in the data — the game does not layer items by `nLayer`; it Y-sorts sprites over a separate floor tile-layer. Ostraplan's single-pass renderer instead ranks each part by the conditions it contributes (`Catalog.RenderLayer`, memoized): **floor** (`IsFloor`/`IsFloorSealed`/`IsFloorFlex`) < **wall/door** (`IsWall`/`IsPortal`, checked first — a door also seals floor) < **fixtures & the rest** < **power conduit** (`IsPowerConduit`, thin runs on top). `ShipDocument.DrawOrder` sorts by rank, then `nLayer`, then Y, then insertion. Hit-testing returns the topmost layer; `HitTestStack` (reverse draw order) drives the right-click layer picker for reaching buried parts.
- **Sprite draw.** Non-sheet sprites draw at `vScale` size centered on the footprint (§4). Sheet items draw per-tile.
- **Autotiling** (`Item.SetSpriteSheetIndex`, ported in `Autotile.cs`). Sheet items (`bHasSpriteSheet` + `ctSpriteSheet`) pick a sheet cell from the 4 cardinal neighbours whose tile conds trigger `ctSpriteSheet`: mask bits **N=8, W=4, E=2, S=1** → the fixed 16-entry `Item.SpriteSheetIndices` table → a cell index whose **rows count from the texture bottom** (Unity UV origin; WPF flips the row). Core wall sheet is 64×64 = a 4×4 grid of 16 px tiles. These constants are exact ports — do not "fix" them.

---

## 7. Docking & airlocks

- A ship needs **≥1 installed docksys** or it can never hard-dock (`Ship.aDocksys` collects COs that trigger `TIsDockSysInstalled`; 42 core templates have none — no crash, just unmateable). Ostraplan surfaces a standing "no docking port" problem.
- `TIsDockSysInstalled` reqs are `[IsDockSys, IsInstalled]` and **all** must match. Matching *any* (via `IsInstalled`) flags every installed part — a real bug that shipped once; `ProblemScan.IsDocksys` requires all reqs and is regression-tested.
- **No rule ties an airlock to (0,0)** — 0 of 147 core templates place one at the origin, and the Babak has two. The "primary" (`Ship.PrimaryDockingPortID`) is a runtime-cyclable selection defaulting to the first port. Ostraplan's **Primary Airlock convention** is a UI simplification: `ItmDockSys02Closed` (`IsIndestructable`, `IsShipSpecialItem`, no install job) resolves for documents but stays out of the palette; every doc owns exactly one, seeded at the origin and locked (`ShipDocument.IsLocked`). The buildable one is the **Secondary** (`ItmDockSys03Closed`).
- **The one real positional rule: no construction beyond an airlock's mating face.** `TileUtils.GetAirlockBounds` builds a half-plane per port from its `DockA→DockB` arrow (condowner `mapPoints`, pixels around the item centre, **+y up**; `DockA` at the door, `DockB` outside the hull). On the arrow's dominant axis the face line is the A–B midpoint; everything beyond it is out of bounds (and a blocked face can't mate with a station collar). Ported exactly in `ProblemScan.TryGetFace` (verified: the face lands on the port's footprint edge); rendered as red hazard stripes and enforced as a hard CheckFit bound (§5.5). **Coordinate note:** the game is y-up, Ostraplan documents are **y-down**; `mapPoints` conversions are handled where they cross that boundary (`ProblemScan.Transform` is the pattern).

---

## 8. P2 subsystems — surveyed from the decompile, **not yet ported**

Recorded here from the feasibility survey so P2 starts with a map, not a blank page. Treat as leads to re-confirm against the decompile when implementing.

- **Rooms & airtightness — `Ship.CreateRooms`.** BFS flood fill over non-wall tiles, **4-connectivity** (N/W/E/S). Walls terminate expansion and record adjacency; door (portal) tiles are boundaries assigned afterwards to the preferred non-void adjacent room via `RoomA`/`RoomB`. A room is **Void** if any member tile lacks `IsFloorSealed`, or the fill reaches the edge of the (padded) tile array (also marks it Outside). Volume = `0.256 m³ × tileCount` (hardcoded). Grid grows via `TileUtils.PadTilemap`, trims on export via `TileUtils.TrimTiles` — void detection depends on the fill reaching the array edge, so mirror that lifecycle. **The sub-floor/deck split (§4) is airtightness-relevant:** `TILSubfloorAdds` is walkable-but-not-sealed storage, `TIL2DeckAdds` is a solid obstruction.
- **Room certification — `Room.CreateRoomSpecs` / `RoomSpec.Matches`** against the 18 `data/rooms` specs. Sorted by `nPriority` descending, **first match wins**, else `Blank`. A spec matches iff `room.Void == bAllowVoid`, tile count within `[nMinTileSize, nMaxTileSize]` (−1 = unbounded), no member fires any `aForbids`, every `aReqs` satisfied **with multiplicity** (required count consumed by matching parts' stack counts); floor-grate members ignored; only installed parts count. **This is where the full `CondTrigger.Triggered` semantics deferred in §5.2 are finally needed.**
- **Ship Rating — `Ship.CalculateRating`.** Six slots (displayed 1–5, `-`-joined): `[epoch | condition A–E | non-Blank room count | maneuver = mass/RCS-count | size class by nCols×nRows | unused]`. Cutoffs are **hardcoded in the DLL** (version-pinned): condition ≤0.5 E / ≤0.8 D / ≤0.95 C / ≤0.99 B / else A; maneuver 0 RCS→O, <300 A … else E; size <250 Small … else Ultra Large. Needs mass accounting (`GetTotalMass`) — planner ships are empty/pristine, which removes most of the complexity.
- **The parity gate.** 214 core `data/ships` templates ship with the game's own computed `aRooms`/`aRating` baked in; the port must reproduce them. Ships JSON is clean/writable (`nRows`/`nCols`, `vShipPos`, `aItems{strName,fX,fY,fRotation,strID}`, `aRooms{aTiles,roomSpec,bVoid}`). The game trusts precomputed `aRooms`/`aRating` on shallow load and recomputes+logs on full load — that's both the export trust-path and the end-to-end verification seam.

---

## 9. Gotcha index (quick reference)

- **`nLayer` is always 0** — never layer by it; rank by contributed conditions (§6).
- **Footprint ≠ sprite** — socket grid vs `vScale`; the tanks are 7×7 footprint / 3×3 sprite (§4). Keep the footprint for the Law.
- **CheckFit is presence-only** — don't over-build the trigger evaluator for P1 (§5.2).
- **Self-exclusion** — re-validating a placed part must lift its own conds first (§5.6).
- **Envelope uses `aDocksys[0]` in-game** — Ostraplan uses all ports (safe over-approximation) (§5.5).
- **`TIsDockSysInstalled` needs ALL reqs** — an any-match flags every installed part (§7).
- **Loot payload is `aCOs`, not `aLoots`** — `aLoots` nests further loots (§3).
- **Palette join hops through condowner/cooverlay** — `items[strStartInstall]` alone misses ~half (§2).
- **Autotile rows count from the texture bottom** — WPF flips them; the mask is N8/W4/E2/S1 (§6).
- **`loading_order.json` is fragile** — top-level array only; Ostraplan reads it, never writes it (registration stays with ModTools/Ostrasort).
- **Game is y-up, Ostraplan docs are y-down** — convert at the boundary (§7).

---

## 10. Ported / deferred / excluded

| Game logic | Status | Ostraplan home |
|---|---|---|
| Palette join, mod/load-order resolution | **ported** | `DataIndex`, `Catalog` |
| Tile-condition accumulation (`UpdateTiles`) | **ported** | `TileConds` |
| Placement law (`Item.CheckFit`) | **ported (P1)** | `CheckFit`, `ProblemScan`, `ShipCanvas` |
| Airlock envelope (`GetAirlockBounds`) | **ported** | `ProblemScan.TryGetFace` |
| Footprint + sprite scale (`SetData`) | **ported** | `Defs.ItemDef`, `SpriteCache.SpriteTiles` |
| Autotile (`SetSpriteSheetIndex`) | **ported** | `Autotile` |
| Mask rotation (`RotateTilesCW`) | **ported** | `GridMath.Rotate` |
| Full `CondTrigger.Triggered` (count/nested/OR) | **deferred → P2** | needed by room certification |
| Rooms/airtightness (`CreateRooms`) | **deferred → P2** | — |
| Room certification (`RoomSpec.Matches`) | **deferred → P2** | — |
| Ship rating (`CalculateRating`) | **deferred → P2** | — |
| Crew LOS/proximity, docked-ship, zone rules | **excluded** (in-game only) | never ported |

---

*See also: [SPEC.md](SPEC.md) (design, scope, roadmap, normative algorithms) and [README.md](../README.md) (status, build).*
