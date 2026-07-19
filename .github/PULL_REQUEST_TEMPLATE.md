<!--
Thanks for contributing to Ostraplan! Ostraplan is a small, one-person project,
so a focused PR with a clear description is much easier to review and merge.
For anything non-trivial, please open an issue or Discussion first so we can
agree on the approach before you write the code.
-->

## What & why

<!-- What does this change, and what problem does it solve? Link the issue it closes, e.g. "Closes #12". -->

## How it was tested

<!-- Which tests you ran (`.\test.ps1`), and any manual testing in the app. If it touches ported
     game logic, say how you confirmed it still matches the game ("the Law"). -->

## Checklist

- [ ] `.\test.ps1` passes (game-gated tests may show as **skipped**, which is fine).
- [ ] I bumped `<Version>` in `src/Ostraplan.App/Ostraplan.App.csproj` (any user-facing fix or feature — see [CONTRIBUTING](../CONTRIBUTING.md)).
- [ ] I added a `CHANGELOG.md` entry describing the change.
- [ ] No game assets or copyrighted Ostranauts data are committed (Ostraplan reads everything from the user's install at runtime).
- [ ] Commits follow [Conventional Commits](https://www.conventionalcommits.org/) (`feat(planner): …`, `fix: …`).
- [ ] If this ports or touches decompiled game logic, I noted the game version I verified against and updated [docs/GAME-INTERNALS.md](../docs/GAME-INTERNALS.md) if the ported contract changed.
