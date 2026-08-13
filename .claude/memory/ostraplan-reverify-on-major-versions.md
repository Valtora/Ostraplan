---
name: ostraplan-reverify-on-major-versions
description: "Decompile re-verification sweeps run only on major game versions or when Taylan asks, never per patch"
metadata: 
  node_type: memory
  type: feedback
  originSessionId: 7f8a7e16-c20a-467c-aa7f-a1370271038e
  modified: 2026-08-13T19:53:54.651Z
---

Do not re-run the decompile verification sweep against every Ostranauts patch. Full
sweeps happen only on major game versions, or when Taylan explicitly asks for one.

**Why:** He said so directly (2026-08-13) when the install had moved 1.0.0.7 to 1.0.0.9
under the docs' stamps. The suite already runs against the live install's data, so data
drift surfaces on its own; a full decompile re-read per patch is cost without signal.

**How to apply:** A "verified X" stamp (per-port comments, GAME-INTERNALS, and
`GameEnv.VerifiedGameVersion`) moves only when that port is actually re-read against the
newer decompile. If a session happens to re-read one port, move that port's stamp alone
and leave the rest. Ignore the "re-verify per patch" phrasing in GAME-INTERNALS notes:
those name what to check when a sweep runs, not how often to run one. As of 2026-08-13
the install is 1.0.0.9; flight, atmosphere and the power/rename sections are stamped
1.0.0.9, everything else still 1.0.0.7. Related: [[ostranauts-shader-extraction-toolchain]].
