# Stage exit gate

Every stage of the architecture migration
(`Plans/ARCHITECTURE-MIGRATION.md`) must pass **all three** checks below before the
next stage begins. A stage is not "done" until this checklist is green.

## (a) Build — both target frameworks

Built in the IDE (Rider) as normal, plus a headless confirmation:

- **Desktop:** the Windows head builds for `net8.0-windows10.0.17763.0`. The project moved to
  `Src/Platforms/` when `Src/UI/WPR.UI` was dissolved on 2026-08-29, and `-p:SolutionDir=` is
  mandatory (many csprojs resolve `ProjectReference`s through it — see CLAUDE.md).
  ```bash
  dotnet build Src/Platforms/WPR.Platform.Windows/WPR.Platform.Windows.csproj -c Debug -f net8.0-windows10.0.17763.0 -maxcpucount:1 -nodeReuse:false --nologo -p:SolutionDir=<repo>/Src/
  ```
  Add `-p:IncludeAndroidTargets=false` for a desktop-only loop — otherwise the multi-targeting
  dependencies also compile their `net8.0-android` leg. `build-desktop.ps1` wraps all of this.
- **Android:** `Src/Platforms/WPR.Platform.Android` builds per the CLAUDE.md recipe (ANDROID_HOME /
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
| 0 | 15 entries (reality at the time, incl. `WPR.UI` + `WPR.UI.Android` found by the test). A full IDE build may also surface `WPR.UI.Desktop` — if so the test names it; add it to the baseline (one line). |
| 1 | unchanged (net-new WPR.Abstractions + WPR.Diagnostics; nothing references them) |
| 2 | still 15, but `WPR` → `WPR.Runtime` (the split; WPR.Loader is FNA-clean) |
| 3–4 | unchanged (renames/abstraction wiring; no leaks removed yet) |
| 5 | **empty** — FNA/Vortice severed from Runtime + Frameworks |
| 6 | empty (engine extracted clean) |
| 7 | empty; only `WPR.Backend.FNA` / `WPR.Backend.Direct3D11` reference a backend |

### Live baseline (2026-08-29) — 3 entries, test green

| Assembly | Backend | Actual cause (read out of the built IL) | Cleared by |
|---|---|---|---|
| `WPR.Platform.Windows` | FNA | `Game`, `GameComponent`, `DrawableGameComponent`, `GameWindow` — the tilt XNA components | spine stage |
| `WPR.Platform.Android` | FNA | `Game` | spine stage |
| `Microsoft.Xna.Framework.GamerServices` | FNA | `Game`, `GameComponent`, `WprGameThread`, `WprActivationGuard` | spine stage |

**Every remaining leak is FNA, and every one is the spine set** — `Game`, `GameComponent`,
`DrawableGameComponent`, `GameWindow` and nothing else. The whole remaining baseline is blocked on
the spine stage and its window-compositing product call.

Removed so far: `WPR.Runtime` + the 8 XNA facades (2026-08-07),
`Microsoft.Devices.Sensors` (2026-08-29, Stage 5d),
`WPR.Framework.Silverlight` (2026-08-29, Stage 5e — Vortice moved into `WPR.Backend.Direct3D11`),
`WPR.XnaCompability` (2026-08-29 — the assembly was deleted outright: its one FNA-derived type,
the WP7 `GraphicsDeviceManager` override, went to `WPR.Backend.FNA/Compat/`, and the two
display-mode overrides to `WPR.Framework.Xna` as `WPR.Xna.Compat.*`).

> **Reading the failure message.** This test fails in two directions. "New backend leak(s)
> detected" is a regression — fix the code. "These assemblies no longer reference a backend" is a
> *win* that has not been locked in — shrink `KnownBackendLeaks`. The second kind can appear with
> no deliberate work behind it, because a leak can disappear as a side effect of a type moving
> assemblies elsewhere. That is exactly how `Microsoft.Devices.Sensors` cleared.

> **Stale bin/ copies can mask a win.** The test scans *every* built copy of an assembly under
> `Core/`, `Backends/` and `Platforms/` and unions their references — a project's own `bin/` plus
> the copies MSBuild fans out into each referencing project's output. After moving code between
> assemblies, rebuild the dependents (or delete the stale copies) before trusting a red result;
> a single stale DLL keeps a resolved leak alive. (before it was deleted, `WPR.XnaCompability` alone had 29 such files.)
