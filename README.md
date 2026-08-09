# WPR 0.0.18-alpha
![](Images/Wpr_logo.png)

WPR is a Windows Phone 7/8 game runner that re-hosts XNA and Silverlight
titles on modern Windows desktop and Android — plus a rail for launching
externally-rebuilt native ports of games that can't be hosted at all. This is
a fork of the original [WPR](https://github.com/8212369/WPR) — heavily
modified to target **.NET 8 + Avalonia 11.3.9** with a runtime shim layer that
lets unmodified game `.xap` packages run against modern .NET.

> **Status:** work-in-progress. The `main` branch is not guaranteed to build
> or run cleanly at any given checkpoint. Development happens on `main` plus
> short-lived per-feature branches, and a **layered-architecture migration is
> currently in flight** (Stage 5 of 8 — see
> [Docs/ARCHITECTURE-MIGRATION.md](Docs/ARCHITECTURE-MIGRATION.md)), so project
> names and assembly boundaries are still moving.

## Screenshots
![](Images/sshot01.png)

## What's new in this fork

Since branching from upstream WPR:

- **.NET 8 / Avalonia 11.3.9 port.** Replaced the legacy Avalonia 0.9/0.10
  UI stack; rebuilt the desktop and Android entry points
  (`WPR.UI.Desktop`, `WPR.UI.Android`).
- **Silverlight runtime.** Added `WPR.Framework.Silverlight` (namespace
  `WPR.SilverlightCompability`), a from-scratch reimplementation of
  Silverlight 4 / Windows Phone XAML controls on top of Avalonia. Layout,
  gestures and the Panorama / Pivot parallax state machine are written
  in-tree (no Silverlight parser dependency), with a Vortice/D3D11 path for
  `DrawingSurface` content. Launched via `SilverlightLauncher.LaunchAsync`.
- **WPR-owned XNA type system.** `WPR.Framework.Xna` now *defines* the XNA
  API surface (134+ public types: math/value types, enums, packed vectors,
  input structs, component interfaces) instead of borrowing FNA's, and
  exposes backend seams (`WPR.Xna.Rhi` — `IGraphicsBackend`, `IAudioBackend`,
  `IInputBackend`, `IMediaBackend`, `IStorageBackend`, `IXactBackend`) that
  `Backends/WPR.Backend.FNA` implements. This is the core of the ongoing
  migration off a hard FNA dependency.
- **Layered-architecture migration.** The monolithic `WPR` project was split
  into `WPR.Loader` / `WPR.Runtime` / `WPR.Abstractions` / `WPR.Diagnostics`,
  the shims renamed to `WPR.Framework.*` (game-visible assembly identities
  preserved), and backends moved under `Src/Backends/`. Progress is guarded
  by a dependency-fitness test (`Src/Tests/WPR.Tests`).
- **Unity / native port rail.** Titles whose original build is a native ARM
  Unity app can't be hosted in-process; WPR instead launches an
  externally-rebuilt standalone port described by a `wpr-port.json` manifest
  in the install folder (`UnityPortLauncher`, `ApplicationType.UnityPort`).
- **GameMaker fast path.** GameMaker Studio exports (`Assets/game.win`) are
  detected and run through their own runner (`GameMakerLauncher`), with
  achievements bridged back into the normal store.
- **Persistent achievements.** Per-game achievement progress is seeded at
  install time (`XnaAchievementSeeder` scrapes TrueAchievements once and
  populates a SQLite DB) and stored across runs.
- **Refactored shim layout.** `WPR.Framework.Silverlight`'s source tree
  mirrors the upstream Silverlight namespace hierarchy (one C# file per
  type, file path matches the real namespace) — see
  [CLAUDE.md](CLAUDE.md) for the convention.
- **Per-game debug logs.** `ApplicationLaunch` mirrors `Trace`/`Debug`
  output to `%LocalAppData%\WPR\AppData\<ProductId>\wpr_game_debug.log` so
  silent-crash games leave a diagnostic file.
- **Keyboard accelerometer.** Bind keys to simulate phone tilt for games
  that use `Microsoft.Devices.Sensors.Accelerometer`. The Controls page
  (sidebar) lets you set the four tilt directions, adjust sensitivity,
  toggle the in-game tilt overlay, and live-preview the synthesized
  reading. Orientation-aware: in landscape games the screen-relative
  intent (W = "tilt up the screen") is rotated into the device-portrait
  frame the WP7 sensor contract expects.
- **Startup health-check & explicit Avalonia init logging** on both desktop
  and Android targets.
- **FontAwesome icon provider** registered in the AppBuilder; main app
  list now populates on launch (was waiting on search input).
- **Android target rebuilt** against Avalonia 11; min-SDK raised; SDL2 +
  FFmpeg bindings included via Java bindings projects.


## Architecture

WPR runs a Windows Phone game's original assemblies, IL-rewritten at
install time to redirect WP/Silverlight/XNA API calls to in-tree shims.

```
.xap / XNA folder
      │
      ▼ LibraryScanner          (discovers packages)
      ▼ ApplicationInstaller    (unpacks to %LocalAppData%\WPR\AppData\<ProductId>)
      ▼ ApplicationPatcher      (Cecil-rewrites every .dll; leaves .dll.original)
      ▼ XnaAchievementSeeder    (populates SQLite achievements DB)
      │
      ▼ (user clicks "Run")
      ├─ UnityPortLauncher.TryLaunchAsync        (wpr-port.json → spawn standalone port)
      ├─ GameMakerLauncher                       (Assets/game.win → GameMaker runner)
      ├─ SilverlightLauncher.LaunchAsync         (Silverlight XAPs, in-process on Avalonia)
      └─ XnaLauncher → ApplicationLaunch.Start   (XNA games, WPR.Backend.FNA host)
```

Project layout (`Src/`):

Several projects were renamed during the layered-architecture migration
(see [Docs/ARCHITECTURE-MIGRATION.md](Docs/ARCHITECTURE-MIGRATION.md)).
Note that **project names and namespaces deliberately diverge** in places —
`WPR.Framework.Silverlight` still declares `namespace WPR.SilverlightCompability`,
and `WPR.XnaCompabilityPatch` builds the assembly `WPR.XnaCompability`, because the
patcher tables target those names.

| Project | Role |
| --- | --- |
| `Core/WPR.Loader` | Install/patch pipeline (`ApplicationInstaller`, `ApplicationPatcher`), models, EF Core DB |
| `Core/WPR.Runtime` | Launch/hosting glue (`SilverlightAppHost`, `GameMakerLauncher`) |
| `Core/WPR.Abstractions` | Backend-independent host contracts (`IGameHost`, `IWindow`, `IAudioDevice`, …) |
| `Core/WPR.Common` | Paths, configuration, image/env helpers |
| `Core/WPR.Diagnostics` | Logging (`WprLog`, `FileLog`) |
| `Core/WPR.Framework.Xna` | **WPR-owned XNA type system** — Graphics, Audio, Media, Content, Input, Storage, plus the `WPR.Xna.Rhi` backend seams (`Backend/I*Backend.cs`) |
| `Core/WPR.Framework.Silverlight` | Silverlight 4 / WP XAML re-impl on Avalonia (+ Vortice/D3D11 `DrawingSurface` path) |
| `Core/WPR.Framework.Phone` | `Microsoft.Phone.*` facade (Shell, Tasks, Marketplace, Scheduler, …) |
| `Core/WPR.Framework.Devices.Sensors` | Accelerometer / Compass |
| `Core/WPR.Framework.Devices.Location` | `System.Device.Location` |
| `Core/WPR.WindowsCompability` | `System.Windows.*` shims (Application, BitmapImage, IsolatedStorage, …) |
| `Core/WPR.StandardCompability` | `System.ServiceModel` / WCF-lite shims |
| `Core/WPR.XnaCompabilityPatch` | XNA-side shims layered on top of the XNA type system |
| `Core/Microsoft.Xna.Framework.GamerServices` | Gamer profile, achievements, leaderboards |
| `Backends/WPR.Backend.FNA` | FNA backend — implements the `WPR.Xna.Rhi` seams + hosts the game loop (`ApplicationLaunch`, `FnaGameHost`) |
| `Backends/FNA.Platform` | FNA fork (builds assembly `FNA`): native-backed runtime, window, SDL/FNA3D/FAudio/Theorafile bindings |
| `UI/WPR.UI` | Shared Avalonia UI (views, view-models, launchers, tilt input) |
| `UI/WPR.UI.Desktop` | Windows entry point (net8.0-windows10.0.17763.0) |
| `UI/WPR.UI.Android` | Android entry point (net8.0-android34.0) |
| `Tests/WPR.Tests` | Dependency-fitness test guarding the backend-isolation baseline |
| `Core/WPR.SilverlightCompability.Tests` | Unit tests for the Silverlight/XAML re-impl |
| `JavaBindings/*` | Android bindings: SDL2 (`Org.Libsdl.App`), FFmpegKit |
| `ThirdParty/Icons.Avalonia` | Vendored Projektanker icons, patched for Avalonia 11.3.9 |
| `ThirdParty/assembly-store-reader` | Reads Android assembly stores (port/APK inspection) |

A `WPR.Backend.Direct3D11` (Vortice) backend is planned but not yet stood up —
today the Silverlight side references Vortice directly.

See [CLAUDE.md](CLAUDE.md) for the in-depth build/install/patch workflow,
including the rule that **patcher table changes require reinstalling
affected games** (the IL rewrite happens once at install time).

### Design docs

| Doc | What it covers |
| --- | --- |
| [ARCHITECTURE-MIGRATION.md](Docs/ARCHITECTURE-MIGRATION.md) | The layered-redesign ADR: target dependency graph, the 8 stages, what's landed |
| [STAGE-GATE.md](Docs/STAGE-GATE.md) | The three checks every migration stage must pass before the next begins |
| [STAGE5-SIZING.md](Docs/STAGE5-SIZING.md) | Per-project audit of the FNA severance |
| [STAGE5C-SCOPE.md](Docs/STAGE5C-SCOPE.md) | The RHI seam design — why it mirrors the FNA3D C API |
| [Unity_WP8_Feasibility.md](Docs/Unity_WP8_Feasibility.md) | Why Unity WP8 titles need the rebuilt-port rail instead of hosting |


## Build & run

Recommended:

1. Open `Src/WPR.sln` in **Rider** (or VS 2022 17.8+).
2. Build → run `WPR.UI.Desktop`.

Target frameworks:

- Desktop: `net8.0-windows10.0.17763.0`
- Android: `net8.0-android34.0` (API 34 — the only API level the .NET 8
  Android workload supports; see [CLAUDE.md](CLAUDE.md) for the SDK/workload
  pitfalls and the CLI recipe)

A repo-root `global.json` pins the build to SDK **8.0.421**; without it
MSBuild picks the .NET 10 SDK and the Android leg fails to resolve
`Mono.Android`.

### Packaging scripts

Two repo-root PowerShell scripts produce a runnable artifact without opening
the IDE. Both default to **Release**, write into `Artifacts/<target>/<Configuration>/`
(gitignored), and auto-detect the SDK / Android / JDK paths.

```pwsh
.\build-desktop.ps1          # -> Artifacts\desktop\Release\WPR.UI.Desktop.exe
```

```pwsh
.\build-android.ps1          # -> Artifacts\android\Release\com.wpr.android-Signed.apk
```

Useful switches — desktop: `-Configuration Debug`, `-SelfContained` (bundles
the .NET runtime), `-NoPublish` (plain build, output stays in `bin\`), `-Clean`,
`-Run`. Android: `-Configuration Debug`, `-Clean`, `-Install` (`adb install -r`
to a connected device).

A plain `dotnet build -c Release` on the Android project stops short of
packaging, so the script adds `-t:SignAndroidPackage`. The APK is signed with
the local **debug keystore** — fine for sideloading, not for a Play upload.
For a real key, set `AndroidKeyStore=true` plus `AndroidSigningKeyStore` /
`KeyAlias` / `StorePass` / `KeyPass` on the project.

Both pass `-p:SolutionDir=` explicitly. That's required from the CLI:
`Src/Backends/FNA.Platform/Directory.Build.props` shadows the `Src/` one that
defines `SolutionDir` (nearest-wins, and it doesn't import the parent), so
`FNA.Core.csproj` can't resolve `$(SolutionDir)Core\WPR.Framework.Xna` and the
build cascades into CS0246 on every XNA type. Rider/VS don't hit this because
the `.sln` supplies `SolutionDir` as a global property.

`build-android.sh` is the Linux equivalent of the Android script.

### Publishing a release (CI)

[`.github/workflows/release.yml`](.github/workflows/release.yml) builds both
distributables and attaches them to a GitHub Release. It is **manual dispatch
only** — Actions → *Release* → *Run workflow* — and takes:

| Input | Meaning |
| --- | --- |
| `version` | `MAJOR.MINOR.PATCH`, e.g. `0.0.18`. Becomes the tag `v0.0.18`, the installer version, and the APK `versionName`. |
| `android_version_code` | Integer `versionCode`. Blank uses the workflow run number. Must increase between releases or Android refuses the upgrade. |
| `publish_release` | Off = build only, grab the workflow artifacts (useful for a dry run). |
| `prerelease` | Marks the GitHub Release as a pre-release. |

Produces `WPR-Setup-<version>.exe` (self-contained x64 — users need no .NET
install) and `WPR-<version>.apk`. The Windows leg publishes then compiles
[`Packaging/windows/WPR.iss`](Packaging/windows/WPR.iss); the Android leg runs
on Linux with the .NET Android workload.

#### Android signing

Without a keystore, .NET Android signs with a **debug key generated fresh on
each runner** — so every release gets a different signature and users get
*"App not installed"* when updating over a previous version. To fix that, create
a key once and store it as repository secrets:

```bash
keytool -genkeypair -v -keystore wpr.keystore -alias wpr -keyalg RSA -keysize 2048 -validity 10000
```

Then add four repository secrets (Settings → Secrets and variables → Actions):
`ANDROID_KEYSTORE_BASE64` (the file as base64 — `base64 -w0 wpr.keystore`),
`ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`, `ANDROID_KEY_PASSWORD`.
The workflow picks them up automatically and warns in the run log when they are
absent. **Keep the keystore file backed up** — losing it means never being able
to ship an in-place upgrade again.

### CLI build (for quick edit-verify)

The full-solution `dotnet build` hits `NU1202` on `Avalonia.Android` if the
workload version doesn't line up. To verify a small edit on a leaf project:

```pwsh
dotnet build <project>.csproj -c Debug `
    -f net8.0-windows10.0.17763.0 `
    -maxcpucount:1 -nodeReuse:false --nologo
```

The `-maxcpucount:1` flag avoids an MSBuild CS0006 race; the explicit TFM
skips the Android leg.

### Tests

```bash
dotnet test Src/Tests/WPR.Tests/WPR.Tests.csproj -c Debug
```

`BackendIsolationTests.Backend_references_match_documented_baseline` is the
architecture-migration guard: it fails both when a *new* assembly starts
referencing FNA/Vortice and when a stage removes an existing leak (so the win
gets locked into the baseline). It reads built assemblies, so build the whole
solution in the IDE first. See [Docs/STAGE-GATE.md](Docs/STAGE-GATE.md) for the
per-stage exit checklist.


## Game compatibility

See the interactive [Compatibility List](https://bubbleshum.github.io/WPR/) —
searchable / sortable / filterable, with box art.


## Runtime types supported

The installer recognises four package flavours
([`ApplicationType.cs`](Src/Core/WPR.Loader/Models/ApplicationType.cs)):

| Type | Status | Notes |
| --- | --- | --- |
| `XNA` | Working | Main path; hosted in-process on `WPR.Framework.Xna` + the FNA backend. |
| `Silverlight` | Experimental | Hosted in-process through the in-tree `WPR.Framework.Silverlight` Avalonia re-impl. |
| `UnityPort` | Working (desktop) | Not hosted — WPR spawns an externally-rebuilt standalone port described by `wpr-port.json` in the install folder. Android needs a per-game AAR and isn't wired up. |
| `ModernNative` | Not supported | C++/CX + WinRT apps ship as native PE binaries — out of scope. |

GameMaker Studio exports are detected separately (by an `Assets/game.win`
file) and run via `GameMakerLauncher` rather than a stored type.


## Known limitations & TODO

- **The architecture migration is mid-flight.** Stage 5 (severing FNA/Vortice
  from the frameworks and runtime) is in progress; `WPR.XnaCompability`,
  GamerServices, `Microsoft.Devices.Sensors`, `WPR.Framework.Silverlight`,
  `WPR.UI` and `WPR.UI.Android` still reference a backend directly. Stages 6–8
  (engine extraction, backends-as-pure-adapters, new platforms) haven't started.
- **Per-game shim gaps are the usual failure mode.** Most game-specific crashes
  are a missing shim type or patcher entry, not a bug in the runner. Each launch
  writes `wpr_game_debug.log` into the game's install folder — start there.
- **Android lags desktop.** It builds and runs, but far fewer titles have been
  exercised there, and the Unity/native port rail is desktop-only (Android would
  need a per-game AAR).
- **Silverlight runtime is partial.** Panorama / Pivot / PerformanceProgressBar /
  ToggleSwitch, the gesture pipeline (`GestureService` / `GestureListener`) and
  the WP7 theme (`ButtonStyleLight`, `DarkThemePanoramaStyle`) are implemented;
  `LongListSelector`, `WrapPanel`, `PhoneTextBox` and `PhoneApplicationPageStyle`
  are still TODO.
- **No `WPR.Backend.Direct3D11` yet** — the second backend is designed
  (`WPR.Xna.Rhi` is deliberately D3D11-mappable) but unimplemented, so the RHI
  seam has only one consumer proving it.
- README + Wiki translation (RU / CN).
- Long-term: explore a port to MAUI for unified multi-platform.


## Reinstall vs. rebuild

A common gotcha — patcher changes do **not** affect already-installed games:

- **Shim implementation change** (any `.cs` under `WPR.Framework.*`,
  `WPR.*Compability*`, GamerServices): rebuild only. Installed games pick up
  the new behaviour on next launch.
- **Patcher table change** (`ApplicationPatcher.cs` — new entries in
  `Patches` / `MemberPatches` / `WprFrameworkXnaTypes`): rebuild **and
  reinstall** the affected games, and bump `ApplicationPatcher.Version`. The IL
  was rewritten at install time; new redirects don't apply retroactively.


## Tech notes

- Newest Rider / VS 2022 (17.8+) recommended.
- Targets `net8.0-windows10.0.17763.0` — Windows 11 recommended; Windows 10
  may need the 17763 (1809) baseline or newer.
- Desktop runtime pulls in `FAudio.dll` / `FNA3D.dll` / `SDL2.dll` /
  `FNWP72.dll` / `ffmpeg.exe` (shipped next to the executable).
- Per-game install data lives under `%LocalAppData%\WPR\AppData\<ProductId>`,
  with a `<game>.dll.original` sibling kept for re-patching and a
  `wpr_game_debug.log` capturing that game's `Trace`/`Debug` output.

## Update History

Moved to the wiki — see
[Update History](https://github.com/Bubbleshum/WPR/wiki/Update-History).


## Credits

- [mediaexplorer74/WPR](https://github.com/mediaexplorer74/WPR) — the fork this
  one is based on; foundational Avalonia port work, Android target groundwork,
  and the long-running RnD that made everything downstream possible
- [Tyler Jaacks](https://github.com/TylerJaacks) — net5/6 → net8 upgrade
- [Hector47](https://github.com/Hector47) — online services groundwork

### Related forks worth looking at

- [TylerJaacks/WPR](https://github.com/TylerJaacks/WPR) — branches
  `net8_upgrade` and `dotnet_upgrade` carry useful work
- [Hector47/WPR](https://github.com/Hector47/WPR) — `master` has GameServices ideas
- [yangzhongke/Windows-Phone-Emulator](https://github.com/yangzhongke/Windows-Phone-Emulator) —
  Silverlight 4 prior art for WP control reimplementations (defers to the
  Silverlight XAML parser, so not transplantable here, but the C# for
  Panorama/Pivot/Transitions is a useful reference)


## ::

AS IS. No support. Developers / geeks only — DIY mode.
