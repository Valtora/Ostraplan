# Ostranauts — Game Internals Reference

A reference for **how Ostranauts works internally**, reconstructed by decompiling
`Assembly-CSharp.dll` and reading the live game data. It is the source of truth
Ostraplan is built against: Ostraplan keeps its promise ("the Law") by *porting*
this logic, never by referencing the DLL at runtime (its types are
`MonoBehaviour`s that round-trip through Unity, so calling them off the game gives
silently wrong answers). Each system below is described as the game implements it,
with the relevant `Type.Method` citations; a short **Ported in Ostraplan** note
points to where that system is reimplemented.

**Verified against game `1.0.0.13`** (`GameEnv.VerifiedGameVersion`, Steam build 24918081),
except where a section carries a stamp of its own, which may be **older or newer** than this
line: a stamp moves only for a port that was actually re-read, so a section the latest sweep
did not touch keeps the version it was last read at. Rating cutoffs and other magic
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
- [14. Device signal connections (two channels)](#14-device-signal-connections-two-channels)
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
- [25. The inventory grid (`GridLayout`, `GUIInventoryItem`)](#25-the-inventory-grid-gridlayout-guiinventoryitem)
- [26. Ship damage (micrometeoroids, weapons and collisions)](#26-ship-damage-micrometeoroids-weapons-and-collisions)
- [27. Ship weapons and firing groups (`WeaponsSystem`)](#27-ship-weapons-and-firing-groups-weaponssystem)
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

#### A pocket is SLOTTED, and the slot is named on its own condition owner

*Verified against game `1.0.0.11`.*

Those pockets are not cargo sitting in a grid. Each is slotted onto its host's
paper-doll, and a save records that in **two** places:

- on the **item**, `strSlotParentID` instead of `strParentID`;
- on the item's **condition owner**, `strSlotName` — the slot it occupies
  (`CondOwner.GetJSON` writes it from `slotNow`).

Both are load-bearing. `Ship.SpawnItems` branches on `strSlotParentID` and re-slots
by calling `value.compSlots.SlotItem(condOwner.jCOS.strSlotName, condOwner)`, and
`Slots.SlotItem` returns **false immediately on a null slot name**, which drops the
item on the floor of the load with an "unprocessed sub items" warning. The other
branch is worse for a garment: an item written with `strParentID` needs the host to
have an `objContainer`, and a garment has none at all, so nothing attaches it and
the host comes up empty.

Which slot each pocket gets is decided the way `CondOwner.SetData` decides it when
the game runs the loot itself: walk the child's `mapSlotEffects` keys **in
declaration order** and take the first slot the host declares that still has room
(`SlotItem` refuses a full slot). That order matters because the four pouches in a
backpack are all one def, `PocketPouchSmall01`, whose `mapSlotEffects` names all
four slots — resolving each pouch independently puts all four in `pocket_pouchSm01`
and loses three of them. An EVA suit is the opposite shape: four different pocket
defs, one slot key each.

`GetCondOwner` also explains why the loot cannot be relied on to fill this in. It
runs only `if (bLoot && jCOSIn == null)`, and any item with a baked CO — which every
piece of authored cargo needs, since a save load skips an item that has none — takes
the `dictCOSaves` branch that recurses with `bLoot: false`.

**This cuts both ways, and the split is worth stating plainly, because it decides
which writer owns the pockets.**

| Case | Has a CO? | Loot runs? | Who writes the pockets |
| --- | --- | --- | --- |
| Contained cargo, template or save | yes (always required) | no | Ostraplan |
| Top-level item in a **template** | no | **yes** | the game |
| Top-level item in a **save** | yes (a save load skips an item without one) | no | Ostraplan |

So a garment lying on the deck of an exported mod ship is left alone, and the same
garment written into a save — by the write-back or by a grant — needs its pockets
emitted explicitly. Writing them in the template case would not even suppress the
loot: the pre-pass that clears `bLoot` keys off `aCondOverrides` on an item with a
non-empty **`strParentID`**, and a pocket is slotted, so it never joins that set. The
loot would run anyway and the two sets would race for the same four slots.

> **Ported in Ostraplan:** `Cargo.FreeSlotFor` (the assignment rule, shared by the
> importer and by `CargoEdit`'s intrinsic materialisation), `CargoItem.SlotName`, and
> the two writers: `SaveEdit` (`strSlotName` on the synthesized CO) and
> `ShipExport.ExportedCondOwnerSave.StrSlotName`. On import the save's own
> `strSlotName` wins, since it is authoritative; a template carries no COs, so the
> rule above stands in.
>
> Without it an EVA suit reached the game **with no slots at all** — the suit is not
> a container, so its four compartments, written as loose cargo, had nothing to
> attach to. A backpack hid the same fault behind its own 4×4 grid.

### A condition's readable name lives in a differently shaped file

*This subsection verified against **1.0.0.13**.*

Two data folders both look like they define conditions and answer different questions.

`data/conditions` is a normal list of objects and carries `nDisplayType`. Only the four
declaring `nDisplayType == 1` reach the mega tool tip as a figure, which is what
`CondDisplayDef` reads.

`data/conditions_simple` is where the **readable name and description** of every condition
live, 1421 of them on stock data. It is shaped unlike anything else Ostraplan reads: one
object whose `aValues` is a single flat array of strings, seven per condition, in the order
its own comment gives.

```
// [strName], [strNameFriendly], [strDesc], [nDisplaySelf], [nDisplayOther], [strColor], [bInvert]
"IsLong","Long","[us] [is] 1m or longer, making it difficult to stow in some containers.", ...
```

So it is chunked rather than deserialized (`SimpleCondDef.ParseTable`), and a trailing
partial row is dropped rather than guessed at. Descriptions carry the game's `[us] [is]`
grammar tokens, which are substituted at display time against whatever the sentence is about.

This is what lets a rule built out of conditions be printed as a sentence rather than as a
list of internal tokens: a container forbidding `IsLong` is one that will not hold "anything
1m or longer", in the game's own words.

> **Ported in Ostraplan:** `SimpleCondDef` and `Catalog.CondNames`, read by
> `ContainerRules` to print a container's item filter.
> **Re-verify on a major game version:** the seven-field row order, since the file carries no
> keys and a field inserted in the middle would shift every value without failing to parse.

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

#### The loot runs on every spawn EXCEPT one, and it is the one a writer uses

*Verified against game `1.0.0.11`.* "On every spawn" holds only where the item is being
spawned from a def. **A CO that comes out of save data never runs its skin's cond loot at
all**, and nothing anywhere says so.

`CondOwner.SetData` ends with `if (jCOSIn != null) bFreezeConds = true;`, and
`DataHandler.GetCondOwner` attaches the `COOverlay` and calls `Init` *after* `SetData`
returns. `Init` reaches the CO only through `Loot.ApplyCondLoot` →
`ParseCondEquation` / `AddCondAmount`, and **both return on their first line while
`bFreezeConds` is set**. The flag is cleared in exactly one place, `Ship.PostGameLoad`,
which runs once the whole ship is up. So the deltas are not deferred, they are dropped.

What survives the freeze is everything in `COOverlay.Init` that is not a condition: the
sprite (`Item.SetAlt`), `strNameFriendly`, `mapSlotEffects`, `mapAltItemDefs`, the destroy
swaps, and `aInteractionsReplace`. **That split is the trap**, because a skin whose loot
swaps a condition its own replacement interaction tests for arrives self-contradictory:
`ItmBookStudyEngSoftware01` gets `ACTStudySkillEngSoftware` (list swap, survives) while
keeping `IsStudyMaterialEngElectronic` (cond swap, frozen out), and
`ACTStudySkillEngSoftware.CTTestThem` is `TIsStudyMaterialEngSoftware`, so the book offers
no study action at all. It can still be picked up and swung, because the melee charge
profile is on the base def.

The game's own writer is the other half of the rule and confirms it: `CondOwner.GetJSON`
writes every cond out in full and collapses to `DEFAULT` **only** when the whole written
set matches the def's `aStartingConds` (`num > 1 && num == list2.Count`). A real save's MSS
floor CO carries `IsMSS`, `IsWhite` and `StatBasePrice=1.0x19` literally.

Two consequences for anything that writes a CO:

- **`aConds: ["DEFAULT"]` is wrong for a skin.** It resolves through
  `GetCondOwnerDef(strCODef)`, which is the *base* condowner by that point, so the part
  loads with the shared base's flat stats. Write the folded conds instead.
- **`aCondRules` must be present.** `SetData` takes the rules from `jCOSIn.aCondRules`
  whenever there is save data, so a CO that omits the field loads with **no** cond rules.
  60 vanilla condowners declare them (every canister, RTA tank, reactor core, fire
  extinguisher). `["DEFAULT"]` expands to the def's own set, and to nothing for a def that
  declares none, so it is always safe to write.

> **Ported in Ostraplan:** `PartDef.SkinCondLoot` (the fold happened) and
> `PartDef.SavedConds` (what a save writer must record), used by `SaveEdit.SynthesizeCo`
> — which `SaveGrant` shares — and by `ExportedCondOwnerSave.For` for a template's
> contained cargo. A **template's top-level** item is the one case that needs none of
> this: it carries no CO, so the game spawns it with `bLoot: true` and nothing is frozen.
> Reported by a player against 1.1.0: an MSS wall written back into a save read 21 credits
> with generic tags, and a textbook could no longer be read. Tests:
> `CondLootOverlayTests`.

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

### Docking legality is purely geometric (`Ship.GetAvailableDockingPorts`)

There is **no port compatibility table and no port "type"**. `GetAvailableDockingPorts(incomingShip,
earlyOut = true)` walks each open port on both ships and, per pair, calls
`GridUtils.GetIncomingDockRotation(usRot, incomingRot, out dockOffset)` for the turn that leaves the
incoming port facing ours plus a one-tile step along that face, then `GridUtils.CanOverlay(...)`,
which lays the incoming ship's whole grid over ours and returns true only when nothing collides.
`earlyOut: false` returns the full cross product, which is literally a compatibility table.

**`IsTypeB` takes no part in this.** It decides only which port bounds construction (above). A
Secondary fails more often than a Primary because of where it gets put, not because of what it is.

The collision rule (`GridUtils.AllowedToOverlap`) is three lines:

- ours `"Blank"` and theirs `"Blank"` or `IsDockSys` → allowed
- ours `IsDockSys` and theirs `"Blank"` → allowed
- anything else → collision

**`"Blank"` is the game's empty-cell sentinel, not an absent item, and that is the whole rule.**
`GridUtils.CreateDockingPortGrid` stamps a `Blank` into all eight neighbours
(`SilhouetteUtility.AllDirectionVectors`) of every item, so a `Blank` collides with any real cell.
Two hulls must therefore stay a full tile apart everywhere; the two exceptions above exist for the
seam alone, where each collar lands on the other's halo. **The collars do not interpenetrate** — the
`dockOffset` puts them one tile apart, and port-on-port is a collision.

Four more things about that grid, each of which changes answers:

- **It is `CreateDockingPortGrid`, not `CreateShallowItemGrid`.** `CreateFullGrid` calls the former.
  There is **no `IsInstalled` filter**, so loose deck cargo occupies cells and lays its own halo; a
  crate near an airlock refuses a mate. Only parented items (`strParentID`), unresolvable defs and
  `IsSystem` defs are skipped.
- **Only `_bulkyItems` spread across their footprint** (`IsPortal`, `IsNavStation`,
  `IsHeavyLiftRotor`, `IsRCSCluster`, `IsShipWeapon`), over their non-`"Blank"` socket adds. Anything
  else is **one cell** whatever its size, so a 1x4 antenna is a single cell plus its halo. Every
  docksys is `IsPortal`, which is what gives a port the `OriginX/OriginY` mating anchor
  `GatherDockingPortData` reads; a non-bulky item leaves it at (0,0).
- **An item whose cell is already taken is dropped**, and a neighbour's halo counts as taken. Three
  walls in a row register as two with a `Blank` between them, so the grid is **order-dependent** on
  `aItems`. It costs nothing in practice, since a `Blank` refuses an incoming hull just as a wall does.
- **The frame is part of the answer.** The grid is `(nCols+1) × (nRows+1)`, y-up, origin from
  `CalculateGridOffset`. `Grid<T>`'s setter grows `Width`/`Height` past the right and bottom edges but
  not past the left and top, so a cell at `x = −1` is stored and can never be read back by
  `CanOverlayBOnA`'s bounds test. 218 of the 220 stock templates sit flush against that edge, so the
  game really does let another hull come a tile closer on one side. `CanOverlayBOnA` also **skips**
  an incoming cell landing outside our bounds rather than refusing it, which is what lets a ship hang
  off the edge of a station.

Measured over the install at 1.0.0.13: 221 ships, 162 with a Primary, 59 with no port at all, 55
Secondaries. 26,064 of 26,082 ordered Primary-to-Primary pairs mate; the 18 that do not are one
symmetric cluster of six ships whose airlocks have a wall standing level with them. Secondaries
genuinely discriminate — `Station_EJDR`'s two take 135 and 119 of the 162.

> **Ported in Ostraplan:** `DockShip` (the grid), `DockMating` (the overlay), `DockSurvey` (the
> stock-primary sweep) and `DockPose` (the pose as something drawable), behind
> **Design ▸ Docking Compatibility** in its two modes
> ([issue #47](https://github.com/Valtora/Ostraplan/issues/47)). A design is the **incoming** ship,
> since that is the way round a player meets it. `DockShip.FromTemplate` keeps a template's own frame
> because of the left-edge quirk above; `FromDocument` uses the bbox±1 frame `ShipExport` writes and
> the item order `ShipExport` emits, so the prediction matches the file Ostraplan
> produces rather than some other framing of the same ship.
>
> `DockPose` turns a pose into the other ship's parts in your design's own tile frame, which is what
> the canvas ghosts. It **fits** that transform by evaluating the overlay's own round trip at three
> tiles rather than composing the two frame mappings by hand: the result is the same rigid transform
> (the two y flips cancel, so there is no reflection), and it cannot disagree with the overlay
> because it is the overlay. A grid cell is not a part — one cell per item whatever its size, plus
> Blank halo cells that are not items at all — so `DockShip.Parts` carries the drawable form beside
> the collision one.

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
> centroid), **as a fallback only**: a design that authors a spawner for either role
> takes that role instead, per role, so a designer can put an arrival where they mean
> it. The save-edit path preserves the original `aShallowPSpecs` verbatim.

### The spawner's type is the array it lives in

A `SysLootSpawner`'s `GUILootSpawn` panel carries `strType`, which takes exactly three
values, and it is **not** independent of which array the spawner sits in. Measured over
every spawner in the shipped ship files (core plus archived content):

| `strType` | `aItems` | `aShallowPSpecs` | Names an entry in |
|---|---:|---:|---|
| `Loot` | 2,954 | 0 | `data/loot`, `strType "item"` |
| `Pspec` | 0 | 600 | `data/personspecs` |
| `Pspec Loot` | 0 | 77 | `data/loot`, `strType "pspec"` |

Neither array ever holds the other kind. So the type decides the destination, which is
what lets one editor author both: Ostraplan keeps every spawner as a deck item in the
document and routes it on the way out.

The rest of the panel is `strLoot` (the target), `strRange` (scatter in tiles, 0 on
2,849 of them), `strCount`, and three condition gates — `strNew`, `strDamaged`,
`strDerelict` — deciding whether the spawner fires when the game creates the ship new,
damaged or derelict. The def's `LootSpawn` template declares only `strType`, `strLoot`
and `strRange`, defaulting to `Loot` / `Blank` / `0`; the rest are written by the panel.
`Blank` is a real loot entry that yields nothing, so an unconfigured spawner is inert
rather than broken.

> **Ported in Ostraplan:** `SpawnerSettings` (the panel), `SpawnerCatalog` (what each
> type may name), `LooseObject.Spawner`. Read on import from **both** arrays, written
> back to whichever the type selects. The three gates are always written out, because
> the template declares none of them and a spawner inheriting an unknown default is one
> whose behaviour cannot be predicted.
>
> A loot spawner is the one `IsSystem` object Ostraplan keeps on import. Fire and
> explosions are still dropped: those are runtime state, while a spawner is what a
> design says the ship should arrive carrying. Not on the **save-edit** path, though,
> where `SaveEdit` rebuilds `aItems` from the surviving originals verbatim and so
> preserves them untouched; importing them there would write a second copy beside each.

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

### The build cursor draws three map points, and the `use` one is gated

The same block of `CanvasManager` that places the connector nubs above also puts a sprite
on two more of the selected part's map points:

| Point | Sprite | Shown when |
|---|---|---|
| each `aInputPts` name | `GetPowerInputGridSprite(k)` | the CO has a `jsonPI` with input points and lacks `IsPowerInputIgnore` |
| `PowerOutput` | `GetPowerOutputGridSprite()` | the point is declared |
| `use` | `GetUseGridSprite()` (`prefabUsePointTile`, a pair of blue footprints) | the point is declared **and its raw value is not (0, 0)** |
| `ReactorPlug` | `GetReactorGridSprite()` | the point is declared |

The `(0, 0)` gate on `use` is the interesting one and is worth keeping: the point is
pixels around the item's own centre, so a zero one is the default a condowner gets and
marks the item itself rather than a side of it. 103 of the 355 buildable parts on stock
1.0.0.13 declare an offset one, and it is what distinguishes an arcade cabinet's front
from its back when the sprite does not.

This is a different question from "can a crew member reach it", which is
`Pathfinder.GetClosestWalkableDestination` over the built deck (§ *Reaching a device*) and
needs the whole ship to answer. The `use` point needs only the def and its rotation.

> **Ported in Ostraplan:** `UsePoint` (the gate, and the rotation through
> `GridMath.MapPoint`), drawn by `ShipCanvas.DrawUsePoint` on the armed ghost, on a
> selected part and, for every part at once, under the Access overlay. The reachability
> answer stays with `WalkNetwork`.

---

## 14. Device signal connections (two channels)

The game has **two entirely separate** ways one device drives another, stored differently,
validated differently, and consumed by different code. Getting this wrong is what made
Ostraplan's exported pumps, scrubbers, heaters and coolers do nothing, so the distinction
is the whole of this section.

*Verified against game `1.0.0.13`.*

| | **Breaker channel** | **Sensor channel** |
| --- | --- | --- |
| What it does | switches a device on and off remotely | a device *follows* a sensor and runs while it is tripped |
| Created by | `GUIBreaker.SetInput` → `Electrical.SetUpConnection` | `GUIAirPump.SetInput`, which writes a key and nothing else |
| Stored in | the `Electrical` GPM's `inputConnections` / `outputConnections` | `strInput01` on the **driven device's own** panel |
| Read by | `Electrical.ResolveSignalQueue`, then `Powered.Run` | `GasPump.UpdateRemote` / `Heater.UpdateRemote` |
| Sources | `ItmElectricalBox01` only, in all of `data/ships` | any alarm, or the thermostat |
| Cardinality | unlimited (`HasUnlimitedPorts`) | one sensor **per device**; a sensor drives any number |
| Stock usage | 274 wired items | **1,780** links |

### 14a. The breaker channel (the `Electrical` GPM)

An **`Electrical`** GPM component (`strGPMKey = "Electrical"`) is attached to every
condowner whose `aStartingConds` carry **`IsSignalable`** — alarms, pumps, sensors,
lights, doors, RCS, antennae.

- **Directional and ID-based, with no geometry.** `Electrical` holds `outputConnections`
  and `inputConnections`, each a `Dictionary<string, ElectricalConnection>` **keyed by the
  connected item's `strID`**. `Electrical.SetUpConnection(co)` adds `co.strID` to *this*
  device's **`outputConnections`**, so **A→B means A's `outputConnections` lists B and B's
  `inputConnections` lists A.** There is no distance, adjacency or conduit requirement in
  the persisted model.
- **Only a breaker box creates one.** `GUIBreaker.SetInput` is the sole caller of
  `SetUpConnection`. Its panel's `strValidCOTrigger01` is `TIsSignalOpen` =
  `IsSignalable` ∧ `IsInstalled`, which is what the box may drive. Across all of
  `data/ships` the only def ever appearing as the source of an `outputConnections` entry
  is `ItmElectricalBox01` (and its Off/Damaged forms); everything else is only ever a sink.
  The box's own panel labels these "inputs" while storing them as outputs, which is a UI
  quirk, not a second direction.
- **Runtime semantics.** A wired sink gains **`IsConnected`** (via `TUpConnected`) and
  **`IsSignalledOn`** (via `TUpSignalled`); **`TIsConnctedSignalledOff`** = `IsConnected` ∧
  ¬`IsSignalledOn` is the `strShutDownCT` of 73 power-infos. `Electrical.ResolveSignalQueue`
  counts inputs whose `signalType` is `On`; under the default `OR` gate a device holding a
  connection that is *not* `On` resolves false, raises **`IsSignalOff`**, and
  `Powered.Run` shuts it down.
- **A source with no inputs of its own never signals anything.** `ResolveSignalQueue`
  propagates only when its gate result *changes*, and a device with `inputConnections.Count
  == 0` resolves true at load and stays there. So an alarm wired to a pump on **this**
  channel does nothing at all — and worse, leaves the pump held off. That is why the
  editor restricts sourcing to breaker boxes.
- **Persist shape.** The wiring rides on the item's **`aGPMSettings`** entry
  `{ "strName": "Electrical", "dictGUIPropMap": [ …flat key/value… ] }`. The canonical key
  set is exactly `status`, `inputConnections`, `outputConnections`, `signalQueue`,
  `sendQueue`, `override`, `delay`, `gate`. A connection value is a comma-joined list of
  `<targetStrID>#<signalType>#<switchStatus>#<nickName>`.
- **`SignalType` is per side, and this bites.**
  `Ostranauts.Electrical.SignalType { None=0, Off=1, On=2, Toggle=3, Cycle=4, Connect=5,
  Disconnect=6 }`. Stock ships write **`0` on every one of their 203 output entries** and
  **`1` or `2` on every input entry** (175 On, 79 Off). Writing `0` on the input side, as
  Ostraplan did until 1.6.0, leaves the driven device permanently shut down by the rule
  above. `gate` is a `GateMode { OR=0, AND=1, NOR=2, NAND=3 }`; `delay` is `0.0` and
  `override` `true` on essentially every stock item.
- **`inputIDs`, `outputIDs` and `positives` are not real.** `Electrical` neither reads nor
  writes them. They survive in three legacy stock ships (`_Chromastronauts`, `_meatTest`,
  `_box`) and nowhere else.

### 14b. The sensor channel (`Panel A` `strInput01`)

This is the channel nearly every ship in the game actually uses, and it never touches
`Electrical`.

- **A device names the sensor it follows on its own control panel.**
  `GUIAirPump.SetInput(co)` writes `dictPropMap["strInput01"] = co.strID` and sets
  `bUpdateRemote`; it creates no `Electrical` connection. `GasPump.UpdateRemote` (for air
  pumps and both atmo scrubbers) and `Heater.UpdateRemote` (for heaters and coolers) read
  that key back into `strRemoteID`.
- **What it decides.** `GasPump.Pump` / `Heater.Heat` resolve, in order: `IsOverrideOn` →
  run; `IsOverrideOff` → stop; no sensor → test **itself**; otherwise → test the **sensor**.
  The test is the gas-respire's `strSignalCTMain` (`TIsReadyPumpAir` for `AirPump`,
  `AirPump02`, `AtmoScrubber02`) or the panel's `strCondMonitor01` (`DcGasTemp01` for a
  heater, `DcGasTemp03` for a cooler).
- **So an unwired device never runs.** `IsReadyPumpAir` is carried only by a **tripped**
  alarm (`ItmAlarm*OnR`, plus `ItmAlarmCO2OnY`) and by `OutfitEVA01` — never by a pump. The
  temperature conds are carried only by `ItmAlarmTempOnB`/`OnR`. A device testing itself can
  therefore never pass, and only a hand-set bus knob will start it.
  **The one exception is the CO2 scrubber**: `AtmoScrubber01`'s gas-respire names no
  `strSignalCTMain`, `DataHandler.GetCondTrigger(null)` returns the `Blank` trigger, and
  `CondTrigger.Triggered` returns true immediately for a blank one, so it runs regardless.
- **Validity is per device, from its own panel's `strValidCOTrigger01`.**

  | Panel (`data/guipropmaps`) | Devices | Valid sensor | Monitored cond |
  | --- | --- | --- | --- |
  | `AirPump` | air pumps | `TIsAlarm2` (any alarm) | `IsReadyPumpAir` |
  | `AtmoScrubber` | CO2 scrubber | `TIsAlarm2` | `IsReadyHeat` |
  | `AtmoScrubber02` | contaminant scrubber | `TIsAlarm2` | `IsReadyPumpAir` |
  | `Cooler` | coolers | `TIsAlarmTemp` (thermostat) | `DcGasTemp03` |
  | `Heater` | heaters | `TIsAlarmTemp` | `DcGasTemp01` |

  `TIsAlarm2` requires `IsAlarm2`, carried by all seven alarm families in every state;
  `TIsAlarmTemp` requires `IsAlarmTemp`, carried by the thermostat alone.
- **The limit is one sensor per device, not one device per sensor.** The key lives on the driven
  device, so nothing stops several naming the same sensor, and `CrewSim.ShowInputSelector` highlights
  every condowner satisfying the trigger with no exclusion for one already in use. The stock ships
  bear it out: of their 941 wired sensors, **307 drive more than one device** — commonly one
  thermostat running a deck's heaters and coolers together, and up to eight devices off a single
  sensor.
- **"No sensor" is written as the device's own `strID`.** `SetInput(null)` falls back to
  `COSelf`, so 337 stock devices point at themselves. `Heater` tests for it explicitly and
  `GasPump` reaches the same outcome by testing itself. An empty string reads as unwired
  too, since `GetCOByID("")` is null.
- **The authored keys.** Of the 2,124 stock devices carrying a `Panel A`, everything except
  five keys is a template constant materialised from the def. The five a player can set are
  `strInput01`, `nKnobBus` (`0` forced off → `IsOverrideOff`, `1` auto, `2` forced on →
  `IsOverrideOn`; 1,405 / 173 / 180 of the 1,758 that carry it), `bTurbo`, `bReverse` and
  `bSlowMode`.
- **A mode key is only safe where the def declares its cond.** `GUIAirPump.LoadCOStats`
  hides a checkbox whose cond (`IsTurbo` / `IsReverse` / `IsSlowMode`) is absent, but
  `GasPump.UpdateRemote` applies `bTurbo` regardless — and the rate multiplier it then reads
  off `IsTurbo` is **zero** on a def that does not declare it, so an ungated turbo flag stops
  the pump. On stock 1.0.0.13 only `ItmAirPump02*` declares `IsReverse`/`IsSlowMode`, and
  **nothing** declares `IsTurbo`.

### 14c. Why a partial panel is enough to write

`CondOwner.SetData` materialises **every** panel a def declares out of `data/guipropmaps`
(a fresh copy per instance, `DataHandler.GetGUIPropMap`) before anything else touches the
condition owner — on the save path as much as the template path, since `SetData` takes the
def and the save record together. `Ship.CreatePart` then merges the item's own
`aGPMSettings` on top **key by key**, last duplicate winning.

So an exported item only has to carry the keys it is actually authoring. Baking a copy of
a game template into every ship would work today and go stale the first time the game or a
mod changed one.

> **Ported in Ostraplan:** `DeviceLink`/`DeviceLinks` (breaker) and
> `SensorLink`/`SensorLinks` (sensor), both validated from the defs' declared panels via
> `DevicePanels` rather than from any hardcoded def list, so mods get the same rules.
> `DeviceSettings` carries the bus knob and modes. Written by
> `ShipExport.WireDeviceLinks` / `WireSensorLinks` on the template path and
> `SaveEdit.ApplyWiring` on the save path; read back by `GpmPanels` on import, so a ship's
> existing wiring survives a round trip. Gate, threshold and delay logic is left to the
> in-game signal box: Ostraplan authors connections and the per-device switches, not logic.


### 14d. The reactor panel (`ReactorIC`) is read by the simulation, not by the UI

The pump's four authored keys are written by `GUIAirPump` at runtime and appear in no
template. The reactor's are the other way round: the `ReactorIC` prop map **declares** all
thirteen with their defaults, and it is `FusionIC.Update` — the reactor simulation, which
runs whether or not anybody has the panel open — that reads them back through
`COSelf.GetGPMInfo("Panel A", …)` on every tick.

| Key | What reads it | Positions |
|---|---|---|
| `knobBus` | `FusionIC.Update` (`nKnobStateBus`; 0 forces every module it drives off) | 0 OFF, 1 BATT, 2 CHRG |
| `knobPump` | core purge; pumps `StatICPressureA` down to 0.35 on 1 and to 0.10 on 2 | 0 OFF, 1 RGH, 2 TRB |
| `knobRatio` | the power split: 0 sends it all to the MHD, 1 sends 95% to thrust. **Anything that is not 1 is coerced to 0** | 0, 1 |
| `chkAlign` `chkCoilFwd` `chkCoilRear` `chkCryo` `chkFuelReg` `chkIgnition` `chkMHDOn` `chkPellet` | one per module `FusionIC` drives | `bool.ToString()` |
| `slidCycle` | `StatICThrustThrottle = slidCycle × ratio`, and bleeds core temperature | 0–1 |
| `slidFlow` | lerps the pellet rate between idle and the feeder's maximum | 0–1 |

The instance name is not a convention: `FusionIC` and `Ship.GetReactorGPMValue` both write
the literal `"Panel A"`, so that is where the game looks whatever a def declares. All
thirteen stock defs that declare the panel use it.

`GUIReactor` writes the same keys when a player throws a switch, and the panel's own
`SetPowerBus` default branch and `FusionIC.SetControlsOff` both reset all thirteen to the
template defaults, which is what a shut-down core looks like on disk.

Ignition is gated (`GUI_REACTOR_IGNITION`, and `FusionIC`'s own `IsReadyFusion` test) on
the core being at vacuum, the capacitors charged, and `chkAlign` / `chkPellet` /
`chkFuelReg` all thrown. A `chkIgnition` set without them does not light the core.

**A def need not declare the panel to carry one.** The shipped stations author it on
`ItmReactorIC02Ignition`, whose condowner declares no `mapGUIPropMaps` entry for it at all;
`Ship.CreatePart`'s merge puts it on the condition owner regardless. Reading the panel by
its keys rather than by the def's declaration is what makes that import.

> **Ported in Ostraplan:** `ReactorSettings` (the thirteen keys, their clamps and the
> game's own knob labels) and `DevicePanels.ReactorPanel`, which finds the panel by its
> `strGUIPrefab` rather than by a def name so a modded core is authored the same way.
> Written by `ShipExport.WireSensorLinks` on the template path and `SaveEdit.ApplyWiring`
> on the save path; read back by `GpmPanels.Reactor`, which scans for the keys so a panel
> the def never declared still imports. Measured on stock 1.0.0.13: the shipped ships carry
> 57 reactor panels, 34 of them on CHRG with the ignition switch on. The editor and the two
> writers are on the placement, not on a loose deck item, because an uninstalled core has no
> core to light. That costs nothing on stock data: exactly one shipped ship authors the panel
> on a loose form (`Station_VORB_Port`'s damaged core) and every key on it is the template
> default, which Ostraplan would drop either way.

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
> Ostraplan's own dialog. Renaming is offered on **any placed part**, the same latitude the
> game gives. It was once narrowed to containers and devices, which left the secondary
> airlock, the gas canisters, the beacon and every damaged variant unnameable while their
> imported names still displayed, so the narrowing was dropped in 0.93.1.

---

### The mega tool tip shows less than you would expect

An object's mega tool tip (`Ostranauts.UI.MegaToolTip`) is a host plus a prefab list of
data modules. For an **item** the relevant ones are:

| Module | What it shows |
|---|---|
| `ItemModule` | `strNameFriendly`, `strDesc`, the object's factions, its portrait. Carries `StartRename`, so this is where a rename happens in game. |
| `ValueModule` | `GetBasePrice()`. Exact (`~$N`) for a crew with `SkillAdmin`, else `$`–`$$$$$` tiers. Destroys itself outright on a def with no `StatBasePrice` rather than printing zero. |
| `NumberModule` | Every condition whose def declares `nDisplayType == 1`. |
| `GasModule` | The 8 gases, where present, plus `StatGasPressure`. |

> **`nDisplayType == 1` matches four conditions in the whole of stock 1.0.0.11**:
> `StatGasTemp`, `StatLiqD2O`, `StatLiqHe` and one more liquid. An ordinary crate's tool tip
> is therefore a name, a description, factions and a price, and **nothing else**. A panel
> that fills itself with every `Stat*` a def declares is not parity, it is a different
> feature — which is why Ostraplan's raw list is a separate, labelled section.

**Factions are per-instance save state.** `CondOwner.aFactions` is populated from
`JsonCondOwnerSave.aFactions` and by `AddFaction` at runtime; nothing on a def declares any.
Ordinary cargo does carry them (a drink pouch, a coffee, a chair on a station-owned ship),
and they identify the company or station the object came from. Their friendly names live in
the **save's own** `objSystem.aFactions` — about 400 in a mature playthrough, most of them
the per-person factions the game mints as it goes — and **no data file under the install
lists them**, which is why an imported design has to carry its own table.

> **Ported in Ostraplan:** `CargoInfo` (the panel's contents, in the game's module order),
> `Catalog.CondDisplay` / `CondDisplayDef` (the `nDisplayType == 1` set and its formatting),
> `CargoItem.Factions` + `ShipDocument.FactionNames` (read at import from the session
> record). Behind Alt+click in the container view. **Re-verify on a major game version:**
> the `nDisplayType` set, since a patch that marks more conditions display-type 1 silently
> grows the panel. `CargoInfoTests` pins the count as a drift alarm.

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

### Wear is a shader, not a sprite (`Sprites/AlbedoPass`)

A part's condition changes how it looks, and none of the rule is in the assembly or the
data. `strImgDamaged` is **not** a sprite the game swaps to: it is a second texture blended
in by a procedural pass, and the 640-odd defs that name no such texture still wear.

The fragment path, for the ordinary ship view (`_OverlayMode == 0`, i.e. no PDA visualiser):

```
if (_OverlayAmount >= 0.2) {              // nothing wears above 80% condition
    q  = frac(uv * (_Columns, _Rows))     // cell-local for a sheet item
    px = trunc(_Aspect.xy * 16 / _MainTex_ST.xy)
    q  = floor(q * px) / px               // snap to the material's texel grid
    p  = (q + _PositionOffset.xy) * _Complexity * _Aspect.xy

    n = fbm(p.x, p.y, _PositionOffset.z)
    n = _Sinew ? abs(n - _Cut) * _Intensity + _Trim
               :    (n - _Cut) * _Intensity + _Trim

    if (n >= 1 - _OverlayAmount) {        // this texel has worn through
        wear = _DmgPresent ? tex2D(_DmgTex, uv) * _WearCol : _WearCol
        rgb  = _Lerp ? lerp(rgb, wear, saturate(n)) : wear
    }
}
```

`fbm` is 8 octaves of trilinear value noise, frequency doubling from `0.005` and amplitude
halving from `25`, hashed by `frac(sin(i.x + 157·i.y + 113·i.z) * _Seed)`. The normaliser
sums the **halved** amplitude, so the field runs 0–2 rather than 0–1; that factor of two is
what lifts it over a default `_Cut` of 0.8 at all.

Three things about it that are easy to get wrong:

- **`_PositionOffset` is the part's world position** (`Item.RefreshShaderVariables`:
  `(tf.position.x, tf.position.y, ZScale, 0)`), which is §7's item centre. The in-world
  renderer shares one material per texture set and so **never assigns `_Seed`** — every
  part on every ship runs the shader default `453.5453186`. World position is therefore the
  only thing decorrelating two identical walls, which is what makes the pattern
  reproducible rather than random. (`Item.SetUpInventoryMaterial` *does* set `_Seed`, per
  object, from the `strID` hash — that is the inventory material, not this one.)
- **The def's tuning fields are sentinels, not values.** `Item.SetData` pushes each only
  when the def set one, and `JsonItemDef`'s constructor pre-sets `fDmgCut`/`fDmgTrim` to
  `-999` and `bLerp`/`bSinew` to **`true`**. Reading `bLerp` with the usual false fallback
  flips the ~800 core defs that omit it.
- **`Item.GetWearColor` has three rungs**: a named `strDmgColor` wins; failing that, a def
  naming a damaged texture gets plain white so its own art shows through unrecoloured; only
  a def naming neither falls to `DamageTintDefault`.

> **Ported in Ostraplan:** `WearShader` (the noise, the slice, the threshold, the tuning
> resolution and `GetWearColor`), `ItemDef.Wear`/`WearFields` for the def fields, and
> `SpriteCache.WornSprite`/`WornSheetCell` for the bake the canvas draws. Behind the
> **Simulate ▸ Damage Brush**. **Re-verify on a major game version by re-extracting the
> shader** — every constant here lives in compiled GPU code, so no data check can see it
> drift, and `--wearsmoke` is what catches it. Extraction follows §1's shader route.

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

*Verified against game `1.0.0.11`.* The shaders live in compiled GPU code, so re-checking this
section means re-extracting and disassembling them rather than re-reading a decompile, and the
1.0.0.13 sweep did not do that. Nothing in `resources.assets` is visible to the parity corpus
either, so this section is the one place where a silent drift would not surface as a test.

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
else — and it paints tile conditions like one too: every loose def's `aSocketAdds` is
`TILItemAdds` (`IsItemTile`) across its **whole footprint**, which is 1x4 for
`ItmAntenna01Loose` and bigger than 1x1 for 521 of the 888 loose items in 1.0.0.11. Its
`aSocketForbids` is `TILItemForbids` (`IsFixture` / `IsObstruction` / `IsItemTile`) over the
same cells, and its `aSocketReqs` is blank throughout — so the *interactive* drop refuses a
fixture or another item, and requires no floor.

**Those masks do not gate a spawned ship.** `Ship.SpawnItems` `AddCO`s a template's
top-level items with no fit check, and the shipped content relies on it: of the 3054 deck
items across the 221 core templates, `Station_MTRS_Nuked` alone lies 254 pieces of scrap on
unfloored wreckage, `Station_Ground` lies `RegolithBig` on a station exterior, and `Babak`
writes **fifteen separate `ItmPillAntibiotic01` objects at one position with no `aStack`** —
fifteen distinct COs the game spawns as fifteen. So "one loose item per tile" is not the
game's model, and neither is "a loose item needs a floor".

> **Ported in Ostraplan:** `LoosePlacement`, on the interactive path only — what the Items
> palette lays is held to the masks, and a design that arrives from a template, a save or an
> `.oplan` is left exactly as written. Written outside the intended frame it widens the rebuilt grid, and on the next load
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
>
> An import **reads** the panel too (`NavConsole.StoredLayout`), so a console somebody sat at
> in game arrives arranged the way they left it. Only a map that differs from the console
> def's own is kept: **all 120 consoles in the core `data/ships` files carry `NavModConfig`
> as a verbatim copy of the def's**, because that is what the item spawns with, and storing
> that would put a redundant map on every imported console and make the write-back stamp it
> over one it should have left alone. Without this read the arrange dialog showed a recomputed
> stock screen for a ship that had been arranged differently — reported against 1.7.1, with
> Diagnostics on the wrong side and a strip of screen reading as free that in game was not.
>
> **The fit test needs float slack.** The game compares anchors in **float32**, where a panel
> butted against its neighbour lands on the neighbour's edge exactly: `0.05f + 0.10f` is
> `0.15f`. In double it is `0.15000000000000002`, a hair past a neighbour starting at `0.15`,
> so a drop the game accepts read as an overlap (the same 1.7.1 report: red over visibly clear
> screen). `NavConsole` compares every edge with `1e-6` of slack — four orders of magnitude
> below the 2dp granularity every real anchor has, so it cannot mask a genuine collision.

### A module's screen is a prefab in `resources.assets`, not anything in the data

What a module *looks like* is nowhere in `StreamingAssets`. The `navmod/` PNGs are the 16×16 sprites
of the module as a loose item (what the edit menu's tray shows, through `DataHandler.LoadPNG` on
`Item.ImgOverride`). The panel itself is a Unity UI prefab: `GUIOrbitDraw.LoadModules` does
`base.transform.Find(prefab)` under the board first, then
`Resources.Load<GameObject>("GUIShip/GUIOrbitDraw/" + prefab)`, and those prefabs are serialized into
`Ostranauts_Data\resources.assets` (a Unity **6000.3.10f1** build under 1.0.0.13). Three things about
where they sit, all easy to get wrong:

- **Every module has a `NavMod*` and a `NavMod*Dmg` prefab, except two.** `NavModMap` and
  `NavModControls` have no standalone prefab at all: they exist only as children of `GUIOrbitDraw`
  (inactive until `LoadModules` activates them), which is why the `Find` comes first.
- **The PDA has its own copies.** `GUIPDANAV` embeds a `NavModMap` and a `NavModControls` laid out for
  the hand-held (full-screen containers, different children). Same names, different objects; only the
  parent tells them apart.
- **Sprites can live in another file.** A UI sprite the board shares with a scene is in
  `sharedassets0.assets`, and a `PPtr` is relative to the file it was read from, so a texture pointer
  read off such a sprite resolves against the wrong file if it is resolved against `resources.assets`.

Inside, a prefab is a tree of `RectTransform`s under a `Container` (the rect `LoadModules` sets the
console's anchors on), carrying `Image`s (a sprite or a bare tinted quad, simple or nine-sliced),
`TextMeshProUGUI` labels (text, point size, auto-size range, colour, alignment, the upper-case style bit
in `m_fontStyle`), `RawImage`s for the live screens (the map, the MFDs), and the game's own
`GUILamp` / `GUIKnob` / `GUIBtnLitRim` scripts that set state at runtime. Five modules
(Reserves, Sensors MFD, Weapons MFD, Torch Drive, Flight Dynamics) use layout groups or aspect
fitters, whose children are placed by the engine at runtime. The label faces are TextMeshPro SDF
atlases, but the source fonts (`Jura-Regular`, `Jura-Bold`, `robotocondensed`, `Roboto-Medium`,
`NotoSansSC-Regular`) ship in the same file as `Font` objects whose `m_FontData` is the TrueType file.

**Units.** The ship GUI canvases (`Canvas GUI` and its siblings) scale with the screen against a
**1280×720 reference matched on height**, with `referencePixelsPerUnit` 32, so a canvas unit is 1.5 screen
pixels at 1080p and the console board (about 1700×810 screen pixels there) is about **1133×540 units**.
TextMeshPro point sizes, anchored offsets and `sizeDelta` are all in those units. A label with auto-size on
stores the size TextMeshPro **fitted in the editor** in `m_fontSize` (`CLEAR` is 11.7 against a maximum of
36), which is the size the game shows; fitting again from the maximum grows every label to fill its rect. A
sliced sprite's border is in sprite pixels and covers `border × 32 / pixelsPerUnit` units (the UI art is 100
PPU, so a third), divided by the `Image`'s own multiplier.

Only `NavModControls` saves its container full-screen; every other prefab's container is already at
its stock rect, so the two agree except there. Seven nodes carry a rotation, flip or scale on their
`RectTransform`, which Unity applies to the whole subtree: the rotor-efficiency meter and its track
(`NavModEngineMode`) are turned −90°, the airstream arrow (`NavModFlightDynamics`) 180°, a mooring slider
handle is mirrored in y, and the two reserve meters are scaled ×2 in y. Everything else is axis-aligned.

> **Ported in Ostraplan:** `NavModArt` (Core) reads the file with AssetsTools.NET, MIT, plus its
> MonoCecil generator over the game's managed DLLs for the layout of script fields, and a copy of
> UABEA's `classdata.tpk` (`src/Ostraplan.Core/Assets`, see its NOTICE) for the engine's own types,
> which a built game strips. It walks each module's `Container` and emits a `NavModScene` of fills,
> sprites and labels in the container's unit square, laid out at the size `NavConsole.ScreenSizes`
> gives the module; `NavModArtCache` (App) draws that with WPF at whatever size the arrange board
> shows the module, in the game's own faces (written once to `%APPDATA%\Ostraplan\fonts`). What is
> not reproduced: layout groups (children drawn where the prefab saved them), `RawImage` screens
> (black), the SDF materials' glow, and every value the game writes at runtime. **Re-verify** on a
> game update that changes the Unity version string in `resources.assets`: the class database has to
> cover it, and `NavModArt.Build` names the version it could not find one for. The arrange window
> falls back to flat panels either way, so a stale database is a cosmetic regression, never a broken
> one. `NavModArtTests` reads the live file.

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

> **The entry name is not the RegID.** *Verified against game `1.0.0.11`.* The save is
> written as loose files and then zipped from disk (`DotNetZipCompressor.CompressFolder`),
> so every write goes through `DataHandler.ReplaceInvalidCharacters`, which substitutes
> **`|` → `%`** and **`*` → `§`**. An apartment with RegID `BCRS|RES_1` is therefore the
> entry `ships/BCRS%RES_1.json` (a stock save shows `ships/VORB%Aux.json` for `VORB|Aux`).
> The `revert: true` branch of that function is never called anywhere, and it does not
> need to be: `CrewSim.DoLoadGame` enumerates the folder and reads `strRegID` out of the
> JSON body, so the filename is decorative on load. It is **not** decorative on write.
> Addressing an entry as `ships/<RegID>.json` when the RegID contains a pipe misses the
> real entry and, on a create-if-absent path, leaves two records claiming one RegID.

> **A ship's condowners are not necessarily in its own record.** *Verified against game
> `1.0.0.11`.* An item's live state — wear, gas, inventory, power, door position — lives on
> its condowner, paired to the item by `strID`, but that pairing runs through **one global
> registry** and not through the record. `Ship.InitShip` copies whatever `json.aCOs` it
> finds into `DataHandler.dictCOSaves` and then nulls the field, and `Ship.SpawnItems`
> resolves every item against that dictionary, wherever the entry came from. The writer
> exploits this: the COs of the ship the player is standing on go into that ship's record,
> and **every other ship's go into the session record**. In a real save the player's own
> ship read back as 7686 items against 2 COs, with all 7686 in the character record, while
> the station they were standing on carried its own 921.
>
> So "every `aItems` entry has an `aCOs` entry beside it" holds only for the ship the player
> is aboard. Anything that reads a ship's live state, or that rebuilds a record and checks
> the pairing, has to look in the session record too. The exception is a ship that has never
> been visited (`fLastVisit == 0`, `Ship.IsTemplateShip`): it loads through the
> `bTemplateOnly` path, which builds condowners from the defs and needs no save entries at
> all.

> **Ported in Ostraplan:** `SaveImport` (player-ship identification + layout strip);
> `SessionCos` reads condowners out of the session record and cuts them back out of it, on
> the bytes rather than through a parser, and `SaveEditImport` adopts the ones an edited
> ship's own record is missing. **Re-verify per patch:** that the writer still partitions
> the registry by which ship the player is on.

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

### What the game's own writer emits (`Ship.SaveCOs` / `GetJsonItem` / `CondOwner.GetJSONSave`)

*Verified against game `1.0.0.11`.* The routine to copy is `Ship.SaveCOs`, which walks the
ship's condowners and, for each, rebuilds an **item** entry with `Ship.GetJsonItem` and a
**CO** entry with `CondOwner.GetJSONSave`. Both are built from scratch on every save, which
is why the game never has to remove anything: a field it does not write this time simply is
not there. A writer that edits a record **in place** has to take stale entries out itself.

`GetJsonItem` writes exactly ten fields, and three of them are worth stating:

- **`strName` is `co.strCODef`**, the condowner name — the *skin's* name, because
  `COOverlay.Init` assigns `component.strCODef = cOOverlay.strName`. Not the item def.
- **`fRotation` is `item.fLastRotation`**, not the transform's Z euler, whenever there is an
  `Item`.
- **`aCondOverrides` gets one entry and only one**: `StatDamage`, and only
  `if (damage > 0.001)`. Nothing else the game writes ever lands here.

`GetJSONSave` writes the CO. Everything below is either written unconditionally or is empty
for a freshly-built item, so the shape a writer must produce is small:

| Field | Game writes | Freshly-built item |
|---|---|---|
| `strID`, `strCODef`, `bAlive`, `strCondID`, `strIdleAnim` | always | required |
| `strRegIDLast`, `strFriendlyName` | always | required |
| `aConds` | every live cond, collapsing to `DEFAULT` only when the set matches the def exactly | see §3 |
| `aCondRules` | the CO's rules; an **empty array is omitted by the writer**, so an absent field means "none" and is not evidence of who wrote it | `["DEFAULT"]` |
| `inventoryX` / `inventoryY` | always, including `0` | omit == 0 |
| `aTickers` | when any | must be baked (§17) |
| `aStack`, `strSlotName` | when the CO is a stack head / is slotted | as authored |
| `fLastICOUpdate` | always, and always non-zero in a real save | the save's epoch |
| `nDestTile` | never assigned in `GetJSONSave`, so it is always `0` in a save | omit |
| `strIMGPreview` | `Item.ImgOverride`, re-derived on spawn and **never read back** by `SetData` | omit |
| `aCondReveals` | **never written at all** — read on load, written nowhere | omit |
| `fMSRedamageAmount`, `aCondZeroes`, `aQueue`, `aReplies`, `mapDGasMols`, `aLot`, `aAttackIAs` | always, but empty/zero unless the part has run | omit |
| `social`, `cgs`, `mapIAHist2`, `aPledges`, `aFactions`, `aMyShips`, `strBodyType`, `aFaceParts`, `dict*` | crew and robots only | n/a |

`aAttackIAs` is worth one line because it looks like weapon data: it is only ever populated
by `ApplyAModes`, from a save load or from an interaction granting an attack mode at
runtime. A weapon spawned from its def has none.

> **Ported in Ostraplan:** `SaveEdit.SynthesizeCo` + the item writers in `SaveEdit` /
> `SaveGrant` produce this shape. The fields marked "omit" above are omitted deliberately —
> each is either never read back, or deserialises to the same value the game wrote.

### An item's `aCondOverrides` is the SHALLOW channel, and a full load throws it away

*Verified against game `1.0.0.11`.* It reads like a per-instance override that lands on top
of everything, and on a template it is exactly that. On a **save** it is not: it is the
shallow-state mirror, and the CO's `aConds` is what the ship actually comes up holding.

`Ship.InitShip` calls `SpawnItems`, and `SpawnItems` is where both
`JsonItem.ApplyOverrideCondsToCO` calls live. That method is
`co.SetCondAmount(...)` → `AddCondAmount`, which **returns on its first line** for any CO
built from save data: `SetData` has frozen its conds, and `Ship.PostGameLoad` — the only
place `bFreezeConds` is ever cleared — does not run until several hundred lines later in
the same method. So on a full load of a visited ship, every override is dropped.

`ApplyUniqueMapConditions` is the tell. It does the same job on the same COs and
deliberately brackets its `AddCondAmount` with `bFreezeConds = false` / restore.
`ApplyOverrideCondsToCO` does not.

The game never notices because it writes **both**: in a real save every item carrying an
`aCondOverrides` entry has the same cond at the same value in its CO's `aConds` (checked
across a live save: `StatDamage` on an EVA suit, a knife, a soldering iron, a drill, a
battery — identical in both places every time). The overrides are what
`Ostranauts.Ships.DamageSystem` and `Ostranauts.Trading.DataCOWrapper` read off a ship
nobody has loaded, which is the whole point of them.

The two exceptions that still work through the override alone, because nothing is frozen:

- a **template's top-level** item, which carries no CO at all;
- the **structural marker** use — `SpawnItems` collects `strID`s of items that have any
  `aCondOverrides` and a non-empty `strParentID` *before* applying anything, so a marker
  used to keep a contained item alive is read as presence, not as a condition.

**The pairing runs both ways, and a writer has to keep both halves.** `GetJsonItem` mirrors
the CO's damage onto the item; in a real save every item carrying a `StatDamage` override
has the identical value in its CO, checked across an EVA suit, a knife, a soldering iron, a
drill and a battery. Writing one without the other splits the ship in two: the shallow
readers answer from the item, and the loaded ship answers from the CO.

> **Ported in Ostraplan:** `SaveEdit.SetStatDamage` writes the CO's cond **and** the item's
> mirror, matching `GetJsonItem`'s `> 0.001` rule and removing the entry below it — the
> removal being the half the game gets for free by rebuilding each item from scratch.
> `SaveEdit.SetFillConds` writes an authored tank/canister fill onto the **CO** and
> `SetFillOverrides` mirrors it onto the item. A fill is the one thing Ostraplan puts in
> `aCondOverrides` that the game never does, which is safe in both directions: the game has
> no authored-fill concept to write, both shallow readers pass an unknown cond through, and
> `RemoveShallowDamage` explicitly keeps everything that is not `StatDamage`. Before this
> the write-back wrote a fill only onto the item, so it priced right at a broker and then
> came up holding the def's stock 13,373 mol the moment the player flew the ship, and wear
> went only onto the CO, so a worn ship quoted its old condition. Tests:
> `ContainerFillTests`, `WearTests`.

### Writing a cond the def also declares needs the marker expanded

A corollary of the `DEFAULT` expansion in [§3](#3-conditions-and-loots--the-tile-vocabulary):
`CondOwner.SetData` replaces the marker by **appending** the def's `aStartingConds`, then
applies the whole list in order, zeroing each cond before setting it. The def therefore has
the last word on every cond it declares, and an amount written beside the marker is
overwritten by the def's own value on load.

This is why `StatDamage` can be written next to the marker and a gas amount cannot: no
ordinary def declares `StatDamage`, but a tank declares `StatGasMolO2`. The exceptions are
worth knowing — **thirteen mineral defs** (`ItmMineral01`…`ItmMineralStone01`) declare
`StatDamage` as a spawn roll (`StatDamage=0.5x1-50`), so painted wear on one of those is
contested too.

> **Ported in Ostraplan:** `SaveEdit.ExpandDefaultIfContested`, which spells the def's
> conds out **only** when the writer is setting one the def declares (`PartDef.Declares` /
> `PartDef.CondEntries`). Expanding unconditionally would put twenty entries on every part
> in the record.

### `SetUpBehaviours` is the last line of `SetData`, so a save-loaded CO never runs it

*Verified against game `1.0.0.11`.* `CondOwner.SetUpBehaviours` is where every part in the game
gets the conds no def declares. It is called on the **final line of `SetData`**, and
`bFreezeConds` is set some 450 lines earlier whenever there is save data, so every one of
its `AddCondAmount` calls is a no-op on that path. Its **list** operations still work,
which is why the `ACTBash` it appends is there on a part that has nothing else.

What it gives, and what missing it costs:

| Cond | Defs declaring it | Read by | Missing means |
|---|---|---|---|
| `IsDamageable` | **none** | `Interaction`'s melee / environmental damage branch | the part cannot be hit |
| `IsDestructable` | **none** | `TIsExplosionTarget`, `TIsDestructable` | not picked as an explosion target |
| `StatRepairProgressMax` | 17 of 359 palette defs | `DestCheck.DamageCheck` | absent reads as 0, so `progress >= max` is true at once and the job completes on its first tick |
| `StatInstallProgressMax` / `StatUninstallProgressMax` | nearly all | same | same, for the three that do not |

The two early returns are part of the rule: a part with no `StatDamageMax`, or carrying
`IsSystem`, stops after the ceilings; an `IsUndamageable` part, or one that is neither
`IsInstalled` nor `IsSolid`, stops before `IsDamageable`.

A game-built part carries all of these because the backfill ran once on an unfrozen CO,
when the part was first spawned, and `GetJSON` has written them out ever since. That is why
a live save's MSS floor CO has `IsDamageable`, `IsDestructable` and
`StatRepairProgressMax=1.0x1000` while `ItmFloorGrate01` declares none of the three, and it
is the trap: reading the def tells you nothing about what the game's own part is carrying.

**`IsDestructable` has a second route that does survive.** A def with its own
`Destructable` update command gets it from `DestCheck.SetData` during `AddCommand`, which
runs *before* the freeze. Only defs relying on the `SetUpBehaviours` fallback lose it. The
destroy behaviour is never lost either way, because the component `AddCommand` builds is
not a condition.

**The guards read the BASE conds, not the skin's.** `SetUpBehaviours` is the last line of
`SetData` and `COOverlay.Init` runs after `SetData` returns, so the backfill sees the base
condowner's conds and the skin's cond loot lands on the result. Reading the rule off the
folded conds instead gets 249 defs wrong, in both directions:

- 254 skin loots move `StatDamageMax`, which is one of the guards.
- `CNDOLConduit04` takes `StatInstallProgressMax` from the base's 150 to zero, i.e.
  **absent**, which is an instant install. Backfilling from the folded view would see it
  missing and restore 1000, making the part slower than the game intends.
- 247 damaged/patch floor skins adjust `StatInstallProgressMax` on a base that does not
  declare it. The game backfills 1000 and *then* applies the delta, so `ItmFloorAERO01Dmg`
  ends at 1050 and `ItmStorageBinFloor1x103Dmg` at 950. The folded view alone shows 50 and
  nothing at all.

> **Ported in Ostraplan:** `PartDef.BehaviourBackfill` is the rule itself, including both
> early returns; `Catalog.ResolveDef` runs it against the base conds and lets the skin loot
> settle on top, exactly as the game orders it, and stores the finished entries as
> `PartDef.BehaviourConds`. Written by `SaveEdit.SynthesizeCo` and
> `ExportedCondOwnerSave.For`. None of the conds it adds is declared by any def — that is
> what the guards test — so they never collide with the `DEFAULT` marker's expansion and can
> sit beside it. `SaveEdit.HealBehaviourConds` repairs a **kept** CO that an older Ostraplan
> wrote without them, since nothing in the game ever adds them a second time; it only ever
> adds, and defers to the CO over the def on `IsUndamageable` / `IsIndestructable` /
> `fLastICOUpdate`, which are things a part can acquire in play and a def cannot know about.
> Tests: `CondLootOverlayTests`, `SaveEditInjectSyntheticTests`.

### `fLastICOUpdate` is when the CO was last caught up, and zero means "the age of the save"

A synthesized CO that omits it gets zero, and `CondOwner.CatchUp` / `EndTurn` advance every
**timed** cond on the CO by `StarSystem.fEpoch - fLastICOUpdate`. On a mature save that is
tens of billions of seconds in one step, which expires any timed cond immediately. The
game stamps a newly-created CO with the current epoch, so that is what to write.

Narrow in practice: of 446 timed conditions, only two appear as an *item's* starting cond
— `IsReadyEvolveTimer` on the `ItmMeat01` family and `IsReadyHarvestTimer` on
`ItmPlanterMeat01`. The rest are runtime social, medical and cooldown conds applied to
crew. Worth writing all the same, since the cost is one field.

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

`CondOwner.ClaimShip` refuses to claim a ship for an `IsPlayer` CO when that ship
`IsStation()` or `IsStationHidden()`, which is why an apartment never enters `aMyShips`
and rests on `dictShipOwners` plus its residence conds instead
([below](#apartments-are-ships-sold-as-station-sub-modules)). For anything else both
halves are required.

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

> **`objSS.size` is a constant on a station, so it is no use as a clearance.** *Measured
> across a mature save, 144 ship records.* A **ship** gets a hull-derived figure: always a
> multiple of 20, tracking the grid's **x** extent only (a 15×65 hull reads the same 220 as a
> 15×24 one), running here to 2020 on a 105×57 hull, i.e. about **19.25 per tile**. Every
> **station** reads exactly **1500** — an 11×13 apartment and a 190×65 residential block
> alike, LA Construction Zone #4 at 167×167 included. So a clearance test that trusts the
> number is measuring a constant, and the spawn band's 3 km floor is not necessarily outside
> a station that declares 1.5 km and is three times that. This is the mechanism behind a
> granted ship spawning intersecting the station it was granted at (reported against 1.7.1);
> the game's own broker path has the same blind spot. Note the units are not metres anywhere
> else in the game — the flight model puts a tile at 0.32 m — but they are the units the spawn
> geometry is reckoned in.
>
> **Ported in Ostraplan:** `GrantAnchor.RadiusMetres` takes the larger of the reported size
> and `SaveGrant.HullRadius` over the anchor's own `nCols`/`nRows` (the longest dimension,
> since the game's own figure ignores the other one), and `DrawSpawnPoint` clears **both**
> hulls rather than counting the anchor's twice. When the anchor is wider than the whole 3–5
> km band, the fallback stands off past it by the 3 km floor instead of returning the outer
> radius, which was not a clearance at all: 5 km is inside a 5 km hull.

> **Ported in Ostraplan:** `SaveGrant` (build + write), surfaced as the Export dialog's
> "Into a save game" tab. Writes to a copy of the save; the original is never opened for
> writing. **Re-verify per patch:** the two spawn radii, the ferry range, and whether
> `aPathRecent` is still the only json-gated field on `ShipSitu`.

> **Ported in Ostraplan:** `KioskExport` (`AppendShipToPool`, `PinShipToPool`,
> `StripShipsFromPool`), `StartingShipExport`. Where another ship mod overrides the same pool,
> whole-object load semantics would drop one side; the resolution is Ostrasort's per-item-union
> `--patch`.
>
> **One mod writes one object per pool, however many ships it holds.** Whole-object load
> semantics apply within a single mod's own files too, so a mod carrying several ships cannot
> emit a `RandomShipBrokerOKLG` per ship: the game would keep whichever it read last and the
> rest would never reach the kiosk. `BundleExport` therefore clones each pool once and appends
> every ship into that one object. The `CGEncShipbreakerShipEvents` pool is shared the same way
> by every starting ship in the mod, which is also why a *guaranteed* start is a property of the
> mod rather than of a ship: pinning the pool is an operation on the pool, and it can only be
> performed once.
>
> **A pool clone already contains the exporting mod's own last write.** The clone source is the
> *effective* data, and a registered mod is part of that, so an export re-reads whatever it wrote
> the time before. `AppendShipToPool` then finds its own entry present and leaves it, which is
> correct for another mod's entry and wrong for its own: a ship since renamed or dropped stays in
> the pool naming a template the mod no longer defines. An export therefore strips the names it
> owns before appending the ones it holds now. What it owns is the ship it is writing plus the
> `strName`s in the `data/ships` file it is about to overwrite, **minus** a replacement's target,
> which is a core ship's name that core's own pools list.

### Apartments are ships, sold as station sub-modules

*Verified against game `1.0.0.11`.* An earlier revision of this section, written against
the 0.16-era decompile while closing
[issue #12](https://github.com/Valtora/Ostraplan/issues/12), called these "hidden
stations" and named the fixture recipes as the blocker. Both claims were wrong or have
since expired; what follows replaces them.

An apartment is an **ordinary ship record** in every structural respect: the same
`JsonShip` schema, the same `aItems` / `aRooms` / `aDockingPorts` / `nRows` / `nCols`,
loaded and saved down the same paths. Nothing about the layout is special-cased.

**It is a station, not a hidden station.** `GUIShipBroker.OnPurchaseConfirm` calls
`SpawnShip(…, isStation: true)`, then sets `HideFromSystem`, `LockToBO(<station's BO>)`
and `objSS.bIsBO = true`. But the two predicates split on ports, not on `bIsBO`:
`IsStation()` is `HasDockingPorts && bIsBO`, `IsStationHidden()` is
`!HasDockingPorts && bIsBO`. Every stock residence carries one `ItmDockSys02Closed`,
which has `IsDockSys` + `IsInstalled` and so registers through `TIsDockSysInstalled`, so
`IsStation()` is **true** and `IsStationHidden()` is **false**. What actually hides an
apartment is `bShipHidden` plus `_subStation`, and neither comes from the broker:

```csharp
// Ship.InitShip
if (strRegID.Split('|').Length > 1) { HideFromSystem = true; _subStation = true; }
```

**The pipe in the RegID is the whole mechanism.** RegIDs are `<STATION>|RES_<n>`, `n`
being the first free index. Besides `InitShip` above, three systems key off it:
`DataHandler.GetTransitConnections` truncates the RegID at and including the pipe (so
`BCRS|RES_1` resolves the transit node named `BCRS|`); `JsonTransitConnection.TargetsWildCard`
is `strTargetRegID.Contains("|")`, which fans one connection out to every loaded ship whose
RegID contains the target; and a 0.15.0.x save migration rewrote `BCRS_RES|RES…` to
`BCRS|RES…`, which is why the prefix is the **top-level** station and not the residential
sub-module. A RegID without a pipe means no transit route in or out.

**`<STATION>` is not the ship the kiosk stands on.** `GUIShipBroker.SetupApartments` uses
`COSelf.ship.strRegID` only when that ship `IsStation()`, and otherwise takes
`GetNearestStation(…, excludeOutposts: true)`. Both paths require `IsStation()`, which is
`HasDockingPorts && bIsBO` — and `HasDockingPorts` means an `aDockingPorts` entry not
prefixed `"MP|"`, a mooring point being no dock. Four of the eight placed Real Estate kiosks
sit on a portless residential module (`BCER_ROOF`, `BCRS_RES`, `MSUZ_RB`, and `MVOL`'s on
the station proper), so the nearest-station fallback is the normal path, not the exceptional
one. That fallback is what the 0.15.0.x migration was cleaning up after.

The distinction is easy to get wrong because a residential module looks like a station from
every angle except the one that counts: it carries `bIsBO`, it is a separate ship record, it
has a transit node of its own and a public name that says "Station". `bIsBO` alone is
`IsStationHidden`, not `IsStation`. In a save the discriminator is exact and cheap: across
all sixty body-orbit ships in a stock game, every one with a `<RegID>|` node has docking
ports and every residential module has none.

**How the connection actually resolves, and what a failure looks like.**
`JsonTransit.GetConnectionsForKiosk` expands a wildcard by scanning `CrewSim.system.dictShips`
for keys **containing** the target, emitting one row per match labelled
`"<label> | <RegID>"`. If nothing matches it emits a single row keeping the bare label and
**overriding `ctUserOptional` to `TIsDead`**, so the entry is present and permanently
disabled. A "Private Residence" row with no ` | ` suffix is therefore diagnostic: it means no
loaded ship's RegID contains the wildcard, not that the user failed a gate. `OKLG_RES|RES_1`
does not contain `OKLG|`, which is exactly how a residence minted off the wrong prefix
presents.

Note also that this expansion consults **no ownership registry at all**. `dictShipOwners`
does not gate the kiosk; `ctUserOptional` (`TIsHomeowner<STATION>`) is the only user gate,
and it is copied onto each expanded row.

**A residence is reached from the residential module, not from the station.** At K-Leg the
`OKLG` node offers no Private Residence connection whatsoever: the player transits
`OKLG` → `OKLG_RES` ("Azikiwe Estates Transfer Station"), and the residence row lives on the
`OKLG_RES` and `OKLG|` nodes. So the module a residence's registration must **not** name is
the same module the player reaches it from.

**Ownership is one registry, not two.** `CondOwner.ClaimShip` early-returns for an
`IsPlayer` CO when the ship `IsStation()` or `IsStationHidden()`, and the broker calls it
straight after `RegisterShipOwner`, so the claim is refused. An owned apartment therefore
lives in `objSystem.dictShipOwners` **only** and never reaches `aMyShips`. Anything that
enumerates the player's property through `aMyShips` will not see it. Purchase also grants
`IsHomeowner<STATION>` on the buyer (`GUIShipBroker.UpdateResidenceConds` applying the
trader's `strLootResidence`), which is what the transit connection's `ctUserOptional`
gate reads. Selling reverses the cond and calls `Destroy`, removing the record entirely.

**The stock catalogue.** Eleven sellable templates (`ResAero01/02`, `ResBCER01`, `ResBCRS01/02`,
`ResEJDR01`, `ResMLAB01`, `ResMSUZ01/02`, `ResOKLG01`, `ResRyokka01`), several of them
re-skins of the same floor plan, from 19×23 / 346 items up to 55×65 / 3,595. Price is
`sum(aRooms[].roomValue) × DiscountSell × 10` read off the **template's baked** room
values, not a recompute: 3.0M to 6.1M credits before the kiosk modifier. Buying is gated
on `TIs<SHIP>StrataLegal`, and the pools additionally on `Plot_OKLGHousing_01Done` and
`-TutorialNoHousingYet`.

**A residence reaches a broker differently from a ship.** A ship is named directly in a
`strType: "ship"` pool's weighted `aCOs` string (§19, `KioskExport`). A residence is
listed in `Itm<STATION>ResBrokerInv.aLoots`, and each name there resolves through a
**self-reference loot** in `loot/loot_self_reference.json` carrying
**`strType: "station"`** — `Trader.GetShipLootByType("station")` filters on exactly that
tag, and without the self-reference entry `GetLoot` returns a blank and the listing is
empty. Eleven such entries exist, one per template. So making a designed residence
purchasable needs *two* loot objects, not one.

Eleven broker pools exist; **eight** have a kiosk placed on a ship template (BCER, BCRS,
MSUZ, MVOL, OKLG, SVIR, VENC, VNCA). JFTS, MLAB and MTRS have pools and kiosk cooverlays
but no placement, so they are dormant. Eight stations have a `<STATION>|` transit node,
but the sets do not match: **MVOL sells residences and has no `MVOL|` node**, so an
apartment bought there appears to have no transit route; `VORB|` exists with no res
broker (it serves the stock `VORB|Aux` sub-station, a non-player instance of the same
wildcard mechanism).

**The fixture-recipe blocker no longer applies to Ostraplan.** `ItmKioskTransit02` /
`03b`, `ItmDockSys02Closed` and `ItmSink01Station` still have no install, uninstall or
dismantle job, so they have no bill of materials and no socket rule, and that is still
the right answer to *"make this station fitting buildable"* (see
[SCOPE.md](SCOPE.md#often-the-honest-answer-is-thats-a-mod)). But the **SPECIAL palette
tab**, added in v0.66.0 five days after issue #12 closed, builds `Catalog.SpecialItems`
from every `strType: "Item"` condowner carrying `IsInstalled` that has no build job,
which is precisely this class. Across all eleven templates the remaining recipe-less defs
are runtime state variants the powered-state and repair maps already normalise
(`ItmDoor01ClosedOn`, `ItmAirPump03OnG`, `ItmAlarmO2OnG`, `ItmCooler01On`,
`ItmVent01Open`), `Compartment`, `SysLootSpawner`, and a short fixture tail
(`ItmReactorIC02IgnitionMini`, `ItmWallRock011x1`, `ItmFloorGrate03`). All of it is
already placeable.

> **Ported in Ostraplan:** `ResidenceGrant` (station discovery, `<STATION>|RES_<n>` minting, the broker price,
> the body-orbit situation, the homeowner cond), plus the residence branches in `SaveGrant.BuildShip` /
> `WriteGrant` and `SaveZip` for the §17 filename encoding. A design carries `DocumentKind.Residence`, which
> gates the four vessel-only analyses and routes the delivery. `SaveImport` now unions `aMyShips` with
> `dictShipOwners` so an owned apartment is findable, and `SaveEdit.ValidateSubStation` aborts a write-back that
> would lose the registration or the station lock. `ResidenceGrant.IsFullStation` is the `IsStation()` +
> `excludeOutposts` pair above; a ship the data already routes to is kept regardless, so a mod may hang a route
> off something portless. **Re-verify per patch:** the pipe convention in `Ship.InitShip`, the `ClaimShip`
> station refusal, the docking-port half of `IsStation`, and whether the transit node set still matches the
> stations that place a Real Estate kiosk.

> **Not ported:** making a designed residence purchasable through a Real Estate broker. That needs a
> `strType: "station"` self-reference loot plus an `Itm<STATION>ResBrokerInv.aLoots` append, which is a second
> export shape and buys nothing the save routes do not already deliver.

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
> run, `SystemRows` for rows 2–15 alone), with the cutoffs in `ShipDiagnosticsThresholds` and
> the readout on its own **Diagnostics** toolbar action. Rows 0 and 1 are the only ones needing
> a rating and both are neutral, so `SystemRows` answers "which of this ship's systems work"
> without certifying every room first — which is what lets `DamageFallout` ask it of a hull
> twice, once whole and once wrecked, and report the rows that flipped. Backup power goes through
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

Both terms take the **same** steering input, `(fX cos θ − fY sin θ, fX sin θ + fY cos θ)`,
so rotor thrust is not confined to the ship's own up and a design really can point all of
it against gravity whatever its attitude. What separates the two is that the RCS term is
gated on `RemoveGasMass` returning something: in MIXED an empty feed zeroes the RCS half
and leaves the rotors alone, where in RCS mode it stops the burn outright. **So RCS holds
a ship up, but only while the reaction mass lasts**, which is why Ostraplan reports the
two separately rather than in one sum.

> **Ported in Ostraplan:** `Atmosphere` (bodies, band interpolation, density, gravity —
> reading `star_systems` through `DataIndex`, so a mod that adds a body or retunes Venus
> is picked up like any other data) and `FlightDynamics` (`Measure` for the design's
> profile, `FlightPoint` for one operating point), with the readout on **Design ▸ Flight
> Dynamics**. Airspeed, angle of attack and attitude are flight state rather than design
> facts, so they are user inputs; the environment defaults to the body's own figures and
> stays editable. `FlightPoint.HoverRatio` is lift plus rotors over local gravity, and RCS
> is reported beside it as an acceleration and a countdown (`RcsHoverSeconds`, the RCS
> delta-v over the shortfall) rather than folded into that ratio.
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

## 25. The inventory grid (`GridLayout`, `GUIInventoryItem`)

*Verified against **1.0.0.11**.*

A container's contents are laid out on a `GridLayout`, a `nContainerWidth` ×
`nContainerHeight` array of condowner ids. Every cell an item covers holds that item's id,
so occupancy and identity are the same table.

### Where an item lands

`GUIInventoryItem.AddToWindow`, which runs once per item as a window opens:

1. Start from the item's persisted cell, `CondOwner.pairInventoryXY` (the save's
   `inventoryX` / `inventoryY`).
2. `GridLayout.FindNearestUnoccupiedTile` — the free rect nearest that cell by **squared
   distance**, scanned row-major so ties resolve top-left-most.
3. Failing that, `FindFirstUnoccupiedTile` — the first free rect, row-major.
4. Failing that, `Debug.Log("Could not fit inventory item on grid - panic!")` and the item
   is **not drawn**. It stays in the container (containment is `strParentID`, not the
   window), it just has nowhere to appear.

Most saved items sit at (0,0) because a container never opened in-game never materialised a
layout, so step 2 is what produces the arrangement a player actually sees.

### The footprint, and how rotation rides on it

`GetWidthHeightForCO` resolves an item's tile footprint, in this precedence:

```
w, h = item.nWidthInTiles, item.nHeightInTiles      the live Item component's geometry
if def.inventoryWidth  != 0:  w = def.inventoryWidth        overrides, NOT rotation-aware
if def.inventoryHeight != 0:  h = def.inventoryHeight
```

**Rotation survives a save round trip through the first line, not the second.** Loading sets
`Item.fLastRotation`, whose setter spins `Item.RotateCW` until the angle matches, and
`RotateCW` ends with `Swap(ref nWidthInTiles, ref nHeightInTiles)`. So the geometry read
back on the next load is already turned, and a rotated item reserves the same cells it
reserved before the save.

> The `inventoryWidth` / `inventoryHeight` override is the hole in that: it is applied after
> the swap and is never itself swapped, so an item whose def declares one would reload
> holding its un-rotated footprint while still drawing turned. **No core condowner declares
> either field** (0 of 1,120 on a stock 1.0.0.9 install), so nothing in the vanilla game
> reaches it, but a mod could.

`RotateCW` returns immediately for `bHasSpriteSheet`, and the `fLastRotation` setter is
guarded the same way, so **sheet items (walls, floors) never rotate** — in a container just
as on the ship grid (§5).

### Rotation happens in hand, and the drop is what validates it

There is **no rotate-in-place operation.** `CommandRotateItem` does nothing unless
`GUIInventory.instance.Selected` is set, meaning an item is picked up and following the
cursor. `RotateCWSelected` then turns the transform −90°, updates `fRotLast`, and swaps
`itemWidthOnGrid` / `itemHeightOnGrid` — **with no fit check at all.** Legality is settled
later, by `PlaceAtScreenPosition` → `IsGoodPlacement`, when the item is dropped.

The drop cell is **centred on the cursor**, not anchored by the item's top-left.
`GUIInventoryWindow.PairXYFromLocalPoint`:

```
cell = (int)(localPoint - (pixelExtent - oneCell) / 2) / oneCell
```

where `pixelExtent` is the item's drawn size with width and height swapped when the rotation
is vertical. In tile terms that is `cursorCell − (footprint − 1) / 2`, the same centring
`ShipCanvas.TryPlacePose` uses for the armed brush. The cast truncates toward zero, which is
what lets a drop just past the top or left edge settle into row or column 0.

### One window per container, and how they are arranged

*This subsection verified against **1.0.0.13**.*

There is no unified inventory anywhere in the game. `GUIInventory.SpawnInventoryWindow` opens
a window per container, each with its own `GridLayout`, and recurses into
`CO.GetSlots(bDeep: true)` doing the same for the contents. A slotted child gets a window of
its own when all of these hold:

- the slot is not `bHide`, and is not the `social` slot (which is diverted to the
  conversation window),
- `TIsOpenInInv` fires on the child **and** on its parent, which forbids `IsHiddenInv` and
  `IsLocked`,
- and the child has an `objContainer` **or** a non-empty `dictSlotsLayout` of its own.

That last clause is the one that separates a coat's pockets from a rifle's magazine: the
pockets are containers, so they open onto the coat, and the magazine is not.

Where the host declares a position for the slot, the child is parented to the host window,
moved to that offset and given `ToggleTab(false)`, which drops its tab, background and
border. That is what a backpack looks like in game: four bare pouch grids pinned under its
own 4x4, reading as one inventory. Where the host declares no position, the child gets an
ordinary titled window placed beside the parent by `GetWindowPosition` instead.

#### The offsets are exact, in cells

Two constants set the geometry, and the canvas ratio cancels between them:

```
child window position  =  parent position + dictSlotsLayout[slot] * 1.5f * CanvasRatio
one grid cell          =  (int)(24f * CanvasRatio)
```

So an offset is `layout * 1.5 / 24`, which is `layout / 16` **cells**, at any zoom. The host's
own grid takes a second offset of the same kind: `GUIInventoryWindow.SetData` reads
`dictSlotsLayout["self"]` and shifts the grid image inside its own window by it, which is why
`self` is a separate quantity from a slot's offset rather than folded into it.

`ItmBackpack01` is the worked example. `self` is `{5, 0}`, so its 4x4 sits a fraction of a
cell right of the window origin; its four pouches are at `y = -68`, which is 4.25 cells down
(the game's +y is up), clearing a 4-tall grid by a quarter of a cell, and at `x` 0/20/40/60,
which is 1.25 cells apart, so four 1x1 pouches make a row with the same quarter-cell gap.
Only 20 core defs declare a `dictSlotsLayout` at all, every one of them a garment or a
backpack. No human condowner does, so a crew paper-doll is not laid out this way.

### What decides a slot fit is on the ITEM, and no equipment slot filters anything

*This subsection verified against **1.0.0.13**.*

`Slot.CanFit` is the whole of it, and the test it applies is
`coFit.mapSlotEffects.ContainsKey(strName)`. **The item names the slot**, and the host only
has to declare that slot in `aSlotsWeHave`. There is no whitelist on the slot saying what it
accepts, which is the opposite of the way a container works.

A slot does have a trigger field, `strCTAutoSlot`, read by `Slot.CanAutoSlot` and consulted
only on the `bAuto` path. It is not what it looks like: **all 40 slots that declare one are
wound slots**, every one of them naming `TIsAutoSlotWound`, whose whole content is a forbid
on `IsAutoSlotWoundForbid`. Not one equipment slot in the shipped data filters anything.

That is why Ostraplan does not port it. Wounds are anatomy rather than storage and are out of
scope (see `Cargo.CanHoldCargo`), Ostraplan has no auto-slot path for the field to govern, and
a panel showing a rule the tool never applies would be worse than one that says nothing. What
`SlotRules` shows instead is the real rule, from the direction it actually runs: which defs
declare this slot.

> **Ported in Ostraplan:** `PartDef.SlotKeys` (`mapSlotEffects` keys) and
> `PartDef.SlotsWeHave` were already the two sides of `CanFit`; `SlotRules` reads them back
> the other way to answer "what goes in here".
> **Re-verify on a major game version:** whether any non-wound slot has started declaring
> `strCTAutoSlot`, which is the one thing that would make porting it worthwhile.
> `InventoryLayoutTests` and `ContainerRulesTests` both hold a corner of this.

### What the game never does

The search never turns an item to make it fit: `FindNearestUnoccupiedTile` and
`FindFirstUnoccupiedTile` read the footprint as it stands. Nor does the game have an
add-item-to-container operation at all, so there is nothing to be faithful to when Ostraplan
places one. Trying the transpose is therefore Ostraplan's own rule rather than a divergence,
and what it writes is a state the game reproduces exactly on load, per the round trip above.

> **Ported in Ostraplan:** `InventoryGrid.Pack` (the nearest-then-first fill),
> `InventoryGrid.FirstFreeCellRotated` (the capacity rule, with the transpose Ostraplan adds),
> `CargoItem.EffW`/`EffH` (the swapped footprint), `CargoEdit.Move`/`Rotate`, and
> `InventoryWindow`'s drag (cursor-centred drop, R turning the item in hand). Ostraplan grows
> the grid rather than hiding an item that will not fit, where the game panics and draws
> nothing.
> `InventoryLayout` ports the arrangement above: `ShowsWithHost` is
> `SpawnInventoryWindow`'s five clauses, `ToCells` is the `1.5 / 24` conversion, and `Compose`
> is the recursion, which `InventoryWindow.BuildFigure` draws. A pouch stays reachable through
> the breadcrumb as well, which the game has no equivalent of because it opens every level at
> once and never drills.
> **Re-verify on a major game version:** `GetWidthHeightForCO`'s precedence,
> `Item.RotateCW`'s swap, whether any shipped def has started declaring
> `inventoryWidth` / `inventoryHeight`, and the `1.5f` in `SpawnInventoryWindow` against the
> `24f` in `PairXYFromLocalPoint` (the two together are the whole of the layout geometry).

---

## 26. Ship damage (micrometeoroids, weapons and collisions)

**Verified against game `1.0.0.17`.** Both damage paths were re-read against that decompile
for the `FindPointsOfImpact` change below; the live-data figures here are pinned by
`WeaponImpactTests` and `MicrometeoroidTests`, which run against the installed game.

Ostranauts has **two damage systems**, not one. They live in the same class
(`Ostranauts.Ships.DamageSystem`), share the attack-data folder, and disagree about almost
everything that matters: how the impact is located, what geometry is traced, and how much
a part can absorb before it breaks. Which one runs is decided by the *source* of the hit,
never by the target, so the player's own deep-loaded ship is damaged by both.

| | Micrometeoroids | Projectiles and collisions |
|---|---|---|
| Entry point | `DamageRayRandom` → `DamageRay` | `DamageRayShallow` → `ProjectRayOnGrid` |
| Geometry | `Physics.RaycastAll` against Unity colliders | tile-grid traversal, no physics |
| Attack schema | `JsonAttackMode` (`data/attackmodes/coAttacks`) | `JsonShipAttack` (`data/attackmodes/shipAttacks`) |
| Health read | `Destructable.DmgLeft`, the **current form** only | `DataCO.GetMaxHealth`, the **whole break chain** |
| Multi-tile part | absorbs **once**, one collider per hit list | absorbs **per cell** it occupies |
| Also used by | explosions, small arms, melee | missiles, mass drivers, point defence, ship-on-ship collisions, scuttling |

The practical consequence: a micrometeoroid advances a part exactly one break stage and
moves on, while a missile can take the same part all the way to destroyed in one cell.

### What a part can take

A condowner is damageable when its `aUpdateCommands` carries a `Destructable` line:

```
"Destructable,StatDamage,<breakInteractionLoot>,StatDamageMax,<signalCheckPeriod>"
```

`Destructable.SetData` reads slot 1 as the damage stat, slot 2 as the loot naming the
interaction that fires when the pool fills, and slot 3 as the ceiling cond. 952 of the
1,120 stock condowners declare one; 451 of those are also `IsInstalled`.

Two different ceilings are then read off that one declaration:

- **`DataCO.Health`** is plain `StatDamageMax`, the current form's own pool. Crossing it
  fires the break interaction, which mode-switches the part to its damaged form and
  subtracts the pool again (`DestCheck.DamageCheck`, which also clears `IsPristine`).
- **`DataCO.GetMaxHealth`** sums the whole chain: this form's `StatDamageMax` plus,
  recursively, the max health of whatever it breaks into. It resolves the next form by
  walking the break loot to an `Interaction`, taking that interaction's
  `objLootModeSwitch`, and requiring a loot with exactly one entry in `aCOs`. A cooverlay
  re-skins the result through `JCOO.GetModeSwitch`, the same mapping `Catalog.RepairForms`
  walks in the opposite direction (§12).

| Part | `Health` (first break) | `GetMaxHealth` (destroyed) | Note |
|---|---|---|---|
| `ItmWall1x1` | 15 | 45 | → `ItmWall1x1Dmg` (30) |
| `ItmFloorGrate01` | 15 | 45 | |
| `ItmDoor01Closed` | 20 | 80 | three stages |
| `ItmBattery02` | 10 | 40 | |
| `ItmCapacitor01` | 5 | 13 | |
| `ItmReactorIC02Off` | 120 | 123 | |
| `ItmReactorIC03Ignition` | 25 | 25 | breaks via `ACTReactorIC03DamageExplode` |

278 of the 451 installed damageable parts (62%) have a chain that adds health past the
first stage. `GetMaxHealth` memoises into `maxHealth` on first call and falls back to
`Health` for anything not `IsInstalled`, for a missing `Destructable` line, and for any
break whose loot does not resolve to exactly one condowner.

> **Wear is not part of a design.** `StatDamage` is per-instance save state and no def
> declares it (§12), so every part in a plan starts at zero and its remaining pool is the
> full ceiling. That is what makes a worst-case answer deterministic once the attack's own
> randomness is pinned.

### The micrometeoroid path (`DamageRay`)

#### Where a strike comes from

**Two spawn sites, and the atmosphere one is the narrower of them.** A third call exists on
the `Mmoid` button in `CrewSim`'s debug panel and is not gameplay.

**The tension beat** is the one that reaches everywhere. `BeatManager.Micrometeoroid` rolls
`tension_micrometeoroid`, authored at `0.025` in `data/plot_manager/pm_settings.json`, and
spawns against the player's ship anywhere in the system:

```
if (fRoll < chance && !bOnStation && !bDockedWithStation && !IsUsingTorchDrive && !IsInAtmo)
        StarSystem.SpawnMicroMeteoroid(CrewSim.coPlayer.ship, 1f, ...)
```

It passes `fMult: 1f` outright, so a beat strike always arrives at exactly the ATC speed
limit whatever the ship is doing. Being on a station, docked, running the torch drive or
inside an atmosphere all suppress it, and that last exclusion is what makes the two sites
mutually exclusive rather than cumulative.

**The atmosphere roll** is the other. `Ship.UpdateGravAndAtmo` rolls on every atmosphere
update, which is throttled to one per `0.33` of epoch time, and only when the ship has a
gravity point of reference:

```
if (atmosphere.fMicrometeoroidChance > 0 && ptPORGrav != zero
    && Rand(0,1,Flat) < atmosphere.fMicrometeoroidChance)
        StarSystem.SpawnMicroMeteoroid(ship, fMult, resetTimeScale: true)
```

`fMicrometeoroidChance` is authored per atmosphere shell in `data/star_systems`. In stock
1.0.0.11 **only Earth declares a non-zero value**, so this site alone is an Earth
phenomenon and it is the only one that can exceed `fMult: 1`:

| Shell | `fMaxAltitude` (km from centre) | chance | orbital v (m/s) | `fMult` |
|---|---|---|---|---|
| `Earth_Surface` | 6386 | 0 | | |
| `Earth_Troposphere` | 6421 | 0 | | |
| `Earth_Stratosphere` | 6771 | 0.1 | 7671 | 10.23 |
| `Earth_Mesosphere` | 7071 | 0.25 | 7507 | 10.01 |
| `Earth_LowOrbit` | 7300 | 0.1 | 7388 | 9.85 |
| `Earth_HighOrbit` | 7600 | 0.01 | 7241 | 9.65 |

The strength multiplier is closing speed against the body, in units of the ATC speed
limit:

```
fMult = max( |boAtmo.vVel − objSS.vVel| / 5.013440329548757E-09 , 0.5 )
```

That constant is `CrewSim.ATC_SPEED_LIMIT`, and against the game's own AU
(`149 597 872 km`) it is **exactly 750.0 m/s**. It has no ceiling, only the `0.5` floor, so
a ship matching the body's velocity still takes half-strength strikes while one in a
circular orbit at the shells above takes roughly ten times that. The orbital velocities in
the table are `sqrt(GM/r)` for Earth's authored `fMassKG` of `5.97e24`, which is what a
ship actually holding one of those orbits is doing.

Putting both sites together, the whole authored range of `fMult` is `0.5` to `10.23`, with
`1.0` the only value reachable away from Earth. That is what bounds the speed input in
Ostraplan: `375` to `7700` m/s, opening on `750`.

> **The atmosphere site is unreachable in normal play, and the table above is not a menu of
> options.** Only Earth's shells declare `fMicrometeoroidChance`, and reaching one means
> flying inside Earth's atmosphere. Ostranauts is played out at Ceres, Venus and the Jovian
> stations, so in every place a player actually is, the beat site is the only one that can
> fire and a micrometeoroid arrives at exactly `750` m/s for exactly **55 damage**.
>
> This is recorded because the data above is accurate and still led a design astray: the
> figures were read as a set of situations worth offering the user a choice between, and
> built into a preset list, when they describe one place nobody goes. Anything reading this
> section for a **user-facing** decision wants the single 55, with the rest offered as an
> explicit "what if it hit harder" and not as scenery. And none of the vocabulary here
> (`fMult`, the ATC speed limit, the beat) is a word the game says to a player: the only one
> it uses is "micrometeoroid". See `memories/ostranauts-terminology` in the skills repo.

#### The attack

`AModeMicrometeoroid` is a plain `JsonAttackMode`:

| `fRange` | `fDmgBlunt` | `fDmgCut` | `fDmgEnv` | `fPenetration` |
|---|---|---|---|---|
| 100 | 11 | 44 | 55 | 1.0 |

`DamageRay` builds three pools, and only `fDmgEnv` damages structure (blunt and cut go to
crew wounds):

```
envPool   = fDmgEnv   * jam.GetDmgAmount(null) * fMult
bluntPool = fDmgBlunt * jam.GetDmgAmount(null) * fMult
cutPool   = fDmgCut   * jam.GetDmgAmount(null) * fMult
```

`GetDmgAmount(null)` is `Rand(0, 1, Mid)`, a mid-biased roll and **not** a constant, so a
strike's strength is random even at a fixed speed. Pinning it to 1.0 is what "maximum
strength" means; the worst-case structural pool is therefore `55 × fMult`, from 28 at the
floor to about 563 in the stratosphere.

#### The ray always passes through world origin

```
half     = (nCols/2, −nRows/2)
r        = |half|
vStart   = vShipPos + half + AngleAxis(θ, forward) * up * r
DamageRay(vStart, −vStart.normalized, r * 2, ...)
```

The direction normalises `vStart` itself rather than `vStart − centre`, so the ray is aimed
at **world (0,0)**, not at the ship. Item world position is `vShipPos + (col, −row)`, which
`GetTileIndexAtWorldCoords` and `GetWorldCoordsAtTileIndex1` both assert and `MoveShip`
maintains, so world origin is grid tile:

```
convergenceTile = ( col = −vShipPos.x , row = vShipPos.y )
```

`vShipPos` is serialised per ship in `JsonShip`, first set from the top-left tile
coordinates of the first item placed, translated by `MoveShip` and transformed by
`RotateCW`. It is not the centre and nothing keeps it near one. Measured across the 220
core ship objects that carry it:

| | |
|---|---|
| convergence tile inside the grid | 188 (85%) |
| convergence tile **outside the hull** | 32 (15%), including `Babak` and `Babak Refit` |
| median offset from ship centre, as a fraction of the half-diagonal | 0.41 |
| ships offset by more than half the half-diagonal | 37% |
| ships where `\|centre\| > r`, so the `2r` ray cannot reach origin from some angles | 9 |

So micrometeoroid exposure is a **fan through one fixed point**, not an even sweep, and for
the chargen starter that point is fourteen tiles off the port side. This reads as an
oversight rather than a design, but it is what 1.0.0.11 does, and any honest per-angle
answer has to reproduce it.

#### The collider is the sprite rectangle, exactly

Every item is instantiated from one prefab (`DataHandler` builds `strType == "item"` as
`GetMesh("prefabQuad")` then adds `Item` and `CondOwner`). `prefabQuad` carries a
**BoxCollider of size `(1, 1, 1)` at centre `(0, 0, 0.5)`**, read directly out of
`resources.assets`. `Item.ResetTransforms` then overwrites **only z** (centre 5, size 10)
and sets `localScale = (vScale.x, vScale.y, 1)`.

So the world collider is `vScale` tiles wide by `vScale` tiles high, centred on the item's
transform. `vScale` is the sprite in tiles, `textureSize / 16` clamped to at least 1, or
exactly `1 × 1` for any sprite-sheet item, which covers every autotiled wall and floor
(§4). It is **not** the socket footprint, so the 7×7 tanks present a 3×3 target.

Two consequences fall out of the z arithmetic:

- The ray runs in the `z = 0` plane and a collider spans `[GetZPos(), GetZPos() + 10]`
  where `GetZPos()` is `−4 × fZScale`. Escaping the band needs `fZScale > 2.5`; the
  largest in stock data is 1.5, so **every placed item is in band**. Only decorative
  backgrounds are excluded, because `Ship.BGItemAdd` pushes their collider centre `+125`.
- `Physics.RaycastAll` returns **one hit per collider**, so a multi-tile part absorbs once
  per strike no matter how many of its tiles the ray crosses.

> **The collider is the form the part is in now, so a wreck shrinks.** A break does not edit
> the object, it replaces it: `DestCheck.DamageCheck` fires the break interaction, which runs
> `CondOwner.ModeSwitch(coNew, tf.position)`, and that swaps in a whole new `CondOwner` with
> its own `Item`. The replacement takes the outgoing object's transform position verbatim, and
> `Item.ResetTransforms` then scales its quad by its own `vScale`, so the collider keeps its
> centre and changes its size. 140 of the 1152 stock break pairs change sprite size, and the
> jumps are large: `ItmCanisterLHe02` goes 3×3 to 1×1 as `ItmScrapAluminum`, `ItmDoor02Closed`
> 8×8 to 1×1. Only one of the 140 is a break into another still-installed form
> (`ItmHeater02` 8×8 to `ItmHeater02Dmg` 2×2); the rest are the end of a chain, where what
> remains is loose debris on the deck rather than the fitting that was there.

#### Consuming the pool

Hits are sorted by distance and walked in order. A hit with a `Destructable` component
takes `min(envPool, DmgLeft("StatDamage"))`, where `DmgLeft` is
`StatDamageMax − StatDamage` **on the current form**, then `DamageCheck()` fires any break.
Blunt and cut are scaled down by the same fraction the environmental pool lost, and the
walk stops once `envPool` reaches zero.

Because the hit list was built before any break, a part that mode-switches mid-walk is not
re-hit for its new form's pool. A fresh `ItmWall1x1` therefore costs a strike 15 and comes
out as `ItmWall1x1Dmg`, never as rubble.

> **There is no penetration test, so a hull that only cracks does not stop anything.** The
> loop's single exit is `if (num2 <= 0.0) break`, the pool running dry. Nothing anywhere asks
> whether the part survived, whether it was a wall, or whether a hole was made: each hit takes
> `min(pool, DmgLeft)` and the walk `continue`s to the next collider regardless. A 565-point
> strike meeting a fresh `ItmWall1x1` spends 15 on it, leaves it standing as
> `ItmWall1x1Dmg`, and carries the other 550 straight into the compartment behind. That is why
> a strike shows a trail of interior damage under an intact hull, and it is the game's answer
> rather than an artefact of the port: a micrometeoroid here is a damage budget spent along a
> line in distance order, not a projectile that has to breach.

> **A docked ship's parts do not shield you.** With `bAllowDocked: false` the loop
> `continue`s past any hit whose `CO.ship` is not the target, **without consuming any
> pool**. The ray passes through a neighbouring hull for free.

#### Point defence

`SpawnMicroMeteoroid` gives defence a chance only half the time:

```
if (Random.Range(0, 10) < 5 || !WeaponsSystem.TriggerMicroMeteoroidDefense(angle))
      ... apply the strike ...
```

`TriggerMicroMeteoroidDefense` needs `HasWeapons()` **and** `HasActiveSensorOn()`, an
emitting sensor (radar or lidar, the two whose `SensorStrength` defaults above zero)
switched on. It then models the incoming rock as a `ShipSitu` one docking range out along
the strike angle, closing at the ATC speed limit, and offers it to every non-reloading
`IsShipDefensiveWeapon` whose `IsShipWeaponArcAngle` / `IsShipWeaponArcRange` cover it
(`IsPointInView`, a half-angle dot-product test). **Interception is therefore capped at
50%** however good the layout is.

### The projectile path (`DamageRayShallow` → `ProjectRayOnGrid`)

Every projectile in the game is itself a `Ship` with `Classification == Projectile`, and
`CollisionManager` resolves its impact through `DamageRayShallow` regardless of whether the
target is deep-loaded. Missiles, mass driver rounds, point-defence rounds, ship-on-ship
collisions and scuttling all run here.

#### The attacks

`data/attackmodes/shipAttacks` holds eight `JsonShipAttack` entries. Ammo selects one by
`IsShipAttackModeId`, indexed into the `AttackModeMapping` loot:

| id | ammo | attack | `strType` | `fTotalDamage` | `fRadius` | `nSoftEdgeTileRadius` | `fFireChanceCoeff` |
|---|---|---|---|---|---|---|---|
| 1 | `ItmAmmo20mm` | `PointDefenseImpact` | point | 15 | 1 | 2 | 0.1 |
| 2 | `ItmAmmo150mm` | `MassDriverAttack` | ray | 350 | 0 | 0 | 0.1 |
| 3 | `ItmAmmoMissile01` | `MissileAttack01` | circularBlast | 600 | 11 | 3 | 0.25 |
| 4 | `ItmAmmoMissile02` | `MissileAttack02` | circularBlast | 450 | 9 | 3 | 0.25 |
| 5 | `ItmAmmoMissile03` | `MissileAttack03` | circularBlast | 300 | 6 | 3 | 0.25 |
| 6 | `ItmAmmoDecoyMissile01..03` | `MissileDecoy01` | point | 15 | 1 | 2 | 0.1 |
| 7 | — | `ScuttleImpact` | ray | 500 | 1 | 1 | — |
| — | — | `DefaultExplosion` | circularBlast | 300 | 4 | 1 | 0.25 |

`MassDriverAttack` and `ScuttleImpact` carry `fMaxRange: 10`. The three missiles carry
`aTriggerConds: ["IsWall", "IsRigid", "IsPortal"]`, so they detonate at the first
structural tile they reach rather than flying to the middle of the ship.

The launchers, for arc work:

| Weapon | arc | range (m) | defensive |
|---|---|---|---|
| `ItmShipWeaponPDC01` / `03` | 85° | 12 000 | yes |
| `ItmShipWeaponPDC02` | 85° | 15 000 | yes |
| `ItmShipWeaponDecoyLauncher01` | 360° | — | yes |
| `ItmShipWeaponMassThrower01` | 20° | 90 000 | no |
| `ItmShipWeaponMassThrower02` | 15° | 120 000 | no |
| `ItmShipWeaponMassThrower03` | 15° | 66 000 | no |
| `ItmShipWeaponMissileLauncher01..03` | 360° | — | no |

#### The grid

`GridUtils.CreateShallowItemGrid` builds a `List<DataCOWrapper>[,]` over the ship's
bounding box, anchored with `gridOffset = (−vShipPos.x, nRows − vShipPos.y)`. It works from
live condowners when the ship is deeper than `Shallow` and from `json.aItems` otherwise.
Filters, in order:

- `installedOnly` (the default) keeps only `IsInstalled`. The shallow branch also admits
  anything `IsExplosive`.
- `IsMooringPort` is always dropped.
- The live branch additionally drops `IsSystem`.
- A part holding any of `IsPortal`, `IsNavStation`, `IsHeavyLiftRotor`, `IsRCSCluster`,
  `IsShipWeapon` is **bulky**: `SilhouetteUtility.GetFloorVectorGrid` spreads it across
  every cell of its rotated footprint, and it is entered once per cell. Everything else
  occupies its anchor cell alone.

#### Entry geometry

`FindIntersect` puts the incoming object into the ship's frame (rotating by `−objSS.fRot`
about the grid centre), takes the relative velocity as the direction, and asks
`FindIntersection` for the crossing with the grid's bounding rectangle. That function
returns the **nearest of the four edge crossings** by straight-line distance, and it is
worth knowing that it never checks the crossing lies ahead of the object: the `x = 0` and
`y = 0` edges are guarded by requiring the other coordinate be positive, but the
`x = gridMax` and `y = gridMax` edges carry no guard at all, so a degenerate geometry can
return a point off the box.

`DamageRayShallow` then calls `AddVariance`, which is the aim scatter:

- direction rotated by a uniform `±10°`;
- the entry point slid along whichever edge it sits on by
  `round(gridDimension × uniform(−0.4, 0.4))`, clamped to the edge.

`AddStartingTiles` finally spreads `fRadius` tiles either side of the entry cell, along the
entry edge, giving `2·fRadius + 1` parallel starts.

#### The three patterns

**Ray** (`RunRayPattern`) splits `fTotalDamage` evenly across the starting tiles and walks
each one by `round(point + dir·k)`, up to `fMaxRange` steps. An empty cell does not count
against the range, so the budget is `fMaxRange` **occupied** cells deep.

**Circular** (`RunCircularPattern`) first finds the impact cell with `FindPointsOfImpact`,
walking from the entry cell along the direction until it reaches a cell holding a part that
is not already at max health and, when `aTriggerConds` is set, holds one of those conds. It
then collects every cell within Euclidean `fRadius` and applies, in ascending distance
order:

```
cellDamage = fTotalDamage * (1 − distance / max(1, fRadius))
```

> **The impact cell is damaged twice.** The list is seeded with `(impact, 0.0)` and the
> square scan then adds the same cell again at distance 0, so the centre of every blast
> takes two full-strength applications.

> **Why a missile can fly over a wall, and why "it only detonates on exterior hull" is the
> wrong reading.** Two mechanisms, neither of them a rule about the hull. Only the second is
> still live: the game retired the first in `1.0.0.17`.
>
> **1. A tile used to be judged on one part, and it was whichever came first. Fixed in
> `1.0.0.17`.** `FindPointsOfImpact` walks a cell's parts and `continue`s past any at max
> health. Every part that survives that skip is now tested against the attack's trigger conds,
> and the walk goes on to the next part when one does not match:
>
> ```csharp
> foreach (DataCOWrapper item2 in list2) {
>     if (Math.Abs(item2.CurrentDamage - item2.DataCO.GetMaxHealth()) < 0.01) continue;
>     if (triggerConds != null) {
>         for (int i = 0; i < triggerConds.Length && flag; i++)
>             if (item2.HasCond(triggerConds[i])) { list.Add(item); flag = false; break; }
>         continue;                    // ← up to 1.0.0.16 this was an unconditional `break`,
>     }                                //   sitting outside the `if` and taken either way
>     list.Add(item); flag = false; break;
> }
> ```
>
> So a tile stops a missile whenever anything on it with health left carries a trigger cond,
> which is the answer a designer would expect and the one Ostraplan has always given.
>
> **What it did up to `1.0.0.16`.** The loop `break`ed after the first part it did not skip,
> whether or not that part carried a trigger cond.
> `MissileAttack03` declares `aTriggerConds: ["IsWall", "IsRigid", "IsPortal"]`, and interior
> walls carry `IsWall` exactly as exterior ones do (`ItmWallMSSLFWhite`, `ItmWallCAYL05`,
> `ItmWall1x1` all do). But a wall usually shares its tile with a floor, and a floor carries
> none of the three. List the floor first and the missile examined the floor, found no match,
> broke, and moved on **over a tile with a wall on it**.
>
> **Measured on a real ship** (a save-imported *Dancing Jack*, 5863 parts) at `1.0.0.13`, firing
> straight down column 22:
>
> | Tile | Contents, in the ship's own order | Missile, up to 1.0.0.16 |
> |---|---|---|
> | `(22,2)` | Whipple Framework `[IsWall]` — **alone on the tile** | detonates |
> | `(22,46)` | Wall `[IsWall]` (idx 631) │ Floor (idx 1228) │ Conduit | detonates |
> | `(22,23)` | Floor (idx 2802) │ Wall `[IsWall]` (idx 5613) │ Conduit | **passes over** |
> | `(22,22)` | Floor │ Auto Air Vent `[IsRigid]` | **passes over** |
>
> It was never that exterior walls have no floor under them: `(22,46)` has one. It was that the
> outermost hull course is often wall-*only* (46% of that ship's trigger-carrying tiles have the
> trigger alone on them), and that where a hull tile did share, the wall happened to come first
> in the ship's item list. Ship-wide: **1543** tiles carry a trigger part, **85%** had it first
> and stopped the missile, **15%** (232 tiles) had one present but not first and let it through.
> That is why the behaviour read as "only the outside stops them". All four rows detonate at
> `1.0.0.17`.
>
> **Ostraplan asked about the tile throughout, and that is now the port rather than a
> deviation.** `ImpactPoint` asks whether the *tile* holds a trigger that still has capacity,
> not whether its *first* part does.
>
> It was written that way because the game's old rule made the impact point depend on the order
> parts appear in the ship's item list, with no tie-break to port because the game did not have
> one. Two plans identical on screen gave different answers, and a planner whose whole job is
> "what would a hit here break" cannot usefully answer "it depends how the file was written".
>
> **The export used to enforce the ordering, and no longer does.** `ShipExport.TriggerFirst`
> emitted every trigger-carrying part ahead of every part carrying none, each group keeping its
> own relative order, so that the game's old rule applied to an Ostraplan file yielded the
> intuitive answer. It existed because `aItems` is emitted in `ShipDocument.Placements` order
> and `.oplan` round-trips that order exactly (device links are stored as indices into it), so
> the order was precisely the order the parts were laid down: floor a deck and then wall it,
> which is the obvious way to build, and every one of those walls was transparent to missiles in
> game with nothing on the plan saying so. `1.0.0.17` makes the ordering inert, so the partition
> came back out and `aItems` is document order again (#45).
>
> **This also closes the two cases the ordering could not reach.** A ship read out of a save
> keeps its own item order until it is re-exported, and `SaveEdit` rebuilds `aItems` as
> surviving originals verbatim plus new parts appended, so a wall added to an existing save
> lands after the floor already on its tile. Both used to leave the planner's answer disagreeing
> with what that hull does in game. Neither can now.
>
> Everything downstream of the impact point is the game's arithmetic exactly: the blast falloff,
> the doubled centre, the soft-edge cap, and what each cell absorbs. Spent parts are still
> skipped, so successive shots still walk the impact point inward.
>
> **2. The walk point-samples, so a diagonal steps over cells.** Both `FindPointsOfImpact`
> and `RunRayPattern` advance by one unit of the **normalised** direction and round:
> `point += normalizedDirection; item = RoundToInt(point)`. That is not a grid traversal. On
> a path that is not axis-aligned a single step can cross a column boundary and a row
> boundary at once, and the cell between them is never sampled. A freehand path of
> `(11.9, −4.4) → (34.9, 45.7)` takes 57 steps and makes **20** such jumps, any of which a
> one-tile wall can fall into.
>
> Ostraplan lets the user draw any line, where the game only ever fires from a bounding-box
> edge with ±10° scatter, so near-diagonal paths are far more reachable here than in play.
> A supercover traversal would fix the geometry and break the parity; the parity is what is
> shipped. `WeaponImpactTests` pins both mechanisms.
>
> **A third difference, also deliberate: the drawn line is an aim, not a path.** The walk is
> bounded by the grid rather than by the length of the drag, so a shot runs along its heading
> until it finds something or leaves the ship. Bounding it at the release point made the same
> shot down the same line hit or miss according to how far someone happened to drag, which is
> a property of the gesture rather than of the hull. The game does not bound a projectile by a
> distance either — it enters at the grid edge and runs — so a pointer is closer to it than a
> segment. The canvas draws the aim past the end of the drag so a blast landing beyond it is
> not a surprise.

**Point** (`RunPointPattern`) gives each starting tile its own impact point via the same
walk, and applies the full `fTotalDamage` at each.

`nSoftEdgeTileRadius` marks the outermost starts (or, for circular, every cell beyond
`fRadius − nSoftEdgeTileRadius`) as `damageOnly`, which caps that cell at `DataCO.Health`
instead of `GetMaxHealth`. With three starting tiles and a soft edge of 2, **every** tile of
a point-defence impact is soft.

> **The soft cap is on the part, not on the tile, and it lifts.** `ApplyDamageToCell`
> downgrades the ceiling only under `if (damageOnly && !item.IsDamaged)`, so a soft hit is
> capped at the first break while the part is whole and prices it against the whole chain
> the moment it is not. `DataCOWrapper.IsDamaged` is `DataCO.HasCond("IsDamaged")` or
> `CurrentDamage > 0 && CurrentDamage >= DataCO.Health`, which a part satisfies as soon as
> its first pool fills. So 20 mm fire cannot take a wall from whole to gone in one burst, but
> a second burst on the same tile finishes it, and a part that arrives already damaged is
> priced against the whole chain from the first round.

#### Applying damage to a cell

`ApplyDamageToCell` walks the cell's parts in order, skipping any already at
`GetMaxHealth`, and pushes `CurrentDamage` up to that ceiling (or `Health` when
`damageOnly` **and** the part is still whole), passing the remainder on. `IsSocial` parts
are crew and take `ApplyCrewDamage` instead, through the `JsonShipAttack`'s own
`strJsonAttackMode`.

> **Max health, not destruction, is what takes a part out of the reckoning.** Both
> `FindPointsOfImpact` and `ApplyDamageToCell` skip on
> `|CurrentDamage − GetMaxHealth()| < ε` (0.01 and 0.1 respectively) and never ask what form
> the part ended up in. The two come apart whenever a chain finishes on something the game
> still names but does not install: `ItmStorageBin2x101` runs bin → `…Dmg` → `ItmScrapAluminum`
> → `ItmScrapTrash`, and because `GetMaxHealth` stops chaining at the first non-`IsInstalled`
> form, the bin is full at 35 while still being a named object rather than nothing. A part with
> no `Destructable` line at all satisfies the same test untouched, its max health being zero,
> which is the mechanism behind "a strike passes straight through it".

> **In the shallow branch a part never changes form.** `DataCOWrapper.CurrentDamage`'s setter
> mode-switches the live `CondOwner` through its break chain only when it wraps one; wrapping a
> `JsonItem` it just writes `StatDamage` and the item keeps its original def. Everything the
> projectile path reads afterwards — `HasCond`, `Health`, `GetMaxHealth`, `IsDamaged` — is
> therefore the **original** def's, with one accumulating `StatDamage` measured against the
> whole chain. That is the branch a planner models.

Fire is rolled per damaged part on a ship at `Loaded.Edit` or deeper:

```
base = 0.0 if IsFireproof, 0.9 if IsFlammable, 0.5 if IsBurnable, else 0.25
ignite if Rand(0,1,Flat) <= base * jam.fFireChanceCoeff
```

A part that crosses into damaged for the first time also triggers system side effects:
`IsRCSCluster` decrements `nRCSCount` and sets the ship drifting once it reaches zero;
`IsNavStation` unregisters the ship, drops its transponder antenna and sets it drifting;
`IsFusionReactorCore` has a 25% chance to scuttle a shallow ship outright; `IsShipWeapon`
resets the weapon data. `IsFusionReactorCore` and `IsExplosive` additionally return as the
hit's `Explosive`, which runs `TriggerChainExplosion`: a circular pattern centred on that
cell using the part's own ship attack, or `DefaultExplosion` when it declares none.

### What a planner cannot reproduce exactly

- **Every strike is a random draw.** `GetDmgAmount`'s mid-biased roll on the meteoroid
  path, `AddVariance`'s aim scatter on the projectile path, the fire roll, and the 50%
  point-defence bypass are all live RNG. A plan can only answer for a pinned worst case.
- **Crew wounds are out of scope.** The blunt and cut pools exist to hurt people
  (`Wound.Damage`, `GetWoundLocation`), and a design has no crew.
- **`vShipPos` moves at runtime.** `MoveShip` and `RotateCW` both rewrite it, so a ship's
  convergence tile is a property of its current world anchor and not only of its layout. A
  design can report the anchor its own template or save carries, which is what it will
  spawn with, and nothing beyond that.

> **A ship Ostraplan writes gets a NEW convergence point, and it lands outside the hull.**
> `ShipExport` anchors the file at `vShipPos = (0,0)` and the game re-seeds the anchor off the
> first item on load, so world origin falls on the export grid's own origin — the bounding box
> minus its one-tile pad. Every exported ship therefore converges just off its top-left corner,
> where 85% of the shipped fleet converges *inside* the hull. That is not a defect of the export:
> it makes such a ship take **fewer** micrometeoroid hits than a stock one, because most angles
> now miss. It does mean a design imported from a save must be measured in the frame it arrived
> with (`ShipDocument.SourceShipPos`) rather than the one it would be exported into, or the
> answer describes a different ship from the one the player is flying.
- **Fire propagates.** It is modelled here as one step from the triggering hit; the game
  keeps burning afterwards, which is simulation and excluded by the same rule as gas flow
  and crew pathing. Chain explosions are a different case, and are not modelled at all
  because the game does not have them (see below).

### Chain explosions: there are none to port (#42)

Neither a mining charge nor a loose missile detonates when something else damages it. Checked
against the shipped data of 1.0.0.13, and worth writing down because the opposite is widely
believed and one of the two cases looks like an authoring mistake rather than a decision.

Damage response is data, not code: an item's `aUpdateCommands` names the loot to fire when a
stat reaches its max, in the form `Destructable,<stat>,<loot>,<statMax>,1.0`.

**Mining charges cannot chain, and that is deliberate.** `ItmExplosiveCharge01`/`02` and their
`…Armed` forms all route damage to `ACTDefaultDestroy`, which is the ordinary destroy path
(`MSDestroyDefault` → `ItmDefaultDestroyed`). Only the armed forms carry a second command, and
it is on the fuse rather than on damage:

```
"Destructable,StatDamage,ACTDefaultDestroy,StatDamageMax,1.0"        // damage → destroyed
"Destructable,StatFuse,ACTExplosiveChargeExplode,StatFuseMax,1.0"    // timer  → explodes
```

The item description says so outright, and is the likely source of the confusion because it
reads at a glance like a warning that damage sets them off: *"While damage activates a failsafe
killswitch, neglect and disrepair can be hazardous…"*. The killswitch **is** the destroy path.
Damage is what stops a charge going off, not what sets it off.

**Missiles were given the mechanic and it is not wired up.** The whole chain exists for all
three live missiles, complete and unreachable:

| Def | State |
|---|---|
| `ACTAmmoMissile0*DamageExplode` | **defined** in `loot.json`, referenced by nothing |
| `MSAmmoMissile0*DamageExplode` | defined, reached only through the above |
| `ItmAmmoMissile0*DamageExplode` | defined; spawns `SysExplosionMissile0*` plus component loot |
| `ACTMissile0*Destroy` | **named by `ItmAmmoMissile0*` on damage, and defined nowhere** |

So a live missile's damage command points at a loot that does not exist, while the loot that
would have exploded it is orphaned. The two names differ by exactly the `Ammo` infix, which
reads like a rename applied to the loot files and not to the items. `MSMissile01Destroy` and
`MSMissile02Destroy` are defined and turn a missile into its component shell, but are equally
unreachable, and `MSMissile03Destroy` was never written at all. Decoy missiles are unaffected:
they use `ACTDefaultDestroy` like everything else and behave correctly.

None of these names appears in `Assembly-CSharp.dll` in either encoding, so nothing invokes them
from code either. (`ACTDefaultDestroy` does appear, once, so the engine knows that one by name.)

**Nothing to port, so nothing is simulated.** A damaged loose missile in game today does not
explode, and a damaged charge is destroyed by design. Ostraplan models neither, which is the
faithful answer for as long as the data stays this way. If Blue Bottle Games repoints the
missiles' damage command at the `…DamageExplode` loot, this becomes a real mechanic and the
solver would need a propagation step; that is the trigger to revisit, and until then a toggle
would only be a mode to remove later.

> **Ported in Ostraplan:** `MicrometeoroidStrike` and `WeaponImpact` behind
> **Simulate ▸ Micrometeoroid Strike…** and **Simulate ▸ Weapon Impact…**, sharing one damage
> heat overlay scaled green at zero, amber past `DataCO.Health` and red past
> `DataCO.GetMaxHealth`, and one `DamageState` that accumulates across strikes beside the
> document rather than in it. The two solvers stay separate because the two models are.
> **The path is drawn by the user, not aimed by the game.** Everything a ray does once drawn is
> ported exactly, but where it may go is not constrained: a planner that could only fire the
> rays the game rolls could not answer "what would a hit here cost", which is the question a
> design is being checked against. `GameRayFor` still exposes the game's own aiming, and the
> canvas marks the convergence point, so the difference is visible rather than hidden.
> The randomness is not reproduced: the roll is pinned to its worst case, aim variance is
> off, and the fire chance is not rolled at all (§26 "What a planner cannot reproduce").
> **A chain that ends in loose debris ends the part.** The game names what a bin or a tank
> leaves behind (`ItmScrapTrash`, `ItmScrapAluminum`), so "did the break form resolve" cannot
> tell a wall becoming a damaged wall from a rack becoming a pile of metal. `Catalog.IsInstalledForm`
> is the test instead, which is the line `DataCO.GetMaxHealth` already stops its own chain walk
> at. A part that reaches it is destroyed, `DamageState.Project` drops the tile and both solvers
> pass over it. That last part is a deliberate deviation: the scrap is a real collider in the
> game, but it is not a part of the ship and the plan does not draw it.
> **A damaged wall still seals.** `ItmWall1x1Dmg` carries `IsWall`, `IsCheckRoom` and
> `IsInstalled` exactly as `ItmWall1x1` does, and takes *more* damage before it is gone
> (`StatDamageMax` 15 → 30). What it loses is half its burst pressure: `StatGasPressureMax`
> 4000 → 2000, and `ItmWallWindow1x1Dmg` the same. So a broken hull course reads amber on the
> plan and goes on holding air, which is the game's answer and is asked about often enough to
> be worth writing down. Ostraplan does not yet model structural `StatGasPressureMax` anywhere
> (`ContainerFill` uses it for canister burst only), so the halved ceiling is not reported.
> **Re-verify on a major game version:** the
> `AModeMicrometeoroid` and `shipAttacks` numbers, `AttackModeMapping`'s ordering, the
> `prefabQuad` collider, the `ATC_SPEED_LIMIT` constant, whether `−vStart.normalized` has
> been corrected to aim at the ship centre, whether any body other than Earth has been
> given a `fMicrometeoroidChance`, and whether `FindPointsOfImpact` still tests every part on
> a tile for a trigger cond rather than only its first.

---

## 27. Ship weapons and firing groups (`WeaponsSystem`)

**Verified against game `1.0.0.13`.**

### A firing group is a condition on the weapon

There is no firing-group object anywhere in the game. Nine weapons in group 3 is nine weapons
each carrying **`IsShipWeaponFiringGroup`** at amount 2 — a plain simple condition
(`data/conditions_simple`, friendly name "Firing Group"), declared on the condowner like any
`Stat*`.

**Stored 0-based, shown 1-based.** `MFDWeaponDetails.CycleFiringGroup` steps the amount and wraps
it at 0..8; both MFD readouts print `RoundToInt(GetCondAmount(...)) + 1`; and
`WeaponsSystem.ShootManual(g)` matches `g == amount + 1` against the 1..9 that
`NavModWeaponsControl.KeyHandler` sends from `CommandFireGroup1`..`9`. So there are **nine**
groups, fixed in code rather than in data, and the number on the key is one more than the number
in the file.

`ShootManual` fires the ship's weapons that are `IsPowered` and pass
`TIsShipWeaponInstalledOn` — `IsShipWeapon` + `IsInstalled`, forbidding `IsOff` and `IsDamaged`.

### The stock groups, and the mass-thrower hole

| Class | Type cond | Declared amount | Shown as | Arc |
|---|---|---|---|---|
| Point-defence cannon | `IsShipWeaponPDC` | 2 | 3 | 85°, 12-15 km |
| Missile launcher | `IsShipWeaponMissileLauncher` | 1 | 2 | 360° |
| Decoy launcher | `IsShipWeaponDecoyLauncher` | 3 | 4 | 360° |
| Mass thrower | `IsShipWeaponMassThrower` | **none declared** | 1 | 15-20°, 66-120 km |

Eleven of the twelve mass-thrower defs declare no `IsShipWeaponFiringGroup` at all (only
`ItmShipWeaponMassThrower01Dmg` does, at 2). `CondOwner.GetCondAmount` returns 0 for a condition
an owner does not carry, so the game reads every one of them as group 1. `ItmShipWeaponDecoyLauncher01DmgLoose`
says 1 where its five siblings say 3 — a data slip with no effect, since a loose damaged launcher
does not fire.

The `…Off` and `…Loose` defs mostly drop `IsShipWeaponArcAngle`, which their running form declares.
Only the PDCs keep it in both states.

**Not one of the 220 core `data/ships` files authors a firing group.** Every ship in the game
spawns at its defs' own, and the player re-groups it by hand.

### The two other switches on the page, and why they are absent from every def

`IsShipWeaponFiringModeManual` and the two target-select conds are **not declared by any of the
fifty weapon defs**. They exist only as global condition definitions, created at runtime by
`MFDWeaponDetails.OnButtonDown` calling `SetCondAmount` — which works because
`CondOwner.AddCondAmount` falls back to `DataHandler.GetCond` when the owner's own map has no such
cond. So "absent means default" is the normal case for them, and writing one onto an item that
never declared it is exactly what the game does.

Both are load-bearing rather than cosmetic:

- **`IsShipWeaponFiringModeManual`** — `NavModWeaponsControl.KeyHandler` leaves the weapon out of
  `_weaponsToFire`, so it never auto-fires at the combat target and answers only to its group's key.
- **`IsPDCTargetModeMMMOnly`** — the same loop skips the weapon unless the target's
  `Classification` is `Projectile`.
- **`IsPDCTargetModeShipsOnly`** — `WeaponsSystem.ActivateDefenseSystems` drops the weapon from the
  point-defence volley, so a ship whose cannons all carry it has **no** missile or meteoroid defence.

They are a tri-state stored as two flags: `OnButtonDown` cycles none → `MMMOnly` → `ShipsOnly` →
none and never sets both.

### Which way a weapon points

`WeaponsSystem.GetItemsDefaultFiringAngle(itmRotation)` is `ship.objSS.fRot + rad(itmRotation)`,
and `SpawnProjectile` resolves that angle against world **+Y**. The item's own rotation is the only
per-weapon term, so a weapon's bearing is its placement rotation and nothing else. `IsShipWeaponArcAngle`
is the total arc centred on it; `IsShipWeaponArcRange` is how far that arc reaches, in metres.

`IsShipWeaponArcAngleReduction` is the targeting solution converging and is pure runtime state:
`NavModWeaponsControl` walks it toward the arc at `IsShipWeaponTargetingSpeed` per tick and fires
when it closes. Nothing a design can say touches it.

### Editing it in game

`MFDWeaponDetails` is the only editor: one weapon at a time, its group stepped by a button that
wraps. The single bulk action is `ApplyToAll`, which copies `IsOff`, the firing mode, both target
flags and the firing group from the open weapon onto **every weapon carrying the same type cond**
— and warns "Could not find type Cond on weapon" and does nothing for a decoy launcher, which it
has no branch for.

> **Ported in Ostraplan:** `WeaponPanel`, and it is authored rather than merely read. The group,
> firing mode and target select ride the same per-instance condition route a container's fill takes:
> an `aCondOverrides` entry per changed cond on the mod export, and on a save write-back the same
> entries on the item **plus** the condition owner's own `aConds` (see §17 and `SaveEdit.SetFillConds`
> for why one channel is not enough). Only conds that differ from the def are written, so a design
> that says nothing stamps nothing; a save import reads each weapon's page back
> (`SaveEditImport.ReadWeapon`), so a player's own arrangement survives a round trip through the
> planner rather than being overwritten with stock groups.
>
> A mass thrower **is** given a group despite its def declaring none, unlike the gate
> `DeviceSettings.Applicable` applies to a pump's modes. The game accepts it by the `AddCondAmount`
> fallback above and its own Apply To All writes all five keys unchecked; a ship's main gun that
> could not be assigned to a group would be a hole in the feature rather than a safeguard. The target
> select **is** gated on `IsShipWeaponPDC`, because those two conds are only read down paths a
> cannon reaches.
>
> `WeaponPanel.Facing` reports the bearing as Fore/Starboard/Aft/Port from the placement rotation,
> composing the export's `fRotation = Norm(-Rot)` with its y-flip; a 360° arc, or a def declaring no
> arc, reports "any bearing" rather than being given a side it does not have. The weapon's own
> `IsOff` state is **not** modelled here: Ostraplan already carries it as the `…Off` def pair through
> `Restate`, and a second representation would let a design say both things at once.
>
> **Re-verify on a major game version:** the 0..8 clamp in `CycleFiringGroup`, the `+ 1` in
> `ShootManual` and both MFD readouts, the `CommandFireGroup` bindings, the stock group amounts in
> `condowners_ship_combat.json`, and whether the mass throwers have been given the group cond their
> siblings carry.


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
- **Damage is TWO systems**: micrometeoroids raycast Unity colliders and read the current form's
  `StatDamageMax`; missiles, mass drivers, point defence and collisions walk the tile grid and read
  the whole break chain (`DataCO.GetMaxHealth`). Both hit the player's loaded ship (§26).
- **The damage collider is the SPRITE rect, not the footprint**: every item is `prefabQuad`, whose
  BoxCollider is `1×1` scaled by `vScale`, so a 7×7 tank presents a 3×3 target (§26, §4).
- **Every micrometeoroid ray passes through world origin**: `−vStart.normalized` aims at `(0,0)`,
  i.e. grid tile `(−vShipPos.x, vShipPos.y)`, which is off-centre on most ships and outside the hull
  on 32 of the 220 core ships including both Babaks (§26).
- **Micrometeoroid strength is closing speed / 750 m/s**, floored at 0.5 and uncapped;
  `5.013440329548757E-09` is `ATC_SPEED_LIMIT`. Only Earth declares `fMicrometeoroidChance` (§26).
- **A blast damages its impact cell twice**: the cell list is seeded with the impact point and the
  square scan adds it again at distance 0 (§26).

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
| Device signal connections, breaker channel (`Electrical` GPM, `GUIBreaker`) | ported (§14) | `DeviceLink` / `DeviceLinks`, `ShipExport.WireDeviceLinks` |
| Device signal connections, sensor channel (`Panel A` `strInput01`, `GasPump`/`Heater`) | ported (§14) | `SensorLink` / `SensorLinks`, `DevicePanels`, `ShipExport.WireSensorLinks` |
| Device panel settings (`nKnobBus`, `bTurbo`, `bReverse`, `bSlowMode`) | ported (§14) | `DeviceSettings`, `Placement.Device` |
| Loot spawner panel (`GUILootSpawn`: type, target, range, count, the three condition gates) | ported (§6); the type routes the spawner to `aItems` or `aShallowPSpecs` | `SpawnerSettings`, `SpawnerCatalog`, `LooseObject.Spawner` |
| Object rename (`CondOwner.Rename` / `CheckForRename`, the `Rename` GPM) | ported (§14) | `Rename`, `Placement.CustomName`, `LooseObject.CustomName` |
| Power-state switching (`PreferPoweredState` both ways; alarm sensing) | ported (nominal states only, §12) | `Catalog.PowerToggle` |
| Deferred lighting (`Visibility` + `LoSPass`) | ported (preview only) | `LightNetwork`, `VisibilityMesh`, `LightComposite` |
| `JsonShip` (de)serialization — export/template/save schema | ported | `ShipExport` (write), `ShipTemplate` (read) |
| Coordinate/rotation mapping (centre ↔ top-left, CCW) | ported | `ShipGrid.TemplateTile` + `ShipExport` |
| On-demand resolution of any placed (non-buildable) def | ported | `Catalog.Lookup` / `Catalog.ResolveDef` |
| Save player-ship identification (`strShip`) + layout strip | ported | `SaveImport` |
| Save write-back (frame rebuild, room-CO drop, dimensions) | ported | `SaveEdit`, `SaveEditImport` |
| Ship zones (`aZones`) as authored data | modelled (preserve/draw/edit, not validated) | `ShipZone` / `ZoneGeometry` |
| Loose-item placement (`TILItemForbids` over the item's footprint) | ported for the **interactive drop only**, which is the only path the game itself gates: `Ship.SpawnItems` places a template's deck cargo unchecked, so an imported design is never judged. The reverse — structure refusing to build over a deck item — is deliberately not ported, so the deck stays out of `Conds` | `LoosePlacement`, `ShipDocument.LooseConds` |
| Wear/damage (`BreakIn` / `DamageAllCOs`) | ported (optional) | `WearModel` |
| Repair (`installables` repair jobs, §12) | ported (broken def → working def; the undamage jobs are the `WearOptions.Repaired` half) | `Catalog.RepairForms`, `Repair` |
| Container contents (`GasContainer` capacity, pressure, mass and value; `aCondOverrides`) | ported (the static model; no gas *flow* between containers, §24) | `ContainerFill`, `Placement.Fill` |
| Nav console loadout (`SysLootSpawner` + `ItmNavStationMods*`, §17) | modelled (the stock `Pod` set + course plot + flight dynamics, baked as literal items; the spawner is not reproduced) | `NavConsole` |
| Nav console screen layout (`GUIOrbitDraw.LoadModules`, `EditMenu.DoesModFit`, `SaveModules`, §17) | ported (rects, bounds, overlap, tray; no rect is invented or resized) | `NavConsole.Arrange` / `ConfigEntries` |
| Obtainability (brokers, chargen) | ported | `KioskExport`, `StartingShipExport` |
| Contained/slotted sub-objects on read; exterior-margin trim | not modelled (corpus-only; import drops sub-objects) | — |
| Micrometeoroid strike (`SpawnMicroMeteoroid`, `DamageRayRandom`, `DamageRay`) | ported (worst-case roll; §26) | `MicrometeoroidStrike`, `DamageState`, `SpriteExtent` |
| Projectile/collision damage (`DamageRayShallow`, `ProjectRayOnGrid`, `ApplyDamageToCell`) | ported (no aim variance, no fire roll; §26) | `WeaponImpact` |
| Break chain (`Destructable`/`DestCheck`, `DataCO.Health` / `GetMaxHealth`) | ported | `Catalog.BreakForms` / `Health` / `MaxHealth` |
| Fire spread and post-impact burning | excluded (a simulation, not a plan) | never ported |
| Crew LOS/proximity, docked-ship, station build-zone permission **in `CheckFit`** | excluded (in-game only — they gate the interactive builder, not a spawned ship) | never ported |

---

*Companion documents: [usage.md](usage.md) (how to use Ostraplan) and
[README.md](../README.md) (overview, install, build).*
