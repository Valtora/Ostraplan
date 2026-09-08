# Features

What Ostraplan does, on one page. For how to use any of it, see [usage.md](usage.md). For
where the remit starts and stops, see [SCOPE.md](SCOPE.md). For what shipped when, see
[CHANGELOG.md](../CHANGELOG.md).

## Design

The palette carries every buildable part in your install, split into the game's eight build tabs (HULL, HVAC, POWR, SENS, CTRL, FURN, APPS, MISC) plus **All**, an **ITEMS** tab of loose floor cargo, a **SPECIAL** tab of the structure the game places but never lets you build (asteroid and ice cores, regolith walls, floor signs, station kiosks, terminals and transit lifts), and a **FAV/REC** tab for the parts you pinned and the ones you just placed. Search by friendly or internal name; modded parts appear inline with an origin badge. Drag onto the real grid with game-accurate autotiling, `R` to rotate, and `Q`/`E` to turn the plan the way the in-game camera turns. Behind that is a full editing suite: drag-paint, box and hollow fill, symmetry mirroring on axes you can move, flood-select, "Replace with…", ship-wide re-skin, group rotate and flip (`H` / `Shift+H`), copy/paste, and unbounded undo/redo. **Surfaces mode** (`T`) treats the deck as a canvas, re-skinning walls and floors without moving rooms, airtightness or the rating. Anything can be named, an installed part and a crate on the floor alike. Where a fitting is only legal late in the build, **Build last** sends it to the end of its class, and **Force** is the escape hatch for a build the placement law cannot describe.

## What the ship carries

"View contents" opens the inventory of an installed container and of an item lying on the deck alike, down through nested containers and through the pockets rather than grids that an EVA suit, a backpack, a coverall or a wrist PDA store in. Those arrive with the item exactly as the game spawns them, and they travel to your save with whatever you put in them. Canisters and tanks are filled with what they are rated for. **Loot spawners**, the invisible markers that decide what a ship arrives carrying, can be placed, set up, drawn with their scatter square, or **run** on the spot into the items they stand for, using the game's own loot tables and arithmetic. The **Item Manifest** lists every item the design holds wherever it sits, arranged by type or by location, scoped to the whole ship or to one zone. **Zones** (haul, barter, forbid, and content-trigger zones) are drawn with the same tools as parts and round-trip through export and save write-back.

## Validate

You cannot place what the game would refuse: the ghost glows green or red with the failing tiles and the reason, and building past an airlock's mating face is blocked. Compartments, room certification and the six-slot **Ship Rating** are computed the way the game computes them, and the **Law report** puts every problem in one place, tracing an air leak to the exact unsealed tile. Four reports then answer questions the game itself will only answer on a ship you have already built.

- **Ship diagnostics.** The nav console's own checklist, sixteen rows on its own pass/fail thresholds: transponder, antenna, nav station, reactor and its reactants, thrusters, distributor, reaction mass, backup power, and life support. Every red row names what is missing and where to get it, so you find out you forgot the antenna before you build.
- **Propulsion.** RCS acceleration and delta-v, torch acceleration, and reactant hours, with an optional towed mass, along with the reason whenever a figure reads zero (a tank that feeds nothing, a laser with no capacitor to drive it).
- **Flight Dynamics.** What the design does in air, at a body and altitude you pick. The game's own atmosphere tables supply the gravity, pressure, density and temperature; set an airspeed and an attitude and read the lift, drag and rotor thrust, and whether it holds altitude. Venus, Earth, Mars, Titan and the four gas giants have authored atmospheres.
- **Docking compatibility.** Whether an airlock will actually mate, against one ship you choose or against every stock template in your install. Each row draws the two hulls docked on the plan, the other one ghosted at the mating pose, and a refusal highlights the tiles of yours that are in the way.

A **bill of materials** counts the install kits for the whole ship or the current selection. Point it at a ship you already have and it costs the **retrofit** instead: kits to obtain, kits recovered.

## Overlays

Each one is a view and changes nothing about what a click does. **RoomViz** (`C`) tints and labels every compartment with what it certifies as, its size and its value, and says why one certifies as nothing, down to the single canister in your quarters that costs you the room. **Light Viz** (`L`) reproduces the game's deferred lighting pixel-exact, real occluders, glass windows that pass light, lit wall faces and normal-mapped relief included, and is off by default so a design opens on the flat sprite view rather than an unlit airlock. **WalkViz** (`K`) tints every tile crew can stand on by connected area, rings in red the fittings nobody can operate at the spot they would have to stand, and dashes in amber anything reachable only in a suit. **Access** (`J`) marks the tile a crew member would work a fitting from. **PowerViz** (`P`) floods power from every generator and battery along the conduit network, animating live runs and drawing orphaned ones dim red. **Wire** shows the device wiring, **Zones** shows the zones, and **Spawners** takes the loot spawners off the plan when you want the ship on its own.

## Devices and wiring

Connector badges show a powered part's IN and OUT plugs while you place it, so a device can be lined up with a conduit before committing. Wiring starts from the part: right-click it, choose **Wiring…**, and click its partner, with every legal partner ringed while you pick. Sensor wiring is what makes an air pump, a scrubber, a heater or a cooler run at all, and signal box wiring switches anything installed on and off. Each device then carries the control panel the game gives it in the inspector, bus knob and per-model modes included, and a fusion core carries its full panel down to the eight switches of the ignition sequence, so a ship can spawn with its reactor already lit. **Firing Groups** sorts every weapon on the ship in one window, which in game means the Weapons MFD one weapon at a time. **Arrange screen** lays out a nav console's modules, each drawn as the panel you would see at the console. All of it exports, writes back into a save, and comes back on import.

## Simulate

**Simulate ▸ Micrometeoroid Strike…** and **Weapon Impact…** fire along a line you drag and mark every part they damage, keyed as damaged, broken or destroyed, then report what the hit did to the *ship*: which compartments lost their air, what the rating did, and what each broken part turned into. That damage lives beside the design and is never saved, so firing again and starting over cost the same nothing. The **Damage Brush** is the other half of the menu and works the other way round, painting on condition that you keep, part by part, which is how a station meant to look lived-in gets described. Going the other way, **Repair All** swaps every broken part for the working one using the game's own repair jobs, across the whole ship or a selection, and repairs a themed wall into its own theme.

## Import and export

Getting a design into the game runs through one **wizard**: pick a destination, answer only the steps that destination needs, then a **Review** step that tells you exactly what will be written before anything is.

- **Import a template.** Any core or modded ship, as a starting point.
- **Import your ship from a save.** Your live layout, cargo, wiring and condition, straight out of a save game. Your **station apartment** too: the game keeps one as an ordinary ship record, and Ostraplan finds it the way the game registers it, which is not the list your vessels are in.
- **Edit your live ship.** Import it, redesign, and write it back into a **copy** of the save with crew, cargo and position preserved, and its in-game identity along with it. Any design will do, not only one imported from that ship: point a stock template at a ship in a save and it replaces the layout wholesale.
- **Export as a mod.** A spawnable local mod in the game's own `data/ships` shape, with rooms and rating precomputed. Give it a way into the game (broker kiosk, station Special Offer, Shipbreaker starting ship, or salvage scattered through the derelict fields); at least one route is required, so an export can never produce a ship nothing will ever spawn.
- **Export a ship pack.** Several designs gathered into one mod, each with its own name, condition, replacement target and way of being obtained, written with **one merged set of loot files** so two ships sharing a kiosk are both in it. Save the pack as an `.oplanmod`, edit any design it references, and export the lot again.
- **Add a design to a save as a new ship.** Drop a design into a **copy** of a save as a ship you already own, without replacing anything that is there. It arrives 3 to 5 km away, exactly where the game parks a ship you have bought with nowhere to dock, so the P.A.S.S. ferry will take you to it.
- **Transfer a ship between saves.** Layout, cargo, loose items, zones, wiring, in-game identity and each part's real condition, out of one playthrough and into another. It copies rather than moves, so both saves keep working and neither original is modified.
- **Design a station apartment.** A residence is a ship in every way that matters to a planner: same grid, same placement law, same rooms, same airtightness. Open a residence template or your own apartment out of a save, redesign it, and deliver it by those same save routes. Ostraplan knows it is not a vessel, so the Ship Rating, the nav checklist, propulsion and flight dynamics step aside instead of reporting a design with no engine as a catastrophe.

A **wear slider** exports or injects a ship worn rather than pristine, using the game's own kiosk damage model (defaults to the ~88% condition a "Used" kiosk ship comes at, no part below 10%).

## Mod-aware

Ostraplan resolves your `loading_order.json` exactly like the game, so modded parts appear in the palette. A design records the mods it needs; open it without them and it stays **read-only** so nothing is dropped without your say-so. Enable the mods and the parts come back, or confirm the drop and carry on. The Law is exact for vanilla parts and best-effort for modded ones, so a modded part flagged illegal is a warning rather than a hard block.

## The rest of it

Tabs, so several designs are open at once and you can copy between them. PNG and SVG snapshots. Light/dark theming. A **plan backdrop** you choose: a solid colour, a checkerboard, or one of the game's own places. Optional scale markings every 5, 10 or 20 tiles. The window reopens at the size and position you closed it at, or maximised every launch if you would rather. A resizable palette and inspector that can be hidden altogether. **UI scaling from 80% to 200%**, up for a high-resolution monitor run at 100% Windows scaling and down to fit more into the window you have. And an optional background update check.
