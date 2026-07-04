# Ostraplan

Out-of-game ship planner for **Ostranauts** (Blue Bottle Games). Drag-and-drop every buildable part onto the game's exact tile grid, with the game's own placement, room, airtightness, and Ship Rating rules enforced live.

> **The Law:** if you can build it in Ostraplan, you can build it in Ostranauts, and it will be a valid ship.

**Status:** specification phase — see [docs/SPEC.md](docs/SPEC.md). No code yet.

- Windows, WPF, .NET 10 (same stack as [Ostrasort](https://github.com/Valtora/Ostrasort)).
- Reads all data and sprites from your local game install at runtime — no game assets are distributed.
- Mod-aware: resolves `loading_order.json` exactly like the game, so modded parts appear in the palette.
