# Ostraplan — Edit-your-live-ship-in-a-save: scope & design

**Status:** SCOPE LOCKED (2026-07-05) — investigation complete, all decisions settled (§1, §10); **no feature code written yet**, ready to start Phase 1 on the user's go-ahead.
This scopes the feature requested after P3: *import your active ship from a save, redesign it out-of-game, and write it back into your playthrough — keeping crew, cargo, position and ship identity* — writing to a **copy** of the save (never the original).

> This is a distinct, riskier feature from P3's **layout-only** import/export. Call it **P5 (Save Edit)**; P4 (QoL) stays next unless re-prioritised.

---

## 1. Signed-off intent (2026-07-05)

| Decision | Choice |
|---|---|
| Direction | **Full in-place edit** — scope it first, build after sign-off |
| Original save | **Always write to a separate copy**; never touch the original |
| Must deliver | **Preserve crew & cargo** (a pristine-hull reinject is not enough) |

## 2. The two discoveries that make it feasible

Both verified against the real "Charon" save (`ships/J-P3HF.json`: 2952 items, 2957 COs) and the decompile.

1. **Item ↔ live-state is 1:1 by `strID`.** Every one of the 2952 `aItems` has exactly one matching `aCOs` entry under the same `strID` (2952/2952); the 5 extra COs are the player crew + loot-spawners. An item's wear, power state, gas, inventory, door open/closed — all of it — lives in *that* CO. **Preserving an item = keeping its `aItems` entry *and* its `aCOs` entry, both keyed by the same `strID`.** Contained cargo and crew are trees (nesting depth ≤5) parented onto items/crew by `strID`.

2. **A new item needs no synthesized state.** `DataHandler.GetCondOwner` (≈L2293): when an item is spawned with a `strID` that **isn't** in the save's `dictCOSaves`, it falls through to building a **fresh default CO from the item's def** — identical to a freshly-built item. So a part added in Ostraplan only needs an `aItems` entry with a fresh `strID`; the game creates its live-state on load. **We never fabricate `aCOs` for new parts** — the single biggest risk (writing a wrong CO) disappears.

Also relied on (from P2): the game **recomputes `aRooms`/`aRating` on full load**, so those baked values need only be self-consistent, not perfect.

## 3. The model: structural diff by identity

Ostraplan edits **structural parts** (the things on the grid). Crew and cargo are **invisible passengers** attached to structural parts (or to crew) by `strID`; Ostraplan never shows or moves them — it only needs to *not lose* them.

Editing your ship, then, is a **diff of the structural layout against the original save record**, classified per part:

| Class | Detected by | On inject |
|---|---|---|
| **Kept** (unchanged) | same `OriginStrID`, same pose | original `aItems` entry **verbatim** (exact `fX/fY/fRotation`, `aCondOverrides`, `aGPMSettings`, `strID`) + its `aCOs` + any cargo subtree |
| **Moved / rotated** | same `OriginStrID`, new pose | original entry with updated `fX/fY/fRotation`; keep `strID` + `aCOs` + cargo (cargo `fX/fY` shifted by the same delta) |
| **New** (added in Ostraplan) | no `OriginStrID` | fresh `aItems` entry (fresh `strID`, def only) — **no** `aCOs`; the game defaults it |
| **Deleted** | `OriginStrID` present in the original, absent now | drop the `aItems` entry, its `aCOs`, **and its contained cargo subtree** — if that subtree holds cargo, warn + list + confirm before injecting (§10.1) |

Everything **not** structural — `shipCO`, `objSS` (world position/velocity), `aCrew`, the crew's COs, `aDocked`, economy/physics caches, `origin`, `strRegID`, `aZones`, `aLog`, `commData` — is preserved **verbatim** from the original record. Only `aItems`, `aCOs` (filtered), `aRooms`, `aRating`, `nCols`, `nRows`, `vShipPos`, `dimensions` are rewritten.

## 4. Editor changes — identity plumbing

The core new capability: a placement must remember **which original save item it is** (if any).

- `Placement` gains a nullable **`OriginStrID`** (the save item's `strID`) — null for parts the user added.
- A new **import-for-editing** path (distinct from P3's layout-only import) builds the document *and* retains a **`SaveShipContext`**: the full original ship record + maps `OriginStrID → (original aItems entry, its aCOs entry, its cargo subtree)`, plus the source save path + `RegID`.
- Identity is preserved by move/rotate/group-rotate/`SetPose`, and **dropped** by paint, box-fill, duplicate, copy/paste, symmetry-mirror, and P3 template/layout import (those make *new* parts). Delete removes the placement (its `OriginStrID` becomes a "deleted" in the diff).
- `.oplan` persists `OriginStrID` per part **and** a lightweight reference to the source save (`RegID` + save name), so a save-derived design can be reopened later and still injected. Missing-context reopen degrades to "export as new template" (P3), never a broken inject.

This threads through undo/redo (poses already round-trip), the command stack, and the canvas — moderate but well-contained surgery.

## 5. Inject pipeline (`Core/SaveEdit.cs`)

1. **Resolve context** — the `SaveShipContext` (from this session's import, or re-located from the `.oplan`'s `RegID` against the chosen save).
2. **Diff** structural placements vs the original by `OriginStrID` → kept / moved / new / deleted.
3. **Rebuild `aItems`**: verbatim kept, repositioned moved, fresh new, plus every surviving cargo subtree (parents that weren't deleted); drop deleted parts' subtrees.
4. **Rebuild `aCOs`**: keep every CO whose `strID` still exists after the diff (kept/moved parts, **all** non-structural COs — crew, loot-spawners — and surviving cargo); drop the rest. New parts contribute none.
5. **Recompute** `aRooms`/`aRating` (the P2 engine) + `nCols`/`nRows`/`vShipPos`/`dimensions`, in the **original ship's coordinate frame** (kept items keep world-absolute `fX/fY`; the grid may grow to contain new parts).
6. **Validate** (see §7). Abort with a clear report on any dangling reference.
7. **Confirm cargo loss** (§10.1): if the diff drops any cargo (a deleted container's contents), present the consolidated warning — the cargo listed per container, the advice to empty it in-game first to keep it, and an explicit confirm/abort. No write happens without confirmation.
8. **Write to a COPY**: duplicate the save folder → `"<name> (Ostraplan)"`; inside the copied zip, replace **only** `ships/<RegID>.json`; leave `saveInfo.json`, portraits, the player CO record, and every other ship untouched. Produce a **change report** (N kept / M moved / A added / D deleted / C cargo items dropped).

## 6. Coordinate & index care

- **`fX/fY` are world coordinates**, not tile indices — so growing the grid (adding a wing) does **not** disturb kept items. Kept items are written **verbatim** (no recompute → zero rounding drift on the untouched majority).
- New/moved items are mapped Ostraplan-doc-tile → world `fX/fY` in the **original `vShipPos` frame** (reuse `ShipGrid.TemplateTile`'s inverse, anchored at the original `vShipPos`, not export's `(0,0)`).
- **Grid-relative indices** (crew `nDestTile` = 1822, an index into `nCols·nRows`) shift if `nCols`/`nRows` change. The game re-derives most positioning from `fX/fY` on load, but this is the sharpest edge — validate and, if needed, recompute `nDestTile` from the crew's `fX/fY`.

## 7. Validation (before any write)

- Every surviving `aItems` `strID` is unique; every non-new item has a CO **or** is a valid default-CO case; every cargo `strParentID`/`strSlotParentID` resolves to a surviving item or CO.
- No structural part references a deleted parent; rooms recompute without exception; grid contains every part.
- The write is **transactional on a copy** — the original is never opened for writing.

## 8. Risks & mitigations

| Risk | Mitigation |
|---|---|
| **Corrupting a live save** | Copy-only (never the original); full validation; change report; user loads the copy to verify. The original is one click away. |
| Cargo in a **deleted** container | Warn + list the cargo + advise emptying it in-game first; require explicit confirmation before it's dropped (§10.1). |
| Crew/cargo standing on **deleted** floor | Crew reposition is the game's job on load; flag in the report. Deleting an occupied tile warns. |
| A **moved** container's cargo | Shift contained `fX/fY` by the same delta; unit-tested. |
| Grid **resize** vs `nDestTile`-style indices | Recompute from `fX/fY`; validate; keep the original grid size when only appending outside it if simpler. |
| Only verifiable **in-game** | Ships a checklist; the user loads the copy and reports. Same E2E discipline as the rest of Ostraplan. |
| Modded ship whose mods aren't loaded | P3 already skips+reports unresolved defs; inject refuses if the structural diff is incomplete. |

## 9. Phasing

- **Phase 1 — identity + context (no writes).** `Placement.OriginStrID`; import-for-editing retaining `SaveShipContext`; `.oplan` persistence; the diff engine (kept/moved/new/deleted) with unit tests against the real save (import → no-op diff = all "kept"; move one wall = one "moved"; add/delete = correct classes). Nothing is written to any save yet.
- **Phase 2 — inject to a copy.** The merge (§5), validation (§7), save-folder copy + zip rewrite, the change report, and the UI (an "Update ship in save…" action enabled only for save-derived documents). First real end-to-end.
- **Phase 3 — harden.** Moved-container cargo, crew position/`nDestTile`, delete semantics, the in-game E2E checklist, docs + memory. Public-safety pass on the copy/validate guarantees.

Each phase: tests green, `publish.ps1` smoke, its own commit; in-game verification is user-driven.

## 10. Decisions (settled 2026-07-05)

1. **Cargo in a deleted container** → **interactive warn + confirm.** At inject, if any deleted part (or its descendants) holds cargo, show a consolidated warning that **lists the cargo per container**, states that injecting will delete it, and advises going **in-game to remove the cargo first** if they want to keep it. Require explicit confirmation to proceed with dropping it; otherwise abort so they can empty it in-game and re-import.
2. **Edit scope for v1** → **full add / move / delete**, with the loud delete-cargo warning above; deleting an occupied/containered part is allowed but always surfaced.
3. **Which ship** → **only the player's active ship** (found via the character record's `strShip`). Other ships in the save are out of scope for v1.
4. **Re-inject after reopening a `.oplan`** → **persist the source-save `RegID` + save name**; inject re-locates the ship in the chosen save by `RegID`. A design whose source can't be re-located degrades to the P3 "export as new template" path, never a broken inject.

## 11. Definition of done

An imported ship, edited (add a room, move a wall), injected into a **copy** of the save, **loads in-game** with the edited structure, the **crew and cargo intact**, the ship at its **same position/identity**, and a rating matching Ostraplan's — the original save byte-for-byte untouched. Verified by the user loading the copy.
