---
name: ostraplan-button-styling-fluent
description: "Ostraplan uses WPF Fluent ThemeMode; custom Button/ToggleButton styles MUST be BasedOn the Fluent implicit style, and never hard-set Background/Foreground for active state"
metadata: 
  node_type: memory
  type: feedback
  originSessionId: 0542b44c-39cb-40a5-825d-83ac3a1cc560
  modified: 2026-07-21T16:21:45.710Z
---

Ostraplan themes its chrome with WPF's **Fluent `ThemeMode`** (set in `ThemeManager.Apply`, `app.ThemeMode = Dark ? ThemeMode.Dark : ThemeMode.Light`), on top of the app's own DynamicResource brushes (`AccentBg`, `AccentText`, `Ink`, `Dim`, `PanelBorder`, etc., repopulated per theme in `ThemeManager.Apply`). The ship canvas always stays dark; only the surrounding chrome themes.

**Why:** Fluent supplies control chrome via *implicit* styles keyed by `{x:Type Button}` / `{x:Type ToggleButton}`. An explicit `Style TargetType="Button"` with **no `BasedOn`** breaks the control out of the Fluent style, so it falls back to the light Aero2 template — a light-gray button that does not match the app's other (dark, Fluent) toolbar buttons. This exact bug shipped once: the first cut of the view-overlay toggle buttons used a bare `Style TargetType="Button"` and rendered light-gray, mismatched.

Separately, hard-setting `Background`/`Foreground` (in XAML or via `SetResourceReference`/`ClearValue` in code) to force an "active" look **fights Fluent's VisualStateManager**: VSM hover/pressed states have higher precedence than a local value, so on mouse-over the background is overridden while the light foreground stays, giving poor/washed-out contrast (light-on-light).

**How to apply:**
- Any custom toolbar button style must chain to Fluent: `BasedOn="{StaticResource {x:Type Button}}"` (or `{x:Type ToggleButton}`). Add only padding/margin/etc. on top. Fluent's own styles are in app resources because `ThemeManager.Apply` runs at `App.OnStartup` before `MainWindow` parses, so the `{StaticResource {x:Type ...}}` resolves.
- For an on/off "active" affordance, use a **`ToggleButton` + its native Fluent checked state** (drive `IsChecked` from the source-of-truth flag). The checked accent is theme-aware with correct contrast in every state (normal/hover/pressed, light/dark) by construction. Do **not** re-color it by locally setting Background/Foreground.
- Drive the canvas from the ToggleButton's `Click` (user-only) and set `IsChecked` from a central sync method; assigning `IsChecked` raises Checked/Unchecked but never Click, so there is no feedback loop. See `MainWindow.SyncViewToggles` and the `OverlayToggle` style. Related: [[ostraplan-expose-tuning-as-user-controls]].
- Accent/severity colours come from the app brushes in `ThemeManager` (`AccentBg`/`AccentText` = the Ship Rating button look). Reference them via `DynamicResource` so a light/dark switch re-resolves them.
