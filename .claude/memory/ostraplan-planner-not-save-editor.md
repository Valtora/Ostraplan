---
name: ostraplan-planner-not-save-editor
description: "Ostraplan is deliberately a ship planner, not a save editor; features that only edit save state are declined"
metadata: 
  node_type: memory
  type: project
  originSessionId: b5074ec8-c0eb-484e-896c-e9c09f215abe
  modified: 2026-08-03T09:30:56.685Z
---

Ostraplan stays a **ship planner**. Features that edit save state without involving a
design are out of scope, even when they are technically easy. Decided 2026-08-02 while
assessing [issue #17](https://github.com/Valtora/Ostraplan/issues/17).

Applied there as: **granting the current design into a save as a new owned ship is in
scope** (a design is the input); **apartment purchase/editing is declined** (a stock
station template, no design involved, and an open-ended maintenance burden); **verbatim
save-to-save ship copying is declined for now** (import-then-grant covers the use case,
losing only per-part damage and crew).

**Why:** the line keeps the tool's remit, its test surface, and its exposure to
undocumented save internals bounded. Save-state features pin Ostraplan to game internals
it otherwise never touches.

**How to apply:** when triaging a feature request, ask whether a *design* is an input to
it. If not, it is probably a save-editor feature and should be declined with reasons
rather than scoped down.

Re-applied 2026-08-03 closing [issue #12](https://github.com/Valtora/Ostraplan/issues/12)
(apartment editing) as not planned. Findings worth keeping, from the 0.16-era decompile:

- An apartment is an ordinary ship record spawned as a **hidden station**
  (`GUIShipBroker.OnPurchaseConfirm`: `SpawnShip(..., isStation: true)`, `HideFromSystem`,
  `LockToBO(station)`, `bIsBO = true`). Six stock templates, five station brokers, price
  `sum(aRooms.roomValue) * discount * 10`.
- RegID is `<STATION>|RES_<n>` and the pipe is **load-bearing**:
  `DataHandler.GetTransitConnections` truncates at it, and `TargetsWildCard` is
  `strTargetRegID.Contains("|")`. Wrong RegID = no transit route in or out.
- The parts issue #12 wanted to move (`ItmKioskTransit02/03b`, `ItmDockSys02Closed`,
  `ItmSink01Station`) have **no install/uninstall/dismantle recipe at all**, so there is no
  BOM, cost or socket rule to port. That, not the plumbing, is the real blocker.
- **The answer to give is "that's a mod".** A loose form plus an install/uninstall pair is
  a handful of JSON objects, and Ostraplan's palette picks it up for free because the
  catalogue is built from installables across game data + enabled mods.

Related: [[ostraplan-expose-tuning-as-user-controls]].
