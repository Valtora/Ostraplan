# Changelog

All notable changes to Ostraplan. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/); versions are the app version
(`Help ▸ version`), which the built-in update check compares against GitHub
release tags.

Ostraplan validates ships by *porting* Ostranauts' own logic; the game version
each release was verified against is recorded in
[docs/GAME-INTERNALS.md](docs/GAME-INTERNALS.md) (currently **1.0.0.7**).

## [Unreleased]

### Added
- **A warning when something is parked in front of an airlock that needs to dock**
  ([#29](https://github.com/Valtora/Ostraplan/issues/29)). A towing brace attaches to an
  airlock by a single tile, which means all four of its rotations find somewhere to sit,
  and three of them leave it lying across the airlock or out in front of it. Only the
  primary airlock's face stops you building past it, so on a secondary the brace would go
  down two tiles the wrong side of the hatch with nothing said. That pose is legal in the
  game too, so Ostraplan still places it, but the port it blocks can never take a station
  collar and you now get told so. The usual fix is to give the brace the same rotation as
  its airlock. Dismissible, for the case where the port is a deliberate internal bay.

### Fixed
- **Clothing arrived in game with no pockets, and could not be filled in Ostraplan either.** Put a
  pair of coveralls in a backpack and there was nowhere to put anything, and the pair that reached
  your save came back as coveralls with no pockets at all: useless, permanently. Coveralls have no
  pockets of their own. The game gives them out when the garment is created, as separate little
  containers, and the same is true of the pouches in a backpack, the rows in an EVA locker and the
  data store in a wrist PDA. Ostraplan only understood capacity a def declares outright, so it saw
  a garment as holding nothing and wrote it into the save as a bare item, and nothing ever handed it
  its pockets. Those pockets are now part of the item from the moment you add it: they show up in the
  inventory, you can drill into them and fill them, and they travel to your save with whatever you
  put in. They are not charged for, since you do not buy pockets separately from the coveralls.
  Genuine starting stock is untouched: a railgun still arrives with exactly the ammunition you
  authored and no more.
- **A ship whose airlock was pried open came back from the save shifted, and could not dock.**
  If you had forced your primary airlock open in game (running out of power will do it), the save
  holds it as a different object to the closed one, and Ostraplan only recognised the closed
  version. So it decided your ship had no primary airlock at all: the airlock stopped being fixed
  in place, the export tagged no port as the primary, and reopening the design quietly added a
  *second* airlock at the origin. That extra port is what moved everything. The game registers each
  primary port at the head of its list, so the newest one wins, and the ship is then positioned by
  an airlock sitting out in space instead of the real one, a few tiles off from the station collar.
  Ostraplan now identifies the primary airlock by what it *is* rather than by which state it is in,
  so open, damaged and modded airlocks all count, and your ship's own state is left exactly as you
  had it. Designs saved while this was broken still carry the stray port, so the problem scan now
  reports a ship carrying more than one primary airlock, names each one with its tile, and says
  which one the game would dock by. Delete the one at the origin.
- **Reactor parts could be built on top of one another, and Ostraplan said nothing.** A design could
  stack two Reactor Fuel Regulators across six shared tiles, bury a Fusion Core Pump and a Pellet
  Feeder inside one of them, drop a Laser Capacitor over the top, and pave the Field Coils' centre
  tile (the one that has to stay open to space) — and the problem scan reported it clean. The same
  hole applied to air pumps and vents. The cause was a rule added in 0.8.0, which said a sealed floor
  stops `IsFixture` blocking a placement. Most parts also test `IsObstruction`, so for them nothing
  changed and the hole stayed hidden. But ten parts — the entire fusion chain, the air pumps and the
  vents — are guarded by `IsFixture` alone, and for those the rule removed the only occupancy check
  they had, on every floored tile in the ship. That rule is gone. **The 0.8.0 entry below is wrong**:
  it claimed the game lets you build a fixture on an under-floor storage bin, and the game refuses
  that. Nothing goes on top of a sub-floor bin except ceiling-level items, and conduits and overhead
  lights were never affected either way, because neither one tests `IsFixture` in the first place.
  Reaching *across* a bin to work an adjacent fixture is a separate question, handled by the crew
  walkability rules, and is unchanged. A design that scanned clean before may now report blocking
  problems: those placements were always illegal in game, and would have failed on spawn.
- **Ammo stacks survive the trip into your save.** A hundred rounds loaded into a PDC arrived
  in game as a hundred separate bullets rather than five stacks of twenty, and rounds added to
  ammo the ship already carried did not arrive at all. Ostranauts stores a stack as one item
  that lists the rest, and the list was the part Ostraplan was leaving out when it wrote into an
  existing save: the rounds were all written, with nothing tying them together, so the game
  loaded them as singles. Stacks are now listed on the way out, and re-listed from whatever the
  stack actually holds, so topping one up and taking from one both land. This only ever affected
  updating a ship in an existing save. Exporting a design as a mod, and granting a whole new ship
  into a save, were always written correctly.
- **"Deduct the edit cost from your credits" works while you're docked.** The checkbox was
  disabled, with "No player balance found in this save", on any save written while you were
  not standing on the ship you were editing — docked at a station, aboard someone else's
  vessel, or editing a second ship of your own. Nothing was wrong with the save. Your credits
  are on your character, and a save files that character in the record of whatever they were
  standing on at the time, so on a docked save the money is in the station's record and
  Ostraplan was only ever looking in the ship's. It now finds the character wherever they
  are, and writes the deduction back to that record, so the option is available and correct
  in every case. Autosaves were the usual way to hit this, since a docking autosave puts you
  on the station.

## [0.87.3] 2026-08-15, the game's own draw order, aimed surface brushes, and menus that scale

### Fixed
- **Right-click menus and tooltips follow the UI scale** ([#25](https://github.com/Valtora/Ostraplan/issues/25)).
  Everything else grew with the setting and these two stayed at 100%, which on a high-density screen is exactly
  where it hurts. A menu attached to what you right-clicked is positioned by it but not part of it, so it never
  inherited the scale the way a dropdown does; it is scaled on open now, and submenus follow the menu that opened
  them. A menu opened at the edge of the screen is still fitted onto it at the larger size.
- **A Surfaces brush lands at the rotation you aimed it at** ([#27](https://github.com/Valtora/Ostraplan/issues/27)).
  Pressing **R** turned the ghost but not the tile: painting a decal onto a floor that was already there re-used
  that floor's rotation, and painting a decal onto a copy of itself did nothing at all, so an arrow under a door
  came out square whichever way you tried. A re-skin now lands the way the ghost showed it, and turning a decal
  you have already laid works without deleting it first. **Replace with…** and **Find and Replace All…** are
  unchanged: they keep each part's own rotation, which is what makes them a re-skin rather than a re-placement.
- **Objects draw in the game's order instead of the order they were built** ([#28](https://github.com/Valtora/Ostraplan/issues/28)).
  Bins sat behind racks on one tile and in front of them on the next; atmosphere alarms disappeared behind EVA
  chargers and seats. Every part in Ostranauts carries its own place in the draw order (`fZScale` in the item
  data, which the game turns into both a sprite depth and a render queue), and Ostraplan was not reading it — it
  fell through to the order the save happened to list things in, so two identical spots on one ship could
  disagree. It reads it now, so a bin is always in front of a rack and an alarm always in front of a charger,
  whatever order they went down in. Two consequences worth knowing: **walls and doors draw over most fixtures**,
  which is the game's own order and can hide something mounted on a wall tile (press **`** or use the right-click
  list to reach it), and **Move Back / Move Forward** now only shuffle a part against the ones the game puts at
  the same place in the order as it, rather than overruling an answer the game already gave.

## [0.87.0] 2026-08-14, Repair All, canister and tank fills, and nav console arrangement

### Added
- **Repair All.** A ship you imported out of a save arrives with everything that has happened to it, and there was
  no way to undo any of it. There are two kinds of damage in Ostranauts and Ostraplan now clears both.
  - **Parts that are broken as parts** — a damaged wall, a patched hull plate, a wrecked alarm — are their own
    thing in the game's data, and they travel with the design. **Design ▸ Repair All…** swaps every one of them for
    the working part the game's own repair job yields: same tile, same rotation, same name, same contents, one
    undo step. It says how many it found before it touches anything. To fix a section rather than the ship,
    right-click a selection and choose **Repair**. A themed wall is repaired into the same theme rather than
    reverting to a generic one, and repaired devices come back switched on, the way a part you build does.
  - **Wear a part has accumulated** is not part of the design at all: it lives in the save, against each part's
    own health pool. Writing a design back over a ship now offers **"Repair everything"** in the condition panel,
    which clears it across the whole ship and brings the Condition rating back to A.
  - **The condition panel is now one choice instead of two checkboxes.** Keep the condition the ship already has,
    repair it to 100%, or wear it to a target average. Those were always mutually exclusive, and expressing them
    as a tickbox and a slider left "unticked at 100%" meaning two different things depending on where the ship was
    going. Nothing about the wear model changed.
- **You can say how full a canister or tank is.** Right-click any canister, RTA or fuel tank and choose **Fill…**
  for a slider per gas, plus a section for the bulk fuels. It feeds straight into what the ship is worth, how much
  reaction mass the RCS has, and how long a torch drive can burn, so a ship that flies on paper flies on the same
  numbers in game. The fill is saved with the design, undone with `Ctrl+Z`, and written into the ship on export
  and on a save write-back.
  - **The gases share one budget, because that is how the game works.** A container's pressure is the total of
    everything in it at once, so oxygen and nitrogen compete for the same room rather than each getting a share
    of the volume. Every slider's own maximum is "all that is left, plus what this one already holds", so pulling
    one back frees the others up. The gauge shows the total against the container's pressure rating and cannot be
    pushed past it: a canister over its rating takes damage every second in game and eventually bursts into
    shrapnel, and the game's own "full" sits exactly on the rating.
  - **Any ordinary canister will take any gas.** An N2 can and an O2 can are the same shell rated to the same
    pressure, and the label is only what it shipped with, so you can fill an RTA with whatever the ship needs.
    Eight gases are offered — the eight the game can actually store. Water vapour, hydrogen and helium look
    available in the game's code but have no condition behind them and cannot be held by anything.
  - **Fuel tanks are kept as fuel tanks.** A deuterium, helium-3, cryogenic helium or water tank is offered only
    the payload it is built around and no gas at all, because the reactor matches its tanks by name and anything
    else in one is weight the drive cannot use. Their payload has no pressure and no shared budget, so it is
    simply capped at what a full tank carries.
- **Importing a ship out of a save reads what its tanks are actually holding.** Every figure Ostraplan quotes came
  from the part's specification, so a ship with three empty oxygen cans was valued, rated and flown as though they
  were full — on a stock O2 RTA that is about $5,600 of oxygen counted on a $410 shell, three times over. A
  half-empty ship now prices and flies as a half-empty ship.
- **Arrange the nav console screen yourself (right-click a console ▸ Arrange screen…).** The planner's version of
  the console's own edit menu in game: the board is the screen, each module is a panel on it, and you drag them
  where you want them. Drag a panel onto the tray to take it off the screen and out of the tray to put it back, or
  double-click a shelved one to drop it in the first free spot. A panel turns red where it would not fit and snaps
  back if you drop it there; two panels may share an edge, which is how the stock set tiles the board exactly.
  **Reset to stock** returns the console to the arrangement the game itself would produce. The arrangement is part
  of the design: saved with it, undoable, and written into the ship on export and on a save write-back.
- **An empty nav console is stocked when the ship comes in, not quietly at export.** Ships from before 1.0 have
  consoles with nothing in them at all — the game had no console inventory back then — and a stock ship template
  keeps its modules in a spawner Ostraplan doesn't import, so both arrive bare. Ostraplan used to slip the modules
  in on the way out, which worked but left the console looking empty in the planner the whole time you were
  designing. It now fits them at import, says so in the import summary, and you can see them under **View
  contents…**, take one out, or put a different one in. A console that already carries a module is left exactly
  as it is, salvage gaps and all.
- **Move Back and Move Forward (`Ctrl+[` / `Ctrl+]`).** Right-click a part or a loose item and step it through the
  pile of things sharing its tile when the automatic order is not what you want, with **Reset order** to hand that
  pile back to it. The choice is saved with the design, and it stays inside the render layer, so nothing can be
  pushed under a deck plate or over a conduit run.
- **Press `` ` `` to step down the stack under the cursor.** Reaching a part drawn underneath meant a trip through
  the right-click menu every time; now the selection walks down the pile a keystroke at a time and wraps at the
  bottom. The menu still lists the whole pile for when you want to see it.
- **Ostraplan says what an update brought.** An update applies on restart, so until now the app came back looking
  identical and the only way to find out what had changed was to go and read the release on GitHub. The first
  launch after updating shows that version's notes, covering **every release you crossed** if you had been away
  for a few of them. **Help ▾ ▸ View Changelog** brings them back whenever you want, and **All releases on
  GitHub** in that window opens the published release.
  - The notes are read from the changelog **built into the copy you are running**, so they describe your build
    rather than whatever GitHub currently calls latest, and they work offline and in the portable zip.
  - A fresh install shows nothing: it has not updated from anything.

### Fixed
- **"Replace with…" and "Find and Replace All…" no longer hide the parts a big canister obviously matches.** A swap
  is offered between parts of the same size, and size was being read off the item's raw socket grid rather than the
  machine you can see. The three large cryogenic canisters (Cryo Reservoir, D2O Canister, Liq. He Canister) are a
  3x3 machine sitting inside a 7x7 grid, because they reserve two rings of sub-floor around themselves that nothing
  stands on, so they were classed 7x7 and could only ever be swapped for each other. Ostraplan already knew the
  difference — you select, outline and click them as the 3x3 they are — and the swap now uses that same body. So a
  canister offers the twelve 3x3 fixtures it shares the deck with (the water tanks, the MHD generator, the radar,
  the stabilizer and the rest) instead of two, and those parts offer the canisters back. The swap also lands the new
  part where the old one stood rather than at the corner of its socket grid, so a tank becoming a canister does not
  jump two tiles. If the wider sub-floor apron then wants deck the ship has not got, the problem scan flags it, the
  same as a move into a spot that no longer fits.
- **An imported ship's nav console no longer exports with no screens at all.** A console is only a frame: every
  screen on it is a separate module held inside, and Ostraplan fits a set to a console that has none. It decided
  "has none" by asking whether the console was empty, and a console is never empty — it carries a data chip in
  its own slot, on every ship in the game. So the modules went in only for a console you placed yourself, and any
  ship you imported and exported came back with a chip, no screens, and nothing to fly it with. The test is now
  whether the console holds a **module**, and a chip, a manual or anything else in a slot no longer counts.
- **The module set is the one a stock console actually has.** It was missing **Mooring Control**, the page you
  moor and dock with. It is now the game's own stock loadout, plus **Course Plot** and **Flight Dynamics** for
  the trips that need them.
- **The console is laid out the way the game lays one out.** Each screen has a fixed place on the console, and
  the game shelves any module whose place is already taken — deciding which one loses by whatever order it reads
  the console's contents in. Ostraplan now works that out itself and writes the arrangement into the ship, so a
  console comes up looking like a stock console instead of like a coin toss. The stock thirteen fill the screen
  exactly; **Course Plot** and **Flight Dynamics** are aboard but shelved, and you drag either onto the screen
  from the console's own edit menu when you need it. The import summary says so, and a console you arranged
  yourself in game is never rearranged by a write-back.
- **A device dropped into a container comes back working.** Cargo written into a save was written without its
  control panels, so a nav module put into a console by hand had no page behind it. It now carries the same panel
  wiring a newly-built part gets.
- **Grey bulkhead bins no longer demand a floor they never needed.** The grey Rakow "Reserve" bins — the 2x and the
  corner, the ones you see hung on the outside of a hull — are their own part in the game, but Ostraplan was reading
  them as the tan "Vanilla" bin they sit next to in the data. That one mounts over a deck, so touching a grey bin
  turned it red with *needs a sealed floor beneath* even though the game is perfectly happy with it. They were also
  priced, weighed and rated as the tan bin, and lost the roomier container filter. Only these two bins (and their
  damaged forms) were affected; every other part already resolved correctly, and a check now holds the whole
  catalogue to it.
- **Undo takes a moved part all the way back.** Nudging an imported part hands it to the placement rules — that is
  intended, since you have just built it somewhere new — but `Ctrl+Z` only put the tiles back, so the part stayed
  flagged, and stayed on the bill as new construction, with no way to undo either. Undo now restores it completely,
  for a drag, a rotate and a group transform alike.
- **A canister no longer draws over the machine it feeds.** An installed gas canister sits on its regulator's
  gas-input point, which is *the regulator's own row*, so the game's sprite sort cannot tell the two apart and
  Ostraplan fell through to the order they were placed in: drop the canister second and it covered the Hydra. It
  now goes behind, as do canisters and fuel tanks generally, wherever they overlap what they are plugged into. A
  canister is whatever the game's own vessel rule says is one, so a modded one behaves the same.
- **Loose items no longer float on top of everything.** Anything dropped on the deck was drawn after the whole
  ship regardless of what it was lying against, and there was no way to say otherwise. Deck clutter still draws
  over installed parts, which is what lying on the floor looks like, but it is now part of the same order as
  everything else: a dropped canister goes behind a fixture like an installed one, and any loose item can be
  re-stacked by hand. It is also listed in the right-click stack picker, where before it was invisible.
- **A part standing inside a bigger part's body draws under it**, rather than over it if it happened to be placed
  later.

## [0.80.0] 2026-08-13, Flight Dynamics, retrofit costing, import choices and device switching

### Fixed
- **The torch note no longer claims modules are missing when they are switched off.** A reactor whose laser
  arrays, pellet feeders or fuel regulators sat on its module points in their off state read as "the reactor has
  no laser array, no pellet feeder, no fuel regulator", which is exactly how a ship built before **Switch on**
  existed looks: the palette used to place those modules off with no way back. Off modules still make no thrust
  (the game's own rule), but the note now says they are installed and switched off, and points at the right-click
  **Switch on** action that fixes it. A firing torch with some modules off mentions the uncounted ones too.
- **Container contents no longer go missing depending on which import you used.** Importing a ship "for editing"
  kept every container's contents; importing the same ship layout-only, or importing a template, dropped them
  without offering a choice or saying which you were getting. That is what was being reported as cargo importing
  inconsistently. Both routes now ask, and the import report says what came in **as well as** what was left behind,
  telling apart what a checkbox could have fetched from what none can: crew-carried gear (crew are never imported)
  and the contents of a container lying on the deck. "For editing" reports what it kept and that the rest stays in
  the save untouched, instead of pointing at a checkbox that path does not have.
- **Items lying on the deck are no longer imported as ship structure.** A tool, a shirt or a piece of scrap on the
  floor came in as a grid placement, which made it a buildable part: counted in the bill of materials and re-checked
  against the placement law. They now come in as loose objects, which is what they are. The split is the game's own
  (installed structure carries `IsInstalled`; a loose item does not), the same rule the ITEMS palette already used.
  - **A stack on the deck keeps its count.** A pile of 20 scrap persists in the save as a head item plus 19 members,
    and the members used to be reported as cargo left behind while the pile imported as a single piece. It now
    imports as one loose object ×20, and exports back as the same stack.
  - **Except on "your ship, for editing"**, which deliberately keeps them as placements. Only a placement carries a
    save identity, so reclassifying there would leave the save's own item in place while writing a fresh copy beside
    it, doubling every deck item on each round trip. Keeping that write-back lossless wins.

### Added
- **Choose what an import brings in besides the structure.** **Container contents** and **items lying on the deck**
  are now checkboxes on the import, both on by default and remembered between imports. On a template or a
  layout-only save import the contents become the design's own, so they persist in the `.oplan` and travel through
  Export; crew are never imported.
  - **"Your ship, for editing" always brings everything and doesn't ask**, because its write-back emits cargo from
    what was imported: leaving it out would delete that cargo from the save.
- **Name a container or a device.** Right-click one and choose **Rename…**, so a hold of identical racks reads
  "spare tool storage" and "spare reactor parts" instead of five identical rows. It is the game's own rename rather
  than an Ostraplan label, so it travels into the game through Export and Update Ship in Save, and shows in the
  inspector, the right-click menu and the contents window. Clearing the box restores the stock name.
  - **Import now reads names that already exist**, which is what was actually asked for: a ship you labelled in
    game keeps its labels, and so do stock ships that carry them (the **Babak Refit** ships with 51, "Pressurization
    SB" on an electrical box among them). Every one of those was dropped on import before.
  - A name survives a move, an uninstall and a switch on or off, none of which change what a thing is called.
    Offered on containers and devices only. Names typed in Ostraplan are capped at 64 characters; a name read off
    an imported ship is carried and written back exactly as the game stored it, however long.
- **Switch a placed device on or off.** New right-click actions. The game installs powered fixtures off and
  Ostraplan builds the on form wherever it can name one, but a device whose on-state is a colour variant fell
  through and was placed off with no way back — the **Transponder** being the one people hit. It is not cosmetic:
  the Ship Rating and Diagnostics both ignore anything switched off, so a transponder left off really does read as
  a fault.
  - **Alarms only ever switch to their nominal state**, never an alert one, so a design cannot be authored
    mid-emergency. Safe to bake in even where it looks wrong for the ship: every switched-on alarm carries the
    game's own sensor, which reads the real conditions each tick and trips the alarm itself, so one set nominal
    aboard a ship in vacuum goes red on its own. The nominal state is read from the data rather than a colour list
    (the alert states qualify their name in parentheses), which is why it picks Green for the gas alarms and White
    for the thermostat, and keeps working for a modded alarm.
- **Flight Dynamics: what a design does in air (#23).** New report under **Design ▸ Flight Dynamics**, porting the
  game's own atmospheric flight model. The game shows these figures only on a ship that is already flying, in the
  nav console's Flight Dynamics module, and only for wherever that ship happens to be. Here the place is an input.
  - **Pick a body and an altitude** and the report reads the local **gravity, pressure, density, temperature and
    composition** straight out of the game's own `data/star_systems` tables. Venus (ten authored bands, to 350 km),
    Earth, Mars, Titan and the four gas giants all have them, and a mod that adds a body or retunes one is picked
    up like any other data. All three figures the maths uses stay editable, so somewhere the game does not have is
    one number away.
  - **Set airspeed, angle of attack and how far the nose sits off the horizontal**, and read **lift, drag and rotor
    thrust in G**, plus whether the design **holds altitude** against local gravity. Under that: rotor thrust with
    and without turbo, the airspeed at which wings alone would carry it, and a warning when the game's own caps
    (lift at ten local gravities, drag at 2000 m/s²) are what is limiting the answer. **Copy report** puts the lot
    on the clipboard.
  - **Mass hurts twice.** The game divides lift by mass in its coefficient and again to make an acceleration, so
    doubling a design's mass quarters its lift. That, rather than wing area, is usually what decides whether
    something flies, and the report says so.
  - **Aero hull cuts frontal drag only, and only past a threshold** (`max(1, aero / 100)`), so the first hundred
    points of `StatAeroLift` buy nothing and broadside drag is never reduced. **Rotors need air**: rated thrust at
    100 kPa, nothing in vacuum, half as much again in Venus's deep cloud layer.
  - Along the way: **every gravity in Ostranauts is about 2% light**, because the game's gravitational constant is
    written `2E-44f` and a float that small is subnormal, so it actually stores 1.9618×10⁻⁴⁴. Earth reads
    9.66 m/s², Venus 8.43. Ostraplan reproduces the game's figure rather than physics'. See
    [GAME-INTERNALS §23](docs/GAME-INTERNALS.md).
- **The bill of materials can now cost a retrofit, not just a build (#24).** **Retrofit from…** in the Bill of
  Materials nets the design's bill against a ship you already have, so the figures become what the *conversion*
  costs rather than what the design costs. The starting ship can be another **design**, a **ship template**, or a
  **ship in a save** — it is read and measured only, never imported, and the design on the canvas is untouched.
  - The list becomes a diff: **`+N` kits to obtain**, **`−N` recovered**, and `=` for a part type that already
    matches, each with its before → after counts. **Copy list** follows the mode and pastes the same signs.
  - **Recovered material is real material.** Uninstalling a part yields its own uninstalled form, which is the same
    kit the bill counts, so a part the design drops comes back rather than being spent.
  - **It prices material, not labour.** A part that only moves nets to zero — no kit changes hands, but the
    uninstall and re-install jobs are still yours. Non-buildable structure (raw hull, fixed systems, the primary
    airlock) is reported as a count on each side rather than as lines, since it cannot be bought either way.
  - Retrofit always compares the **whole** design, even with a selection active: netting a selection against a
    whole ship would answer nothing.

## [0.73.0] 2026-08-12, Surfaces mode, ship transfer between saves

### Changed
- **The save picker leads with the character, then where they are, then the save's own details.** The ship name led
  and the character trailed it in a dim subtitle, which reads badly for the commonest case there is: several saves
  of one character docked at one station, where every row's leading line was identical and the only thing telling
  them apart was a folder name at the end of the second line. Each row is now the character, the ship or station
  they are on, and a metadata line carrying **when the save was written, how long it has been played, the game
  build, and the folder name** — the same facts the game's own Load screen shows, in the same order.
  - The build reads as bare version numbers, and as **`0.15.1.15 → 1.0.0.9`** for a save made on one build and last
    written by another. That arrow is the only visible sign a save has been carried across a game update, which is
    the first thing worth knowing about one that will not open.
  - **The picker's title and description now belong to whatever asked for it.** One dialog serves four different
    flows, and all four said "Import a ship from a save game" and "crew, cargo, wear and damage are discarded" —
    true only of the first, and actively wrong in front of a write-back that preserves all of it.

### Added
- **Surfaces mode: paint skins straight onto the deck.** New toolbar toggle (**T**) for the detail work a ship-wide
  re-skin cannot express — checkerboard tiling in a bathroom, caution markings around a reactor or a door, an
  armoured run of wall down one flank. Replacing by *area* rather than by type was possible before only as
  box-select, layer-filter, "Replace with…", and it fell apart the moment the box caught a door.
  - **Everything that is not a wall or floor is ghosted and steps out of the way of clicks**, so the floor under a
    bed is one click away instead of a trip through the right-click layer picker, and a box-select over the deck
    catches deck. **View ▸ Surfaces** sets how visible the ghosted layers stay (15% by default, 0 hides them).
  - **A 1×1 wall or floor brush re-skins whatever is on the tile** rather than being refused for landing on it.
    Every gesture already in the editor inherits this: drag to paint a run, **Shift+drag** to box an area,
    **Ctrl at release** for the outline only, **Alt+click** to pick a skin off the ship. Each stroke is one undo step.
  - **Replace, Both or Fill**, per stroke. **Replace** is the default and only re-skins what is already there, so a
    box or a checkerboard dragged over a room never spills new deck past its irregular edges — the failure mode of
    a brush that does both. **Fill** is the old behaviour (bare tiles only), **Both** does both. The ghost says
    which way the tile under the cursor will go before you click.
  - **Reach the floors under the walls.** The game allows a floor and a wall on one tile in either build order, and
    the shipped ships do it nearly everywhere (the core 02 hull floors 335 of its 410 wall tiles), but the wall
    draws over the floor and wins every hit test. **SHOW ▸ Floors** ghosts the wall layer along with everything
    else, so those floors can be seen, clicked and re-skinned; **Walls** does the reverse. It is not only a
    construction-time concern: a floor's autotiling reads its neighbours, so whether the floor continues under a
    wall changes how the visible floor beside it draws its edge. Flex flooring still refuses — its own socket mask
    forbids walls, and the ghost says so.
  - **A second brush and a pattern.** The Surfaces bar sets brush **B** alongside the armed brush, and **Checker**,
    **Rows** or **Columns** alternate between them. The pattern is keyed to the ship's own tile grid rather than to
    where a stroke starts, so separate passes continue one checkerboard instead of each restarting it, and painting
    under symmetry produces no seam down the axis.
  - Only 1×1 wall and floor skins paint, so a stroke runs past anything of a different shape (a wide door keeps its
    def while the wall either side of it changes) and never touches the primary airlock. Like the ship-wide
    re-skin, it changes sprites and names only: rooms, airtightness, certification and the Ship Rating are
    unaffected. Light Viz switches off while the mode is on, because a lit composite has no layers left to ghost.
- **Transfer a ship from one save to another, in one action.** New **File ▸ "Transfer Ship to Another Save…"**.
  Pick the source save and ship, and Ostraplan reads it in and takes you straight to the destination picker.
  Much requested, and the awkward part was never the capability: this was already possible as Import ▸ "Your ship,
  for editing" followed by an export into another save, and almost nobody found it. Both halves are unchanged;
  this walks them.
  - **Each part now arrives in the condition it is really in.** A grant used to synthesise every part fresh and
    roll new wear over it, so a ship moved between saves turned up at whatever the wear slider said rather than
    worn the way it actually was. The real per-part damage now comes across, matched part by part through the save
    items the design was imported from. It is a new **"Keep each part's condition from the source save"** tick on
    the **Condition / Wear** panel, on by default for a design that came from a save, and it stands the wear
    slider down while it is on, since the two are alternatives rather than settings that combine. Parts drawn in
    after the import were never on the original, so they arrive undamaged.
  - Layout, cargo, loose items, zones, device wiring and the ship's in-game identity make the trip, as they did.
  - **Crew do not, and this is now said out loud** rather than left to be discovered. They belong to the save they
    are in rather than to the ship, and are stored on whichever ship record they are physically standing on, so
    while you are docked they are all in the station's record and not aboard the ship at all.
  - It **copies** rather than moves: the ship is written into a copy of the destination save, the source save is
    only read, and both playthroughs keep working. Transferring a ship back into its own save is therefore legal,
    and is how you clone one.
  - The **Save & price** step names the save the ship came out of, because on a transfer "which save" is the whole
    question the step is asking and a list of similar autosaves is easy to misread.
- **Any design can now replace a ship in a save, not only one that was imported from it.** The **Update a ship in
  a save** destination used to be greyed out unless the design came from **Import ▸ "Your ship, for editing"**, and
  the `.oplan` recorded that provenance, so opening a ship's base layout, saving it, and reimporting produced a
  design that could never be written back. There was no way to move your ship onto a different hull short of
  redrawing the whole layout by hand. Reported on Discord.
  - A design with no source save is now **asked which ship in which save to replace** — the same save and ship
    pickers the import uses, with the same warning in front of a station or a vessel you don't own.
  - The write is a **wholesale replacement of the layout**: nothing on the target ship is recognised as already
    built, so it all comes out and the design goes up in its place. The ship stays the same ship — crew, cargo,
    world position, registration and identity are kept — and cargo carries over wherever the container holding it
    survives the swap. Cargo in a container the design doesn't have is destroyed, which **Review** itemises before
    anything is written, as it always has.
  - A confirmation says all of that **before** the wizard costs anything, rather than leaving the user to infer it
    three steps later from an "N deleted, M added" line.
  - The **Write target & cost** step now names the ship it is writing to, not just the save. When you picked the
    target a moment ago, a save name alone doesn't tell you which ship you picked inside it.

### Fixed
- **Cancelling the ship picker on "Update Ship in Save…" no longer opens the export wizard anyway.** Backing out of
  the question left the wizard sitting there on a step whose only content was the reason it could not continue,
  which reads as the cancel having been ignored. The picker now runs before the wizard is built, so a cancel
  abandons the action outright. Asked from inside the wizard instead — by picking the destination once it is
  already open — a cancel still just blocks Next, which is the right answer for a window already on screen.
  - Along the way: a save context located earlier in the session could stand in for the ship you had just picked,
    so a second write in one session could land on the first ship you chose rather than the one in front of you.
    The cached context is now only reused when it is the ship being asked for.
- **An unreadable save now says what it choked on.** "The ship could not be parsed" was the whole of it: the JSON
  error behind it was caught and thrown away, leaving nobody — user or maintainer — able to tell a truncated record
  from a stray byte from a file that was never a ship. Reported on Discord by someone whose pre-1.0 save no version
  of Ostraplan would open. The import now reports the parser's own complaint, the line and position (1-based, as an
  editor counts them), the JSON path, and an excerpt of the file with a caret under the offending character.
  Control characters in the excerpt are escaped, since a raw NUL or newline inside a string is one of the things
  that breaks a save and is invisible printed as-is. The position is resolved through UTF-8 byte counts rather than
  character offsets, so a crew or ship name with a non-ASCII letter in it doesn't shift the caret off the fault.
  - The same applies to **"Couldn't find the player's ship in this save"**, which a *damaged* character record
    produced just as readily as a save that genuinely had none. Each record passed over is now named with the
    reason it was passed over.
  - **Import ▸ From ship template** reports the reason too, for a hand-edited or mod ship file that won't load.
  - For the record, pre-1.0 saves are not the problem as a class: 0.15.1.6, 0.15.1.15 and 1.0.0.9 saves all import,
    and nothing Ostraplan reads out of a ship record changed between them.
- **A save folder holding more than one zip could be read from the wrong one.** Ostranauts writes exactly one, named
  after the folder, but a backup or an extracted copy left beside it could be picked up instead — reporting a
  perfectly good save as having no player ship. The zip named after the folder now wins, falling back to the largest.

## [0.70.0] 2026-08-11, ship identity on save edits, Settings and UI scaling

### Added
- **Your ship's name and identity are yours to change when you edit it in a save.** Editing your live ship let
  you type a make, model, designation or description into **Ship Info**, saved them with the design, and then
  ignored every one of them at the write: the write-back cloned the ship's identity off the original record, so
  a ship that started life as a Testudo Salvage Tug stayed one whatever you wrote. Two people reported it,
  which is two more than a field that does nothing deserves. The identity fields now write onto the ship, and
  the **The ship** step accepts them instead of showing them greyed.
  - **The import brings the ship's identity with it**, so Ship Info opens on the ship's real make, model, year,
    designation and description rather than on blanks. Editing over something you can see is the point: blanks
    invited exactly the typing that then went nowhere.
  - **A blank in-game name keeps the name the ship has**, rather than falling back to the design name. A ship
    with no stored name gets a fresh random one every load, so blank cannot mean "no name" here.
  - **Review restates the identity** and says whether it changed, since the in-place write is irreversible.
  - The registration (`strRegID`, and the `strName` that mirrors it on a save's ship) is still never touched.
    The rest of the save refers to your ship by it.
- **A Settings window, and UI scaling.** New **⚙ Settings** button on the toolbar (or **Ctrl+,**), holding
  everything that is Ostraplan's own preference rather than part of a design.
  - **UI scale, 100% to 200% in 5% steps.** Magnifies everything Ostraplan draws: toolbar, palette, inspector,
    dialogs, reports and the canvas. It is for a high-resolution monitor run at 100% Windows scaling, where a
    27" 4K panel renders the app's text about a third the physical size a 1080p one does. Thanks to Wekuz for
    raising it. The scale is a layout transform rather than a magnifying glass, so the app lays itself out at
    the larger size and text and vectors stay sharp. It applies as you drag the slider; dialogs and reports
    resize with it (clamped to your screen and re-centred), while the main window keeps the size you gave it.
  - **Theme moved here** from the toolbar combo, and **Mod overrides** from the View menu (it is a rule you
    prefer, not a view you toggle).
  - **The Ostranauts install and Saves folders are now yours to set.** The install folder could previously only
    be picked when auto-detection had already failed. Both show what they resolved to and where that came
    from, and **Automatic** puts either back.

### Fixed
- **A ship name typed in the export wizard sticks.** The wizard's **Ship name** box named that one export and
  was then thrown away, because only the six identity fields flowed back onto the design. The next export
  re-seeded the box from the design and the name you typed was gone. It now flows back like the rest.
- **Saves kept outside the default location are found.** Ostranauts lets you move its save folder, and
  Ostraplan hard-coded the LocalLow path, so anyone who had moved theirs got "No save games found" from every
  import and write-back. It now follows the game's own `strSaveLocation` setting, and Settings takes an
  explicit folder for a location neither of them knows about. Thanks to the Discord report that surfaced it.
- **A relocated Mods folder is read again.** Ostraplan looked for the game's `strPathMods` setting at the root
  of its `settings.json`, which is an array, so the lookup threw and was swallowed and the setting never
  applied. It reads both shapes now.
- **Restarting to update no longer throws away unsaved changes.** Every other way out of Ostraplan — closing
  the window, starting a new design, opening or importing another one — asks whether to save first. The
  update button did not: Velopack ends the process itself, so the window never got the close event that
  carries the prompt, and clicking **Restart to update** discarded the design with no warning. It now asks
  exactly as closing does, and answering **Cancel** cancels the restart rather than only the save. Your
  settings are written on that path too, which they also were not. Thanks to HailePrime for the report.

## [0.68.3] 2026-08-10, auto-save, reports you can work beside, and a warning badge worth reading

### Added
- **The Ship Rating and Diagnostics reports no longer block the editor.** Both open beside the design instead
  of over it, so you can leave one up and carry on placing parts while you read it. That is what makes the
  Ship Rating's **Show** buttons and its **Value Opportunities** list actually usable: the room a hint names is
  highlighted on a canvas you can still work on, which was the request
  ([discussion #22](https://github.com/Valtora/Ostraplan/discussions/22)).
  - A report measures the design as it stood when it ran, and being modal was what used to guarantee it still
    matched the canvas. So it now says when it does not: edit anything and a bar appears across the top of the
    report, with a **Re-run** button that recomputes in place. Running the report again from the toolbar
    refreshes the open window rather than stacking a second one.
  - Opening or importing a different design closes an open report, since its figures, its leak highlight and
    its dead-weight box all belong to the design that produced them.

- **Opt-in auto-save, under File ▸ Auto-save.** Turn it on and Ostraplan takes a rotating snapshot of the
  open design every **10 minutes**, keeping the **3 most recent per design** and rotating out anything
  older. Both figures are yours to set in the same submenu: 1 to 60 minutes, and 1 to 20 snapshots kept.
  The switch is a check box reading **Enabled** or **Disabled**, and turning it off greys out the two
  settings, so the submenu says whether the feature is running rather than leaving you to read a tick.
  - **It never writes your `.oplan`.** Ctrl+S is still the only thing that does. Snapshots go to
    `%APPDATA%\Ostraplan\autosave` and the unsaved-changes star stays up, so an auto-save cannot commit an
    edit you were about to undo, and it cannot overwrite a good file with a mid-thought one.
  - **Each design keeps its own three.** Rotation is keyed on the design's full path, so two ships that
    happen to both be called `Kestrel.oplan` in different folders never evict each other. A design you have
    never saved has no path to key on, so all such designs share one set between them.
  - **Recover auto-save…** lists every snapshot newest first and loads the one you pick as *unsaved
    changes* to the design it came from. Nothing is written until you save, and saving goes back to that
    design's own file, because the snapshot records which file that was. One of a design that had never
    been saved asks you where to put it, as it should.
  - A tick is skipped when there is nothing to record or recording would be wrong: no unsaved changes, a
    design held read-only because its mods are missing (the copy on disk is the complete one), or an
    analysis reading the design at that moment. A snapshot that cannot be written says so once and is
    logged thereafter, rather than interrupting you every ten minutes.

### Fixed
- **Eight core data files no longer fail to load, and the station fittings that depend on them work again.**
  Core writes multi-line descriptions with real line breaks inside a JSON string, which the game's own parser
  accepts and the JSON spec does not, so Ostraplan's stricter reader threw the whole file away. On a stock
  1.0.0.7 install that was `interactions_encounters.json` and seven of the plot files, and it cost twelve
  interactions that real parts reference: the Venus embassy and OKLG medical kiosks, the Venus racing kiosk,
  the express transit door and the Ceres plot crate. They are all SPECIAL-tab fittings, so the damage showed
  up as the **Walk** overlay reading them as having no usable actions at all.
  - Such a file is now mended and re-read rather than dropped. The mend only ever replaces a control character
    with its own escape, which denotes the same character, and it has to parse before it is accepted, so a file
    that is broken some other way still fails honestly.
  - Mended files are listed in the bug-report diagnostics instead of being counted, since nothing was lost and
    there is nothing for you to do about them.

- **The data-warning badge now only reports things you can actually do something about.** Every warning is
  attributed to the source that carries it, and a defect in the **game's own** data is logged and folded into a
  bug report but no longer counted on the toolbar. Ostraplan never writes core data and no player can fix it, so
  standing there as a permanent count it taught you to ignore the badge, which is exactly when the next one,
  about a mod you can disable or update, needs reading.
  - A stock 1.0.0.7 install goes from **9 warnings to none**. The last one was core's `FloorLDPH04AInstall`,
    which installs an `ItmFloorLDPH04A` that core never defines, so that floor variant cannot be offered no
    matter what Ostraplan does. It is in the activity log, where it belongs.
  - Anything a mod brings in still surfaces exactly as before, as does a mod folder or Workshop item named in
    your load order that is not on disk. Attribution is by source identity rather than by name, so a mod calling
    itself "core" cannot pass its own defects off as the game's.

- **Device wiring no longer shows when Wire mode is off.** Committed wires drew whatever view you were in, so a
  thoroughly wired ship stayed criss-crossed with violet lines over the sprites, the room tints and the lighting.
  Only the rings and the drag preview were ever gated on the mode; the wires themselves were not. The wiring is
  now part of Wire mode in full, like every other overlay is part of its own toggle.
- **Wires are drawn heavier**, since a hairline crossing a busy, high-contrast deck at an arbitrary angle was
  easy to lose. The drag preview matches the committed width, so a wire no longer changes weight the moment you
  commit it; the dashes and the lighter tint are what tell the two apart.

## [0.66.0] 2026-08-08, Ostranauts 1.0, the ship checklist, and export art

### Added
- **A SPECIAL palette tab, for the structure the game places but never lets you build.** Asteroid and ice
  cores, regolith walls, floor signs and emblems, station kiosks, embassies, terminals and transit lifts,
  station floors and furniture, and the running states of things like a reactor or a blast door: 139 parts on
  a stock 1.0.0.7 install, plus whatever your mods add. None of them has an install job, which is exactly why
  they never appeared in the eight build tabs, and until now the only way to get one into a design was to copy
  it out of a ship template ([#18](https://github.com/Valtora/Ostraplan/issues/18)).
  - They are ordinary placements once down: they autotile, seal rooms, obey the Law, count towards the rating
    and travel through both export routes. What they cannot do is be *built* — no install job means no install
    kit, so the bill of materials counts them under "not buildable" alongside raw hull and the fixed airlock.
  - The tab is derived, not a hand-written list, so a game patch or a mod that adds one gets it for free. Two
    kinds of def are deliberately left out: a runtime state of something already buildable (a damaged or
    patched wall, a switched-off or locked device), and a def the game data never named, which is a dev or
    test artefact rather than a part.

- **A basic ship checklist, using the game's own Ship Diagnostics tooling.** A new **Diagnostics** toolbar
  button runs the sixteen-row status page the game's nav console prints (`NavModDiagnostics` →
  `ShipStatus.PrintStatus`) against your design, on the game's own pass/fail thresholds: rating code and mass,
  transponder, transponder antenna, nav station, reactor and its helium-3 and deuterium, RCS thrusters, RCS
  distributor, reaction mass, backup power, and the four life-support rows. In game the page is reachable only
  by sitting at a console on a ship that already exists, so until now the way to find out a design had no
  transponder antenna was to build it. Every red row says what is missing and which build tab it comes from,
  and **Copy report** puts the whole readout on the clipboard.
  - The cutoffs are the game's, including the ones people misread: **more than one** switched-on RCS cluster
    (a single thruster can push but not turn the ship), >100 kg He3, >1000 kg D2O, ≥200 kg reaction mass,
    ≥20 kWh backup power, >35 kg O2 stores. They are literals inside the DLL, invisible to data diffing, so
    they are pinned by a test that fails if a patch moves them.
  - Two rows are measured where the game measures them, not ship-wide. **Backup power** is read at the nav
    console's own power inputs, so a battery the conduit network never reaches counts for nothing. **O2
    stores** are the oxygen in the canisters on a pump's gas-input tile, so a hold full of O2 with no pump
    plumbed to a can reads zero — which is what it reads in game.
  - Three rows are answered differently than at a console, because a plan is not a running ship, and each
    says so in the report: **NAV STATION** is a real presence test (the console hardcodes ONLINE, since the
    page is read at it), **TRANSPONDER** shows INSTALLED where the console shows the registration ID assigned
    at spawn, and **REACTOR** shows INSTALLED where the console shows OFFLINE until the reactor is lit — which
    a planned one never is. Quantities are as-spawned, so this is the readout a freshly built or freshly
    bought ship gives.

- **Every ship broker kiosk in the game is now offered**, not the five that existed in 0.15.1.6. Game 1.0 opened
  the rest of the system and there are thirteen station brokers plus four Special Offer slots; the export wizard
  listed OKLG, BCER, BCRS, Venus and VORB and hid the other eight. The list is now read out of the loaded loot
  data rather than written down, so a station a later patch adds, or one another mod adds, appears on its own.
  Stations are labelled by their ATC code, with a name where the world data gives one.

- **An exported mod now ships its own preview art**, so the ship shows a picture wherever the game shows one
  (issue #21). Character creation asks for exactly `images/ships/<ship>/<ship>.png` and has no fallback, so an
  Ostraplan ship offered as a Shipbreaker start drew the game's red missing-image X where its portrait belongs.
  The export writes that file, at the same 800×600 on black the game's own ship editor produces.
  - **Room thumbnails come with it**, one per certified room, named the way the game names its own
    (`BridgeRoom.png`, `Engineering_1.png`) so the broker kiosk can pair each with its room icon. The broker
    listing now reads like a core ship's instead of falling back to a grey silhouette.
  - Framing follows the game: the whole ship centred with a little air, each room centred at a closer zoom with
    the surrounding decks still visible and cut off by the frame. The art is drawn at the design's real
    orientation, not the editor's Q/E plan-view rotation.
  - Re-exporting sweeps the ship's old images first, so a thumbnail of a room a redesign no longer has cannot
    linger in the kiosk. Nothing outside `images/ships/<ship>/` is touched.
  - A replacement export files its art under the ship it replaces, which is what makes the new design's picture
    override the original's.

- **Moved parts have their own price multiplier** on a save edit's **Write target & cost** step (issue #19). The one
  "Cost multiplier" slider became two, **Added parts** (default 2.0× base value) and **Moved parts** (default 1.0×),
  replacing the multiplier's fixed half-price weighting for a move. The defaults reproduce the old pricing exactly,
  so nothing changes until you move a slider.
  - **This is what a modular refit needed.** Extending the nose of a ship, or shuffling a modular block, moves a
    great many tiles without conjuring anything, and the single multiplier billed all of it. Setting **Moved parts**
    to **0×** now leaves only the genuinely new parts on the bill, which removes the two-pass workaround of
    exporting once to move and again to add.
  - Deleted parts stay free, and authored cargo still rides the added-parts multiplier: it is conjured the same way
    a new part is.
  - The cost readout shows each side with its own multiplier rather than one bracket times one number, so the bill
    reads as what it is.
  - The remembered `costMultiplier` setting is replaced by `newCostMultiplier` and `movedCostMultiplier`. A settings
    file written before this has neither, so both sliders start at their defaults.

### Changed
- **The edit cost is a ledger now, not an equation.** The **Write target & cost** step used to state the bill as
  one wrapped line (`( 12 added: $4,300 + 40 moved: $9,100 ) × 2.0× = $17,700`), which is compact and close to
  unreadable. It is now a tally: one row per kind of change, with base value, multiplier and resulting figure in
  aligned right-hand columns, a rule, and a total. Rows for a kind of change the edit doesn't contain are simply
  absent, so a move-only edit shows two lines rather than a row of zeros.
- **A balance meter shows what the edit takes out of your credits.** Under the tally, a bar fills as the
  multipliers rise and reads `Balance $50,000 … Left $30,420` with the share as a percentage. It turns red and
  says how far short you are the moment the cost passes your balance, which is the same point at which Next
  refuses, so the wall is visible before you hit it rather than only once you try to move on.

### Fixed
- **Loose items now export onto the tile you dropped them on.** An item from the Items palette was written into
  the exported ship in the editor's own tile coordinates, while every part around it was written in the file's
  grid coordinates. So the whole scattering came out displaced by one fixed amount: the design's bounding box
  corner, minus the one-tile margin the game pads around a ship. That offset happens to be zero only for a design
  whose parts begin at tile (1, 1), which is why items landed a couple of tiles off the corner or the centre of
  the room they were placed in, and stayed the same distance off wherever else they were put. A stack keeps every
  copy on the head's tile. (#20)
  - **Writing a design into a save was never affected**, nor was the editor view. Only an exported mod ship, and
    the kiosk and Special Offer copies built from one.
  - **A displaced item could take the ship's rooms with it.** The game rebuilds a ship's tile grid on load by
    padding one tile around every free-standing item, and a loose item counts even though it is structurally
    inert. One shoved past the hull's own margin therefore widened the grid, and the room and zone tiles baked
    into the file are flat indices that then decode a column further along on each row: rooms binding to the
    wrong tiles, zones sliding. Only designs with an item near an edge were exposed, and it took a full load to
    show, which is why it read as a cosmetic offset. The export now checks its own declared grid against the one
    the game will rebuild.

- **An exported ship no longer weighs nothing and no longer reads as having no thrusters.** The block of
  shallow-load state every core template carries (`fShallowMass`, `fShallowRCSRemass`/`Max`, `nRCSCount`,
  `nRCSDistroCount`, and the torch figures) was declared on the export but never filled in, so it went out as
  zeroes. That is what printed "Mass: 0 (kg)" and "RCS Count: 0" on the character-creation and kiosk spec sheets,
  and it went further than cosmetics: the game refuses RCS flight outright when the thruster or distributor count
  is zero, so a copy of the ship that had not been fully loaded yet could not manoeuvre. Every figure is now
  baked from the same propulsion analysis the design's own report is built on.
  - The design's **expected haul mass** is deliberately left out of the ship's mass. It is a planning input for
    the acceleration report, not something the ship weighs.

- **Uninstalling a part you already own is no longer billed as building a new one.** "Make Loose Item", "Install
  item" and toggling a door open or shut all rebuild the part under a different def, and that dropped its link to
  the save item it came from — so the edit cost saw a free deletion plus a brand-new part, and charged the full
  added-parts price for a fixture the player already had. They now price on the **moved** multiplier, which is why
  that slider reads **Moved or un/installed parts**.
  - **The link can't simply be kept**, which is why this was wrong in the first place: the write-back reuses the
    save's own item record for a kept or moved part, and that is impossible once the def has changed. The part now
    records *where it came from* separately, so the save is written exactly as before while the cost model can tell
    a re-stated part from conjured material.
  - **Uninstalling and re-installing the same part is free.** Swapping back to the def it started as restores the
    save identity outright, so a change of mind costs nothing rather than being billed as a move.
  - The counts line and the done report name these parts separately (`… · 3 un/installed · …`) instead of reporting
    one uninstall as an addition *and* a deletion.
  - **Replacing a part with a genuinely different part is unchanged**: a re-skin or a "Replace with…" is new
    material and still prices as added.
  - A `.oplan` gains optional `swappedFrom` / `swappedFromDef` fields on a part. Additive, so an older design
    still loads; its re-stated parts just price as they used to until they are swapped again.

### Documentation
- **Re-verified against Ostranauts 1.0.0.7** (Steam build 24535205), the first pass since 0.15.1.6.
  `GameEnv.VerifiedGameVersion` and every per-system claim in
  [docs/GAME-INTERNALS.md](docs/GAME-INTERNALS.md) now say 1.0.0.7 because each was actually re-read against
  that build, not because the string was updated.
  - **The ported logic held up.** The rating cutoffs and size classes, the coordinate mapping and its
    away-from-zero rounding, the room flood fill, room certification, the placement law and its docking
    envelope, ship value down to all eleven gas molar masses, the condition-trigger evaluator, the sixteen-entry
    autotile table, all eleven propulsion constants and both fusion formulas, the power flood, walkability, line
    of sight, and the export's contained-item retention gate are unchanged in 1.0.0.7.
  - **The compiled shaders were re-extracted too**, not just the C# side. `Sprites/LoSPass` still computes
    `1/(F²(d²+Z²)+0.1)` with `F = 3` and `Z = 0.25`, still decodes normals as `2r-1`, and still blends
    `OneMinusDstColor One`; `Sprites/DefaultAdditive` still blends `SrcAlpha One`. Light Viz needs no change.
  - **The corpus grew from 192 ship templates to 220.** The figures measured off it were re-measured: the
    common template field set is 56 rather than 54, 177 of 220 ships carry boarding spawners rather than 150 of
    192, 237 of 482 baked void rooms carry a non-zero room value, and the derelict size bands shifted slightly
    (Medium now reaches 2,509 parts, Big 5,852 with a median of 2,323). Rooms parity is 219/220.
  - **Three room-parity exclusions were retired** because the game's data changed under them: Coffin.json was a
    malformed template and has been rebuilt, ResAero01.json no longer has a slant wall at all, and Ostrich
    A8R.json still has four but the game now files them the way a plain flood fill does. Only the Vector2
    interceptor's airlock still fails, for the reason already recorded.
  - A stale test threshold went with it: the Babak loader test asserted a part count above a fixed 4,000, which
    only ever meant "everything resolved" while that template stayed the size it was. The game has since
    redesigned it to 3,985 items, so the test now asserts what it always meant.

- **The project's scope is written down** in a new [docs/SCOPE.md](docs/SCOPE.md). Ostraplan designs ships and gets
  them into your game, and that is the whole remit. The doc gives the test that settles nearly every request
  ("does a *design* go into it?"), what is in, what is deliberately out and why, and worked examples either side
  of the line. The README carries a shorter version and the feature-request form now points at it, so a request
  outside the boundary gets an explanation rather than silence.
- **Build and release instructions moved out of the README** into a new
  [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md). Publishing to GitHub is a maintainer step, not something a reader
  of the README needs. It also now records the WPF `BG1002 App.baml` build quirk, the versioning rule, and the two
  traps in the Velopack release flow. The README keeps a two-line "build and run".
- **Documentation reconciled with the code.** The README claimed Light Viz was on by default (it has started off
  since 0.52.0), listed the palette tabs without the FAV/REC tab added in 0.51.0, miscounted the tabs, described
  the obtainable-in-game routes without derelict fields, and pointed at the wrong menu for *Check for updates*.
  `docs/OPLAN-FORMAT.md` was missing the `extraMassKg` field entirely. usage.md gains the FAV/REC tab and a
  Snapshots section (the PNG/SVG note had drifted into the middle of the save-injection walkthrough).
- **The policy docs are reachable.** CODE_OF_CONDUCT.md and SECURITY.md were linked from nowhere; the README and
  CONTRIBUTING now link them.

## [0.61.1] 2026-08-02, The export wizard and ships into saves

### Added
- **Spawn a design as a derelict.** A mod export can now scatter the ship through the salvage fields as a wreck to
  be found, alongside the existing kiosk, Special Offer and starting-ship routes. `star_system.json`'s
  `aSpawnDerelictRings` names an ordinary `strType: "ship"` loot pool per ring, so a derelict pool takes exactly
  the override a broker kiosk does.
  - **It reaches a new game only**, and says so plainly. Rings are filled when the world is generated, so a save
    you already have will never grow one.
  - **The wear slider turns itself off** for an export aimed only at the fields. Being a wreck is not in the ship
    file at all: all 192 core ship templates carry `DMGStatus = 0`, and the spawner marks the ship derelict and
    lets `Ship.BreakIn` damage it on load. Baking wear on top would double-damage every part. Move the slider
    yourself and your choice stands.
  - **Venus writes to the right pool.** `RandomDerelictVenus` has an empty `aCOs` and delegates to
    `RandomScavShipVNCA` and `RandomScavShip`, so it is a chooser like `RandomDerelict` rather than a leaf. The
    VNCA pool is the honest target.
  - **The size bands overlap, and Ostraplan says so** instead of pretending otherwise. Small runs 107 to 800
    parts, Medium 319 to 2508, Big 520 to 5853, so no threshold separates them. Each band's real range is shown
    and the nearest median is suggested. Measured against 0.15.1.6 and recorded in
    [docs/GAME-INTERNALS.md](docs/GAME-INTERNALS.md) §19.

### Changed
- **Export is now a wizard.** The dialog it replaces was one long scrolling panel with the two destinations behind a
  `TabControl`, and those tab headers were near invisible against the dark background. The wizard removes tabs
  entirely, so that problem goes away rather than being restyled around. Step one asks where the design should go,
  and the rail down the left then shows only that destination's steps: the mod path's twenty-odd controls never
  appear on a save export, and vice versa. All destinations share one **The ship** step, because the name, the
  in-game identity and the condition are exactly what all of them want.
  - **Review builds before anything is written.** It runs the real engine and reports the real outcome: part and
    room counts, the rating, placement warnings, the price against your balance, and exactly where the write will
    land. The commit that follows performs only the write. This matters because several of those facts are only
    knowable after the build, which is why the flows this replaces confirmed them *after* running the engine.
  - **Wear is pinned at Review.** Wear rolls per part from a time-based seed, so a rebuild at commit would have
    damaged different parts from the ones Review described. The seed is now fixed when Review builds and reused by
    the write, and the two save destinations skip the rebuild altogether by writing the artifact Review produced.
  - **Destinations that can't be used are shown disabled with the reason**, not hidden. Hiding a feature teaches
    nobody that it exists, and the reason is usually the actionable part.
  - **Done is a pane, not a popup.** The three `Dlg.Success` boxes are gone; the result stays where the run
    happened. Anything the export would overwrite or delete is now an acknowledgement checkbox on Review, beside
    the facts that justify it, rather than a separate dialog on top of them.
  - **Your last export is remembered** (destination, wear, kiosk choices, price, write target). Reopening starts
    at the first step, with every step that still holds already marked done, so the rail lights up and Review is
    one click away: a repeat export stays quick without the wizard opening one click from a write. Every step is
    revalidated first, so if the save was deleted or the output folder is gone it opens on the step that can say
    so, and the rail will not skip past it. These live in Ostraplan's settings, never in the `.oplan`, so a shared
    design carries no local paths, save names or credit amounts.
  - **The rail is a control, and now looks like one.** Any completed step, and anything behind you, jumps straight
    there on a click, and highlights on hover so you can tell.
  - **A mod export now requires a way to get the ship in game.** With no kiosk, Special Offer, start or derelict
    field ticked, the mod writes a ship file nothing will ever spawn, which is the commonest first-time mistake and
    invisible until the ship fails to turn up. The wizard refuses instead. A bare ship file is still a legitimate
    output for a modpack or hand-wired loot, so **No route: I'll wire it up myself** sits under an **Advanced**
    disclosure: enough friction that it cannot be the accident, none that it cannot be the decision. The section
    opens on its own when the step is empty, which is exactly when it is the thing you need, and stays shut once
    there are routes in it. Ticking a real route clears and disables it, since the two contradict.
  - **Blocking design problems reach Review.** `ProblemScan` has always rated problems as blocking or warning, and
    nothing outside the PROBLEMS list ever acted on the distinction, so a design flagged "No docking port" could be
    exported without a word. Each one is now an acknowledgement you tick before the commit arms. They acknowledge
    rather than refuse because a blocking problem is not equally fatal everywhere: a hull with no docking port is a
    broken purchase and a perfectly good derelict.
  - **Updating a ship in a save is now the wizard's third destination.** `Analyse ▸ Update Ship in Save…` survives
    and opens the same wizard with it preselected, so the muscle memory still works. It has no save picker, because
    the design already names the save and the ship: selecting the destination re-locates that ship, and a save that
    has moved or been deleted says so there rather than after the build. Identity is read-only on this path and
    shown greyed with the reason, because a save edit rewrites the ship's structure and not who it is.
  - **Missing parts is its own step**, update-only and only while something is unresolved. A stand-in is a real
    edit to the design rather than an export setting, so cancelling the wizard afterwards asks whether to keep it.
    Leaving parts unresolved is still allowed, and now shows as an acknowledgement on Review instead of a separate
    warning dialog. Dropped cargo works the same way.
  - **The in-place overwrite keeps its confirmation.** It is irreversible, and it is the only step that can
    usefully check whether the game is running, which Review cannot do minutes earlier.
- **The mod folder's overwrite check now looks at the folder the export will really write**, resolved through the
  mod name rather than assumed from the ship name. A customised mod name previously had its warning checked against
  a folder the export was not going to touch.
- **Picking a save no longer reads it on the UI thread.** The read parses the save's largest record.

### Added
- **Add a design to a save as a new ship** (issue #17). **Export** is now two destinations rather than one: the
  familiar **As a mod** tab, and a new **Into a save game** tab that writes the design into a *copy* of a save as a
  brand-new ship you already own, without touching any ship that is already there. The point is the gap the issue
  names: until now the only ways to get a design in front of you either rewrote an existing ship or edited the
  world's kiosk loot. The ship's name and identity, and its condition, are shared between both tabs, because both
  destinations want exactly those.
  - **It arrives where a bought ship would.** The game's own "you bought a ship and the station has no free port"
    path scatters the ship 3 to 5 km out at a random bearing, and that pair of radii is ported literally, so a
    granted ship is parked exactly where a purchased one lands. That is 1% of the P.A.S.S. ferry's 5,000 km range,
    so you can call a ferry to board it, and far enough out that even an RCS-only hull flies home in minutes.
    Docking it to the station is deliberately **not** attempted: it needs reciprocal entries on both ships and a
    formation the game computes from port geometry, which is a large surface for a small convenience.
  - **You own it properly.** Ownership in Ostranauts lives in two places and needs both. The character record's
    `dictShipOwners` is what the ferry and the broker read, and the player's `aMyShips` is what `OwnsShip` reads,
    which gates crew pledges, fast-forward and every interaction that tests whether a ship is yours. Write only the
    first and the ship is reachable but your own crew will not work on it.
  - **Price it, or gift it.** Tick the charge box and the cost comes off your character's balance (the authoritative
    `StatUSD` on the player CO, mirrored into `saveInfo.money`), with the balance shown live and the button disabled
    when you cannot afford it. Left unticked it is free.
  - **Your save is never written.** The result is a new `<save> (Ostraplan)` folder; the original is not opened for
    writing at all. The one edit inside the character record is done textually, inserting two array entries, so a
    60 MB record is not round-tripped through a serialiser to add two strings.
  - Designs imported from a save can be granted too, which is a save-to-save ship transfer: import from one save,
    add to another. Layout, cargo, loose items, zones and device wiring all survive that trip. Per-part damage and
    crew do not, and the dialog says so.

### Fixed
- **Ship Rating ran in release builds but not development ones.** The rating's progress dialog is a local of the
  same scope as the analysis lambda, and the reporter's own lambda captures it, so the compiler files both in one
  closure. The UI-capture guard walks that closure, found a dialog in it, and threw before the analysis started —
  so a Debug build logged an error and rendered no report. Nothing actually touched the dialog off-thread
  (`Progress<T>` posts back to the UI thread's synchronization context), so this is the guard's documented
  opt-out case and is now marked as one. Release builds strip the guard, so shipped versions were unaffected.
- **WalkViz: crew access on the plan** (issue #14). A new overlay (the **Walk** toolbar button, or **K**) tints
  every tile crew can stand on by which connected area it belongs to, so two tiles sharing a colour are reachable
  from each other on foot and two colours mean there is no route. This is what catches a compartment you have
  quietly walled yourself out of. It needs no simulation: walkability is a pure function of the tile conditions
  Ostraplan already computes, ported from the game's own `Tile.IsWalkable` and the adjacency rule of its
  jump-point search (a diagonal only passes when one of the two orthogonals it cuts between is open).
  - **Fittings you cannot operate are named and ringed.** The game does not let a crew member use a device from
    anywhere convenient: they must reach a specific point on it, within *that interaction's* own range, with line
    of sight. Those ranges are per-interaction and vary a lot (a nav console is 0 tiles, an air pump 1, a cooler,
    heater, bed or sensor 2, a reactor 3), so a cooler boxed in by a bench is unusable however close you can get.
    Unreachable fittings are ringed in red **on the fitting itself**, so clicking the mark selects the part at
    fault rather than whatever happens to own the tile in front of it, and the Law report lists them by kind with
    counts. Line of sight is the game's real test against the same occluder boxes Light Viz uses, so a window
    passes and a canister does not. Where the crew may stand is deliberately as loose as the game is: the band
    rounds **up**, so a 1.5-tile interaction reaches two tiles; standing on a fixture is a last resort rather than
    a refusal, so a cargo bay floored wall-to-wall in racks is still usable; and a fitting embedded in the hull
    (sensors, wall lights, ship weapons) is not blinded by the wall it is mounted in.
  - **Door state is not cosmetic here**, unlike for rooms and the rating. An unpowered, locked or damaged closed
    door carries `IsPortalStuck` and is a solid wall to pathing; a powered one crew simply open, so it still joins
    both sides. Toggling a door shut can therefore split the ship in two, and now shows it.
  - **"Suit up" is told apart from "impossible".** Hull-mounted kit (lift rotors, external cargo pods, some
    sensors) has no interior tile to work from, and that is simply how it is reached. Anything only usable from
    outside is dashed amber and left out of the Law report; solid red and a report entry mean nobody can operate it
    at all. A doorway with vacuum on one side is dashed the same way: crossable, but only in a suit. Pressure is
    read from the compartments either side rather than from a gas simulation, so it is advice, not a wall.
  - **Mineable rock and ice are terrain, not fittings.** Regolith carries a "Mine" action, so it looks like an
    operable device; a block in the middle of an asteroid being unreachable is what rock *is*. Without this the
    core Port Mojave alone reports 1,811 unusable "devices" and buries the two findings that matter.
  - **Spacewalks are a switch, not an assumption.** The game counts the hull exterior as walkable (walking needs no
    floor at all), which strictly makes almost every design one big zone. Interior routes are what the overlay
    shows by default; **View ▸ Walk overlay ▸ Count spacewalks** takes the game literally. The same menu chooses
    whether painted **Forbid zones** apply, since the game's test is per crew member and a plan has no crew. Both
    settings persist.
  - Verified against game **0.15.1.6** and pinned by tests, including live-data assertions that the four stuck-door
    defs still carry `TILPortalClosedStuck` and that the interaction ranges above have not moved.
- **Propulsion figures on the Ship Rating report** (issues #15 and #16). The game works all of this out, but shows
  it in exactly one place: a nav console, on a ship you have already built. The report now has a **PROPULSION**
  block giving **RCS acceleration in G**, **RCS delta-v**, **torch acceleration in G** and **reactant remaining in
  hours**, so "have I enough thrusters", "have I enough intakes" and "have I enough laser-feeder pairs" are all
  answerable from the plan.
  - **Reaction mass is a plumbing question, not an inventory one.** Only tanks sitting on an installed
    distributor's gas-input points count, exactly as the game's own `GetRCSRemain` reads them, so a canister in a
    rack is correctly worth nothing. Any airtight tank qualifies and **every gas counts by mass**, which is how the
    Katydid runs its RCS on O2; capacity is still priced as an N2 refill, which is what a fuel kiosk sells.
  - **Delta-v does not care how many thrusters you fit.** The thruster count cancels out of the game's own
    expression, so it is set purely by reaction mass over ship mass. More thrusters buy acceleration and no range
    at all, and the report says so, because it is the opposite of what most people assume.
  - **Torch thrust and burn time both scale with the reactor's pellet ceiling**,
    `2 × min(min(feeders, 2×regulators), min(lasers, 2×capacitors))`. A laser with no capacitor to drive it, or a
    feeder with no fuel regulator, contributes nothing, and the report names which side is capping you.
  - **A "dead weight to haul" box** for tugs and salvage runs, which lands where the game puts a docked ship's
    mass, and is **saved with the design**. It is dead weight in the literal sense: not fuel, so it adds no
    reaction mass and every figure only gets worse as you raise it.
  - **Every zero says why.** No distributor, distributor switched off, tanks not plumbed in, a reactor with no
    capacitors, no helium-3 aboard: each names the missing link instead of printing a bare dash. (The smaller
    `LHe01` tank is the cryo feed and holds no reactant, which the report will tell you rather than let you guess.)
  - Verified against game **0.15.1.6** and pinned by tests: every ported constant, the module pairing, and the
    gas-input geometry across the shipped fleet (108 of the 111 core ships carrying a distributor resolve a real
    fed RCS system through the same code path).

### Fixed
- **Overhead lights are placeable again** (issue #11). The ceiling lights (`ItmLitCeiling1x1` family) are the only
  buildable parts whose socket rules demand a power conduit on an adjacent tile, and the planner was hard-blocking
  every one with the cryptic reason *"needs IsPowerConduit"* — so with no conduit in the design, no light could go
  down anywhere and the tool never said why. That conduit rule is real, but only the game's *interactive* builder
  enforces it: every dev-authored / spawned ship (the core Baleen carries 31 ceiling lights and **zero** adjacent
  conduits) hangs them freely and wires them through the electrical graph. Since a planner produces spawn-placed
  ships, an overhead light now **places** where a conduit is missing and is flagged with a gentle, dismissible
  advisory — an amber ghost reading *"⚠ places, but no power conduit adjacent"* plus a Problems-panel warning that
  points at the anchor tile — instead of a hard, unexplained block. Drop a POWR conduit on the adjoining tile (or
  rotate the light to face an existing one) and the flag clears.
- **"Use as brush" hands you the part at the angle you picked it** (issue #13). The eyedropper (**Alt+click**, or
  the right-click menu) took only the part's *def* from the tile you clicked and left the brush at whatever angle
  it was last rotated to, so picking a canister that sits at 270° could arm it facing some other way. It now adopts
  the picked part's rotation as well, which is what pointing at a thing and saying "that one" ought to mean.
  The brush angle is still **sticky across parts** on purpose, so a run of consoles can all be painted facing the
  same way without re-rotating each time, and that is exactly why the rest of this entry exists: it needs to be
  visible rather than deduced from the ghost.
- **The brush's rotation is now shown in the status bar**, beside the view's, whenever a part that can turn is
  armed, **and on the ghost itself**: a compass needle runs from the centre of the footprint out towards the part's
  leading edge, in the same green/amber/red as the validity outline and over a dark halo so it stays readable on a
  busy sprite. It is drawn at every angle, 0° included, so it reads as "this is which way it faces" rather than as
  a warning. A carried-over angle used to be invisible until you noticed the ghost looked wrong. Walls and floors
  get neither cue, because they autotile rather than turn.
- **Holding R no longer spins the brush.** The rotate key acted on auto-repeat, so a key held a beat too long ran
  through several 90° steps, and the angle it happened to land on then followed you onto the next part you armed.
  One press is now one 90° step, as **Ctrl+R** and the flip keys already were.
- **The activity log records the brush's angle**, both when a part is armed and when R turns it. A report about a
  part that went down facing the wrong way can now be read back from the log, which was not possible for issue #13.
- **A modded wall or floor can no longer be drawn at a rotation it will never place at.** Sheet parts autotile
  instead of turning, and the placement law, the stored pose and the footprint maths all pin them to 0 by reading
  `bHasSpriteSheet` alone. The renderer was instead keying off `bHasSpriteSheet` **and** a `ctSpriteSheet`, so a def
  that declares the first without the second (no core part does; a mod may) fell through to the rotated path and
  would ghost turned while placing straight. It now uses the same rule as everything else.

## [0.52.0] 2026-07-21, Smoother Light Viz and toolbar view toggles

### Added
- **View overlays are now toolbar buttons, and they highlight while active.** The overlay toggles that used to
  hide inside the **View ▾** menu (**Zones · Rooms · Power · Light · Wire mode**) are promoted to buttons on the
  main toolbar, each lighting up in the accent colour while its view is on, so what you're looking at reads at a
  glance without opening a menu. Their keyboard gestures (Z / C / P / L) are unchanged, and the highlight stays in
  step however you toggle. The **View ▾** menu keeps the non-overlay items (fit, symmetry, Light Viz dimming, mod
  overrides).

### Changed
- **Light Viz now starts off.** The plan opens on the flat sprite view instead of the in-game lighting, so a new
  or freshly opened design no longer greets you with a black, unlit airlock. Turn it on with the **Light** button
  or **L** whenever you want the lighting preview.

### Fixed
- **Light Viz no longer flickers while you edit.** Manipulating parts with the overlay on used to flash the ship
  unlit: it dropped to flat sprites for the duration of a drag, and every edit briefly cleared the lit image to a
  black silhouette while the new one recomputed off-thread. The ship now keeps its lit look throughout a
  move/paint drag (the in-flux part draws live over the retained lit backdrop), and the composite is held on
  screen and swapped in place when the recompute finishes, so there is no unlit flash between edits.

## [0.51.0] 2026-07-20, Favorites and Recent

### Added
- **Pin the parts you use most, and grab the ones you just placed** (issue #10). The part palette gains a new
  **FAV/REC** tab at the front with two groups:
  - **Favorites** — click the ☆ on any palette row (or right-click a placed tile / loose item ▸ **Add to
    Favorites**) to pin it. The star fills gold, and the part is one click away from any tab, forever.
  - **Recent** — every part you place is recorded here automatically, newest first (the last 8), so re-using
    something you just built no longer means searching for it again. Favorited parts are left out of Recent
    (they already have a home in Favorites), and reappear there if you unpin them.
  Both lists persist across sessions, honour the search box, and — for returning users with pins — the palette
  opens straight to the FAV/REC tab. First-timers still land on the full catalogue.

## [0.50.0] 2026-07-19, Traceable bug reports

### Changed
- **Bug reports capture far more, and the activity log finally says what and where.** Two long-standing
  weaknesses in **Help ▸ Report a Bug**:
  - The report carried only the last ~25 activity-log lines (all that fits in a GitHub issue URL). It now
    also writes a complete diagnostics file to `%APPDATA%\Ostraplan\reports\` and reveals it in Explorer, so
    you can drag it into the issue to attach it — no size limit. That file bundles the **whole session's
    activity trail**, the tail of the crash log (`error.log`, previously never included in a report), and any
    catalog load warnings, all scrubbed of your account name and file paths. A best-effort slice is still
    folded inline so a report is useful even un-attached.
  - Activity-log entries were bare command names (`Edit: Place`, `Edit: Rotate`). They now name the part, its
    tile and rotation, and batch counts — e.g. `Edit: Place Nav Station @(12,7) r90`, `Edit: Remove ×3 (Wall,
    Nav Station)`, `Edit: Move Wall by (+3,-2)`, and a form swap as `Remove … + Place …`. Unhandled crashes
    now also drop a `CRASH:` marker into the trail, so a report's timeline shows the crash beside the actions
    that led to it.

### Fixed
- **"Make Loose Item" now works on the Nav Station and Transponder** (issue #9). These fixtures describe their
  uninstall drop with `strLootOut` pointing at a runtime-only marker (`ItmStationNavLooseEmpty`,
  `ItmTransponder01LooseChance`) that has no condowner, so Ostraplan couldn't resolve a loose form and the
  right-click **Make Loose Item** action was silently unavailable — even though the game lets you uninstall them.
  The loose form is now recovered from the inverse of the fixture's own install job (the real packaged def, e.g.
  `ItmStationNavLoose`), which is guaranteed to render and round-trips with **Install item**.

## [0.49.0] 2026-07-19, Installer + automatic updates

### Changed
- **New installer and automatic updates (Velopack).** Ostraplan now ships as a proper per-user installer
  (`Ostraplan-win-Setup.exe`, no admin) plus a portable zip (`Ostraplan-win-Portable.zip`). Once installed,
  new versions download in the background on launch and the toolbar shows a **Restart to update to vX** button;
  the update applies only when you click it, so unsaved work is never discarded. Installs into
  `%LOCALAPPDATA%\Ostraplan` with an Add/Remove Programs entry; your settings and activity log stay in
  `%APPDATA%\Ostraplan` and survive updates and uninstalls. Not code-signed yet, so the first run of the
  installer shows a one-time SmartScreen prompt (More info ▸ Run anyway).
- The old opt-in self-install (a copy into `%LOCALAPPDATA%\Programs\Ostraplan` with hand-made shortcuts) and the
  browser-download update prompt are gone, replaced by the above. A pre-existing self-install is tidied away
  automatically the first time the installed copy runs.

## [0.48.0] 2026-07-18, Light Viz by default + smooth zoom

### Changed
- **Light Viz is now ON by default.** The plan opens showing the in-game lighting; press `L` (or View ▸ Light
  overlay) to switch back to the flat fully-lit view. The toggle stays session-only.
- **Smooth fine-grained zoom.** The wheel and `+`/`−` now zoom in 0.1× steps (of the 16 px/tile native scale)
  instead of jumping through a coarse step table, and holding **Shift** accelerates to 0.5× per notch. Range is
  unchanged (0.125×–8×); fit/focus framing snaps to the same lattice.

### Added
- **Light Viz rotation regression tests + headless diagnostics.** A report of wall-light glows drawn perpendicular
  to their wall traced back to a stale pre-release binary, not the shipped code; the investigation re-verified the
  full pipeline against real designs headlessly. The decal/normal rotation contract is now locked by unit tests
  (`GlowRotationTests`), and env-gated diagnostic dumps (`LightDebugDump`, `LIGHT_DUMP_DIR`/`LIGHT_DUMP_OPLAN`)
  can render any .oplan's light scene to an image off-app for future per-patch verification.

## [0.47.0] 2026-07-18, Light Viz goes pixel-exact

### Changed
- **Light Viz is now pixel-exact with the game's renderer.** The whole lighting pipeline was reverse-engineered
  from the shipped build — the `Visibility` shadow-mesh geometry from the decompiled DLL, and the actual falloff
  math from the disassembled `Sprites/LoSPass` GPU shader — and re-implemented as a software renderer at the
  game's native 16 px/tile. What changed on screen:
  - **Occlusion is the game's real occluder data (`aShadowBoxes`), not "walls block".** Windows are glass and let
    light through; thin/aero walls don't block light at all; an open door spills light through the doorway (only
    its end caps block); and beds, LH/LHe canisters, reactor pods, stabilizers and docking frames DO cast shadows
    (while being fully lit themselves), exactly as in game.
  - **Wall faces are lit.** Light penetrates half a tile into a wall face (the game's skirt extrusion), so hull and
    room walls catch the light of the room facing them.
  - **The exact falloff curve.** Brightness = `colour × alpha × N·L / (9·(d² + 0.0625) + 0.1)` with `d` the
    distance over twice the radius — the disassembled shader, not an approximated gradient. Real lamps reach their
    true radius (18 tiles for ceiling/wall lights).
  - **Normal-mapped relief.** Every item's `strImgNorm` normal map is baked and shaded per pixel, so walls and
    fixtures catch light directionally, like in game.
  - **Soft light stacking.** Overlapping lights accumulate with the game's screen blend (`OneMinusDstColor One`) —
    they saturate gently toward white instead of blowing out.
  - **Lamp glow decals.** Each light's additive glow sprite (`strImg`) is drawn over the lit scene, the halo the
    game shows on lamps, alarms and status LEDs.
  - **Unlit means black.** In game, the visible ship IS the sum of its lights (ambient is black and the sprite
    layer isn't even drawn); Light Viz now shows the same truth. The Brightness / Unlit black sliders are gone —
    the overlay is game-exact, and toggling Light Viz off returns to the plain fully-lit view.

### Added
- **Exterior daylight (View ▸ Light Viz).** Pick a parallax location (Deep Space, Venus Atmosphere, …) and the
  sun angle, and the location's real sun lights (radius-1000 lights from `data/parallax`) shine on the design —
  occluded by the hull, streaming through glass windows, exactly as the game lights a docked ship. Both settings
  persist.
- **The status bar now shows the size of a box selection (#8).** As you drag out any rubber-band box — a band select,
  a Shift+drag box fill, or a zone box — the bottom bar reads out its live dimensions as "W × H tiles" next to the
  tile coordinate. Handy for measuring room interiors as you build them. The readout clears when you release.

### Fixed
- **Wire Mode no longer strands the item in your cursor (#7).** While Wire Mode was on, a right-click only cleared the
  armed wire source, so a palette brush picked up beforehand couldn't be put down — left-click wires devices (intended)
  and right-click did nothing to the held item. Right-click now discards the held brush first (then the wire source),
  and Esc drops a held brush before touching the wire source or leaving the mode. Placing is still disabled in Wire
  Mode by design; only discarding was broken.

## [0.46.0] 2026-07-18, Light Viz: see how your ship will be lit before you build

### Added
- **Light Viz, an interior-lighting overlay (`L`).** Simulates how your ship will be lit in game, so you can place
  lighting fixtures and judge the result before you build. Each light the game would cast (ceiling and wall lights,
  floor strips, grow lamps, the TV, and the small coloured status LEDs on devices) is shadow-cast from its exact
  position and stops at walls, so rooms light smoothly and a doorway throws light into the next space. The ship is
  rendered **the way the game does it** — multiplicatively (final = sprite × light) — so lit areas show the real
  sprite, bright, and only genuine shadow goes dark, rather than washing the plan with a flat overlay.

  Two controls on the View ▸ **Light Viz** submenu (drag the slider or type an exact value, both persisted):
  **Brightness** (how strongly a light lifts its area) and **Unlit black** (how far unlit areas darken — 0 keeps the
  ship full-bright with just a glow, good for editing; push it up for the true in-game dark look). Computed
  off-thread only while the overlay is on, exactly like PowerViz and RoomViz. This release covers **interior**
  lighting; exterior sun/star light through windows and airlocks is a later addition.

### Fixed
- **Cargo pods (and other interlocking parts) can be stacked again.** Building a cargo train worked in the game
  but not in Ostraplan: the second pod's ghost lit up green, yet the click placed nothing. Two cargo pods attach
  by overlapping a single row (the lower pod's top edge shares the upper pod's bottom wall), and the planner's
  anti-double-paint guard was rejecting *any* overlap of a same part, so the one placement the game actually
  allows was the one Ostraplan refused. The guard now only skips an exact duplicate (the same part already at the
  same tile and rotation, which is all it was ever meant to catch while dragging), leaving the placement law
  (`CheckFit`) the sole judge of overlaps. Reported as
  [#6](https://github.com/Valtora/Ostraplan/issues/6).

## [0.45.0] 2026-07-17, save edits no longer corrupt the ship, plus RoomViz

### Added
- **RoomViz, a rooms overlay (`C`).** Shows every compartment the way the game will flood-fill it: each one tinted
  in its own colour, labelled with what it certifies as, how many tiles it has and what it is worth. A room that
  certifies as nothing tells you **why** right there on the plan — what it still needs, and which item sitting in it
  **blocks** the spec. That last one is the classic silent failure: a gas canister parked in an otherwise-perfect
  quarters keeps it Blank and costs you the room's value, with nothing on screen to say so. Unsealed compartments
  are red. The exterior isn't tinted, so a room that is open to space simply loses its tint.

  It is the same certification the Ship Rating report runs, just live on the canvas, and like PowerViz it only
  computes while the overlay is on. Also on the View menu.

- **Missing-mod parts are now flagged loudly, and you can stand a real part in for them.** When you import your
  ship for editing and it uses parts from a mod you don't have loaded, Ostraplan can't see those items at all —
  but they're still sitting in your save. Everything Ostraplan works out (rooms, the ship's grid, the rating) is
  based on what it *can* see, so a missing modded **wall** means it runs a room straight through where that wall
  stands, and a missing part at the hull edge throws the grid out. Either one can hand you the same ghost rooms
  and shifted zones this release fixes below.

  So instead of a small note after the fact, you now get a proper prompt listing exactly what's missing, with
  the option to pick a real part to take each one's place. A stand-in **replaces** the item in the save you write
  back (the modded part isn't kept), which the prompt says plainly — that way what you see on the canvas is
  exactly what lands in your save. Best fix is still to enable the mod and re-import, and the prompt says so
  first. If you'd rather leave them alone, you can, and **Update Ship in Save** will warn you once more before
  writing.

  Stand-ins need no bookkeeping: delete one and you're back to leaving the modded item untouched, move one and it
  still replaces its original, and both survive saving and reopening the `.oplan`.

- **You can now drop missing-mod parts and get on with it.** Opening a design whose mods aren't loaded still holds
  it read-only, because saving would rewrite it without those parts and you probably didn't mean that. But if you
  *did* mean it — you've moved off that mod and want the parts gone — Save now offers exactly that, and confirming
  drops them, clears the warning, and hands you back a normal design. Previously the only way out was to enable
  the mods again, even when you never wanted the parts back, which wasn't a decision Ostraplan should have been
  making for you.

### Fixed
- **Editing a save no longer leaves ghost rooms behind, or skews your zones.** Two separate bugs, both of which
  corrupted the ship on load. Thanks to @Maddremor for the report and, crucially, for the two saves (one clean, one
  broken, same ship) that made this a five-minute diff instead of a hunt.

  **Ghost rooms.** In Ostranauts a room *is* an object: the game backs each one with a hidden `Compartment` and
  saves the room under that object's id. Ostraplan rebuilt the room list with brand-new ids on every save edit, but
  left the old `Compartment`s in the file. On load the game couldn't match them up, so it minted a fresh room for
  each one and the originals stayed behind as rooms belonging to nothing — the ghosts. This happened on **every**
  save edit, whatever you changed. Ostraplan now clears the old room objects and lets the game rebuild each room
  cleanly. (If you have an affected save, its `Player.log` will be full of `Generating new room with old ID`.)

  **Skewed zones.** The game does not read the ship's grid size from the save; it rebuilds it on load, always
  leaving a one-tile margin around the ship. Ostraplan grew the grid to fit the ship *exactly*, with no margin,
  whenever an edit pushed a part past the old edge. Rooms and zones are stored as plain tile numbers, so the two
  grids disagreeing shifted every one of them — a little at the top of the ship, more with every row down. What
  set it off was subtle: the big fuel and gas tanks reserve a 7×7 under-floor area around a 3×3 body, so moving
  one near the hull could push the grid out with nothing visibly out there at all. Ostraplan now uses the game's
  own rule, verified against every ship in every local save.

- **Ship dimensions no longer come out as `15,36m x 11,20m`** on a machine whose region uses a decimal comma. The
  game writes and reads a decimal point. Cosmetic (the game recalculates the field), but it was wrong in the file.

## [0.44.0] 2026-07-17, Secondary airlocks no longer wall off half the map

### Added
- **Total ship mass on the Ship Rating report.** It sits alongside the four rating slots, so you no longer have
  to read it out of the Maneuver explanation. This is the mass of the structure you have built, which is the
  same figure the Maneuver grade divides by. In game a ship also carries its cargo, so a loaded one weighs more
  there than the report says.

### Fixed
- **A Secondary Exterior Airlock no longer paints a no-build zone in front of itself.** Placing one used to redden
  everything beyond its face, out to the edge of the map, and block building there. The game doesn't do this: it
  bounds construction by the **Primary** airlock only, so a Secondary can face into the hull. Internal docking bays
  for smaller craft are now buildable in Ostraplan, as they already were in Ostranauts. The Primary's own red zone
  is real and stays. Thanks to @hkorhal for the report (#5).

  Ostraplan had been bounding by *every* docking port. That was a deliberate "stricter than the game is always safe"
  call, on the reasoning that over-refusing can never let through a design the game would reject. It was wrong twice
  over: the game's build law (`Item.CheckFit`) derives its envelope from `aDocksys.FirstOrDefault()` alone, and
  `Ship.AddCO` sorts every Secondary behind the Primary, so a Secondary is never that port. The all-ports rule does
  exist in the game (`TileUtils.GetAirlockBounds`), but only to decide where a **meat blob** may spread.

Ships the 0.43.1 fix below, which was never released separately, plus the hardening that keeps that class of bug
from hiding again.

### Fixed
- **Editing the ship while an export, a save write-back or a Ship Rating was running could corrupt the result.**
  Those three engines read the live design on a background thread, so a part moved (or an Undo pressed) at the wrong
  moment could be written half-way between two positions, drop out entirely, or abort the run outright. The Ship
  Rating was the easiest to hit, being the one that takes long enough to click something during. The editing
  surface is now greyed out for the run. The problem scan was never affected: it already reads its own snapshot.

### Changed
- **A failure that isn't really about your save no longer claims to be.** "The edit can't be written back" and
  "Export failed" now appear only for causes you can act on (a ship that can't be represented, a write that didn't
  land). Anything else is a bug in Ostraplan and now surfaces as an unexpected error with its full stack trace in
  `error.log`, instead of being flattened into a misleading message. That flattening is what disguised the 0.43.1
  bug as a save problem.

### Internal
- Background work goes through `Ui.OffThread`, which in Debug builds rejects a lambda that captures anything owned
  by the UI thread, naming the capture. Release builds strip the check. This is the bug below, caught at the call
  site rather than in a user's dialog.

## [0.43.1] 2026-07-15, "Update ship in save" works again

### Fixed
- **"Update ship in save" always failed with "The edit can't be written back. The calling thread cannot access this
  object because a different thread owns it."** The wear setting was read off the dialog's slider from inside the
  `Task.Run` that builds the injected ship, so a WPF control was touched from a background thread and threw before
  the engine ever ran. The value is now snapshotted on the UI thread before going off-thread, as the export path
  already did. The failure was unconditional — it did not depend on the ship, the save, or anything installed.

## [0.43.0] 2026-07-14, skinned parts show their real per-brand stats (cooverlay cond-loot)

### Fixed
- **Branded walls, floors, and other cooverlay skins now show their true in-game stats, not the shared base's.**
  A skin (e.g. a Mobile Space Systems "Light Framework" wall) is a cooverlay over a base condowner (`ItmWall1x1`,
  24 kg), but it also carries a `strCondLoot` that the game applies on every spawn to shift the stats per brand.
  Ostraplan resolved the skin to its base and ignored that loot, so every branded metal wall read a flat 24 kg.
  It now folds the cooverlay's cond-loot deltas onto the base exactly as the game's `COOverlay.Init` does, so the
  palette and inspector match what you actually build: MSS "Light Framework" 20 kg, Testudo 25, Van Hummel 27,
  Ryokka 28, Langdon-Phillips 48, and likewise for price, install effort, and brand flags. Base (unskinned) parts
  are unchanged. This also flows into Base Value, the bill of materials, and the maneuver rating, which read the
  same figures.

### Changed
- **Removed the "Law verified against 0.x.y" banner.** The status bar previously turned the version yellow and
  warned when your installed game version differed from the version the Law was verified against. That nag is gone.
  It now just shows the detected game version (`Game 0.x.y`). The Law changes rarely, so the verified version is
  reviewed and updated manually on a per patch basis rather than warning on every game update.

## [0.41.0] — 2026-07-14 — dismissible unsealed-compartment alerts

### Added
- **Unsealed compartments are now findable and dismissible from the PROBLEMS panel.** The "N unsealed compartments"
  warning previously only pointed you at the Ship Rating modal to locate the leaks. Now:
  - a **Show** button on the warning highlights the leak points on the canvas and brings them into view (no need to
    open the Ship Rating report);
  - a **Dismiss** button hides the warning (and drops it from the warning badge count);
  - a **Restore Alerts** button appears under the PROBLEMS list to bring dismissed warnings back;
  - **dismissals persist in the `.oplan`**, so a design reopens with the same alerts hidden.

  Dismissal is by warning type, so it survives edits (the general mechanism, `Problem.DismissKey` /
  `ShipDocument.DismissedAlerts`, can cover more warnings later).

## [0.40.0] — 2026-07-14 — comprehensive keyboard shortcuts

### Added
- **More keyboard shortcuts, and they now show in the menus.** The File/Design/View dropdown items display their
  shortcut on the right (standard app-menu style), so they're discoverable, and a few common ones were added:
  - **Ctrl+A** select all parts;
  - **Ctrl+Shift+Z** redo (alias for Ctrl+Y);
  - **Ctrl+E** export, **Ctrl+I** Ship Info, **Ctrl+B** Bill of Materials;
  - **+ / −** zoom in/out from the keyboard (anchored at the view centre; the wheel still zooms at the cursor).

  The existing file/edit shortcuts (Ctrl+N/O/S, Ctrl+Shift+S save-as, Ctrl+Z/Y, Ctrl+C/V/D, Ctrl+R replace) are
  unchanged. The F1 controls window now lists the full set.

## [0.39.0] — 2026-07-14 — raw stats in the inspector

### Added
- **The inspector shows the raw game figures the game hides.** Selecting (or arming) a part now adds:
  - a **STATS** block with the true numbers, friendly-labelled — **Mass** (kg), **Health** (the durability pool
    `StatDamageMax`, which the game never shows as a number), install/dismantle/uninstall/repair **work**, **power**,
    volume, pressure, thrust, armor — shown only when the part carries them;
  - an **All game data (raw)** expander listing every numeric `Stat*` cond verbatim (internal name → value);
  - a **Conditions (flags)** expander listing every non-stat starting cond the part has (`IsInstalled`,
    `IsSignalable`, `IsWall`, …).
  All of it reads data already in memory (the same source as Base Value), so it adds no loading. The inspector now
  scrolls when the detail runs long.

## [0.38.0] — 2026-07-14 — device signal connections

### Added
- **Wire devices together (signal connections).** A new **Wire mode** (View menu) lets you connect signalable
  devices the way the game does: click a device to arm it as the signal source, then click another to connect them
  (click a connected one again to disconnect); the source stays armed so you can wire it to several targets.
  Connectable devices (alarms, air pumps, sensors, lights, anything `IsSignalable`) ring in violet, the armed source
  rings brightly, and existing wires draw as violet lines with a dot at the driven end. Esc or right-click cancels.
  - **Validated like the game.** A connection is legal only between two distinct **installed** signalable parts; there
    is no distance rule (the game wires by id, not proximity), so that is the whole check.
  - **Saved and exported.** Connections persist in the `.oplan` and are baked into the exported ship's
    `Electrical` GPM (`inputConnections`/`outputConnections`), so the wiring spawns with the ship.
  - Gate/threshold logic is left to the in-game signal box; Ostraplan authors plain connections only.

## [0.37.0] — 2026-07-14 — tidier toolbar

### Changed
- **The toolbar is grouped into dropdown menus.** The File, Design and View groups each collapse into a single
  menu button (matching the existing Import/Help menus), so the toolbar is far less crowded. Undo/Redo and the
  headline **Ship Rating** stay as direct buttons. **File** holds New/Open/Save/Save As/Import/Export/Update Ship
  in Save; **Design** holds Ship Info/Ship Re-skin/Snapshot/Bill of Materials; **View** holds Fit, a Symmetry
  submenu (Off/Vertical/Horizontal/Both), and the Zones/Power/Mod-overrides toggles as checkmarked items (their
  state is also visible on the canvas). All keyboard shortcuts are unchanged (F, M, Z, P, Q/E, Ctrl+Z/Y, etc.).

## [0.36.0] — 2026-07-14 — remembered view orientation

### Added
- **The design remembers its orientation.** The plan-view rotation (Q/E) is saved in the `.oplan`, so a design
  reopens in the same orientation it was saved in. New/imported designs start north-up. Rotating the view now
  marks the design as having unsaved changes (the `*`), since the orientation is part of the saved file.

## [0.35.0] — 2026-07-14 — ship info editor, guaranteed starting ship, container rotate fix

### Added
- **"Ship Info" editor.** A new toolbar button (Design group) edits the ship's in-game identity — in-game name,
  make, model, year, designation and description. The values are **saved with the design** (in the `.oplan`) and
  **pre-fill the Export dialog**, so they no longer reset to blank every export. Edits made in the Export dialog
  flow back onto the saved identity, so the two never drift.
- **Guaranteed starting ship.** When exporting as a Shipbreaker starting ship you can now choose **"Only your ship
  offered (guaranteed start)"** instead of the weighted chance. This pins the start-event pool to your ship alone
  (dropping the vanilla salvage pods, and any other mod's start ships, from that roll), so a fresh Shipbreaker
  always starts with it. The old **"Weighted chance"** (one option alongside the vanilla pods) stays the default.

### Fixed
- **Rotating an item inside a container no longer squashes its sprite.** In the container Contents window, an item
  rotated with `R` swapped its grid footprint but drew its sprite upright, so a rotated item (e.g. a tall missile
  laid flat) rendered as a stretched sliver. The sprite now turns with the footprint and fills the cell correctly.

## [0.34.0] — 2026-07-13 — clearer symmetry axes

### Changed
- **The symmetry axes are more prominent.** Thicker and brighter dashed lines, so the mirror axis (and its centre
  marker) is easy to see against the ship instead of getting lost in it.

## [0.33.0] — 2026-07-13 — smooth panning on big ships

### Fixed
- **WASD / drag panning is smooth again on large ships.** The cached ship drawing was baked in screen space, so
  **every pan frame rebuilt the whole ship** (draw-order sort + per-tile autotile + every sprite) — on a big station
  that dropped the frame rate far enough that panning stuttered and chained key presses (e.g. W then A for a
  diagonal) arrived late and felt one-directional. The cache is now baked **pan-independently** and the live pan is
  applied as a transform, so a pan frame is a single cached blit, not a rebuild. The cache still rebuilds on a zoom
  or content change (both actually change the baked geometry), and view rotation was already a transform.
- **PowerViz panning is smooth too.** The conduit overlay was re-stroked as one dashed `DrawLine` per segment (plus
  a thick glow pass) every animation frame, so panning with it on stayed laggy on a big ship. The lit and unpowered
  segment sets are now baked into **frozen pan-independent geometries** (one `DrawGeometry` per layer, rebuilt only
  on a data or zoom change) and the flow animation is throttled to ~30 fps — the whole overlay is a handful of GPU
  strokes per frame.

## [0.32.0] — 2026-07-13 — power connectors + PowerViz

### Added
- **Ghost power connector points.** While placing (or with a powered part selected), the part shows its power
  connectors as labelled badges — a lightning glyph plus **IN** (blue, where it draws power) or **OUT** (green,
  where a source feeds the network) — so input vs output reads at a glance and the marker stands out against the
  conduit flow. Rotates with the part (and stays upright), so you can orient a device to meet a conduit before you
  place it. Ported from the game's build-cursor connector sprites; the plugs come from each device's
  `data/powerinfos` `aInputPts` (resolved through the condowner's `jsonPI`).
- **PowerViz — a conduit power overlay** (toolbar **Power: On/Off**, or **`P`**). A port of the game's
  `TileUtils.GetPoweredTiles`: power floods 4-cardinally from every installed generator/battery's output over
  `IsPowerPath` tiles (conduits and powered fixtures). **Lit runs** animate a cyan flow, **orphaned runs** (conduit
  not reaching any live source) draw dim dashed red, and a **wired device with no feed** gets an amber warning
  marker on its unpowered plug — so you can confirm at a glance that everything is hooked up. The toolbar tooltip
  reports how many device plugs are unconnected. This is connectivity *visualisation*, not a power-draw simulation
  (still a non-goal): it answers "is it wired and oriented right", using the game's own network graph.

## [0.31.0] — 2026-07-12 — wear slider + zoom out further

### Added
- **A Wear slider** on both **Export** and **Update Ship in Save**, so a design can enter the game worn rather
  than pristine. It bakes per-part damage exactly the way the game wears a ship sold from a broker kiosk
  (`Ship.DamageAllCOs` → `CondOwner.BreakIn`): each installed part takes `StatDamage = uniform(0, ceiling ×
  StatDamageMax)`, so condition varies part to part. The slider picks the target **average** condition (10%–100%);
  it defaults to **~88%**, the game's own kiosk ("Used") value, and no part is ever left below **10%** condition.
  - **Note on vanilla wear:** the game's kiosk ships average ~88% condition (parts spread ~75%–100%), a lighter
    knock than folklore suggests — drag the slider left for a grungier ship, or to 100% (or untick) for pristine.
  - Export bakes the damage as each part's `aCondOverrides` (`DMGStatus` stays New, so the game keeps exactly the
    baked wear); save-edit writes it as each installed part's `StatDamage` cond, the same way the game stores it.
    The baked Ship Rating "Condition" grade reflects the applied wear. On a save-edit, wear re-rolls the condition
    of **every** installed part (replacing existing damage) — leave it unticked to preserve each part's wear.

### Changed
- **You can zoom out much further.** The zoom range now goes down to 0.125× (2 px/tile) via new 0.5× / 0.25× /
  0.125× steps, so a whole station fits on screen; panning was already unrestricted. Max zoom is unchanged (8×).

## [0.30.2] — 2026-07-12 — transforming a pasted selection no longer drifts or breaks symmetry

### Fixed
- **A group rotation no longer drifts.** Rotating a multi-part selection with a non-square bounding box used to
  creep down and to the right a little more with each turn (round-half-up re-centring of the swapped W×H box), so
  repeated rotates walked the group across the grid. The re-centring now rounds symmetrically, so a rotate and its
  inverse cancel and four turns return exactly. Odd-parity bounds still take at most a one-time half-tile offset,
  but it no longer accumulates. ([#3](https://github.com/Valtora/Ostraplan/issues/3))
- **Rotating or moving a non-symmetric selection with mirror mode on no longer warps it.** The symmetry-preserving
  rotate and move only ever made sense for a genuine mirror-partner set. Applied to an arbitrary selection (most
  visibly a fresh paste sitting on one side of the axis) they mangled it: identical parts collapsed onto each other
  under rotation, and a drag reflected the far-side parts about the axis as if it were an "invisible mirror line".
  Both edits now first check that the selection is actually symmetric about the axis; if it is not, they fall back
  to a plain group rotate (about the selection's own centre) and a rigid move.
  ([#3](https://github.com/Valtora/Ostraplan/issues/3), [#4](https://github.com/Valtora/Ostraplan/issues/4))

## [0.30.1] — 2026-07-11 — plain PNG snapshot follows the editing orientation too

### Changed
- **The plain PNG snapshot now also renders in your current view orientation**, matching the Ship Rating room
  map (0.30.0). If you've rotated the plan view with Q/E, the exported image is rotated to match instead of
  always north-up.

## [0.30.0] — 2026-07-11 — Ship Rating image follows the editing orientation

### Changed
- **The Ship Rating room map (PNG and SVG) now renders in your current editing orientation.** If you've rotated
  the plan view with Q/E, the exported image is rotated to match, so it reads the same way as your editor
  instead of always snapping back to north-up. The ship art and room tints turn together (the raster canvas
  swaps its width/height at 90°/270°, and the SVG wraps them in a rotation group); the room labels stay upright
  and re-route to the nearest edge of the rotated image so they remain readable.

## [0.29.1] — 2026-07-11 — copy/paste keeps container contents

### Changed
- **Copy/paste and duplicate now carry a container's contents.** Copying (or duplicating) a stocked container
  and pasting it reproduces the container *with* its cargo — each pasted copy gets an independent deep-clone of
  the contents (fresh item ids, marked as authored), so it exports and writes back to a save as a real stocked
  container rather than an empty one. Non-container parts are unaffected.

## [0.29.0] — 2026-07-11 — compartment fill, brush/replace hotkeys, SVG room map, self-adopting updater

### Added
- **Fill a whole compartment.** Double-click enclosed ("compartmentalized") empty space to highlight the
  entire sealed compartment, then arm a part and press **Enter** to fill it in one undo step — each tile is
  placed only where the game's CheckFit allows and a same-def part isn't already there. Areas open to space
  can't be selected, so a fill can never leak into vacuum. Esc (or any edit) clears the highlight. Reuses the
  same room flood-fill that powers zone painting.
- **Hotkeys for the two commonest edits.** **Alt+click** is now an eyedropper — arm the part under the cursor
  as the brush (the "Use as brush" action, previously right-click only). **Ctrl+R** opens "Replace with…" for
  the current selection. Both still appear on the right-click menu, now with their shortcuts shown.
- **Save the room map as SVG.** The Ship Rating room map's "Save image…" dialog now offers **SVG** alongside
  PNG: the ship sprites are embedded once as a pixel-crisp layer and every annotation (room tints, leader
  lines, labels) is written as true vectors, so the diagram stays sharp at any zoom.

### Changed
- **The updater now self-adopts, so old shortcuts never open a stale build.** Running a freshly downloaded
  newer Ostraplan.exe replaces the installed copy at `%LOCALAPPDATA%\Programs\Ostraplan`, refreshes your
  Desktop/Start-Menu shortcuts, and relaunches from there — the same pattern Ostrasort uses. Because a design
  can hold unsaved edits, it never force-kills a running copy: if the installed exe is in use it asks you to
  close it and retry rather than risking your work. Dev/`bin` launches and same-location launches are skipped.

## [0.28.0] — 2026-07-11 — symmetry-aware selection, move, rotate, delete

### Added
- **Symmetry mode now applies to editing, not just placement.** With symmetry on (M: Vertical / Horizontal /
  Both), selecting a part also selects its mirror partner(s), so a click, box-select, or flood-select grabs the
  whole symmetric group (matched by def and exact mirrored position, the way a symmetry-mode build lays them
  down). Manipulating the group keeps it symmetric: dragging moves the grabbed side by the raw delta and the far
  side by the mirrored delta (a part straddling an axis is pinned along that axis), a group rotate turns one side
  and reflects it onto its partners (so a left/right pair stays a left/right pair instead of swinging into a
  top/bottom one), and deleting removes the whole group. The live drag preview mirrors too, and a symmetric move
  commits as a single undo step. Ctrl+click still toggles a part (and its partner) out of the selection. The
  geometry (`SymmetryOps`) is unit-tested.

## [0.27.0] — 2026-07-11 — filtered box-select, reactor build hints, maneuver numbers, constructibility fix

### Fixed
- **Reactor components no longer false-flag "needs an installed Fusion Reactor Core beneath".** The
  constructibility check (which verifies the game can build a design incrementally) simulated one fixed build
  order: a coarse rank (docking → floors → walls → fixtures) then document order. Every reactor part (field
  coils, core, and each component) is a "fixture", so their relative order was just the order they appear in
  the file — and a real ship lists the components long before the coils and core they seat on, so each
  component was checked before its core existed and flagged as un-buildable. The simulation now sweeps to a
  fixed point instead: each pass places every pending part that currently fits, repeating while progress is
  made, so it finds the coils → core → component order (or any valid order) whenever one exists. This is
  general, not reactor-specific: any fixture that mounts on another fixture authored later in the file is
  affected. Parts that genuinely fit no build order are still flagged, and the modded-part trust behaviour
  (a failing modded part is trusted into the sim so its dependents don't cascade-flag) is preserved.

### Added
- **Shift+drag box-select with filter chips.** With nothing armed, holding Shift and dragging always
  rubber-bands a selection rectangle, even when the drag starts on a part (previously that would grab and
  move it, and a fully-decked ship had no empty tile to start a box-select from). When the catch spans more
  than one layer, a chip menu opens at the cursor (Floors / Walls & doors / Fixtures / Conduits, with
  counts); untick chips to prune the selection live — e.g. keep the walls without the floors under them.
  Chips combine, unlike the right-click "Select only" single-layer filter. Ctrl+Shift+drag adds to the
  existing selection.
- **The Ship Rating panel now shows the maneuver numbers.** The caption spells out the actual figures behind
  the grade: total installed mass, total RCS thrust, the graded mass ÷ thrust metric with the A–E cutoffs,
  and the true thrust-to-mass ratio (per kg and per tonne). With no RCS installed it says so and still
  reports the ship's mass.

### Changed
- **Placement failures now explain the reactor build chain.** Arming the Fusion Reactor Core over bare floor
  used to fail with a raw condition name; it now says to build the Field Coils first (and that their centre
  tile must stay open to space). Reactor components likewise point at the missing installed core. When a pose
  fails several rules at once, these staged-build hints win over the generic "needs a sealed floor beneath"
  so the actionable tip isn't buried. A forbidden floor now reads "a floor is in the way here" instead of
  "blocked by IsFloor", and under-floor overlaps report "tile is already occupied".

## [0.25.0] — 2026-07-10 — dropped the pristine margin

### Changed
- **Removed the "Pristine bonus" margin from kiosk prices.** A full code trace confirmed a designed or exported
  ship can never have pristine parts (the game only rolls pristine on used and derelict ships, and installing
  always makes a fresh non-pristine part), so the margin implied an upside a built ship can't reach. The panel
  now shows a clean sell price, buy price, and build cost, with a short note that the final in-game price can
  vary by roughly ±15% (tanks topped past their default fill, cargo, or parts not in the design). The value
  maths still prices the gas each tank starts with and excludes loose cargo, exactly as the game does.

## [0.24.0] — 2026-07-10 — pristine wording made exact

### Changed
- **Sharpened the pristine bonus explanation after a full code trace.** Verified against the game code: pristine
  is added in only two places (the random roll on used and derelict ships when they first load, and kiosk stock
  items) and removed in only one (a part taking damage). Installing a part always creates a fresh non-pristine
  part regardless of the kit it came from. The caption now says the roll only happens on used and derelict
  ships, so a ship you build or buy new sits at the base price with no reachable pristine bonus.

## [0.23.0] — 2026-07-10 — pristine bonus label + airlock hint gate

### Changed
- **The kiosk "Margin" figure is now "Pristine bonus, up to" and sits right next to the sale price.** It reads
  as an add-on to the sell figure it qualifies (with a clear gap before the buy figure), and the clearer label
  says what the number actually is: the extra sale value if parts were pristine, which the game only rolls
  onto ships it spawns.
- **Airlocks are no longer suggested as value upgrades.** Like the reactor core and the bridge, a docking port
  is a deliberate, ship-defining placement (an airlock goes exactly where the ship mates), not a room
  furnishing, so "needs a docking port" is out of the value hints. The "Nearly certifies" diagnostics still
  show Airlock lines for a room actually being built as one.

## [0.22.0] — 2026-07-10 — kiosk panel polish + bridge hint gate

### Changed
- **Kiosk price panel polish.** The margin now sits directly after the sale price (smaller type), all dollar
  figures round to whole dollars, and the Ship Rating window opens larger by default (clamped to the screen).
- **Bridge rooms are no longer suggested as value upgrades.** Like the reactor, "add a nav station" technically
  qualifies for almost every room, and a ship wants one bridge, not a console per closet. The "Nearly
  certifies" diagnostics still show Bridge lines for rooms actually being built as a bridge.
- **Corrected the pristine story (checked the install code).** Buying a part fresh from a kiosk makes the
  *item* pristine, but installing consumes the item and spawns a brand new part, which is never pristine. The
  only way an installed part gets the 25% pristine markup is a small random roll (2.5%) the game makes on
  used, damaged, and derelict ships when they first load. The margin hint now says so, prior wording claimed
  hand-installed parts were pristine, which was wrong.

## [0.21.0] — 2026-07-10 — kiosk prices + towing hint gate

### Changed
- **The Ship Rating value panel now shows kiosk prices, not abstract value.** "Estimated value / build cost /
  broker sell / broker buy" is replaced by "Sell to kiosk", "Buy from kiosk", and a "Margin" figure with a
  percentage. The margin is the honest uncertainty in the number: the game marks each pristine part up 25% (on
  its shell price, never its gas), and pristine on installed parts only comes from a small random roll on
  game-spawned ships. So the price shown is the base and the margin is the ceiling a lucky roll can reach.
- **Towing Room is only suggested for airlocks that can hold the brace.** The towing brace's own placement rule
  requires a docking-system tile beside it (it can only ever be built at a docking port) and the brace is a 7×2
  fixture, so the hint now appears only for rooms certified as Airlock with at least 7 tiles. It previously
  sprayed onto every uncertified room, since the Towing Room spec's only shape gate is "2+ sealed tiles".

## [0.20.0] — 2026-07-10 — value engine field-calibrated against live sales

### Fixed
- **The Pristine markup is gone from estimates and export bakes — spawned ships never have it.** The game
  applies its ×1.25 "Pristine" bonus per part only to a runtime condition that exactly two code paths grant:
  derelict break-in, and trader stock items. A ship spawned from an export never gets it, so Ostraplan's flat
  ×1.25 overshot real resale quotes by up to 25% and made exported ships buy high and sell low (the baked buy
  price carried the markup, the game's own recompute didn't). Verified against a real sale: the reported
  min-max build now estimates $2.14m sell vs its actual $2.3m in-game sale (was $2.65m); the remainder is the
  game's random break-in roll on used ships plus parts the live ship carries that the plan doesn't.
- **A part's value now includes the gas its def spawns with.** The game prices canister contents (mols ×
  molar mass × the data-driven price/kg) plus liquid D2O and solid He3 fuel: a full O2 RTA is ~$5,648 of
  oxygen on a $410 shell, which is why canister-heavy builds read low before. Gaseous He3 is worth $0 in the
  game's own math (its molar-mass table has no He3 entry); He3 pellets are priced.
- **Broker buy estimate corrected from 1.25× to the data's 1.2×.** Both factors are now read straight from
  the core ship brokers' conds (they buy at `DiscountBuy` 0.8×, sell at `DiscountSell` 1.2×; the
  "1.1 − break-in" haircut turns out to be derelict-only). The min-max build's buy estimate is now $3.21m
  against the observed "3m or so" (was $4.14m).

### Changed
- **Reactor rooms are no longer suggested.** "Add a reactor core" technically qualifies for every sealed room
  of 4+ tiles, which spammed both "Nearly certifies" and "Value opportunities" on every ship. A reactor is a
  ship-defining build (5×5 core, field coils, vacuum exposure), not a room furnishing, so hints never advise
  one; rooms that already contain a core still certify and report as Reactor rooms normally.

## [0.19.0] — 2026-07-10 — void-room value + opportunity Show buttons

### Fixed
- **Engines and exterior-mounted gear now count toward the broker value, matching the game.** Ostraplan valued
  void (unsealed / open-to-space) rooms at $0 on the assumption that only sealed compartments count — the game
  disagrees: neither `Room.CalculateRoomValue` nor `Ship.GetShipValue` filters void rooms, and 192 core
  templates bake real value into their void rooms (the AirRacer's unsealed engine space alone is worth $343k).
  Parts in unsealed areas are now valued at that room's modifier (×1.0, or ×1.05 for an exterior cargo space),
  which raises the estimate for any design with engines or exterior equipment. Also settles a Discord theory:
  there is no special ×3 for wall-attached items — the ×3 O2 bonus is one global flag over the whole sum, so a
  single added part merely *looks* tripled when the bonus is active.

### Changed
- **Every "Value opportunities" entry now has a Show button** that highlights exactly which room the hint is
  about on the canvas (same mechanism as the airtightness leak highlighting; one highlight at a time). Rooms
  come from the same flood-fill partition the game uses, so entries never overlap or double-count — a tank farm
  legitimately produces one Engineering Room entry per canister compartment.

## [0.18.0] — 2026-07-10 — value opportunities

### Added
- **"Value opportunities" in the Ship Rating report** — an optional, collapsed section at the bottom that shows,
  for every sealed room (including completely empty ones), the higher-value room specs its shape allows, exactly
  what to add or remove to get there, and the broker-sell gain on the room's current contents (for example, an
  empty 9-tile room plus one installed canister or battery becomes an Engineering Room at ×1.4 room value).
  Certified rooms get upgrade hints too (a Basic Quarters that is one storage bin and a chair away from Luxury
  Quarters), but only when the upgrade also outranks the current spec in certification priority — the game picks
  the highest-priority matching spec, so items added for a lower-priority spec would change nothing. The section
  also calls out the single biggest lever: when the ship has no working O2 supply, it shows what feeding an air
  pump from an installed O2 canister would add (the whole-ship ×3).

## [0.17.0] — 2026-07-10 — room membership & value law (Discord reports)

### Fixed
- **Wall-mounted items now count toward room certification and value, matching the game.** The game assigns a
  part to the room at its centre tile, but when that tile is a room-less wall tile it retries at the part's
  "use" point (decompiled `Tile.AddToRoom`) — that's how wall storage bins, sensors, antennas, coolers, and ship
  weapons participate in rooms. Ostraplan only used the centre tile, so a bin mounted on a south or east wall
  silently vanished from certification (the Discord "bins present but quarters won't certify" report) and from
  the room's broker value. Corpus certification parity improved from 2109/2148 to **2124/2148 rooms exact**
  (still 0 over-certifications).
- **The ×3 "O2 atmosphere" value bonus now requires a working O2 supply, not just a pump.** The game grants it
  only when an installed air pump has an installed O2 canister (RTA) with O2 in it at its gas-input tile;
  Ostraplan granted it for any placed air pump (the Discord "pump = valid O2 atmo?" report). One fed pump ×3s
  the value; extra pumps add nothing — that part was always game-correct. Exports now also bake the real
  `nO2PumpCount`, so a purchased design with a working O2 supply quotes the right price at the broker before its
  first full load, and "Update ship in save" refreshes the count for the edited layout.
- **"Update ship in save" now bakes the parts-based room value** (the same fix exports got in 0.7.0) instead of
  the physical room volume, so a shallow-load broker quote of an edited ship reads its real worth.
- **Report note:** an air pump embedded in the wall line contributes $0 to the ship's broker value *in the game
  too* (its room-membership fallback lands on its own wall tile) — Ostraplan matches; this is not a bug.

### Changed
- **"Nearly certifies" now tells you what's actually wrong, including blockers.** The law report used to show
  only the highest-priority spec missing items — which was almost always "Reactor room" (any ≥4-tile room is one
  reactor core short of it), while never mentioning that a *forbidden* item was parked in the room. Each
  uncertified room now lists its two closest specs ranked by how near they are, with concrete lines like
  "Basic Quarters: remove O2 Resident Tank Assembly ×2" or "Luxury Quarters: needs a chair · remove Ship Battery"
  — the exact answer to "why isn't my Luxury Quarters recognized?" (Quarters specs forbid gas canisters,
  installed RTAs, ship batteries, floor hatches, toilets, and reactor cores — in the game too.)

## [0.16.0] — 2026-07-10 — P.A.S.S. boarding spawners

### Fixed
- **Exported ships now spawn you at a proper boarding point instead of somewhere random.** A ship template
  carries hidden **spawn points** the game uses to place people: a **Boarding** point (where you appear when
  arriving by the P.A.S.S. ferry or a skywalk) and a **NotBoarding** point (where an NPC already assigned to the
  ship spawns). Ostraplan drops all system objects on import (loot spawners, fire, and these spawn points share
  the same `IsSystem` flag) and never re-created them, so every exported ship had none — arriving at your own
  Ostraplan ship dumped you at a fallback tile, frequently *outside* the hull (the "I skywalk to my ship and end
  up somewhere random on the map" reports). Export now bakes both automatically: the **Boarding** point on the
  interior tile nearest the primary airlock (the dock entry, where you'd expect to arrive), and the
  **NotBoarding** point deeper inside. No action needed beyond re-exporting an existing design to pick up the fix.
  (The **save-edit** path — "Update ship in save" — was never affected: it keeps the original ship's spawn points.
  And **nav-console modules** were and remain correctly populated — a separate, already-working mechanism.)

## [0.15.0] — 2026-07-10 — flip a selection · reactor core fix · modded overrides

### Added
- **Modded parts can now break the placement rules (with a warning), instead of being silently blocked.** A new
  **"Mod overrides"** toolbar toggle (off by default, remembered): when on, a modded part may be placed where
  Ostraplan's rules say it doesn't fit — it lands and is flagged as a **warning** in Problems ("modded part may
  not fit — verify in-game"), rather than being hard-blocked. The reason: Ostraplan's Law is a port of the *base
  game's* logic, so it's authoritative for vanilla parts but only best-effort for modded ones (a mod can add its
  own conditions or code). **Core parts stay fully enforced.** The armed ghost shows amber (not red) when a
  modded part will place via the override, and any modded part flagged illegal — however it got there — is now a
  yellow warning rather than a red blocker, and is trusted into the build check so parts placed on it don't
  cascade-flag.
- **Flip a selection horizontally or vertically** (`H` / `Shift+H`, also on the right-click menu). Mirrors the
  selected parts about the selection's centre — `H` left↔right, `Shift+H` up↔down — with each part reflecting
  its position and snapping its rotation to a real 0/90/180/270 (so the result is always buildable; the game's
  ship format has no mirror field, only a rotation). Walls and floors auto-tile rather than turn, so they move
  but keep their orientation. One undo step, and (like a group rotate) an illegal landing is allowed but
  flagged, not blocked.

### Fixed
- **The fusion reactor core now builds as the real part instead of a hollow copy.** The palette was building
  `ItmFusionReactorCore01On` — a glow-state *item* the game ships with no condowner, so it carried an internal
  name, **0 mass, 0 value, and none of its `IsFusionReactorCore` conditions**. Placement still worked (its
  sockets are identical, which is why this hid), but the reactor counted as weightless in the maneuver rating,
  contributed nothing to room value or the bill of materials, and exported broken. `PreferPoweredState` now
  only swaps a device to an operational counterpart that is a real condowner, so the reactor core builds as the
  installable `…Off` form (417 kg, priced, correctly named). The other reactor parts (field coils, laser array,
  core/cryo pumps, pellet feeder, MHD generator) already swapped to real operational condowners and are
  unaffected. Rebuild any reactor design to pick up the correct mass/value.

### Fixed
- **A bought ship now docks at the station instead of stranding out in the system.** Exports now bake
  `aDockingPorts` (the installed docking-port item ids) and `strPrimaryDockingPortID`, which core ship
  templates carry but Ostraplan omitted. The game only rebuilds those from the ship's items on a *full*
  load; a broker ship is spawned and (on some paths) docked while still *shallow*-loaded, and a shallow
  ship reads its ports straight from the file. With the fields missing, a purchased Ostraplan ship exposed
  zero open docking ports (`Ship.GetOpenDockingPorts`), so the game could not mate it to the station and
  left it drifting at its `objSS` (hundreds of millions of km away), where it was also absent from the
  P.A.S.S. ferry list. Verified against decompiled `GUIShipBroker.OnPurchaseConfirm` / `CrewSim.DockShip`
  and the game's `Ship` load path. Re-export an existing design to pick up the fix.

## [0.14.1] — 2026-07-09 — internal test hardening

### Changed
- **Internal:** made the save-edit placement-law immunity test deterministic. It previously imported
  whatever ship was in your newest save game and asserted a placement outcome against it, so ordinary
  play could make it fail spuriously; it now runs on a synthetic ship. No app-facing change.

## [0.14.0] — 2026-07-09 — friendlier replacement-mod names

### Changed
- **Export dialog notes the Special Offer "$0" quirk.** In-game the ship-broker's Special Offer slot always
  lists at "$0" (the real price shows on the Confirm dialog) — it's hardcoded in the game, not a pricing
  error, and confirmed by decompile: the ship's baked value is correct (the Confirm price proves it). The
  dialog now says so and points you at a regular broker kiosk for a visible list price.

### Added
- **Replacement mods get a clearer name.** Exporting a design that replaces an existing ship now names the
  **mod** distinctly from the ship it overrides — defaulting to `"{replaced ship} - Replaced via Ostraplan"`
  instead of reusing the replaced ship's own name (which read confusingly, as if the mod *were* that ship).
  A new **Mod name** field in the export dialog shows this default and lets you rename the mod to whatever
  you like; it auto-follows the ship name for a normal (non-replacement) export, and stops auto-updating
  once you type your own. The mod's name is now fully separate from the ship's in-game identity.

## [0.13.0] — 2026-07-09 — in-game availability, ship identity & vanilla-ship replacement

### Added
- **Find and Replace All…** context menu action. Select one or more copies of the same part and swap
  every copy of it anywhere in the ship — not just the current selection — for a chosen compatible part,
  in one undo step. Uses the same compatibility rule as "Replace with…" (same render layer + footprint,
  containers excluded), so a bulk swap can't turn a floor into a fixture or a wall into a door. Locked
  matches are counted in the picker but skipped by the swap.
- **Replace a vanilla (or modded) ship.** The export dialog can now tick "Replace an existing ship" and
  pick any core or mod ship: the export takes over that ship's identity (`strName`), so — loaded after
  core — the game spawns your design in its place everywhere (brokers, derelicts, missions). Pairs with
  the import flow for the "retrofit an existing hull with installed parts mods" workflow: import a
  vanilla ship, edit it with modded parts, and export it back over the original. The picker pre-selects
  the ship you imported. Caveats shown in the dialog: structure only (the original's cargo/crew loadout
  isn't carried over), and it affects new spawns, not ships already in a save. A replacement keeps the
  vanilla varied-naming behaviour unless you set an explicit in-game name.
- **Get your ship in-game, from the export dialog.** Exporting a design can now make it directly
  obtainable in a playthrough — no more hand-editing `loot.json` (which players broke into CTDs and
  infinite-ship loops). Tick any of:
  - **Ship broker kiosks** (OKLG / BCER / BCRS / Venus / VORB) — the ship joins that station's normal
    broker stock at an editable weight (defaulting to the pool's average, so it shows up about as often
    as a stock ship). The whole effective pool is preserved, so ships from other mods survive.
  - **Special Offer** (the free-ship-when-you-own-nothing slot, per station variant) — pins the slot to
    your ship.
  - **Starting ship** — offers the ship as a weighted option in a fresh **Shipbreaker** career start
    (alongside the vanilla salvage pods), with an editable start station and mortgage (pre-filled from
    the broker buy estimate). Built on the game's own `CGEncShipSalvagePod*` chain, so it needs no other
    mod. Note: vanilla chargen has no true ship *picker*, so this is a weighted chance, not a guaranteed
    choice.
  The export writes the extra `data/loot`, `data/lifeevents` and `data/interactions` files itself; where
  another ship mod touches the same kiosk pool, Ostrasort's `--patch` merges them (the dialog says so).
- **Ship identity fields in the export dialog.** Set the ship's in-game **name** (its `publicName`) plus
  **make / model / year / designation / description** — the same flavor fields core ships and mods like
  Ithalan's Additional Ships carry. The in-game name is now kept sticky (see the fix below).
- **One-click register with Ostrasort.** When staging into the game's Mods folder, tick "Register with
  Ostrasort" and the export hands off to Ostrasort headlessly — it registers the mod in
  `loading_order.json` (`--apply`) and, if the export touched any kiosk loot, merges conflicts with other
  ship mods (`--patch`). Ostraplan finds Ostrasort automatically (or asks once and remembers the path),
  and still never writes `loading_order.json` itself.

### Fixed
- **Exported ships no longer spawn inside the sun.** The exported orbital position defaulted to Sol's
  exact `(0,0)` origin; the kiosk/Special-Offer/starting-ship spawn path (unlike template import) does not
  reposition a template, so the ship materialised in the centre of the star. It now carries a small
  nonzero position like every core template does.
- **A custom in-game ship name now sticks across spawns.** Export hardcoded `publicName` to `"$TEMPLATE"`,
  which makes the game re-roll a random name every spawn; a real name typed in the dialog is now written
  through and kept. (The registry `strRegID` / "callsign" is *not* settable from a data mod — the game
  always mints a fresh one on spawn — so the dialog doesn't pretend to control it.)

## [0.12.0] — 2026-07-09 — optional save backup

### Added
- **Optional backup when updating a ship in a save.** The "Update ship in save" dialog's
  in-place write now has a **Back up the original save first** checkbox (ticked by
  default). Untick it to write straight into the save without spawning a backup copy —
  handy when iterating on a ship so you don't accumulate a pile of backup saves. Ticked
  stays the safe default (with the confirmation and result messages adapting to the
  choice); a copy write still never touches the original.

## [0.11.0] — 2026-07-09 — ITEMS palette: loose cargo on ships

### Added
- **ITEMS palette tab — drop loose cargo onto ships.** A new **ITEMS** tab lists
  every loose item in the game (food, ammo, clothing, tools, books, brushes, scrap,
  personal effects — the whole loose universe). Arm one and click to drop it: onto a
  **floor tile** (it rests on the deck, one item per tile) or into a **container**
  under the cursor that accepts it (same fit rules as the inventory editor). A live
  green/red ghost shows whether the drop will land. **Right-click** a dropped item for
  its menu — **Change Quantity…** (stack a stackable item up to its per-item limit) and
  **Delete**; left-click selects it (details in the inspector), **Del** removes it.
  Loose items and their stack counts persist in the `.oplan`, spawn in the ship both when
  you **export a mod** and when you **update a ship in a save** (a stack becomes a proper
  stack head + members with a CO each), and appear in the PNG snapshot. *Why it matters:*
  designs can now be provisioned — a stocked galley, a loaded ammo locker, scattered
  salvage — not just built empty.

### Fixed
- **All textbook and toothbrush variants now appear** in the loose-item picker (10
  textbooks, 6 toothbrushes), not just one of each. These are metadata skins over a
  shared base item; the container add-picker already surfaces them since 0.9.0, and the
  new ITEMS tab lists the full set.

## [0.10.0] — 2026-07-09 — operational-state build defaults

### Changed
- **Powered fixtures build in their operational (On) state, not Off.** Ostranauts
  installs most devices switched **off** (the state a ship's rating never counts and
  that a player must turn on after loading). Ostraplan already did this for RCS
  thrusters; it now does it for **every** device with a clean operational counterpart
  — coolers, heaters, scrubbers, chargers, alarms, sensors, reactors, weapons, plus
  furniture — so a design's rating reflects reality and an exported ship spawns with
  its systems working. Devices whose "on" state is genuinely ambiguous (a colour/alert
  alarm, a transponder, the fusion reactor's startup sequence, an open/closed vent) are
  left exactly as the game installs them.

## [0.9.0] — 2026-07-09 — ship zones, faithful cargo & one-click install

### Added
- **Optional one-click install.** Ostraplan can copy itself to a fixed per-user home
  (`%LOCALAPPDATA%\Programs\Ostraplan`) and create Desktop and Start Menu shortcuts, so
  you have one place to keep and launch it instead of hunting for the downloaded exe.
  It offers this once on first run and otherwise stays out of the way; you can trigger
  it any time from **Help ▾ ▸ Install Ostraplan / shortcuts**. No admin rights, nothing
  written outside your user profile, and deleting that folder uninstalls it. (This fixed
  home is also where a future built-in updater would drop new builds.)
- **Ship zones — drawn, editable, and preserved on round-trip.** Ostranauts'
  crew/trade zones (Haul, Barter, Forbid, and the content trigger/spawn zones) now
  survive import → export and import → save-edit instead of being dropped or
  silently relocated, and you can create and manage them in the planner. A new
  **Zones** panel (right inspector) lists them; **+ Add** makes one and arms it for
  painting; **click a zone to paint** its tiles with the same tools as parts
  (drag to add, **Ctrl**-drag to erase, **Shift**-drag a box, **double-click**
  fills an enclosed room), each stroke one undo step. **Edit** sets the name, type
  (Haul/Barter/Forbid as independent toggles, matching the in-game editor), target
  role, colour, and — under Advanced — content-zone fields (encounter triggers,
  owner/target person-specs). A **Zones** toolbar button (or **Z**) toggles the
  overlay. Zones persist in the `.oplan`, export into `data/ships` `aZones`, and are
  re-projected into the correct tiles whenever the grid grows on save write-back.
  *Why it matters:* dropping zones broke player storage/no-go setups and, on
  authored station/quest ships, the scripted encounters wired to trigger zones.

### Changed
- **The Problems list is now an expandable list with a "View" button.** Each problem
  collapses to its title (click to expand the detail), and issues with a location get
  a **View** button that pans and zooms the canvas straight to the offending tiles, so
  a flagged part is easy to find on a big ship.
- **The update check now interrupts on launch.** When a newer GitHub release
  exists, Ostraplan raises a modal on startup (**Download Latest Version**, which
  opens the release page, or **Not Now**) instead of only revealing the toolbar
  Update button quietly. The button still stays as a persistent reminder after
  you dismiss the modal, and the modal shows on every launch while a newer
  version is out, so a release is never missed. (Mirrors the same change in
  Ostrasort.)
- **The add-to-container quantity control is clearer and capacity-aware.** The quantity
  field no longer hides its own number behind a clear "×" button, and it now has −/+
  steppers and shows how many of the selected item still fit ("of N"). The value is
  clamped to what the container can actually hold, so you can't enter a quantity that
  would just be rejected, and the picker says "container full" when there's no room.

### Fixed
- **"Make Loose Item" now works on walls, floors, and conduits — and keeps their
  theme.** These are placed as themed skins (a Testudo wall, an Aero floor), and only
  the plain base part carries an uninstall recipe, so the loosen action was silently
  unavailable on any skinned wall/floor/conduit. It is now offered, and loosening a
  themed part yields the matching themed loose item (a Testudo loose wall, not a
  generic one), mirroring what the game drops when you uninstall it.
- **Nav-console modules and themed loose walls/floors now show up in the container
  add-picker.** When you added items to a container, the picker drew only from plain
  condowners and skipped cooverlay skins entirely — so a nav console offered none of
  its actual modules ("nothing inside them"), and floors/walls showed a single generic
  "Floor (Loose)" instead of every themed variant you can store in game. The add-picker
  universe now also includes cooverlay skins (resolved through their base), so nav
  modules (Controls, Flight Dynamics, Map, …) and the full set of themed loose
  walls/floors are offered. Each container still narrows the list to what it accepts.
- **Exported inventories now spawn exactly as authored — right contents, right counts,
  right stacks.** A design's authored cargo (items packed into storage racks, bays,
  weapons, and every other container) survives being spawned from an exported
  `data/ships` mod, at the quantities you set. A `data/ships` file loads as a *template*,
  and the game silently drops contained items that aren't carried the way a save carries
  them, refilling the container from its default loot instead — so filled racks and bays
  came back empty, and a weapon loaded with two stacks of five rounds came back with only
  a couple. Export now writes each contained item (and the modules it injects into a nav
  console) with the same per-instance data a save uses: a "keep me" marker that also
  suppresses the container's default loot (so a stocked weapon gets exactly the ammo you
  authored and nothing extra), plus stack data so a ×N stack rebuilds at the right count
  instead of collapsing. (The save-edit path already handled this and was unaffected.)
- **Fixtures on floor-storage items no longer false-flag as "already occupied".** An
  under-floor storage bin or rack (e.g. ItmRackUnder01, the floor bins) provides a
  walkable sealed-floor surface that the game lets you build on and reach across. Its
  tiles carry IsFixture, which the placement law's obstruction mask lists, so a rack
  placed on — or whose access tile fell on — such a floor was wrongly flagged. A sealed
  floor is now treated as a valid build/stand surface (a genuine obstruction still
  blocks), matching the game.

## [0.7.0] — 2026-07-08 — loose items & reliable symmetry

### Changed
- **Symmetry now previews every mirror.** With symmetry on, the placement ghost
  shows the cursor part *and* each of its mirror copies, green where the mirror
  will land and red (offending tiles tinted) where the placement law refuses it.
  Previously only the cursor part was previewed and a mirror that didn't fit was a
  silent no-op, which read as "symmetry only works most of the time" — especially on
  large ships, where mirrors more often land on structure that isn't symmetric yet.
  The mirror geometry (reflection + rotation) was also lifted out of the canvas into
  a pure, unit-tested unit, so it can't silently drift.
- **RCS thrusters are built in their ON state.** The game installs an RCS cluster
  Off (and its maneuver rating doesn't count an Off thruster), so a designed ship
  used to read maneuver "O" and you'd have to power each thruster by hand after
  loading in game. Ostraplan now builds the identical On variant, so a design shows
  a real maneuver grade and an exported ship's thrusters work on spawn. (Imported
  ships keep whatever state they were saved in.)

### Added
- **Make Loose Item / Install item.** Right-click a placed fixture to uninstall it
  into its loose (packaged) form on the tile, or re-install a loose one — the two
  directions of the game's own install/uninstall jobs. Eligibility is data-driven,
  so only genuinely uninstallable fixtures qualify (raw hull, walls and the fixed
  airlock never do). The swap keeps tile, rotation and any cargo, is one undo step,
  and conserves an item's baked contents (a gas canister stays charged); an install
  that no longer fits is flagged in Problems rather than blocked. Placing *arbitrary*
  loose inventory (tools, food, consumables) remains a separate, not-yet-built flow.

### Fixed
- **Exported ships now carry their real broker value.** Export baked each room's
  physical *volume* into `roomValue` instead of the game's parts-based room value, so
  a spawned design read as nearly worthless at a broker until the game recomputed it
  on full load. It now bakes the same parts value the game does (and that Ostraplan
  already shows in the inspector).

## [0.6.0] — 2026-07-07 — first public release

The first public build of Ostraplan. Consolidates the full editing suite, the
complete validation Law, interop (export/import), live-ship save editing, and the
container/cargo viewer and editor — plus in-app bug reporting and an activity log.

### Added
- **The full Law:** placement sockets (`Item.CheckFit`), room/airtightness
  flood-fill (`Ship.CreateRooms`), room certification (`RoomSpec.Matches`), and
  the six-slot **Ship Rating** (`Ship.CalculateRating`), all ported from the game
  and parity-tested against its own baked room/rating data.
- **Law report** with air-leak tracing, and a live **Problems** list.
- **Interop:** export a design as a spawnable local mod; import a core/modded ship
  template or your own ship from a save.
- **Edit your live ship:** import it with its identity and write structural edits
  back into a **copy** of the save — crew, cargo, world position and ship identity
  preserved, the original untouched.
- **Editing suite:** drag-paint, box/hollow fill, symmetry mirror, flood-select,
  "Replace with…", ship-wide wall/floor **re-skin**, group rotate, copy/paste, and
  unbounded undo/redo.
- **Bill of materials** (install-kit counts), **PNG snapshot**, light/dark
  **theming**, and a GitHub **update check**.
- **Containers & cargo:** view any container's contents on the grid (right-click ▸
  **View contents**), drill into nesting, and add / remove / rearrange loose
  cargo — carried through Export and save write-back.
- **Report a Bug** (Help menu) opens a pre-filled GitHub issue with diagnostics; an
  on-disk **activity log** records your actions for troubleshooting; and the app
  **version** now shows in the title bar.

### Changed
- **Missing-mod designs now open read-only.** Opening an `.oplan` while a mod it
  depends on isn't loaded names the missing parts *and their mods*, shows a
  standing "MISSING MODS" warning, and blocks saving until the mods are enabled
  (verify with [Ostrasort](https://github.com/Valtora/Ostrasort)). Previously the
  missing parts were dropped from the view and a later save lost them for good —
  and building over where they belonged could silently break the ship.
- **Save-edit designs are clearly linked to their save.** The import dialog and
  docs now spell out that an `.oplan` from a save references the ship's live state
  (crew/cargo/wear) rather than embedding it, and that Export is the way to a
  save-independent, shareable ship.

### Docs
- Public-facing README, a usage guide ([docs/usage.md](docs/usage.md)), and this
  changelog.
- Spec reconciled with the code — notably the `.oplan` format and a new save-edit
  round-trip section; dropped/again-planned items corrected. (The standalone spec
  document was later retired; the game-behaviour reference now lives in
  [docs/GAME-INTERNALS.md](docs/GAME-INTERNALS.md).)

### Known limitations
- **Ship Zones aren't drawn yet.** Any ship you import or export will lose or move
  its zones, so they need to be deleted and redrawn. This is under active
  development and will be addressed in an update over the coming weeks.

## Development history (pre-public milestones)

These shipped internally on the road to the public release.

### 0.4 — Interop — 2026-07-05
Export as a spawnable mod (the game's `data/ships` shape with precomputed
rooms/rating), template import, and save-game import. Round-trip verified
(`doc → export → re-parse → rebuild` reproduces the same tiles, rooms and rating).

### 0.3 — The Law: rooms, certification & rating — 2026-07-05
Rooms/airtightness, room certification, and Ship Rating, reached from the Ship
Rating button and the law report. Parity: rooms 188/192, certification 2109/2148
rooms exact with zero over-certifications of a real compartment.

### 0.2 — Placement law — 2026-07-04
`Item.CheckFit` ported onto the tile-condition accumulator: ring-grid
reqs/forbids, the off-ship rule, mask rotation, and hard rejection at the single
placement choke point, plus the airlock construction envelope.

### 0.1 — Foundation — 2026-07-04
Mod-aware data index, the palette over the game's eight build tabs, the sprite
canvas with game-exact autotiling, drag-paint/box-fill/symmetry, undo/redo, zoom
and pan, the `.oplan` format, and the Primary Airlock convention with its
construction-envelope hazard overlay.
