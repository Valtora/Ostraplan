---
name: ostraplan-planner-not-save-editor
description: "Ostraplan is deliberately a ship planner, not a save editor; features that only edit save state are declined"
metadata: 
  node_type: memory
  type: project
  originSessionId: b5074ec8-c0eb-484e-896c-e9c09f215abe
  modified: 2026-08-02T16:01:20.960Z
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

Related: [[ostraplan-expose-tuning-as-user-controls]].
