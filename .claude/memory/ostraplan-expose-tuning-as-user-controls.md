---
name: ostraplan-expose-tuning-as-user-controls
description: "In Ostraplan, expose tunable visual/feel parameters as persisted user controls, not hardcoded constants"
metadata: 
  node_type: memory
  type: feedback
  originSessionId: b8ffbb75-83e1-46cc-9cbf-ad81ef21a2a7
  modified: 2026-07-18T17:43:52.752Z
---

When a visual or behavioural parameter in Ostraplan is a "feel" knob (a display level, a brightness/threshold, a tuning gain), expose it as a user-controllable, persisted setting rather than baking it into a constant.

**Why:** During the Light Viz work the user twice turned a hardcoded default into a user control — first the "level of black" (unlit dimming), then the reveal gain (light brightness). They want to dial the feel themselves; my chosen constant is at best a default.

**How to apply:** Mirror the existing pattern — an `AppSettings` property (persisted, sensible default), a `Board.X` property on `ShipCanvas` that rebuilds the affected visual on change and is restored on startup, and a labelled slider + editable numeric box in the View menu (`MainWindow.LightSliderRow`, the View ▸ Light Viz submenu). Keep a good default so it works untouched.

**Limit (2026-07-18):** this applies to *feel* knobs, not to fidelity. When a feature's whole point became exactness (Light Viz v0.47's pixel-exact game port), the user explicitly chose to REMOVE the Brightness / Unlit black tuners — a game-exact output shouldn't be adjustable. Functional controls (the exterior-sun location + angle, which select real game data) stayed. See [[ostranauts-shader-extraction-toolchain]].
