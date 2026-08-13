# Memory index

- [Bump version on every change](bump-version-on-change.md) — fix or feature → bump the version (any repo)
- [Bundle fixes, slow releases](bundle-fixes-slow-releases.md) — bump per fix but batch into one release; entries stay under [Unreleased], don't tag unless asked; release title = bare version, no tagline
- [Branded wall/floor stats via cooverlay strCondLoot](wall-mass-flattened-to-itmwall1x1.md) — skins ARE distinct via loot deltas (no refactor); Ostraplan ignored the loot (fixed v0.43.0)
- [Expose tuning as user controls (Ostraplan)](ostraplan-expose-tuning-as-user-controls.md) — prefer persisted View-menu sliders over hardcoded feel constants (but not for game-exact fidelity outputs)
- [Ostranauts shader extraction toolchain](ostranauts-shader-extraction-toolchain.md) — UnityPy + d3dcompiler_47 to disassemble LoSPass etc.; re-verify Light Viz constants per patch
- [Ostraplan Velopack release](ostraplan-velopack-release.md) — every release MUST attach Velopack artifacts (run publish.ps1); one-shot `vpk upload github --publish` creates the release + assets, then `gh release edit` for notes; vpk needs --outputDir publish\releases
- [Ostraplan WPF baml build quirk](ostraplan-wpf-baml-build-quirk.md) — direct `dotnet build` of Ostraplan.App csproj can fail BG1002 App.baml; build the .slnx or test project instead (also recurs on XAML-only incremental .slnx builds — clean the App obj/bin and rebuild)
- [Ostraplan commits straight to main](ostraplan-commits-straight-to-main.md) — no PR workflow; `--ff-only` merges; issue refs go in the subject as `(#N)`, not a `Refs:` footer
- [Ostraplan is a planner, not a save editor](ostraplan-planner-not-save-editor.md) — a feature needs a *design* as its input; pure save-state features (apartments, verbatim ship copying) are declined with reasons
- [Ostraplan button styling (Fluent ThemeMode)](ostraplan-button-styling-fluent.md) — custom Button/ToggleButton styles MUST be BasedOn the Fluent implicit style; use a ToggleButton's native checked state for active, never hard-set Background/Foreground (VSM washout)
- [Re-verify only on major game versions](ostraplan-reverify-on-major-versions.md) — no per-patch decompile sweeps; move a "verified X" stamp only for a port actually re-read; full sweep only on major versions or on request
