# Stage exit gate

Every stage of the architecture migration
(`docs/ARCHITECTURE-MIGRATION.md`) must pass **all three** checks below before the
next stage begins. A stage is not "done" until this checklist is green.

## (a) Build — both target frameworks

Built in the IDE (Rider) as normal, plus a headless confirmation:

- **Desktop:** `WPR.UI.Desktop` builds for `net8.0-windows10.0.17763.0`.
  ```bash
  dotnet build Src/UI/WPR.UI.Desktop/WPR.UI.Desktop.csproj -c Debug -f net8.0-windows10.0.17763.0 -maxcpucount:1 -nodeReuse:false --nologo
  ```
- **Android:** `WPR.UI.Android` builds per the CLAUDE.md recipe (ANDROID_HOME /
  JAVA_HOME env + `-p:AndroidSdkDirectory`).

## (b) Smoke titles — reach gameplay after reinstall

The two canonical acceptance games (locked 2026-08-05):

| Title | Launcher | Notes |
|---|---|---|
| **Minesweeper** | (fill in on first run) | must reach interactive gameplay |
| **MonstaFish** | (fill in on first run) | must reach interactive gameplay |

Because the migration changes assembly identities/patcher tables, **reinstall both
games** (not just relaunch) before checking — the install-time IL rewrite must
re-run. See CLAUDE.md ("reinstall <game>" vs "rebuild").

## (c) Dependency-fitness test — baseline matches

Run `WPR.Tests` after a full solution build:

```bash
dotnet test Src/Tests/WPR.Tests/WPR.Tests.csproj -c Debug
```

`BackendIsolationTests.Backend_references_match_documented_baseline` must pass.
When a stage removes an FNA/Vortice leak, the test will fail asking you to shrink
`KnownBackendLeaks` — do that in the same stage so the win is locked in. The test
is the machine-checkable half of the "Runtime/Frameworks/Engine have no FNA
references" success criteria.

### Baseline burn-down (update as stages land)

| Stage | Expected `KnownBackendLeaks` after the stage |
|---|---|
| 0 | 15 entries (current reality, incl. `WPR.UI` + `WPR.UI.Android` found by the test). A full IDE build may also surface `WPR.UI.Desktop` — if so the test names it; add it to the baseline (one line). |
| 1 | unchanged (net-new WPR.Abstractions + WPR.Diagnostics; nothing references them) |
| 2 | still 15, but `WPR` → `WPR.Runtime` (the split; WPR.Loader is FNA-clean) |
| 3–4 | unchanged (renames/abstraction wiring; no leaks removed yet) |
| 5 | **empty** — FNA/Vortice severed from Runtime + Frameworks |
| 6 | empty (engine extracted clean) |
| 7 | empty; only `WPR.Backend.FNA` / `WPR.Backend.Direct3D11` reference a backend |
