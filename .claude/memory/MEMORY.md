# Memory index

- [Bump version on every change](bump-version-on-change.md) — fix or feature → bump the version (any repo)
- [Bundle fixes, slow releases](bundle-fixes-slow-releases.md) — bump per fix but batch into one release; entries stay under [Unreleased], don't tag unless asked; release title = bare version, no tagline
- [Branded wall/floor stats via cooverlay strCondLoot](wall-mass-flattened-to-itmwall1x1.md) — skins ARE distinct via loot deltas (no refactor); Ostraplan ignored the loot (fixed v0.43.0)
- [Expose tuning as user controls (Ostraplan)](ostraplan-expose-tuning-as-user-controls.md) — prefer persisted View-menu sliders over hardcoded feel constants (but not for game-exact fidelity outputs)
- [Ostranauts shader extraction toolchain](ostranauts-shader-extraction-toolchain.md) — UnityPy + d3dcompiler_47 to disassemble LoSPass etc.; re-verify Light Viz constants per patch
- [Ostraplan Velopack release](ostraplan-velopack-release.md) — every release MUST attach Velopack artifacts (run publish.ps1); one-shot `vpk upload github --publish` creates the release + assets, then `gh release edit` for notes; vpk needs --outputDir publish\releases
- [Ostraplan WPF baml build quirk](ostraplan-wpf-baml-build-quirk.md) — direct `dotnet build` of Ostraplan.App csproj can fail BG1002 App.baml; build the .slnx or test project instead (also recurs on XAML-only incremental .slnx builds — clean the App obj/bin and rebuild)
- [Ostraplan button styling (Fluent ThemeMode)](ostraplan-button-styling-fluent.md) — custom Button/ToggleButton styles MUST be BasedOn the Fluent implicit style; use a ToggleButton's native checked state for active, never hard-set Background/Foreground (VSM washout)
