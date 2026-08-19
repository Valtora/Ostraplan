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
  atmospheric flight characteristics, bill of materials, power connectivity, crew reach.
- **Costing the work**: what a design takes to build from scratch, and what it takes to
  retrofit a ship you already have into it.
- **Writing a design into the game**: as a spawnable local mod, as a new ship added
  to a copy of a save, or back over the ship in the save you imported it from.
- **Designing a station residence** and delivering it by those same save routes. Not as a
  mod: a residence reaches the game through a Real Estate broker, and stocking one is
  shopping rather than designing.
- **Carrying what belongs to the ship** through those writes: zones, container cargo,
  loose items, device wiring, and a wear level.

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
- **Modelling the economy** beyond the bill of materials and the prices the game
  itself publishes.
- **More than one ship per document.** One design, one ship. Having several documents
  open at once, a tab each, is a different thing and is fine: each tab is still one
  design holding one ship.
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
