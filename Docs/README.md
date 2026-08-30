# Docs

Technical reference for WPR — how the thing is put together and how to build it.

For the plain-English introduction, start at the [README](../README.md).
For forward-looking design work — the architecture migration, stage scopes,
feasibility studies — see [`Plans/`](../Plans/README.md).

## Current

| Doc | What it covers |
| --- | --- |
| [ARCHITECTURE.md](ARCHITECTURE.md) | How WPR runs a game: the install/patch pipeline, the package types, the project layout, reinstall-vs-rebuild, where runtime data lives |
| [BUILDING.md](BUILDING.md) | Prerequisites, desktop and Android builds, run configurations, the Android TFM gating, packaging scripts, tests, troubleshooting a fresh clone |
| [RELEASING.md](RELEASING.md) | The manual-dispatch release workflow, what it produces, and Android signing secrets |

[`CLAUDE.md`](../CLAUDE.md) at the repo root is the working-practice companion to
these — the conventions and gotchas that apply while you're editing, rather than
the structure itself.

## Historical

Kept for reference; both describe work that has already happened and neither is
maintained.

| Doc | What it covers |
| --- | --- |
| [Migration_Documentation.md](Migration_Documentation.md) | Record of the original `OldSrc` → `Src` project migration (Russian) |
| [Code Update Result.txt](<Code Update Result.txt>) | Record of an early build-fix pass over Icons.Avalonia and the Android head |
