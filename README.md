<p align="center"><img src="Ostraplan-logo.png" alt="Ostraplan" width="180"/></p>

# Ostraplan

An out-of-game ship planner for **Ostranauts** (Blue Bottle Games). Design a ship
on your desktop — drag every buildable part onto the game's exact tile grid — and
Ostraplan validates it live with the game's *own* rules, so you know it works
before you ever lay a floor tile in-game.

> **The Law:** if you can build it in Ostraplan, you can build it in Ostranauts,
> and it will be a valid ship.

That promise is kept by *porting* the game's real validation logic — placement
sockets, room/airtightness flood-fill, room certification, and the Ship Rating —
not by approximating it. Ostraplan is a sibling tool to
[Ostrasort](https://github.com/Valtora/Ostrasort): same stack, same "read your
live install as the source of truth" philosophy.

## What it does

- **Every buildable part, one palette.** The game's eight build tabs
  (HULL · HVAC · POWR · SENS · CTRL · FURN · APPS · MISC), searchable by friendly
  or internal name, drawn with the real 16 px sprites.
- **Build on the real grid.** Drag-and-drop with game-accurate autotiling for
  walls and floors; rotate with `R` (the same key as in-game); crisp pixel-art
  zoom and pan; plan-view rotation.
- **Live legality — the Law.** You cannot place anything the game would refuse:
  the ghost glows green/red with the exact failing tiles and the reason, and
  construction beyond an airlock's mating face is unbuildable. Moves and rotations
  into an illegal spot are flagged, not silently allowed.
- **Rooms, airtightness & Ship Rating.** Flood-fill compartments, room
  certification, and the six-slot rating — all reachable from a law report that
  traces air leaks to the unsealed tile.
- **A full editing suite.** Drag-paint, box- and hollow-fill, symmetry mirror,
  double-click flood-select, "Replace with…", ship-wide wall/floor **re-skin**,
  group rotate, copy/paste, and unbounded undo/redo.
- **Bill of materials.** Counts each part's install kit for the whole ship or the
  current selection.
- **Import & export.**
  - *Export* a finished design as a **spawnable local mod** (the game's own
    `data/ships` shape, with rooms and rating precomputed).
  - *Import* a ship from a **core or modded template**, or your **own ship from a
    save game**.
  - **Edit your live ship:** import it, redesign the structure, and write it back
    into a **copy** of the save — crew, cargo, world position and ship identity
    all preserved, the original untouched.
- **Mod-aware.** Resolves your `loading_order.json` exactly like the game, so
  modded parts appear in the palette. A design that depends on mods records them;
  open it without those mods and it opens **read-only** until you enable them, so
  nothing is silently lost.
- **Quality of life.** PNG snapshot of a design, light/dark theming, and an
  optional GitHub update check.

## Scope — what it can and can't do

Ostraplan validates *structure*. It is a planner, not a simulator.

**It does:** the full placement/room/certification/rating law · export as a
spawnable mod · template and save-game import · edit-your-live-ship write-back ·
mod-aware data resolution · bill of materials.

**It deliberately doesn't:**

- Simulate power networks, gas flow, thermal, or crew pathing/gameplay. (The game
  authors no per-device power draw or gas-throughput rates, so an honest budget
  would require simulating the network — a non-goal.)
- Model economy beyond the bill of materials.
- Edit more than one ship per document (no docked-layout editing).
- Write `loading_order.json` — registering an exported mod stays with ModTools /
  Ostrasort (single-writer discipline).
- Publish to the Steam Workshop — export produces a local mod; uploading remains
  the in-game flow.
- Run anywhere but Windows.

**Read-only guarantee:** Ostraplan never modifies your game install, your saves
(the save-edit write-back always targets a *copy* unless you explicitly opt into
in-place, which keeps a `.bak`), or `loading_order.json`.

## Quick start

**Just want to use it:** download `Ostraplan.exe` from the
[Releases](https://github.com/Valtora/Ostraplan/releases) page and double-click
it. It's a single self-contained executable — no .NET install required.

**Requirements:** Windows, and a local install of Ostranauts (Steam). Ostraplan
finds the game automatically and reads its data and sprites at runtime — **no game
assets are distributed with the tool.**

**Build from source** (.NET 10 SDK):

```powershell
dotnet run --project src\Ostraplan.App     # launch
dotnet test                                # engine + live-data + render-smoke tests
.\publish.ps1                              # build publish\Ostraplan.exe (self-contained, self-tested)
```

## Documentation

- [docs/usage.md](docs/usage.md) — how to use it: the palette, placing and
  validating, the law report, import/export, and editing your live ship.
- [CHANGELOG.md](CHANGELOG.md) — what shipped, version by version.
- [docs/SPEC.md](docs/SPEC.md) — design, scope, the `.oplan` file format, and the
  roadmap.
- [docs/GAME-INTERNALS.md](docs/GAME-INTERNALS.md) — the reverse-engineering
  reference: every game algorithm Ostraplan ports, the data-model gotchas, and
  what's ported / deferred / excluded.

## Licence / disclaimer

Ostraplan is released under the [MIT Licence](LICENSE).

It is a fan-made tool and is **not affiliated with or endorsed by Blue Bottle
Games**. Ostranauts and all its data and art are © Blue Bottle Games — Ostraplan
ships **none** of it, and reads everything from your own legitimate install at
runtime. Please support the game: <https://store.steampowered.com/app/1024960/Ostranauts/>.
