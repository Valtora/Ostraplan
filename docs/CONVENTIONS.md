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

## An install is the game, not a folder shaped like it

`GameEnv.InstallProblem` is the single answer to "is this an Ostranauts install", and every caller goes
through it: auto-detection, the Steam library scan, the startup gate and the Settings picker. It asks for
the game's own exe (`GameEnv.GameExeName`) **and** `Ostranauts_Data\StreamingAssets\{data,images}`.

It used to ask only whether an `Ostranauts_Data` folder was there, and a **mod deploy target** is exactly
that folder and nothing else. On a machine with no game but with ModTools deploying into one, the skeleton
resolved as the install: `LoadOrder.Read` fell back to a core-only load, `LoadSource` skipped every data
folder that was not there without complaining, `Catalog.Build` returned an empty catalogue, and the planner
opened with an empty palette and said nothing. The suite had the same hole and was worse about it, reporting
87 failures where [DEVELOPMENT.md](DEVELOPMENT.md) promises an honest skip.

Two rules follow from that, and both are the reason the check is where it is:

- **Never re-derive "is this an install" at a call site.** A second opinion is how the two halves drifted
  apart in the first place: the Settings picker and `GameEnv.Locate` had separately-written checks, so a
  folder Settings accepted could still be refused at the next launch.
- **A resolved install is not a loaded one.** `MainWindow.LoadDataAsync` holds at `ShowInstallGate` until
  the catalogue actually carries parts, because no on-disk check can prove a half-downloaded copy has data
  in it. An empty palette is a failure state, never a start state.

The gate itself is a wall rather than a warning: there is no editing surface behind it, so its only exits
are a folder that passes, the game's Steam page, or closing the app. A skipped test is honest; an app that
opens with nothing in it is not.

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

## A fractional tile coordinate is ambiguous, so say which frame it is in

Integer tile coordinates are unambiguous. A **continuous** one is not, and the two halves of the app had picked
opposite answers without either saying so.

- **Corner frame.** An integer is a tile's top-left corner, so tile `(x, y)` covers `[x, x+1)` and its middle is
  `(x + 0.5, y + 0.5)`. The canvas uses this, because it is the inverse of its own screen transform
  (`CellRect` and `DocPointAt` are a matched pair), and `GridMath.MapPoint` already documents it for a footprint.
- **Centre frame.** An integer is a tile's *centre*. The damage solvers use this, because it is the frame the
  game's item transforms are in: a collider is centred on the item's position, which is why
  `MicrometeoroidStrike` builds one as `p.X + w/2 - 0.5`.

Both are right for what they describe and neither should move. What was missing was the conversion, so every
strike drawn on the canvas was resolved against a hull sitting half a tile up and to the left of the one on
screen. It shipped in 1.0 and the tests could not catch it: they are written in the solver's own frame, where they
were correct, and no test crossed the boundary.

**Cross between them through `TileFrame`, never through a bare `± 0.5`.** `TileFrame.CornerToCentre` on the way
in, `CentreToCorner` on the way out, and `CellOf` to floor a corner-frame point onto its tile. The names are the
point: an unexplained half-tile offset in the middle of a method reads like a rounding fudge and gets "cleaned up"
by the next person through. `SimulateWindow` is the working example, and it converts in exactly two places.

If you add anything that takes a position from the canvas and hands it to Core, decide which frame the Core side
is in and say so in its doc comment.

**A symmetry axis is a third unit again, and it is a type rather than an int.** `SymAxis` carries *twice* the
axis coordinate in the centre frame, so an even value is a column's middle and an odd one the seam between two
columns. The doubling is what lets an even-width design mirror about its own centre line, which a whole-tile axis
cannot name at all: a 20-wide hull has no middle column, so it was mirrored half a tile off itself with no way for
the designer to correct it ([issue #46](https://github.com/Valtora/Ostraplan/issues/46)).

It is a struct and not a pair of ints on purpose. Every one of those parameters used to be a tile index, and an
int holding halves passed to something expecting tiles compiles perfectly and mirrors the ship to the wrong place.
The type turns that into a build error, which is how the change to half units found all six call sites at once.
Cross to the canvas through `SymAxis.Corner` (which goes through `TileFrame` like everything else), and back
through `SymAxis.NearestTo`.

## A deck item is its footprint, and it has a condition layer of its own

A `LooseObject` stores one tile, and that tile is the **top-left of its rotated footprint** — not the item. 521
of the 888 loose items the game ships are bigger than 1x1 (`ItmAntenna01Loose` is 1x4), and the canvas has always
drawn them across the whole of it. Anything that asks "where is this item" and answers with `(o.X, o.Y)` is
therefore wrong for the majority of them, and wrong in a way rotation cannot fix: the anchor is the top-left
whichever way the item faces, which is exactly how it was reported ("loose items in multiple zones take the top
left corner, regardless of rotation").

**Ask `ShipDocument.LooseTiles`, never the anchor.** The tile index is footprint-keyed, `LooseAt` answers for any
tile the item covers, and `ItemManifest.TilesOf` returns the whole footprint — the same "in the zone when any of
its body is" rule placements have always had.

**`LoosePlacement.Check` governs the cursor and nothing else.** The game runs `Item.CheckFit` on the
*interactive* hand-drop only; `Ship.SpawnItems` places a template's deck cargo unchecked, and the shipped content
leans on that hard. Measured over all 221 core templates and their 3054 deck items:

| Rule | Whose | Core items it would refuse |
|---|---|---|
| `TILItemForbids` (`IsFixture` / `IsObstruction` / `IsItemTile`) over the footprint | the game's | **6** |
| Deck under every footprint tile | was Ostraplan's, now dropped | hundreds — `Station_MTRS_Nuked` strews 254 pieces of scrap over unfloored wreckage, `Station_Ground` lies regolith on an exterior |
| One item per tile | Ostraplan's | `Babak` writes 15 separate `ItmPillAntibiotic01` at one position, with no `aStack` |

So **a design that arrives is never judged**, exactly as `ProblemScan` exempts given/locked structure from the
placement law, and **there is no floor requirement** — the two homegrown rules were what fought the data, not the
ported one. One item per tile survives at the cursor (a pile the plan cannot draw is not a plan) and nowhere else.

Do not add a design-wide deck warning back without re-running that measurement. It was built once and taken out
again because it flagged 14 of the 221 ships the game ships.

**`LooseConds` is deliberately not `Conds`.** Rooms, airtightness, the rating and the placement law for structure
must not see what is lying on the floor, so the deck items' `IsItemTile` lives in its own `TileConds` and is read
only by the loose law, through `CheckFit`'s `overlay` parameter. The consequence is that the **reverse** direction
is not ported: an installed part whose forbid mask names `IsItemTile` would refuse to be built over a deck item in
game, and here it places. That is the right trade — a planner builds the ship before it dresses it — but it is a
choice, not an oversight.

**The index never deletes to keep the invariant true.** It is a list per tile, so an import brings in every object
it carries and each of them draws. It used to be `Dictionary<tile, LooseObject>` with `LooseObjects` reading its
`Values`, which meant importing `Babak` kept one of those fifteen pills and lost the other fourteen with no trace.

## All text on the plan reads upright

`ShipCanvas.OnRender` runs the whole pass under a `RotateTransform` of `ViewRot`, so **anything textual has to
counter-rotate about its own anchor** or it is upside down at 180 degrees and sideways at 90:

```csharp
var rotate = ViewRot != 0;
if (rotate) dc.PushTransform(new RotateTransform(-ViewRot, anchor.X, anchor.Y));
```

Room labels, connector badges and the origin marker did this from the start; the zone name did not, and a design
turned round showed its zones mirrored. `RenderSmokeTests.Every_label_on_the_plan_reads_upright_at_any_view_rotation`
now holds every glyph on the canvas to it at all four rotations, so a new label that forgets fails a test rather
than shipping.

Sprites are the opposite case and rotate with the view on purpose, as does a part's facing needle: those are
about the ship's orientation, not the reader's.

## Theming and control styles

The app themes its chrome with WPF's **Fluent `ThemeMode`**, set in `ThemeManager.Apply`
(`app.ThemeMode = Dark ? ThemeMode.Dark : ThemeMode.Light`), on top of the app's own
`DynamicResource` brushes (`AccentBg`, `AccentText`, `Ink`, `Dim`, `PanelBorder`, …),
which the same method repopulates per theme. The ship canvas does **not** follow the
theme; it follows the user's chosen backdrop, which is a separate setting (below).

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

**A wrapped `TabControl` reorders itself, and you cannot stop it.** WPF lays a
TabControl's headers out with a `TabPanel`, which moves the row holding the *selected*
header down against the content. With enough headers to wrap, the strip therefore
rearranges on every click and nothing stays where the user last saw it. `ItemsPanel`
does not help: Fluent's TabControl template hard-codes its `TabPanel` and ignores the
override entirely, which was confirmed by replacing it with a `StackPanel` and getting a
byte-identical render.

So **do not use a TabControl for anything whose headers wrap.** The palette's category
strip is a `WrapPanel` of `ToggleButton`s plus a `ContentControl`, which keeps every
category where it was put and gets the Fluent checked accent for free. `MainWindow`'s
`AddPaletteTab` / `SelectPaletteTab` are the working example, and `--palsmoke` renders
the strip at three selections so a regression is visible without launching the app.

**A colour chip is a `Border`, not a `Button`.** The same Fluent rule bites in a second
place: a swatch has to *be* its colour, and a `Button` carrying a local `Background` loses
it to the hover state exactly when the pointer is on it, so every swatch greys out as you
aim at one. `SettingsDialog.SwatchGrid` uses `Border` plus `MouseLeftButtonDown`, which has
no control template to fight.

## The canvas backdrop decides the plan's ink

The plan used to be drawn on one hardcoded near-black (`#14161A`), so every mark laid over
it could be white or near-white at a low alpha and be sure of reading. That assumption is
gone: `BackdropSettings` lets the user pick any colour, a checkerboard, or one of the
game's parallax locales, and a light one erases faint white ink completely.

So the marks that are **ink** come in two sets, and `Backdrop.IsLight` (WCAG relative
luminance, threshold 0.5) picks between them. `BackdropBrushes.For` returns the brush and
that flag together; `ShipCanvas.SetBackdrop` takes both.

Keep the set small. Something belongs in it only if it exists as **a faint scratch on the
ground**: the grid, the coarse grid, the origin axes, the origin marker, the hover ring.
Selection blue, the ghost pens, the hazard fills and the room labels all carry their own
colour at an alpha that reads on anything, and a room label draws on an opaque dark box of
its own, so none of them switches. Adding a new low-alpha white overlay means adding its
dark twin at the same time, or it vanishes on a white backdrop and nothing will tell you.

**The backdrop is app-wide, not per design.** It is about the person looking at the plan,
so a design shared with somebody else opens on *their* backdrop. It also reaches
`RenderSnapshot`, the plain PNG export. It deliberately does **not** reach the room-diagram
snapshot (a labelled schematic with its own contrast budget) or the mod's ship portrait art
(`PreviewBg`, black because it sits beside the game's own portraits in the same UI).

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

**And a wrapping `TextBlock` sets the width, not the other way round.** `TextWrapping.Wrap` does
not make a `TextBlock` narrow. It wraps to whatever width it is *given*, and in a `StackPanel`
measured at infinite width it is given none, so it reports its whole unwrapped length as its
desired width and every wrapping line of prose in the panel is a bid for the window's width. Under
`SizeToContent` the longest of those bids wins. One line of hint text was therefore deciding the
size of the inventory window: a 1×3 rack with two items in it opened at the 900px `MaxWidth` cap
and was reported as looking ridiculous, which it did.

Prose is never what a window is sized to. Either give it a `MaxWidth` of its own, or measure the
real content first with the prose out of the way and hold it to that.
`InventoryWindow.FitHintToContent` is the working example of the second, and is worth preferring
where the real content's width varies: it keeps the window sized to the thing it exists to show.

**The same omission has two different symptoms, so neither one tells you the rule.** A wrapping
`TextBlock` widens a `SizeToContent` window; a **non**-wrapping one in a window with a declared
width is cut off at the frame with no ellipsis and no scrollbar, and the reader never learns there
was more. Both shipped, a release apart: the inventory hint widened its window, and the
micrometeoroid strength note was clipped mid-word in a window 470px wide.

So the test is not "will this wrap badly", it is **whose length decides this string**. If the data
decides, it wraps. Every line whose text is built from a count, a name, a path, a money figure or a
list of bodies read out of game data belongs in that group however short today's example reads,
because today's example is not the long one. A heading, a fixed caption and a table cell in a sized
column do not, and a cell should take `TextTrimming` instead so a long value ends in an ellipsis
rather than being silently cut. Status-bar items are the one deliberate exception: the bar is one
line by construction and a wrapped item would push the strip open.

`ShipInfoUI`'s `SideValue` style and `WizardStep.Note` set it for every label that goes through
them, which is why the misses are always the hand-rolled `TextBlock` that skipped the helper.

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
