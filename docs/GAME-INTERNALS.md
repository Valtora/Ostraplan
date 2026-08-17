# Ostranauts — Game Internals Reference

A reference for **how Ostranauts works internally**, reconstructed by decompiling
`Assembly-CSharp.dll` and reading the live game data. It is the source of truth
Ostraplan is built against: Ostraplan keeps its promise ("the Law") by *porting*
this logic, never by referencing the DLL at runtime (its types are
`MonoBehaviour`s that round-trip through Unity, so calling them off the game gives
silently wrong answers). Each system below is described as the game implements it,
with the relevant `Type.Method` citations; a short **Ported in Ostraplan** note
points to where that system is reimplemented.

**Verified against game `1.0.0.7`** (`GameEnv.VerifiedGameVersion`, Steam build 24535205),
except where a section carries a later stamp of its own. Rating cutoffs and other magic
numbers are compiled into the DLL and invisible to data diffing, so they can drift between
patches with nothing in the data to show for it. The version pin exists to flag that.

The per-section **"re-verify" notes say what to check when a sweep runs, not how often to
run one**. See [When a sweep is warranted](#when-a-sweep-is-warranted) in section 1.

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
- [20. Propulsion (RCS and the torch drive)](#20-propulsion-rcs-and-the-torch-drive)
- [21. Crew walkability and interaction reach](#21-crew-walkability-and-interaction-reach)
- [22. The ship diagnostic (`ShipStatus.PrintStatus`)](#22-the-ship-diagnostic-shipstatusprintstatus)
- [23. Atmospheric flight (lift, drag and rotors)](#23-atmospheric-flight-lift-drag-and-rotors)
- [24. What a canister holds (`GasContainer`)](#24-what-a-canister-holds-gascontainer)
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
| `ShipStatus.cs` | `PrintStatus` (the nav console's ship diagnostic), `GetO2UnderPump` |
| `Powered.cs` | `UsePower` / `QueryPower` (what "connected power" totals at a device) |
| `GasContainer.cs` | `Run` (pressure/partial pressures/mass), `Init`, `AddGasMols`, `GetGasMass`, `CheckPressureDifference` (the burst check), `GetTotalGasValue` |
| `JsonItem.cs` | `ApplyOverrideCondsToCO` (how `aCondOverrides` reaches a spawned condowner) |

### The GPU shaders

Some of the render constants are not in the C# assembly at all. The Light Viz falloff and
the blend modes live in compiled Unity shaders, so `ilspycmd` cannot reach them and
section 16 was reconstructed like this instead:

1. `pip install UnityPy`, load `Ostranauts_Data/resources.assets`, filter
   `obj.type.name == "Shader"`. The ones that matter: `Sprites/LoSPass` (the light-mesh
   fragment, which is the falloff math), `Sprites/DefaultAdditive` (glow decals),
   `Sprites/AlbedoPass`, `Hidden/FinalCombinePass`, `Sprites/StencilCombinePass`.
2. Read the typetree: `platforms` / `offsets` / `compressedLengths` /
   `decompressedLengths` / `compressedBlob`, LZ4-block-decompress, then a header of
   `int32 count` followed by `count ×` **triplets** of `(offset, length, segment)`. Each
   subprogram segment holds a `DXBC` container: find the magic, and the total length is
   at magic + 24.
3. Disassemble through the system `d3dcompiler_47.dll` via ctypes (`D3DDisassemble`). No
   external tooling is needed.
4. Blend state is `m_ParsedForm.m_SubShaders[].m_Passes[].m_State.rtBlend0`, against
   Unity's `BlendMode` enum (1 = One, 4 = OneMinusDstColor, 5 = SrcAlpha). `LoSPass` is
   `OneMinusDstColor One`, a screen blend; `DefaultAdditive` is `SrcAlpha One`.

### When a sweep is warranted

**A full re-verification sweep is not run against every game patch.** Sweeps happen on
major game versions, or when specifically asked for. The suite already runs against the
live install's data, so data drift surfaces on its own as a parity regression, and a full
decompile re-read per patch is cost without signal.

A **"verified X" stamp moves only for a port that was actually re-read** against the newer
decompile. That includes the per-port comments in the code, the stamps in this file, and
`GameEnv.VerifiedGameVersion`. If a piece of work happens to re-read one port, move that
port's stamp alone and leave the rest, which is why the sections here do not all carry the
same version.

**When a sweep does run:**
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

### Core data is not always valid JSON

The game's own parser accepts raw control characters inside a string literal, which the
spec does not (RFC 8259 requires them escaped). Core uses this freely for multi-line
prose: a `strDesc` in `data/interactions` is written with real line breaks between the
quotes.

```json
"strDesc" : "At long last, you approach the location of your many visions.
                                      <- a real CRLF, inside the string
Before you you see an enormous golden sphere. …"
```

`System.Text.Json` rejects those files outright. On a stock **1.0.0.7** install that was
eight core files (`interactions_encounters.json` and seven under `interactions/plotIAs/`),
and dropping them cost twelve interactions that real parts reference: the Venus embassy
and OKLG medical kiosks, the Venus racing kiosk, the express transit door, and the Ceres
plot crate. Those are `SPECIAL`-tab fittings, so the loss showed up as the Walk overlay
reading them as having no actions at all.

`DataIndex.Parse` therefore falls back a third time, escaping control characters found
inside string literals and re-parsing. It cannot change meaning (a control character and
its escape denote the same character) and it is validated rather than assumed, since the
mended text still has to parse before it is accepted. Files mended this way are recorded
in `DataIndex.Repaired` and reported in the bug-report diagnostics, but they are **not**
warnings: nothing is lost and there is nothing for a user to do.

What remains a warning is data that is genuinely incomplete, such as core's
`FloorLDPH04AInstall`, whose `strStartInstall` names an `ItmFloorLDPH04A` that no def
declares (its `03A` / `04B` / `04C` siblings all have one). That installable is skipped,
so the floor is absent from the palette, and no amount of parsing recovers it.

Every warning carries the source that produced it (`DataWarning`), because that decides
whether anyone can act on it. A defect in **core** is permanent and not the player's
doing, so it is logged and folded into a bug report but kept off the toolbar's badge; a
defect a mod brought in is theirs to disable or update, so it surfaces. Attribution is by
source identity, not by comparing the label to `"core"`, so a mod named that way cannot
launder its own defects.

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

**The overlay is a fallback, not a priority.** `DataHandler.GetCondOwner` reads
`dictCOOverlays` only when `dictCOs` has no entry of that name:

```csharp
if (!dictCOs.ContainsKey(strCO)) {
    if (!dictCOOverlays.ContainsKey(strCO)) return null;
    jsonCOOverlay = dictCOOverlays[strCO];
    strCO = jsonCOOverlay.strCOBase;          // only now does the skin's base apply
}
```

A real condowner therefore wins outright, and when it does, `jsonCOOverlay` stays
null — so `COOverlay.Init` never runs and none of the skin's deltas (`strCondLoot`,
`strImg`, `mapIAReplaces`) apply either. Core 1.0.0.7 ships **eight** names that are
both: the grey Rakow "Reserve" bins `ItmStorageBin2x104` / `ItmStorageBin2x2C04` and
their `Dmg`/`Loose` forms, each a complete condowner *and* a legacy cooverlay
pointing at the "01" sibling. Resolving overlay-first hands them the 01's
`strItemDef`, and the 01's socket mask **requires `TILFloor`** beneath the bin while
the 04's does not — the grey bin mounts on a bulkhead with open space under it, so
every imported one reads "needs a sealed floor beneath". It also mis-priced them
(882 vs 1482), under-massed them (14 vs 18 kg) and dropped `IsTough` / the
`Cumbersome` container filter. (Only the friendly name is genuinely overlay-first in
the game — `GetCOFriendlyName` reads the skin regardless — and for these eight it is
identical either way.)

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

#### A loot can spawn ITEMS, and that is where a garment's pockets come from

`strType` decides how `aCOs` reads. For the socket-mask loots above it is a
condition table. For `strType: "item"` the same entries name **items to spawn**,
with the number after the `x` as the count: `ItmPocketsCoverallsx2` is
`["PocketHip01=1x2"]`, i.e. two `PocketHip01`. A def points at one through its
condowner's `strLoot`.

This is not just stock. For **17 garments** it is the object's own anatomy: a pair
of coveralls (`OutfitSuit02`) declares **no** `nContainerWidth` at all, is not a
container, and gets its entire capacity from `strLoot`. Backpacks (4×
`PocketPouchSmall01`), EVA lockers, the wrist PDA (`DataStore`) are the same. The
field also carries genuine stock, though — a Coilgun's 15× `ItmAmmo150mm`, a body
part's wounds — so the two cannot be treated alike.

> **Ported in Ostraplan:** `LootDef.Items` / `Catalog.IntrinsicContents`, which
> expands `strLoot` and keeps **only the children that are themselves containers**.
> That single test separates anatomy from stock without a def-name list: pockets are
> containers, ammo and wounds are not. Those children are materialised as
> `CargoItem.Intrinsic` nodes when the item is added, so they show up in the
> inventory, can be filled, and reach the save. Excluded from the edit cost, since
> you do not buy pockets separately from the coveralls.
>
> Without this a garment written into a save arrived **with no pockets** and was
> permanently useless. Ostraplan synthesises a contained item straight from its def,
> and a save-loaded item is restored as recorded rather than respawned from the def,
> so nothing ever ran the loot.

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

### Skin cond-loots (`COOverlay.Init`) — where a branded part's stats come from

The loots above write **tile** conditions. A cooverlay's `strCondLoot` is a second,
separate mechanism that writes the **part's own** conditions, and it is what makes the
branded walls and floors genuinely distinct parts rather than reskins.

A branded metal wall (Testudo, Ryokka, Langdon-Phillips, Mobile Space Systems, Minsheng,
Van Hummel, …) is a cooverlay whose `strCOBase` is the shared condowner `ItmWall1x1` at
24 kg, but whose `strCondLoot` carries **signed per-brand deltas** applied on top.
`DataHandler.GetCondOwner` adds the `COOverlay` and calls `Init`, which runs
`Loot.ApplyCondLoot` and accumulates through `AddCondAmount`. This happens on **every**
spawn: build, template and loot alike. A built wall's real stats are therefore
`base + loot deltas`, never the base alone.

`CNDOLWallMSSLFWhite` (the MSS "Light Framework") is the worked example: `-StatMass x4`,
`StatBasePrice +65`, `-StatInstallProgressMax x150`, `IsMSS`, `IsWhite`, `-IsHiddenInv`.
Against `ItmWall1x1` (mass 24, price 21, install 600, `IsHiddenInv` 2) that gives mass 20,
price 86, install 450, `IsHiddenInv` 1, plus the two brand conds, matching the baked wall
on a real player ship cond for cond. Built mass per brand: MSS 20, Testudo 25, Van Hummel
27, Ryokka 28, Langdon-Phillips 48; Testudo Aero takes `-10` to 14; Caylon plastic is
unchanged.

> **Zero means absent.** `AddCondAmount` removes a condition it drives to `<= 0` rather
> than storing it at zero, so the Caylon floor loot's `-StatMass x13` against a 6.5 kg
> grate leaves **no mass condition at all**. Mirror the removal; do not store a negative.

Two traps when reading this data. A shallow or partial spawn can skip the overlay loot
entirely and leave a part showing its flat base stats with `DEFAULT` and no brand
conditions, which looks like evidence that the brands are identical and is not: the
canonical player-built part carries the full deltas. And `LootDef.CondName` keeps the
leading `-` (`"-StatMass=…"` returns `-StatMass`), so strip it before keying, because
`CondAmount` handles the sign separately.

> **Ported in Ostraplan:** `CoOverlayDef.CondLoot` and `Catalog.ApplyCondLoot`, which fold
> the loot's `aCOs` deltas (recursing `aLoots`, condition-type only, dropping `<= 0`) onto
> the base `StartingCondValues` / `StartingCondNames`. Before v0.43.0 `Catalog.ResolveDef`
> resolved to `strCOBase` and read that def's `aStartingConds` while ignoring
> `strCondLoot`, so every branded wall showed the flat 24 kg. Tests:
> `CondLootOverlayTests`. Retuning per-brand stats means editing the `CNDOLWall*` loots in
> `data/loot`, not authoring new condowners.

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

The socket grid minus that under-floor reservation is the part's **body**
(`Catalog.BodyBox`): 3×3 at offset (2, 2) for those canisters, the whole footprint
for everything without an apron. The body is the object as a user meets it, so it
is what Ostraplan hit-tests, outlines and band-selects, and what decides which
parts can stand in for each other (`Catalog.SwapClass`, used by `ReplaceOps`,
`ThemeOps` and `SurfacePaint`). Classing swaps on the raw socket grid instead left
a canister swappable only with the two other canisters that share its apron, while
the 3×3 machines it visibly matches were unreachable. The placement law is a
separate question and still reads the full socket grid.

> **Ported in Ostraplan:** `Defs.ItemDef` (footprint), `SpriteCache.SpriteTiles`
> (sprite), `Catalog.IsUnderFloorLoot`, `Catalog.BodyBox` / `Catalog.SwapClass`.

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

> **Sub-floor bins are not buildable surfaces, and take no exemption.** An
> under-floor storage bin or rack (`ItmRackUnder01`, `ItmStorageBinFloor…`) tags its
> walkable tiles `IsFloorSealed` + `IsFixture` (via `TILFloorFixture`) and **never**
> `IsObstruction`. It is walkable, but you cannot build on it: the game refuses
> everything on top of a sub-floor bin **except ceiling-level parts**. That rule needs
> no special case in the cell test, because the data already draws the line — a conduit
> forbids only `TILPowerConduitOff` and an overhead light only `TILLight`, so neither
> expands to `IsFixture` and both cross a bin under the plain rule, while a rack's
> `TILObstruction` does and is refused.
>
> **This was wrong from 0.8.0 to 0.44.x** and is worth stating plainly, because the old
> text asserted the opposite as verified fact. The waiver ("`IsFixture` does not trip the
> forbid on a tile that also carries `IsFloorSealed`") let a rack build on a bin, which
> the game does not allow, and it cost far more than it bought: it also disarmed every
> part whose forbid mask is `TILFixture` (→ `IsFixture`, `IsFloorFlex`) rather than
> `TILObstruction` (→ … + `IsObstruction`). Ten palette parts sit in that class — the
> whole fusion chain, the air pumps and the vents — and for them `IsFixture` is the only
> occupancy guard there is, so on any floored tile they stacked without limit and scanned
> clean. Do not reintroduce it.
>
> The other half of the old claim, reaching an adjacent fixture *across* such a floor, is
> a **reachability** question and is not a socket forbid at all. It belongs to the
> two-tier destination search in §21, whose tier-2 fallback already walks onto a rack when
> no clean tile is in range.

> **Soft requirement — the overhead-light conduit (deliberate deviation, issue #11).**
> `IsPowerConduit` is the one req condition Ostraplan does **not** hard-enforce. Among all
> ~331 buildable parts it is required (via `TILPowerConduitOff`, an adjacent `aSocketReqs`
> cell) **only** by the overhead ceiling lights (`ItmLitCeiling1x1*`). The game's *interactive*
> builder blocks a light with no adjacent conduit — but that gate is player-build-only, and every
> dev-authored / spawned ship hangs ceiling lights freely (the core **Baleen**: 31 ceiling lights,
> **0** adjacent conduits), wiring them through the electrical (GPM) graph. Since a planner emits
> spawn-placed ships, `CheckFit.SoftReqs` treats a missing `IsPowerConduit` as an **advisory**:
> the pose stays `Ok`, but `FitResult.Advisory` / `AdvisoryCells` carry it → an amber "places, but"
> ghost and a dismissible `ProblemScan` warning. Everything else in `aSocketReqs` remains a hard block.

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
> and skipped by `ProblemScan`; moving or rotating a given part clears the flag, and
> **undoing** that move restores it (`MoveCommand` / `SetPosesCommand` /
> `RotateCommand` snapshot it, and `ShipDocument.MoveTo` / `SetPose` take the state
> to land in) — otherwise a nudge and a Ctrl+Z leave imported structure permanently
> re-authored, judged by the law and billed as new construction.
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

> **Ported in Ostraplan: identify the Primary by its CONDITIONS, never its def name.**
> `Catalog.IsPrimaryDocksys` is `TIsDockSysInstalled` (`IsDockSys` + `IsInstalled`)
> without `IsTypeB`, memoized per def. `Catalog.PrimaryDocksysDef` is *only* the
> variant a brand-new design is seeded with. The same port exists in other states,
> and a save carries whichever one the ship is in: a player who pries the door open
> has `ItmDockSys02Open`, and a damaged one is `…Dmg`. Matching `ItmDockSys02Closed`
> alone made such a ship read as having **no** primary port, which cost three things
> at once — the airlock stopped being fixed against move/rotate/delete, `ShipExport`
> tagged no port as primary, and reopening the design seeded a *second* airlock at
> the origin. That last one moved the written grid frame (the inject writes bbox±1)
> and left the ship unable to dock or undock. `ProblemScan` now reports a design
> carrying more than one primary-class port, because designs saved while this was
> broken still hold the stray.

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
**personspecs** (`strCT: TIsBoarding` / `TIsNotBoarding`). **177 of 220 core
templates carry `aShallowPSpecs`** (the 43 without are stations, buoys, and rocks).

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
`bVoid`, and 237 of the corpus's 482 baked void rooms carry a non-zero `roomValue` (an unsealed engine
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

### Switching a placed device, and why an alarm is different

`PreferPoweredState` picks the build target and stops there, so a device whose on-state it
cannot name is placed Off with no route back. Reaching **both** states needs the mapping in
both directions, and the on-state naming is looser than that function assumes: the
Transponder's is `ItmTransponder01OnR`, a colour variant.

Of the 173 installable condowners carrying `IsOff` on a stock **1.0.0.9** install (damaged,
closed and colour-custom states carry it too), 90 are named `…Off`. 76 of those map to
exactly one reachable on-state, 8 map to none (the fusion reactor core and ICs,
`ItmSwitch02Off`, `ItmVent02Off`, and three damaged EVA lockers — nothing a planner should
switch), and **6 have several — every one an alarm**, because the colour *is* the alarm
level:

| Family | States |
|---|---|
| CO2 | `OnG` nominal, `OnY` "(Warning)", `OnR` "(Alert)" |
| N2 / O2 / Smoke / Contaminants | `OnG` nominal, `OnR` "(Alert)" |
| Temp | `OnW` nominal, `OnB` "(Too Cold)", `OnR` "(Too Warm)" |

The nominal state is identifiable **from the data**, with no colour list: the alert states
qualify their `strNameFriendly` in parentheses and the nominal one does not. That picks
`OnG` for the five gas alarms and `OnW` for the thermostat.

**Authoring a nominal alarm cannot lie**, because the game overrules it. Every on-state
carries a sensing update command and a sense ticker that the off state does not
(`GasPressureSense,AlarmPressureO2` + `PressureSenseO2`; the thermostat uses
`Sensor,AlarmTemp`). `GasPressureSense.Run` / `Sensor.Run` read the real conditions at the
sensor's map point each tick and queue the alarm or clear interaction, which is what swaps
the CO's state. An O2 alarm authored Green aboard a ship in vacuum is flipped to Red by its
own sensor. Note the corollary: an alarm left **Off** carries no sensor at all and never
self-corrects, which is correct — it is off.

> **Ported in Ostraplan:** `Catalog.PreferPoweredState` (the build target) and
> `Catalog.PowerToggle` (both directions, nominal-only), behind the right-click **Switch
> on** / **Switch off** actions. It is not cosmetic: the Ship Rating and the diagnostic
> both forbid `IsOff`, so a switched-off transponder really does read as a fault.
> **Re-verify per patch:** the alert-state naming convention, and whether any device grows
> a second unqualified on-state (which would make it ambiguous and silently drop out of the
> menu). `PowerStateTests` sweeps every condowner and pins the transponder and the alarms.

### Damage is two different things, and so is "repair"

A part is damaged in Ostranauts in one of two entirely separate ways, stored in different
places, fixed by different jobs.

1. **Accumulated wear.** A condowner carries `StatDamage ∈ [0, StatDamageMax]` in its
   `aConds`. This is per-instance save state; the Ship Rating's Condition slot is the mean
   of `clamp01(1 − StatDamage/StatDamageMax)` over installed parts (§10). No def declares
   `StatDamage`, so it exists only on a save's COs and has no representation in a design.
2. **A broken def.** `ItmWall1x1Dmg`, `ItmWall1x1Patch`, `ItmAlarmSmokeDmg` are separate
   condowners carrying `IsDamaged` / `IsPatched`. This is a fact about *what is on the
   tile* — different sprite, different conditions, different value — and it therefore
   belongs to the layout, not to an instance.

`data/installables` files a job for each, and **both declare `strJobType: "repair"`**:

| File | Entries (1.0.0.9) | `strProgressStat` | `strInteractionTemplate` | Loot |
|---|---|---|---|---|
| `installables_undamage.json` | 505 | `StatDamage` | `ACTUndamage*` | the **same** def back |
| `installables_repair.json` | 267 | `StatRepairProgress` (252) | `ACTRepair*` | the **working** def |

> **The job type cannot be the discriminator.** 12 of the undamage jobs have a loot that
> *does* differ from their action CO — `ItmDoor01ClosedOnLocked → ItmDoor01Closed`,
> `ItmDockSys02Open → ItmDockSys02Closed` — because grinding the wear off a door also
> normalises its lock and power state. Reading those as broken→working mappings would make
> a bulk repair silently unlock every locked door and shut every powered one. Keying on
> `strProgressStat` separates them exactly, and drops the 15 dev-only
> `reset`/`StatDebugProgress` entries (`Crate01Reset`, `StationNavDebug`) with them.

Two further properties of the repair map, both verified on stock 1.0.0.9:

- **A themed part's damaged state is not a cooverlay of its own.** `ItmWallAERO01`'s
  `mapModeSwitches` carries all four base states at once — `[ItmWall1x1, ItmWallAERO01,
  ItmWall1x1Patch, ItmWallAERO01Patch, ItmWall1x1Dmg, ItmWallAERO01Dmg, ItmWall1x1Loose,
  ItmWallAERO01Loose]` — so a damaged skin only ever appears as the *right-hand* side of a
  pair. The mapping is recovered by repairing the left side through the base map and
  re-skinning the result forward through the same overlay. All **1,794** cooverlay damaged
  states resolve this way; none needs a cross-overlay hop.
- **A repair job returns the Off state** (`ItmAlarmSmokeDmg → ItmAlarmSmokeOff`), which the
  rating never counts — the same trap `PreferPoweredState` exists for above.

> **Ported in Ostraplan:** `Catalog.RepairForms` (broken → working, `PreferPoweredState`
> applied) behind **Design ▸ Repair All…** and the right-click **Repair**, and
> `WearOptions.Repaired` for the wear half, which clears `StatDamage` on every structural CO
> of a save write-back. The two are deliberately separate features because they are
> separate data. **Re-verify on a major game version:** the `strProgressStat` values, and
> whether any new cooverlay needs the cross-overlay hop. `RepairTests` pins the door/dock
> trap and the themed-wall mapping.

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

### The `Rename` GPM — an object's own name

An item's `aGPMSettings` is a **list** of panels, and `Electrical` is only one of them.
A player renaming an object (`CondOwner.Rename`, forbidden on humans and robots) stores
the result as a second panel:

```json
{ "strName" : "Rename", "dictGUIPropMap" : [ "strName", "Pressurization SB" ] }
```

`Rename` sets `strNameFriendly` and `strNameShort` on the CO, and clearing it removes the
panel and restores the def's own names. `Ship.SpawnItems` spawns each item through
`Ship.CreatePart`, which merges every panel from the item onto the CO — per key, a later
duplicate overwriting an earlier one — and then calls `CondOwner.CheckForRename`, so the
name is re-applied on load from a template **and** from a save. Core ships already carry
it: the stock `Babak Refit` ships with 51 of them, `Pressurization SB` on an electrical box
and `Bow DPP Port` on an air pump among them. The game caps neither the length nor the
content of a name, and stores it verbatim.

> **Ported in Ostraplan:** `Rename` (the panel shape, read and written) and
> `Placement.CustomName`, read on import (`ShipTemplate` → `TemplateImport`), written on
> export (`ShipExport`) and on save write-back (`SaveEdit.ApplyRename`). Two traps, both
> now covered by tests: `dictGUIPropMap` is a **flat alternating** key/value array, not an
> object; and a write must **replace** an existing `Rename` panel rather than append one —
> the game's load merges duplicates with the **last** panel winning, so an append would
> read correctly by accident while growing a stale panel per edit and leaving the item a
> shape the game itself never writes. Names read off a ship are carried and written back
> **verbatim** (`Rename.OrNull`); the 64-character cap applies only to names typed in
> Ostraplan's own dialog. Ostraplan offers renaming on containers and devices only, which
> is narrower than the game's "anything not a person".

---

## 15. Rendering

- **Z-order is `fZScale`, per item def. Higher draws nearer the viewer.** `nLayer` is `0`
  for every item and the game never reads it. What it reads is `JsonItemDef.fZScale`,
  applied twice by `Item`, both monotonic in the value and agreeing in direction:

  | Where | `Item.cs` | Effect |
  |---|---|---|
  | Sprite position | `_tf.position.z = GetZPos()` = `-fZScale × 4` | The camera looks down **+Z** (mouse picking rays start at `z = -10` and travel `Vector3.forward`), so a more negative Z is **nearer**. |
  | Material queue | `rend.sharedMaterial.renderQueue = 2000 + round(fZScale × 100)` | A higher queue draws **later**. |

  Sorting on the raw `fZScale` therefore reproduces the game's order exactly. The default
  is `1f`, from `JsonItemDef`'s constructor, and it is deliberate: of the 1034 core item
  defs only 55 leave it unset, and those are the **walls, racks and struts**. The scale
  the shipped data actually uses (counts and values read off a stock **1.0.0.9** install,
  Steam build 24663190):

  | `fZScale` | What sits there |
  |---|---|
  | 0.001 | background regolith plate |
  | 0.01 | floors, floor labels/decals |
  | 0.02 – 0.5 | loose forms (all `…Loose` variants are 0.5), seats 0.1, chargers 0.2, canisters 0.5 |
  | 0.74 – 0.98 | scrubbers, vents, atmosphere alarms 0.75, RCS distro 0.8, hull sensors 0.98 |
  | **1.0** | **walls, doors, racks** — the unset default |
  | 1.01 – 1.02 | bulkhead bins 1.01, RCS clusters 1.01, power conduit 1.02 |
  | 1.5 | the highest the data goes |

  Note that **walls draw over most fixtures**. That is the game, not a bug: `Catalog.RenderLayer`'s
  floor < wall < fixture < conduit ranking is a classification of what kind of deck element a part is,
  used by the swap classing, the Surfaces focus and the right-click layer filter, and it deliberately
  does not agree with the draw order.
- **Sprite draw.** Non-sheet sprites draw at `vScale` size centred on the footprint
  (§4). Sheet items draw per tile.

> **Ported in Ostraplan:** `ItemDef.ZScale` → `ShipDocument.RenderOrder`;
> `RenderStackAt` drives the right-click layer picker and the `` ` `` cycle key.

**Where the game stops answering.** Two defs given the *same* `fZScale` cannot be
separated: their sprites sit at one Z in one render queue, and `nLayer` is 0 on both. This
is not a corner case — a canister installed on an RCS regulator's `GasInput` point sits at
pixel offset `(±16, 0)`, i.e. **exactly** the regulator's own row. Ostraplan therefore adds
its own terms **below** the z-scale, and they are a **convention, not a port** — do not
"fix" them towards a game behaviour that does not exist:

| Term | Rule |
|---|---|
| Manual bias | `Placement.ZBias` / `LooseObject.ZBias`, the user's Move Back / Move Forward. Applied inside one z-scale, so a nudge settles what the game leaves open rather than overruling what it decides — and nothing can be pushed under a deck plate. |
| Object rank | Canisters, then other placed parts, then loose deck clutter. |
| Bottom edge | The body's last row (`BodyBounds`), so a small part standing within a larger one's body reads as sitting in it. |
| Insertion | Last resort, so an unedited design draws the same way twice. |

A canister is whatever satisfies the game's own **`TIsVessel`** trigger (an OR over
`IsVessel01` / `IsVesselH2` / `IsVesselHe` / `IsVesselHe3` / `IsVesselCO2` / `IsVesselO2`
/ `IsVesselN2`), read from the data rather than from a def-name list, so a modded
canister ranks with the rest.

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
combine passes; extracted with UnityPy, DXBC disassembled via `d3dcompiler_47`, procedure
in [section 1](#the-gpu-shaders)).

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
and unknown fields are ignored**. A well-formed template is the **56 top-level fields
present on all 220 core templates** plus `aRating`; unlisted fields are safely omitted.

- Values are pristine/neutral (wear and runtime physics caches 0), `origin` /
  `publicName` = `"$TEMPLATE"`, `nConstructionProgress` 100. **The shallow-state block is
  the exception** — see below.
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

### Which items build the grid, and when

The frame rule of §18 is not a save-only concern: a **template** spawn rebuilds its grid
by the same route, so a `data/ships` file's `nCols` / `vShipPos` must agree with what the
loader will derive or its own `aRooms` / `aZones` decode against a different grid.

Two gates decide whether an item contributes:

- **Load state.** `Ship.SpawnItems` passes `bTiles = nLoad > Loaded.Shallow` to `AddCO`,
  and only a true `bTiles` reaches `UpdateTiles`. A **Shallow** ship therefore builds no
  tilemap at all (`aTiles` stays empty, and `SetZoneData` early-returns on that), which is
  why the file's own `nCols` is what the shallow view reads.
- **Parentage.** Only a **top-level** (parentless, unslotted) item is `AddCO`'d. A
  contained or slotted one is attached to its parent instead
  (`objContainer.AddCOSimple` / `compSlots.SlotItem`) and never touches the tilemap. So
  cargo, equipped gear, nav-console modules and the members of a stack pad nothing.

Everything else pads, and `UpdateTiles` pads **before** it reads `aSocketAdds`: the margin
is applied for any CO carrying an `Item` and no `Pathfinder` (crew), regardless of whether
it contributes a single tile condition. A **loose floor item is a top-level item**, so it
grows the frame exactly like an installed part despite being non-structural everywhere
else. Written outside the intended frame it widens the rebuilt grid, and on the next load
above Shallow `SetZoneData` indexes `aTiles[storedIndex]` directly while `CreateRooms`
looks up its `mapTileRooms` by rebuilt tile index — so both decode onto the wrong tiles.

> **Ported in Ostraplan:** the export's declared grid is asserted to equal the frame the
> game will rebuild (bbox of the top-level items ± 1) in `ShipExportMappingTests`; the
> save-edit side of the same rule is `SaveEditFrameTests`.

### The shallow-state block is real data, not a cache

`Ship.GetJSON` writes a block of derived figures on save, and `Ship.InitShip` reads them
straight back for a ship loaded **Shallow**. They are only recomputed once the ship
reaches an Edit/Full load, so on a template they are the ship's stats for as long as it
stays unloaded. **Every core template carries a real `fShallowMass`** (0 of the 220 ship
elements in 1.0.0.7 are zero), and each of the rest is populated wherever the ship
actually has that system.

| Field | Written from | Read by |
| --- | --- | --- |
| `fShallowMass` | `Ship.Mass` (Σ `StatMass` over top-level COs, no `IsInstalled` filter) | `Ship.Mass` while shallow (+ cargo mass); "Mass: (kg)" on the chargen/kiosk spec sheet |
| `fShallowRCSRemass` / `…Max` | `GetRCSRemain()` / `GetRCSMax()` | the AI fuel-request path |
| `nRCSCount` | `fRCSCount` (Σ `StatThrustStrength`, **not** a headcount) | `Ship.Maneuver`; "RCS Count" on the spec sheet |
| `nRCSDistroCount` | counted on distributor install, ignoring power state | `Ship.Maneuver` |
| `bFusionTorch` | `bFusionReactorRunning` | "Torch Drive: Yes/No"; AI interregional routing; sensor signature |
| `fFusionThrustMax` / `fFusionPelletMax` / `fShallowFusionRemain` | `FusionIC` | the nav console's course plot and reactant clock |

Two of these are load-bearing rather than cosmetic. `Ship.Mass` returns `fShallowMass`
verbatim while shallow, so a zero divides through every acceleration the flight model
computes; and `Ship.Maneuver` **returns without thrusting** when `fRCSCount == 0` or
`nRCSDistroCount == 0`, so a shallow ship with a zeroed pair cannot manoeuvre at all.

A template's reactor is unlit, yet every core torch ship still ships `bFusionTorch: true`
with its thrust and pellet figures baked. Shallow, this block **is** the ship's stated
torch capability; `FusionIC` overwrites all three the moment the ship loads far enough to
run one.

> **Ported in Ostraplan:** `Propulsion` supplies every figure; `ShipExport.Build` bakes
> them. The design's *expected haul mass* is deliberately excluded from `fShallowMass` —
> it is a planning input for the acceleration report, not ship mass.

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

### A nav console is stocked by a loot spawner, not by parented modules

A nav console (`ItmStationNav`) is a bare frame: a 5×4 container
(`strContainerCT: TIsFitContainerNavMod`) whose screens are separate hot-swappable
`ItmNavMod*` items held loose inside it. Its own def carries **no** module loot: `strLoot`
is `ItmNAVDataStorage`, a `DataStore` chip that goes in the console's `data` **slot**
(`aSlotsWeHave: ["data"]`) with a datafile or two inside it. So every console in the game
holds something, and none of it is a screen — a console is "empty" when it has no **loose**
cargo, not when it has no cargo.

Core templates do not parent modules to the console either. Across the 220 core
`data/ships` files, **not one of the 127 consoles carries a module item**. Instead a
`SysLootSpawner` sits at the console's own `fX/fY` with a `strType: "Loot"` prop map
naming a stock set in `data/loot`, `strCount: "1"`, and `strNew`/`strDamaged`/`strDerelict`
all `"True"` — the game rolls it at spawn, so a derelict copy of the ship gets the damaged
variants. **82 of the 127 consoles have such a spawner**; the other 45 are meant to be
bare.

| Set | Modules | Core consoles using it |
|---|---|---|
| `ItmNavStationModsPod` | 13, including `MooringControl`; no torch, no weapons | 35 |
| `ItmNavStationModsTorchShip` | 13, including `CoursePlot` + `TorchDrive`, no mooring | 18 |
| `ItmNavStationModsCombat` | 15, including `WeaponsMFD` + `Fire2x2` | 12 |
| `ItmNavStationModsAtmo` | 12, including `FlightDynamics`, no mooring or sensors | 9 |
| `ItmNavStationModsTorchCombat` / `…2`, `…TorchShip2` | 13–17 | 8 |

`ItmNavStationModsRandom*` are a different shape and easy to misread: `…RandomPod` is a
**single weighted pick** (`A=0.125x1|B=0.125x1|…`), not a set — it is what a salvage
container or a shop rolls, not what a console is fitted with.

> **Ported in Ostraplan:** `NavConsole`. The spawner itself is **not** reproduced (it is
> an `IsSystem` object, dropped on import like every other): Ostraplan bakes
> `NavConsole.StandardModules` (the `Pod` set plus `CoursePlot`) as literal contained
> items, which works identically on the template path (`ShipExport`) and the save path
> (`SaveEdit`), and needs no spawner behaviour. `NavConsole.StockEmptyConsoles` fits them
> at **import** to any console that arrives without modules — a pre-1.0 ship (consoles had
> no inventory at all before 1.0) or a core template, whose modules were in the dropped
> spawner — so the planner shows what will actually spawn. A console placed from the
> palette is filled by the same list at export/inject time. All three gate on
> `NavConsole.NeedsModules` (no **loose** cargo), never on "no cargo": the slotted data
> chip above made every imported console look stocked, and they exported with a chip and no
> screens.

### The console screen is anchor rects, and a module with no room is shelved

Where each module appears on the console is **not** derived from its inventory cell. The
console's own `NavModConfig` prop map holds `module key → "xMin|yMin|xMax|yMax"` (anchors in
0..1, y up), keyed by the module's **GUI prefab** (`NavModMap`), not its item def name.
`GUIOrbitDraw.LoadModules` walks the modules in the console, reads that map, and falls back
to the module's own `strDefaultPos` when the console has no entry (or an empty one).

`EditMenu.DoesModFit` then decides whether it stays: a rect outside 0..1, or one strictly
overlapping a module **already placed**, gets `DisableMod()` — the module remains in the
console and in the edit menu's tray, it just is not on screen. So **the order the modules
are walked in decides who keeps a contested slot**, and that order is container order.
`SaveModules` writes the inverse: every key blanked, then the anchors of each active module
at 2dp, which is why `""` is the game's own "in the tray" marker.

Two consequences worth knowing:

- **Stock rects collide by design.** `NavModMooringControl` and `NavModFlightDynamics` are
  the same rect; `NavModSensorsMFD`, `NavModTorchDrive` and `NavModWeaponsMFD` are another.
  No stock loot set carries both of a pair — except `ItmNavStationModsTorchShip` (18 core
  consoles), where `NavModCoursePlot` (`0|0.4|0.25|0.8`) swallows `NavModTargetData`
  (`0.15|0.4|0.25|0.8`), so one of those two loads shelved in vanilla.
- **The pod set tiles the screen exactly**, leaving one free `0.15×0.4` strip at
  `0|0.4|0.15|0.8`. Nothing else fits it at its stock size, so a 14th module has to be
  resized or shelved.

> **Ported in Ostraplan:** `NavConsole.Arrange` reproduces `LoadModules` + `DoesModFit`
> (defaults, bounds, strict overlap, first-come order) and `NavConsole.ConfigEntries` emits
> the `SaveModules` shape, which `ShipExport` and `SaveEdit` bake onto the console so the
> contested slots are decided by the design rather than by container order. It never invents
> a rect or resizes a panel: `StandardModules` is ordered by screen priority, and the two
> situational modules it carries beyond the stock 13 (course plot, flight dynamics) ride in
> the tray. On a **kept** console the write fills only keys the save leaves empty, so a
> screen the player arranged in game survives the write-back — unless the user arranged that
> console here, in `NavArrangeWindow` (the planner's stand-in for the console's edit menu),
> in which case their layout is written whole. A stored layout lives on
> `Placement.NavLayout` and in the `.oplan`; a console left alone stores nothing and follows
> the computed arrangement. The one deviation from the game: it lets a module be dropped
> overlapping another and resolves it by shelving one on the next load, while the dialog
> snaps such a drop back, since a design should not record an outcome decided later.

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
around every subsequent **top-level** item (`Vector2(-1f, 1f)`; `IsRoom` COs get
`Vector2.zero` and pad nothing). Which items qualify, and the load-state gate that
decides whether any of this runs, are in [§17](#17-ship-serialization-templates-and-saves)
and apply to a template export just as much as to a save. So the loaded grid is always:

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

### A cargo stack lives in the head CO's `aStack`, on this path too

`CondOwner.PostGameLoad` is the same code a save load runs, so the stack rule from
[§17](#17-ship-serialization-templates-and-saves) applies verbatim to an inject: a stack
is re-collected **only** from its head CO's `aStack`, and a lead item whose copies are
merely parented to it comes back as N loose singles in the container. What differs from a
template export is only what a save load does *not* need — a save keeps every `strID`
already, so `bForceLoad` and the `aCondOverrides` marker are template concerns.

The head's saved `aStack` is authoritative only while the stack is untouched. Adding to a
stack appends authored members under the save's own head, and removing from one takes
members out of `aItems` through the drop set, so the field has to be **rewritten from the
members that actually survive** rather than left as the save wrote it. Writing the
members but not the list produced the reported "a hundred rounds of ammo arrive as a
hundred separate bullets"; leaving them out of the descent entirely lost every round
added to ammo the ship already carried.

### `dimensions` is display-only but locale-sensitive

The game writes it with `((float)nCols * 0.32f).ToString("#.00")`; formatting with a
comma-decimal locale emits `"15,36m x 11,20m"` into the save. Use `InvariantCulture`.

> **Ported in Ostraplan:** `SaveEdit` (inject), `SaveEditImport` (context). The frame is
> written as `bbox(item footprints) ± one-tile margin`; crew `nDestTile` is recomputed on
> any reframe; room COs are dropped (`SaveEdit.RoomCoIds`). Cargo stacks are relisted by
> `SaveEdit.SetStackMembers` from the members `EmitCargo` emitted. Asserted against every
> local save (`SaveEditFrameTests`), plus `SaveEditInjectSyntheticTests` for the stacks.

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

### A ship's picture is a file named after the ship

Everywhere the game shows a ship portrait it resolves the art from
`images/ships/<strName>/`, keyed on the ship's **`strName`** and nothing else — not the
`data/ships` file name, not the `publicName`, not the mod folder. `DataHandler.LoadPNG`
searches `<modPath>/images/` across every loaded mod, most recently loaded first, then
the core streaming assets, so **a mod's image overrides core's for the same name**. That
is what lets a replacement export supply its own picture for the ship it replaces.

Two call sites, and they behave very differently on a miss:

- **Chargen** (`GUIChargenCareer.PageEvent`) loads exactly one file by name,
  `/ships/<strName>/<strName>.png`, where `<strName>` is the first entry of the `…Reward`
  loot's `aCOs`. There is **no fallback**: `LoadPNG` returns `Resources/Sprites/missing`,
  which is the red X, and the panel shows it full size next to the ship's stats.
- **The broker kiosk** (`UsedShipListEntry.SetData`) loads the whole folder with
  `LoadPNGFolder`, treats the one file whose name **contains** the ship's `strName` as the
  main image and every other file as a room thumbnail, and falls back to a generated
  silhouette when the folder is empty. So a missing folder degrades here rather than
  breaking.

The game writes these itself from the ship editor (`GUIShipEdit.SaveShipEdit` →
`ScreenshotUtil`): an 800×600 crop of an orthographic camera render with LoS and CRT off
and loot spawners hidden, on black. One image for the whole ship plus one per room, named
by the room's spec `strName` with `_1`, `_2` … appended for repeats — the suffix matters,
because the broker recovers a thumbnail's room icon by stripping at the first underscore
and looking the remainder up in `data/rooms`. Void rooms, blank-spec rooms and rooms of
three tiles or fewer get no image.

> **Ported in Ostraplan:** `ShipCanvas.RenderGamePreview` draws the set from the design's
> own sprites at the same size and framing conventions; `ShipExport.Write` files it. Only
> the mod destination writes art: a ship put into a save is a loaded ship, and the game
> screenshots those itself.

### Derelict rings (world generation, not gameplay)

`star_systems/star_system.json` carries an `aSpawnDerelictRings` array. Each entry names a
`strLootShipType` — an ordinary `strType: "ship"` loot pool — plus the body to spawn
around, a count range, radii, and an owner/faction. The K-Legrange field alone is 40–60
ships around `1036 Ganymed`.

Three consequences a planner has to respect:

- **A derelict pool takes exactly the same override a broker kiosk does.** Both are a
  single `|`-delimited `aCOs` pick, so `KioskExport.BrokerPoolOverride` works verbatim.
- **It is world generation, so a mod only reaches a NEW GAME.** Rings are populated when
  the world is built; an existing save never grows one.
- **Being a wreck is not in the ship file.** All **220** core ship templates carry
  `DMGStatus = 0`. The spawner marks the ship derelict and `Ship.BreakIn` damages it on
  first Edit-load, so an export aimed at a ring should bake **no** wear of its own or the
  two compound.

`RandomDerelict` is a chooser, delegating `Small=0.30 | Medium=0.35 | Big=0.35`.
`RandomDerelictVenus` is **also** a chooser, delegating to `RandomScavShipVNCA` (0.85) and
`RandomScavShip` (0.15) — its own `aCOs` is empty — so the honest write target for "put my
ship in the Venus fields" is the VNCA leaf, not the composer.

The size bands **overlap heavily**, measured by member part count against 1.0.0.7:

| Pool | Members | Parts (min–max) | Median |
| --- | --- | --- | --- |
| `RandomDerelictSmall` | 12 | 107–800 | ~252 |
| `RandomDerelictMedium` | 11 | 319–2509 | 348 |
| `RandomDerelictBig` | 21 | 520–5852 | 2323 |

No threshold separates them, so any claim that a given hull "is" Small or Big would be
invented. Nearest-median is the most a planner can honestly offer, with the ranges shown
alongside it.

### Putting a ship into a save directly (ownership, placement, and one lethal null)

A save's ships are loaded by **enumerating every file under `ships/`** in the zip
(`CrewSim.DoLoadGame`), not from a manifest: `objSystem.dictShips` is written on save and
never read back. So a new `ships/<RegID>.json` is picked up simply by existing, and unlike
the template path `strRegID` **is** honoured (`_SpawnShip(bTemplate: false, …)`).

**Ownership lives in two places and needs both.** `JsonShip` has no owner field at all.

| Where | Read by | Consequence if missing |
|---|---|---|
| `objSystem.dictShipOwners` (flat alternating `[regID, ownerCOID, …]`) | `StarSystem.GetShipOwner`, hence the P.A.S.S. ferry filter and the broker's sell list | `GetShipOwner` returns the literal `"UNREGISTERED"`; the ship is unreachable and unsellable |
| The owning CO's `aMyShips` | `CondOwner.OwnsShip` | Crew pledges, `bTargetOwned` interactions, fire response and fast-forward all treat the ship as somebody else's |

`CondOwner.ClaimShip` refuses to claim a station or hidden-station ship for an `IsPlayer`
CO, which is why an apartment (`objSS.bIsBO = true`) never enters `aMyShips` and rests on
`dictShipOwners` plus its residence conds instead.

**Placement.** `GUIShipBroker.OnPurchaseConfirm`'s no-free-port fallback calls
`SetSituToRandomSafeCoords` with radii `2.005376131819503E-08` / `3.342293553032505E-08`
AU, which are exactly **3.000 and 5.000 km**, at a flat random bearing, retrying up to 25
times against bodies and other ships. `objSS.vPosx/vPosy` are **absolute system coordinates
in AU**, not offsets from `boPORShip`: a docked ship shares its host's coordinates exactly
(verified: separation 0.0). The ferry's own cut-off (`GUIPDAFerry.ShowRequest`) is
`3.342293712194078E-05` AU = **5,000 km**, and it lists a destination only if it is a
station or a ship whose `GetShipOwner` matches the crew's CO.

> **`ShipSitu.aPathRecent` is the one field that must not be omitted.** Every collection on
> `Ship` is created in its constructor and merely *replaced* when the save carries one, so
> leaving `aCrew`, `aLog`, `aWPs`, `aProxIgnores` or `aTrackIgnores` out is safe. But
> `ShipSitu(JsonShipSitu)` does **not** chain to the constructor that calls `InitPath()`, so
> `aPathRecent` is built **only** when `aPathRecentX` is present. `StarSystem.UpdateShip`
> then ends with an unguarded `objSS.aPathRecent.Count` (IL_0409), which throws every frame
> — and because that exception escapes `StarSystem.Update` into `CrewSim.Update`, it stops
> the **entire simulation**, not just the offending ship: the player cannot move and every
> stat runs red. Seed one entry (position at the current epoch), as `LogPath` would.

Two fields the game re-derives, so they need no fabrication: `objSS.size` (recomputed from
the floor plan by `Ship.InitShip`) and `origin` (re-rolled from the `TXTShipOrigin<first
letter of RegID>` loot whenever it reads `"$TEMPLATE"`, on the save path as well as the
template one).

> **Ported in Ostraplan:** `SaveGrant` (build + write), surfaced as the Export dialog's
> "Into a save game" tab. Writes to a copy of the save; the original is never opened for
> writing. **Re-verify per patch:** the two spawn radii, the ferry range, and whether
> `aPathRecent` is still the only json-gated field on `ShipSitu`.

> **Ported in Ostraplan:** `KioskExport` (`AppendShipToPool`, `PinShipToPool`),
> `StartingShipExport`. Where another ship mod overrides the same pool, whole-object load
> semantics would drop one side; the resolution is Ostrasort's per-item-union `--patch`.

### Apartments are hidden stations

Recorded because it is asked for regularly, and the answer is not obvious. An apartment is
an ordinary ship record spawned as a **station**: `GUIShipBroker.OnPurchaseConfirm` calls
`SpawnShip(…, isStation: true)`, then `HideFromSystem`, `LockToBO(station)` and
`bIsBO = true`. Six stock templates across five station brokers, priced
`sum(aRooms.roomValue) × discount × 10`.

The RegID is `<STATION>|RES_<n>` and **the pipe is load-bearing**:
`DataHandler.GetTransitConnections` truncates at it, and `TargetsWildCard` is
`strTargetRegID.Contains("|")`. A RegID without it means no transit route in or out.

The real blocker on doing anything with these in a planner is not the plumbing. The parts
involved (`ItmKioskTransit02` / `03b`, `ItmDockSys02Closed`, `ItmSink01Station`) have
**no install, uninstall or dismantle recipe at all**, so there is no bill of materials, no
cost and no socket rule to port. Adding those recipes is a data mod, which is the answer
to give: see [SCOPE.md](SCOPE.md#often-the-honest-answer-is-thats-a-mod). Verified against
the 0.16-era decompile while closing
[issue #12](https://github.com/Valtora/Ostraplan/issues/12).

---

## 20. Propulsion (RCS and the torch drive)

Nothing outside a **nav console** shows any of this. The figures live on `Ship` and
`FusionIC` and surface only through `NavModReserves` (delta-v, reaction mass),
`NavModCoursePlot` (max thrust in G, reactant hours for a plotted course) and
`NavModTorchDrive` (reactant hours). Neither the ship rating, the broker, nor the build
UI exposes them, which is why a planner has to recompute the lot.

### RCS

```
RCSAccelMax        = 100f * (0.728f * fRCSCount) * 5.26077E-09f / massIncludingDockedShips   [AU/s²]
DeltaVRemainingRCS = RCSAccelMax * GetRCSRemain() / 0.7279999852180481 / fRCSCount           [AU/s]
```

`fRCSCount` is `Σ StatThrustStrength` over installed, non-`IsOff` clusters
(`TIsRCSClusterAudioEmitter`) — the **same** number behind the Maneuver grade (§10).
Accelerations render as G with `/ 6.6845869117759804E-12 / 9.81`; distances render
through `MathUtils.GetDistUnits`, which takes AU.

Collapsed, with mass in kg:

- **thrust** = `57 293.6 N` per unit of `StatThrustStrength` (each core cluster declares 1.0)
- **delta-v** = `78 700.0 m/s × reactionMass / mass`

**The thruster count cancels out of delta-v.** Fitting more clusters buys acceleration
and no range whatever. And gas mass never enters `StatMass` (`GasContainer` tracks
`fGasMass` separately and writes no cond), so burning reaction mass does not lighten the
ship: the model is linear, not a rocket equation.

**Reaction mass is plumbing, not inventory.** `GetRCSRemain` / `GetRCSMax` walk each
installed, switched-on **distributor** (`TIsRCSDistroInstalledOn`, i.e. `IsRCSReg` +
`IsInstalled`, not `IsOff`), then each of its `GasInput*` map points, and take the
containers found there (`TIsRCSValidInput` = requires `IsAirtight`, forbids `IsHuman` /
`IsSystem`). A canister in a rack feeds nothing.

- Remaining is `GasContainer.Mass`, the total of **every** gas by mass — so an O2 tank is
  reaction mass too (the vanilla Katydid).
- Maximum is always priced as N2: `StatGasPressureMax × StatVolume / 293 / 0.008314` mol
  × the N2 molar mass, whatever the tank actually holds. That matches a fuel-kiosk refill.
- `ItmRCSDistro01` is a **3×3 socket grid whose adds are centre-only**, with its four
  `GasInput` points on the cardinal neighbours of its centre — cells its own mask leaves
  Blank, so a 1×1 RTA legally occupies one. `ItmRCSDistro02` has a single point at `(8,8)`.
- **The game de-duplicates nothing.** A tank spanning two GasInput points, or shared
  between two distributors, is counted once per hit. That double count is in
  `GetRCSRemain` itself, which the flight model reads, so it is behaviour and not a
  display artefact.

> **`NavModReserves` double-scales the docked ratio.** `GetDeltaVRemaining(bAllowDocked:
> true)` multiplies `DeltaVRemainingRCS` by `Mass / totalDockedMass` — but `RCSAccelMax`,
> which `DeltaVRemainingRCS` is built from, already divided by that same total. Under tow
> the console therefore under-reads delta-v by `(M/M_total)²`. Undocked the factor is 1 and
> the two agree. `Ship.DeltaVRemainingRCS` is the value the autopilot and `AIShipManager`
> plan against, so it is the authority.

### Torch drive

`FusionIC.Run` counts the modules sitting on the reactor core's `Module01..NN` map points
(`ItmFusionReactorCore01*` declares **12**; `FusionIC.Init` stops at the first name the core
does not declare). A module qualifies via `TIsFusionModule` (`IsInstalled`, forbidding
`IsPowerConduit` / `IsWall` / `IsFloorGrate` / `IsDamaged`) and is then classified by its
`IsFusionLaserArray` / `IsFusionPelletFeeder` / `IsFusionCapacitor` / `IsFusionFuelRegulator`
cond. Lasers, feeders and regulators are skipped when `IsOff`; **capacitors are counted by
list length regardless of state**. On a pristine design every module's health term is 1, so:

```
StatICPellMax = 2 * min( min(feeders, 2*regulators), min(lasers, 2*capacitors) )
```

A laser with no capacitor to drive it, and a feeder with no fuel regulator, contribute
nothing. With `veRatio = StatICVe / 70 500 000`:

```
fFusionThrustMax = 332499980926.5137 * veRatio / 70500000 * StatICPellMax * 393.06358381502895   [N]
massFlowMax      = 699999988079.071 * veRatio / 70500000 / 70500000 * 393.06358381502895 * StatICPellMax   [kg/s]

GetMaxTorchThrust(f) = Lerp(1, PelletMax, f) / PelletMax * f * fFusionThrustMax / Mass * 6.6845869117759804E-12
fShallowFusionRemain = min( StatLiqD2O / (massFlowMax * 0.667f), StatSolidHe3 / (massFlowMax * 1f) )   [s]
```

At limiter `f = 1` the first factor is 1, so **max thrust in G** is
`fFusionThrustMax / Mass / 9.81`, and the console's reactant clock is
`fShallowFusionRemain / 3600` hours at full flow (a lower cycle lasts proportionally
longer). Live thrust additionally scales by `StatICCoreTemp / 0.725`, which is 1 at the
ideal core temperature.

**Reactants are matched by condowner name, exactly.** `CODicts.GetTriggeredCOListByType`
keys on the CO name, so only `ItmCanisterLH02` (44,722.8 `StatLiqD2O`) and
`ItmCanisterLHe02` (5,216 `StatSolidHe3`) count. `ItmCanisterLHe01` is the **cryo** feed
(`CTCryo`) and carries no He3 at all; a modded or reskinned tank under any other name is
invisible to the reactor however much fuel it declares.

> **Only the ignited core carries `StatICVe`.** `ItmFusionReactorCore01Ignition` has
> `7.05e7`, `ItmReactorIC03Ignition` has `1.05e7`, and the installable `…Off` form has none
> — while `…On` is the condowner-less orphan item (§12). Since every planned ship's reactor
> is unlit, a literal read reports zero thrust for every design, so the placed core must be
> resolved through to its `…Ignition` counterpart. The RCS side needs no such help:
> `Catalog.PreferPoweredState` already builds the switched-on cluster and distributor.

### What a planner cannot reproduce exactly

`GetCOsAtWorldCoords1` is a **physics raycast** (`Physics.RaycastAll`) against colliders,
so a map-point lookup is one of the in-game-only predicates of §5.7. Socket-footprint
coverage is the closest headless stand-in and is exact for the 1×1 RTAs that actually feed
an RCS system; it can over-claim only where a part's socket grid is much larger than its
collider (the 7×7 / 3×3 fuel canisters, §4). Measured against the shipped fleet, **108 of
the 111 core ships carrying a distributor** resolve a real fed RCS system through it.

> **Ported in Ostraplan:** `Propulsion` (`PropulsionEstimate` for the derived figures,
> `Estimate` for the scans), surfaced on the Ship Rating report. Mass is the game's own
> top-level walk (placed parts, no `IsInstalled` filter, plus loose deck items), which is
> deliberately **not** `ShipRating.Mass` (§10, installed parts only) — the report shows both
> and says why. The `NavModReserves` double-scaling is **not** reproduced; the towed-mass
> input scales once, like `DeltaVRemainingRCS`. **Re-verify per patch:** every constant
> above, the pellet-max pairing, and the `GasInput` / `Module` map-point layouts.

---

## 21. Crew walkability and interaction reach

Whether crew can *get somewhere*, and whether they can *use what is there*, is a static
function of tile conditions plus two per-interaction data fields. No simulation is needed.

### Which tiles are walkable (`Tile.IsWalkable`)

Evaluated in this order, all against the tile's accumulated conditions (§3):

| # | Test | Blocks when |
|---|---|---|
| 1 | `IsForbidden` | the tile carries `IsZoneForbid` **and** its `JsonZone.Matches` the crew member |
| 2 | `IsBurningHazard` | `IsTileBurning` and the crew is not `IsFireproof` |
| 3 | wall | `IsWall` **and not** `IsPortal` |
| 4 | portal | `IsPortalStuck`; **or** (without airlock permission) a closed door (`IsWall`) with a pressure differential — `Pathfinder.CheckDoorPressure` |
| 5 | fixture | `IsObstruction` **and** `IsFixture` (`bPassable = !IsObstruction`) |
| 6 | EVA gravity | `IsEvaTileWithGravitation`: ship not grounded, `Gravity ≥ 0.33`, and the tile is `IsEVATile` or not a `TIsShipTile` |

Consequences that are easy to get wrong:

- **No floor is required.** Empty in-grid tiles carry none of these conditions, so they are
  walkable. That is the spacewalk case, and `Ship.GetTileAtWorldCoords1` returns an in-bounds
  tile whether or not it is a ship tile (its `checkIfShipTile` gate is an early return, not a
  filter). Rule 6 is what suspends this, and only under gravity.
- **Rule 5 needs both conditions.** An open door is `IsObstruction` with no `IsFixture`, so it
  is walkable; an under-floor rack (`TILFloorFixture` → `IsFloorSealed` + `IsFixture`, no
  obstruction) is walkable too.

### Door state IS load-bearing here (unlike rooms and rating)

§8 establishes that open and closed doors give the same rooms and the same airtightness. That
does **not** carry over to walking. From `data/items`:

| Def | Socket add | Walkable |
|---|---|---|
| `ItmDoor01Open` / `…OpenOn` / `…OpenOnLocked` / `ItmHatch01Open` | `TILPortalOpen` | yes |
| `ItmDoor01ClosedOn` (powered) | `TILPortalClosed` | yes — crew open it |
| `ItmDoor01Closed` (unpowered), `…ClosedOnLocked`, `…ClosedDmg`, `ItmDockSys03ClosedDmg` | `TILPortalClosedStuck` (adds `IsPortalStuck`) | **no** |

An unpowered, locked or damaged closed door genuinely seals a section off.

### Connectivity (`Ostranauts.Pathing.JumpPointSearch`)

The game pathfinds with jump-point search over 8 directions, caching an `IsWalkable` grid.
For a connectivity partition only the adjacency rule matters: the four cardinals always, and
a diagonal only when at least one of the two orthogonals it cuts between is walkable
(`Jump` returns `INVALID_POINT` when both behind-orthogonals are blocked). `Pathfinder`
itself adds runtime concerns a planner has none of: other crew's occupied tiles, per-room
cost penalties, failed-attempt memory.

### Reaching a device (`Interaction.Triggered`)

- A condowner names its actions in **`aInteractions`** (names into `data/interactions`). A
  cooverlay skin may substitute entries via `aInteractionsReplace` (`COOverlay.Init`).
- Each interaction carries **`strTargetPoint`** (the map point on the target the crew walks
  to, almost always `"use"`; `null` or the `REMOTE` sentinel means no approach is needed) and
  **`fTargetPointRange`** (how far away they may stand). The label shown is `strTitle` —
  interactions carry no `strNameFriendly`.
- Range is **Chebyshev**: `TileUtils.TileRange` = `max(|dx|, |dy|)`, rounded.
- Ranges are per-interaction, not per-device class: `GUINavStation` 0, `GUIAirPump` 1,
  `GUICooler` / `GUIHeater` / `SeekSleepSimple` / `GUISensor` 2, `GUIReactor` 3,
  `ACTDecorAdmire` 4. In core, **315 condowners carry both an interaction and a `use` point**,
  **119 of them buildable**.

### Where the crew actually stands is a two-tier choice, and far looser than the range

This is the part that is easy to get wrong, because the strict-looking range test in
`Interaction.Triggered` is **not** what gates a normal interaction:

1. **`Triggered`'s range + LOS test only runs on the `bNoWalk` branch** (13 interactions in core).
   For everything else it runs a *path* check, and only when the caller passes `bCheckPath: true`
   — which is AI work assignment (`WorkManager`, the pledges, `JsonJobSave`), never the player's
   own menu. A player-issued order is not range-gated at all; the crew simply walks.
2. **The walk destination comes from `Pathfinder.GetClosestWalkableDestination`**, a cost BFS
   *outward from the target point*. It sizes its acceptance band with **`Mathf.CeilToInt`**
   (so a 1.5-tile interaction genuinely reaches two tiles) and prefers a tile that is not a
   wall, not **`IsFixture`**, not burning and not occupied, with line of sight.
3. **When that search finds nothing, the game does not give up.** `GetPath` runs
   `closestWalkableDestination.Add(destination)` and paths to the target tile itself, which
   succeeds for anything `Tile.IsWalkable` admits — no sight test on that path, since it goes
   straight to the jump-point search. So the `IsFixture` rejection is a *preference*, not a
   requirement: a cargo bay floored wall to wall in under-floor racks is still usable.
4. `SetGoal2` then retargets `tilDest` to the **end of the path it actually found**, and the
   completion gate is `Pathfinder.InRange()` against *that* tile — not against the target point.

- Line of sight is `Visibility.IsCondOwnerLOSVisibleBlocks`, run from the target's **`LOS`**
  map point (falling back to its centre — `CondOwner.GetPos` returns `tf.position` for an
  undeclared point). Anything within 1 unit is visible outright; otherwise each non-glass
  `aShadowBoxes` occluder (§16) is segment-tested, skipping boxes owned by the target and any
  box containing either endpoint. **The destination search calls it with
  `bIgnoreEndpoints: true`** (grazing a box edge does not block) and, for axis-aligned targets,
  defers to `IsCondOwnerLOSVisible` — a `Physics.RaycastNonAlloc` that blocks only on installed
  walls and closed portals. Only the `bNoWalk` branch uses the strict `bIgnoreEndpoints: false`
  form.

> **Ported in Ostraplan:** `WalkNetwork` (walkable mask, zone labelling, device reach),
> `LineOfSight` (the sight test), `LightNetwork.Occluders` (the shared occluder source),
> surfaced as the **WalkViz** overlay (`K`) and as advisory findings in the Law report.
> Two exclusions keep the output honest rather than merely literal, both measured against the
> 220-ship corpus: **mineable terrain** (`IsMineable` — 28 rock/ice defs, none buildable) names
> an `ACTMine` interaction and so parses as an operable fitting, but a block inside an asteroid
> is unreachable by definition (Port Mojave alone: 1,811 false findings); and a device with no
> interior standing tile is re-tested with the exterior counted and reported as **EVA-only**
> rather than unusable, which is how all hull-mounted kit is reached (the "Hand Of God" rig:
> 33 of 35).
>
> **Two further deviations, both because the game escapes into an in-game-only predicate.**
> A part embedded *in* the hull line (anchor tile `IsWall`: sensors, antennas, wall lights, ship
> weapons — the same ~87 defs that need the room-membership use-point fallback, §9) has its
> sight origin inside the wall, so every ray out crosses the neighbouring wall tiles' boxes; the
> game escapes via the raycast, which does not register the collider it starts inside, so
> Ostraplan grants sight to embedded parts and leans on range and walkability alone. And a def
> that declares **no** map point for its interaction's target resolves to its own body, so a
> range-0 reading would demand standing inside it — unsatisfiable by any layout, and therefore
> noise rather than a finding, so an undeclared point widens to a minimum radius of 1.
>
> Corpus effect of the whole chain: **94 of 14,417 devices read blocked (0.7%)** and 300 as
> EVA-only, against 2,665 for a literal first reading. What remains concentrates in multi-tile
> furniture (bar and conference tables) and tightly packed exterior cargo pods.
> Rules 2 and 6 are **not** modelled (runtime-only: fire, and the ship's world position).
> Rule 4's pressure half is approximated by the room partition — a portal with a Void room on
> one side and a sealed room on the other is reported as EVA-only rather than treated as a
> wall, since crew with airlock permission do cross it. Rule 1 is a user toggle, because the
> game's test is per crew member and a plan has no crew.
>
> **Re-verify per patch:** the `IsWalkable` order and conditions, which door defs carry
> `TILPortalClosedStuck`, the JPS diagonal rule, and the `strTargetPoint`/`fTargetPointRange`
> pairs above (`WalkNetworkTests` asserts the last two against live data).

---

## 22. The ship diagnostic (`ShipStatus.PrintStatus`)

The game has exactly one place where it enumerates the systems a working ship is expected
to carry: the **Diagnostics** module on a nav console
(`Ostranauts.ShipGUIs.NavStation.NavModDiagnostics`). Ticking its status box runs
`ShipStatus.PrintStatus`, which fills sixteen fixed rows named by `ShipStatus.aNames` and
wraps each value in `<color=#009900>` (good) or `<color=#990000>` (bad). It is the game's
own ship checklist, and it is reachable only by sitting at a console on a ship that already
exists — which is why a planner has to recompute it.

The module is a physical item like every other nav module (`ItmNavModDiagnostics`; how a
console gets its modules is §17), so a console without it shows no diagnostic page at all.

### The sixteen rows

| # | `aNames` caption | Source | Green when |
|---|---|---|---|
| 0 | `VESSEL RATING CODE:` | `Ship.GetRatingString()` (§10) | *(no colour — informational)* |
| 1 | `VESSEL MASS:` | `Ship.Mass` (§20 — top-level walk, no `IsInstalled` filter) | *(no colour)* |
| 2 | `TRANSPONDER:` | `Ship.strXPDR`, else `TIsXPDRInstalled` present | a registration ID is set |
| 3 | `TRANSPONDER ANTENNA:` | `TIsXPDRAnt`, minus `IsOff` | ≥1 switched on |
| 4 | `NAV STATION:` | *hardcoded* `ONLINE` | always |
| 5 | `REACTOR:` | `TIsReactorIC` + `IsInstalled`, `StatPower != 0` | the core is **lit** |
| 6 | `REACTOR HE3:` | Σ `StatSolidHe3` over `TIsCanisterLHe02Installed` | `> 100` kg |
| 7 | `REACTOR D2O:` | Σ `StatLiqD2O` over `TIsCanisterLH02Installed` | `> 1000` kg |
| 8 | `RCS THRUSTERS:` | `TIsRCSClusterInstalled`, minus `IsOff` | **`> 1`** switched on |
| 9 | `RCS DISTRIBUTOR:` | `TIsRCSDistroInstalled`, first not `IsOff` wins | ≥1 switched on |
| 10 | `RCS REMASS:` | `Ship.GetRCSRemain()` (§20) | `>= 200` kg |
| 11 | `BACKUP POWER:` | `Powered.PowerConnected` **at the console** | `>= 20` kWh |
| 12 | `LIFE SUPPORT WORKING O2 PUMPS:` | fed / installed, `TIsAirPump02Installed` | ≥1 fed |
| 13 | `LIFE SUPPORT O2 STORES:` | O2 mass under those pumps | `> 35` kg |
| 14 | `LIFE SUPPORT HEAT:` | `TIsHeater01Installed`, first not `IsOff` wins | ≥1 switched on |
| 15 | `LIFE SUPPORT COOL:` | `TIsCooler01Installed`, first not `IsOff` wins | ≥1 switched on |

Every cutoff is a **literal compiled into the DLL**, invisible to data diffing — the same
hazard as the rating cutoffs (§10). Row 8's `num2 > 1.0` is the one people misread: a
single healthy thruster reads red, because one thruster can push but not turn the ship.

### Two rows are measured somewhere specific, not ship-wide

**Backup power** is `COSelf.GetComponent<Powered>().PowerConnected` on the **console**, so
it totals only sources whose flood reached the console's own `aInputPts` tiles
(`Powered.UsePower` → `QueryPower` over `Tile.aConnectedPowerCOs`, which
`TileUtils.GetPoweredTiles` files per source, §13). A battery the conduit network never
reaches counts for nothing. `QueryPower` skips a non-positive charge.

**O2 stores** are read by `ShipStatus.GetO2UnderPump`: for each switched-on pump, the
`TIsRTAO2Installed` can sitting at its `GasInput` map point. A hold full of oxygen with no
pump plumbed to a can reads `0.00 kg`. Each core air pump declares exactly **one**
`GasInput` point, which collapses the method's per-point last-writer-wins accumulation
(it *assigns* `Item1` rather than adding to it) to a straight sum over pumps — so the
quirk is unreachable on core data. This is the same scan as the ×3 atmo bonus (§11.3).

### What a planner cannot reproduce exactly

Four rows ask about a *running* ship rather than a design, and are answered differently:

- **Row 4** is hardcoded `ONLINE` because the page is being read at that very console, so
  it cannot report its own absence. A design can easily have none, so it becomes a real
  `TIsNavStationInstalled` presence test.
- **Row 2** prints the registration ID the game assigns at spawn. A plan has none, so an
  installed, switched-on transponder reports installation instead.
- **Row 5** needs `StatPower != 0`, which the fusion sim sets once the core is lit; **no
  reactor def carries it**. A planned or freshly bought reactor is always installed unlit,
  so a literal port would read `OFFLINE` on every design ever made. Same divergence, same
  reason, as reading `StatICVe` off the ignited core (§20).
- **Rows 6, 7, 10, 11, 13** are quantities, reported as-spawned — what a newly built or
  newly bought ship reads, not a claim about a save in progress.

> **Ported in Ostraplan:** `ShipDiagnostics` (`Build` for the rows, `Analyze` for the whole
> run), with the cutoffs in `ShipDiagnosticsThresholds` and the readout on its own
> **Diagnostics** toolbar action. Backup power goes through
> `PowerNetwork.PowerConnectedTo`; O2 through `ShipValue.ScanO2Supply` (which
> `CountO2Pumps` now delegates to); mass and remass through `Propulsion`. The four
> divergences above are stated in the report's own text, not hidden.
> **Re-verify per patch:** `aNames` (captions and order), every cutoff in the table, and
> whether any row's source trigger changed — `ShipDiagnosticsTests` pins the captions and
> the cutoffs, so drift surfaces there.

---

## 23. Atmospheric flight (lift, drag and rotors)

The game has a real atmosphere and a real flight model, and both are almost entirely
invisible. `NavModFlightDynamics` prints lift, drag, angle of attack, gravity and
pressure on a flying ship, and nothing anywhere shows what a *design* would do. The
whole model is driven by two numbers the ship accumulates as parts are installed, plus
per-body atmosphere tables in the game's own data.

### The atmosphere is data

`data/star_systems/*.json` gives every body an `aAtmosphericValues` array
(`JsonAtmosphere`): partial pressures in kPa per gas, a temperature in K, and
`fMaxAltitude` — the band's top **measured from the body's centre**, so Venus's
"48-52km" band has `fMaxAltitude` 6104 against a 6052 km radius. On stock **1.0.0.9**,
eight bodies have tables: Venus (10 bands, to 350 km), Earth, Mars, Titan, Jupiter,
Saturn, Uranus, Neptune. Everything else is vacuum.

`BodyOrbit.GetAtmosphereAtDistance` picks the first band the point is under, then lerps
**from that band towards the one above it**, across the span from the previous band's
ceiling (or the body radius, for the lowest) to this band's own ceiling:

```
t     = InverseLerp(prevCeiling ?? radiusKM, band[i].fMaxAltitude, distanceKM)
sample = Lerp(band[i], band[i+1] ?? Void, t)          // Void = all gases 0, 2.72548 K
```

So a band's authored figures are what you get at its **floor** and its neighbour's are
what you get at its **ceiling**, and above the top band the game returns vacuum with no
fade. Density is `GasContainer.GetGasDensity`: `Σ P·M/(R·T)` with `R = 0.008314` and the
same molar-mass switch the ship's own gas containers use (§11).

Gravity is `StarSystem.GetGravAccelScalar`: `fGravAccelConstant × fMassKG / r²` in AU/s².

> **`fGravAccelConstant` is `2E-44f`, and that is subnormal as a float.** Representable
> floats are spaced 1.4×10⁻⁴⁵ apart down there, so it stores **1.9618×10⁻⁴⁴** — about 2%
> under its written value. Every gravity in the game is 2% light: Earth reads 9.66 m/s²,
> Venus 8.43, Mars 3.66, Titan 1.34. A port that "tidied" this to a literal `2e-44` would
> disagree with the game on every body.

### Lift and drag

`Ship.CalculateLiftDrag` assembles the coefficients and `ShipSitu.CalculateLiftDrag` does
the work. With `size = (nCols + nRows) × 0.32 / 2` (metres) and `aero = fAeroCoefficient`:

```
dragScale  = Lerp(3f, 15f, (size - 3) / 50)
areaSide   = size × dragScale
areaFront  = areaSide / max(1, aero / 100)
area(AoA)  = Lerp(areaFront, areaSide, sin(AoA))

liftAccel  = |0.5 ρ v² × (aero / Mass) × cos(AoA) × cos(attitude)| / Mass   capped at 10 × g_local
dragAccel  = clamp(0.5 ρ v² × area(AoA) / Mass, 0, 2000)                     m/s²
```

Three things are worth stating plainly:

- **Mass divides the lift twice.** The coefficient the game forms is `aero / Mass`, and
  the force it produces is divided by `Mass` again to make an acceleration. Doubling a
  design's mass therefore **quarters** its lift. This is the single most important fact
  for anyone designing around the model, and it is not a slip in the port.
- **`v` is airspeed, not orbital speed.** It is measured against the body's own velocity
  (`vVel - bO.vVel`), so a ship keeping station with the atmosphere makes no lift.
- **Aero hull cuts frontal drag only.** The `max(1, aero / 100)` divisor means the first
  hundred points of `StatAeroLift` buy nothing, and side-on drag is never reduced at all.

`fAeroCoefficient` starts at **1** and `Ship.AddICO` adds each installed part's
`StatAeroLift` — but only past the `IsShipSpecialItem` gate that guards the whole tail of
that method. Aero hull carries 100 (1×1) or 200 (slant), so a winged ship runs into the
thousands and a bare one sits at 1. `RemoveCO` subtracts it back.

### Rotors

```
LiftRotorsThrustStrength = Σ Rotor.ThrustStrength(rotor)                  over aActiveHeavyLiftRotors
Rotor.ThrustStrength     = (IsTurboOn ? StatThrustStrengthTurbo : StatThrustStrength) × 30
CurrentRotorEfficiency   = clamp(voidRoom StatGasPressure / 100, 0, 1.5)
rotorAccel               = strength × efficiency / 149597870 / mass       [AU/s²]
```

A rotor joins `aActiveHeavyLiftRotors` on `TIsHeavyLiftRotorNotOff` (`IsHeavyLiftRotor`,
forbids `IsOff`). `ItmHeavyLiftRotor01On` declares `StatThrustStrength` 7.5 and
`StatThrustStrengthTurbo` 15, so one rotor is **225 kN**, or 450 kN on turbo. The AU
conversion works out to exactly kN → N, so the efficiency-scaled strength is the thrust
in kilonewtons.

The efficiency is read off the **Void room's** pressure, which `Room.SyncAtmoVoid` →
`GasContainer.SyncAtmo` sets to the ambient atmosphere's partial pressures every update,
so it is the ambient figure by a longer route. Rotors give nothing in vacuum and half as
much again in Venus's deep cloud layer.

`Ship.Maneuver` in `EngineMode.MIXED` adds rotor acceleration to RCS acceleration; `AUTO`
picks MIXED whenever there is rotor thrust and any efficiency at all, and RCS otherwise.
Note the rotor term is **not** divided by `fDeltaTime` while the RCS term is.

> **Ported in Ostraplan:** `Atmosphere` (bodies, band interpolation, density, gravity —
> reading `star_systems` through `DataIndex`, so a mod that adds a body or retunes Venus
> is picked up like any other data) and `FlightDynamics` (`Measure` for the design's
> profile, `FlightPoint` for one operating point), with the readout on **Design ▸ Flight
> Dynamics**. Airspeed, angle of attack and attitude are flight state rather than design
> facts, so they are user inputs; the environment defaults to the body's own figures and
> stays editable.
> **Two deliberate omissions**, both immaterial: the ship's own radius is added to its
> distance from the body before the atmosphere is sampled (tens of metres against
> kilometre-thick bands), and `BodyOrbit` converts km↔AU with 149597872 where the console's
> acceleration path uses 149597870 (one part in 7.5×10⁷).
> **Re-verify per patch:** `CalculateLiftDrag` on both `Ship` and `ShipSitu` (the
> coefficients and both clamps), `Rotor.ThrustStrength`'s ×30, `CurrentRotorEfficiency`'s
> /100 and 1.5 cap, and `fGravAccelConstant`. `FlightDynamicsTests` pins the subnormal
> constant, the per-body gravities and the Venus cloud-layer figures, so drift surfaces
> there.

---

## 24. What a canister holds (`GasContainer`)

Every airtight vessel — a canister, an RTA, a fuel tank, a drink flask, and a room's own
`Compartment` — is a `GasContainer`. It stores **one condition per gas species**,
`StatGasMol<gas>`, and derives everything else from the sum. `GasContainer.Run`:

```
StatGasPressure  = Σ mols · R · StatGasTemp / StatVolume        R = 0.008314000442624092
StatGasPp<gas>   = mols_gas / mols_total · StatGasPressure
fGasMass         = Σ mols_gas · GetGasMass(gas)                 hardcoded kg/mol switch
```

**Capacity is molar and shared, not volumetric per species.** Rearranged, a container is
full at

```
maxMols = StatGasPressureMax × StatVolume / (R × StatGasTemp)
```

and every species draws on that one budget. Which gases the moles are made of changes the
mass, the value and the reaction mass, never the capacity. The core data confirms it to the
mole: `ItmRTAO2` is 0.787 m³ at 41,400 kPa and 293 K, which computes 13,375.11 mol against a
declared `StatGasMolO2` of **13,373**; `ItmCanisterO2Small` computes 25.47 against a declared
**25.47**. So the game's own idea of "full" *is* the pressure rating, and every ordinary
canister (`ItmCanister01`, `ItmRTAO2` / `N2` / `CO2`) is the same 0.787 m³ / 41,400 kPa shell
— which is why an N2 can and an O2 can are interchangeable in practice.

Temperature is in the divisor, so the cryogenic tanks are a different animal:
`ItmCanisterLH02` at 4 K holds 607,409 mol in 40.4 m³ at only 500 kPa.

### The pressure rating is a burst threshold

`CheckPressureDifference` runs once a second per container against the room it sits in.
When `|P_container − P_room|` exceeds `StatGasPressureMax + 150` kPa the container takes
`Random(0, diff/threshold)` damage, and when its health runs out it fires
`AModeCanisterShrapnel` rays into the compartment. A container whose `StatGasPressureMax` is
0 is skipped entirely, which is why the damaged canister shells drop the stat.

### Eleven species in code, eight that actually work

`FluidStrings.moleculeNames` is `CH4, CO2, H2, H2O, H2SO4, He2, N2, NH3, O2, CO, Smoke`, and
`GetGasMass` knows all eleven. But core data declares a `StatGasMol*` / `StatGasPp*`
condition for only **eight** of them — H2, H2O and He2 have none. An undeclared condition
cannot be stored on anything: `CondOwner.AddCondAmount` returns the moment
`DataHandler.GetCond` comes back null, and `GasContainer.AddGasMols` checks the same thing
before it will move any. So those three are inert, with no error anywhere.

The reverse also bites: `Run` resolves a species' partial-pressure condition by
`FluidStrings.mol.IndexOf(cond)` and indexes `FluidStrings.pps` with the result, so a
*modded* species outside the eleven throws inside the game's own update loop. The set that
genuinely works is therefore the **intersection**: the eleven the code knows, and whatever
the loaded data declares.

### Liquids and solids are not gas

`StatLiqD2O` (44,722.8 kg on `ItmCanisterLH02`), `StatSolidHe3` (5,216 on
`ItmCanisterLHe02`), `StatLiqHe` (1,304 on the cryo feed `ItmCanisterLHe01`) and a mod's own
bulk conditions are kilogram payloads with no pressure relationship at all — those tanks'
gas side is a token 0.0001 mol of N2. The game publishes no maximum for them; the def's own
load is the only capacity figure there is. `StatSolidTemp` shares the prefix and is a
temperature, not cargo.

The two torch reactants are matched by **exact condowner name** (§20), so only
`ItmCanisterLH02` and `ItmCanisterLHe02` feed the drive however much any other tank declares.

**Nothing carries both.** Across core data and the Ostranauts mods, every def that declares a
bulk payload carries at most a 0.0001 mol token of N2 on its gas side — enough for
`GasContainer.Init` to have something to iterate, not storage. A tank is therefore either a
gas container or a fuel tank, never both, which is why Ostraplan offers a fuel tank only its
own reactant: a deuterium tank full of oxygen is weight the drive cannot use.

### Setting a container's contents from data

`JsonItem.aCondOverrides` is the mechanism, and it **sets** rather than adds:
`ApplyOverrideCondsToCO` feeds each entry to `CondOwner.SetCondAmount`. `Ship.SpawnItems`
applies it after the condowner is fully built, on both branches (top-level and contained)
and for a template spawn and a save load alike. Because every canister carries
`IsGasMolChanged`, `Init`/`Run` then recompute the pressure and all the partial pressures
from the amounts, so only the amounts need writing.

> **A condowner's own `aConds` is the wrong place for this on a *new* part.** A synthesized
> CO is written `aConds = ["DEFAULT"]`, and `CondOwner`'s init strips that marker and
> **appends** the def's starting conds to the end of the list, zeroing each condition before
> adding it. An explicit `StatGasMolO2` written there is therefore overwritten by the def's
> own 13,373 mol every time. `StatDamage` escapes this only because no def declares it.

> **Ported in Ostraplan:** `ContainerFill` (the capacity model, the shared budget, mass and
> value), `PayloadSpec`/`PayloadLine` (what a given def can hold — a def declaring any bulk
> payload is treated as a fuel tank and offered **no** gas), `Placement.Fill` (the per-part
> amounts), laid over a part's conditions once in `ShipGrid.FromDocumentFramed` so
> value, RCS reaction mass, the torch reactant clock and the rating all follow. Written out
> as `aCondOverrides` by both `ShipExport` and `SaveEdit`, and read back off a save's
> condowners by `SaveEditImport`. Core `data/ships` templates only ever override
> `StatDamage`, so template import has nothing to read and does not try.
> **Re-verify on a major game version:** `FluidStrings.moleculeNames`, `GetGasMass`'s switch,
> the `R`/`StatGasTemp` capacity formula, the `+150` kPa burst margin, and whether the data
> has started declaring H2 / H2O / He2. `ContainerFillTests` pins the capacity against the
> real defs and asserts those three are still undeclared, so drift surfaces there.

---

## Appendix A — Quick reference

- **`nLayer` is always 0; draw order is `fZScale`** — higher draws nearer, walls sit at the
  1.0 default and so draw over most fixtures (§15). `Catalog.RenderLayer`'s floor/wall/
  fixture/conduit ranking classifies the *kind* of deck element and is not the draw order.
  Between two defs sharing one `fZScale` the game answers nothing, so those terms are
  Ostraplan's own convention plus a manual override (§15).
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
- **…but door state is NOT cosmetic to WALKING** — `ItmDoor01Closed` (unpowered), `…ClosedOnLocked`
  and the `…Dmg` forms add `TILPortalClosedStuck` → `IsPortalStuck` and genuinely seal a section
  off; `ItmDoor01ClosedOn` does not, because crew open it (§21).
- **Walking needs no floor** — an empty in-grid tile is walkable (the spacewalk case); only
  `IsEvaTileWithGravitation` suspends it, and only under gravity (§21).
- **Interaction range is Chebyshev and per-interaction** — `max(|dx|,|dy|)` against the target's
  `use` point, at the interaction's own `fTargetPointRange` (nav console 0, pump 1, cooler 2,
  reactor 3). No single radius is right (§21).
- **The strict range test only gates `bNoWalk` interactions** — everything else is gated by a
  *path*, and only for AI work assignment (`bCheckPath` defaults false). A player order is not
  range-gated at all (§21).
- **The standing-tile band rounds UP** (`Mathf.CeilToInt`), and rejecting `IsFixture` is a
  preference, not a rule: when nothing clean is in range the game paths to the target tile
  itself and stands on the fixture (§21).
- **A canister's capacity is molar and shared** — `StatGasPressureMax × StatVolume / (R ×
  StatGasTemp)` moles across *all* species at once, not a slice of volume each. The pressure
  rating is a real burst threshold, and the game's own "full" sits exactly on it (§24).
- **Three of the eleven gases cannot be stored** — H2, H2O and He2 are in the code's list but
  core data declares no condition for them, so nothing can hold any (§24).
- **`aCondOverrides` SETS a condition, and a new part's `aConds` cannot** — a synthesized
  `["DEFAULT"]` condowner appends the def's own conds afterwards and overwrites anything
  written there (§24).
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
  skip non-ship files. All 220 carry `aRooms`; only 2 carry `aRating` (§17).
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
- **RCS delta-v ignores the thruster count** — it cancels out of the game's own expression, leaving
  `78,700 m/s × reactionMass / mass`. More thrusters buy acceleration, never range (§20).
- **Reaction mass is only what sits on a distributor's `GasInput` point** — a canister in a rack
  feeds nothing, any airtight tank qualifies, and every gas counts by mass (§20).
- **Torch thrust and burn time both scale with `StatICPellMax`** =
  `2 × min(min(feeders, 2×regulators), min(lasers, 2×capacitors))` (§20).
- **Only the `…Ignition` core carries `StatICVe`** — a planned ship's reactor is always unlit, so the
  placed core must be resolved through to it or every torch figure reads zero (§20).
- **Bake `aDockingPorts` + `strPrimaryDockingPortID` or a bought ship never docks** (§6).
- **Bake `Boarding`/`NotBoarding` spawners into `aShallowPSpecs` or arrivals land outside
  the hull** (§6).

### The parity corpus (ground truth)

The corpus is **220 core ship objects** that carry baked `aRooms` (roomSpec + bVoid + tile
sets), giving a 220-ship rooms **and** certification gate. Only **Babak / Babak Refit**
(both damaged derelicts) carry baked `aRating`. Notes:

- The Babaks' baked `aRating` room slot is **stale** (`aRating[2] = "18"` while their
  current `aRooms` certify 20 non-Blank rooms), so a rating check bounds the recomputed
  count against `aRooms`, not the `aRating` string.
- A faithful room partition reproduces the baked `aRooms` for **219/220**. The one
  exclusion is the Vector2 interceptor's airlock. Three others (a malformed Coffin and two
  aero slant-wall hulls) were retired at 1.0.0.7 because the game's own data changed, not
  because the port did. Portal-tile filing and exterior-void over-claim are compared
  leniently because neither affects the Law.
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
| Propulsion (`RCSAccelMax`, `DeltaVRemainingRCS`, `GetRCSRemain`/`Max`, `FusionIC` + `GetMaxTorchThrust`) | ported (map-point lookup approximates a raycast) | `Propulsion` |
| Atmospheric flight (`Ship`/`ShipSitu.CalculateLiftDrag`, `Rotor.ThrustStrength`, `CurrentRotorEfficiency`) | ported (ship radius and one AU constant dropped, §23) | `FlightDynamics` |
| Per-body atmospheres and gravity (`BodyOrbit.GetAtmosphereAtDistance`, `GetGravAccelScalar`, `GasContainer.GetGasDensity`) | ported | `Atmosphere` |
| Orbits, orbital mechanics, station-keeping | excluded (a simulation, not a plan) | never ported |
| Power connectivity (`GetPoweredTiles`, `Powered.PowerConnected`) | ported | `PowerNetwork` |
| Ship diagnostic (`ShipStatus.PrintStatus` / `NavModDiagnostics`) | ported (4 rows diverge — a plan is not a running ship, §22) | `ShipDiagnostics` |
| Crew walkability + JPS adjacency (`Tile.IsWalkable`, `JumpPointSearch`) | ported (fire and the EVA-gravity gate excluded; door pressure approximated by room Void) | `WalkNetwork` |
| Interaction reach (`Interaction.Triggered` range + LOS) | ported | `WalkNetwork`, `LineOfSight` |
| Crew pathing itself (costs, occupancy, doors opening over time) | excluded (a simulation, not a plan) | never ported |
| Device signal connections (`Electrical` GPM) | ported | `DeviceLink` / `DeviceLinks`, `ShipExport.WireDeviceLinks` |
| Object rename (`CondOwner.Rename` / `CheckForRename`, the `Rename` GPM) | ported (containers and devices only, §14) | `Rename`, `Placement.CustomName` |
| Power-state switching (`PreferPoweredState` both ways; alarm sensing) | ported (nominal states only, §12) | `Catalog.PowerToggle` |
| Deferred lighting (`Visibility` + `LoSPass`) | ported (preview only) | `LightNetwork`, `VisibilityMesh`, `LightComposite` |
| `JsonShip` (de)serialization — export/template/save schema | ported | `ShipExport` (write), `ShipTemplate` (read) |
| Coordinate/rotation mapping (centre ↔ top-left, CCW) | ported | `ShipGrid.TemplateTile` + `ShipExport` |
| On-demand resolution of any placed (non-buildable) def | ported | `Catalog.Lookup` / `Catalog.ResolveDef` |
| Save player-ship identification (`strShip`) + layout strip | ported | `SaveImport` |
| Save write-back (frame rebuild, room-CO drop, dimensions) | ported | `SaveEdit`, `SaveEditImport` |
| Ship zones (`aZones`) as authored data | modelled (preserve/draw/edit, not validated) | `ShipZone` / `ZoneGeometry` |
| Wear/damage (`BreakIn` / `DamageAllCOs`) | ported (optional) | `WearModel` |
| Repair (`installables` repair jobs, §12) | ported (broken def → working def; the undamage jobs are the `WearOptions.Repaired` half) | `Catalog.RepairForms`, `Repair` |
| Container contents (`GasContainer` capacity, pressure, mass and value; `aCondOverrides`) | ported (the static model; no gas *flow* between containers, §24) | `ContainerFill`, `Placement.Fill` |
| Nav console loadout (`SysLootSpawner` + `ItmNavStationMods*`, §17) | modelled (the stock `Pod` set + course plot + flight dynamics, baked as literal items; the spawner is not reproduced) | `NavConsole` |
| Nav console screen layout (`GUIOrbitDraw.LoadModules`, `EditMenu.DoesModFit`, `SaveModules`, §17) | ported (rects, bounds, overlap, tray; no rect is invented or resized) | `NavConsole.Arrange` / `ConfigEntries` |
| Obtainability (brokers, chargen) | ported | `KioskExport`, `StartingShipExport` |
| Contained/slotted sub-objects on read; exterior-margin trim | not modelled (corpus-only; import drops sub-objects) | — |
| Crew LOS/proximity, docked-ship, station build-zone permission **in `CheckFit`** | excluded (in-game only — they gate the interactive builder, not a spawned ship) | never ported |

---

*Companion documents: [usage.md](usage.md) (how to use Ostraplan) and
[README.md](../README.md) (overview, install, build).*
