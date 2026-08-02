---
name: ostraplan-commits-straight-to-main
description: "Ostraplan has no PR workflow; commits land straight on main and reference issues as (#N) in the subject"
metadata: 
  node_type: memory
  type: project
  originSessionId: b5074ec8-c0eb-484e-896c-e9c09f215abe
  modified: 2026-08-02T18:46:58.024Z
---

Ostraplan has **no pull-request workflow**. Work is committed and merged straight to
`main`. When a branch is used, merge it with `--ff-only` so the individual commits stay
distinct rather than being squashed.

Its commit subjects reference the **issue** in parentheses at the end, e.g.
`feat(walk): show which tiles crew can reach and which fittings they can use (#14)`.

**Why:** those `(#N)` suffixes look exactly like the PR numbers GitHub appends on a
squash merge, so the history reads as PR-based when it is not. Confirmed by the user on
2026-08-02 after I inferred the opposite and used a `Refs: #17` footer instead.

**How to apply:** put `(#N)` at the end of the subject when a commit closes or relates to
an issue, rather than using a `Refs:` footer. Do not offer to open a PR for this repo.

Related: [[ostraplan-planner-not-save-editor]], [[bundle-fixes-slow-releases]].
