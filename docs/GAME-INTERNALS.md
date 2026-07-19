# Ostranauts — Game Internals Reference

A reference for **how Ostranauts works internally**, reconstructed by decompiling
`Assembly-CSharp.dll` and reading the live game data. It is the source of truth
Ostraplan is built against: Ostraplan keeps its promise ("the Law") by *porting*
this logic, never by referencing the DLL at runtime (its types are
`MonoBehaviour`s that round-trip through Unity, so calling them off the game gives
silently wrong answers). Each system below is described as the game implements it,
with the relevant `Type.Method` citations; a short **Ported in Ostraplan** note
points to where that system is reimplemented.

**Verified against game `0.15.1.6`** (`GameEnv.VerifiedGameVersion`). Rating
cutoffs and other magic numbers are compiled into the DLL and invisible to data
diffing, so they can drift silently between patches. The version pin exists to
flag exactly that: re-verify after every game update.

**Contents**

- [1. Working with the decompile](#1-working-with-the-decompile)
- [2. The data model](#2-the-data-model)
- [3. Conditions and loots — the tile vocabulary](#3-conditions-and-loots--the-tile-vocabulary)
- [4. Footprints and sprites — two independent sizes](#4-footprints-and-sprites--two-independent-sizes)
- [5. The placement law (`Item.CheckFit`)](#5-the-placement-law-itemcheckfit)
- [6. Docking and airlocks](#6-docking-and-airlocks)
- [7. The coordinate model](#7-the-coordinate-model)
- [8. Rooms and airtightness (`Ship.CreateRooms`)](#8-rooms-and-airtightness-shipcreaterooms)
- [9. Room certification (`RoomSpec.Matches`)](#9-room-certification-roomspecmatches)
- [10. Ship Rating (`Ship.CalculateRating`)](#10-ship-rating-shipcalculaterating)
- [11. Ship value (`Ship.GetShipValue`)](#11-ship-value-shipgetshipvalue)
- [12. Operational vs installed state](#12-operational-vs-installed-state)
- [13. The power network](#13-the-power-network)
- [14. Device signal connections (the `Electrical` GPM)](#14-device-signal-connections-the-electrical-gpm)
- [15. Rendering](#15-rendering)
- [16. Lighting](#16-lighting)
- [17. Ship serialization (templates and saves)](#17-ship-serialization-templates-and-saves)
- [18. Writing a ship back into a save](#18-writing-a-ship-back-into-a-save)
- [19. Obtaining a ship in-game (brokers, chargen)](#19-obtaining-a-ship-in-game-brokers-chargen)
- [Appendix A — Quick reference](#appendix-a--quick-reference)
- [Appendix B — Ported / deferred / excluded](#appendix-b--ported--deferred--excluded)

---

## 1. Working with the decompile

The decompiled source is **not** committed (IP hygiene — no decompiler output
lives in this repo). Regenerate on demand (~7 s; `ilspycmd` is an installed global
dotnet tool):

```powershell
ilspycmd -p -o <scratch-dir> "C:\Program Files (x86)\Steam\steamapps\common\Ostranauts\Ostranauts_Data\Managed\Assembly-CSharp.dll"
```

Cite by **`Type.Method`**, which is stable; line numbers shift when the game
updates. The members that matter most:

| File | Members |
|---|---|
| `Item.cs` | `CheckFit` (placement law), `SetData` (footprint + sprite scale), `RotateCW`, `SetSpriteSheetIndex` (autotile) |
| `CondTrigger.cs` | `Triggered` (the trigger evaluator; full semantics) |
| `Loot.cs` | `GetLootNames` (how a socket loot resolves to condition names) |
| `TileUtils.cs` | `RotateTilesCW`, `GetSurroundingTiles`, `PadTilemap` / `TrimTiles`, `GetAirlockBounds`, `GetPoweredTiles` |
| `Ship.cs` | `UpdateTiles`, `CreateRooms`, `CalculateRating`, `GetShipValue`, `GetTileIndexAtWorldCoords`, `AddCO` / `AddICO` |
| `Room.cs` / `RoomSpec.cs` | `CreateRoomSpecs`, `Matches`, `CalculateRoomValue` |
| `CondOwner.cs` | `TLTileCoords` (item centre → top-left tile), `GetBasePrice`, `BreakIn` |
| `Visibility.cs` | `LateUpdate` (the light shadow-mesh geometry) |

**Re-verification checklist after a game patch:**
1. Re-decompile; diff `CheckFit`, `SetData`, `RotateCW`, `CalculateRating`
   (cutoffs), `CreateRooms`, `RoomSpec.Matches`, `GetBasePrice` for logic changes.
2. Re-run the parity corpus (`ParityTests`, `GameDataTests`) — it asserts the
   real-data facts this file documents, so drift surfaces as a parity regression.
3. Bump `GameEnv.VerifiedGameVersion` once green.

---

## 2. The data model

Game data lives under `…/StreamingAssets/data/<type>/` as JSON arrays of objects,
each keyed by **`strName`**. The game loads every folder listed in
`loading_order.json` in order; a later-loaded object with the same `(type,
strName)` **replaces** the earlier one, **whole-object** (no field merge). `core`
loads first. Field names use Hungarian prefixes: `str` string, `n` number, `b`
bool, `a` array, `map` key/value list, `json` nested object.

> **Ported in Ostraplan:** `DataIndex` (effective-data resolution, adapted from
> Ostrasort), `Catalog`.

### The palette join

A build-menu entry becomes a placeable part by a chain of lookups
(`Installables.dictJobBuildOptions` / `GUIPDA.ShowJobOptions`):

```
data/installables   (strJobType == "install", strBuildType ∈ HULL HVAC POWR SENS CTRL FURN APPS MISC)
        │  strStartInstall
        ▼
data/condowners  ── or via ──▶  data/cooverlays  (strCOBase → the real condowner; the overlay swaps sprite + friendly name)
        │  strItemDef
        ▼
data/items   ── geometry: nCols, aSocketAdds / Reqs / Forbids, strImg, bHasSpriteSheet, ctSpriteSheet
```

- `strStartInstall` names the **condowner** to place, resolved directly or through
  a cooverlay whose `strCOBase` is the real condowner (`DataHandler.LoadCO`'s
  fallback). Roughly **half** of the ~330 build-menu entries are cooverlay skins.
- State variants are separate menu entries: doors install as `…Open`, beds and
  appliances as `…Off`.
- A naive `items[strStartInstall]` lookup finds only ~157 of ~330 parts — the
  condowner/cooverlay hop is mandatory.

> **Ported in Ostraplan:** `Catalog.Build` (palette), `Catalog.Lookup` /
> `Catalog.ResolveDef` (on-demand resolution of any placed def, including the
> ~half of a real ship that is not in the buildable palette: raw hull,
> `Compartment`, RCS clusters, sensors).

---

## 3. Conditions and loots — the tile vocabulary

Everything about placement and rooms is written in **conditions** accumulated on
tiles, and conditions are produced by **loots**.

### Loot mechanics (`Loot.cs`)

- A loot carries `aCOs` (its own payload: strings like `"IsWall=1.0x1"` that name
  condition **names**) and `aLoots` (nested loot names).
- `GetLootNames()` flattens the `aCOs` cond-names **plus** the recursive expansion
  of `aLoots`. Socket masks use deterministic single-unit loots (`chance 1.0`,
  `count 1`), so this is a plain set-union with no randomness.
- `"Blank"` (and any unresolved name) resolves to empty — an **unconstrained**
  cell.

> **Ported in Ostraplan:** `Catalog.LootConds`.

### Tile-condition accumulation (`Ship.UpdateTiles`)

Each tile holds a condition multiset (`Tile.coProps`). On place or remove,
**every overlapping part** adds or subtracts its per-cell `aSocketAdds` loot's
conditions (±1). Presence means "count > 0". State variants (door Open vs Closed)
are *different item defs with different adds*; the installer places the
`strStartInstall` def.

> **Ported in Ostraplan:** `TileConds`.

### The `TIL*` loot table (from `data/loot`)

**Adds** — what a part contributes to its own footprint tiles:

| Loot | Expands to |
|---|---|
| `TILFloor` | `IsFloor`, `IsFloorSealed` |
| `TILWallAdds` | `IsObstruction`, `IsWall` |
| `TILFixtureAdds` | `IsFixture`, `IsObstruction` |
| `TILExtFixtureAdds` | `IsFixture`, `IsFixtureExt`, `IsObstruction`, `IsWallDeco` |
| `TILSubfloorAdds` | `IsSubTile` *(walkable sub-floor: under-floor storage, no solid body)* |
| `TIL2DeckAdds` | `IsFixture`, `IsSubTile`, `IsObstruction` *(the visible tank body, above-floor)* |
| `TILPowerConduit` | `IsPowerConduit`, `IsPowerPath` |
| `TILPowerFixtureAdds` | `IsFixture`, `IsObstruction`, `IsPowerPath` |
| `TILFloorFixture` | `IsFloorSealed`, `IsFixture` *(buildable floor fixture — see §5)* |

**Req / forbid** — what a cell tests for (same expansion, different intent):

| Loot | Expands to | Used as |
|---|---|---|
| `TILFloor` | `IsFloor`, `IsFloorSealed` | **req**: both must be present |
| `TILWall` | `IsWall` | **req** |
| `TILObstruction` | `IsFixture`, `IsFixtureExt`, `IsObstruction`, `IsItemTile`, `IsFloorFlex` | **forbid**: fail if *any* present |
| `TILSubfloorForbids` | `IsSubTile` | **forbid** |
| `TIL2DeckForbids` | `IsFixture`, `IsSubTile`, `IsObstruction`, `IsItemTile`, `IsWallDeco`, `IsFloorFlex` | **forbid** |

The condition vocabulary that drives structural logic: `IsFloor` /
`IsFloorSealed` / `IsFloorFlex` (floor), `IsWall` / `IsPortal` (walls and doors),
`IsObstruction` (solid/blocking), `IsFixture` (furniture/appliances), `IsSubTile`
(sub-floor), `IsPowerConduit` / `IsPowerPath` (power runs), `IsDockSys` /
`IsInstalled` (docking ports).

---

## 4. Footprints and sprites — two independent sizes

The game keeps two independent sizes for an item, and they must not be conflated:

| Size | Formula | Source | Used for |
|---|---|---|---|
| **Socket / placement grid** | `nWidthInTiles × nHeightInTiles` = `nCols × (aSocketAdds.Count / nCols)` | `Item.SetData` | CheckFit, ghost/selection extent, tile accumulation |
| **Visual sprite size** | `vScale = round(texturePx / 16)` tiles, min 1 | `Item.SetData` | how large the sprite draws, centred on the footprint |

For most parts these are equal (a 1×1 wall is 16×16 px; a 3×5 bed is 48×80 px).
**The large fuel tanks are where they diverge, and it is not a data error:**

- `ItmCanisterLH02` (D2O), `ItmCanisterLHe01` / `ItmCanisterLHe02` (He3): `nCols =
  7`, 49 adds, so a **7×7 socket grid** but a **48×48 px = 3×3 sprite**.
- The socket grid is an **abstraction of sub-floor storage**: the outer ring adds
  `TILSubfloorAdds` (walkable sub-floor); only the **centre 3×3** adds
  `TIL2DeckAdds` (the solid, visible tank). `aSocketReqs` is `TILFloor` across the
  whole inner 7×7, so the game genuinely requires a **7×7 sealed-floor pad** to
  place one.

The correct rule: render the sprite at `vScale` centred on the footprint, but keep
the **footprint at the socket grid** for placement. Shrinking the footprint to 3×3
would allow placement in a gap the game refuses — a false positive.

A cell is **under-floor** when its adds mark `IsSubTile` **without**
`IsObstruction`.

> **Ported in Ostraplan:** `Defs.ItemDef` (footprint), `SpriteCache.SpriteTiles`
> (sprite), `Catalog.IsUnderFloorLoot`.

---

## 5. The placement law (`Item.CheckFit`)

For a candidate `(part, anchor, rotation)`:

1. **Ring grid.** `aSocketReqs` / `aSocketForbids` are per-cell loot names over the
   **(W+2)×(H+2) ring** (footprint plus a 1-tile border), row-major, border
   included. `aSocketAdds` covers only the W×H footprint. Ring cell `(r,c)` maps to
   world tile `(anchorX − 1 + c, anchorY − 1 + r)`.
2. **Cell test — presence only.** CheckFit builds a *throwaway* `CondTrigger {
   aReqs = reqLoot.GetLootNames(), aForbids = forbidLoot.GetLootNames() }` (default
   `bAND = true`) and calls `Triggered`. Because these are trivial triggers of
   condition names, only the presence path runs: **every req condition present
   (count > 0), no forbid condition present**. The full `CondTrigger.Triggered`
   machinery — count multiplicity, nested `aTriggers`, `bAND = false` OR-logic,
   `strHigherCond` / `aLowerConds`, `fChance` — is **unreachable from placement**
   (it is reached from room certification, §9).
3. **Off-ship rule.** A ring cell with no accumulated conditions (empty space)
   **passes iff it has no requirement**. This is how "must attach to structure" /
   "needs floor beneath" is encoded. An existing-but-empty tile behaves identically.
4. **Rotation.** 90° steps rotate the req/forbid ring masks and the adds mask.
   `TileUtils.RotateTilesCW(cells, W+2)` is a plain clockwise tile rotation.
   **Sheet items (walls/floors, `bHasSpriteSheet`) never rotate** — `Item.RotateCW`
   returns early for them.
5. **Airlock envelope.** No ring cell may fall beyond the mating face of the
   **primary** docking port. The game derives the envelope **once, from
   `aDocksys.FirstOrDefault()` alone** (before the ring loop), not per port — see
   §6 for why that first port is always the Primary when one exists.
6. **Self-exclusion.** When re-checking an *already-placed* part, its own tile
   contribution must be subtracted first — walls and fixtures add `IsObstruction`
   **and** forbid `TILObstruction` on their own footprint, so they fail against
   themselves otherwise.
7. **Excluded predicates.** Several in-game-only tests are part of `CheckFit` but
   cannot exist in a planner: crew proximity / line-of-sight
   (`GUIInventory.instance.Selected` + `Visibility.IsCondOwnerLOSVisible`),
   docked-ship connection (`TileUtils.WouldConnectShips`), and station **build-zone**
   permission (whether a tile may be built on when it belongs to a station). This
   is distinct from ship **zones as data**, which are modelled (§17).

> **Floor fixtures are buildable surfaces.** The common `TILObstruction` forbid
> mask expands to include `IsFixture`. But an under-floor storage bin or rack
> (`ItmRackUnder01`, `ItmStorageBinFloor…`) tags its walkable tiles `IsFloorSealed`
> + `IsFixture` (via `TILFloorFixture`) and **never** `IsObstruction`, and the game
> lets you build on — and reach an adjacent fixture across — that floor. So the cell
> test does **not** let `IsFixture` trip the forbid on a tile that also carries
> `IsFloorSealed`; a genuine `IsObstruction` still blocks.

> **Ported in Ostraplan:** `CheckFit`, `ProblemScan`, enforced at the single
> placement choke point `ShipCanvas.TryPlacePose`. `GridMath.Rotate` reproduces
> `RotateTilesCW` exactly.

### Worked examples (real data)

- **`ItmWall1x1`** — 1×1, `aSocketReqs` all Blank (free-standing, like the game),
  centre forbid `TILObstruction` (won't stack on an obstruction). Sheet item
  (`ctSpriteSheet = TIsWall`).
- **`ItmBed01Off`** — 3×5. Reqs: `TILFloor` across the footprint + `TILWall` down
  the **right** border (the headboard). Forbids: `TILObstruction` on the footprint
  and the left border. Adds `TILFixtureAdds` (so it forbids the obstruction it will
  itself add — hence self-exclusion).
- **`ItmCanisterLH02`** — 7×7 socket grid, 3×3 sprite (§4).
- **`ItmDockSys03Closed`** — the buildable "Secondary Exterior Airlock", 7×2,
  free-standing (all-Blank reqs).

### Construction is order-dependent; existing hull is never re-validated

The game validates each placement against the ship's *current* state during
construction, so in-game legality is order-dependent (floors, then walls, then
fixtures). It **never re-validates existing structure**. A real ship legally
contains structure that a from-scratch build order would refuse — for example hull
baked beyond where a later-added airlock's face falls, or a fixture stacked on a
floor whose own forbid mask a final-state re-check would trip. The game does not
care, because the structure was already there.

Consequently, imported structure must not be re-validated against the placement
law; only genuinely new construction (a newly placed or moved part) is.

> **Ported in Ostraplan:** imported parts are marked **given** (`Placement.IsGiven`)
> and skipped by `ProblemScan`; moving or rotating a given part clears the flag.
> A **constructibility pass** re-simulates a canonical floors→walls→rest build
> order with incremental CheckFit and warns (only) if some part never becomes
> placeable.

---

## 6. Docking and airlocks

- A ship needs **≥1 installed docksys** or it can never hard-dock (`Ship.aDocksys`
  collects COs that trigger `TIsDockSysInstalled`). 42 core templates have none —
  no crash, just unmateable.
- `TIsDockSysInstalled` reqs are `[IsDockSys, IsInstalled]` and **all** must match.
  Matching *any* (for example via `IsInstalled` alone) would flag every installed
  part.
- **No rule ties an airlock to the origin `(0,0)`.** Zero of 147 core templates
  with a port place one there, and the Babak has two. The "primary"
  (`Ship.PrimaryDockingPortID`, persisted `strPrimaryDockingPortID`) is a
  runtime-cyclable selection that defaults to the first port.
- The special **Primary** airlock is `ItmDockSys02Closed` (`strNameShort` literally
  "Primary Airlock"): `IsIndestructable`, `IsShipSpecialItem`, and **no install
  job**, so players can neither build nor remove one. The buildable port is the
  **Secondary**, `ItmDockSys03Closed`.

### The one real positional rule: no construction beyond the primary's mating face

A port's face comes from its `DockA → DockB` arrow (condowner `mapPoints`, pixels
around the item centre, **+y up**; `DockA` at the door, `DockB` outside the hull).
The face lies at `DockA ± |arrow| / 2`. It sets **exactly one** bound component in
the direction it faces (`DockB.y > 0.5 → max.y`, `< −0.5 → min.y`, `x > 0.5 →
max.x`, `< −0.5 → min.x`), leaving the other three at ±∞. So one port is a
**half-plane**: bounded on its facing axis, unbounded perpendicular. A blocked face
is also why a port can never mate with a station collar.

> **Ported in Ostraplan:** `ProblemScan.TryGetFace` (face math), enforced as a
> CheckFit bound (§5.5) and drawn as red hazard stripes. The game is y-up,
> Ostraplan documents are y-down; conversion happens at the boundary
> (`ProblemScan.Transform`).

### Only one port bounds construction, and `IsTypeB` decides which

`Item.CheckFit` reads `aDocksys.FirstOrDefault()`. `Ship.AddCO` files a
**non-TypeB** port with `aDocksys.Insert(0, …)` and a **TypeB** port with
`aDocksys.Add(…)`, so any non-TypeB port outranks every TypeB one regardless of
item order. In core data the only *installed* non-TypeB ports are
`ItmDockSys02Closed` / `02Open` (the **Primary**); every **Secondary**
(`ItmDockSys03*`) carries `IsTypeB = 1.0x1`, and `MooringPort` is non-TypeB but not
`IsInstalled`, so it never registers.

⇒ The Primary bounds; a Secondary never does while a Primary exists. That is what
makes an *internal docking bay* (a Secondary facing into the hull) legal in game. A
design with *only* Secondaries has one at `aDocksys[0]`, and it then does bound.

Do **not** confuse `TileUtils.GetAirlockBounds` with the construction rule. It runs
the same face math but over **all** `aDocksys`, and the game only ever calls it from
`Ship.SpawnMeat` / `Meat.cs` — it decides where a **meat blob** may spawn and
spread. (`GUIInventoryItem` also bounds by all ports, for hand-dropping a loose item
from inventory.) The construction authority is `Item.CheckFit`, which uses the
single primary port.

### Buying a ship docks it at purchase time — it must expose its ports while shallow

Both broker paths spawn the for-sale ship `Ship.Loaded.Shallow` at the template's
baked `objSS`, hidden and undocked (`Trader.AddNewShips` for the regular list,
`GUIShipBroker.AddSpecialOfferShip` for the Special Offer). On Buy,
`GUIShipBroker.OnPurchaseConfirm` transfers ownership and then docks the ship to the
broker's station:

- `CrewSim.DockShip` when the station is deep-loaded (re-spawns the ship `Full`, so
  `Ship.AddCO` rebuilds `aDockingPorts` from the items); but
- `shipByRegID.Dock(station)` on the **shallow** branch (`station.LoadState <=
  Shallow`), which docks the still-shallow ship without a full re-spawn.

A shallow ship reads its ports **only** from `json.aDockingPorts` (the `Ship` load
sets `aDockingPorts = json.aDockingPorts`; only the `>= Edit` path `Clear()`s and
rebuilds them from items). If a dock fails (no mate found, or the shallow ship
exposes no ports) the game does **not** reposition the ship: it is left at its
`objSS`, and a ship far from the ATC also drops out of the P.A.S.S. ferry list
(`GUIPDAFerry.ShowRequest` distance filter).

**Therefore a spawnable `data/ships` export must bake `aDockingPorts` (installed
docksys item strIDs, primary / non-TypeB first, TypeB last) and
`strPrimaryDockingPortID`.** Core templates carry them.

> **Ported in Ostraplan:** `ShipExport` bakes both. `ItmDockSys02Closed` is the
> non-TypeB primary, `ItmDockSys03Closed` is TypeB.

### A boarded ship needs `Boarding` / `NotBoarding` spawn points

A person delivered to a ship — off the P.A.S.S. ferry, or via a skywalk — is placed
at the ship's `Boarding` person-spawner, and an NPC already assigned to the ship at
its `NotBoarding` one. Both are **`SysLootSpawner`** objects (an `IsSystem` def) in
the template's **`aShallowPSpecs`** array (a *different* array from `aItems`),
distinguished by their `aGPMSettings` prop map: `strType: "Pspec"`, `strLoot:
"Boarding"` / `"NotBoarding"`, `strRange: "1"`. `Boarding` / `NotBoarding` are
**personspecs** (`strCT: TIsBoarding` / `TIsNotBoarding`). **150 of 192 core
templates carry `aShallowPSpecs`** (the 42 without are stations, buoys, and rocks).

Without these spawners, arrivals land at a fallback tile (frequently outside the
hull). A spawnable export must bake both on pressurized (non-void) interior tiles.

> **Ported in Ostraplan:** `ShipExport.BuildBoardingSpawners` (Boarding on the
> interior tile nearest the primary airlock, NotBoarding nearest the interior
> centroid). The save-edit path preserves the original `aShallowPSpecs` verbatim.

---

## 7. The coordinate model

An `aItems` entry's `(fX, fY)` is its footprint **centre** (`CondOwner.TLTileCoords`):

- top-left tile world = `(fX − (W/2 − 0.5), fY + (H/2 − 0.5))` using the **rotated**
  W×H;
- tile `(col, row)` with `col = round(worldX − vShipPos.x)`, `row = −round(worldY −
  vShipPos.y)`;
- index `col + row·nCols`.

`fRotation` is **CCW** (Unity Z-euler); a CW tile rotation must negate it, or the
asymmetric 90°/270° socket patterns are misplaced. Only top-level `aItems` are
placed on the grid; contained or slotted items (`strParentID` /
`strSlotParentID`) are not (they carry no wall/floor conds).

The **inverse** (writing a grid part back to an item centre), with `vShipPos =
(0,0)` so the offset terms vanish, for a part at top-left `(col, row)` with rotated
footprint `(wr, hr)` and rotation `Rot`:

- `fX = col + (wr/2 − 0.5)`, `fY = −(row + (hr/2 − 0.5))`, `fRotation = Norm(−Rot)`.

> **Ported in Ostraplan:** `ShipGrid.ToRot` (negation), `ShipGrid.TemplateTile`
> (shared forward/inverse mapping), `ShipExport` (write). Verified 622/622 walls on
> the Babak.

---

## 8. Rooms and airtightness (`Ship.CreateRooms`)

A BFS flood fill with **4-connectivity** (N/W/E/S). **`IsWall` is the only flood
boundary.** Portals never seed.

A **door** is a 5×1 item — `[wall, wall, portal, wall, wall]` — whose four side
cells are always `IsWall` (they seal the doorway into the wall line, open *or*
closed). Only its **centre** cell differs by state:

- **Open** (`TILPortalOpen` → `IsPortal`, no `IsWall`): a walkable portal that
  flood-*sinks* into the first room reaching it and never expands.
- **Closed** (`TILPortalClosedStuck` → `IsPortal` + `IsWall`): a hard fill boundary.

Either way the door splits the hull into the **same two rooms** with the **same**
airtightness — door state is cosmetic to the room and rating law. The centre tile is
then filed into a compartment: an open one is already claimed by the fill; a closed
one is assigned by `AssignPortals` to a **non-void cardinal-neighbour room** (never
the exterior — a floored doorway must not read as a hull breach). `AssignPortals` is
geometry-based: for a straight door, `RoomA` / `RoomB` are just the two cardinal
neighbours perpendicular to it, so the assignment needs no world-point lookup.

A room is **Void** if any member tile lacks `IsFloorSealed`, **or** a cardinal
neighbour is off-grid (which also marks it **Outside** / `bOuter`). Void is fixed
during the fill, so a door tile filed afterward never voids a sealed room. Volume =
`0.25599998 × tileCount`.

**Exterior rooming is asymmetric and trim-dependent.** The game leaves the far empty
margin around a small ship unroomed, bounded by `TrimTiles` rather than a clean
bounding box. The Outside room is Blank and never counts toward the rating.

> **Ported in Ostraplan:** `ShipGrid`, `RoomBuilder.Build` (+ `AssignPortals`).
> Parity is lenient on exterior-void over-claim (harmless); interior compartments
> must match exactly.

---

## 9. Room certification (`RoomSpec.Matches`)

A room certifies as the highest-`nPriority` spec that matches, else `Blank`. A spec
matches iff:

- `bAllowVoid == room.Void`;
- tile count within `[nMinTileSize, nMaxTileSize]` (−1 = unbounded);
- no member fires any `aForbids`;
- every `aReqs` is satisfied **with multiplicity** (`"TIsChairInstalled=1.0x4"`
  needs 4; each match consumes `StackCount`, always 1 for a planner).

Floor-grate members (`IsFloorGrate`) are skipped; only installed parts count. Reqs
and forbids are **condtrigger names** evaluated against each part's `aStartingConds`
by `CondTrigger.Triggered`: the `bAND` path (reqs / forbids / nested `aTriggers`)
and the `bAND = false` OR path (`aTriggersForbid`, then any req / `aTrigger`; e.g.
`TIsRoomCargo` is an OR of storage-bin / rack). `fChance` / `strHigherCond` are
unreachable from room specs and safe-pass.

> **Certification tests CondOwner conds, not tile conds.** A room's requirements are
> evaluated against the member parts' `aStartingConds`, with multiplicity from the
> spec's `xN`.

> **Ported in Ostraplan:** `RoomSpecs` (`RoomCertifier`), `CondEval` (the reachable
> `CondTrigger.Triggered` branches).

### Room membership and the `"use"`-point fallback (`Tile.AddToRoom`)

A part joins a room at its **anchor (centre) tile**. But when that tile is a ship
tile with **no** room, the game retries at the part's **`"use"` map point** and joins
*that* room. This is how every **wall-embedded** part participates in certification
and value: wall storage bins, sensors and antennas (poking through the hull, centre
on the wall cell), coolers, ship weapons, cargo pods, cladding — roughly 87 core
defs. Anchor-only membership silently drops them.

Both lookups pass `TIsShipTileOrSub` (an OR of `IsFloor` / `IsFixture` /
`IsObstruction` / `IsPortal` / `IsWall` / `IsSubTile`), so a use point facing
**empty space** (an outward-rotated wall part) rescues nothing. The air pump's use
point is `0,0` — its own wall tile — so a wall-embedded pump joins **no room and
contributes $0 to ship value, in-game too**.

> **Ported in Ostraplan:** `RoomBuilder.AssignParts` + `ShipGrid.MapPointTile`
> (map-point px rotated via `GridMath.MapPoint`, world coord rounded away-from-zero
> like `MathUtils.RoundToInt`).

### Diagnostics

`Matches` returns only the *first* failure, which hides the common case "every
requirement met but a forbidden item is present". For example, `LuxuryQuarters`
forbids `TIsCanister` (an OR that includes installed RTAs) plus batteries, hatches,
toilets, and reactor cores; parking an O2/N2 RTA or a battery in the bedroom
silently blanks it. A useful diagnosis assesses reqs and forbids independently and
names the blocking part.

> **Ported in Ostraplan:** `RoomCertifier.Diagnose` + `ShipAnalysis.NearMisses`.
> Note: "add a reactor core" satisfies the Reactor spec (≥4 sealed tiles +
> `TIsReactorIC`) for nearly every room, so suggestions whose only missing req
> includes `TIsReactorIC` are suppressed as noise.

---

## 10. Ship Rating (`Ship.CalculateRating`)

Six slots; the cutoffs are hardcoded in the DLL (unit-pinned, version-sensitive):

| Slot | Meaning | Rule |
|---|---|---|
| 0 | Epoch | Timestamp at rating time |
| 1 | Condition A–E | Mean of `clamp01(1 − StatDamage/StatDamageMax)` over installed parts. Cutoffs: ≤0.5 E, ≤0.8 D, ≤0.95 C, ≤0.99 B, else A. A pristine ship grades **A** |
| 2 | Room count | Number of rooms whose matched spec ≠ `Blank` |
| 3 | Maneuver | `mass / fRCSCount`, where `fRCSCount = Σ StatThrustStrength` over installed RCS clusters (`TIsRCSClusterAudioEmitter`) and `mass = Σ StatMass`. 0 RCS → `O`; else `<300 A, <500 B, <750 C, <1500 D, else E` |
| 4 | Size class | Grid area `nCols·nRows`: `<250 Small, <900 Medium, <1600 Lunamax, <2300 Ceresmax, <3000 Titanmax, <3700 Very Large, else Ultra Large` |
| 5 | Unused | Pass-through |

> **Ported in Ostraplan:** `Rating`.

---

## 11. Ship value (`Ship.GetShipValue`)

`GetShipValue` = Σ room `RoomValue`, multiplied by **3** when the ship has a
registered O2 pump (see below). `Room.CalculateRoomValue` = Σ member `GetBasePrice()
× fValueModifier`. Membership is the `AddToRoom` chain of §9, so a part in no room (a
wall-embedded air pump, use point `0,0`) is worth **$0 in-game too**.

**Void rooms count.** Neither `CalculateRoomValue` nor `GetShipValue` filters
`bVoid`; the 192 baked core void rooms carry non-zero `roomValue` (an unsealed engine
bay can be worth hundreds of thousands — its engines). There is **no** wall- or
atmosphere-specific per-item multiplier: the ×3 is one global flag over the whole
sum, which is why single-item experiments appear to "show" ×3 on whatever part was
just added.

### `GetBasePrice` decomposed

- `StatBasePrice` (falling back to `StatMass` when 0), damage-scaled on damaged
  parts.
- **+ gas/fuel contents** (`GasContainer.GetTotalGasValue`): each `StatGasMol<gas>` ×
  molar mass (a hardcoded kg/mol switch in `GetGasMass`; a gas absent from it —
  notably He3 — weighs 0) × the **data-driven price/kg** from the `GasPrices` loot
  (`O2 = 13.2`, `N2 = 4.10`, `He3 = 7.73`, `H2 = 2.43`/kg …), plus `StatLiqD2O ×
  price("H2")` and `StatSolidHe3 × price("He3")`. An O2 RTA spawns full — 13,373 mol
  ≈ **$5,648 of O2** on a $410 shell — so ignoring contents visibly undercounts
  canister builds.
- **× 1.25 only when the CO carries `IsPristine`**, which a designed ship's parts
  **never** have. There are exactly three `IsPristine` write-sites in the DLL and
  zero condowner defs carry it in `aStartingConds`. It is **added** by `Ship.BreakIn`
  (first Edit-load of Derelict/Damaged/Used ships, a 2.5% roll per solid undamaged
  part — 25% with the player's `IsDueBonusDerelict` flag) and `Trader.AddNewItems`
  (kiosk stock **items**); **removed** by `DestCheck` the moment a part takes
  `StatDamage`. Install never grants it: the finished part is spawned fresh from
  `strStartInstall`'s def. A built or exported ship therefore has uniformly
  markup-free parts.

### Broker factors

A non-derelict sale is exactly `GetShipValue × DiscountBuy` and a vendor listing `×
DiscountSell` (`GUIShipBroker.GetQuotedPrice`; the `1.1 − fBreakInMultiplier` haircut
applies **only** to derelicts). Core ship-broker kiosks carry `DiscountBuy = 0.8`,
`DiscountSell = 1.2` (`loot.json` `CONDTraderDiscount*ShipBroker*`).

### The ×3 "atmo bonus" is a fed pump, not merely a pump

`Ship.AddICO` registers a pump into `aO2AirPumps` only when `ctAirPump`
(`TIsAirPump02Installed` = `IsAirPump` + `IsInstalled`) fires **and**
`ShipStatus.GetO2UnderPump` finds an installed O2 RTA (`TIsRTAO2Installed` =
`IsVesselO2` + `IsRTA` + `IsInstalled`) with `StatGasMolO2 > 0` at one of the pump's
`GasInput` map-point tiles. (`ItmRTAO2` starts full at 13,373 mol; only the running
`ItmAirPump02OnG` even *has* a GasInput point — the Off pump can never register.) The
bonus is a **flag** (`aO2AirPumps.Count > 0`, shallow `nO2PumpCount > 0`): a second
fed pump adds nothing.

> **Ported in Ostraplan:** `ShipValue` (`PartValue`, `CountO2Pumps`), `Catalog.GasPrices`.
> A shallow-loaded spawn never re-derives the pump count, so an export bakes it as
> `nO2PumpCount`.

---

## 12. Operational vs installed state

The game installs most powered devices in their **Off** state (`strStartInstall =
Itm…Off`, carrying `IsOff`) — the state a rating never counts (rating triggers forbid
`IsOff`; `TIsRCSClusterAudioEmitter` is one) and that a player switches on after
loading. A design meant to be rated (and to spawn working) should use the
**operational counterpart** instead.

The on-state naming is not uniform, so the counterpart is found by trying `…On`
(cooler, switch), then `…OnG` (the green/normal state pumps and most alarms use),
then dropping `Off` (RCS, heater, bed) — accepting only a candidate that resolves to
a **real condowner** (non-empty `StartingConds`), is not itself `IsOff`, and shares
the footprint. About 58 of 63 install-Off palette devices pair cleanly; the rest
(colour/alert alarms, transponder, the reactor's `Ignition`, open/closed vents) are
ambiguous and left installed.

> **The condowner requirement is load-bearing.** Some devices ship a bare **item**
> for a glow/animation state with **no condowner** — notably
> `ItmFusionReactorCore01On`: identical 5×5 sockets to the Off form, but no CO, so it
> resolves with an internal-name label, `StatMass` / `StatBasePrice` = 0, and none of
> its `IsFusionReactorCore` conds. The game never *installs* such an orphan. Handing
> the reactor core to it would let placement succeed (identical sockets) while the
> core counts as **weightless** in the maneuver rating and contributes nothing to
> value. Requiring a real condowner leaves the reactor core as the installable
> `ItmFusionReactorCore01Off`.

The reactor build chain (2 field coils + 4 reactor segments make one core placement,
then components attach to the core's inputs) is enforced entirely through socket
loots: the coils' centre cell forbids `TILFloorFixtureForbids` (must be
vacuum-exposed, no floor), the core's centre requires the coils'
`TILFusionFieldCoilsFixtureAdds`, and each component's attach cell requires the
core's `TILFusionReactorCoreFixtureAdds`.

> **Ported in Ostraplan:** `Catalog.PreferPoweredState`.

---

## 13. The power network

`TileUtils.GetPoweredTiles` is a connectivity graph (no draw/generation balance — the
game authors no per-device draw, so a budget is not derivable).

- **Sources** = installed COs firing `IsPowerGen` **or** `IsPowerStorage` **or**
  `IsRechargingContainer` (all with `IsInstalled`, not `IsOverrideOff`) that carry a
  **`PowerOutput`** map point — in core, the batteries (`ItmBattery02*`) and reactor
  cores (`Itm…Ignition`).
- From each source's `PowerOutput` tile, a **4-cardinal BFS** spreads over tiles with
  **`IsPowerPath`** (contributed by conduits via `TILPowerConduit` and powered
  fixtures via `TILPowerFixtureAdds`). A tile only propagates if it *itself* has
  `IsPowerPath`, so the seed lights only if wired.
- Reached tiles are **powered**; leftover `IsPowerPath` tiles the flood never touches
  are **orphaned** runs. A wired device is **connected** when one of its input-plug
  tiles lands on the powered set (its own footprint carries `IsPowerPath`).

### Connector points (the build-cursor nubs)

A device names a `JsonPowerInfo` via its condowner's **`jsonPI`** field
(`data/powerinfos`, `DataHandler.dictPowerInfo`); that power-info's **`aInputPts`**
are the map-point names where it draws power (`PowerSource` / `PowerA` / `PowerB` /
…). The game draws a `GetPowerInputGridSprite` at each `aInputPts` point (unless the
CO has `IsPowerInputIgnore`) and a `GetPowerOutputGridSprite` at `PowerOutput`.

**Key link:** `jsonPI` is a condowner field whose value is a *power-info* name, **not**
the condowner's own `strName` (0 of 126 overlap) — resolve through `dictPowerInfo`,
never by CO name. The connector map points are cursor cosmetics, not what carries
power.

> **Ported in Ostraplan:** `PowerNetwork`; connectors on `PartDef.PowerInputPoints` /
> `PowerOutputPoint`.

---

## 14. Device signal connections (the `Electrical` GPM)

The game's **signal-wiring** system (sensor → alarm/pump/light, controllers, logic
gates) is distinct from the power network. It is driven by an **`Electrical`** GPM
component (`strGPMKey = "Electrical"`) attached to every condowner whose
`aStartingConds` carry **`IsSignalable`** (alarms, air pumps, sensors, switches,
lights, …).

- **The model is directional and ID-based, not geometric.** `Electrical` holds
  `outputConnections` and `inputConnections`, each a `Dictionary<string,
  ElectricalConnection>` **keyed by the connected item's `strID`**.
  `Electrical.SetUpConnection(co)` adds `co.strID` to *this* device's
  **`outputConnections`** (so this device **drives** `co`). So **A→B means A's
  `outputConnections` lists B and B's `inputConnections` lists A.** There is **no**
  distance / adjacency / conduit requirement in the persisted model — a connection is
  a pair of `strID` references. (In game it is *created* with a rewire tool
  (`IsToolWireCutter`), whose interaction has its own proximity rules, but the stored
  connection is pure ID.)
- **Runtime semantics.** A wired sink gains **`IsConnected`** (via `TUpConnected`) and
  **`IsSignalledOn`** (via `TUpSignalled`); **`TIsConnctedSignalledOff`** =
  `IsConnected` ∧ ¬`IsSignalledOn` fires the device's power-info `strShutDownCT`, i.e.
  a connected device is held off until its source signals it on. `gate` (a
  `GateMode`), `positives`, and the threshold slider are per-device *logic* (AND / OR
  / threshold over inputs), not connection legality.
- **Persist shape.** The wiring rides on each item's **`aGPMSettings`** entry `{
  "strName": "Electrical", "dictGUIPropMap": [ …flat key/value… ] }`. A connections
  value is a **comma-joined list of `<targetStrID>#<signalType>#<status>#<name>`**
  entries (e.g. `…#0#true#N2 Pressure Alarm`).
- **Legality.** Both endpoints must be **installed** parts carrying `IsSignalable`, on
  the same ship; a device may not connect to itself, and duplicate links collapse.
  That is the whole rule — there is no geometric constraint.

> **Ported in Ostraplan:** `DeviceLink` (a directed part-id pair), `DeviceLinks`
> (validity), baked on export into each wired item's `Electrical` GPM
> (`ShipExport.WireDeviceLinks`). Gate/threshold logic is out of scope — that is the
> in-game signal box's job.

---

## 15. Rendering

- **Z-order.** `nLayer` is `0` for **every** item in the data; the game does not layer
  items by `nLayer`, it Y-sorts sprites over a separate floor tile-layer. A single-pass
  renderer instead ranks each part by the conditions it contributes: **floor** (`IsFloor`
  / `IsFloorSealed` / `IsFloorFlex`) < **wall/door** (`IsWall` / `IsPortal`, checked
  first — a door also seals floor) < **fixtures & the rest** < **power conduit**
  (`IsPowerConduit`, thin runs on top). Hit-testing returns the topmost layer.
- **Sprite draw.** Non-sheet sprites draw at `vScale` size centred on the footprint
  (§4). Sheet items draw per tile.

> **Ported in Ostraplan:** `Catalog.RenderLayer` → `ShipDocument.DrawOrder`;
> `HitTestStack` drives the right-click layer picker.

### Autotiling (`Item.SetSpriteSheetIndex`)

Sheet items (`bHasSpriteSheet` + `ctSpriteSheet`) pick a sheet cell from the 4 cardinal
neighbours whose tile conds trigger `ctSpriteSheet`:

- mask bits **N = 8, W = 4, E = 2, S = 1** →
- the fixed 16-entry `Item.SpriteSheetIndices` table →
- a cell index whose **rows count from the texture bottom** (Unity UV origin; a WPF
  renderer flips the row).

The core wall sheet is 64×64 = a 4×4 grid of 16 px tiles. These constants are exact —
do not "fix" them.

Autotile connectivity honours `bAND`: `TIsWall` is one AND req (`IsWall`), but
`TIsConduitSprite` is `bAND = false`, an **OR** of `IsPowerConduit` / `IsPowerSwitch`
/ `IsPowerJack` — a conduit connects to *any* of them.

> **Ported in Ostraplan:** `Autotile` (+ `TileConds.Triggered` for the presence-only
> path; nested sheet triggers defer to `CondEval`).

---

## 16. Lighting

The game's lighting is a **deferred light pass**, reconstructed from the decompiled
`Visibility` / `Occluder` / `Block` / `Item` / `GameRenderer` and the disassembled GPU
shaders in `resources.assets` (`Sprites/LoSPass`, `Sprites/DefaultAdditive`, and the
combine passes; extracted with UnityPy, DXBC disassembled via `d3dcompiler_47`).

- **The visible ship IS the light accumulation.** In normal play the main camera **does
  not draw the sprite layer at all** (`CrewSim.ToggleAmbientLight` masks the Default
  layer off; the in-game ship editor's "ambient light" checkbox turns it back on). Each
  light's `Visibility` mesh samples the deferred albedo RT and writes `albedo × light`
  into the frame; ambient (`GameRenderer.clrAmbient`) is black and never set. Unlit hull
  = not drawn = black.
- **Occluders are the item defs' `aShadowBoxes`, not `IsWall`.** Format
  `"dx,dy,rx,ry[,glass]"` (tiles from item centre, +y up; half-extents swap on 90°
  rotations via `Block.RotateCW`); `bIsWall` = the item's `aSocketAdds` contains
  `TILWallAdds`. Consequences: **windows (`ItmWallWindow1x1`) are glass, light passes**;
  **thin/aero walls have no boxes, no occlusion**; **open doors block only their 2 end
  caps** (closed = all 5); **beds, LH/LHe canisters (3×3), reactor IC pods, stabilizers,
  aero parts and docksys frames DO occlude** (91 core items carry boxes).
- **Mesh geometry (`Visibility.LateUpdate`).** An angular occluder-merge (sorted
  segments, split/overwrite, same-block neighbour merge) against all non-glass blocks in
  range; a **64-segment rim at `Radius − 0.5`**; then a second pass from a **0.5 minimum
  ring** that merges in the **skirt**: each boundary face extruded outward by its
  thickness — `max(rx, ry)` for wall blocks, 0.5 for the rim, 0 for non-walls — with
  mitred joins, so **light penetrates half a tile into wall faces** (lit walls) and the
  rim reaches the full radius. Touched **non-wall** blocks get their whole footprint quad
  added fully lit (`IlluminateBlock`): a canister is lit but shadows what is behind it.
- **Shading (`Sprites/LoSPass` fragment).** With `u = (pixel − centre)/(2R)`, `F =
  _LightFalloff = 3`, `Z = _LightZ = 0.25`: `L = normalize(−u.x·F, −u.y·F, F·Z)`, `atten
  = 1/(F²(|u|² + Z²) + 0.1)`, `diffuse = max(0, N·L)` where N comes from the **normal
  RT** (`strImgNorm` through `ShaderSetup.NormalPNGtoDXTnm`: `nx = 2·png.r − 1`, `ny =
  2·png.g − 1`, z forced 1, unnormalised). Contribution = `albedo × colour.rgb × colour.a
  × cookie.a × diffuse × atten`, clamped 8-bit per light. `fLightZ` / `nLightFalloff` are
  **not** cosmetic — they are the falloff. Item lights never carry cookies (only crew LOS
  / VFX do).
- **Accumulation is the screen blend.** The pass blends `OneMinusDstColor One` (`acc' =
  src(1 − acc) + acc` per channel): overlapping lights saturate softly toward white,
  never blow out. Glow decals (`strImg` on **every** `aLights` entry, casting or not) draw
  after lighting with `Sprites/DefaultAdditive`: `+ tex.rgb × tex.a²` at native size,
  centred at `ptPos/16` from the item centre. Flicker is damage/power-driven
  (`Powered`), so a pristine design never flickers. AO (`Hidden/AOPass`), crew
  fog-of-war (`Sprites/StencilCombinePass`) and CRT post are cosmetic layers a planner
  omits.
- **Radii from data:** default 6 only when `fRadius ≤ 0`; real lamps are radius 18
  (`Ceiling1x1*` / `Wall1x0*`), TV 16, planter 3, terminal 0.2. Intensity = colour
  alpha/255 (`WhiteLightCeiling` a = 100 → 0.392).
- **Exterior daylight:** each parallax location's `aSunLights` are ordinary `Visibility`
  lights, **radius 1000**, at their raw `ptPos` (world tiles, ~±250) parented to a sun
  transform whose z-rotation tracks the world background (`ParallaxController`).
  Hull-occluded; streams through glass windows.

Lighting gates nothing in-game (there is no darkness stat) — it is a faithful preview,
not a Law constraint.

> **Ported in Ostraplan:** `LightNetwork.Build` (scene resolution), `VisibilityMesh`
> (float-exact geometry, run y-up so windings and skirt normals stay sign-exact),
> `LightComposite` (per-pixel shading at 16 px/tile). **Re-verify per patch:** ambient
> black, colour ≠ Blank casts, F = 3 / Z = 0.25 / +0.1, radius defaults (6 item / 1000
> sun), `aShadowBoxes` semantics, blend modes.

---

## 17. Ship serialization (templates and saves)

### The `data/ships` file (`JsonShip`)

A ship file is a **top-level array** of ship objects (the ship element carries `nCols`
+ `aItems`; roughly a dozen files in core are non-ship). The game (de)serializes with
**Newtonsoft** — proven by `Dictionary<string,string>` fields (`aDocked`,
`aMarketConfigs`) that Unity's `JsonUtility` cannot handle — so **missing fields default
and unknown fields are ignored**. A well-formed template is the **54 top-level fields
present on all 192 core templates** plus `aRating`; unlisted fields are safely omitted.

- Values are pristine/neutral (wear/mass/physics caches 0 — the game recomputes on full
  load), `origin` / `publicName` = `"$TEMPLATE"`, `nConstructionProgress` 100.
- `strRegID` must be non-empty (the loader indexes `strRegID[0]`), but the game
  **regenerates** it and **re-derives `origin`** from a loot table when `origin ==
  "$TEMPLATE"`, and null-guards `aCrew` / `aCOs`, so a template needs no crew or cargo.
- `shipCO` is a minimal `ShipCO` (`aConds` = the three `Stat*ProgressMax=1.0x1000` +
  `DEFAULT`).

**`aItems` entry** = `strName`, `fX`, `fY`, `fRotation`, `strID`. Extras appear for
`strParentID` / `strSlotParentID` (contained/slotted sub-objects), `aGPMSettings`
(device settings), `aCondOverrides` (per-instance conds), `bForceLoad`.

**`aRooms`** = each room's tile indices (`col + row·nCols`) + `bVoid` + `roomSpec` +
`roomValue` (the **parts** value `Room.CalculateRoomValue` sums, which `GetShipValue`
reads on a shallow load — **not** the physical `Volume`).

### Contained cargo is stored the SAVE way

A `data/ships` file spawns as a template (`bTemplateOnly`); `Ship.SpawnItems` /
`Container` / `CondOwner.PostGameLoad` show:

- A parented item is **dropped** unless it has `aCondOverrides` (which also flags its
  **root container** so `bLoot` is cleared and the container is not refilled from its
  default loot) **or** `bForceLoad` (which keeps the item's `strID`). Without this a
  template comes back empty, or with only the def's default loadout.
- A **stack** is rebuilt only from the stack-head CO's `aStack` (a `string[]` of member
  `strID`s) in `PostGameLoad`.

So a faithful export gives every contained/slotted item **both** `bForceLoad: true` and
an `aCondOverrides` marker (a benign `StatDamage=0`, which is the non-null array the
pre-pass tests), **and** bakes a save-style **`aCOs`** entry per contained item
(`aConds:["DEFAULT"]` repopulates the def's pristine conds; `inventoryX/Y` from the grid
cell). A stack head's CO carries `aStack` = its member `strID`s. Top-level parts need
none of this. `aCOs` is omitted entirely when a design has no cargo.

> **Ported in Ostraplan:** `ShipExport` (write), `ShipTemplate` / `TemplateImport`
> (read). A round-trip (`doc → export → parse → import`) reproduces the same tiles /
> rooms / rating exactly.

### Ship identity on spawn

- `publicName` is re-rolled to a random `DataHandler.GetShipName()` **only** when the
  on-disk value is `null` / `""` / `"$TEMPLATE"`; any other string survives and is the
  name shown at the transponder / comms / broker / rating UI. So a real name must be
  written through (not `"$TEMPLATE"`).
- `strRegID` is **never read** from the file — `StarSystem.SpawnShip` overwrites it with
  a caller-minted ID before `InitShip` runs, unconditionally (RegIDs must be unique). A
  custom callsign cannot be baked in.
- `objSS` must be **small-nonzero**, never exact `(0,0)`: the loot-spawn path
  (kiosk/Special-Offer/starting-ship) does not reposition a template, and `(0,0)` around
  "Sol" is the star's own origin (the "spawns inside the sun" bug).

### Save games

A save is a **folder** with `<name>.zip` + `saveInfo.json` (+ portrait/screenshot).
Inside the zip: `ships/<RegID>.json` (one per ship in the loaded neighbourhood), a
`<playerName>.json` character record, and copies of `saveInfo`/portrait/screenshot. Save
ships use the same `JsonShip` schema (a superset of a template), so reading a save reads
only the top-level layout and drops all runtime state for free.

**The player's ship is `strShip` on the character record** (a RegID). Do **not** match
`saveInfo.shipName` — it is a renamed **display** name (`publicName`, e.g. "Charon") that
matches no ship's `strName`.

> **Ported in Ostraplan:** `SaveImport` (player-ship identification + layout strip).

### Non-buildable and unresolvable defs

About half of a real ship's distinct top-level defs are not in the buildable palette
(raw hull, `Compartment`, RCS clusters, sensors) but all resolve to geometry via the
condowner → `strItemDef` hop. Loot spawners, fire, and explosions carry `IsSystem` and
resolve to geometry but are **runtime effects, not structure** (`Ship.UpdateTiles`
early-returns on a CO with no `Item`), so a layout read should drop them.

An item whose def **won't resolve** (a modded part whose mod is not loaded) is invisible
to a layout read but **still real in the save**: a missing modded wall stops dividing a
room, and a missing part at the hull edge under-sizes the frame the game rebuilds (§18).
Never treat "not in the catalog" as "not there".

> **Ported in Ostraplan:** `Catalog.Lookup` (resolve any placed def), import drops
> `IsSystem` and contained sub-objects; unresolved defs are reported, and `Substitution`
> lets a real part stand in for a missing one.

---

## 18. Writing a ship back into a save

Writing an edited layout **back into a save** is not the export inverse: the record is
live, and two of its fields are re-derived by the loader rather than trusted.

### The grid frame is rebuilt on load

A full load does **not** trust `nCols` / `vShipPos`; they feed only the *shallow*
(unloaded) view (`x = (LoadState > Loaded.Shallow) ? nCols : json.nCols`).
`Ship.UpdateTiles` re-derives the tilemap as each item spawns: it seeds `vShipPos` off
the first item's `TLTileCoords`, then `TileUtils.PadTilemap`s a **one-tile margin**
around every subsequent item (`Vector2(-1f, 1f)`; `IsRoom` COs get `Vector2.zero` and pad
nothing). So the loaded grid is always:

> **frame = bounding box of all item footprints, plus a one-tile margin on every edge.**

Every `aRooms` / `aZones` entry is a flat `col + row·nCols` index, so a frame of a
different **width or origin** decodes each index to the wrong tile, the error compounding
by one column per row (a stale trailing **row** is inert by the same arithmetic). Two
consequences:

- **Pad by the margin, do not hug the content.** A part's **socket** footprint drives the
  bbox: a 7×7 tank socket (its body is only 3×3) at the hull edge pushes the bbox out with
  no visible item tile there.
- **The frame may legitimately shrink.** `PadTilemap` only ever grows, so deconstructing
  an outermost part leaves a stale empty rank in the live grid; a reload rebuilds it tight.

### A room's strID is its `Compartment` CO's strID

Rooms are not parts; each is backed by a `Compartment` condowner (`Room.coRoom`), and
`Room.GetJSONSave` writes `jsonRoom.strID = coRoom.strID`. On load, `Ship.CreateRooms`
maps tile index → `JsonRoom`, and for each room resolves `GetCOByID(strID)`: a hit becomes
`new Room(co)` and is consumed by `RemoveCO`; a miss logs `Generating new room with old
ID: <guid>` and mints a replacement. So regenerating `aRooms` with fresh strIDs while
keeping the original Compartments leaves **every original unbound** — an `IsRoom` CO no
room claims, i.e. a ghost room. The fix is to drop every room CO and let the game rebuild
each from the saved strID (room atmosphere is regenerated anyway via `bPrefill`).

### `dimensions` is display-only but locale-sensitive

The game writes it with `((float)nCols * 0.32f).ToString("#.00")`; formatting with a
comma-decimal locale emits `"15,36m x 11,20m"` into the save. Use `InvariantCulture`.

> **Ported in Ostraplan:** `SaveEdit` (inject), `SaveEditImport` (context). The frame is
> written as `bbox(item footprints) ± one-tile margin`; crew `nDestTile` is recomputed on
> any reframe; room COs are dropped (`SaveEdit.RoomCoIds`). Asserted against every local
> save (`SaveEditFrameTests`).

---

## 19. Obtaining a ship in-game (brokers, chargen)

The game merges loot/chargen data by `strName`, so a mod makes a ship reachable by
overriding or appending to the relevant pools:

- **Broker kiosks** (`RandomShipBroker{OKLG,BCER,BCRS,Venus,VORB}`): a pool's `aCOs` is a
  **single** element that is a `|`-delimited `Name=WeightxCount` set from which the game
  picks **one**. Add a ship by appending `|Name=Wx1` to that string (`LootList.Append`),
  never a second array element (which rolls a second ship). Regular vendor ships show
  `GetShipValue × priceModifier` live in the list.
- **Special Offer** (`RandomShipBrokerSpecialOffer{,VENC,VNCA,VORB}`): one pinned
  `Name=1.0x1`. Note a Special Offer entry **always lists at "$0"** in the list
  (`UsedShipListEntry.SetSpecialOfferData` hardcodes it); the DTO still carries the real
  price, so the Confirm Transaction dialog shows and charges it. A real *list* price needs
  a regular broker pool, not the special-offer slot.
- **Starting ship** (Shipbreaker career): the `CGEncShipbreakerShipEvents` roll is an
  `…Intro`/`…Take` lifeevent+interaction pair (modeled on core `CGEncShipSalvagePod*`)
  plus a `…Reward` ship loot. Vanilla has **no** true chargen ship-picker — it is
  weighted-random; "Take" grants the ship via `strShipRewards` and starting gear via
  `aLootItms:["addus,ItmShipbreakerLoadout"]`.

`fLastQuotedPrice` is a red herring for buy pricing: neither buy path reads it (only the
sell/derelict `GetQuotedPrice` cache does), and it is reset to 0 on a non-derelict
Edit-load.

> **Ported in Ostraplan:** `KioskExport` (`AppendShipToPool`, `PinShipToPool`),
> `StartingShipExport`. Where another ship mod overrides the same pool, whole-object load
> semantics would drop one side; the resolution is Ostrasort's per-item-union `--patch`.

---

## Appendix A — Quick reference

- **`nLayer` is always 0** — rank by contributed conditions (§15).
- **Footprint ≠ sprite** — socket grid vs `vScale`; the big tanks are 7×7 footprint / 3×3
  sprite (§4). Keep the footprint for the Law.
- **CheckFit is presence-only** — count multiplicity / nested triggers / `bAND=false` are
  unreachable from placement (§5).
- **Self-exclusion** — re-validating a placed part must lift its own conds first (§5).
- **Only one port bounds construction, and `IsTypeB` decides which** — `aDocksys[0]`; the
  Primary bounds, a Secondary never does while a Primary exists (§6).
- **`TIsDockSysInstalled` needs ALL reqs** (§6).
- **Loot payload is `aCOs`, not `aLoots`** — `aLoots` nests further loots (§3).
- **Palette join hops through condowner/cooverlay** — `items[strStartInstall]` alone
  misses ~half (§2).
- **Autotile rows count from the texture bottom**; the mask is N8/W4/E2/S1; connectivity
  honours `bAND` (§15).
- **Item `(fX,fY)` is the footprint CENTRE**, and `fRotation` is CCW while a CW tile
  rotation must negate it (§7).
- **Only `IsWall` bounds the room fill** — a door's side cells are always `IsWall`; its
  centre is a walkable portal when open (flood-sinks) and an `IsWall` boundary when closed.
  Same two rooms either way (§8).
- **Room certification tests CondOwner conds, not tile conds** (§9).
- **A room-less anchor falls back to the `"use"` point** — wall-embedded parts join the
  room their use point reaches; the air pump's use point is its own wall tile, so it joins
  no room and is worth $0 (§9, §11).
- **Void rooms have value; the ×3 O2 bonus is a global flag, never per-item** (§11).
- **Installed parts are never Pristine** — value them markup-free; the ×1.25 needs
  `IsPristine`, which install never grants (§11).
- **A part's value includes the gas its def starts with** — a full O2 RTA is ~$5.6k of gas
  on a $410 shell; He3 *gas* is worth 0 (§11).
- **Broker rates: sell = value × 0.8, buy = value × 1.2**; the derelict haircut is separate
  (§11).
- **The ×3 atmo bonus needs a FED pump** (`TIsAirPump02Installed` + a fed installed O2 RTA)
  (§11).
- **Ship files are top-level arrays** — the ship is an element with `nCols` + `aItems`;
  skip non-ship files. All 192 carry `aRooms`; only 2 carry `aRating` (§17).
- **A loading ship rebuilds its own grid** — the frame is `bbox(item footprints) ± a
  one-tile margin`; write a room/zone index in any other frame and it decodes wrong,
  drifting a column per row (§18).
- **A room IS its `Compartment` CO** — fresh room strIDs + kept Compartments = ghost rooms;
  drop the room COs and let the game rebuild them (§18).
- **The placement law is construction-time only** — the game never re-validates existing
  structure, so imported parts must be exempt (§5).
- **Filter `IsSystem` on read; an unresolvable def is invisible but REAL** — never treat
  "not in the catalog" as "not there" (§17).
- **A save's player ship is `strShip`, not `saveInfo.shipName`** (§17).
- **Bake `aDockingPorts` + `strPrimaryDockingPortID` or a bought ship never docks** (§6).
- **Bake `Boarding`/`NotBoarding` spawners into `aShallowPSpecs` or arrivals land outside
  the hull** (§6).

### The parity corpus (ground truth)

The corpus is **192 core ship objects** that carry baked `aRooms` (roomSpec + bVoid + tile
sets), giving a 192-ship rooms **and** certification gate. Only **Babak / Babak Refit**
(both damaged derelicts) carry baked `aRating`. Notes:

- The Babaks' baked `aRating` room slot is **stale** (`aRating[2] = "18"` while their
  current `aRooms` certify 20 non-Blank rooms), so a rating check bounds the recomputed
  count against `aRooms`, not the `aRating` string.
- A faithful room partition reproduces the baked `aRooms` for **188/192** (4 exotic
  exclusions: a malformed Coffin, two aero slant-wall hulls, one interceptor airlock);
  portal-tile filing and exterior-void over-claim are compared leniently because neither
  affects the Law.
- Certification reproduces the baked `roomSpec` with **zero over-certifications** of a real
  compartment. The residual diffs are two documented corpus-only artifacts:
  contained/slotted cargo the top-level loader cannot count (under-certification), and the
  exterior over-claim (`CargoRoomExterior` on the unbounded Outside room). Neither reaches
  a from-scratch authored design.

> **Ported in Ostraplan:** `ParityTests` (rooms + certification across the corpus),
> `RatingTests` (size-slot parity + unit-pinned cutoffs).

---

## Appendix B — Ported / deferred / excluded

| Game logic | Status | Ostraplan home |
|---|---|---|
| Palette join, mod/load-order resolution | ported | `DataIndex`, `Catalog` |
| Tile-condition accumulation (`UpdateTiles`) | ported | `TileConds` |
| Placement law (`Item.CheckFit`) | ported | `CheckFit`, `ProblemScan`, `ShipCanvas` |
| Airlock envelope (mating face) | ported | `ProblemScan.TryGetFace` |
| Footprint + sprite scale (`SetData`) | ported | `Defs.ItemDef`, `SpriteCache.SpriteTiles` |
| Autotile (`SetSpriteSheetIndex`) | ported | `Autotile` |
| Mask rotation (`RotateTilesCW`) | ported | `GridMath.Rotate` |
| `CondTrigger.Triggered` — reachable branches (bAND, OR, nested, forbids) | ported | `CondEval` (CO-level); presence path in `TileConds` |
| Rooms / airtightness (`CreateRooms`) | ported | `ShipGrid`, `RoomBuilder` |
| Room certification (`RoomSpec.Matches`) | ported | `RoomSpecs` (`RoomCertifier`) |
| Ship Rating (`CalculateRating`) | ported | `Rating` |
| Ship value (`GetShipValue` / `GetBasePrice`) | ported | `ShipValue`, `Catalog.GasPrices` |
| Power connectivity (`GetPoweredTiles`) | ported | `PowerNetwork` |
| Device signal connections (`Electrical` GPM) | ported | `DeviceLink` / `DeviceLinks`, `ShipExport.WireDeviceLinks` |
| Deferred lighting (`Visibility` + `LoSPass`) | ported (preview only) | `LightNetwork`, `VisibilityMesh`, `LightComposite` |
| `JsonShip` (de)serialization — export/template/save schema | ported | `ShipExport` (write), `ShipTemplate` (read) |
| Coordinate/rotation mapping (centre ↔ top-left, CCW) | ported | `ShipGrid.TemplateTile` + `ShipExport` |
| On-demand resolution of any placed (non-buildable) def | ported | `Catalog.Lookup` / `Catalog.ResolveDef` |
| Save player-ship identification (`strShip`) + layout strip | ported | `SaveImport` |
| Save write-back (frame rebuild, room-CO drop, dimensions) | ported | `SaveEdit`, `SaveEditImport` |
| Ship zones (`aZones`) as authored data | modelled (preserve/draw/edit, not validated) | `ShipZone` / `ZoneGeometry` |
| Wear/damage (`BreakIn` / `DamageAllCOs`) | ported (optional) | `WearModel` |
| Obtainability (brokers, chargen) | ported | `KioskExport`, `StartingShipExport` |
| Contained/slotted sub-objects on read; exterior-margin trim | not modelled (corpus-only; import drops sub-objects) | — |
| Crew LOS/proximity, docked-ship, station build-zone permission | excluded (in-game only) | never ported |

---

*Companion documents: [usage.md](usage.md) (how to use Ostraplan) and
[README.md](../README.md) (overview, install, build).*
