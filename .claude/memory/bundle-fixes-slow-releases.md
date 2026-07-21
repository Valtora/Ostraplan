---
name: bundle-fixes-slow-releases
description: "Ostraplan (and likely sibling tools) — bump the version per fix but bundle several into one release, don't cut a release each time"
metadata: 
  node_type: memory
  type: feedback
  originSessionId: dd031a74-88f3-4de2-a471-23e0f20f6ad0
  modified: 2026-07-18T19:18:57.864Z
---

As of 2026-07-17 the user wants to slow the rate of new Ostraplan releases and bundle multiple fixes into each one, rather than cutting a release per fix.

**Why:** frequent single-fix releases are noisy for users and for the update checker; batching related fixes makes for a cleaner release history and fewer update prompts.

**How to apply:**
- Still bump the csproj `<Version>` on each change (the [[bump-version-on-change]] convention holds — the dev build should identify honestly).
- Put changelog entries under `## [Unreleased]`, NOT a dated `## [X.Y.Z]` heading. The dated version heading is added only when the release is actually cut.
- Do NOT run `gh release create` / tag a release unless the user explicitly asks. Committing and pushing to `main` is fine; releasing is a separate, deliberate step.
- When the user does decide to release, promote the accumulated `[Unreleased]` block to a dated version heading matching the current csproj version.
- **Release TITLE is the bare version only** (e.g. `gh release create v0.48.0 --title "v0.48.0"`) — no feature highlight or tagline in the title (added 2026-07-18; v0.47.0/v0.48.0 shipped with "— Light Viz…" suffixes, don't repeat that). Feature summaries belong in the release NOTES body, and cadence is slowing from v0.48.0 on (fewer, more consolidated releases).
