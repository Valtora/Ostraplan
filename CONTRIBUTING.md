# Contributing to Ostraplan

Thanks for your interest! Ostraplan is a free, one-person project built in spare
time around a day job, so the most valuable contributions are **clear bug reports**
and **focused pull requests**. This guide covers both.

## Ways to help

- **Report a bug** — the fastest fixes come from precise repro steps. Use the in-app
  **Report a bug** button (it auto-fills your version, OS, game version and recent
  actions) or the [bug report form](https://github.com/Valtora/Ostraplan/issues/new?template=bug_report.yml).
- **Request a feature** — via the [feature form](https://github.com/Valtora/Ostraplan/issues/new?template=feature_request.yml).
  Check [What Ostraplan won't do](README.md#what-ostraplan-wont-do) first.
- **Ask or brainstorm** — [Discussions](https://github.com/Valtora/Ostraplan/discussions)
  is the place for questions and half-formed ideas.
- **Send a pull request** — see below.

## Before you write code

For anything beyond a small, obvious fix, **open an issue or Discussion first**.
Ostraplan has a deliberately narrow scope (it validates the *build*, it is not a
simulator), and agreeing on the approach up front saves us both from a PR that
can't be merged as-is.

## The one rule that defines the project

**"The Law": if it builds in Ostraplan, it must build in Ostranauts — and vice
versa.** The validation engine is a *port* of the game's own decompiled logic
(`Item.CheckFit`, `Ship.CreateRooms`, `Ship.CalculateRating`, …). That means:

- **Never** add a reference to `Assembly-CSharp.dll` from the desktop code. The
  game's types are entangled with `MonoBehaviour`/raycasts and give silently wrong
  results off the game's runtime. We re-implement the logic instead.
- If you port or change ported logic, note the **game version** you verified
  against and update [docs/GAME-INTERNALS.md](docs/GAME-INTERNALS.md) if the ported
  contract changed. Ported constants can drift when the game patches.
- No copyrighted game data or art is ever committed. Ostraplan reads everything
  from the user's own install at runtime.

## Building and testing

You need the **.NET 10 SDK**. Windows only (the app is WPF).

```powershell
dotnet run --project src\Ostraplan.App     # launch the app
.\test.ps1                                 # run the xUnit suite (Debug)
.\test.ps1 -Filter Rooms                   # run a subset by name
.\publish.ps1                              # build the Velopack release into publish\releases
```

Most tests are **game-free** and run anywhere. Tests that need a local Ostranauts
install (the Law parity corpus, real prices, sprite rendering) report as
**skipped** — never a false pass — when the game is absent. A green run is always
honest. See [docs/TESTING.md](docs/TESTING.md). Please add or update tests for any
change to validation logic.

## Pull request checklist

- Tests pass (`.\test.ps1`); add tests for logic changes.
- **Bump the version.** Any user-facing fix or feature bumps `<Version>` in
  `src/Ostraplan.App/Ostraplan.App.csproj`. The built-in update check compares this
  against GitHub release tags, so it must move when behaviour changes. (Bump per
  change; releases are cut separately and can batch several bumps.)
- **Add a `CHANGELOG.md` entry** describing the change for users.
- **Conventional Commits** for messages: `feat(planner): …`, `fix: …`,
  `docs: …`, `test: …`, `chore(release): …`.
- Keep PRs focused — one logical change is easier to review than a grab-bag.

## Style

- Match the surrounding code: its naming, comment density, and idioms. Ostraplan's
  code leans on clear comments that explain *why* a ported rule works the way it does.
- Objective, plain tone in user-facing text, docs, and commit messages.

## Licence

By contributing, you agree that your contributions are licensed under the
project's [MIT License](LICENSE).
