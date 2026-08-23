# Conventions

The rules a change has to follow that are not obvious from reading the code, each one
written down because getting it wrong has cost a release at least once. For building,
versioning and releasing see [DEVELOPMENT.md](DEVELOPMENT.md); for the boundary on *what*
may be built, see [SCOPE.md](SCOPE.md).

## Where logic lives

`Ostraplan.Core` holds everything that can be exercised without a window: the ported game
logic, the data parsers, export, and save I/O. `Ostraplan.App` is the WPF shell around it.

Put new logic in Core and let the app call into it. That is what keeps the bulk of the
suite game-free and fast, and it is the difference between a rule that can be regression
tested and one that can only be eyeballed.

## Per-document state belongs to the session, not the window

`MainWindow` holds **several designs at once**, one per document tab. Anything that belongs
to one design goes on `DocumentSession` (the document, its `CommandStack`, its `OplanMeta`,
its `ShipCanvas`, its report windows, its unsaved flags). Anything shared by every design
stays on the window: the catalogue, sprites, `GameEnv`, settings, the clipboard.

The window then exposes the active session's state through private properties named exactly
as the old fields were (`_doc`, `_stack`, `_meta`, `Board`, …). **Keep writing new code
against those**, the same as before tabs. A new per-document field is a new property on
`DocumentSession` plus a one-line shim, not a field on `MainWindow`.

Two rules that are easy to get wrong, both of which cost a design's work when they are:

- **A callback that outlives the method capturing it must capture the session, not the
  shim.** A report window's `Closed` handler, or anything after an `await`, runs whenever
  the user gets round to it, by which point the shim resolves to whatever tab is active
  then. `var session = _active;` at the top, and use it. `ShowRatingReport` and
  `AttachSavedCargoAsync` are the working examples.
- **Work that finishes against a background tab must still land on it.** An off-thread
  result belongs to the design that asked for it, so it goes on `session.Board` /
  `session.LastProblems` whichever tab is on screen. Only the *shared* chrome (the toolbar,
  the PROBLEMS list, the title) is guarded on `ReferenceEquals(session, _active)`.

## Theming and control styles

The app themes its chrome with WPF's **Fluent `ThemeMode`**, set in `ThemeManager.Apply`
(`app.ThemeMode = Dark ? ThemeMode.Dark : ThemeMode.Light`), on top of the app's own
`DynamicResource` brushes (`AccentBg`, `AccentText`, `Ink`, `Dim`, `PanelBorder`, …),
which the same method repopulates per theme. The ship canvas always stays dark; only the
surrounding chrome themes.

**Every custom `Button`/`ToggleButton` style must chain to Fluent.**

```xml
<Style x:Key="OverlayToggle" TargetType="ToggleButton"
       BasedOn="{StaticResource {x:Type ToggleButton}}">
```

Fluent supplies control chrome through *implicit* styles keyed by `{x:Type Button}`. An
explicit `Style TargetType="Button"` with **no `BasedOn`** breaks the control out of the
Fluent style and it falls back to the light Aero2 template: a light-grey button sitting
among dark Fluent ones. That exact bug shipped once, in the first cut of the view-overlay
toggles. Add only padding, margin and the like on top of the chain. The lookup resolves
because `ThemeManager.Apply` runs at `App.OnStartup`, before `MainWindow` is parsed.

**Never hard-set `Background`/`Foreground` to force an "active" look.** Not in XAML and
not through `SetResourceReference`/`ClearValue` in code. Fluent's VisualStateManager
hover and pressed states take precedence over a local value, so on mouse-over the
background is replaced while the light foreground stays, leaving light-on-light. For an
on/off affordance use a **`ToggleButton` and its native Fluent checked state**, driving
`IsChecked` from the source-of-truth flag: the checked accent is theme-aware and has
correct contrast in every state by construction.

Drive the canvas from the toggle's `Click` (user-initiated only) and set `IsChecked` from
a central sync method. Assigning `IsChecked` raises `Checked`/`Unchecked` but never
`Click`, so there is no feedback loop. `MainWindow.SyncViewToggles` and the
`OverlayToggle` style are the working example.

Accent and severity colours come from the `ThemeManager` brushes (`AccentBg`/`AccentText`
is the Ship Rating button look). Reference them with `DynamicResource` so a light/dark
switch re-resolves them.

## A window that sizes to its content must be bounded

`SizeToContent` has no ceiling of its own, and `UiScale` cannot supply one: it scales and
clamps `Width`/`Height`/`Min*`/`Max*` to the work area, but a dimension that sizes to content
has no declared size to clamp, and it only touches `MaxHeight` when one is already set. A
window that sizes to its content and shows anything list-shaped therefore grows until it runs
off the screen. The missing-mods warning names one bullet per unresolved part and one per mod
dependency, so a design leaning on forty mods produced a dialog taller than the monitor with
its OK button below the bottom edge; the Arrange-screen tray does the same on a console
carrying a lot of modules.

Where content is as long as the data makes it, cap the height and let it scroll:

- **`MaxHeight` on the window**, not on the panel, so `UiScale` scales it with the rest of the
  chrome and clamps it to the work area.
- **A `DockPanel`, not a `StackPanel`**, wherever something has to stay on screen. Dock the
  header `Top` and the buttons `Bottom`, then add the scrolling body last so it fills what is
  left. A `StackPanel` gives every child its desired height whatever the space it is arranged
  into, so the buttons go off the bottom instead of the body scrolling.
- **`VerticalScrollBarVisibility.Auto`**, so a message short enough to fit looks exactly as it
  did before.

`MessageDialog.BuildLayout` and `MissingPartsDialog` are the working examples. The `--dlgsmoke`
preview renders the capped case as `dlg-warning-scroll-dark.png`, which is how to eyeball it
without a design that actually names forty mods.

## Tunable parameters are user controls, not constants

When a visual or behavioural parameter is a **feel** knob (a display level, a brightness
or threshold, a tuning gain), expose it as a persisted user setting rather than baking in
a constant. A chosen constant is at best a good default, and the person using the tool is
better placed than the code to decide what looks right.

The established pattern is an `AppSettings` property with a sensible default, a `Board.X`
property on `ShipCanvas` that rebuilds the affected visual when it changes and is restored
on startup, and a labelled slider plus an editable numeric box in the View menu (see
`MainWindow.LightSliderRow` and the View ▸ Light Viz submenu). Keep the default good, so
it works untouched.

**The limit is fidelity.** This applies to feel, not to exactness. Where a feature's whole
point is matching the game, the tuners come out: Light Viz became a pixel-exact port of
the game's own shader in v0.47, and its Brightness and Unlit-black sliders were removed
because a game-exact output should not be adjustable. Functional controls that select real
game data, such as the exterior sun location and angle, stayed. If a slider can make the
output disagree with the game, it does not belong.

## Prose in the app and the docs

Objective and plain, in user-facing text, docs and commit messages alike. Comments earn
their place by explaining *why* a ported rule works the way it does, since the what is
usually already legible and the why lives in a decompile the reader does not have open.
