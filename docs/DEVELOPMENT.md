# Development

How to build, run, test, and release Ostraplan. For the rules a contribution has to
meet, see [CONTRIBUTING.md](../CONTRIBUTING.md); for the conventions a change has to
follow, see [CONVENTIONS.md](CONVENTIONS.md); for what the tool is allowed to become,
see [SCOPE.md](SCOPE.md).

## Prerequisites

| | |
|---|---|
| **.NET 10 SDK** | Required. |
| **Windows** | Required. The app is WPF, so it neither builds nor runs anywhere else. |
| **An Ostranauts install** | Optional for building, required for the game-gated tests. Without it those tests report as *skipped*, never as passed. A **mod deploy target** does not count: `Ostranauts_Data\Mods` with no game around it is not an install and is refused as one (see [CONVENTIONS.md](CONVENTIONS.md)). |
| **`vpk`** (Velopack CLI) | Only for cutting a release: `dotnet tool install -g vpk`. |

## Repo layout

```
Ostraplan.slnx            the solution
src/Ostraplan.Core        the engine: ported game logic, data parsing, export, save I/O
src/Ostraplan.App         the WPF app: canvas, palette, inspector, export wizard
tests/Ostraplan.Tests     the xUnit suite
docs/                     this documentation
scripts/                  test.ps1 and publish.ps1, the two entry-point scripts
```

Both scripts anchor their paths on the repo root rather than the working directory, so
they run correctly from anywhere, but the documented form here is from the root.

`Ostraplan.Core` holds everything that can be tested without a window, which is why
most of the suite is game-free and fast. Keep new logic there and let the app call
into it.

## Build and run

```powershell
dotnet run --project src\Ostraplan.App     # build and launch
dotnet build Ostraplan.slnx                # build everything
```

> **Build the solution, not the app project.** `dotnet build` aimed straight at
> `src\Ostraplan.App\Ostraplan.App.csproj` can fail with
> `error BG1002: File '…\obj\Debug\net10.0-windows\App.baml' cannot be found.` It is a
> WPF markup-compile quirk in App.xaml's baml pass, not a code error, and clearing
> `obj`/`bin` does not reliably clear it. Building `Ostraplan.slnx` or the test project
> works, and both pull App and Core in as dependencies. If a XAML-only incremental
> build of the solution hits it anyway, delete `src\Ostraplan.App\obj` and
> `src\Ostraplan.App\bin` and rebuild.
>
> A bare `dotnet test` can trip it too, because its restore properties re-trigger the
> markup compile. Build the test project first, then run with `--no-build`:
>
> ```powershell
> dotnet build tests\Ostraplan.Tests\Ostraplan.Tests.csproj
> dotnet test tests\Ostraplan.Tests\Ostraplan.Tests.csproj --no-build
> ```

The app takes a few developer flags, each of which renders something and exits:

| Flag | What it does |
|---|---|
| `--smoke` | Shows and closes a native-backed WPF window. `scripts\publish.ps1` uses it to prove a published build loads its native DLLs. |
| `--bgsmoke <dir>` | Every plan backdrop on one page: the default, white, the checkerboard and each of the game's parallax locales composited, labelled with whether it flips the overlays to dark ink. Needs the install. |
| `--dlgsmoke <dir>` | The standard dialogs, light and dark, as PNGs. |
| `--invsmoke <dir>` | The inventory viewer: a synthesized backpack, an editable one, rotation, the first real save container, and an item's info panel. Needs the install. |
| `--mansmoke <dir>` | The item manifest off a real save's ship, collapsed and expanded, so the table's columns can be held against each other. Needs the install. |
| `--palsmoke <dir>` | The palette's category strip, dark and light, at three different selections, for checking that a category keeps its position and that the toggle style still chains to Fluent. |
| `--navsmoke <dir>` | The nav console arrange board, at rest and mid-drag, so the screen layout can be eyeballed against the game's. Needs the install. |
| `--svgsmoke <dir>` | A real ship's room map to SVG, validated as XML. Needs the install. |
| `--wearsmoke <dir>` | A strip of four real parts at descending condition, for holding the wear port up against the game. Needs the install. |

The preview renders are for eyeballing a layout change; they are not assertions, so they
do not replace a test.

## Test

```powershell
.\scripts\test.ps1                  # everything (Debug)
.\scripts\test.ps1 -Filter Rooms    # only tests whose full name contains "Rooms"
.\scripts\test.ps1 -Configuration Release
```

Most tests are game-free and run anywhere. Tests that genuinely need a local
Ostranauts install report as **skipped**, never as a false pass, so a green run is
always honest. There is no CI: run the suite locally before you commit.

[TESTING.md](TESTING.md) covers how the suite is structured, how to write a game-free
test with the `Fixtures` builder, and what is covered where.

## How work lands

Ostraplan has **no pull-request workflow**. It is a one-person project and work is
committed straight to `main`. Where a branch is used at all, it is merged `--ff-only` so
the individual commits stay distinct instead of being squashed into one.

Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/), and
an issue reference goes **in the subject, in parentheses at the end**:

```
feat(walk): show which tiles crew can reach and which fittings they can use (#14)
```

Not a `Refs:` footer. Those `(#N)` suffixes are the project's own convention and look
like the numbers GitHub appends on a squash merge, which is why the history reads as
PR-based when it is not.

Outside contributions are a different path and do go through a pull request. See
[CONTRIBUTING.md](../CONTRIBUTING.md), which is written for that case.

## Versioning

`<Version>` in `src/Ostraplan.App/Ostraplan.App.csproj` is the **single source of
truth**. It is what Help shows, what `scripts\publish.ps1` reads off the built exe to name
the artifacts, and what the in-app update check compares against GitHub release tags.

Semver, and it moves on **every user-facing change**: patch for a fix, minor for a
feature, major for a break. Bump per change, not per release. Releases are cut
separately and routinely batch several bumps.

Every bump gets a `CHANGELOG.md` entry describing the change for users, written under
`## [Unreleased]` until a release closes it into a versioned heading. Entries stay under
`[Unreleased]`: the dated `## [X.Y.Z] — YYYY-MM-DD` heading is written only when a
release is actually cut.

## Publishing the artifacts

```powershell
.\scripts\publish.ps1
```

**Close the running app first**: it locks its own exe and the publish will fail.
(Ostranauts itself running is fine, that is a different exe.)

It does a self-contained `win-x64` publish into `publish\raw`, smoke-tests the published
exe, reads the version off it, then packs with Velopack into `publish\releases`:

| Artifact | What it is |
|---|---|
| `Ostraplan-win-Setup.exe` | The per-user installer. |
| `Ostraplan-win-Portable.zip` | Unzip-and-run. |
| `Ostraplan-X.Y.Z-full.nupkg` | The update package the in-app updater downloads. |
| `RELEASES` / `releases.win.json` | The update manifests. `releases.win.json` is the one the updater actually reads. |
| `assets.win.json` | A vpk-internal upload manifest. Not needed on the release. |

The publish is deliberately **not** single-file: Velopack does its own bundling, and a
normal layout keeps the WPF native DLLs (PresentationNative, wpfgfx, D3DCompiler)
beside the exe.

## Cutting a release

Cutting a release is a **separate, deliberate step**, not something a change triggers.
Versions are bumped per change and releases batch several of them, so the accumulated
`[Unreleased]` block is what gets promoted to a dated heading when one is cut.

Every release **must** carry the Velopack artifacts. A notes-only release ships no
binaries, and every installed copy's update check then sees nothing, so the release
may as well not exist.

The release **title is the bare version** and nothing else (`v0.52.0`, not
`v0.52.0 — Light Viz`). Feature summaries belong in the notes body.

1. Bump `<Version>`, and promote the `[Unreleased]` block to a dated
   `## [X.Y.Z] — YYYY-MM-DD` heading matching it.
2. Run `.\scripts\publish.ps1`.
3. Publish in one shot. This creates the GitHub release **and** attaches every asset:

   ```powershell
   vpk upload github --outputDir publish\releases `
       --repoUrl https://github.com/Valtora/Ostraplan `
       --publish --releaseName vX.Y.Z --tag vX.Y.Z --token (gh auth token)
   ```

4. Apply the release notes: `vpk upload` sets the title but not a rich body, so

   ```powershell
   gh release edit vX.Y.Z --notes-file <path>
   ```

Installed and portable copies pick the new version up on their next launch, by
comparing against `releases.win.json`.

**Two traps, both learned the hard way:**

- **`--outputDir publish\releases` is not optional.** `vpk upload` defaults to `.\Releases`
  and fails with "Could not find assets file for channel 'win'" without it.
- **Do not `gh release create` first.** A pre-existing tag or release makes vpk need
  `--merge`, and risks clobbering the notes. v0.51.0 was cut that way once and
  published notes with zero binaries attached. The one-shot flow above avoids the whole
  situation.

## Distribution shape

Ostraplan installs per-user, with no admin rights and nothing written outside the user
profile:

- **Install root:** `%LOCALAPPDATA%\Ostraplan`
- **User data:** `%APPDATA%\Ostraplan` (settings, activity log, bug-report diagnostics,
  and the `autosave\` snapshot store). It survives updates and uninstalls.

Velopack replaced a self-installing, self-adopting exe in v0.49.0. That build put itself
in `%LOCALAPPDATA%\Programs\Ostraplan`, and `LegacyInstall.cs` tidies that directory away
once on first run of a Velopack build. User data never moved, so there was nothing to
migrate.

`VelopackApp.Build().Run()` is the first statement of an explicit `Program.Main`
(App.xaml is compiled as a `Page` so the SDK does not also generate one): the
installer's install, update, and uninstall hooks arrive as command-line args and must
be handled before any window exists. `VeloUpdate.cs` wraps the
`UpdateManager`: it downloads a new version in the background on launch and applies it
only when the user clicks **Restart to update**, so an update never costs unsaved work.

The build is not code-signed, which is why first run can trip SmartScreen.
