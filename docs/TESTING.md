# Testing

Ostraplan's regression suite is an xUnit project at `tests/Ostraplan.Tests`. It guards the ported
game logic — the placement Law, room/rating engine, save-edit write-back, export, cargo, and the
data parsers — so a change that shifts behaviour is caught before it ships.

## Running

```powershell
.\test.ps1                        # everything (Debug)
.\test.ps1 -Filter Rooms          # only tests whose full name contains "Rooms"
.\test.ps1 -Configuration Release
```

or straight through the SDK:

```powershell
dotnet test                                         # everything
dotnet test --filter FullyQualifiedName~SaveEdit    # a subset
```

A run prints `Passed / Failed / Skipped`. **Skipped is not a pass** — see the next section.

## Two tiers: game-free vs game-gated

Most tests are **game-free**: they build synthetic data with the `Fixtures` helper and run on any
machine, in a fraction of a second, unaffected by which Ostranauts version is installed. These are
the bulk of the suite and the ones that catch regressions on every change.

A minority are **game-gated**: they genuinely need a local Ostranauts install because they validate
the port against the game's *real* data — the Law parity corpus (`ParityTests`, every core ship),
real-price/economy math, sprite rendering, and the end-to-end save import/inject/export against real
saves. These call `TestData.RequireGame()` and are marked `[SkippableFact]` / `[SkippableTheory]`.

When the game **isn't** installed, a gated test reports **Skipped** (with a reason), never a false
green. That honesty is the point: a run that says `Passed: 200, Skipped: 40` tells you 40 tests
didn't actually execute, whereas the old silent `return` made them look passed. On a dev machine
*with* the game, everything runs and `Skipped` is 0.

> If you add a gated test, use `var g = TestData.RequireGame();` (not an `if (… ) return;`) **and**
> mark the method `[SkippableFact]`/`[SkippableTheory]`, or the skip surfaces as a failure.

## Writing a game-free test — the `Fixtures` builder

Prefer game-free. `Fixtures` assembles a synthetic `Catalog` the same way `Catalog.Build` wires real
data — a part's footprint tiles carry socket-add loots, and those loots carry the conditions the
engine reads (`IsWall`, `IsFloorSealed`, `IsPortal`, …) — so render layering, rooms, the Law and
analysis all behave as on real data.

```csharp
var cat = new Fixtures()
    .Floor("Floor")                 // IsFloor + IsFloorSealed
    .Wall("Wall")                   // IsWall + IsObstruction
    .Container("Box", 4, 4)         // an inventory grid
    .Build();

var doc = Fixtures.Doc(cat,
    Fixtures.P("Floor", 0, 0),
    Fixtures.P("Wall", 1, 0));

var partition = RoomBuilder.Build(ShipGrid.FromDocument(doc, cat));
```

Semantic shortcuts (`Floor`/`Wall`/`Door`/`Conduit`/`Fixture`/`Container`) cover the common tiles;
`Part(…)` takes explicit `tileConds`, socket `reqs`/`forbids`, starting conds, container grid, base
price, etc. for anything else. Pure JSON parsers (`Defs`) can be tested even more directly, against a
`JsonDocument.Parse(…)` element — see `DefsParsingTests`.

Only reach for `TestData.RequireGame()` when the assertion truly needs real game data.

## What's covered where

| Area | Game-free | Game-gated (needs install) |
|---|---|---|
| Placement Law (`CheckFit`, sockets, rotation) | `CheckFitTests`, `EngineTests` | `GameDataTests` |
| Grid math / coordinate inverse | `CoordinateMapTests`, `EngineTests` | — |
| Rooms & partitioning | `RoomBuilderTests`, `DocumentAnalysisTests` | `ParityTests` (192-ship corpus) |
| Ship rating cutoffs | `RatingGradeTests` | `RatingTests` |
| Room certification | — | `CertTests`, `ParityTests` |
| Save-edit inject (write-back) | `SaveEditInjectSyntheticTests` | `SaveEditInjectTests` (real saves) |
| Diff / identity | `SaveEditTests` (pure) | `SaveEditTests` (end-to-end) |
| Export (mapping, `mod_info` shape, nav) | `ShipExportMappingTests`, `NavConsoleTests` | `ShipExportTests` |
| `.oplan` round-trip | `EngineTests` | `SaveEditTests` |
| Cargo edit / inventory grid | `CargoEditTests`, `InventoryGridTests`, `CargoTests` | `ContainerModelTests` |
| Data parsers (`Defs`, `CondAmount`) | `DefsParsingTests`, `CondAmountTests` | — |
| Activity log / path scrubbing | `AuditLogTests` | — |
| Import (template / save) | `TemplateLoaderTests`* | `ShipImportTests`, `ShipSaveImportTests` |
| Rendering | — | `RenderSmokeTests` |

\* `TemplateLoaderTests` loads a real core ship, so it is gated too.

## Notes

- `TestData` loads the install once per run (lazily) and shares it; gated tests reuse it.
- The suite has **no external dependencies** beyond xUnit and `Xunit.SkippableFact` (the skip
  mechanism). There is no CI wired up — run it locally before committing.
- Game-gated tests are stable across game patches only where the game's own numbers are; the parity
  corpus deliberately re-verifies against the live data, so a game update that changes a baked
  `aRooms`/`aRating` is expected to surface there first.
