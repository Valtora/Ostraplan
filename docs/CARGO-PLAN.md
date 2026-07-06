# Ostraplan — container & cargo system (design + build plan)

**Status:** Phases 1 + 2 done (cargo model, save-import population, **gridded inventory viewer**), 2026-07-06. Phase 3 (preserve-through-edits) next.
**Scope (signed off via AskUserQuestion):** the *full* system — preserve cargo through edits, view any container's inventory, and edit/author contents. Covers **container cargo** (`strParentID`) **and slotted equipment** (`strSlotParentID`). The standard nav-module auto-injection fills **only empty consoles** (a console carrying real modules keeps them).
**Viewer decisions (AskUserQuestion, 2026-07-06):** a **separate themed dialog that mirrors the in-game gridded inventory** (items occupy multiple tiles — not a flat list), **real item sprites**, **drill-in** for nested containers, and a **full paper-doll** for slotted gear. Cargo re-attaches **eagerly on `.oplan` reopen**.

## Why

Ostraplan models only top-level structural grid parts. On import it **drops every container's contents** (cargo, equipment, nav modules) — `TemplateImport.Build` skips `item.Contained`. Those contents survive a save write-back only if the container is *kept* or *moved* (verbatim copy); a **delete / re-skin / replace drops them** (the "Cargo will be permanently deleted" warning), and **exported ships carry no cargo at all**. So Ostraplan is cargo-blind: it can't show contents, and container edits are lossy. Goal: model cargo as first-class data attached to each part so it's preserved through edits, viewable, and authorable.

## Game container model (verified: live data + decompile)

- **Two parent links** on an item: `strParentID` = loose cargo in a container's grid; `strSlotParentID` = equipped into a **named slot**. (Nav modules use `strParentID`.)
- A **container** = an item with `IsContainer` + a grid (`nContainerWidth`×`nContainerHeight`) + an allowed-item filter `strContainerCT` (a CondTrigger). `IsHiddenInv` merely hides contents from the main UI (nav consoles, HVAC filters use it). **Named slots** come from `aSlotsWeHave` (EVA pockets, crew hands `heldL`/`heldR`); positions in `dictSlotsLayout`.
- **Nesting**: arbitrary depth via the parent chain. Contained items carry world-ish `fX/fY` (≈ the parent's). In a **save** each contained item has its own 1:1 CO (live state); a **template** defaults COs on load.
- Contents surfaced in-game via the `Inventory` interaction / a `GUI*` prefab.

### Inventory-grid layout (verified for the viewer — decompile `GUIInventory`/`GridLayout` + real saves)

- Container grid dims live on the **condowner def**: `nContainerWidth`×`nContainerHeight` (default **6×6** when a container declares none — the game's `Container` default). Real examples: backpack 4×4, crate 3×3, dolly02 6×6, small canister 1×1.
- An item's **footprint on the grid** = `GUIInventoryItem.GetWidthHeightForCO`: the def's `inventoryWidth`/`inventoryHeight` when set, else the item's map footprint (`nWidthInTiles`/`nHeightInTiles`), else 1×1.
- An item's **grid position** = its CO's persisted `inventoryX`/`inventoryY`. **But most contained items sit at (0,0)** — a container never opened in-game never materialised its layout — so on open the game *packs* them: `AddToWindow` honours the stored cell when free, else takes the unoccupied cell **nearest** it (`FindNearestUnoccupiedTile`), else the first free (`FindFirstUnoccupiedTile`). The viewer's `InventoryGrid.Pack` ports exactly this (so a real backpack with distinct stored positions stays put, and a pile-at-(0,0) container fills sensibly).
- **Stacks (verified — an important quirk for later item manipulation):** a stack of N identical items is stored as a **lead item plus its N-1 copies as same-def children** (parented to the lead via `strParentID`); the game's `StackCount = aStack.Count + 1`. So **the real count = same-def-children + 1**. The `IsStacking` cond is the stack **capacity** (`nStackLimit-1`), *constant per def*, **not** the count — do not read it as the count. `Cargo.BuildForest` flags such a node `IsStack` (Stack = members+1), keeps the members (for preservation / splitting later) but the viewer draws it as one ×N tile that isn't a container. A genuine container holds children of a *different* def, so this never collapses real cargo. (Bug caught in the first in-app test: stacks were showing as drillable boxes of themselves with the capacity as the count.)
- **Slots (paper-doll):** the host def lists `aSlotsWeHave` and a `dictSlotsLayout` = `{ slot: {x,y,z} }` (pixel offsets, +y up, a `"self"` host anchor). Slot metadata (friendly name, icon, align) is in `data/slots` (`slots.json` + `slots_wounds.json`). A slotted child's slot = its own def's `mapSlotEffects` **keys** intersected with the parent's `aSlotsWeHave` (`Slots.SlotItem` on load). The viewer scales the raw offsets so cells don't overlap.

## Ostraplan cargo model

- **`CargoItem`** (`StrID`, `DefName`, `Friendly`, `Slotted`, `Children[]`) — a lightweight identity+display tree node. **`Placement.Cargo`** = a part's direct children.
- **Populated at save-import** from the ship's parent→children index (`SaveEditImport` already builds it). Rides through a move automatically (the same `Placement` object). The verbatim item/CO JSON stays in `SaveShipContext` keyed by `StrID`; the write path pulls it from there.

## Build phases

1. **Model + save-import** ✅ *(done — commit 39b857c + enrichment this session)* — `CargoItem` (now carries grid pos/size/stack/slot), `Placement.Cargo`, populated on import, tests. **No behaviour change** — the inject still uses the context. Safe.
2. **Inventory viewer** ✅ *(done — this session)* — right-click a placed container → **"View contents (N)…"** opens `InventoryWindow`: the container's tile grid drawn with real sprites (`InventoryGrid.Pack` mirrors the game's fill), stacks with ×N, a paper-doll for slotted gear (`dictSlotsLayout` + `data/slots`), and **drill-in** with a breadcrumb. Cargo re-attaches eagerly on `.oplan` reopen (`AttachSavedCargoAsync`). New Core: `InventoryGrid`, `SlotDef`, container/slot fields on `CondOwnerDef`/`PartDef`, `Catalog.Slots`, `SaveShipContext.CargoByOrigin`. Preview via `--invsmoke`. Read-only.
3. **Preserve-through-edits (write path)** — cargo travels through re-skin / replace / def-change (transfer `Cargo` to the new placement); delete warns from `Placement.Cargo`; inject **and export** read cargo from placements; nav auto-inject fills only empty consoles. **Fixes the loss.**
4. **Editor + authoring** — add/remove items; pre-load containers in exported ships; validate against grid size + `strContainerCT` filter ("the Law" extended to cargo); `.oplan` persists authored cargo.

Slots (`strSlotParentID`) are handled in the model from phase 1; slot-specific UI lands in phases 2/4.

## Risks / notes

- Phase 3 refactors the delicate, heavily-tested save-edit **inject** — do it behind the existing inject tests.
- Contained `fX/fY` are world-ish (≈ parent) — the existing move-cargo delta shift is correct; keep it.
- Authored cargo (phase 4) needs synthesized COs (like `SaveEdit.SynthesizeCo`) + capacity/filter validation.
- Reopened `.oplan` designs won't show cargo until phase 2 re-attaches it on relocate (phase 1 attaches on fresh import only).
