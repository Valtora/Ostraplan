---
name: ostraplan-wpf-baml-build-quirk
description: Building the Ostraplan.App csproj directly can fail with BG1002 App.baml not found; build the solution or test project instead
metadata:
  type: project
---

Running `dotnet build src/Ostraplan.App/Ostraplan.App.csproj` (and even `-t:Rebuild`, and `dotnet test` on the test project) can fail intermittently with:

`error BG1002: File '…\obj\Debug\net10.0-windows\App.baml' cannot be found.`

This is a WPF markup-compile quirk (App.xaml's baml pass), not a code error — nuking `obj`/`bin` does **not** reliably fix it.

**How to build reliably instead:**
- `dotnet build Ostraplan.slnx` (the whole solution) — works.
- `dotnet build tests/Ostraplan.Tests/Ostraplan.Tests.csproj` — pulls App+Core as dependencies and builds them fine.
- For tests: `dotnet build tests/…csproj` first, then `dotnet test tests/…csproj --no-build` (running `dotnet test` without a prior plain build re-triggers the baml failure via its restore properties).

**Why:** confirmed 2026-07-19 while fixing issue #9 + the bug-report work — every direct App-csproj build hit BG1002 while the solution/test-project builds succeeded with 0 errors.
