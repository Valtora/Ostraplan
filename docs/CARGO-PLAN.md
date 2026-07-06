# Ostraplan — container & cargo system (design + build plan)

**Status:** Phase 1 (cargo model + save-import population) in progress, 2026-07-06.
**Scope (signed off via AskUserQuestion):** the *full* system — preserve cargo through edits, view any container's inventory, and edit/author contents. Covers **container cargo** (`strParentID`) **and slotted equipment** (`strSlotParentID`). The standard nav-module auto-injection fills **only empty consoles** (a console carrying real modules keeps them).

## Why

Ostraplan models only top-level structural grid parts. On import it **drops every container's contents** (cargo, equipment, nav modules) — `TemplateImport.Build` skips `item.Contained`. Those contents survive a save write-back only if the container is *kept* or *moved* (verbatim copy); a **delete / re-skin / replace drops them** (the "Cargo will be permanently deleted" warning), and **exported ships carry no cargo at all**. So Ostraplan is cargo-blind: it can't show contents, and container edits are lossy. Goal: model cargo as first-class data attached to each part so it's preserved through edits, viewable, and authorable.

## Game container model (verified: live data + decompile)

- **Two parent links** on an item: `strParentID` = loose cargo in a container's grid; `strSlotParentID` = equipped into a **named slot**. (Nav modules use `strParentID`.)
- A **container** = an item with `IsContainer` + a grid (`nContainerWidth`×`nContainerHeight`) + an allowed-item filter `strContainerCT` (a CondTrigger). `IsHiddenInv` merely hides contents from the main UI (nav consoles, HVAC filters use it). **Named slots** come from `aSlotsWeHave` (EVA pockets, crew hands `heldL`/`heldR`); positions in `dictSlotsLayout`.
- **Nesting**: arbitrary depth via the parent chain. Contained items carry world-ish `fX/fY` (≈ the parent's). In a **save** each contained item has its own 1:1 CO (live state); a **template** defaults COs on load.
- Contents surfaced in-game via the `Inventory` interaction / a `GUI*` prefab.

## Ostraplan cargo model

- **`CargoItem`** (`StrID`, `DefName`, `Friendly`, `Slotted`, `Children[]`) — a lightweight identity+display tree node. **`Placement.Cargo`** = a part's direct children.
- **Populated at save-import** from the ship's parent→children index (`SaveEditImport` already builds it). Rides through a move automatically (the same `Placement` object). The verbatim item/CO JSON stays in `SaveShipContext` keyed by `StrID`; the write path pulls it from there.

## Build phases

1. **Model + save-import** *(this increment)* — `CargoItem`, `Placement.Cargo`, populate on import, tests. **No behaviour change** — the inject still uses the context. Safe.
2. **Inventory viewer** — click any placed container → a panel of its contents (grid/slot layout, names/icons). Read-only. Also re-attach cargo on `.oplan` reopen (`RelocateContext`).
3. **Preserve-through-edits (write path)** — cargo travels through re-skin / replace / def-change (transfer `Cargo` to the new placement); delete warns from `Placement.Cargo`; inject **and export** read cargo from placements; nav auto-inject fills only empty consoles. **Fixes the loss.**
4. **Editor + authoring** — add/remove items; pre-load containers in exported ships; validate against grid size + `strContainerCT` filter ("the Law" extended to cargo); `.oplan` persists authored cargo.

Slots (`strSlotParentID`) are handled in the model from phase 1; slot-specific UI lands in phases 2/4.

## Risks / notes

- Phase 3 refactors the delicate, heavily-tested save-edit **inject** — do it behind the existing inject tests.
- Contained `fX/fY` are world-ish (≈ parent) — the existing move-cargo delta shift is correct; keep it.
- Authored cargo (phase 4) needs synthesized COs (like `SaveEdit.SynthesizeCo`) + capacity/filter validation.
- Reopened `.oplan` designs won't show cargo until phase 2 re-attaches it on relocate (phase 1 attaches on fresh import only).
