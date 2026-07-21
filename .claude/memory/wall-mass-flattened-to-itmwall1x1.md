---
name: wall-mass-flattened-to-itmwall1x1
description: Branded walls/floors ARE distinct in current data via cooverlay strCondLoot deltas (not a refactor); Ostraplan ignored the loot and showed the flat base stat.
metadata: 
  node_type: memory
  type: project
  originSessionId: 267f64a1-9ef9-408b-a8fa-2dfd1c982856
---

**Branded walls/floors are genuinely distinct in the CURRENT game data — there was NO stat-flattening refactor.** Each branded metal wall (Testudo, Ryokka, Langdon-Phillips, Mobile Space Systems, Minsheng, Van Hummel, ...) is a **cooverlay** whose `strCOBase` is the shared condowner `ItmWall1x1` (24 kg) BUT whose `strCondLoot` applies per-brand **signed deltas** on top. The game's `COOverlay.Init` runs `Loot.ApplyCondLoot` (accumulate via `AddCondAmount`) on **every** spawn (build/template/loot), so a built wall's real stats = base + loot deltas. Verified in the decompile (`GetCondOwner` adds the `COOverlay` and calls `Init` -> `ApplyCondLoot`) and reproduced cond-for-cond from a real save.

Example `CNDOLWallMSSLFWhite` (MSS "Light Framework"): `-StatMass x4`, `StatBasePrice +65`, `-StatInstallProgressMax x150`, `IsMSS`, `IsWhite`, `-IsHiddenInv`. On `ItmWall1x1` (mass 24, price 21, install 600, IsHiddenInv 2): -> **mass 20, price 86, install 450, IsHiddenInv 1, +IsMSS/IsWhite** = EXACTLY the player-ship (`OKLG`) baked wall. Per-brand built mass (= wiki = HailePrime's in-game readings): MSS 20, Testudo 25, Van Hummel 27, Ryokka 28, Langdon-Phillips 48; Testudo Aero -10 -> 14; Caylon plastic +0. A cond a loot drives to <= 0 is REMOVED (`AddCondAmount` fCount<=0 cleanup; "zero == absent"), e.g. CAYL floor loot `-StatMass x13` on a 6.5 grate base -> no mass cond (a game data quirk to mirror, not store negative).

The B-PKC4 anomaly (a spawned ship showing MSS walls at flat 24 with `DEFAULT` and no brand conds) is a shallow/partial spawn that didn't run the overlay loot; the canonical player-built wall (OKLG) has the full deltas.

**The Ostraplan bug (fixed):** `Catalog.ResolveDef` resolved a cooverlay to its `strCOBase` condowner and read that def's `aStartingConds`, but **ignored `strCondLoot`**, so every branded wall/floor showed the flat base stat (all walls 24 kg). Fix (v0.43.0): `CoOverlayDef.CondLoot` now parsed; `Catalog.ApplyCondLoot` folds the loot's `aCOs` signed deltas (recursing `aLoots`, condition-type only, dropping <=0) onto the base `StartingCondValues`/`StartingCondNames`, replicating `COOverlay.Init`. Palette + all resolved parts now match the game. Tests: `CondLootOverlayTests`. Gotcha: `LootDef.CondName("-StatMass=...")` keeps the leading `-` (returns `-StatMass`) — strip it before keying, `CondAmount` handles the sign separately.

**Two wrong turns I made before landing this (for context):** (1) first claimed the walls were identical (24) and only differed in old saves; (2) then claimed a refactor flattened them and mass is a frozen per-instance save value, and shipped a save-instance-reading patch — both WRONG and reverted. The user's intuition ("in-game is the source of truth; find the assignment logic") was correct: the logic is the cooverlay cond-loot, always applied.

**How to apply:** for the economy overhaul, per-brand wall stats already exist (in `data/loot` `CNDOLWall*`), so retuning them = editing those loots (or the base `ItmWall1x1`), not authoring new condowners. See [[bump-version-on-change]].
