---
name: ostraplan-velopack-release
description: Ostraplan releases MUST ship Velopack artifacts; publish.ps1 + one-shot vpk upload github --publish creates the release AND attaches assets
metadata: 
  node_type: memory
  type: project
  originSessionId: 2732ed0a-ae71-46f9-b778-8f5c63c655fa
  modified: 2026-07-20T09:51:34.853Z
---

Since v0.49.0 (2026-07-19) Ostraplan ships via **Velopack**, not the old self-install/self-adopt exe. `VelopackApp.Run()` is the first line of an explicit `Program.Main` (App.xaml is compiled as a `Page` so the SDK doesn't also generate a Main). `VeloUpdate.cs` wraps the `UpdateManager` (background download on launch, apply-on-restart-click). Install root is `%LOCALAPPDATA%\Ostraplan`; user data stays in `%APPDATA%\Ostraplan` (no migration needed). `LegacyInstall.cs` tidies the old `%LOCALAPPDATA%\Programs\Ostraplan` self-install once.

**Every Ostraplan release MUST attach the Velopack artifacts** (`Ostraplan-win-Setup.exe`, `Ostraplan-win-Portable.zip`, `Ostraplan-X.Y.Z-full.nupkg`, `RELEASES`, `releases.win.json`) — a notes-only release ships **no binaries** and users' in-app update check sees nothing. So when cutting a release, always run the publishing workflow, not just `gh release create`.

**Release recipe (user's preferred one-shot flow):**
1. Bump `<Version>` in `Ostraplan.App.csproj`, close Ostraplan (the game Ostranauts running is fine — it locks a different exe), run `.\publish.ps1` (needs `dotnet tool install -g vpk` once). It publishes self-contained, runs a WPF `--smoke` test, and `vpk pack`s artifacts into `publish\releases`.
2. `vpk upload github --outputDir publish\releases --repoUrl https://github.com/Valtora/Ostraplan --publish --releaseName vX.Y.Z --tag vX.Y.Z --token (gh auth token)` — this **creates the GitHub release and attaches every asset in one go**. Do **not** `gh release create` first; a pre-existing tag/release makes vpk need `--merge` and risks clobbering the curated notes.
3. Notes: the cut-release skill drafts curated notes. `vpk upload` sets the title but not a rich body, so after the one-shot upload apply the approved notes with `gh release edit vX.Y.Z --notes-file <path>`.

**Gotchas:**
- `vpk upload` defaults its releases dir to `.\Releases` and fails with "Could not find assets file for channel 'win'" if you omit `--outputDir publish\releases`. Ostrasort's in-repo recipe has the **same latent bug** (it hasn't cut a Velopack release yet).
- `publish\releases` also contains `assets.win.json` — that's a vpk-internal upload manifest; the release only needs the five assets above (the updater reads `releases.win.json`, not `assets.win.json`).
- **v0.51.0 was cut wrong the first time**: `gh release create` published notes with zero binaries. The fix was `publish.ps1` then `gh release upload v0.51.0 <assets> --clobber` onto the existing release. Avoid the whole situation by using the one-shot vpk flow from the start. The generic `cut-release` skill now has a Step 6 that forces confirming/attaching artifacts before publishing.

Note: the workspace `CLAUDE.md` (one dir up) still describes Ostraplan's updater as the self-adopting `Updater.cs` overwriting `%LOCALAPPDATA%\Programs\Ostraplan` — that is now **stale**; reality is Velopack. Same stale description exists for Ostrasort. See [[bump-version-on-change]] and [[bundle-fixes-slow-releases]].
