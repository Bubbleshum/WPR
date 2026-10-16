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

WPR runs a Windows Phone game's **original binaries**. It does not decompile,
port or rewrite the game's logic.

- **Managed titles** (WP7/WP8 XNA and Silverlight): at install time WPR rewrites the
  game's IL so that every call into a Windows Phone, Silverlight or XNA API is
  redirected to an in-tree reimplementation of that API.
- **WP8 native titles** (ARMv7 C++/CX against WinRT): nothing is rewritten. The ARM
  code runs on a JIT (dynarmic) inside `WPR.Engine.Wp8Native`, and its imports (CRT,
  Win32, WinRT, Direct3D 11, XAudio2) are answered by a host runtime.

Either way the game runs in-process on .NET 10 as an XNA `Game` on SDL + FNA
(FNA3D for graphics, FAudio for sound). The desktop launcher UI is Avalonia; the
Android launcher is native Android views.

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
      ├─ GameMakerLauncher.TryLaunchAsync        (Assets/game.win + a Runner.exe → GameMaker runner)
      └─ XnaLauncher → ApplicationLaunch.Start   (everything else, in-process on WPR.Backend.FNA:
                                                  XNA, Silverlight (mixed-mode and pure UI), WP8 native)
```

WP8 native titles have no IL, so `ApplicationPatcher` never sees them: the
installer extracts them, seeds a committed achievement catalogue if one exists, and
records them as `ModernNative`. `RepatchAsync` and Android's launch-time repatch
skip them.

`ApplicationPatcher` is the heart of it. Three tables drive the rewrite:

| Table | What it does |
| --- | --- |
| `Patches` | Redirects Silverlight / WP / XNA **types** to WPR shim types |
| `WprFrameworkXnaTypes` | Rescopes XNA types to the `WPR.Framework.Xna` assembly |
| `MemberPatches` | Redirects a handful of individual **CLR methods** |

`WprFrameworkXnaTypes` is tested **before** `Patches`, so a FullName present in
both silently loses its `Patches` redirect.

`ApplicationPatcher.Version` (currently **40**) is the staleness marker: bump it
whenever a table changes, so the installer knows already-installed games carry
outdated IL.

## Package types the installer recognises

Four flavours, from [`ApplicationType.cs`](../Src/Core/WPR.Database/Models/ApplicationType.cs):

| Type | Status | Notes |
| --- | --- | --- |
| `XNA` | Working | The main path; hosted in-process on `WPR.Framework.Xna` + the FNA backend. |
| `Silverlight` | Mixed-mode: working. Pure UI apps: experimental | Every Silverlight title is hosted by `SilverlightGame` on the XNA path, on both heads: the page is laid out and rasterised on the CPU (`SoftwareVisualRasteriser`). A **mixed-mode** title (its main assembly names `SharedGraphicsDeviceManager`; a page that drives XNA from a `GameTimer`) also draws its own scene; a pure UI app is all page. The Avalonia host that pure UI apps used to run on was deleted on 2026-10-13. |
| `ModernNative` | Partial | WP8 native ARM titles, hosted in-process by `WPR.Engine.Wp8Native` on both heads. Covers `.exe` titles and **Direct3D/XAML** titles: XAPs whose manifest says `Silverlight` but whose game is a native ARM component DLL (`NativeXamlComponent` finds it, and the installer records the title as `ModernNative`). A guest that stops is reported to WPR Hub with the calls it was given stub answers for. |
| `UnityPort` | Working (desktop) | Not hosted — WPR spawns an externally-rebuilt standalone port described by `wpr-port.json` in the install folder. Android would need a per-game AAR and isn't wired up. |

GameMaker Studio exports are detected separately (by an `Assets/game.win` file)
and, on the desktop when a Runner.exe is available, run via `GameMakerLauncher`
rather than a stored type; otherwise they go to `SilverlightGame` like any other
Silverlight title.

Unity WP8 titles (`UnityPlayer.dll`) are not hosted in-process: `NativeXamlComponent`
skips them, and a spike on 2026-10-12 found the managed/native bridge too wide (see
[`Plans/Unity_WP8_Feasibility.md`](../Plans/Unity_WP8_Feasibility.md) §11). They stay
on the `UnityPort` path.

## The three in-process runtimes

Every in-process game is an XNA `Game` driven by `ApplicationLaunch` in
`WPR.Engine.GameLoop`. What differs is where the frame and
the input come from.

| Runtime | Draws through | Input |
| --- | --- | --- |
| XNA | `GraphicsDevice` → `IGraphicsBackend` → FNA3D | SDL → `IInputBackend` → `TouchPanel` |
| Silverlight (mixed-mode and pure UI) | the Silverlight page rasterised on the CPU by `SoftwareVisualRasteriser` into a `Texture2D` on the same device, composited when nothing else painted the frame; a mixed-mode title also draws its own XNA scene | `TouchPanel` → `SilverlightGame.PumpSilverlightTouch` → routed Silverlight events |
| WP8 native | `Wp8GpuReplay` replays the guest's Direct3D 11 frame as draws on the same `GraphicsDevice` (`WPR_WP8_GPU=0` falls back to an older 2D draw list, as a diagnostic) | `TouchPanel` → `WinRt.InjectPointer` (CoreWindow pointer events); Back from `GamePad.Buttons.Back`; keys from `Keyboard.GetState()` edges |

All three share one window, one graphics device, the FNA3D driver choice and the
`TouchPanel` input path, so keyboard→touch bindings, wheel scrolling and the driver
picker apply to all of them. Pure Silverlight joined them in Stages C and D of
[`Plans/SILVERLIGHT-XNA-CONVERGENCE.md`](../Plans/SILVERLIGHT-XNA-CONVERGENCE.md)
(2026-10-13); until then it ran on a desktop-only Avalonia renderer.

### WP8 native in more detail

The runtime's sources live in `Src/Engine/WPR.Engine.Wp8Native/Runtime/` (they began as a
standalone probe, which `Tools/Wp8/Wp8Probe` still builds from the same files) and are compiled
with `WPR_NO_UNICORN` defined, so the CPU is always dynarmic (0BSD) through the `wprcpu`
C shim: `Src/Engine/WPR.Engine.Wp8Native/Native/wprcpu.dll` on Windows, `libwprcpu.so` under the Android
head's `Libraries/<abi>/`. The main pieces:

- **Module loader** (`PeImage`, `ModuleLoader`): maps the ARM PE image and its DLLs
  and binds their imports to host handlers (`HostStubs` and the `*Library` classes).
- **WinRT runtime** (`WinRtRuntime` and the `WinRt*` files): interface slot tables
  are generated from Windows' WinRT metadata (`WinRtSystemInterfaces.g.cs`, written
  by `Tools/WinRtSlotGen`), so an unimplemented member is reported by name.
- **Direct3D 11 and XAudio2**: `Direct3DRuntime` answers the guest's D3D11 calls,
  `FrameCapture` records each frame's resources and draws, and `Wp8GpuReplay`
  replays the frame through FNA. XAudio2 source voices play as
  `DynamicSoundEffectInstance`s (`Wp8NativeAudio`).
- **XAML component shells** (`XamlInterop`, `XamlShells`): for Direct3D/XAML titles,
  the managed page's part (sizes, orientation, the background content provider) is
  played from a per-title description keyed by the component DLL. A title with no
  description fails at launch with that reason.
- **Display model** (`Wp8Display`): one screen model for every size question. The
  default is WXGA (768x1280 physical, scale 160, 480x800 DIPs);
  `WPR_WP8_RESOLUTION=wvga|wxga|720p` picks another.
- **Platform services**: sensors (`Wp8NativeSensorHost`), Xbox Live achievements
  (`Wp8NativeXboxHost`), vibration and URI launching (`Wp8HostServices`), lifecycle
  events (`AppLifecycle`).

An import with no handler is answered with 0 and recorded, and the list goes into
the crash report if the guest stops. `WPR_WP8_TRAP_IMPORTS=1` stops at the first one
instead, as a diagnostic. Trapping is never the default: trap-by-default stopped all 11
installed titles at boot, and an unknown game must run until it actually fails. The rules
that came out of the runtime convergence plan (completed 2026-10-14 and removed; see
[`Plans/README.md`](../Plans/README.md)):

- **`SafeToDefault` holds only imports whose documented answer IS 0**, each with a one-line
  reason. Anything else is implemented to its contract, never "answered 0 because callers
  ignore it".
- **`__abi_WinRTraise*` is never defaulted**: it is the game throwing, and it is raised as a
  guest C++/CX exception (`PlatformRaise`). A raise the old code swallowed can expose a host bug
  the swallow hid (twice so far), so read `raised=[…]` in the run's `summary:` line.
- **No guessed vtables.** An unimplemented `??0…` constructor returns `this` untouched and is
  counted as defaulted; the only guessing left is the WinRT stand-in for an unknown class,
  counted as `guessedslots=[…]`. Implement what shows up there.
- **Diff before and after.** `scratchpad/gamerun/wp8-regression.ps1` runs every installed WP8
  title for a fixed time and records frames, the stop reason and the defaulted lists; a runtime
  change is judged against that table, not against a green build.

Deliberately **not** converged: `Direct3DRuntime` translates guest D3D11 into XNA draws (a
translator, not a second renderer); Unity ports and GameMaker run as separate processes; the
launcher UI (Avalonia on Windows, native views on Android) is not game rendering.

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
| `Core/WPR.Database` | Everything persisted: the `applications.db` catalogue schema (`WPR.Models`), the `achievements.db` schema (`AchievementContext`, `EfAchievementStore`), their EF migrations, and the shipped seed data under `Data/` |
| `Core/WPR.Common` | Paths, configuration, host environment (`WprHostEnvironment`), logging (`Log`), gamer-picture defaults |
| `Core/WPR.Framework.Xna` | **WPR-owned XNA type system** — Graphics, Audio, Media, Content, Input, Storage, plus the `WPR.Xna.Rhi` backend seams (`Backend/I*Backend.cs`) and the `IAchievementStore` seam. Also owns `GamerServices/` (gamer profile, achievements, leaderboards) |
| `Core/WPR.Framework.Silverlight` | Silverlight 4 / WP XAML re-implementation, drawn by its own CPU rasteriser (no UI framework). Also owns the former `WPR.WindowsCompability` types (Application, MessageBox, the Imaging bitmaps, IsolatedStorage, ProtectedData) and the BCL-method redirect targets the patcher rewrites call sites to (`Path2`, `GC2`, `Type2`, `XElement2`) |
| `Core/WPR.Framework.Phone` | `Microsoft.Phone.*` facade (Shell, Tasks, Marketplace, Scheduler, …) |
| `Core/WPR.Framework.Devices.Sensors` | Accelerometer / Compass |
| `Core/WPR.Framework.Devices.Location` | `System.Device.Location` |
| `Engine/WPR.Engine` | Composition root for a platform: `PlatformDescriptor`, `IPlatformCapabilities`, `PlatformComposition.Apply` — heads declare capabilities, this turns them into registry writes |
| `Engine/WPR.Engine.Audio` | The three runtime audio seams (`IAudioBackend`, `IXactBackend`, `IMediaBackend`), the `IAudioModule` plug and `AudioBackendRegistry` that composes them, plus the install-time `IAudioTranscoder` + `AudioTranscoderBackend` and the `IAudioDecoder` / `IVideoDecoder` contracts |
| `Engine/WPR.Engine.Sensors` | `IAccelerometerProvider` + `SensorBackend` (motion input, in neutral `System.Numerics` terms) |
| `Engine/WPR.Engine.Notifications` | `INotificationManager` + `NotificationBackend` and the notification DTOs (replaces `WPR.Common.NativeUI`) |
| `Engine/WPR.Engine.Graphics` | `GraphicsDriver` / `GraphicsDriverPreference` (the FNA3D driver choice a head declares), `GraphicsDriverProbe` (demotes Vulkan after a launch that never presented a frame) and the measured `GraphicsCapabilities` |
| `Engine/WPR.Engine.GameLoop` | The launch sequence: `ApplicationLaunch` (picks XNA, Silverlight or WP8 native and runs it), `IGameHost`, `IGameLaunchHooks`, `SilverlightGame`, `GameLifecycleQuirks` |
| `Engine/WPR.Engine.Input` | Keyboard emulation on the XNA side: the tilt components and `SyntheticTouchInputBackend`, which injects keyboard- and wheel-driven touch into `TouchPanel` |
| `Engine/WPR.Engine.Wp8Native` | WP8 native ARM titles as an XNA `Game` (`Wp8NativeGame`): the runtime in `Runtime/`, plus the GPU replay, audio, sensors, Xbox Live and launcher/vibration hosts (see above) |
| `Engine/WPR.Engine.Online` | WPR Hub contracts and registry (`ILeaderboardService`, `ICrashReporter`, `OnlineBackend`, `PlaytimeTracker`); speaks strings and longs only |
| `Engine/WPR.Engine.Content` | `ContentPaths`: how Windows-style paths in game file I/O are resolved on the running filesystem |
| `Engine/WPR.Engine.Launchers` | `IUriLauncher` / `IShareSheet` + `LauncherBackend`, and `WebLinkPolicy` (only http/https leave the game) |
| `Engine/WPR.Engine.Vibration` | `IVibrationProvider` + `VibrationBackend`, including the global on/off switch |
| `Backends/WPR.Backend.FNA` | FNA adapter: implements the `WPR.Xna.Rhi` seams and `IGameHost` (`FnaGameHost`). The only graphics backend |
| `Backends/FNA.Platform` | FNA fork (builds assembly `FNA`): SDL platform layer and the FNA3D/FAudio/Theorafile/SDL2 bindings. Defines no XNA API |
| `Modules/Audio/WPR.Audio.FAudio` | Fills all three audio seams over FAudio/FACT, plus FAudio's `XNA_Song` and Theorafile video |
| `Modules/Audio/WPR.Audio.AndroidMediaPlayer` | Android-only: the song half of `IMediaBackend` over platform `MediaPlayer`; forwards video to the module below it |
| `Modules/Video/WPR.Video.MediaCodec` | Android-only `IVideoDecoder` over `MediaExtractor` + `MediaCodec`, reading the sidecars the installer writes beside each movie |
| `Modules/Input/*` | `WPR.Input.Keyboard` (desktop tilt, Back and touch emulation) and `WPR.Input.AndroidSensor` (the device's accelerometer and gyroscope) |
| `Modules/Notifications/*`, `Modules/Launchers/*`, `Modules/Vibration/*` | Per-platform implementations of the engine contracts above (Windows toast, Android channel, shell-execute, Android intents, Android vibrator) |
| `Modules/Online/WPR.Online.Hub` | The WPR Hub client: leaderboards, crash reports, sign-in, presence, achievement and playtime sync, friends and messages. Queues to disk and flushes from the launcher |
| `Core/WPR.Shell` | Launcher logic shared by both heads with no UI type in it: strings, achievement roll-ups, the accent palette, hub setup, the rating prompt, update checks |
| `Platforms/WPR.Platform.Windows` | Windows head (`net10.0-windows10.0.17763.0`): `Program`, `App`, `MainWindowDesktop`, the whole Avalonia UI (pages, view-models, views, brand theme `Themes/Brand.axaml`), the launchers (`XnaLauncher`, `UnityPortLauncher`), `WindowsPlatform` |
| `Platforms/WPR.Platform.Android` | Android head (`net10.0-android36.0`): native `Activity` shell (`Native/*`), `GameActivity`, `AndroidPlatform` |
| `Tests/WPR.Tests` | Dependency-fitness test guarding the backend-isolation baseline |
| `Core/WPR.SilverlightCompability.Tests` | Unit tests for the Silverlight/XAML re-implementation |
| `JavaBindings/*` | Android bindings: SDL2 (`Org.Libsdl.App`), FFmpegKit |
| `ThirdParty/Icons.Avalonia` | Vendored Projektanker icons, patched for Avalonia 11.3.9 |

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
| `%LocalAppData%\WPR\AppData\<ProductId>\` | One installed game: its patched DLLs (WP8 native titles are extracted unchanged), a `<game>.dll.original` sibling kept for re-patching, and, in a Debug build or a "play with logging" run, `wpr_game_debug.log` capturing that game's `Trace`/`Debug` output |
| `%LocalAppData%\WPR\IsolatedStore\<ProductId>\AppFiles\` | The game's isolated storage (its saves), one store per game |
| `%LocalAppData%\WPR\Logs\` | `wpr-<process>.log`: every `Log.*` and `Trace` line, Release builds included, capped at 4 MB |

`wpr_game_debug.log`, or the session log in a Release build, is the first place to
look when a game crashes silently.

## Native dependencies

The desktop runtime pulls in `FAudio.dll`, `FNA3D.dll`, `SDL2.dll`, `FNWP72.dll`
and `ffmpeg.exe`, all shipped next to the executable and committed to the repo,
plus `wprcpu.dll` (the dynarmic shim for WP8 native titles), committed under
`Src/Engine/WPR.Engine.Wp8Native/Native/` and copied to the output by `WPR.Engine.Wp8Native`.
There are no submodules and no `git lfs` step — a plain `git clone` is a complete
checkout.
