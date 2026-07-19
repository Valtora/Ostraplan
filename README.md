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

- **Every buildable part in one palette.** The game's eight build tabs (HULL, HVAC, POWR, SENS, CTRL, FURN, APPS, MISC), searchable by friendly or internal name, drawn with the real 16 px sprites. Modded parts appear inline with an origin badge. A tenth **ITEMS** tab holds loose floor cargo (food, tools, ammo) you can drop onto tiles or into containers.
- **Build on the real grid.** Drag-and-drop with game-accurate autotiling, `R` to rotate, crisp pixel-art zoom and pan, and `Q`/`E` plan-view rotation that matches the in-game camera.
- **A full editing suite.** Drag-paint, box and hollow fill, symmetry mirroring, flood-select, "Replace with…", ship-wide re-skin, group rotate and flip (`H` / `Shift+H`), copy/paste, and unbounded undo/redo.
- **Bill of materials.** Install-kit counts for the whole ship or the current selection, ready to copy out.
- **Zones.** Draw and manage the game's crew and trade zones (Haul, Barter, Forbid, and content-trigger zones) with the same tools as parts. They round-trip faithfully through export and save write-back.

### Validate

- **Live validation.** You cannot place what the game would refuse. The ghost glows green or red with the failing tiles and the reason, and building past an airlock's mating face is blocked.
- **Rooms, airtightness, and Ship Rating.** Flood-fill compartments, room certification, and the six-slot rating, all computed the way the game computes them.
- **RoomViz** (`C`). Every compartment tinted and labelled with what it certifies as, its size, and its value. A room that certifies as nothing says why, down to the single canister in your quarters that quietly costs you the room.
- **Light Viz** (`L`, on by default). The game's deferred lighting reproduced pixel-exact on the plan: real occluders, glass windows that pass light, lit wall faces, normal-mapped relief, and optional parallax exterior daylight.
- **Law report.** Every problem in one place, tracing air leaks to the exact unsealed tile.

### Power and wiring

- **Connector badges** show a powered part's IN and OUT plugs while you place it, so you can line a device up with a conduit before committing.
- **PowerViz** (`P`) floods power from every generator and battery along the conduit network: live runs animate, orphaned runs draw dim red, and a wired device with no feed gets a warning marker.
- **Wire mode** lets you connect signalable devices (sensor to alarm, switch to pump) the way the in-game rewire tool does, and the wiring spawns with an exported ship.

### Import and export

- **Import a template.** Any core or modded ship, as a starting point.
- **Import your ship from a save.** Pull your live layout straight out of a save game.
- **Edit your live ship.** Import it, redesign, and write it back into a **copy** of the save, with crew, cargo, position, and identity preserved (the original untouched).
- **Export as a mod.** A spawnable local mod in the game's own `data/ships` shape, with rooms and rating precomputed. Optionally make it obtainable in-game (broker kiosk, station Special Offer, or Shipbreaker starting ship), replace an existing ship's identity, and hand it to Ostrasort to register in one click.
- **Wear slider.** Export or inject a ship worn rather than pristine, using the game's own kiosk damage model (defaults to the ~88% condition a "Used" kiosk ship comes at, no part below 10%).

### Mod-aware

Ostraplan resolves your `loading_order.json` exactly like the game, so modded parts appear in the palette. A design records the mods it needs; open it without them and it stays **read-only** so nothing is silently lost. Enable the mods and the parts come back, or confirm the drop and carry on. The Law is exact for vanilla parts and best-effort for modded ones, so a modded part flagged illegal is a warning rather than a hard block.

*Plus PNG and SVG snapshots, light/dark theming, and an optional background update check.*

## What Ostraplan won't do

Ostraplan validates the **build**. It is a **plan**ner, not a simulator, so it won't:

- Simulate power, gas, thermal, or crew pathing (the game authors no per-device rates, so an honest budget would need a full network sim);
- Model the economy beyond the bill of materials;
- Edit more than one ship per document;
- Write `loading_order.json` (registration stays with Ostrasort/ModTools) or publish to the Workshop (export makes a local mod; you upload in-game);
- Run anywhere but Windows.

**Read-only by default:** it never touches your game install, saves, or `loading_order.json` unless you ask. Save-editing creates a **copy** unless you explicitly opt into an in-place edit, which then keeps a backup anyway.

## Quick start

Download **`Ostraplan-win-Setup.exe`** from the [Releases](https://github.com/Valtora/Ostraplan/releases) page and run it. It installs for your user only (no admin, nothing outside your user profile), adds Start-Menu and Desktop shortcuts and an Add/Remove Programs entry, and opens the app. Prefer not to install? Grab **`Ostraplan-win-Portable.zip`**, unzip it anywhere, and run `Ostraplan.exe`.

It isn't code-signed yet, so the first run may trip Windows SmartScreen ("Windows protected your PC") — click **More info ▸ Run anyway**. If you'd rather not trust the binary, build it yourself (below).

**Updates are automatic.** When a new version is out, Ostraplan downloads it quietly in the background on launch and shows a **Restart to update** button in the toolbar. The update applies only when you click it, so you never lose unsaved work. There is also a *Check for updates* button in Help. Your settings and activity log live in `%APPDATA%\Ostraplan` and survive updates and uninstalls.

**Requirements:** Windows, and a **local Ostranauts install**. Ostraplan finds a Steam install automatically and reads its data and sprites at runtime; point it at the folder if yours is elsewhere. Without the game, Ostraplan has nothing to read and won't work. **No game assets are distributed with the tool.**

## Building from source

Needs the **.NET 10 SDK**. Windows only (the app is WPF).

```powershell
dotnet run --project src\Ostraplan.App     # launch
.\test.ps1                                 # run the test suite (most tests are game-free)
.\test.ps1 -Filter Rooms                   # run a subset by name
.\publish.ps1                              # build the Velopack release into publish\releases
```

`publish.ps1` needs the Velopack CLI once (`dotnet tool install -g vpk`). It does a self-contained publish, smoke-tests the built exe, then packs the installer (`Ostraplan-win-Setup.exe`), the portable zip, the update package, and the release manifest into `publish\releases`. Close the running app first — it locks its own exe. Cut a release by uploading that whole folder:

```powershell
vpk upload github --outputDir publish\releases --repoUrl https://github.com/Valtora/Ostraplan --publish --releaseName vX.Y.Z --tag vX.Y.Z --token (gh auth token)
```

Installed and portable copies pick up the new version on their next launch (they compare against `releases.win.json`).

Most tests run without the game; the ones that need a local Ostranauts install report as **skipped** (never a false pass) when it is absent. See [docs/TESTING.md](docs/TESTING.md).

## Documentation

- [docs/usage.md](docs/usage.md) — how to use it, start to finish.
- [docs/GAME-INTERNALS.md](docs/GAME-INTERNALS.md) — the reverse-engineering reference: how Ostranauts works internally, and what Ostraplan ports.
- [docs/OPLAN-FORMAT.md](docs/OPLAN-FORMAT.md) — the `.oplan` document format, field by field.
- [docs/TESTING.md](docs/TESTING.md) — how the test suite is structured (game-free vs game-gated) and how to run it.
- [CHANGELOG.md](CHANGELOG.md) — what shipped, version by version.
- [CONTRIBUTING.md](CONTRIBUTING.md) — bug reports and pull requests.

## Licence and disclaimers

Ostraplan is free and open source under the [MIT License](LICENSE).

It is a fan-made tool, **not affiliated with or endorsed by Blue Bottle Games**. Ostranauts and all its data and art are © Blue Bottle Games. Ostraplan ships **none** of it, reading everything from your own install at runtime. Please support and buy the game: <https://store.steampowered.com/app/1022980/Ostranauts/>. **You cannot use Ostraplan without a valid copy of the game on your machine.**

**No warranty.** Ostraplan is provided as-is, with no warranty of any kind. It can write to your save files, so back them up first. Use it at your own risk. I am not responsible if it breaks your game or save, or causes your ship to become sentient.

**Active development.** There will be bugs, and I will do my best to fix them promptly, but this is a free tool built around a day job, so please be patient. Report bugs on the [Issues tracker](https://github.com/Valtora/Ostraplan/issues).
