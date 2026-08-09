# Development

How to build, run, test, and release Ostraplan. For the rules a contribution has to
meet, see [CONTRIBUTING.md](../CONTRIBUTING.md); for what the tool is allowed to
become, see [SCOPE.md](SCOPE.md).

## Prerequisites

| | |
|---|---|
| **.NET 10 SDK** | Required. |
| **Windows** | Required. The app is WPF, so it neither builds nor runs anywhere else. |
| **An Ostranauts install** | Optional for building, required for the game-gated tests. Without it those tests report as *skipped*, never as passed. |
| **`vpk`** (Velopack CLI) | Only for cutting a release: `dotnet tool install -g vpk`. |

## Repo layout

```
Ostraplan.slnx            the solution
src/Ostraplan.Core        the engine: ported game logic, data parsing, export, save I/O
src/Ostraplan.App         the WPF app: canvas, palette, inspector, export wizard
tests/Ostraplan.Tests     the xUnit suite
docs/                     this documentation
test.ps1 / publish.ps1    the two entry-point scripts
```

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

The app takes one developer flag: `--smoke` shows and closes a native-backed WPF
window and exits, which is what `publish.ps1` uses to prove a published build actually
loads its native DLLs.

## Test

```powershell
.\test.ps1                          # everything (Debug)
.\test.ps1 -Filter Rooms            # only tests whose full name contains "Rooms"
.\test.ps1 -Configuration Release
```

Most tests are game-free and run anywhere. Tests that genuinely need a local
Ostranauts install report as **skipped**, never as a false pass, so a green run is
always honest. There is no CI: run the suite locally before you commit.

[TESTING.md](TESTING.md) covers how the suite is structured, how to write a game-free
test with the `Fixtures` builder, and what is covered where.

## Versioning

`<Version>` in `src/Ostraplan.App/Ostraplan.App.csproj` is the **single source of
truth**. It is what Help shows, what `publish.ps1` reads off the built exe to name the
artifacts, and what the in-app update check compares against GitHub release tags.

Semver, and it moves on **every user-facing change**: patch for a fix, minor for a
feature, major for a break. Bump per change, not per release. Releases are cut
separately and routinely batch several bumps.

Every bump gets a `CHANGELOG.md` entry describing the change for users, written under
`## [Unreleased]` until a release closes it into a versioned heading.

## Publishing the artifacts

```powershell
.\publish.ps1
```

**Close the running app first**: it locks its own exe and the publish will fail.
(Ostranauts itself running is fine, that is a different exe.)

`publish.ps1` does a self-contained `win-x64` publish into `publish\raw`, smoke-tests
the published exe, reads the version off it, then packs with Velopack into
`publish\releases`:

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

Every release **must** carry the Velopack artifacts. A notes-only release ships no
binaries, and every installed copy's update check then sees nothing, so the release
may as well not exist.

1. Bump `<Version>` and close off the `CHANGELOG.md` entry.
2. Run `.\publish.ps1`.
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

`VelopackApp.Build().Run()` is the first statement of an explicit `Program.Main`
(App.xaml is compiled as a `Page` so the SDK does not also generate one): the
installer's install, update, and uninstall hooks arrive as command-line args and must
be handled before any window exists. `VeloUpdate.cs` wraps the
`UpdateManager`: it downloads a new version in the background on launch and applies it
only when the user clicks **Restart to update**, so an update never costs unsaved work.

The build is not code-signed, which is why first run can trip SmartScreen.
