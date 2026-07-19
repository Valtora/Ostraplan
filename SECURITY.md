# Security Policy

Ostraplan is a desktop app that runs on your machine. It's worth being clear about
what it can touch and how to report a problem.

## What Ostraplan does on your system

- **Reads** your Ostranauts install (game data and sprites) and your
  `loading_order.json` — read-only, to mirror the game's rules and show modded parts.
- **Writes** only where you ask it to: exported mods, PNG snapshots, and save-game
  edits. Save write-back defaults to creating a **copy**; an opt-in in-place edit
  makes a backup first.
- **Keeps** its own settings and a scrubbed activity log under `%APPDATA%\Ostraplan\`.
- **Optionally checks for updates** against GitHub Releases, and — if you choose to
  self-install — downloads and runs a release executable.

It ships **no** game assets and makes no network calls beyond the optional update check.

## Supported versions

This is an actively developed, pre-1.0 tool. Only the **latest release** is
supported; please reproduce any issue on the newest version before reporting.

## Reporting a vulnerability

**Please do not open a public issue for a security problem.**

Use GitHub's private reporting instead: go to the
[**Security** tab](https://github.com/Valtora/Ostraplan/security/advisories/new)
and **Report a vulnerability**. This opens a private channel visible only to the
maintainer.

Please include:

- What the issue is and its potential impact.
- Steps to reproduce, and the Ostraplan and OS versions.
- Any proof-of-concept, if you have one.

As a one-person project I can't promise a fixed timeline, but I'll acknowledge your
report as soon as I can, keep you posted on the fix, and credit you when it ships
unless you'd rather stay anonymous. Thank you for reporting responsibly.
