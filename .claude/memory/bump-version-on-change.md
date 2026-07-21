---
name: bump-version-on-change
description: "Bump the project version whenever a bug is fixed or a feature is added — in any repo, not just this one"
metadata: 
  node_type: memory
  type: feedback
  originSessionId: 2251dcd1-b2f6-44b9-a070-2c36709d90e7
---

Every time work fixes a bug or adds a new feature, bump the project's version accordingly before/with the commit. This is a **general rule for any repository**, not specific to Ostraplan.

**Why:** The version is the contract users and update-checks read. Shipping a fix or feature without a version bump means the change is invisible to release tooling and users can't tell they have it. The user considers an unbumped version after a real change a defect in the workflow.

**How to apply:**
- Follow the repo's own versioning scheme and location. In .NET repos that's usually a `<Version>`/`<InformationalVersion>` in the csproj or `Directory.Build.props`; also update the changelog's `[Unreleased]` heading to the new version + date, and any in-app "version" surface.
- Match the change to the bump: a bug fix is a patch bump, a backward-compatible feature is a minor bump, a breaking change is a major bump (semver), unless the repo documents a different convention.
- Ostraplan specifics: the app version is what `Help ▸ version` shows and what the GitHub-release update check compares against; `CHANGELOG.md` uses `## [Unreleased]` then a versioned `## [X.Y.Z] — YYYY-MM-DD — <summary>` heading, and `docs/GAME-INTERNALS.md` records the verified game version. Convert `[Unreleased]` to the bumped version + today's date as part of finishing the work.
- Do this as part of completing the work, but still only **commit/push when the user asks** (see the standing rule). Relatedly: the Ostraplan export ships/kiosk feature added on 2026-07-09 was committed while still under `[Unreleased]` — that batch never got its version bump.
