# Ostraplan

<p align="center"><img src="Ostraplan-logo.png" alt="Ostraplan" width="180"/></p>

Out-of-game ship planner for **Ostranauts** (Blue Bottle Games). Drag-and-drop every buildable part onto the game's exact tile grid, with the game's own placement, room, airtightness, and Ship Rating rules enforced live.

> **The Law:** if you can build it in Ostraplan, you can build it in Ostranauts, and it will be a valid ship.

**Status:** P2 Law milestone — the foundation (mod-aware data index, palette over the game's 8 install tabs, sprite canvas with game-exact autotiling, drag-paint/box-fill/symmetry, undo/redo, `.oplan`) plus the full validation law. **P1** — a faithful `Item.CheckFit` port: you cannot place anything the game would refuse; the ghost glows green/red with the failing tiles and reason, illegal moves are flagged, construction beyond an airlock's mating face is unplaceable. **P2** — rooms/airtightness (`Ship.CreateRooms`), room certification (`RoomSpec.Matches`), and the six-slot **Ship Rating** (`Ship.CalculateRating`), reached from a "Ship Rating" button → progress bar → modal law report with unsealed-tile leak tracing. All validated parity-first against the game's own baked `aRooms`/`aRating` (rooms 188/192, certification 2109/2148 rooms exact with zero over-certifications of a real compartment, rating cutoffs unit-pinned). Docs: [docs/SPEC.md](docs/SPEC.md) (design, scope, roadmap) · [docs/GAME-INTERNALS.md](docs/GAME-INTERNALS.md) (the decompiled-game reference — algorithms, data-model gotchas, what's ported/deferred/excluded).

```powershell
dotnet run --project src\Ostraplan.App     # launch (finds the game via Steam automatically)
dotnet test                                # engine + live-game-data + render smoke tests
```

- Windows, WPF, .NET 10 (same stack as [Ostrasort](https://github.com/Valtora/Ostrasort)).
- Reads all data and sprites from your local game install at runtime — no game assets are distributed.
- Mod-aware: resolves `loading_order.json` exactly like the game, so modded parts appear in the palette.
