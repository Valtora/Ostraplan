# Using Ostraplan

A practical walkthrough. For where the tool's remit starts and stops see
[SCOPE.md](SCOPE.md); for how the game works internally (and what Ostraplan ports) see
[GAME-INTERNALS.md](GAME-INTERNALS.md); for what shipped when see
[CHANGELOG.md](../CHANGELOG.md).

Press **F1** in-app at any time for the full keybinding table.

## Getting started

1. **Install the game.** Ostraplan reads its data and sprites from a local
   Ostranauts install — it finds a Steam install automatically. If yours is
   elsewhere, point it at the folder when asked (the choice is remembered).
2. **Install Ostraplan.** Download `Ostraplan-win-Setup.exe` from
   [Releases](https://github.com/Valtora/Ostraplan/releases) and run it. It installs
   for your user only (no admin, nothing outside your user profile), makes Start-Menu /
   Desktop shortcuts and an Add/Remove Programs entry, and opens the app. It isn't
   code-signed yet, so Windows SmartScreen may warn once — click **More info ▸ Run
   anyway**. Prefer not to install? Download `Ostraplan-win-Portable.zip`, unzip it
   anywhere and run `Ostraplan.exe`. Or build from source
   (`dotnet run --project src\Ostraplan.App`).
3. **Updates are automatic.** When a newer version is out, Ostraplan downloads it
   quietly in the background on launch and shows a **Restart to update to vX** button
   in the toolbar; it applies only when you click, so you never lose unsaved work.
   You can also check on demand from **Help ▾ ▸ Controls & keybinds** (the *Check for
   updates* button). The first launch after an update shows what it brought, covering
   every release you crossed if you have been away a while; **Help ▾ ▸ View Changelog**
   brings it back whenever you want it. Your settings and activity log live in
   `%APPDATA%\Ostraplan` and survive updates and uninstalls.
4. A new design opens with a single **Primary Airlock** at the origin. Every ship
   has exactly one; it's locked (you can't move or delete it), just like in-game.
5. **You can have several designs open at once.** New, Open and every Import start
   theirs in a tab of its own, so nothing you are working on is closed to make room.
   See [Working on more than one design](#working-on-more-than-one-design).

If the version banner warns that the game is newer than the version Ostraplan was
verified against, the validation may have drifted — the numbers are usually still
right, but treat a mismatch as "double-check in-game".

## The window

| Region | What's there |
|---|---|
| **Palette** (left) | Every buildable part, split into the game's eight tabs (HULL · HVAC · POWR · SENS · CTRL · FURN · APPS · MISC) plus **All**, an **ITEMS** tab for loose floor cargo, a **SPECIAL** tab for the structure the game places but never lets you build, and a **FAV/REC** tab at the front for the parts you pinned and the ones you just placed. Search by friendly or internal name. Modded parts show a small origin badge. |
| **Canvas** (centre) | The tile grid. Place, paint, select, pan and zoom here. A **tab strip** appears above it as soon as a second design is open, and disappears again when you are back to one. |
| **Inspector** (right) | The selected part's details, ship stats, the **Problems** list, and the **Law report**. |
| **Toolbar** (top) | Grouped **File · Edit · Design · Analyse**, then the view overlay toggles **Zones · Rooms · Power · Light · Walk · Wire** (each highlights in the accent colour while active) and the **View ▾** menu (fit, symmetry, Light Viz daylight, walk-overlay switches), with **⚙ Settings** and the **Help ▾** menu on the right. When a newer release exists it is downloaded in the background and a **Restart to update to vX** button appears in the toolbar; clicking it applies the update and reopens Ostraplan. |

### Settings

**⚙ Settings** (or **Ctrl+,**) holds everything that is Ostraplan's own preference rather
than part of a design. Changes apply as you make them.

| Setting | What it does |
|---|---|
| **Theme** | Follow Windows, or force light or dark. Chrome only: the canvas stays dark, because the game's sprites are pixel art drawn for dark space. |
| **UI scale** | 80% to 200%, scaling everything Ostraplan draws, right-click menus, dropdowns and tooltips included. Above 100% for a high-resolution monitor run at 100% Windows scaling, where the app's text would otherwise be tiny; below it to fit more into the window you have, on a laptop panel or beside a second copy of the app. It is a layout scale, not a magnifying glass, so text and vectors stay sharp. Dialogs and reports resize with it; the main window keeps the size you gave it, and below 100% spends the space it saves on the canvas. |
| **Mod overrides** | Let a modded part be placed where the core-game rules say it doesn't fit (see [The Law](#the-law--live-validation)). |
| **Ostranauts install** | Where the game's data and sprites are read from. Found through Steam automatically. Read once at launch, so a change takes effect next time you start Ostraplan. |
| **Saves** | Where your save games are. Ostraplan follows the game's own save-location setting, so set this only if your saves are somewhere neither the game nor Ostraplan knows about. Applies immediately. |

Both folders show what they resolved to and where that came from, and **Automatic** puts
either back.

## Placing parts

- **Arm a part:** click it in the palette. Your cursor becomes a ghost preview.
- **Place:** left-click. Keep clicking to place more; **Esc** disarms.
- **Rotate** the armed part: **R** (clockwise), **Shift+R** (counter-clockwise).
  Walls and floors don't rotate — they autotile to their neighbours instead. The angle
  **sticks when you arm another part**, so a run of consoles can all face the same way
  without re-rotating each one. Which way it faces is always on screen: the ghost draws a
  needle from its centre towards its leading edge, at every angle including 0°, and the
  status bar reads out the angle. Walls and floors get neither, since they don't turn.
- **Paint:** left-drag to place along a stroke (one undo step).
- **Box fill:** **Shift**-drag a rectangle. **Ctrl+Shift**-drag fills only the
  rectangle's border (a hollow room).
- **Symmetry:** **M** cycles off → vertical → horizontal → both; placements mirror
  live, positions *and* rotations. Rotating or moving a selection stays symmetric only
  when the selection is a genuine mirror set (its partners are selected too); an
  arbitrary selection, such as a fresh paste on one side of the axis, rotates about its
  own centre and moves rigidly instead.

### Parts you can't build — SPECIAL

The build tabs hold what the game will let a character install. The **SPECIAL** tab holds
the rest of the placeable structure: asteroid and ice cores, regolith walls, floor signs
and emblems, station kiosks, embassies, terminals and transit lifts, station floors and
furniture, and the running states of things like a reactor or a blast door. The game
places all of it and none of it has a build job, so short of copying it out of a ship
template there was previously no way to get one into a design.

They place, autotile, seal rooms and count towards the rating exactly like anything else,
and both export routes carry them: a mod spawns the ship whole, and a save write injects
them the same way it injects any part.

What they cannot do is be *built*. No install job means no install kit, so the bill of
materials counts them under **not buildable** alongside raw hull and the fixed airlock,
and no character could assemble the ship from parts. Most carry no `StatBasePrice`
either, so they add nothing to the save-edit cost estimate.

### Finding a part again — FAV/REC

The palette's first tab keeps the parts you actually use within one click of any tab.

- **Favorites** — click the ☆ on any palette row, or right-click a placed tile or loose
  item ▸ **Add to Favorites**, to pin it. The star fills gold.
- **Recent** — every part you place is recorded automatically, newest first (the last
  8). Favorited parts are left out of Recent, since they already have a home, and
  reappear there if you unpin them.

Both lists persist across sessions and honour the search box. Once you have pins, the
palette opens on this tab; a fresh install still lands on the full catalogue.

### The Law — live validation

Ostraplan runs the game's real placement check. **You cannot place anything the
game would refuse.** The ghost is **green** where it fits and **red** where it
doesn't, with the offending tiles highlighted and the reason in the status bar
(e.g. "needs a wall alongside", "needs floor beneath", "beyond the airlock face").

Moving or rotating an already-placed part into an illegal spot *is* allowed, but
it's flagged: the tiles hazard-tint and the part is listed in **Problems**,
grouped by reason. Imported (pre-existing) structure is exempt until you move it —
moving a part re-applies the law to it.

**Modded parts and the Law.** Ostraplan's rules are a port of the *base game's*
logic, so they're exact for vanilla parts but only best-effort for modded ones (a
mod can add its own conditions or even code). So a modded part flagged illegal is a
**yellow warning**, not a red error — "modded part may not fit; verify in-game." To
place a modded part where the rules say it doesn't fit, turn on **Mod overrides**
in **⚙ Settings**: the ghost turns **amber** (placing against the rules, flagged) and
the part lands, flagged in Problems. **Core parts are always enforced** — the toggle
only affects modded content.

**Overhead lights and power conduits.** Overhead ceiling lights are the one part the
game's *interactive* builder only lets a crew hang on a power conduit — but every
in-game ship drops them freely and powers them through the electrical network, so
Ostraplan lets you place them anywhere. A light with no conduit on its anchor tile
places with an **amber** ghost ("places, but no power conduit adjacent") and a
dismissible **Problems** warning pointing at that tile; run a **POWR** conduit onto
the adjoining tile (or rotate the light to face an existing one) and the flag clears.

## Selecting & editing

- **Select:** left-click a part. **Box-select:** drag over empty space. A box catches
  loose items lying on the deck as well as structure, and everything below (delete,
  move, rotate, flip, copy) then acts on both halves as one.
- **Flood-select:** **double-click** a 1×1 part to grab every connected tile of
  the same kind (a whole wall run, a floor). **Ctrl+double-click** adds to the
  selection.
- **Fill a compartment:** **double-click enclosed empty space** to highlight the
  whole sealed compartment, then arm a part and press **Enter** to fill it in one
  step (each tile is placed only where it actually fits; **Esc** cancels). Space that
  opens to vacuum can't be selected, so a fill never leaks out.
- **Use as brush (eyedropper):** **Alt+click** a part to arm it, at its own rotation,
  and keep painting it. **Replace with…:** **Ctrl+R** swaps the selection for a
  compatible part — one on the same layer and the same size on the deck, so a floor
  swaps for a floor and a 3×3 machine for a 3×3 machine. **Find and Replace All…**
  (right-click menu) does the same to every copy of that part in the ship at once.
- **Paint over the deck:** **Surfaces** mode (**T**) lets a wall/floor brush re-skin the
  tile it lands on rather than refusing it, and ghosts everything else out of the way.
  See [Surfaces mode](#surfaces-mode--painting-the-deck).
- **Move:** drag a selection. **Rotate a selection/group:** **R** / **Shift+R**.
- **Flip a selection:** **H** mirrors it left↔right (horizontal), **Shift+H** up↔down
  (vertical), about the selection's centre. Each part reflects its position and snaps
  its rotation to the nearest buildable orientation; walls and floors move but autotile
  rather than turn. (There's no "flipped" state in Ostranauts, so a single asymmetric
  part can't be truly mirrored — flip a *group* to mirror a whole room or subassembly.)
- **Narrow a box-select:** right-click inside it for **Select only ▸** one kind, or use
  the filter chips offered after a **Shift+drag** (which combine, where "Select only"
  picks one). Both list the render layers in the catch plus a **Loose items** row, so a
  drag over a room can keep just its walls, or just the clutter on its floor. Keeping only
  the loose items and pressing **Del** clears a deck without touching the ship. To clear
  every deck at once, use **Design ▸ Remove All Loose Items…**.
- **Ctrl+click** a loose item to add it to (or take it out of) the selection by hand, the
  same as Ctrl+click on a part. To reach the structure *under* a loose item, press **`**
  to step down the pile, use the right-click stacked picker, or turn on **Surfaces** mode,
  which puts clutter out of the way of clicks entirely.
- **Right-click** for the context menu: Duplicate (**Ctrl+D**), Copy
  (**Ctrl+C**) / Paste (**Ctrl+V**), Rotate, Flip Horizontal / Vertical, Delete
  (**Del**), **Use as brush** (**Alt+click**, the eyedropper — arm the part you clicked, at its
  rotation, and keep drawing), and **Replace with…** (**Ctrl+R**).
- **Reach what's underneath:** **`** steps the selection down the pile of things under
  the cursor, wrapping at the bottom; the right-click menu lists the whole pile too.
  **Move Back** / **Move Forward** (**Ctrl+[** / **Ctrl+]**) change which of them draws
  on top. See [what draws on top of what](#what-draws-on-top-of-what).
- **Undo / redo:** **Ctrl+Z** / **Ctrl+Y**, unbounded. Paint strokes and fills
  are single steps.

### Navigating

- **Pan:** **WASD**, **Space**+left-drag, or middle-drag.
- **Zoom:** mouse wheel (integer 0.125×–8×, crisp pixel art). Zoom right out to
  frame a whole station; zoom in to 8× for tile-precise work.
- **Rotate the view:** **Q** / **E**, in 90° steps, like the in-game camera. All
  input stays rotation-aware.

## Analysing the ship

- **Ship Rating** (Analyse): runs the full room / airtightness / certification /
  rating pass and opens the **law report** — uncertifiable rooms with reasons,
  air-leak tracing to the unsealed tile, and the six-slot rating broken down.
- **Propulsion**, in the same report: **RCS acceleration** in G, **RCS delta-v**,
  **torch acceleration** in G, and **reactant remaining** in hours. In game these
  appear on a nav console and nowhere else, so you cannot see them until the ship is
  built and flying; here they come off the plan.
  - Reaction mass counts only tanks sitting on an installed RCS **distributor's** gas
    inputs. A canister in a rack feeds nothing, which the report tells you rather than
    leaving you to work out. Any airtight tank counts, and every gas in it counts by
    mass, so an O2 tank is reaction mass too.
  - **More thrusters do not buy more delta-v.** The count cancels out of the game's
    own maths: delta-v is reaction mass over ship mass, and thrusters buy acceleration.
  - Torch thrust and burn time both scale with the reactor's pellet ceiling, so a laser
    array with no capacitor driving it, or a pellet feeder with no fuel regulator, adds
    nothing. The report names whichever side is capping you.
  - **Dead weight to haul** models a ship under tow or a hold of salvage: mass the layout
    itself does not carry. It divides into every figure exactly where the game puts a
    docked ship's mass, and it is saved with the design. It is **not** fuel — it adds no
    reaction mass, so raising it only makes the numbers worse. Stowed container cargo
    weighs nothing in game either, so put it here if you want it counted.
- **Diagnostics** (toolbar): the game's **own** ship checklist, off your plan. Sitting at
  a nav console in game, the Diagnostics module prints sixteen rows in green or red —
  transponder, transponder antenna, nav station, reactor and its helium-3 and deuterium,
  RCS thrusters, RCS distributor, reaction mass, backup power, and the four life-support
  rows (working O2 pumps, O2 stores, heat, cool), under the rating code and the ship's
  mass. Ostraplan answers all sixteen from the design, on the game's own pass/fail
  thresholds, so you find out you forgot the antenna before you build the ship rather than
  after. Every red row says what is missing and which build tab it comes from, and **Copy
  report** puts the lot on the clipboard.
  - Some thresholds surprise people, and they are the game's, not ours: **two** switched-on
    RCS clusters (one thruster can push but not turn), more than 100 kg of helium-3, more
    than 1000 kg of deuterium, at least 200 kg of reaction mass, at least 20 kWh of backup
    power, and more than 35 kg of O2 stores.
  - Two rows are measured somewhere specific rather than ship-wide. **Backup power** is
    read at the nav console's own power inputs, so a battery your conduits never reach
    counts for nothing (turn on PowerViz to see which runs are live). **O2 stores** are the
    oxygen in the canisters sitting on a pump's gas-input tile, so a hold full of O2 with
    no pump plumbed to a can reads zero — in game too.
  - Three rows read differently here than at a console, because a plan is not a running
    ship, and each says so in the report: **NAV STATION** is a real presence test (the
    console hardcodes ONLINE, since you are reading the page at it), **TRANSPONDER** shows
    INSTALLED where the console shows the registration ID the game assigns at spawn, and
    **REACTOR** shows INSTALLED where the console shows OFFLINE until the reactor is lit —
    which a planned one never is. Quantities are what the ship spawns holding, so this is
    the readout a freshly built or freshly bought ship gives.
  - It is the game's checklist, not the whole Law: rooms, airtightness, certification and
    the full propulsion figures are the Ship Rating report above.
- **Both reports stay out of your way.** Neither blocks the editor: leave a report open,
  carry on placing parts, and read it against the ship it describes. That is what makes the
  Ship Rating's **Show** buttons and its **Value Opportunities** list usable, since the room
  a hint is talking about is highlighted on a canvas you can still work on.
  - A report measures the design as it stood when it ran, so the moment you edit anything a
    bar appears across the top saying its figures describe the earlier ship. **Re-run**
    recomputes in place. Running the report again from the toolbar refreshes the window that
    is already open rather than stacking another one.
  - Opening or importing a different design closes an open report, since its figures and its
    dead-weight box belong to the design that produced them.
- **RoomViz overlay** — the **Rooms** toolbar button or **C**. The same
  certification, live on the canvas: every compartment tinted in its own colour and
  labelled with what it certifies as, its tile count and its value. An uncertified
  room also lists what it needs and, importantly, which item **in** it blocks the
  spec — a gas canister left in a quarters keeps the room Blank and costs you its
  value, and this is where you see that. Unsealed rooms are red; the exterior isn't
  tinted, so a compartment open to space simply loses its tint. Like PowerViz it only
  computes while it's on.
- **Light Viz overlay** — the **Light** toolbar button or **L**, **off by
  default** (the plan opens on the flat sprite view). The game's own lighting
  reproduced pixel-exact on the plan: real occluders (glass windows pass light, open
  doors spill it, beds and canisters cast shadows while staying lit), lit wall faces,
  normal-mapped relief, and soft light stacking. Press **L** to toggle it on, and
  again for the flat, fully-lit view. Under **View ▸ Light Viz** you can also switch on
  **exterior daylight** for a parallax location and sun angle, hull-occluded and
  streaming through glass. Manipulating parts with the overlay on stays smooth: the ship
  keeps its lit look while you drag, and the composite refreshes in place without a
  flash. It is a faithful preview, not a validation step (Ostranauts has no darkness
  gameplay).
- **WalkViz overlay** — the **Walk** toolbar button or **K**. Every tile crew can
  stand on, tinted by which connected area it belongs to. Two tiles sharing a colour
  are reachable from each other on foot; two colours mean there is no route, which is
  the fast way to catch a compartment you have walled yourself out of. On top of that:
  - **Fittings nobody can operate** are ringed in solid red **on the fitting itself**, so
    clicking one selects the part at fault. The game requires a crew member to reach a
    specific point on the device, within that interaction's own range and with line of
    sight, so a cooler boxed in by a bench is unusable however close you can get. The Law
    report lists them by kind with counts.
  - **Amber dashes mean "suit up", not "broken".** Kit mounted on the hull (lift rotors,
    external cargo pods) has no interior tile to work from, which is just how you reach
    it, so it is dashed rather than flagged. A doorway with vacuum on one side is dashed
    the same way: crossable, in a suit. (Pressure is read from the compartments either
    side, since a plan has no gas simulation.)
  - **Door state matters here**, unlike for rooms and the rating. A closed door only
    seals a section off if it is unpowered, locked or damaged; a powered one crew simply
    open, so it still joins both sides.
  - Under **View ▸ Walk overlay** you can **count spacewalks** (include routes over the
    hull, off by default so interior routes are what you see) and choose whether
    **Forbid zones** apply. Both settings are remembered.
- **Access overlay** — the **Access** toolbar button or **J**. Point at a fitting and it
  marks the tile a crew member would work it from, with a pair of feet, the way the game
  marks it on the deck. The plan on its own cannot tell you an arcade cabinet is usable
  from one side only, or which side that is, and the answer changes what you can put next
  to it.
  - **One tile, the nearest**, which is the one the game settles on. Painting every tile in
    range answers a question nobody asked and reads as a smear around the part.
  - **One part at a time**, because the question is asked about a particular thing.
    Selecting a part pins its mark so you can look elsewhere; with nothing selected it
    follows the cursor.
  - **Nothing marked means nothing can reach it**, which the Walk overlay is where to
    read about.
  - **Amber instead of blue** means it can only be reached from outside the hull. That is
    normal for hull-mounted kit, not a fault, exactly as it is in the Walk overlay.
  - It reads the same analysis the Walk overlay does, so the **View ▸ Walk overlay**
    switches apply to it too, and turning either one on computes it.
- **Problems** (inspector): live blocking/warning issues for placement and
  airlock-envelope. Each entry expands for the detail, and a **View** button pans and
  zooms the canvas straight to the offending tiles so it's easy to find on a big ship.
- **Materials…** (Analyse): the **bill of materials** — each part's install-kit
  count, for the whole ship or the current selection, with **Copy list**.
  - **Retrofit from…** nets the bill against a ship you already have, so it reads as
    what the *conversion* costs rather than what the design costs. The starting ship
    can be another **design**, a **ship template**, or a **ship in a save**; it is only
    read and measured, never imported, and your design is untouched. The list becomes a
    diff: `+N` kits to obtain, `−N` recovered, `=` for a part type that already matches,
    with each line's before → after counts beside it. Retrofit mode always compares the
    **whole** design, even if a selection is active.
  - **Recovered means recovered.** Uninstalling a part yields its own uninstalled form,
    which is the same kit the bill counts, so a part the design drops is material back
    rather than material spent.
  - **It prices material, not labour.** A part that only moves nets to zero: no kit
    changes hands, but you still pay the uninstall and re-install jobs. Non-buildable
    structure (raw hull, fixed systems, the primary airlock) is reported as a count on
    each side rather than as lines, since you cannot buy it either way.
- **Item Manifest…** (Design): every **item** the design carries, wherever it is — lying
  on a deck, inside a container, or nested any depth inside either. Grouped by item type
  the way a shop window is; click a row to open it onto the individual items, each with
  where it actually sits.
  - **Scope it to the whole ship or to one zone**, which is what a shop window does when
    it lists a counter. Any zone, not only a Haul or Barter one. A container counts as
    being in a zone when any part of its body is, and everything inside it comes with it.
  - **Arrange it by type or by location.** By type is the stock list: how many of these
    does the ship carry and what are they worth. **By location** keeps the ship's own
    organisation instead — zone, then the thing it is in, then whatever that is in, down
    to the items — with the totals rolled up at every level, so a hold's whole contents
    are one figure on its own row. Neither replaces the other, and both answer for exactly
    the same items, so the scope and the filter mean one thing whichever you are in. A
    design with no zones starts at the containers rather than under an empty heading.
  - **Three things per item.** **Show** selects it and centres the plan on it, so a stray
    on a deck you never look at is one click from found. **Rename** is the game's own
    rename, the same one the inspector and the container view use. **Delete** removes it,
    and a container takes its contents with it. Each of them is one undo step. In the
    location view these are on the right-click menu, since a tree is mostly structure and
    a column of buttons at every depth would bury it.
  - **Deleting does not ask.** It is one undo step and undo is the confirmation. You are
    still asked in the two cases where the row is not the whole story: a container that
    takes cargo down with it, and a host's own pocket, whose removal leaves the thing
    holding it with nowhere to keep anything.
  - **Right-click a type row to remove every one of them**, wherever on the ship they are,
    as a single undo step. Sixty-eight loose floor panels spread across a dozen containers
    are one action here and sixty-eight errands anywhere else. This one *does* ask, because
    the scale is the part that is not on screen.
  - **Type in the filter box** to narrow the list by an item's name, the name you gave it,
    or where it is. The figures at the top always describe the whole scope.
  - **It is not the bill of materials, and is not meant to be.** The bill counts install
    kits for structure you build, so an installed locker is priced there and what is
    inside it is here. A container *lying on a deck* is an item, so it is listed and so is
    everything in it; an installed one is not.
  - **A host's own pockets and pouches are listed**, and each row says "part of it".
    They are part of the design and they are written into the game — a garment that
    arrives with no pockets cannot hold anything — but nobody put them there, so a row
    that stayed quiet about it would read as a stray. They are still left out of the bill
    of materials and the edit cost, because you do not buy a coat's pockets separately.
  - **Value is the game's own base price**, not what a broker would pay for it. **Copy
    list** puts the whole thing on the clipboard as text, every row expanded.
  - **Adding items is still done where it was**: a container's own **View contents…**, and
    the **ITEMS** palette tab for the decks. The manifest is for seeing what is aboard and
    tidying it.
  - It stays open while you work, like the other reports, and re-walks the design whenever
    you change it.
- **Flight Dynamics…** (Design): what the design does **in air**. The game shows this
  only on a flying ship, in the nav console's own Flight Dynamics module, and only for
  wherever that ship happens to be. Here the place is an input.
  - **Pick a body and an altitude.** Venus, Earth, Mars, Titan and the four gas giants
    have authored atmospheres in the game's data; the report reads the local **gravity,
    pressure, density and temperature** straight out of them. All three figures the maths
    uses stay **editable**, so you can fly somewhere the game does not have.
  - **Set how it is flying**: airspeed (measured against the air, which moves with the
    body), angle of attack, and how far the nose sits off the horizontal. Lift dies at
    90° on either.
  - **Read**: lift, drag and rotor thrust in G, and whether the design **holds altitude**
    (everything anti-gravity over local gravity, with thrust pointed up). Below it, the
    rotor figures with and without turbo, the airspeed at which wings alone would carry
    it, and a warning when the game's own caps (lift at ten local gravities, drag at
    2000 m/s²) are what is limiting the answer.
  - **Mass hurts twice.** The game divides lift by mass in the coefficient and again to
    make an acceleration, so **doubling a design's mass quarters its lift**. That, not
    wing area, is usually what decides whether something flies.
  - **Aero hull** (`StatAeroLift`) is what makes lift at all, and it cuts **frontal**
    drag only, past a threshold: the divisor is `max(1, aero / 100)`, so the first
    hundred points buy nothing and broadside drag is never reduced.
  - **Rotors need air.** A heavy lift rotor gives its rated thrust at 100 kPa, nothing in
    vacuum, and half as much again in Venus's deep cloud layer.
- **Ship Re-skin…** (Design): swap every wall and/or floor to a different cooverlay
  skin, ship-wide, in one undo step. Sprites and names only — rooms, airtightness
  and rating are untouched. (Named "Re-skin" so it isn't confused with the app's
  light/dark theme.) To re-skin an *area* rather than the whole ship, see
  [Surfaces mode](#surfaces-mode--painting-the-deck).
- **Repair All…** (Design): swap every broken part on the ship — damaged walls, patched
  hull plates, wrecked devices — for its working form, ship-wide, in one undo step. See
  [Repairing damage](#repairing-damage).

## Simulate — what a hit would break, and wear you put there yourself

**Simulate ▸ Micrometeoroid Strike…** and **Simulate ▸ Weapon Impact…** fire something at the
design and mark every part they damage. Both open the same window on
a different tab. **Simulate ▸ Damage Brush…** is the other half of the menu and works the other
way round: it paints condition onto the design and keeps it.

**Drag a line across the plan** to aim. The line sets where the strike comes in and the heading
it travels on, and it carries on along that heading until it hits something or leaves the ship,
however short the drag. So how far you pull decides the angle and nothing else, and the aim past
the end of the drag is drawn faintly so a hit landing beyond it is not a surprise. Let go and it
fires. Drag another to fire again. Damage builds up across strikes and is
**never saved**: it lives beside the design, not in it, so closing the window or pressing
**Start over** puts the ship back to pristine. Your `.oplan` is untouched throughout.

- **Three marks, three different things**, keyed in the window itself so nothing has to be
  guessed at:
  - **Damaged** (blue, thin outline) — still the part you drew, with less left in it. Nothing
    about the ship has changed.
  - **Broken** (amber, hatched) — it filled its pool and a *different part* stands there now.
    This is the one that changes a design, and the window lists what each one turned into.
  - **Destroyed** (red, cross-hatched) — gone, the tile is empty.

  These are states rather than points on a scale, because that is how the game works: a part is
  replaced outright when its pool fills, not degraded smoothly. How far through its life it is
  rides along as the strength of the fill, which grades within a state without competing with it.
  The marks sit on a darkened patch so they read the same over any hull colour, and each state
  has its own hatching as well as its own colour, so none of it depends on telling two shades
  apart. The mark covers the object itself, not the clearance around it, so a dead LHe tank
  marks the tank rather than the deck it stands on.
- **It tells you what the hit did to the ship, not only to the parts.** After every strike the
  ordinary design checks are re-run against the hull as the strike left it, and anything they
  say now that they did not say before is reported: a compartment opened to vacuum, a device the
  crew can no longer reach. That is the difference between a reactor with a dent in it and a
  reactor running in a vacuum, which a count of damaged parts cannot express. It is still a
  measurement of one instant: what happens *next* (fire, venting, a reactor cooking off over
  time) is a simulation and is out of scope.
- **Nothing has to breach to get inside.** A strike is a damage budget spent along its line, and
  a wall it only cracks costs it that wall's damage and no more. The rest carries on into the
  compartment behind, which is why you will see interior damage under a hull that is still
  standing. That is the game's own rule, not an approximation here.
- **A wreck is a smaller target than the thing it used to be.** Breaking a part replaces it, so
  what a later strike meets is whatever is there now. An LHe tank presents three tiles of
  target while it stands and one once it is a heap of scrap, and a line that grazed the tank
  will pass straight over the scrap and reach whatever is behind it.
- **Two very different things can hit you**, and the game treats them nothing alike:
  - a **micrometeoroid** advances a part exactly one stage, so it cracks a wall and can never
    finish it in one strike;
  - a **weapon** prices the whole chain at once, so a missile takes that same wall from whole
    to gone in one go.

### Micrometeoroid

Draw the aim, set the **strike strength**, and it fires on release.

- **You can draw paths the game itself cannot fire, and that is deliberate.** In Ostranauts every
  micrometeoroid runs through one fixed point, marked with a crosshair on the plan. That is the
  game aiming at world origin rather than at the ship, and it means real strikes only ever arrive
  along lines through that marker. Drawing through it shows you what actually happens to this
  hull; drawing anywhere else answers the more useful design question, "what would a hit *here*
  cost me". A part no line through the marker reaches is one the game will never chip, so the
  marker is worth a glance before you trust a worrying result.
- **Where the marker sits depends on where the ship came from.** A design imported from a save
  or a template keeps its own, which for most ships is somewhere inside the hull. A design you
  authored here has none yet, so the window uses the one it will get when Ostraplan exports it,
  just outside the top-left corner. If you want to know whether the ship you are *flying* is
  vulnerable, import it from your save rather than measuring the copy you drew.
- **Strike strength is measured in damage**, because damage is what a hull meets, and the range
  is the one the game allows for a micrometeoroid. It is read out of your install rather than
  hard-coded: the bottom is the floor the game clamps to, and the top is the fastest strike any
  authored atmosphere band can deliver, so a mod that adds one moves it. It opens on **55**,
  which is what a micrometeoroid does at every spawn the game can reach away from a planet's
  atmosphere. **Type a figure** for an exact one, and **Reset** puts 55 back.

### Weapon impact

Pick the weapon — every attack your install and your mods declare, from 20 mm point-defence
fire up to the heaviest missile — then draw the path the same way.

- **Missiles detonate on the hull**, not in the middle: they trigger on the first tile along
  the line carrying one of the attack's own trigger conditions (for the missiles, a wall, a
  rigid object or a portal), and the blast falls off with distance from there.
- **A wall stops a missile whenever there is a wall there.** The game itself is fussier: it
  looks at the first part on a tile that still has anything left to give and then stops looking,
  so a wall sharing its tile with a floor stops a missile only when the ship's own item list
  happens to name the wall first. That makes the answer depend on how the file was written
  rather than on the design, which is no use to a planner, so Ostraplan asks about the tile.
  This is one of the few places the tool deliberately does not match the game.
- **You may still see a shot slip past a wall on a diagonal.** That one *is* the game's rule:
  a shot steps one tile at a time along its heading and rounds to the nearest cell, so a
  diagonal can cross a column and a row in the same step and miss the cell in between. Drawing
  a line straight along a row or a column samples every cell it crosses and avoids it.
- **Keep firing to see how deep a hull really is.** Anything with nothing left to give is
  passed over on the next shot, so the impact point walks inward as you go and the readout
  says where each one went off. Watching that number move in is how you answer "how many of
  these would it take to reach the middle". The tally counts only the shots that landed.
- **Point-defence fire cannot take a part from whole to gone in one burst.** Its whole spread
  is "soft edge", which caps a part it finds intact at its first broken form. The cap is on
  the part rather than on the burst, though, so a second burst on the same tile finishes what
  the first cracked. That is the game's rule, not a rounding here.
- **A miss is not the same as a shot that found nothing left.** "Missed" means nothing along
  the line you drew could set the weapon off. If it went off and still did nothing, the window
  says so and gives the tile, which means that part of the hull is already spent and the line
  needs redrawing rather than firing again.
- **Damage is the worst case.** In game every shot is jittered before it lands and fires are
  rolled per part; neither is reproduced, because a plan should tell you what a bad day looks
  like rather than sample one.

### Damage Brush — a ship that has been lived in

**Simulate ▸ Damage Brush…** is the one entry in this menu that *writes* to the design rather
than measuring it. Drag across the plan and everything the stroke crosses takes the condition
you set. Wear appears on the parts as you paint, exactly as the game will draw it.

**Shift+drag** boxes an area instead of a line, the same gesture as a box fill in the palette
or a zone: rubber-band a rectangle and everything inside it is painted when you let go. Nothing
changes while you are still sizing the box, because shrinking it could not take the wear back
off. Use it for a whole compartment, a deck, or a ship, and the freehand brush for the trail of
scuffing between them.

Set either **one condition** for everything the brush touches, or **a range** it rolls within,
per object, as it goes. The range is the one to reach for: a corridor painted at a flat 60%
reads as uniformly tired, where 25–70% reads as a place where some things have held up and
others have not. An area rolls exactly as the brush does, once per object, so boxing a room at
25–70% gives it the same varied look as dragging over every tile of it would have.

- **Nothing shows above 80% condition.** The game draws no wear at all until a part is below
  it, which is why its own second-hand ships look clean — they average about 88%. A range that
  stops at 85% will look like you did nothing, so aim well under.
- **Painting a part to nothing breaks it.** At 0% it becomes its damaged form — a cracked wall,
  a wrecked alarm — because a part in Ostranauts cannot sit at a full damage pool; it breaks.
  So a range like 0–40% breaks some of what it crosses and merely wears the rest, in one
  stroke, which is what gives a derelict its mix. The damaged form it leaves behind is a fresh
  one, and each object is rolled once per stroke however many of its tiles you cross, so
  dragging the length of a big tank breaks it once rather than a stage per tile.
- **A whole stroke is one undo step**, however many tiles it crossed, and a boxed area is one
  stroke however big it was.
- **Deck items take it too**, so a battered crate reads as part of the room. Untick **Include
  loose items** to paint the structure alone. A stack is worn as a stack.
- **Not everything can be worn.** Ship systems and parts with no damage pool of their own are
  left alone, exactly as the game leaves them, and the window says how many a stroke passed
  over.
- **To undo a painted part later**, select it and use **right-click ▸ Clear painted condition**.
  That is not the same as painting it to 100%: cleared means "whatever the export's wear
  setting decides", where 100% means pristine no matter what that setting says. The inspector
  shows a **Condition** row on anything you have painted, so its presence answers "did I paint
  this".

**It reaches the game.** A painted condition is part of the design: it goes into the `.oplan`,
into an exported mod, and into a save write-back, and it beats the whole-ship **Condition /
Wear** setting on all three — including "repair everything", because that is a statement about
the parts you did *not* speak for. This is the opposite of a strike, whose damage is never
stored.

## Saving & sharing

Ostraplan's native format is **`.oplan`** — a small, shareable JSON file. It
stores your parts (def, position, rotation), the mods the design depends on, and
document notes. It does **not** embed game assets. See
[OPLAN-FORMAT.md](OPLAN-FORMAT.md) for the exact shape.

**Sharing a modded design:** the `.oplan` records which mods it needs. If someone
opens it without those mods, Ostraplan names the missing parts and their mods and
holds the design **read-only** — a standing "MISSING MODS" warning appears. This is
deliberate: saving rewrites the design as it stands, so it would drop those parts
for good, and building over where they belong would break the ship in-game.

Two ways out, and it's your call which:

- **You still want the parts:** enable the mods and reopen, and they come back. Use
  [Ostrasort](https://github.com/Valtora/Ostrasort) to confirm they're subscribed,
  enabled, and in a working order.
- **You're done with those mods:** just **Save** and confirm. The parts are dropped,
  the warning clears, and the design carries on as a normal, complete one.

### Working on more than one design

Ostraplan holds as many designs open as you like, one per tab. **File ▸ New**
(**Ctrl+N**), **File ▸ Open** and every **Import** start theirs in a tab of its own, so
opening something never closes what you already have — there is no "save your changes
first?" prompt on the way in any more. The tab strip appears above the canvas the moment
there are two, and hides again when you are back to one.

| | |
|---|---|
| **Switch** | Click a tab, or **Ctrl+Tab** / **Ctrl+Shift+Tab** to step through them. |
| **New tab** | **Ctrl+N**, or the **+** at the end of the strip. |
| **Close** | The **✕** on the tab, **Ctrl+W**, or **File ▸ Close Design**. You're asked about unsaved changes then, not before. The last design can't be closed: Ostraplan always has one open. |

A tab is labelled with the file's name once the design has one, and with the design name
until then. A name too long for the tab is trimmed with an ellipsis, and an apartment
shows its designation alone: the game calls one "K-Leg: Port Azikiwe | Asteroid
Residence", and the tab says "Asteroid Residence". Hover a tab for the full name and the
file it is in.

Each tab is a design in full: its own undo history, its own view (zoom, pan,
orientation and overlays), its own zones and its own **Ship Rating**, **Diagnostics**
and **Flight Dynamics** reports, which close with it. An unsaved tab wears the same
**\*** in the strip as the title bar, and closing the window asks about each one in
turn, showing you the design it means.

**Copy and paste work between tabs.** Copy a selection in one design (**Ctrl+C**) and
paste it into another (**Ctrl+V**) — which is the quick way to carry a section, or a
set of renamed containers, from one version of a ship to the next.

**A paste lands under the cursor**, in the design you are pasting into, and that is the
whole rule — it does not matter which tab the selection came from. Pasting from the
right-click menu uses the tile you right-clicked. With the cursor off the canvas
entirely (over the palette, or another window) there is nothing to point at, so it goes
to the middle of the view, where you can see it.

### Auto-save

**File ▸ Auto-save** takes a rotating snapshot of the open design on a timer. It is
**off until you turn it on**, and the defaults are then **every 10 minutes**, keeping
**3 snapshots per design**. Both are adjustable in the same submenu (1 to 60 minutes,
1 to 20 kept).

Auto-save never writes your `.oplan`. **Ctrl+S is still the only thing that does.**
Snapshots go to `%APPDATA%\Ostraplan\autosave` and the unsaved-changes star stays up,
so an auto-save can't quietly commit an edit you were going to undo.

Each design keeps its own set, keyed on its file path, so two ships that happen to
both be called `Kestrel.oplan` in different folders never rotate each other out.
A design you have never saved has no path to key on, so it keys on the tab it is in
instead: two untitled sketches open side by side keep separate sets rather than
rotating each other away. Every open design is snapshotted, not just the one on
screen, so a tab you left in the background is covered too.

**Recovering:** **File ▸ Auto-save ▸ Recover auto-save…** lists every snapshot,
newest first. Recovering one loads it as *unsaved changes* to the design it came
from: nothing is written until you save, and saving goes back to that design's own
file. A snapshot of a design that had never been saved will ask you where to put it.
**Open auto-save folder** shows the files themselves, which is also how you clear out
snapshots of a design you have since deleted (rotation only prunes designs that are
still being snapshotted).

A tick is skipped, per design, when there is nothing to record or recording would be
wrong: no unsaved changes, a design held read-only because its mods are missing, or
an analysis reading the design at that moment. If a snapshot can't be written at all, Ostraplan
says so once and then only logs it, rather than interrupting you every interval.

### Snapshots

**PNG snapshot** exports the current design as an image for sharing. The **Ship
Rating** room map can also be saved as **SVG** (its "Save image…" dialog offers PNG or
SVG), so the room tints and labels stay crisp at any zoom. Both the plain snapshot and
the room map render in your current view orientation, so if you've rotated the plan
with **Q**/**E** the image matches; the room labels stay upright.

## Import & export

Everything below is under **File ▸ Import** / the **Export** button.

- **Import a template:** browse core and modded `data/ships` and start from an
  existing hull (a Vagabond, say). No in-game identity, wear or damage.
- **From a ship or apartment in a save (layout only):** pick a save, then pick
  **anything you own in it** — every vessel and every station apartment, in one list,
  each row tagged with which it is. The layout comes across as a pristine design.
  Nothing is written to the save, then or ever, which makes this the route for using a
  ship you own as a planning template.
- **Your ship, for editing:** the same ship with its in-game identity and per-part
  condition intact, so **Update Ship in Save…** can write the redesign back onto it.

**What comes in besides the structure.** The first two ask, and remember the answer:

| | |
|---|---|
| **Container contents** | Everything inside lockers, racks and crates, as viewable and editable cargo. Right-click a container and choose **View contents**. |
| **Items lying on the deck** | Tools, scrap and other loose objects on the floor. |

Both default to **on**. Crew are never imported.

- **"For editing" always brings both**, and doesn't ask, because the write-back emits
  each container's contents from what was imported: importing without cargo would delete
  that cargo from the save. Contents that arrive this way stay the *ship's*, so a
  write-back re-reads them from the ship it is about to write over and does not revert a
  locker you have rearranged in game since. Edit a container yourself and it becomes the
  design's, and nothing overwrites it after that.
- **On the other two routes the contents become the design's own** straight away.
- Either way they are stored in the `.oplan` and travel through **Export**.
- **Deck items are cargo, not structure.** A tool or a piece of scrap lying on the floor
  comes in as a loose object: it renders and travels with the ship but takes no part in
  the placement law or the bill of materials. (Before, these imported as buildable parts,
  so a shirt on the deck counted as ship structure.)
- The import report says what came in **and** what was left behind, so a ship that
  arrives without its cargo says so rather than leaving you to notice.
- **Export** opens a **wizard**. Step one asks where the design should go, and the rail
  down the left then shows only that destination's steps, so you never scroll past
  settings that belong to a path you didn't choose. Every destination shares one **The
  ship** step (name, in-game identity, condition), because all of them want exactly
  those.

Three destinations are offered. One that can't be used is shown **disabled with the
reason on it** rather than hidden; today that only happens when you have no save games
at all, which stops both save destinations.

**Choosing a save.** Every flow that asks for one shows the same list: the **character**
first, then the ship or station they're on, then a quieter line with **when the save was
written, how long it's been played, the game build, and the folder name**. The character
leads because several saves of one character docked at one station is the normal case,
and the metadata line is what actually tells those apart. A build shown as
`0.15.1.15 → 1.0.0.9` means the save was made on the first and last written by the
second, which is worth knowing about a save that won't open.

The last two steps are the same everywhere:

- **Review** runs the real engine and tells you what the export will actually produce:
  the part and room counts, the rating, any placement warnings, the price against your
  balance, and exactly where it will be written. Nothing has been written at this point.
  Anything the export would overwrite or delete appears as a checkbox you have to tick
  before the commit button arms, and so does any **blocking design problem** the PROBLEMS
  list is showing (a hull with no docking port, say). Those acknowledge rather than refuse,
  because a blocking problem is not equally fatal everywhere: a ship with no docking port
  is a broken purchase and a perfectly good derelict.
- **Done** reports what happened, in the wizard, with no box to dismiss first.

Your last-used destination, wear, kiosk choices, price and write target are remembered.
Reopening **Export** always starts you at **Destination**, and every step that still holds
is marked done, so the rail lights up and **Review** is a single click away: a repeat
export stays one click without the wizard opening one click from a write.

The rail is clickable. Any step you've already completed, and anything behind you, jumps
straight there on a click (they highlight as you hover). Steps ahead of an unfinished one
don't, so you can't skip past something that still needs an answer.

If something has changed since the last export — the save was deleted, the output folder
is gone — it opens on the step that can explain it, and the rail won't skip past it.

Remembered settings live in Ostraplan's own settings file, never in the `.oplan`, so a
design you share carries no folder paths, save names or credit amounts. The ship's name
and in-game identity do travel with the design.

### As a mod

Writes a spawnable local mod (`data/ships/<Name>.json` in
  the game's own shape, rooms and rating precomputed) to a folder, or staged into
  your `Mods/` folder. This is the way to get a **standalone, shareable ship** that
  doesn't depend on any save. Its steps are **Mod details**, **Obtainable in game** and
  **Where to write**, and between them they let you:
  - **Ship with its own picture.** Alongside the ship file the export writes
    `images/ships/<Name>/`, the folder the game looks in for a ship's portrait: one image
    of the whole ship plus a thumbnail per certified room, drawn from your design at the
    same size the game's own ship editor uses. This is not optional decoration. Character
    creation has no fallback picture, so a ship offered as a Shipbreaker start draws a red
    X without it, and the broker kiosk falls back to a plain silhouette. Re-exporting
    redraws the set and clears out images of rooms the design no longer has.
  - **Name it and give it flavour** — the in-game ship name (kept exactly as typed)
    plus make / model / year / designation / description. Leave the name blank and the game
    names the ship, a different name for each copy it spawns, exactly as it does for the ships
    it ships with. The design's own name is a file name, and it never becomes the ship's.
  - **Replace an existing ship** — pick any vanilla or modded ship and your design
    takes over its identity, so the game spawns yours in its place everywhere. Great
    for retrofitting: import a vanilla hull, rebuild it with your installed parts mods,
    and export it back over the original. Structure only (the original's cargo/crew
    loadout isn't carried over), and it affects new spawns, not ships already in a save.
    The **mod** is named separately from the ship (defaulting to
    "{replaced ship} - Replaced via Ostraplan" so you can tell it apart in the MODS
    screen) — rename it in the **Mod name** field to whatever you like.
  - **Make it obtainable in game** without hand-editing `loot.json`: add it to any
    **ship broker kiosk** (K-Leg / BCER / BCRS / Venus / VORB), pin it as a station's
    **Special Offer**, offer it as a **Shipbreaker starting ship** (a weighted
    chance in a fresh start — vanilla has no true ship picker), and/or scatter it through
    the **derelict fields** as a wreck to be found while salvaging. Other ship mods'
    entries in the same pools are preserved. **At least one of these is required**: without
    one the mod writes a ship file nothing in the game will ever spawn, so the wizard
    refuses rather than letting you find out in game. If a bare ship file is genuinely what
    you want — assembling a modpack, wiring `loot.json` yourself, referencing the ship from
    another mod — tick **No route: I'll wire it up myself** under **Advanced**. That section
    opens on its own when nothing is ticked and stays shut once the step has routes in it.
  - **Derelict fields** come in three size bands plus Venus. Two things are worth knowing.
    They are filled when a world is generated, so ticking one reaches a **new game only** —
    a save you already have will never grow one. And the game wrecks a derelict itself when
    it first loads, so an export aimed only at the fields turns the condition slider off for
    you rather than baking damage on top of damage; move the slider yourself and your
    choice stands. The bands overlap a lot in practice (Small runs 107 to 800 parts, Big
    starts at 520), so Ostraplan shows each band's real range and suggests the nearest
    rather than claiming your hull "is" a given size.
  - **Register with Ostrasort** in one click (when staging into `Mods/`): Ostraplan
    hands the mod to Ostrasort to register it (and patch any kiosk conflicts with
    other ship mods). **Ostraplan itself never writes `loading_order.json`** — that
    stays Ostrasort/ModTools' job. Untick it to register the mod yourself later.
  - **Wear** — spawn the ship worn rather than pristine. The slider picks the target
    **average** condition; it defaults to **~88%**, which is what the game's own kiosk
    ("Used") ships come at, and damage is spread randomly across parts (none below
    10%). Drag it left for a grungier ship, or to 100% (or untick) for pristine.
    (Wear lives on **The ship** step, because every destination bakes it the same way.)
    The roll is pinned when **Review** builds, so the ship you're told about is the ship
    that gets written, part for part.

### Into a save game

Adds the design to a save as a brand-new ship you already own, without replacing
anything that's already there. Use it to fly a design you've just drawn, or to move a
ship from one save to another.

Its one destination-specific step is **Save & price**:

1. Pick the **save game**. Ostraplan reads it and tells you where the ship will appear.
2. Choose where it **writes to**: a copy, or the original save in place. A copy is the
   default and leaves the original untouched. In place is for when you don't want a
   pile of copies as you iterate; it keeps a backup save unless you untick that too,
   and it asks you to confirm before it writes.
3. Optionally tick **Charge for the ship** and type a price. Your character's balance
   is shown live, and Next refuses with the reason if you can't afford it. Left
   unticked, the ship is a gift.
4. **Review** shows the registration the ship will be given, how far out it will be
   parked, and which save it lands in. Click **Add ship** to write it.

What you get:

- Writing to a copy: a new save folder, `<save> (Ostraplan)`, with **your original save
  never modified** — not even opened for writing. Writing in place: the save itself,
  and a backup save named `<save> (backup)` beside it in your Saves folder unless you
  turned that off. Either way, press **Refresh** in the game's Load menu if the save
  isn't listed.
- The ship parked **3 to 5 km** from wherever you are, undocked, exactly where the game
  itself puts a ship you've bought when the station has no free port. Take the
  **P.A.S.S. ferry** to board it (that range limit is 5,000 km, so it's comfortably
  inside). It isn't docked to the station on purpose: faking a dock means writing
  matching entries on both ships and a berth position the game derives from port
  geometry, which is a lot of ways to break a save for a short walk.
- The ship registered to you properly, so it shows in the broker's sell list, the ferry
  offers it, and your crew treat it as yours and will work on it.

Do this from the game's **Main Menu**, not while the save is loaded, or the game may
overwrite the write on its next autosave. That matters for either destination, and it
matters most in place: the game holds the whole save in memory and writes it back on
its own schedule.

A design you imported from a save can still be *added* to one; the dialog reminds you that
this creates a **separate new ship** rather than updating the one you imported. To change
that ship, use **Analyse ▸ "Update Ship in Save…"** instead.

## Transferring a ship between saves

**File ▸ "Transfer Ship to Another Save…"** (or **"Transfer Apartment to Another Save…"**)
moves a ship or an apartment from one playthrough into another in one action. It was always possible as two separate steps and almost nobody
found it, which is the only reason this exists.

1. Pick the **source** save and the ship in it. Ostraplan reads the ship in and puts it
   on the canvas, so you can look at what you're about to copy before it goes anywhere.
2. The export wizard opens on **Into a save game**, already selected. Pick the
   **destination** save on the **Save & price** step (it names the save the ship came
   out of, so a list of similar autosaves doesn't trip you up), then Review and write.

What makes the trip: **layout, cargo, loose items, zones, device wiring, the ship's
in-game identity, and each part's real condition**. The ship arrives worn exactly as it
was, part by part, rather than at a fresh average.

What does not:

- **Crew.** They belong to the save they're in, not to the ship. In practice they're
  usually not even aboard: crew are stored on whichever ship record they're physically
  standing on, so while you're docked they're all in the station's record.
- **The original.** This **copies** rather than moves. Both saves keep working, and
  neither original is modified: the ship is written into a *copy* of the destination
  save, and the source save is only read.

Because it's a copy, granting a ship back into the save it came from is legal, and is
how you clone a ship within one playthrough.

**Re-rolling the condition instead.** The **Condition / Wear** panel on **The ship**
step picks "Keep each part's condition from the source save" by default for a transfer.
The other two answers are a pristine ship or one worn to a chosen average — see
[The condition a ship arrives in](#the-condition-a-ship-arrives-in). Parts you drew in
after importing were never on the original, so they arrive undamaged whichever you pick.

## Editing your live in-game ship

**File ▸ Import ▸ "Your ship, for editing"** imports your live ship *with its
identity*, so you can redesign the structure out-of-game and write it back. Your station
apartment has its own entry, **"Your apartment, for editing"**, and works the same way:
see [Apartments](#apartments).

- Pick the ship, confirm, and redesign as normal.
- **Analyse ▸ "Update Ship in Save…"** opens the export wizard with the **Update a ship
  in a save** destination already selected. (**Export** reaches the same place; the menu
  item is the shortcut.) It writes the result back into a **copy** of the save by
  default: crew, cargo and world position preserved, the original untouched.
  Overwriting in place is an explicit opt-in and keeps a backup save unless
  you untick it. Do it from the game's **Main Menu**, not while the save is loaded, or
  the game will overwrite your edit on its next autosave. In the in-game Load menu,
  press **Refresh** to see the just-written copy.
- A design you imported from a save **this sitting** needs no save picker: Ostraplan
  still has the ship it came from and offers that one. Reopen the design another day and
  it is asked which ship it replaces, like any other, because the file names no save.
- **A design that never came from a save is asked which ship to replace.** Pick the save,
  pick the ship, and the design is written onto it. Nothing on that ship is recognised as
  already built, so every part currently on it is torn out and the design goes up in its
  place, while the crew, cargo, world position, registration and identity that make it
  *that ship* are all kept. Cargo carries over wherever the container holding it survives
  the swap; cargo in a container your design doesn't have is destroyed, and **Review**
  lists exactly what before anything is written. This is how you move a live ship onto a
  different hull — take the Edelweiss template, or any design you've drawn, and put your
  crew and cargo on it — without redrawing the layout by hand.
- The ship's **identity is editable** here, and it is written onto the ship. The import
  seeds it from the ship's own record, so **Ship Info** and **The ship** open on what the
  ship really is (make, model, year, designation, description) rather than on blanks.
  Change any of them and the write-back changes them in game. The one field that reads
  differently is the **in-game name**: leave it blank and the ship keeps the name it has,
  because a ship with no stored name gets a random one on every load. **Review** restates
  the identity and says whether it changed, so an accidental edit is visible before an
  in-place write.
- The **Write target & cost** step carries the cost model: **two multipliers over base
  value**, one for parts you added (default **2.0×**) and one for parts you **moved or
  un/installed** (default **1.0×**). Deleted parts are free, and authored cargo is priced
  like an added part. Pricing the two separately means a modular refit, or extending the
  nose of a ship, need not cost like a rebuild just because a lot of tiles shifted: drop
  the moved multiplier to **0×** and only the genuinely new parts are billed.
- The bill is shown as a **tally**: one row per kind of change with its base value,
  multiplier and figure in aligned columns, then a total. Under it, a **balance meter**
  shows what the edit takes out of your credits and how much is left. Both follow the
  sliders live. The meter turns red and tells you how far short you are once the cost
  passes your balance, which is exactly when Next refuses.
- **The deduction follows your character, not the ship.** Your credits sit on your
  character, and in a save that character is filed wherever they were standing when the game
  wrote it: on your ship while you're aboard, in the station's record while you're docked. So
  the cost comes off your balance whether or not you're on the ship you're editing, and
  whether or not you're on a ship at all. The option only greys out on a save with no
  readable character record to charge.
- **"Make Loose Item", "Install item", toggling a door and repairing a part count as moves,
  not purchases.** A part you already own that only changes *state* is priced on the moved
  multiplier, and the counts line names it separately (`… · 3 un/installed · …`).
  Uninstalling and re-installing the same part is free: it ends up exactly where it started.
  Replacing a part with a genuinely **different** part is still new material and prices as
  added.
- **Condition** is on **The ship** step, as it is for every destination. On a save edit you
  can keep the wear the ship already has, **repair everything** back to 100%, or re-roll it
  to a chosen average — the last two both act on every installed part, not just the ones you
  edited. See [The condition a ship arrives in](#the-condition-a-ship-arrives-in).
- Like every destination, it reopens at the start rather than on Review. Landing one click
  from rewriting a save you already have is a footgun, and this is the path where it would
  cost the most.
- **An `.oplan` is a design and nothing else.** It records no save and no ship. Delete
  the save it came from, move it to another machine, or hand it to somebody who has
  never seen your playthrough, and it still opens, edits, prices and exports. What a
  write-back needs from a save is read out of whichever ship you point it at when you
  run it.

### If your ship uses mods you don't have loaded

Ostraplan will say so on import, and it matters more than it sounds. It can't see
those items at all, but they're still in your save — so it works out your rooms and
the ship's grid *as if they weren't there*. A missing modded **wall** means a room
runs straight through it; a missing part at the hull edge throws the grid out. Write
back like that and you can get ghost rooms and shifted zones in game.

- **Best fix:** cancel, enable the mods (Ostrasort will confirm they're subscribed
  and enabled), and import again.
- **Otherwise:** pick a real part to stand in for each missing one. A stand-in
  **replaces** that item in the save you write back — the modded part isn't kept —
  so choose something the same size where you can. Delete a stand-in and you're back
  to leaving the modded item untouched.
- The wizard offers the same choice again on its **Missing parts** step, which only
  appears on the update destination and only while something is still unresolved. A
  stand-in applied there is a **real edit to the design**, not an export setting, so
  cancelling the wizard afterwards asks whether to keep it.
- Leaving them alone is allowed; **Review** carries an acknowledgement you have to tick
  before it will write.

Editing a ship you don't own (a station, another vessel) is gated behind a stern
warning — it's unsupported.

## Apartments

An apartment is a ship as far as Ostraplan is concerned: the same grid, the same
placement rules, the same rooms, airtightness, certification and overlays. Everything in
this guide applies to one unchanged, so this section is only about the ways it differs.

**Its own menu entries, everywhere.** A ship and an apartment are edited the same way but
they are not the same errand, so each has its own action and each lists only its own kind:

| Ships | Apartments |
|---|---|
| Import ▸ From ship template… | Import ▸ From apartment template… |
| Import ▸ Your ship, for editing… | Import ▸ Your apartment, for editing… |
| Transfer Ship to Another Save… | Transfer Apartment to Another Save… |

The one action that lists both together is **Import ▸ "From a ship or apartment in a save
(layout only)"**, which writes nothing to your save and so has no wrong row to land on.
Your apartments appear there beside your ships, tagged, which is the quickest way to use
one you already own as a starting point for another.

The apartment template list is the eleven residences a Real Estate broker sells, read from
the game's own broker data, so a mod that adds one shows up there too.

**Finding the one you own.** Ostranauts does not file an apartment with your ships. Buying
one registers it in a different place entirely, which is why your apartment never appeared
in the ship list and why it now has a list of its own. If you own no apartment yet, the
picker says so and tells you the save read fine.

**What Ostraplan stops showing.** A residence has no drive and no nav, so the Ship Rating,
the nav-console **Diagnostics** checklist, the propulsion figures and **Flight Dynamics**
do not apply and are hidden rather than reporting a design with no engine as a disaster.
The Ship Rating button becomes **Residence Report** and keeps everything that does apply:
rooms, certification, near-misses, airtightness and the snapshot. Kiosk prices go too,
because the game does not price a residence through the ship broker — a Real Estate broker
charges the summed room values ×10, and that figure appears on the export **Review**.

**Getting one into your game.** The same three save routes a ship has: write your edited
apartment back over the one you own, add a new one to a save, or move one between
playthroughs. Adding or transferring one asks **which station** it belongs at, on the
**Save & price** step. There is no mod export: the game sells a residence through a Real
Estate broker, which a ship mod cannot stock.

**The station list.** Alphabetical by name, and it holds only real stations: the game
builds an apartment's registration off a station proper, never off the residential module
hanging under it, so a name like "Azikiwe Estates Transfer Station" is not offered even
though that is the place you catch the lift from. Pick "K-Leg: Port Azikiwe" and the
transit kiosk at the transfer station will offer your apartment. The picker opens on the
station you are standing in, or failing that on one the game can actually reach.

**The station warning is worth reading.** An apartment is reached through its station's
transit kiosk, and a station only offers that route if the game's data defines one. Pick a
station that has none and Ostraplan says so twice, on the step and again on Review: the
apartment would be yours and completely unreachable. In vanilla, **Mercury Volanus** is the
one that sells apartments without a route to them.

**If Ostraplan guesses wrong.** The design's kind is shown in **Ship Info** and you can
change it there. It is set on import from the registration when there is one (conclusive)
and from the designation otherwise.

## Containers & cargo

Right-click a container — a locker, a nav console, a crate from a save-imported
ship — and choose **View contents…** to see its inventory laid out on the grid and
drill into nested containers. On an editable design you can also **add, remove and
rearrange** loose cargo; contents travel with the ship through **Export** and save
write-back.

**Removing.** **Del** takes one off the selected stack and **Shift+Del** takes the whole
stack, matching the right-click menu's two entries. The selection stays on the tile while
anything is still there, so emptying a stack of five is one click and five presses rather
than five clicks and five presses.

### What an item is, and naming it

**Alt+click any item** in the container view for its info panel, or use **right-click ▸
Info…**. It shows what the game's own object panel shows: the name, the description, the
factions the item belongs to, and its value.

- **The factions are the interesting part.** They come off the item itself rather than its
  def, so an item that arrived with an imported ship reads as whoever it came from — a pouch
  out of a Ceres station names that station. An item you added here belongs to none, and the
  panel says `n/a`, which is what the game says too.
- **RAW CONDITIONS** underneath is Ostraplan's own addition and labelled as such. The game
  hides these; they are here because they are what you actually want when you are editing a
  save and need to know what a def carries.
- The panel stays open while you browse, re-points when you Alt+click something else, and
  closes itself if the item is removed behind it.

**Any item can be named**, not just containers — a labelled round in a locker is as much a
part of a design as a labelled crate. Three ways in, all of them the same rename and all one
undo step:

- **Type over the name** at the top of the info panel, the way you rename a part in the
  inspector. Clear it, or type the stock name back, to put it back.
- **Right-click ▸ Rename…** on the item.
- **Click the title** at the top of the container view to name the container you are looking
  inside. At the root that is the part or deck item itself; drilled in, it is the nested
  container — which is what makes a crate of pouches labelable pouch by pouch.

The name is the game's own rename, so it goes into the game through **Export** and **Update
Ship in Save…**, and comes back when you import a ship carrying one. A stack is named as a
stack.

To see everything the ship carries at once rather than one container at a time, use
**Design ▸ Item Manifest…** (above). It lists every item wherever it is, nested containers
included, and each row can be shown on the grid, renamed or deleted.

### Moving things around the grid

- **Drag** an item to move it. It rides centred on the cursor, and the cell it will land in
  is drawn on the grid as you go: **green** when the drop is legal, **red** when it is not.
  A red drop snaps back, so nothing is committed by accident.
- **R** while dragging turns the item in hand, 90° at a time, and the ghost re-draws at its
  new footprint. Nothing is written until you let go, so position and rotation land as one
  undo step. This is how the game does it too.
- **R** with an item selected but nothing in hand turns it where it sits. It pivots about
  its own centre and slides to the nearest cell that takes the turned footprint, rather than
  refusing because the far edge is in the way.
- Walls and floors never rotate, in a container as on the ship grid. The game refuses to
  turn them at all, so a rotation authored for one would not survive a load.
- **Drop it into a container** to nest it inside, or **onto a name at the top** to move it
  out to a container further up. Either way it lands in the first cell that takes it. Those
  names only appear once you have drilled into something, so the hint offers that second
  drop only when there is somewhere above to drop onto.

An item is turned on its side when it no longer fits upright, both when you add one and when
you drop one into another container. That is what lets a 3×5 Polaris decoy launcher take
**five** 1×3 decoy missiles: three standing up across the columns, two lying flat in the band
left over. The quantity the **Add item** picker offers counts both orientations, so it stops
at the real capacity rather than at the upright count.

A **nav console** is a container too, and an important one: the console itself is only a
frame, and every screen on it is a separate module sitting inside. A console that comes in
with no modules — any ship from before 1.0, where consoles had no inventory at all, and
stock ship templates, whose modules are spawned by something Ostraplan doesn't import — is
fitted with the stock loadout at import, and the import summary says so. The data chip in
the console's own slot doesn't count as a module, and is kept. Open the console with **View
contents…** to see what it carries, pull a module you don't want, or add one you do (the
weapons and torch-drive modules included). A console that already has modules is left
alone, so a stripped salvage console stays stripped.

The set is the game's stock one, plus **course plot** and **flight dynamics** for the trips
that need them. Those two ride along without a place on the screen: a stock console's
thirteen modules tile it exactly, and neither of the extras fits the one gap left. They are
still aboard, and the arrange window below is where you decide what goes where. Everything
else appears exactly where the game puts it on a stock console, which Ostraplan writes into
the ship so it does not depend on what order the game happens to read the modules in.

### Arranging the console screen

Right-click a nav console and choose **Arrange screen…** for the planner's version of the
console's own edit menu in game. The board is the console screen; each module is a panel at
its place on it.

- **Drag a panel** to move it. A module keeps the size its def gives it, so only its corner
  moves, and it lands on the same two-decimal grid the game's own drag uses.
- **Drag it onto the tray** on the right to take it off the screen. It stays aboard the
  ship, exactly like a module you shelve in game, and you can put it back at the console any
  time.
- **Drag one out of the tray** to place it, or **double-click** it to drop it in the first
  free spot that takes it.
- A panel turns **red** while it would not fit: off the screen, or overlapping another. Two
  panels may share an edge, which is how the stock thirteen tile the board exactly. Dropping
  a red panel snaps it back to where it came from.
- **Reset to stock** puts the console back to the arrangement the game itself would produce.

The arrangement is part of the design: it is saved with the `.oplan`, undone with `Ctrl+Z`,
and written into the ship on export or a save write-back. A console you never arrange
carries no arrangement of its own, so it follows the stock layout. On a ship you are writing
back into a save, arranging a console overwrites whatever arrangement it had in game — that
being the point — while a console you leave alone keeps the layout you built at it.

## Filling canisters and tanks

Right-click a canister, an RTA or a fuel tank and choose **Fill…** to set how much of what
it carries. It changes what the ship is worth, how much reaction mass it has for the RCS,
and how long a torch drive can burn, so a design that flies on paper flies on the same
numbers in game.

The important thing to know is that **the gases share one budget**. That is how the game
works: a container's pressure is the total moles of everything in it at once, so oxygen and
nitrogen compete for the same space rather than each getting a share of the volume. Each
slider's own maximum is therefore "everything left, plus what this one already holds" —
drag one to the far right and the tank is full of that gas, pull it back and the others can
take the room. The gauge across the top shows the total against the container's pressure
rating, and the total can never be pushed past it. That is not a safety rail Ostraplan
invented: a canister over its rating takes damage every second in game and eventually bursts
into shrapnel, and the game's own "full" sits exactly on the rating.

Any of the ordinary canisters will hold any gas — an N2 can and an O2 can are the same
0.787 m³ shell rated to the same pressure, and the label is just what it shipped with. So
you can fill an RTA with whatever the ship actually needs. Eight gases are on offer: oxygen,
nitrogen, carbon dioxide, methane, carbon monoxide, ammonia, sulfuric acid and smoke. Water
vapour, hydrogen and helium are not, because the game has no condition for them and cannot
store them however much its code looks like it could.

**Fuel tanks are different, and are kept that way.** A deuterium, helium-3, cryogenic helium
or water tank is built around the one thing it carries, and the reactor matches its tanks by
name, so those tanks are offered **only their own payload** and no gas at all. Filling one
with oxygen would be weight the drive cannot use. Their payload has no pressure and no
shared budget, so it is simply capped at what a full tank carries.

**Empty** drains everything, **Reset to stock** puts the container back to what its def
ships with, and a container at stock carries nothing in the design at all. The fill is saved
with the `.oplan`, undone with `Ctrl+Z`, and written into the ship on export or a save
write-back. Importing a ship out of a save reads the real contents of every tank on it, so a
half-empty ship is priced and flown as a half-empty ship.

## Power

Two aids for wiring a ship's electrics, both driven by the game's own power model.

- **Connector badges.** A powered part shows labelled connector badges while you're
  placing it (and when it's selected): a lightning glyph plus **IN** (blue, where it
  draws power) or **OUT** (green, where a source feeds the network). They rotate with
  the part (staying upright), so you can turn a device to line its plug up with a
  conduit before you place it.
- **PowerViz overlay** — the **Power** toolbar button or **P**. It floods power from
  every installed generator and battery out along the conduit network: **live runs**
  animate a cyan flow, **orphaned runs** (conduit that reaches no live source) draw
  dim dashed red, and a **wired device with no feed** gets an **amber warning
  marker** on its plug. Turn it on to confirm at a glance that everything is hooked
  up; the toolbar tooltip says how many device plugs aren't connected.

This shows *connectivity* — what's wired to a live source — not a power budget:
Ostranauts doesn't publish per-device draw, so a generation-vs-load balance isn't
something Ostraplan can honestly compute.

### Wiring devices together

Signalable devices (a sensor and an alarm, a switch and a pump) can be wired the way
the in-game rewire tool does it. Turn on **Wire mode** (the **Wire** toolbar button), click a signalable
installed device to arm it as the signal **source**, then click another to **connect**
(or a connected one to **disconnect**). The source stays armed so you can wire it to
several targets; **Esc** or right-click cancels. Connectable devices ring violet, and
each link draws as a violet line from source to target. The wiring is part of Wire mode,
so it shows while that mode is on and is out of the way the rest of the time: a
thoroughly wired ship is not left criss-crossed with violet lines over every other view.
The connection is directional
(source drives target) and has no distance requirement, so the only rule is "two
distinct installed signalable parts". The wiring is baked into an **exported** ship, so
it spawns already connected. Gate and threshold logic stays with the in-game signal
box — Ostraplan authors plain connections only.

## Zones

Zones are the painted crew/trade areas the game lets you draw on a ship — **Haul**
(stockpile), **Barter**, **Forbid** (no-go), plus the content **trigger/spawn**
zones authored ships use for scripted encounters. Ostraplan draws them, lets you
manage them, and — importantly — **keeps them correct through import, export and
save write-back** (they used to be dropped on export and shifted onto the wrong
tiles on save-edit).

- **Show/hide** the overlay: the **Zones** toolbar button, or **Z**. Each zone is a
  translucent tint in its own colour with its name at the centre.
- **Add** a zone: **+ Add** in the **Zones** panel (right inspector). It's created
  and immediately *armed for painting*.
- **Paint** a zone: click it in the panel to make it active, then, on the canvas —
  **drag** to add tiles, **Ctrl**-drag to erase, **Shift**-drag a rectangle, or
  **double-click** inside walls to fill that whole room. Each stroke is one undo
  step. **Esc** stops painting.
- **Edit** (panel row): name, type (Haul/Barter/Forbid are independent checkboxes —
  a zone can be several, like the vanilla "cargo" zone), who it applies to, and
  colour. **Advanced** exposes the content-zone fields (encounter trigger, owner and
  target person-specs, category conditions) for station/quest authoring. **✕**
  deletes a zone.

Zones are saved in the `.oplan`, written into an **exported** ship's `aZones`, and
carried through **Update Ship in Save…** — re-projected onto the right tiles even
when the grid grows. Zones you don't author (a station's trigger zones on an
imported ship) are preserved untouched.

## Loose items & fixtures

Right-click a placed fixture — a sink, an appliance, a gas canister — and choose
**Make Loose Item** to uninstall it into its packaged (loose) form on the tile, or
**Install item** to do the reverse. Only parts the game can actually uninstall are
offered (raw hull, walls and the fixed airlock never are). The swap keeps the
tile, rotation and any contents, and is one undo step. A loose fixture no longer
certifies its room, and an item that ships full — a gas canister comes charged
with its gas — keeps that charge across the swap. Re-installing into a spot that
no longer fits isn't blocked, just flagged in **Problems** (like a move into an
illegal tile).

## Repairing damage

A ship imported out of a save arrives with everything that has happened to it. There are
**two** kinds of damage in Ostranauts, they are stored in completely different places, and
each has its own fix.

**Parts that are broken as parts.** A damaged wall, a patched hull plate, a wrecked alarm
is a different *part* in the game's data, not a healthy part with a number on it — so it
belongs to the design and travels in the `.oplan`. **Design ▸ Repair All…** swaps every one
of them for the working part, using the game's own repair jobs, so nothing is invented.

- It tells you how many it found, and of how many kinds, before it changes anything.
- Tile, rotation, custom name and contents all ride across. One undo step for the lot.
- To fix a section rather than the whole ship, select it, right-click and choose **Repair**.
  The entry only appears when something in the selection is actually broken.
- A **themed** wall or floor is repaired into the same theme (a damaged Testudo wall
  becomes an intact Testudo wall, not a generic one).
- **Repaired devices come back switched on**, the same as a device you build. The game's
  repair job hands back the *off* state; Ostraplan prefers the on one wherever it can name
  it, exactly as it does on install.
- On a save write-back a repair counts as a **move**, not a purchase — you already own the
  part (see [What an edit costs](#what-an-edit-costs)).

**Wear a part has accumulated.** This is the other kind, and it is *not* in the design: it
is a running total against each part's health pool, stored on the ship in your save. It is
what the ship's **Condition** rating averages. You clear it on the way in, from the
**Condition / Wear** panel — see below.

## The condition a ship arrives in

The **Condition / Wear** panel on the **The ship** step of the export wizard is one choice
with three answers:

| Choice | What it does |
|---|---|
| **Keep each part's condition** | The ship keeps the wear it has. Offered only where there is some to keep: updating a ship in a save (its own), or granting a design imported from one (the source ship's, matched part by part). Parts you drew in afterwards were never on the original and arrive undamaged. |
| **Full condition** | Every installed part at 100%. On an update this is **"Repair everything"** and actively clears the accumulated wear across the whole ship, bringing Condition back to **A**. On a ship being built fresh it is simply a pristine build. |
| **Worn** | Damaged to a target **average** condition (10%–100%). Parts spread randomly around it, none below 10%. **88%** is the game's own kiosk ("Used") wear. |

Both **Full condition** and **Worn** act on **every** installed part on the ship, not only
the parts you edited. Neither touches parts that are broken as parts — that is **Repair
All**, above.

## Switching devices on and off

Right-click a placed device for **Switch on** or **Switch off**. The game installs
powered fixtures in their *off* state and Ostraplan builds the *on* one wherever it can
name it, but some devices have an on-state Ostraplan couldn't reach — the **Transponder**
is the one people hit, because its on-state is a colour variant. Those were placed off with
no way to switch them on. Now they can be switched either way, in one undo step, keeping
tile, rotation and contents.

This is not cosmetic. Both the **Ship Rating** and **Diagnostics** ignore anything switched
off, so a transponder left off really does read as a fault, and switching your lift rotors
on is what makes them count in **Flight Dynamics**.

**Alarms only ever switch to their nominal state**, never to an alert one, so a design can't
be authored mid-emergency. That is safe to bake in even if it looks wrong for the ship:
every switched-on alarm carries the game's own sensor, which reads the real conditions each
tick and trips the alarm itself. An O2 alarm set nominal aboard a ship in vacuum goes red on
its own. (An alarm left *off* carries no sensor and stays off, which is what off means.)

## Naming a part or a deck item

**Type over the name in the inspector**, the way the game's own object panel works: select a
part and the name at the top of the **PART** block is a text field. It reads as a plain line
until you click it, takes the whole name so typing replaces it, and commits on **Enter** or
when you click away. **Escape** puts it back. **Right-click ▸ Rename…** does the same thing
through a dialog, for when the menu is where you already are.

A name is the game's own rename rather than an Ostraplan label, so a hold of identical racks
reads "spare tool storage" and "spare reactor parts" instead of five identical rows. It shows
in the inspector, in the right-click menu and on the contents window, it travels into the game
through **Export** and **Update Ship in Save…**, and it comes back when you import.

- **Import reads names too.** A ship you labelled in game keeps those labels, and so do
  stock ships that ship with them (the **Babak Refit** carries 51, "Pressurization SB"
  on an electrical box among them). These used to be dropped on the way in.
- **Clearing the field restores the stock name**, and so does typing the stock name back into
  it. There is no separate action.
- **Anything can be named**, the same as in game: airlocks, canisters and signs as much
  as racks and pumps, the primary airlock included. Names typed here are capped at
  64 characters; a name read off an imported ship is kept exactly as the game stored it.
- **Items lying on the deck are named the same way**, because the game renames a tool on
  the floor as readily as the rack it belongs in. Select one and type over the name, or
  **right-click ▸ Rename…**. That is how a Smart Crate reads "Electrical" for the wire and
  sensors that go in it, a SuperHandy is labelled with the ship section it belongs to, and
  a stack of ablative liner replacements says what it is there for. The name shows on the
  item's menu, on its contents window and in the stacked-tile list, and travels with it into
  the game exactly as a part's does. On a stack it belongs to the stack as a whole.
- A name survives a move, an uninstall and a switch on or off, since none of those change
  what the thing is called.

Placing *arbitrary* loose inventory — tools, food, consumables — is the separate
**ITEMS** palette tab: arm one and click to drop it onto a floor tile, or into a
container under the cursor if one accepts it. Right-click a placed loose item for
**Rename…**, **Change Quantity** (stackable items, up to the item's stack limit) and
**Delete**.
Loose cargo carries no structure, so it takes no part in the Law; it just renders and
travels with the ship through **Export** and save write-back.

Loose items select like anything else: a box-select catches them, **Ctrl+click** adds or
removes one, and the box-select filter (**Select only**, or the chips after a
**Shift+drag**) has a **Loose items** row for keeping or dropping the whole catch of them.
Once selected they move, rotate, flip, copy, duplicate and delete with the structure
around them, all in one undo step. Two things behave differently from structure, because
loose items are one per tile where parts stack freely:

- A transform that would land one deck item on a tile another already holds is refused for
  the deck items only — the structure still moves, and the status bar says what stayed.
  A **paste** or **duplicate** instead places the ones that fit and reports the rest.
- **Symmetry** doesn't apply to them. A selection holding any loose item is transformed as
  a plain group about its own centre rather than mirrored about the symmetry axes.

**Design ▸ Remove All Loose Items…** clears every item lying on the ship's decks in one
undo step — the after-the-fact version of the import dialog's **Items lying on the deck**
option, for when you decide once you can see the ship. Cargo inside containers is not
loose and is untouched.

### What draws on top of what

Several things can share a tile — a deck plate, the fixture on it, a canister feeding
that fixture, a jacket dropped on the floor — and the order they draw in is worked out
for you:

- **The part decides, not the ship.** Every part in Ostranauts carries its own place in
  the draw order, and Ostraplan reads it off the game's data. Deck plates and floor
  decals are at the bottom, then seats and chargers, canisters, alarms and vents, then
  walls, doors and racks, and bulkhead bins and power conduit on top. Two of the same
  part draw the same way everywhere on the ship, whatever order they were built in.
- **Yes, walls draw over most fixtures.** That is the game's own order, not a slip. It is
  also why a wall can hide something mounted on its tile: press **`** or use the
  right-click list to reach it.
- Where the game gives two parts the *same* place in the order it stops answering, and
  Ostraplan settles it: **canisters draw under what they feed** (a canister on an RCS
  regulator's input sits on the regulator's own row, so nothing else can separate them),
  **a small part inside a bigger one draws under it**, and loose deck clutter draws over
  installed parts, because that is what "lying on the floor" looks like.

When you disagree, say so: **Move Back** and **Move Forward** (right-click, or
**Ctrl+[** and **Ctrl+]**) step the selected part or loose item through the pile on its
tile, and **Reset order** puts that pile back to automatic. The choice is saved with the
design. A nudge only moves a part against the ones the game put at the same place in the
order as it — it will not push a fixture under a deck plate, and it will not put a rack
over the bin the game draws on top of it.

Anything drawn underneath is still one keystroke away: press **`** with the cursor over
a stacked tile to step the selection down the pile, wrapping at the bottom. The
right-click menu lists the whole pile as well, loose items included, with ● marking what
is selected.

## Surfaces mode — painting the deck

**Surfaces** (toolbar, or **T**) treats the walls and floors as a canvas you paint on,
for the detail work a ship-wide re-skin can't express: checkerboard tiling in a
bathroom, caution markings around a reactor or a door, an armoured run of wall along
one flank. It changes two things while it is on, and nothing at all while it is off.

- **Everything outside the focused layer is ghosted**, and steps out of the way of
  clicks, whichever button you use. The floor under a bed is one click away instead of a
  trip through the right-click layer picker, and right-clicking it opens the menu on *it*
  rather than on the bed, so **Rename…**, **View contents** and **Delete** act on the deck
  you can see rather than on the fixture standing over it. A box-select over the deck
  catches deck. Ghosted parts are still *there*, they just stop being the subject, and the
  right-click picker still lists the whole stack when one of them is what you were after.
  **SHOW** in the Surfaces bar picks the focus (**Both**, **Floors**, **Walls**; see
  [floors under walls](#floors-under-walls)), and **View ▸ Surfaces** sets how visible
  the ghosted layers stay (15% by default; drop it to 0 to hide them outright).
- **A 1×1 wall or floor brush re-skins what is already on the tile** instead of being
  refused for landing on it.

Every gesture you already know works, and now re-skins rather than refusing: **drag**
to paint a run, **Shift+drag** to box an area, **Ctrl at release** for the outline
only (which is how you get a border of caution floor around a compartment, or a line
of armoured wall). **Alt+click** picks a skin off the ship to paint with, and
**double-click** flood-selects a whole connected run of one skin if you would rather
select it and use **Replace with…**.

**R** turns the brush, and a re-skin lands the way the ghost shows it — which is how
you get an arrow decal pointing the right way under a door, or turn one you have
already laid without deleting it first. Autotiling skins (most walls and hull floors)
have no rotation of their own: they pick their sprite from their neighbours, so **R**
does nothing to them by design.

### What a stroke may do — Replace, Both, Fill

**PAINT** in the Surfaces bar decides what a stroke does to each tile, and it is
remembered between sessions.

- **Replace** (the default) only re-skins what is already there. Bare tiles are left
  bare, so a box or a checkerboard dragged across a room never spills new deck past
  its irregular edges. This is the skinning tool.
- **Both** re-skins where there is something and lays a new part where there isn't —
  one brush for everything, spills included.
- **Fill** only lays on bare tiles and never changes what is there.

The armed ghost tells you which way a tile will go before you click: green where the
stroke would land, red with the reason where the current mode declines it.

### Floors under walls

The game allows a floor and a wall on the same tile, in either build order, and the
ships it ships do it almost everywhere (the core 02 hull floors 335 of its 410 wall
tiles). Those floors matter even with the wall standing, because a floor's autotiling
reads its neighbours: whether the floor continues under a wall changes how the visible
floor beside it draws its edge. They also show during construction and wherever a wall
is later lost.

Ostraplan paints them like any other floor. On a wall tile with a floor beneath,
**Replace** re-skins that floor and leaves the wall alone; on a wall tile with no floor,
**Fill** or **Both** lays one under the wall. The one exception is flex flooring
(cargo webbing and the like), whose own socket mask forbids walls — the ghost refuses
it, because the game would.

To *see* and click what you are doing there, set **SHOW** to **Floors**, which ghosts
the wall layer along with everything else and puts the floor under the cursor within
reach. **Walls** does the reverse for reading a wall run against a dimmed deck, and
**Both** is the default.

Only 1×1 wall and floor skins paint. A stroke therefore runs straight past anything of
a different shape — a wide door keeps its own def while the wall either side of it
changes — and the primary airlock is never touched. Re-skinning changes sprites and
names only: rooms, airtightness, certification and the Ship Rating are unaffected,
exactly as with the ship-wide re-skin.

### Two-tone patterns

The **Surfaces bar** (top-left of the canvas, while the mode is on) holds two brushes.
**A** is whatever is armed from the palette. To set **B**, click the B slot and then
pick a second skin of the same kind — from the palette, or by **Alt+clicking a tile on
the plan**, which picks up whatever is already there. With both set, choose
**Checker**, **Rows** or **Columns** and every stroke alternates between them.

If the pick cannot pair with A (it is not a wall or floor skin, or it is the other
layer), it arms as an ordinary brush instead and the bar says so, rather than the click
appearing to do nothing.

The pattern is keyed to the ship's own tile grid rather than to where a stroke starts,
so two passes over neighbouring tiles continue one checkerboard instead of each
restarting it — and painting under active symmetry produces one continuous pattern
rather than a seam down the axis.

Light Viz switches off when Surfaces mode comes on: the lit view composites the whole
ship into one image, so there are no layers left in it to ghost.

## Theming

The **Theme** picker (top-right) switches the app chrome between System / Light /
Dark; the choice persists. The ship canvas always stays dark — the sprites are
drawn for dark space.

## Help & reporting a bug

- **F1** — the full keybinding table.
- **Help ▾** (top-right) — that reference, plus **View Changelog**, **Check for updates**
  (in the Controls & keybinds window), **Report a Bug** and the **activity log**.
- **View Changelog** shows this version's release notes, read from the changelog built
  into the copy you are running, so they describe your build rather than whatever is
  newest. **All releases on GitHub** in that window opens the published release.
- **Report a Bug** opens a pre-filled GitHub issue with diagnostics *and* writes a
  full diagnostics file (`%APPDATA%\Ostraplan\reports\Ostraplan-diagnostics-*.md`),
  revealing it in Explorer — **drag it into the issue to attach it.** The file holds
  your whole session's activity trail, any recent crash traces, and load warnings,
  all with your Windows account name and file paths scrubbed out.
- The **activity log** is an on-disk record of your actions (**View** / **Open folder**
  / **Clear**). Each entry now names *what* and *where* — e.g. `Edit: Place Nav Station
  @(12,7)` — so a problem can be pinned down after it happens.
