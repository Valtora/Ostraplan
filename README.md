# Ostraplan

<p align="center"><img src="Ostraplan-logo.png" alt="Ostraplan" width="180"/></p>

Out-of-game ship planner for **Ostranauts** (Blue Bottle Games). Drag-and-drop every buildable part onto the game's exact tile grid, with the game's own placement, room, airtightness, and Ship Rating rules enforced live.

> **The Law:** if you can build it in Ostraplan, you can build it in Ostranauts, and it will be a valid ship.

**Status:** P0 foundation — mod-aware data index, palette (game's own 8 install tabs + search), tile canvas with real sprites and game-exact wall autotiling, place/move/rotate (`R`, like in-game), undo/redo, `.oplan` save/load. Spec: [docs/SPEC.md](docs/SPEC.md).

```powershell
dotnet run --project src\Ostraplan.App     # launch (finds the game via Steam automatically)
dotnet test                                # engine + live-game-data + render smoke tests
```

- Windows, WPF, .NET 10 (same stack as [Ostrasort](https://github.com/Valtora/Ostrasort)).
- Reads all data and sprites from your local game install at runtime — no game assets are distributed.
- Mod-aware: resolves `loading_order.json` exactly like the game, so modded parts appear in the palette.
