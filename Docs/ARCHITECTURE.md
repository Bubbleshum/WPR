# Architecture

How WPR actually runs a Windows Phone game. This is the technical reference that
used to live in the top-level `README.md`; the README is now the non-technical
front page.

- Forward-looking design work (the layered-architecture ADR, stage scopes,
  feasibility studies) lives in [`Plans/`](../Plans/README.md).
- Day-to-day build/patch/reinstall workflow rules live in
  [`CLAUDE.md`](../CLAUDE.md).

---

## The short version

WPR runs a Windows Phone game's **original assemblies**. It does not decompile,
port or rewrite the game. At install time it rewrites the game's IL so that every
call into a Windows Phone, Silverlight or XNA API is redirected to an in-tree
reimplementation of that API, then hosts the result on .NET 8 + Avalonia (desktop
UI) and SDL/FNA (game loop).

## The install and launch pipeline

```
.xap / XNA folder
      │
      ▼ LibraryScanner          (discovers packages)
      ▼ ApplicationInstaller    (unpacks to %LocalAppData%\WPR\AppData\<ProductId>)
      ▼ ApplicationPatcher      (Cecil-rewrites every .dll; leaves .dll.original)
      ▼ XnaAchievementSeeder    (populates the SQLite achievements DB)
      │
      ▼ (user clicks "Play")
      ├─ UnityPortLauncher.TryLaunchAsync        (wpr-port.json → spawn standalone port)
      ├─ GameMakerLauncher                       (Assets/game.win → GameMaker runner)
      ├─ SilverlightLauncher.LaunchAsync         (Silverlight XAPs, in-process on Avalonia)
      └─ XnaLauncher → ApplicationLaunch.Start   (XNA games, WPR.Backend.FNA host)
```

`ApplicationPatcher` is the heart of it. Three tables drive the rewrite:

| Table | What it does |
| --- | --- |
| `Patches` | Redirects Silverlight / WP / XNA **types** to WPR shim types |
| `WprFrameworkXnaTypes` | Rescopes XNA types to the `WPR.Framework.Xna` assembly |
| `MemberPatches` | Redirects a handful of individual **CLR methods** |

`WprFrameworkXnaTypes` is tested **before** `Patches`, so a FullName present in
both silently loses its `Patches` redirect.

`ApplicationPatcher.Version` (currently **19**) is the staleness marker: bump it
whenever a table changes, so the installer knows already-installed games carry
outdated IL.

## Package types the installer recognises

Four flavours, from [`ApplicationType.cs`](../Src/Core/WPR.Database/Models/ApplicationType.cs):

| Type | Status | Notes |
| --- | --- | --- |
| `XNA` | Working | The main path; hosted in-process on `WPR.Framework.Xna` + the FNA backend. |
| `Silverlight` | Experimental | Hosted in-process through the in-tree `WPR.Framework.Silverlight` Avalonia re-implementation. |
| `UnityPort` | Working (desktop) | Not hosted — WPR spawns an externally-rebuilt standalone port described by `wpr-port.json` in the install folder. Android would need a per-game AAR and isn't wired up. |
| `ModernNative` | Not supported | C++/CX + WinRT apps ship as native PE binaries — out of scope. |

GameMaker Studio exports are detected separately (by an `Assets/game.win` file)
and run via `GameMakerLauncher` rather than a stored type.

## Project layout (`Src/`)

Several projects were renamed during the layered-architecture migration (see
[`Plans/ARCHITECTURE-MIGRATION.md`](../Plans/ARCHITECTURE-MIGRATION.md)). Note
that **project names and namespaces deliberately diverge** in places:
`WPR.Framework.Silverlight` declares `namespace WPR.SilverlightCompability` *and*
`namespace WPR.WindowsCompability` (the latter absorbed when that project was
dissolved), because the patcher tables target those names. Keeping the namespaces
is what let those moves happen without touching a single `NewNamespace` string.

| Project | Role |
| --- | --- |
| `Core/WPR.Loader` | Install/patch pipeline (`ApplicationInstaller`, `ApplicationPatcher`, `LibraryScanner`) |
| `Core/WPR.Runtime` | Launch/hosting glue (`SilverlightAppHost`, `GameMakerLauncher`) |
| `Core/WPR.Abstractions` | Backend-independent host contracts (`IGameHost`, `IWindow`, `IAudioDevice`, …) |
| `Core/WPR.Database` | Everything persisted: the `applications.db` catalogue schema (`WPR.Models`), the `achievements.db` schema (`AchievementContext`, `EfAchievementStore`), their EF migrations, and the shipped seed data under `Data/` |
| `Core/WPR.Common` | Paths, configuration, image/env helpers, notifications |
| `Core/WPR.Diagnostics` | Logging (`WprLog`, `FileLog`) |
| `Core/WPR.Framework.Xna` | **WPR-owned XNA type system** — Graphics, Audio, Media, Content, Input, Storage, plus the `WPR.Xna.Rhi` backend seams (`Backend/I*Backend.cs`) and the `IAchievementStore` seam. Also owns `GamerServices/` (gamer profile, achievements, leaderboards) |
| `Core/WPR.Framework.Silverlight` | Silverlight 4 / WP XAML re-implementation on Avalonia. Also owns the former `WPR.WindowsCompability` types (Application, MessageBox, the Imaging bitmaps, IsolatedStorage, ProtectedData) and the BCL-method redirect targets the patcher rewrites call sites to (`Path2`, `GC2`, `Type2`, `XElement2`) |
| `Core/WPR.Framework.Phone` | `Microsoft.Phone.*` facade (Shell, Tasks, Marketplace, Scheduler, …) |
| `Core/WPR.Framework.Devices.Sensors` | Accelerometer / Compass |
| `Core/WPR.Framework.Devices.Location` | `System.Device.Location` |
| `Backends/WPR.Backend.FNA` | FNA backend — implements the `WPR.Xna.Rhi` seams and hosts the game loop (`ApplicationLaunch`, `FnaGameHost`) |
| `Backends/WPR.Backend.Direct3D11` | Vortice/D3D11 backend for Silverlight `DrawingSurface` content, behind `ISurfaceRendererBackend` |
| `Backends/FNA.Platform` | FNA fork (builds assembly `FNA`): native-backed runtime, window, SDL/FNA3D/FAudio/Theorafile bindings |
| `Platforms/WPR.Platform.Windows` | Windows head (`net8.0-windows10.0.17763.0`): `Program`, `App`, `MainWindowDesktop`, the whole Avalonia UI (pages, view-models, views, brand theme `Themes/Brand.axaml`), the launchers (`SilverlightLauncher`, `XnaLauncher`, `UnityPortLauncher`), tilt input, toast notifications |
| `Platforms/WPR.Platform.Android` | Android head (`net8.0-android34.0`): native `Activity` shell (`Native/*`), `GameActivity`, notifications |
| `Tests/WPR.Tests` | Dependency-fitness test guarding the backend-isolation baseline |
| `Core/WPR.SilverlightCompability.Tests` | Unit tests for the Silverlight/XAML re-implementation |
| `JavaBindings/*` | Android bindings: SDL2 (`Org.Libsdl.App`), FFmpegKit |
| `ThirdParty/Icons.Avalonia` | Vendored Projektanker icons, patched for Avalonia 11.3.9 |
| `ThirdParty/assembly-store-reader` | Reads Android assembly stores (port/APK inspection) |

### The Android shell is native

The launcher UI on Android — start, games, achievements, settings, about — is
plain `android.app.Activity` with XML layouts, **not** Avalonia. The Avalonia
pages under `Platforms/WPR.Platform.Windows/Pages/` affect the Windows head
alone; the Android equivalents are `Platforms/WPR.Platform.Android/Native/*Activity.cs`
and must be changed in parallel when behaviour should match.
`Avalonia.Android` is still referenced and must stay — it supplies the AndroidX
AppCompat resources the app theme parents onto.

There is also no library scan on Android: games are added one at a time through
the system document picker (`Native/XapInstallFlow.cs`).

## Reinstall vs. rebuild

A recurring gotcha — patcher changes do **not** affect already-installed games:

- **Shim implementation change** (any `.cs` under `WPR.Framework.*`): rebuild
  only. Installed games pick up the new behaviour on next launch, because they
  reference the shim assembly rather than a snapshot of it.
- **Patcher table change** (`ApplicationPatcher.cs` — new entries in `Patches` /
  `MemberPatches` / `WprFrameworkXnaTypes`, or a changed target type): rebuild
  **and reinstall** the affected games, and bump `ApplicationPatcher.Version`.
  The IL was rewritten at install time; new redirects do not apply retroactively.

The common "add a new shim type" task is **both**: add the shim class, add the
patcher entry, rebuild, reinstall the affected game.

The desktop app ships two launch profiles for this — *Repatch installed games*
and *Reinstall all library games*. See [BUILDING.md](BUILDING.md#run-configurations).

## Runtime data on disk

| Path | What |
| --- | --- |
| `%LocalAppData%\WPR\Database\` | `applications.db` + `achievements.db`, copied from the build output on first run |
| `%LocalAppData%\WPR\AppData\<ProductId>\` | One installed game: its patched DLLs, a `<game>.dll.original` sibling kept for re-patching, and `wpr_game_debug.log` capturing that game's `Trace`/`Debug` output |

`wpr_game_debug.log` is the first place to look when a game crashes silently.

## Native dependencies

The desktop runtime pulls in `FAudio.dll`, `FNA3D.dll`, `SDL2.dll`, `FNWP72.dll`
and `ffmpeg.exe`, all shipped next to the executable and committed to the repo.
There are no submodules and no `git lfs` step — a plain `git clone` is a complete
checkout.
