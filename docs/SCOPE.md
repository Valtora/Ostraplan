# Scope: what Ostraplan is, and where the line is

Ostraplan does one thing: **it designs ships, and it gets them into your game.**

Everything in the tool serves that sentence. Design a ship against the game's own
rules, know it is valid before you build it, then put it in your game as a mod, as a
new ship in a save, or as an edit to the ship you already fly. That is the whole
remit, and it is deliberately narrow.

This document is the authority on the boundary. If a request falls outside it, it
gets declined with a reason rather than scoped down, and this is the reason.

## The test: is a design the input?

One question decides almost every case.

> **Does the feature take a *design* as its input?**

If yes, it is Ostraplan's business. If no, it is something else's, however easy it
would be to build. Ostraplan is a **planner** that can write what it plans into your
game. It is **not a save editor** that happens to draw ships.

Worked examples, from the request that settled the line
([issue #17](https://github.com/Valtora/Ostraplan/issues/17)):

| Request | Verdict | Why |
|---|---|---|
| Add the current design to a save as a ship you own | **In scope** | A design is the input. The save write exists to deliver it. |
| Design an apartment and put it in your save | **In scope** | A design is the input, same as a ship. See [Apartments](#apartments) below. |
| Buy an apartment from a broker | **Out of scope** | No design in it. That is shopping, which is the game's job. |
| Copy a ship verbatim from save A to save B | **Out of scope** | No design in the middle. Import into a design and add it to the other save instead: that path already works and loses only per-part damage and crew. |

The line keeps the tool's remit, its test surface, and its exposure to undocumented
save internals bounded. Every save-state feature pins Ostraplan to game internals it
would otherwise never have to touch, and each one has to be re-verified against every
game patch, forever.

### Apartments

An apartment is **a ship**. Not a ship-like thing: the game stores one as an ordinary
`JsonShip` record and applies the same grid, the same placement law, the same rooms and
the same airtightness to it. What differs is only how it is delivered, and a residence you
have laid out here is a design like any other, so it passes the test above.

So Ostraplan designs residences and writes them into saves, by the same three routes it
offers a ship: edit the one you own, add a new one, or move one between playthroughs.
It does **not** put one in a broker's window, because buying is not designing.

This reverses the decision that closed
[issue #12](https://github.com/Valtora/Ostraplan/issues/12), and the reversal is worth
recording because the original reasoning was sound at the time. The blocker named then was
that the fittings involved (`ItmKioskTransit02`/`03b`, `ItmDockSys02Closed`,
`ItmSink01Station`) have **no install, uninstall or dismantle recipe at all**, so there was
no bill of materials, no cost and no socket rule to port. That is still true of the game
data. It stopped being a blocker five days later, when the **SPECIAL palette tab** shipped
in v0.66.0 and made every recipe-less installed fitting placeable. The feature was declined
for a reason that a neighbouring feature then removed, and nobody noticed for a release.

### Naming loose items

A loose item can be named here, the same as a placed part. This reverses the line drawn in
commit `ac88233`, and the reversal is worth recording because the original reasoning read
well and was still wrong.

The argument then was that a name belongs to *structure*: loose cargo is contents rather
than ship, an overlay that takes no part in the Law, so it carries no name of its own any
more than it carries tile conditions. Two things sink that. The first is the game, which
renames **anything that is not a person** and keeps the name on the object's own `Rename`
panel whether the object is bolted down or lying on the floor, so a design that cannot name
a deck item says less about the ship than the game itself does. The second is that the loose form is often the
design intent: a stack of Ablative Core Liner Replacements is deck cargo that is *meant* to
be there, and a crate labelled "Electrical" or a SuperHandy labelled with the section it
belongs to is a plan for a ship rather than clutter on one
([issue #38](https://github.com/Valtora/Ostraplan/issues/38)).

A name also costs the model nothing the overlay was not already paying: it rides on the
loose item exactly as a part's does, through the `.oplan`, the export and a save write-back,
and an import reads it back. Tile conditions were the real line, and they still are.

### Painting condition

A design can carry the condition of each part and each deck item, painted by hand, and it
travels into the game with everything else. This reverses the line drawn in
`DamageState.cs`, and the reversal is worth recording because the original reasoning is
still correct about the thing it was written for.

The argument there was that a design carries no wear: `StatDamage` is per-instance save
state, no def declares it, and the scope line admitting a single impact is about *measuring*
a layout rather than storing a damaged one. All of that still holds for a **strike**. A
micrometeoroid run is a measurement, its damage lives beside the document and never in it,
and that is what makes "fire again" and "start over" the same cheap operation.

An authored condition is the other thing entirely. It is a property of the design, the same
as a container's fill or a nav console's arrangement, and this page already listed "a wear
level" among what a design carries into the game. So this generalises one whole-ship number
to per part; it does not open a new category. What decided it was the modding case
([issue #33](https://github.com/Valtora/Ostraplan/issues/33)): a station that is meant to
look lived-in cannot be described by a single average, because the point is that some of it
has held up and some of it has not.

The line that remains is the same one as before. **A strike is measured, a condition is
authored.** Simulate still stores nothing, and still cannot.

### Often the honest answer is "that's a mod"

A request that wants a station fitting *buildable*, as against placeable, is still asking
for game data rather than for a feature. A loose form plus an install/uninstall pair is a
handful of JSON objects in a mod, and Ostraplan's palette picks the part up for free
afterwards, because the catalogue is built from the installables across game data *and*
enabled mods. Pointing at that is a better answer than declining flatly, and it costs the
tool nothing.

## In scope

- **Designing a ship** on the game's real tile grid, with every buildable part from
  your install and your mods.
- **Validating it the way the game would**, by porting the game's own decompiled
  logic: placement, airtightness, room certification, the Ship Rating.
- **Reading a starting point in**: a core or modded ship template, a residence template,
  or your own ship or apartment out of a save.
- **Answering questions the layout can answer**: rooms, rating, propulsion figures,
  atmospheric flight characteristics, bill of materials, power connectivity, crew reach,
  and what a single impact would break.
- **Costing the work**: what a design takes to build from scratch, and what it takes to
  retrofit a ship you already have into it.
- **Writing a design into the game**: as a spawnable local mod, as a new ship added
  to a copy of a save, or back over the ship in the save you imported it from.
- **Gathering several designs into one mod**, each with its own name, condition and way of
  being obtained, saved as a pack you can export again (see
  [More than one ship per document](#out-of-scope) for why a mod may hold several ships
  while a document holds one).
- **Designing a station residence** and delivering it by those same save routes. Not as a
  mod: a residence reaches the game through a Real Estate broker, and stocking one is
  shopping rather than designing.
- **Carrying what belongs to the ship** through those writes: zones, container cargo,
  loose items, device wiring, and condition — a whole-ship wear level, or a condition
  painted part by part (see [Painting condition](#painting-condition)).
- **Naming what the ship is made of**, placed parts and loose deck items alike (see
  [Naming loose items](#naming-loose-items)).

## Out of scope

Each of these is a deliberate no, not a backlog item.

- **Save editing that has no design in it.** Crew, careers, character stats, station
  contents, money, missions. Ostraplan writes to a save only to deliver something it
  designed. An apartment is in scope precisely because a design is the input; buying one
  is not.
- **Simulating the ship.** No power, gas, thermal, crew behaviour, or orbital
  simulation. The game authors no per-device rates, so a budget would need a full network
  sim and a dishonest one is worse than none. PowerViz and WalkViz answer *connectivity*
  and *reach* from the layout, which is static data, and neither runs a sim behind it.
  Propulsion and Flight Dynamics evaluate the game's own expressions at a point you
  choose — the same thing a peak-acceleration figure is — rather than flying anything.
  **A single impact is on the same footing.** Given a strike you specify, the game's own
  damage arithmetic says which parts break, and that is a geometric question about a
  layout, answered once. What is *not* in scope is what follows an impact: fire spreading,
  a hull venting, a reactor cooking off over time. One strike is a measurement; the
  aftermath is a simulation.
- **Modelling the economy** beyond the bill of materials and the prices the game
  itself publishes.
- **More than one ship per document.** One design, one ship. Having several documents
  open at once, a tab each, is a different thing and is fine: each tab is still one
  design holding one ship.

  The **docking check** draws a second ship on the plan, ghosted at the pose that mates it
  with yours, and that is not a second ship in the document. It is an overlay, in the same
  sense as the micrometeoroid damage marks and the air-leak highlight: it is not editable,
  not saved, not exported, and it disappears with the window that asked for it. The rule
  above is about what a *design* contains, and a design still contains one ship.

  **A mod may carry several ships, and that is not a document either.** A ship pack
  ([issue #54](https://github.com/Valtora/Ostraplan/issues/54)) is a list of `.oplan` files
  with a delivery for each, exported together into one mod folder. Every design in it is
  still one design holding one ship, edited on its own; what the pack adds is the
  arrangement between them, which is a property of the *mod* and has nowhere else to live.
  The test at the top of this page decides it cleanly: designs are the input, and the
  export exists to deliver them. The line this rule draws is around a document, and the
  pack does not cross it.

  It also earns its place rather than merely passing the test. The game merges loot data
  whole-object by name, so two ships exported separately into one kiosk pool leave only the
  second: gathering them by hand is not tedious so much as quietly wrong, which is exactly
  the class of thing this tool exists to get right.
- **Managing your mods.** Ostraplan never writes `loading_order.json`. Registration,
  load order, and conflict patching are
  [Ostrasort](https://github.com/Valtora/Ostrasort) and ModTools' job, and Ostraplan
  hands off to Ostrasort rather than duplicating it.
- **Publishing to the Workshop.** Export produces a local mod. You upload it in game.
- **Running anywhere but Windows.** The app is WPF. It can run on Linux via Proton but this is not officially supported.
- **Shipping game assets.** No game data or art is distributed with the tool, ever.
  Everything is read from your own install at runtime, which is also why you cannot
  use Ostraplan without owning the game.

## Two standing safety rules

These are part of the scope, not implementation detail:

- **Read-only by default.** Ostraplan does not touch your game install or your saves **unless** you ask it to.
- **Save writes produce a copy.** Editing in place is an explicit opt-in, and it keeps
  a backup even then.

## If you are filing a feature request

Run the test above first. A request that takes a design as its input and makes it
easier to build, validate, or deliver a ship is very welcome. A request that asks
Ostraplan to edit something in your save that is not a ship you designed will be
closed with a pointer to this page.

If you are unsure which side of the line you are on, open a
[Discussion](https://github.com/Valtora/Ostraplan/discussions) rather than an issue.
Being near the line is not a problem: "add a design to a save" was near the line and
landed in scope.

---

*See also: [usage.md](usage.md) for what the tool actually does, and
[../CONTRIBUTING.md](../CONTRIBUTING.md) for contributing.*
