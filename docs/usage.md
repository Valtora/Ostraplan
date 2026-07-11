# Using Ostraplan

A practical walkthrough. For the design rationale and file format see
[SPEC.md](SPEC.md); for what shipped when see [CHANGELOG.md](../CHANGELOG.md).

Press **F1** in-app at any time for the full keybinding table.

## Getting started

1. **Install the game.** Ostraplan reads its data and sprites from a local
   Ostranauts install — it finds a Steam install automatically. If yours is
   elsewhere, point it at the folder when asked (the choice is remembered).
2. **Launch Ostraplan.** Download `Ostraplan.exe` from
   [Releases](https://github.com/Valtora/Ostraplan/releases) and run it, or build
   from source (`dotnet run --project src\Ostraplan.App`).
3. **Optional: install it.** The first time you run the downloaded exe, Ostraplan
   offers to copy itself to `%LOCALAPPDATA%\Programs\Ostraplan` and make Desktop /
   Start Menu shortcuts, so you have one fixed place to keep and launch it. It's
   entirely optional (pick **Not now** to skip), needs no admin rights, writes
   nothing outside your user profile, and you can re-run it any time from
   **Help ▾ ▸ Install Ostraplan / shortcuts**. Deleting that folder uninstalls it.
   Once installed, updating is one step: download the newer `Ostraplan.exe` and run
   it — it replaces the installed copy, refreshes your shortcuts, and restarts, so
   your existing shortcuts always open the latest version.
4. A new design opens with a single **Primary Airlock** at the origin. Every ship
   has exactly one; it's locked (you can't move or delete it), just like in-game.

If the version banner warns that the game is newer than the version Ostraplan was
verified against, the validation may have drifted — the numbers are usually still
right, but treat a mismatch as "double-check in-game".

## The window

| Region | What's there |
|---|---|
| **Palette** (left) | Every buildable part, split into the game's eight tabs (HULL · HVAC · POWR · SENS · CTRL · FURN · APPS · MISC) plus **All**. Search by friendly or internal name. Modded parts show a small origin badge. |
| **Canvas** (centre) | The tile grid. Place, paint, select, pan and zoom here. |
| **Inspector** (right) | The selected part's details, ship stats, the **Problems** list, and the **Law report**. |
| **Toolbar** (top) | Grouped **File · Edit · Design · Analyse · View**, with the theme picker and the **Help ▾** menu on the right. When a newer release exists, an update dialog appears on launch (**Download Latest Version** / **Not Now**) and an **Update** button stays in the toolbar. Running the downloaded exe then replaces the installed copy and refreshes your shortcuts automatically. |

## Placing parts

- **Arm a part:** click it in the palette. Your cursor becomes a ghost preview.
- **Place:** left-click. Keep clicking to place more; **Esc** disarms.
- **Rotate** the armed part: **R** (clockwise), **Shift+R** (counter-clockwise).
  Walls and floors don't rotate — they autotile to their neighbours instead.
- **Paint:** left-drag to place along a stroke (one undo step).
- **Box fill:** **Shift**-drag a rectangle. **Ctrl+Shift**-drag fills only the
  rectangle's border (a hollow room).
- **Symmetry:** **M** cycles off → vertical → horizontal → both; placements mirror
  live, positions *and* rotations.

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
in the toolbar: the ghost turns **amber** (placing against the rules, flagged) and
the part lands, flagged in Problems. **Core parts are always enforced** — the toggle
only affects modded content.

## Selecting & editing

- **Select:** left-click a part. **Box-select:** drag over empty space.
- **Flood-select:** **double-click** a 1×1 part to grab every connected tile of
  the same kind (a whole wall run, a floor). **Ctrl+double-click** adds to the
  selection.
- **Fill a compartment:** **double-click enclosed empty space** to highlight the
  whole sealed compartment, then arm a part and press **Enter** to fill it in one
  step (each tile is placed only where it actually fits; **Esc** cancels). Space that
  opens to vacuum can't be selected, so a fill never leaks out.
- **Use as brush (eyedropper):** **Alt+click** a part to arm it and keep painting
  it. **Replace with…:** **Ctrl+R** swaps the selection for a compatible part.
- **Move:** drag a selection. **Rotate a selection/group:** **R** / **Shift+R**.
- **Flip a selection:** **H** mirrors it left↔right (horizontal), **Shift+H** up↔down
  (vertical), about the selection's centre. Each part reflects its position and snaps
  its rotation to the nearest buildable orientation; walls and floors move but autotile
  rather than turn. (There's no "flipped" state in Ostranauts, so a single asymmetric
  part can't be truly mirrored — flip a *group* to mirror a whole room or subassembly.)
- **Right-click** for the context menu: Duplicate (**Ctrl+D**), Copy
  (**Ctrl+C**) / Paste (**Ctrl+V**), Rotate, Flip Horizontal / Vertical, Delete
  (**Del**), **Use as brush** (**Alt+click**, the eyedropper — arm the part you clicked and keep
  drawing), and **Replace with…** (**Ctrl+R**).
- **Undo / redo:** **Ctrl+Z** / **Ctrl+Y**, unbounded. Paint strokes and fills
  are single steps.

### Navigating

- **Pan:** **WASD**, **Space**+left-drag, or middle-drag.
- **Zoom:** mouse wheel (integer 1×–8×, crisp pixel art).
- **Rotate the view:** **Q** / **E**, in 90° steps, like the in-game camera. All
  input stays rotation-aware.

## Analysing the ship

- **Ship Rating** (Analyse): runs the full room / airtightness / certification /
  rating pass and opens the **law report** — uncertifiable rooms with reasons,
  air-leak tracing to the unsealed tile, and the six-slot rating broken down.
- **Problems** (inspector): live blocking/warning issues for placement and
  airlock-envelope. Each entry expands for the detail, and a **View** button pans and
  zooms the canvas straight to the offending tiles so it's easy to find on a big ship.
- **Materials…** (Analyse): the **bill of materials** — each part's install-kit
  count, for the whole ship or the current selection, with **Copy list**.
- **Ship Re-skin…** (Design): swap every wall and/or floor to a different cooverlay
  skin, ship-wide, in one undo step. Sprites and names only — rooms, airtightness
  and rating are untouched. (Named "Re-skin" so it isn't confused with the app's
  light/dark theme.)

## Saving & sharing

Ostraplan's native format is **`.oplan`** — a small, shareable JSON file. It
stores your parts (def, position, rotation), the mods the design depends on, and
document notes. It does **not** embed game assets. See
[SPEC §8.1](SPEC.md) for the exact shape.

**Sharing a modded design:** the `.oplan` records which mods it needs. If someone
opens it without those mods, Ostraplan names the missing parts and their mods and
holds the design **read-only** — a standing "MISSING MODS" warning appears and
saving is blocked — until the mods are enabled. This is deliberate: it stops the
missing parts being silently dropped, and stops you building over where they
belong (which would break the ship in-game). Use
[Ostrasort](https://github.com/Valtora/Ostrasort) to confirm the required mods are
subscribed, enabled, and in a working order, then reopen.

## Import & export

Everything below is under **File ▸ Import** / the **Export** button.

- **Import a template:** browse core and modded `data/ships` and start from an
  existing hull (a Vagabond, say). Layout only — cargo and crew aren't read.
- **Import your ship from a save:** pulls your player ship's layout out of a save
  game. Layout only, behind a confirmation.
- **Export as a mod:** writes a spawnable local mod (`data/ships/<Name>.json` in
  the game's own shape, rooms and rating precomputed) to a folder, or staged into
  your `Mods/` folder. This is the way to get a **standalone, shareable ship** that
  doesn't depend on any save. The dialog also lets you:
  - **Name it and give it flavour** — the in-game ship name (kept exactly as typed)
    plus make / model / year / designation / description.
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
    **Special Offer**, and/or offer it as a **Shipbreaker starting ship** (a weighted
    chance in a fresh start — vanilla has no true ship picker). Other ship mods'
    entries in the same kiosk are preserved.
  - **Register with Ostrasort** in one click (when staging into `Mods/`): Ostraplan
    hands the mod to Ostrasort to register it (and patch any kiosk conflicts with
    other ship mods). **Ostraplan itself never writes `loading_order.json`** — that
    stays Ostrasort/ModTools' job. Untick it to register the mod yourself later.
- **PNG snapshot:** export the current design as an image for sharing. The **Ship
  Rating** room map can also be saved as **SVG** (its "Save image…" dialog offers PNG
  or SVG): the room tints and labels stay crisp at any zoom.

## Editing your live in-game ship

**File ▸ Import ▸ "Your ship, for editing"** imports your live ship *with its
identity*, so you can redesign the structure out-of-game and write it back.

- Pick the ship, confirm, and redesign as normal.
- **Analyse ▸ "Update Ship in Save…"** writes the result back into a **copy** of
  the save by default (crew, cargo, world position and ship identity preserved,
  the original untouched). Overwriting in place is an explicit opt-in and keeps a
  `.zip.bak`. Do it from the game's **Main Menu**, not while the save is loaded, or
  the game will overwrite your edit on its next autosave. In the in-game Load menu,
  press **Refresh** to see the just-written copy.
- A save-edit `.oplan` stays **linked** to its save — it references the live state
  rather than embedding it, so keep the save if you want to write back later. For a
  ship detached from any save, **Export** it instead.

Editing a ship you don't own (a station, another vessel) is gated behind a stern
warning — it's unsupported.

## Containers & cargo

Right-click a container — a locker, a nav console, a crate from a save-imported
ship — and choose **View contents…** to see its inventory laid out on the grid and
drill into nested containers. On an editable design you can also **add, remove and
rearrange** loose cargo; contents travel with the ship through **Export** and save
write-back.

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
illegal tile). Placing *arbitrary* loose inventory — tools, food, consumables — is
a separate flow that isn't in this build yet.

## Theming

The **Theme** picker (top-right) switches the app chrome between System / Light /
Dark; the choice persists. The ship canvas always stays dark — the sprites are
drawn for dark space.

## Help & reporting a bug

- **F1** — the full keybinding table.
- **Help ▾** (top-right) — that reference, plus **Install Ostraplan / shortcuts**
  (see [Getting started](#getting-started)), **Report a Bug** (opens a pre-filled
  GitHub issue with diagnostics) and the **activity log**: an on-disk record of your
  actions (**View** / **Open folder** / **Clear**) that helps pin down a problem
  after it happens.
