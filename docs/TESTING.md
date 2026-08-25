# Testing

Ostraplan's regression suite is an xUnit project at `tests/Ostraplan.Tests`. It guards the ported
game logic — the placement Law, room/rating engine, save-edit write-back, export, cargo, and the
data parsers — so a change that shifts behaviour is caught before it ships.

For building, versioning and releasing, see [DEVELOPMENT.md](DEVELOPMENT.md).

## Running

```powershell
.\scripts\test.ps1                        # everything (Debug)
.\scripts\test.ps1 -Filter Rooms          # only tests whose full name contains "Rooms"
.\scripts\test.ps1 -Configuration Release
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
| Rooms & partitioning | `RoomBuilderTests`, `DocumentAnalysisTests` | `ParityTests` (whole core ship corpus, 220 under 1.0.0.7) |
| Ship rating cutoffs | `RatingGradeTests` | `RatingTests` |
| Room certification | — | `CertTests`, `ParityTests` |
| Save-edit inject (write-back) | `SaveEditInjectSyntheticTests` | `SaveEditInjectTests` (real saves) |
| Save grant (a new ship into a save) | `SaveGrantTests`, `SaveGrantWriteTests` (the owner-registry insert) | `SaveGrantWriteTests` (end-to-end, real saves) |
| Diff / identity | `SaveEditTests` (pure) | `SaveEditTests` (end-to-end) |
| Export (mapping, `mod_info` shape, nav) | `ShipExportMappingTests`, `NavConsoleTests` | `ShipExportTests` |
| Nav console loadout + screen arrangement | `NavConsoleTests` | `NavConsoleTests` (the stock rects), `ShipImportTests` |
| `.oplan` round-trip | `EngineTests` | `SaveEditTests` |
| Cargo edit / inventory grid | `CargoEditTests`, `InventoryGridTests`, `CargoTests` | `ContainerModelTests` |
| Canister/tank fill (capacity, shared budget, write-out) | `ContainerFillTests` | `ContainerFillTests` (the capacity against real defs, and which gases the data declares) |
| Repair (broken def → working def; clearing `StatDamage`) | `RepairTests` | `RepairTests` (the repair/undamage job split, and themed walls) |
| Data parsers (`Defs`, `CondAmount`) | `DefsParsingTests`, `CondAmountTests` | — |
| Activity log / path scrubbing | `AuditLogTests` | — |
| Import (template / save) | `TemplateLoaderTests`* | `ShipImportTests`, `ShipSaveImportTests` |
| Deck items (footprint occupancy, the loose socket law) | `LoosePlacementTests` | `LooseObjectTests` |
| Rendering | — | `RenderSmokeTests` |
| The bake window (what the ship cache may cull) | — | `BakeWindowTests`† |
| Document tabs (session isolation, the strip, close/cycle) | `DocumentSessionTests`, `MainWindowTabsTests`† | — |
| Auto-save rotation (per path, per untitled slot) | `AutoSaveTests` | — |
| UI scale (clamp, popup layer) | `SettingsTests`, `UiScalePopupTests`† | — |

`RenderSmokeTests` is mostly smoke — it writes PNGs for eyeballing and asserts only that they are not blank.
`Every_label_on_the_plan_reads_upright_at_any_view_rotation` is the exception and is a real assertion: it renders
a scene carrying every kind of on-canvas text, reads the drawing back with `VisualTreeHelper.GetDrawing`, and
checks each glyph run's accumulated transform has no rotation and no flip. That holds the whole class of "text on
the plan" to the rule rather than one label at a time, which is how the zone name came to be the only label that
did not counter-rotate.

\* `TemplateLoaderTests` loads a real core ship, so it is gated too.

† These build WPF controls, so each runs its body on an STA thread of its own (see the `RunSta` helper each declares). `MainWindowTabsTests` constructs the real `MainWindow`, which is game-free because game data loads on `Loaded` and that is never raised — but it does mean it catches a constructor that throws, which is a crash on launch rather than a bug anyone could report.

`BakeWindowTests` compares the canvas against **itself** rather than against stored images, so it survives a
game update that changes a sprite. Its ground-truth checks render the same frame with the culling turned off
(`ShipCanvas.BakeWholeDesign`, internal and used nowhere else) and hold the culled frame against that — the
checks that only compare two routes to a view cannot catch a window that is uniformly too tight, since both
sides then lose the same content and agree.

## The perf benchmark

`PerfBenchmark` is a **third tier**: a timing printout, not an assertion. It loads three real
templates spanning the scale range (a large player ship, a mid station, and the largest template
the game ships) and reports what the per-edit repaint and each analysis pass cost on them.

Nothing in it fails on a number, because a timing depends on the machine and what else is running.
It exists so a change aimed at responsiveness can be held against a before, in the same way
`--dlgsmoke` exists to eyeball a layout change. It is game-gated like the rest, and carries
`[Trait("Category", "Benchmark")]` so a normal run leaves it out.

```powershell
.\scripts\test.ps1 -Benchmark      # run it, timings printed
```

Take the render figures as **comparable against themselves**, not as frame times. They rasterise
offscreen through `RenderTargetBitmap`, which is software, so they read slower than the live
compositor does; the bake half of each figure is the same work either way.

## Notes

- `TestData` loads the install once per run (lazily) and shares it; gated tests reuse it.
- The suite has **no external dependencies** beyond xUnit and `Xunit.SkippableFact` (the skip
  mechanism). There is no CI wired up — run it locally before committing.
- Game-gated tests are stable across game patches only where the game's own numbers are; the parity
  corpus deliberately re-verifies against the live data, so a game update that changes a baked
  `aRooms`/`aRating` is expected to surface there first.
