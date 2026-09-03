# Stage exit gate

Every remaining stage of the architecture migration
(`Plans/ARCHITECTURE-MIGRATION.md`) must pass **all three** checks below before the
next stage begins. A stage is not "done" until this checklist is green.

## (a) Build — both target frameworks

Built in the IDE (Rider) as normal, plus a headless confirmation:

- **Desktop:** the Windows head builds for `net8.0-windows10.0.17763.0`.
  `-p:SolutionDir=` is mandatory (many csprojs resolve `ProjectReference`s through it —
  see CLAUDE.md).
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

> **Outstanding:** the Launcher column has never been filled in. Do it on the next
> reinstall-forcing stage so the gate is reproducible without guessing.

Because the migration changes assembly identities/patcher tables, **reinstall both
games** (not just relaunch) before checking — the install-time IL rewrite must
re-run. See CLAUDE.md ("reinstall <game>" vs "rebuild").

For anything touching teardown (the Stage-4 host promotion, the spine stage), a
**launch → exit → relaunch** cycle is the check, not a single launch — the regressions
that ordering prevents only appear on the second run in a process.

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

### Live baseline (2026-09-01) — **EMPTY**. Stage 5's fitness criterion is met.

`KnownBackendLeaks` holds nothing. The only assemblies referencing FNA or Vortice are the
adapters in `AllowedReferrers`: `WPR.Backend.FNA`, `WPR.Backend.Direct3D11` and
`WPR.Audio.FAudio`.

The last two entries — both platform heads — cleared together:

| Assembly | Was leaking | Cleared by |
|---|---|---|
| `WPR.Platform.Windows` | `Game`, `GameComponent`, `DrawableGameComponent`, `GameWindow` | the two tilt `GameComponent`s moved to `WPR.Backend.FNA/Input/` behind `ITiltEmulationHost`; the window icon now travels down as pixels (`GameWindowIcon`) instead of through an `Action<Game>` hook |
| `WPR.Platform.Android` | `Game` — **type reference only, no member touched** | the same `Action<Game>` parameter disappearing from `FnaGameHost`'s ctor. The head never passed one; its call site named the full signature |

> **This did NOT require the spine stage**, contrary to what this document and
> ARCHITECTURE-MIGRATION §5 previously asserted. A `GameComponent` does have to derive from
> *something*, and it still derives from FNA's — but a type deriving from a backend type is only a
> *leak* when it lives outside an allowed referrer. Moving the deriving types into the backend was
> always sufficient. The window-compositing product call still gates Stage 5f itself (games binding
> only WPR-owned identities), just not this baseline.

**Two traps this stage walked into, both already documented below — read them before trusting a
red result.** The stale-copy union kept both heads "leaking" from their *Release* outputs long
after Debug was clean; and the scan picked up `wprharness`, the scratch console harness CLAUDE.md
tells you to build into the desktop head's output directory. The second was a real hole in the
test, fixed the same day: it now governs only assemblies a `.csproj` in the tree actually produces
(reading `<AssemblyName>`, since several projects deliberately ship under a WP7 identity).

### Target burn-down for the remaining stages

| Stage | Expected `KnownBackendLeaks` after the stage |
|---|---|
| 5f (spine) | already **empty** — reached 2026-09-01, ahead of this stage. 5f no longer has a baseline to burn down; its remaining job is the identity criterion (games binding only WPR-owned identities), which the fitness test does not measure |
| 6 | empty (engine extracted clean; extend the test to cover the new engine projects) |
| 7 | empty; the only backend referrers are the adapters in `AllowedReferrers` — `WPR.Backend.FNA`, `WPR.Backend.Direct3D11` and `WPR.Audio.FAudio` (permanent: `FNADllMap` resolves natives only for P/Invokes declared in FNA) |

> **Reading the failure message.** This test fails in two directions. "New backend leak(s)
> detected" is a regression — fix the code. "These assemblies no longer reference a backend" is a
> *win* that has not been locked in — shrink `KnownBackendLeaks`. The second kind can appear with
> no deliberate work behind it, because a leak can disappear as a side effect of a type moving
> assemblies elsewhere. That is exactly how `Microsoft.Devices.Sensors` cleared.

> **Stale bin/ copies can mask a win.** The test scans *every* built copy of an assembly under
> `Core/`, `Backends/`, `Platforms/` and `Audio/` and unions their references — a project's own `bin/` plus
> the copies MSBuild fans out into each referencing project's output. After moving code between
> assemblies, rebuild the dependents (or delete the stale copies) before trusting a red result;
> a single stale DLL keeps a resolved leak alive.
