<p align="center"><img src="Ostraplan-logo.png" alt="Ostraplan" width="180"/></p>

# Ostraplan

**Ostraplan** is an out-of-game ship planner for **Ostranauts** (Blue Bottle Games). Drag every buildable part onto the game's exact tile grid, validated live against the game's *own* rules, and know a design works before you lay a single tile in-game.

> **The Law:** if you can build it in Ostraplan, you can build it in Ostranauts, and it will be a valid ship.

That promise is kept by *porting* the game's real validation logic, decompiled from `Assembly-CSharp.dll`: placement sockets, airtightness, room certification, and the Ship Rating. Ostraplan reads every part, sprite, and mod from your own install at runtime, so it always reflects the game you actually have.

It is a sibling tool to [**Ostrasort**](https://github.com/Valtora/Ostrasort), the load-order and mod-conflict manager. Use both: design a ship here, then let Ostrasort register your exported ship mod and keep your load order clean.

<img width="2560" height="1380" alt="ostraplan1" src="https://github.com/user-attachments/assets/68fc32ac-a1ce-4efe-9575-cee53bbf68fe" />

<img width="2560" height="1380" alt="ostraplan3" src="https://github.com/user-attachments/assets/b9a17575-fa33-4b46-ab19-dbe7f4cce41a" />

## Features

### Design

- **Every buildable part in one palette.** The game's eight build tabs (HULL, HVAC, POWR, SENS, CTRL, FURN, APPS, MISC) plus **All**, searchable by friendly or internal name, drawn with the real 16 px sprites. Modded parts appear inline with an origin badge. A **FAV/REC** tab keeps the parts you pinned and the ones you just placed one click away, an **ITEMS** tab holds loose floor cargo (food, tools, ammo) you can drop onto tiles or into containers, and a **SPECIAL** tab holds the structure the game places but never lets you build: asteroid and ice cores, regolith walls, floor signs and emblems, station kiosks, terminals and transit lifts.
- **Build on the real grid.** Drag-and-drop with game-accurate autotiling, `R` to rotate, crisp pixel-art zoom and pan, and `Q`/`E` plan-view rotation that matches the in-game camera.
- **A full editing suite.** Drag-paint, box and hollow fill, symmetry mirroring, flood-select, "Replace with…", ship-wide re-skin, group rotate and flip (`H` / `Shift+H`), copy/paste, and unbounded undo/redo.
- **Surfaces mode** (`T`). Treat the deck as a canvas: everything but walls and floors is ghosted and out of the way, and a wall/floor brush re-skins the tile it lands on instead of refusing it. Paint, box and outline an area into a different skin, or set a second brush and lay a checkerboard or stripes. Sprites only, so rooms, airtightness and the rating never move.
- **Bill of materials.** Install-kit counts for the whole ship or the current selection, ready to copy out. Point it
  at a ship you already have and it costs the **retrofit** instead: kits to obtain, kits recovered.
- **Zones.** Draw and manage the game's crew and trade zones (Haul, Barter, Forbid, and content-trigger zones) with the same tools as parts. They round-trip faithfully through export and save write-back.

### Validate

- **Live validation.** You cannot place what the game would refuse. The ghost glows green or red with the failing tiles and the reason, and building past an airlock's mating face is blocked.
- **Rooms, airtightness, and Ship Rating.** Flood-fill compartments, room certification, and the six-slot rating, all computed the way the game computes them.
- **Ship diagnostics.** The game's own nav-console checklist, sixteen rows on its own pass/fail thresholds: transponder, antenna, nav station, reactor and its reactants, thrusters, distributor, reaction mass, backup power, and life support. In game you can only read it by sitting at a console on a ship that already exists; here you find out you forgot the antenna before you build. Every red row names what is missing and where to get it.
- **Propulsion.** RCS acceleration and delta-v, torch acceleration, and reactant hours, with an optional towed mass. The game computes all of this and shows it only on a nav console, on a ship you have already built; here you get it from the plan, along with the reason whenever a figure reads zero (a tank that feeds nothing, a laser with no capacitor to drive it).
- **Flight Dynamics.** What the design does in air, at a place you choose. Pick a body and an altitude and the game's own atmosphere tables supply the gravity, pressure, density and temperature; set an airspeed and an attitude and read the lift, drag and rotor thrust, and whether it holds altitude. Venus, Earth, Mars, Titan and the four gas giants have authored atmospheres, and every environment figure stays editable. Also the place to learn that the game divides lift by mass twice, so doubling a design's mass quarters its lift.
- **RoomViz** (`C`). Every compartment tinted and labelled with what it certifies as, its size, and its value. A room that certifies as nothing says why, down to the single canister in your quarters that quietly costs you the room.
- **Light Viz** (`L`). The game's deferred lighting reproduced pixel-exact on the plan: real occluders, glass windows that pass light, lit wall faces, normal-mapped relief, and optional parallax exterior daylight. Off by default, so a design opens on the flat sprite view rather than an unlit airlock.
- **WalkViz** (`K`). Every tile crew can stand on, tinted by which connected area it belongs to, so a compartment you have walled yourself out of shows up as its own colour. Fittings nobody can operate are ringed in red at the spot they would have to stand; anything reachable only in a suit (hull-mounted kit, a hatch with vacuum across it) is dashed amber instead of flagged. A closed door only counts as sealed when the game would agree: unpowered, locked or damaged. Optionally counts spacewalks.
- **Law report.** Every problem in one place, tracing air leaks to the exact unsealed tile.

### Power and wiring

- **Connector badges** show a powered part's IN and OUT plugs while you place it, so you can line a device up with a conduit before committing.
- **PowerViz** (`P`) floods power from every generator and battery along the conduit network: live runs animate, orphaned runs draw dim red, and a wired device with no feed gets a warning marker.
- **Wire mode** lets you connect signalable devices (sensor to alarm, switch to pump) the way the in-game rewire tool does, and the wiring spawns with an exported ship.

### Import and export

Getting a design into the game runs through one **wizard**: pick a destination, answer only the steps that destination needs, then a **Review** step that tells you exactly what will be written before anything is.

- **Import a template.** Any core or modded ship, as a starting point.
- **Import your ship from a save.** Pull your live layout straight out of a save game.
- **Edit your live ship.** Import it, redesign, and write it back into a **copy** of the save, with crew, cargo and position preserved (the original untouched). Its in-game identity comes along too, and you can rewrite it. Any design will do, not only one imported from that ship: point a stock template or something you drew from scratch at a ship in a save and it replaces the layout wholesale, keeping the crew and cargo aboard.
- **Export as a mod.** A spawnable local mod in the game's own `data/ships` shape, with rooms and rating precomputed. Give it a way into the game (broker kiosk, station Special Offer, Shipbreaker starting ship, or scattered through the derelict fields as salvage); at least one route is required, so the export can't quietly produce a ship nothing will ever spawn. You can also replace an existing ship's identity, and hand the mod to Ostrasort to register in one click.
- **Add a design to a save as a new ship.** Drop a design into a **copy** of a save as a brand-new ship you already own, without replacing anything that's there. It arrives 3 to 5 km away, exactly where the game parks a ship you've bought with nowhere to dock, so the P.A.S.S. ferry will take you to it. Gift it, or charge yourself for it.
- **Transfer a ship between saves.** One action takes a ship out of one playthrough and puts it in another, with its layout, cargo, loose items, zones, wiring, in-game identity and each part's real condition. It copies rather than moves, so both saves keep working and neither original is modified. Crew stay where they are: they belong to the save, not the ship.
- **Wear slider.** Export or inject a ship worn rather than pristine, using the game's own kiosk damage model (defaults to the ~88% condition a "Used" kiosk ship comes at, no part below 10%).

### Mod-aware

Ostraplan resolves your `loading_order.json` exactly like the game, so modded parts appear in the palette. A design records the mods it needs; open it without them and it stays **read-only** so nothing is silently lost. Enable the mods and the parts come back, or confirm the drop and carry on. The Law is exact for vanilla parts and best-effort for modded ones, so a modded part flagged illegal is a warning rather than a hard block.

*Plus PNG and SVG snapshots, light/dark theming, **UI scaling from 100% to 200%** for a high-resolution monitor run at 100% Windows scaling, and an optional background update check.*

## What Ostraplan won't do

Ostraplan does one thing: **it designs ships, and it gets them into your game.** That is the whole remit, and it is deliberately narrow. One question settles nearly every "could it also…": **does the feature take a *design* as its input?** If not, it belongs to some other tool. Ostraplan is a planner that can write what it plans into your game, **not a save editor that happens to draw ships**.

So it won't:

- **Edit your save beyond delivering a ship.** No apartments, crew, careers, money, or station contents. Adding a design to a save is in scope because a design is the input; editing save state with no design involved is not;
- **Simulate the ship.** No power, gas, thermal, or crew simulation (the game authors no per-device rates, so an honest budget would need a full network sim). PowerViz and WalkViz answer *connectivity and reach* from the layout, which is static data; neither runs the sim behind it;
- **Model the economy** beyond the bill of materials;
- **Edit more than one ship per document;**
- **Manage your mods.** It never writes `loading_order.json` (registration stays with Ostrasort/ModTools), and it doesn't publish to the Workshop (export makes a local mod; you upload in-game);
- **Run anywhere but Windows.**

**Read-only by default:** it never touches your game install, saves, or `loading_order.json` unless you ask. Save-editing creates a **copy** unless you explicitly opt into an in-place edit, which then keeps a backup anyway.

The full statement, with the reasoning and worked examples of requests either side of the line, is in **[docs/SCOPE.md](docs/SCOPE.md)**. Read it before filing a feature request.

## Quick start

Download **`Ostraplan-win-Setup.exe`** from the [Releases](https://github.com/Valtora/Ostraplan/releases) page and run it. It installs for your user only (no admin, nothing outside your user profile), adds Start-Menu and Desktop shortcuts and an Add/Remove Programs entry, and opens the app. Prefer not to install? Grab **`Ostraplan-win-Portable.zip`**, unzip it anywhere, and run `Ostraplan.exe`.

It isn't code-signed yet, so the first run may trip Windows SmartScreen ("Windows protected your PC") — click **More info ▸ Run anyway**. If you'd rather not trust the binary, build it yourself (below).

**Updates are automatic.** When a new version is out, Ostraplan downloads it in the background on launch and shows a **Restart to update** button in the toolbar. The update applies only when you click it, so you never lose unsaved work. To check on demand, there is a *Check for updates* button in **Help ▾ ▸ Controls & keybinds**. The first launch after an update shows what it brought, and **Help ▾ ▸ View Changelog** brings those notes back any time. Your settings and activity log live in `%APPDATA%\Ostraplan` and survive updates and uninstalls.

**Requirements:** Windows, and a **local Ostranauts install**. Ostraplan finds a Steam install automatically and reads its data and sprites at runtime; point it at the folder if yours is elsewhere. Without the game, Ostraplan has nothing to read and won't work. **No game assets are distributed with the tool.**

## Building from source

Needs the **.NET 10 SDK**. Windows only (the app is WPF).

```powershell
dotnet run --project src\Ostraplan.App     # build and launch
.\test.ps1                                 # run the test suite (most tests are game-free)
```

Tests that need a local Ostranauts install report as **skipped** (never a false pass) when it is absent, so a green run is always honest.

For the full build, test, versioning and release procedure, see **[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md)**.

## Documentation

- [docs/usage.md](docs/usage.md) — how to use it, start to finish.
- [docs/SCOPE.md](docs/SCOPE.md) — what Ostraplan is for, and where the line is drawn.
- [docs/GAME-INTERNALS.md](docs/GAME-INTERNALS.md) — the reverse-engineering reference: how Ostranauts works internally, and what Ostraplan ports.
- [docs/OPLAN-FORMAT.md](docs/OPLAN-FORMAT.md) — the `.oplan` document format, field by field.
- [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) — building, running, versioning, and cutting a release.
- [docs/TESTING.md](docs/TESTING.md) — how the test suite is structured (game-free vs game-gated) and how to run it.
- [CHANGELOG.md](CHANGELOG.md) — what shipped, version by version.
- [CONTRIBUTING.md](CONTRIBUTING.md) — bug reports and pull requests.
- [SECURITY.md](SECURITY.md) — reporting a security issue.
- [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) — how we behave here.

## Licence and disclaimers

Ostraplan is free and open source under the [MIT License](LICENSE).

It is a fan-made tool, **not affiliated with or endorsed by Blue Bottle Games**. Ostranauts and all its data and art are © Blue Bottle Games. Ostraplan ships **none** of it, reading everything from your own install at runtime. Please support and buy the game: <https://store.steampowered.com/app/1022980/Ostranauts/>. **You cannot use Ostraplan without a valid copy of the game on your machine.**

**No warranty.** Ostraplan is provided as-is, with no warranty of any kind. It can write to your save files, so back them up first. Use it at your own risk. I am not responsible if it breaks your game or save, or causes your ship to become sentient.

**Active development.** There will be bugs, and I will do my best to fix them promptly, but this is a free tool built around a day job, so please be patient. Report bugs on the [Issues tracker](https://github.com/Valtora/Ostraplan/issues).
